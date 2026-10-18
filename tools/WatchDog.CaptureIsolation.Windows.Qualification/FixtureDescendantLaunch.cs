using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

/// <summary>
/// Fixture-only unsuspended descendant launch. The child inherits the helper's nested job through
/// ordinary Windows job inheritance; no job attribute, assignment, breakaway, or new console is
/// requested.
/// </summary>
internal sealed partial class FixtureDescendantLaunch : IDisposable
{
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private readonly CheckedNativeHandle _process;
    private bool _disposed;

    private FixtureDescendantLaunch(
        CheckedNativeHandle process,
        ProcessCoordinate coordinate)
    {
        _process = process;
        Coordinate = coordinate;
    }

    internal ProcessCoordinate Coordinate { get; }

    internal static unsafe FixtureDescendantLaunch Create(
        string executablePath,
        IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        ArgumentNullException.ThrowIfNull(arguments);
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.Contains('\0'))
            throw new QualificationAssertionException("fixture.descendant.path-invalid");

        var mutableCommandLine =
            (FixtureOwnedLaunch.BuildCommandLine(executablePath, arguments) + '\0').ToCharArray();
        var startup = new StartupInfo
        {
            Size = checked((uint)Marshal.SizeOf<StartupInfo>()),
        };
        fixed (char* commandLine = mutableCommandLine)
        {
            if (!CreateProcess(
                    executablePath,
                    (nint)commandLine,
                    nint.Zero,
                    nint.Zero,
                    false,
                    WindowsProcessLaunchApi.CreateNoWindow,
                    nint.Zero,
                    null,
                    ref startup,
                    out var information))
            {
                _ = Marshal.GetLastPInvokeError();
                throw new QualificationCleanupException("fixture.descendant.create-failed");
            }

            // These owning wrappers are deliberately the first operations after CreateProcessW.
            var process = new CheckedNativeHandle(information.Process);
            var thread = new CheckedNativeHandle(information.Thread);
            try
            {
                var processId = GetProcessId(process);
                var threadId = GetThreadId(thread);
                if (processId == 0 || threadId == 0 ||
                    processId != information.ProcessId || threadId != information.ThreadId)
                {
                    throw new QualificationAssertionException(
                        "fixture.descendant.identity-mismatch");
                }
                if (!thread.CloseOnce())
                    throw new QualificationCleanupException("fixture.descendant.thread-close-failed");
                var creationFileTime = GetCreationFileTime(process);
                if (creationFileTime <= 0)
                {
                    throw new QualificationAssertionException(
                        "fixture.descendant.identity-mismatch");
                }
                return new(process, new(processId, creationFileTime));
            }
            catch
            {
                var wait = WaitForSingleObject(process, 0);
                var terminationSucceeded = wait == WaitObject0 ||
                    wait == WaitTimeout && TerminateProcess(process, 92) &&
                    WaitForSingleObject(
                        process,
                        checked((uint)QualificationConstants.CleanupTimeout.TotalMilliseconds)) ==
                    WaitObject0;
                var threadClosed = thread.CloseOnce();
                var processClosed = terminationSucceeded && process.CloseOnce();
                if (!terminationSucceeded || !threadClosed || !processClosed)
                {
                    throw new QualificationCleanupException(
                        "fixture.descendant.failed-launch-cleanup-incomplete");
                }
                throw;
            }
        }
    }

    internal bool IsExited
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var result = WaitForSingleObject(_process, 0);
            return result switch
            {
                WaitObject0 => true,
                WaitTimeout => false,
                WaitFailed => throw Failure("fixture.descendant.wait-failed"),
                _ => throw new QualificationCleanupException("fixture.descendant.wait-unknown"),
            };
        }
    }

    internal async Task WaitForExitAsync(CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(QualificationConstants.CleanupTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsExited)
                return;
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
        throw new QualificationCleanupException("fixture.descendant.exit-timeout");
    }

    internal void TerminateEmergencyExact()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsExited)
            return;
        if (!TerminateProcess(_process, 93))
            throw Failure("fixture.descendant.terminate-failed");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        if (!IsExited)
        {
            throw new InvalidOperationException(
                "A live fixture descendant handle cannot be discarded before terminal proof.");
        }
        if (!_process.CloseOnce())
            throw new QualificationCleanupException("fixture.descendant.process-close-failed");
        _disposed = true;
    }

    private static long GetCreationFileTime(SafeHandle process)
    {
        if (!GetProcessTimes(process, out var creation, out _, out _, out _))
            throw Failure("fixture.descendant.identity-query-failed");
        return creation.ToLong();
    }

    private static QualificationCleanupException Failure(string code)
    {
        _ = Marshal.GetLastPInvokeError();
        return new(code);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(
        string applicationName,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetProcessId(SafeHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetThreadId(SafeHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeHandle process,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

#pragma warning disable CS0649 // Populated by Win32 interop.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        internal uint Size;
        internal nint Reserved;
        internal nint Desktop;
        internal nint Title;
        internal uint X;
        internal uint Y;
        internal uint XSize;
        internal uint YSize;
        internal uint XCountChars;
        internal uint YCountChars;
        internal uint FillAttribute;
        internal uint Flags;
        internal ushort ShowWindow;
        internal ushort ReservedBytes;
        internal nint ReservedBuffer;
        internal nint StandardInput;
        internal nint StandardOutput;
        internal nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        internal nint Process;
        internal nint Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        internal uint Low;
        internal uint High;

        internal readonly long ToLong() => unchecked((long)(((ulong)High << 32) | Low));
    }
#pragma warning restore CS0649

    private sealed class CheckedNativeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private readonly object _sync = new();
        private bool _explicitlyClosed;

        internal CheckedNativeHandle(nint handle)
            : base(ownsHandle: true)
        {
            SetHandle(handle);
        }

        internal bool CloseOnce()
        {
            lock (_sync)
            {
                if (_explicitlyClosed)
                    return true;
                if (IsClosed || IsInvalid || !CloseHandle(handle))
                    return false;
                _explicitlyClosed = true;
                SetHandleAsInvalid();
                return true;
            }
        }

        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
}
