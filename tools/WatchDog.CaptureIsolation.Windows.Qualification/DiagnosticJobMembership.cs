using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

/// <summary>
/// Fixture-only diagnostic census. It never participates in admission and never uses a PID for
/// termination, waiting, cleanup, or ownership. Production membership capacity remains two.
/// </summary>
internal static partial class DiagnosticJobMembership
{
    private const int JobObjectBasicProcessIdList = 3;
    private const int ErrorMoreData = 234;
    private const int HeaderBytes = sizeof(uint) * 2;
    private const int InitialCapacity = 8;
    private const int MaximumCapacity = 4096;
    private const int MaximumAttempts = 16;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    internal static MembershipEnumerationRecord Capture(
        Guid nonce,
        string scenario,
        SafeWindowsHandle exactJob,
        SafeWindowsHandle retainedHelper,
        ProcessCoordinate expectedHelper,
        RetainedProcessProof retainedDescendant,
        string fixtureExecutablePath)
    {
        ArgumentNullException.ThrowIfNull(exactJob);
        ArgumentNullException.ThrowIfNull(retainedHelper);
        ArgumentNullException.ThrowIfNull(retainedDescendant);
        var first = CaptureCompleteCensus(exactJob);
        if (!first.Complete)
        {
            return new(
                nonce,
                "owner",
                scenario,
                first,
                null,
                false,
                Array.Empty<DiagnosticProcessIdentityRecord>(),
                first.FailureCode ?? "fixture.job.diagnostic-census-incomplete");
        }

        var identities = new List<DiagnosticProcessIdentityRecord>(first.ProcessIds.Count + 2)
        {
            CaptureRetainedHelper(
                retainedHelper,
                exactJob,
                expectedHelper,
                fixtureExecutablePath),
            CaptureRetainedDescendant(
                retainedDescendant,
                exactJob,
                fixtureExecutablePath),
        };
        var expectedIds = new HashSet<uint>
        {
            expectedHelper.ProcessId,
            retainedDescendant.Coordinate.ProcessId,
        };
        foreach (var processId in first.ProcessIds.Where(id => !expectedIds.Contains(id)))
            identities.Add(CaptureQueryOnlyExtra(processId, exactJob, fixtureExecutablePath));

        identities.Sort(static (left, right) => left.ProcessId.CompareTo(right.ProcessId));
        var second = CaptureCompleteCensus(exactJob);
        var stable = second.Complete &&
            first.AssignedProcessCount == second.AssignedProcessCount &&
            first.ListedProcessCount == second.ListedProcessCount &&
            first.ActiveProcessCount == second.ActiveProcessCount &&
            first.ProcessIds.SequenceEqual(second.ProcessIds);
        var identityComplete = identities.All(static identity =>
            identity.Status is "verified" or "observed");
        var failureCode = !second.Complete
            ? second.FailureCode ?? "fixture.job.diagnostic-second-census-incomplete"
            : !stable
                ? "fixture.job.diagnostic-census-unstable"
                : !identityComplete
                    ? "fixture.job.diagnostic-identity-incomplete"
                    : null;
        return new(
            nonce,
            "owner",
            scenario,
            first,
            second,
            stable,
            identities.AsReadOnly(),
            failureCode);
    }

