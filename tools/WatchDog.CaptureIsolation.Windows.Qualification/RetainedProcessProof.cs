using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal sealed partial class RetainedProcessProof : IDisposable
{
    private const uint Synchronize = 0x00100000;
    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private readonly SafeFixtureProcessHandle _handle;

    private RetainedProcessProof(SafeFixtureProcessHandle handle, ProcessCoordinate coordinate)
    {
        _handle = handle;
        Coordinate = coordinate;
    }

    internal ProcessCoordinate Coordinate { get; }

    internal static RetainedProcessProof OpenOnceAndVerify(
        ProcessCoordinate expected,
        string expectedExecutablePath)
    {
        if (expected.ProcessId == 0 || expected.CreationFileTime <= 0)
            throw new QualificationAssertionException("fixture.process.coordinate-invalid");

        var rawHandle = OpenProcess(
            Synchronize | ProcessTerminate | ProcessQueryLimitedInformation,
            false,
            expected.ProcessId);
        if (rawHandle is 0 or -1)
            throw Infrastructure("fixture.process.open-failed");
        var handle = new SafeFixtureProcessHandle(rawHandle);
        try
        {
            var actualProcessId = GetProcessId(handle);
            var actualCreation = GetCreationFileTime(handle);
            var actualPath = GetExecutablePath(handle);
            if (actualProcessId != expected.ProcessId ||
                actualCreation != expected.CreationFileTime ||
                !Path.GetFullPath(actualPath).Equals(
                    Path.GetFullPath(expectedExecutablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                // Never terminate a candidate after any PID-reuse or path mismatch.
                throw new QualificationAssertionException("fixture.process.identity-mismatch");
            }
            return new(handle, expected);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static ProcessCoordinate CoordinateFromProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        var processId = checked((uint)process.Id);
        var handle = process.SafeHandle;
        var observedId = GetProcessId(handle);
        var creation = GetCreationFileTime(handle);
        if (observedId != processId)
            throw new QualificationAssertionException("fixture.process.identity-mismatch");
        return new(processId, creation);
    }

    internal static bool? CurrentProcessWasAlreadyInJob()
    {
        try
        {
            if (!IsProcessInJob(GetCurrentProcess(), nint.Zero, out var result))
                return null;
            return result;
        }
        catch
        {
            return null;
        }
    }

    internal bool IsSignaled()
    {
        var result = WaitForSingleObject(_handle, 0);
        return result switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            WaitFailed => throw Infrastructure("fixture.process.wait-failed"),
            _ => throw Infrastructure("fixture.process.wait-unknown"),
        };
    }

    internal async Task WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSignaled())
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
        throw new QualificationTimeoutException("fixture.process.exit-timeout");
    }

    internal void TerminateExact()
    {
        if (IsSignaled())
            return;
        if (!TerminateProcess(_handle, 91))
            throw Infrastructure("fixture.process.terminate-failed");
    }

    public void Dispose() => _handle.Dispose();

    internal bool IsMemberOfJob(SafeWindowsHandle job)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!IsProcessInJob(_handle, job, out var result))
            throw Infrastructure("fixture.process.membership-query-failed");
        return result;
    }

    private static long GetCreationFileTime(SafeHandle process)
    {
        if (!GetProcessTimes(process, out var creation, out _, out _, out _))
            throw Infrastructure("fixture.process.identity-query-failed");
        return creation.ToLong();
    }

    private static unsafe string GetExecutablePath(SafeFixtureProcessHandle process)
    {
        var buffer = new char[32768];
        uint length = (uint)buffer.Length;
        fixed (char* value = buffer)
        {
            if (!QueryFullProcessImageName(process, 0, value, ref length) || length == 0)
                throw Infrastructure("fixture.process.path-query-failed");
        }
        return new string(buffer, 0, checked((int)length));
    }

    private static QualificationCleanupException Infrastructure(string code)
    {
        _ = Marshal.GetLastPInvokeError();
        return new(code);
    }

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
        SafeFixtureProcessHandle process,
        uint flags,
        char* executableName,
        ref uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeFixtureProcessHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeFixtureProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(
        nint process,
        nint job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", EntryPoint = "IsProcessInJob", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(
        SafeFixtureProcessHandle process,
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

    private sealed class SafeFixtureProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal SafeFixtureProcessHandle(nint handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
