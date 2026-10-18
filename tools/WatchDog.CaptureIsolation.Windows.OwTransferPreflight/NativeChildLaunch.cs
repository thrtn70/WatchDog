using Microsoft.Win32.SafeHandles;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal sealed record LaunchEvidence(
    uint NativeProcessId,
    uint NativePrimaryThreadId,
    long CreationFileTime,
    uint ResumeResult,
    bool JobEmptyObserved);

internal readonly record struct TerminateReapResult(
    bool ProcessExited,
    bool JobEmpty,
    int? ExitCode)
{
    internal bool Confirmed => ProcessExited && JobEmpty;
}

internal readonly record struct JobCloseWaitResult(
    bool JobClosed,
    bool ProcessExited,
    int? ExitCode);

internal sealed class NativeLaunchFailure(NativeChildLaunch launch, Exception cause)
    : Exception("Native launch failed after birth; retained custody requires exact reap.", cause)
{
    internal NativeChildLaunch Launch { get; } = launch;
}

internal sealed class InjectedBirthQueryFailure() : Exception("Injected query failure after native birth slot capture.");

/// <summary>
/// Owns the exact native process, primary-thread, and job handles for one writer launch.
/// The child enters its kill-on-close job in CreateProcessW and remains suspended until Resume.
/// </summary>
internal sealed partial class NativeChildLaunch : IDisposable
{
    private const nuint ProcThreadAttributeJobList = 0x0002000D;
    private const uint CreateSuspendedFlag = 0x00000004;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int ErrorInsufficientBuffer = 122;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    private const uint ResumeFailed = uint.MaxValue;
    private const uint FailureExitCode = 1;
    private static readonly TimeSpan ObservationInterval = TimeSpan.FromMilliseconds(20);

    private readonly OwnedJobHandle _jobHandle = new();
    private readonly OwnedProcessHandle _processHandle = new();
    private readonly OwnedThreadHandle _primaryThreadHandle = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private uint _nativeProcessId;
    private uint _nativePrimaryThreadId;
    private long _creationFileTime;
    private LaunchEvidence? _evidence;
    private int? _exitCode;
    private bool _jobClosed;
    private bool _disposed;

    private NativeChildLaunch()
    {
    }

    internal int Id
    {
        get
        {
            ThrowIfDisposed();
            return checked((int)_nativeProcessId);
        }
    }

    internal uint PrimaryThreadId
    {
        get
        {
            ThrowIfDisposed();
            return _nativePrimaryThreadId;
        }
    }

    internal long CreationFileTime
    {
        get
        {
            ThrowIfDisposed();
            return _creationFileTime;
        }
    }

    internal DateTimeOffset CreationTimeUtc =>
        new(DateTime.FromFileTimeUtc(CreationFileTime));

    internal SafeHandle SafeHandle
    {
        get
        {
            ThrowIfDisposed();
            return _processHandle;
        }
    }

    internal LaunchEvidence? Evidence => Volatile.Read(ref _evidence);

    internal bool HasExited
    {
        get
        {
            ThrowIfDisposed();
            return ObserveProcessExited();
        }
    }

    internal int ExitCode
    {
        get
        {
            ThrowIfDisposed();
            if (!ObserveProcessExited())
                throw new InvalidOperationException("The writer process is still active.");
            return ObserveExitCode();
        }
    }

    internal static NativeChildLaunch CreateSuspended(
        string applicationPath,
        IReadOnlyList<string> arguments,
        bool injectBirthQueryFailure = false)
    {
        ValidateLaunch(applicationPath, arguments);
        var commandLine = BuildCommandLine(applicationPath, arguments);
        var launch = new NativeChildLaunch();
        try
        {
            launch.CreateSuspendedCore(applicationPath, commandLine, injectBirthQueryFailure);
            return launch;
        }
        catch (Exception exception)
        {
            if (!launch._processHandle.IsInvalid)
                throw new NativeLaunchFailure(launch, exception);
            launch.Dispose();
            throw;
        }
    }