    private static unsafe DiagnosticJobCensusRecord CaptureCompleteCensus(
        SafeWindowsHandle exactJob)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(QualificationConstants.ReadinessTimeout.TotalSeconds * Stopwatch.Frequency);
        var capacity = InitialCapacity;
        for (var attempt = 0;
             attempt < MaximumAttempts && Stopwatch.GetTimestamp() < deadline;
             attempt++)
        {
            var bufferBytes = checked(HeaderBytes + capacity * IntPtr.Size);
            var buffer = Marshal.AllocHGlobal(bufferBytes);
            try
            {
                new Span<byte>((void*)buffer, bufferBytes).Clear();
                var succeeded = QueryInformationJobObject(
                    exactJob,
                    JobObjectBasicProcessIdList,
                    buffer,
                    checked((uint)bufferBytes),
                    out _);
                var error = succeeded ? 0 : Marshal.GetLastPInvokeError();
                var assigned = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                var listed = unchecked((uint)Marshal.ReadInt32(buffer, sizeof(uint)));
                if (listed > (uint)capacity || listed > assigned)
                {
                    return FailedCensus(
                        assigned,
                        listed,
                        "fixture.job.diagnostic-census-malformed");
                }

                var processIds = new uint[checked((int)listed)];
                var unique = new HashSet<uint>();
                for (var index = 0; index < processIds.Length; index++)
                {
                    var value = Marshal.ReadIntPtr(
                        buffer,
                        HeaderBytes + index * IntPtr.Size).ToInt64();
                    if (value <= 0 || value > uint.MaxValue || !unique.Add((uint)value))
                    {
                        return FailedCensus(
                            assigned,
                            listed,
                            "fixture.job.diagnostic-census-malformed");
                    }
                    processIds[index] = (uint)value;
                }
                Array.Sort(processIds);

                if (succeeded && listed == assigned)
                {
                    try
                    {
                        return new(
                            assigned,
                            listed,
                            WindowsContainmentApi.Instance.QueryActiveProcessCount(exactJob),
                            true,
                            Array.AsReadOnly(processIds),
                            null);
                    }
                    catch
                    {
                        return FailedCensus(
                            assigned,
                            listed,
                            "fixture.job.diagnostic-active-query-failed",
                            processIds);
                    }
                }
                if (succeeded || error != ErrorMoreData)
                {
                    return FailedCensus(
                        assigned,
                        listed,
                        "fixture.job.diagnostic-census-query-failed",
                        processIds);
                }

                var observedRequired = Math.Max(assigned, listed);
                if (observedRequired > MaximumCapacity)
                {
                    return FailedCensus(
                        assigned,
                        listed,
                        "fixture.job.diagnostic-census-capacity-exceeded",
                        processIds);
                }
                var required = Math.Max(capacity * 2, checked((int)observedRequired));
                if (required > MaximumCapacity)
                {
                    return FailedCensus(
                        assigned,
                        listed,
                        "fixture.job.diagnostic-census-capacity-exceeded",
                        processIds);
                }
                capacity = required;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        return FailedCensus(0, 0, "fixture.job.diagnostic-census-deadline");
    }

    private static DiagnosticJobCensusRecord FailedCensus(
        uint assigned,
        uint listed,
        string failureCode,
        IReadOnlyList<uint>? processIds = null) =>
        new(
            assigned,
            listed,
            0,
            false,
            processIds ?? Array.Empty<uint>(),
            failureCode);

    private static DiagnosticProcessIdentityRecord CaptureRetainedHelper(
        SafeWindowsHandle helper,
        SafeWindowsHandle exactJob,
        ProcessCoordinate expected,
        string fixtureExecutablePath)
    {
        try
        {
            return CaptureIdentity(
                helper,
                exactJob,
                expected.ProcessId,
                expected.CreationFileTime,
                fixtureExecutablePath,
                "retained-helper",
                requireExpectedIdentity: true);
        }
        catch
        {
            return FailedIdentity(expected.ProcessId, "retained-helper", "query-failed");
        }
    }

    private static DiagnosticProcessIdentityRecord CaptureRetainedDescendant(
        RetainedProcessProof descendant,
        SafeWindowsHandle exactJob,
        string fixtureExecutablePath)
    {
        try
        {
            var normalized = NormalizePath(fixtureExecutablePath);
            var isMember = descendant.IsMemberOfJob(exactJob);
            return new(
                descendant.Coordinate.ProcessId,
                descendant.Coordinate.CreationFileTime,
                fixtureExecutablePath,
                HashNormalizedPath(normalized),
                true,
                isMember,
                "retained-descendant",
                isMember ? "verified" : "identity-mismatch");
        }
        catch
        {
            return FailedIdentity(
                descendant.Coordinate.ProcessId,
                "retained-descendant",
                "query-failed");
        }
    }

    private static DiagnosticProcessIdentityRecord CaptureQueryOnlyExtra(
        uint processId,
        SafeWindowsHandle exactJob,
        string fixtureExecutablePath)
    {
        var rawHandle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (rawHandle is 0 or -1)
        {
            _ = Marshal.GetLastPInvokeError();
            return FailedIdentity(processId, "query-only-extra", "open-failed");
        }

        using var handle = new SafeQueryProcessHandle(rawHandle);
        try
        {
            return CaptureIdentity(
                handle,
                exactJob,
                processId,
                expectedCreationFileTime: null,
                fixtureExecutablePath,
                "query-only-extra",
                requireExpectedIdentity: false);
        }
        catch
        {
            return FailedIdentity(processId, "query-only-extra", "query-failed");
        }
    }

    private static DiagnosticProcessIdentityRecord CaptureIdentity(
        SafeHandle process,
        SafeWindowsHandle exactJob,
        uint expectedProcessId,
        long? expectedCreationFileTime,
        string fixtureExecutablePath,
        string source,
        bool requireExpectedIdentity)
    {
        var processId = GetProcessId(process);
        var creation = GetCreationFileTime(process);
        var imagePath = GetImagePath(process);
        var normalizedImage = NormalizePath(imagePath);
        var normalizedFixture = NormalizePath(fixtureExecutablePath);
        var matchesFixture = normalizedImage.Equals(
            normalizedFixture,
            StringComparison.Ordinal);
        var isMember = IsMemberOfExactJob(process, exactJob);
        var identityMatches = processId == expectedProcessId &&
            (!expectedCreationFileTime.HasValue ||
             creation == expectedCreationFileTime.Value);
        var status = identityMatches && isMember &&
            (!requireExpectedIdentity || matchesFixture)
                ? requireExpectedIdentity ? "verified" : "observed"
                : "identity-mismatch";
        return new(
            processId == 0 ? expectedProcessId : processId,
            creation,
            imagePath,
            HashNormalizedPath(normalizedImage),
            matchesFixture,
            isMember,
            source,
            status);
    }

    private static DiagnosticProcessIdentityRecord FailedIdentity(
        uint processId,
        string source,
        string status) =>
        new(processId, null, null, null, null, null, source, status);

    private static long GetCreationFileTime(SafeHandle process)
    {
        if (!GetProcessTimes(process, out var creation, out _, out _, out _))
            throw new QualificationAssertionException("fixture.job.diagnostic-identity-query-failed");
        return creation.ToLong();
    }

    private static unsafe string GetImagePath(SafeHandle process)
    {
        var buffer = new char[32768];
        uint length = checked((uint)buffer.Length);
        fixed (char* value = buffer)
        {
            if (!QueryFullProcessImageName(process, 0, value, ref length) || length == 0)
            {
                throw new QualificationAssertionException(
                    "fixture.job.diagnostic-image-query-failed");
            }
        }
        return new string(buffer, 0, checked((int)length));
    }

    private static bool IsMemberOfExactJob(SafeHandle process, SafeWindowsHandle exactJob)
    {
        if (!IsProcessInJob(process, exactJob, out var isMember))
            throw new QualificationAssertionException("fixture.job.diagnostic-membership-query-failed");
        return isMember;
    }

    private static string NormalizePath(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();

    private static string HashNormalizedPath(string normalizedPath) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)))
            .ToLowerInvariant();

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        SafeWindowsHandle job,
        int informationClass,
        nint information,
        uint informationLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetProcessId(SafeHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeHandle process,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageName(
        SafeHandle process,
        uint flags,
        char* executableName,
        ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(
        SafeHandle process,
        SafeWindowsHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

#pragma warning disable CS0649 // Populated by Win32 interop.
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;

        internal readonly long ToLong() => unchecked((long)(((ulong)High << 32) | Low));
    }
#pragma warning restore CS0649

    private sealed class SafeQueryProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeQueryProcessHandle(nint handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