    internal LaunchEvidence Resume()
    {
        ThrowIfDisposed();
        if (_evidence is not null)
            throw new InvalidOperationException("The writer primary thread was already resumed.");

        var resumeResult = ResumeThread(_primaryThreadHandle);
        var evidence = new LaunchEvidence(
            _nativeProcessId,
            _nativePrimaryThreadId,
            _creationFileTime,
            resumeResult,
            JobEmptyObserved: false);
        Volatile.Write(ref _evidence, evidence);
        if (resumeResult == ResumeFailed)
            throw NativeFailure("ResumeThread");
        if (resumeResult != 1)
        {
            throw new InvalidOperationException(
                $"ResumeThread returned unexpected previous suspend count {resumeResult}.");
        }
        return evidence;
    }

    internal async Task WaitForExitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var observation = await ObserveUntilAsync(
                timeout,
                requireJobEmpty: false,
                cancellationToken).ConfigureAwait(false);
            if (!observation.ProcessExited)
                throw new TimeoutException("The writer process did not exit within the admitted timeout.");
        }
        finally
        {
            _operationGate.Release();
        }
    }

    internal async Task<TerminateReapResult> TerminateAndReapAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_jobClosed)
                throw new InvalidOperationException("The writer job handle is already closed.");

            var initial = ObserveSnapshot(requireJobEmpty: true);
            if (initial.Confirmed)
                return initial;
            if (!TerminateJobObject(_jobHandle, FailureExitCode))
                throw NativeFailure("TerminateJobObject");
            return await ObserveUntilAsync(
                timeout,
                requireJobEmpty: true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <summary>
    /// Closes the last retained job handle to exercise kill-on-close, then waits only for the
    /// exact retained process. Job emptiness cannot be queried after the last job handle closes.
    /// </summary>
    internal async Task<JobCloseWaitResult> CloseJobAndWaitAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ValidateTimeout(timeout);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (!_jobClosed)
            {
                if (ObserveProcessExited() || ObserveJobEmpty())
                    throw new InvalidOperationException("W was not live in a nonempty job before job closure.");
                _jobClosed = _jobHandle.CloseOnce();
                if (!_jobClosed)
                    throw new InvalidOperationException("CloseHandle failed for the writer job.");
            }

            var observation = await ObserveUntilAsync(
                timeout,
                requireJobEmpty: false,
                cancellationToken).ConfigureAwait(false);
            return new JobCloseWaitResult(
                _jobClosed,
                observation.ProcessExited,
                observation.ExitCode);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _operationGate.Wait();
        try
        {
            if (_disposed)
                return;
            if (!_jobClosed)
                _jobClosed = _jobHandle.CloseOnce();
            _primaryThreadHandle.CloseOnce();
            _processHandle.CloseOnce();
            _disposed = true;
        }
        finally
        {
            _operationGate.Release();
            _operationGate.Dispose();
        }
    }

    private unsafe void CreateSuspendedCore(string applicationPath, string commandLine, bool injectBirthQueryFailure)
    {
        var rawJob = CreateJobObject(nint.Zero, null);
        _jobHandle.Adopt(rawJob);
        if (_jobHandle.IsInvalid)
            throw NativeFailure("CreateJobObjectW");

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        if (!SetInformationJobObject(
                _jobHandle,
                JobObjectExtendedLimitInformationClass,
                in limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            throw NativeFailure("SetInformationJobObject");
        }

        nuint attributeBytes = 0;
        var sizeQuerySucceeded = InitializeProcThreadAttributeList(
            nint.Zero,
            1,
            0,
            ref attributeBytes);
        var sizeQueryError = Marshal.GetLastPInvokeError();
        if (sizeQuerySucceeded || attributeBytes == 0 || sizeQueryError != ErrorInsufficientBuffer)
            throw NativeFailure("InitializeProcThreadAttributeList(size)", sizeQueryError);

        var attributeList = Marshal.AllocHGlobal(checked((nint)attributeBytes));
        var initialized = false;
        try
        {
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes))
                throw NativeFailure("InitializeProcThreadAttributeList");
            initialized = true;

            var rawJobForAttribute = _jobHandle.DangerousGetHandle();
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeJobList,
                    (nint)(&rawJobForAttribute),
                    (nuint)IntPtr.Size,
                    nint.Zero,
                    nint.Zero))
            {
                throw NativeFailure("UpdateProcThreadAttribute(JOB_LIST)");
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                },
                AttributeList = attributeList,
            };
            var mutableCommandLine = (commandLine + '\0').ToCharArray();
            fixed (char* commandLinePointer = mutableCommandLine)
            {
                if (!CreateProcess(
                        applicationPath,
                        (nint)commandLinePointer,
                        nint.Zero,
                        nint.Zero,
                        inheritHandles: false,
                        CreateSuspendedFlag | ExtendedStartupInfoPresent | CreateNoWindow,
                        nint.Zero,
                        null,
                        ref startup,
                        out var processInformation))
                {
                    throw NativeFailure("CreateProcessW");
                }

                // These preallocated ownership-slot adoptions must remain the first operations
                // after successful CreateProcessW. Do not allocate, log, cancel, or query first.
                _processHandle.Adopt(processInformation.Process);
                _primaryThreadHandle.Adopt(processInformation.Thread);

                _nativeProcessId = processInformation.ProcessId;
                _nativePrimaryThreadId = processInformation.ThreadId;
            }

            if (injectBirthQueryFailure)
                throw new InjectedBirthQueryFailure();

            if (_processHandle.IsInvalid || _primaryThreadHandle.IsInvalid ||
                _nativeProcessId == 0 || _nativePrimaryThreadId == 0)
            {
                throw new InvalidOperationException("CreateProcessW returned invalid birth ownership.");
            }
            if (GetProcessId(_processHandle) != _nativeProcessId ||
                GetThreadId(_primaryThreadHandle) != _nativePrimaryThreadId)
            {
                throw new InvalidOperationException("Native process or primary-thread identity drifted at birth.");
            }
            if (!GetProcessTimes(_processHandle, out var creation, out _, out _, out _))
                throw NativeFailure("GetProcessTimes");
            _creationFileTime = creation.ToLong();
            if (_creationFileTime <= 0)
                throw new InvalidOperationException("The native process creation FILETIME is invalid.");
        }
        finally
        {
            if (initialized)
                DeleteProcThreadAttributeList(attributeList);
            Marshal.FreeHGlobal(attributeList);
        }
    }

    private async Task<TerminateReapResult> ObserveUntilAsync(
        TimeSpan timeout,
        bool requireJobEmpty,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = ObserveSnapshot(requireJobEmpty);
            if (observation.ProcessExited && (!requireJobEmpty || observation.JobEmpty))
                return observation;

            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= timeout)
                return observation;
            var remaining = timeout - elapsed;
            var delay = remaining < ObservationInterval ? remaining : ObservationInterval;
            if (delay <= TimeSpan.Zero)
                return observation;
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    private TerminateReapResult ObserveSnapshot(bool requireJobEmpty)
    {
        var processExited = ObserveProcessExited();
        var jobEmpty = requireJobEmpty && ObserveJobEmpty();
        int? exitCode = processExited ? ObserveExitCode() : null;
        return new TerminateReapResult(processExited, jobEmpty, exitCode);
    }

    private bool ObserveProcessExited()
    {
        var wait = WaitForSingleObject(_processHandle, 0);
        return wait switch
        {
            WaitObject0 => true,
            WaitTimeout => false,
            WaitFailed => throw NativeFailure("WaitForSingleObject"),
            _ => throw new InvalidOperationException($"Unexpected process wait result {wait}."),
        };
    }

    private bool ObserveJobEmpty()
    {
        if (_jobClosed)
            return Evidence?.JobEmptyObserved == true;
        if (!QueryInformationJobObject(
                _jobHandle,
                JobObjectBasicAccountingInformation,
                out JobObjectBasicAccountingInformationValue accounting,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInformationValue>(),
                out _))
        {
            throw NativeFailure("QueryInformationJobObject");
        }

        var empty = accounting.ActiveProcesses == 0;
        if (empty && _evidence is { } evidence && !evidence.JobEmptyObserved)
            Volatile.Write(ref _evidence, evidence with { JobEmptyObserved = true });
        return empty;
    }

    private int ObserveExitCode()
    {
        if (_exitCode is { } retained)
            return retained;
        if (!GetExitCodeProcess(_processHandle, out var nativeExitCode))
            throw NativeFailure("GetExitCodeProcess");
        var exitCode = unchecked((int)nativeExitCode);
        _exitCode = exitCode;
        return exitCode;
    }

    private static void ValidateLaunch(string applicationPath, IReadOnlyList<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(applicationPath) ||
            !Path.IsPathFullyQualified(applicationPath) ||
            applicationPath.Contains('\0'))
        {
            throw new ArgumentException("An absolute application path is required.", nameof(applicationPath));
        }
        ArgumentNullException.ThrowIfNull(arguments);
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument is null || argument.Contains('\0'))
                throw new ArgumentException("Arguments cannot be null or contain NUL.", nameof(arguments));
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        if (timeout < TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    private static string BuildCommandLine(string applicationPath, IReadOnlyList<string> arguments)
    {
        var values = new string[arguments.Count + 1];
        values[0] = applicationPath;
        for (var index = 0; index < arguments.Count; index++)
            values[index + 1] = arguments[index];
        return string.Join(' ', values.Select(QuoteCommandLineArgument));
    }

    private static string QuoteCommandLineArgument(string value)
    {
        if (value.Length != 0 && value.All(character =>
                !char.IsWhiteSpace(character) && character != '"'))
        {
            return value;
        }

        var result = new System.Text.StringBuilder(value.Length + 2);
        result.Append('"');
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', backslashes * 2 + 1);
                result.Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes);
            backslashes = 0;
            result.Append(character);
        }
        result.Append('\\', backslashes * 2);
        result.Append('"');
        return result.ToString();
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private static InvalidOperationException NativeFailure(string operation) =>
        NativeFailure(operation, Marshal.GetLastPInvokeError());

    private static InvalidOperationException NativeFailure(string operation, int error) =>
        new($"{operation} failed with Win32 error {error}.");

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObject(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeHandle job,
        int informationClass,
        in JobObjectExtendedLimitInformation information,
        uint informationLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        SafeHandle job,
        int informationClass,
        out JobObjectBasicAccountingInformationValue information,
        uint informationLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        nint attributeList,
        uint attributeCount,
        uint flags,
        ref nuint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nuint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint attributeList);

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
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
    private static partial uint GetProcessId(SafeHandle process);

    [LibraryImport("kernel32.dll", EntryPoint = "GetThreadId", SetLastError = true)]
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
    private static partial uint ResumeThread(SafeHandle thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(SafeHandle process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

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
    private struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal nint AttributeList;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal nuint MinimumWorkingSetSize;
        internal nuint MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal nuint Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicAccountingInformationValue
    {
        internal long TotalUserTime;
        internal long TotalKernelTime;
        internal long ThisPeriodTotalUserTime;
        internal long ThisPeriodTotalKernelTime;
        internal uint TotalPageFaultCount;
        internal uint TotalProcesses;
        internal uint ActiveProcesses;
        internal uint TotalTerminatedProcesses;
    }

    private abstract class PreallocatedKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private bool _adopted;
        private bool _closeSucceeded;

        protected PreallocatedKernelHandle()
            : base(ownsHandle: true)
        {
        }

        internal void Adopt(nint value)
        {
            if (_adopted)
                throw new InvalidOperationException("The native ownership slot is already occupied.");
            SetHandle(value);
            _adopted = true;
        }

        internal bool CloseOnce()
        {
            if (IsClosed)
                return _closeSucceeded;
            Dispose();
            return _closeSucceeded;
        }

        protected override bool ReleaseHandle()
        {
            _closeSucceeded = CloseHandle(handle);
            return _closeSucceeded;
        }
    }

    private sealed class OwnedJobHandle : PreallocatedKernelHandle;
    private sealed class OwnedProcessHandle : PreallocatedKernelHandle;
    private sealed class OwnedThreadHandle : PreallocatedKernelHandle;
}
