using System.Runtime.InteropServices;

namespace WatchDog.CaptureIsolation.Windows;

internal interface IWindowsProcessLaunchApi
{
    void CreateSuspendedInJob(
        WindowsLaunchSpecification specification,
        SafeWindowsHandle jobHandle,
        IWindowsProcessOwnershipSink ownershipSink,
        CancellationToken cancellationToken);

    uint GetProcessId(SafeWindowsHandle processHandle);
    uint GetThreadId(SafeWindowsHandle threadHandle);
    long GetProcessCreationFileTime(SafeWindowsHandle processHandle);
    uint ResumePrimaryThread(SafeWindowsHandle threadHandle);
    void TerminateProcess(SafeWindowsHandle processHandle);
}

internal sealed partial class WindowsProcessLaunchApi : IWindowsProcessLaunchApi
{
    private static readonly object HandleInheritanceGate = new();
    internal const nuint ProcThreadAttributeJobList = 0x0002000D;
    internal const nuint ProcThreadAttributeHandleList = 0x00020002;
    internal const uint HandleFlagInherit = 0x00000001;
    internal const uint CreateSuspended = 0x00000004;
    internal const uint CreateUnicodeEnvironment = 0x00000400;
    internal const uint ExtendedStartupInfoPresent = 0x00080000;
    internal const uint CreateNoWindow = 0x08000000;
    internal const uint ResumeFailed = uint.MaxValue;
    internal const int ErrorInsufficientBuffer = 122;

    internal static WindowsProcessLaunchApi Instance { get; } =
        new(WindowsContainmentApi.Instance);

    private readonly IWindowsHandleCloser _closer;

    internal WindowsProcessLaunchApi(IWindowsHandleCloser closer)
    {
        _closer = closer ?? throw new ArgumentNullException(nameof(closer));
    }

    public unsafe void CreateSuspendedInJob(
        WindowsLaunchSpecification specification,
        SafeWindowsHandle jobHandle,
        IWindowsProcessOwnershipSink ownershipSink,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specification);
        ArgumentNullException.ThrowIfNull(jobHandle);
        ArgumentNullException.ThrowIfNull(ownershipSink);
        cancellationToken.ThrowIfCancellationRequested();

        var attributePlan = new WindowsLaunchAttributePlan(
            jobHandle,
            specification.InheritedChildHandles);
        using var inheritedHandleLease = attributePlan.AcquireInheritedHandles();
        lock (HandleInheritanceGate)
        {
            try
            {
                foreach (var handle in inheritedHandleLease.RawHandles)
                {
                    if (!SetHandleInformation(handle, HandleFlagInherit, HandleFlagInherit))
                        throw Failure("helper.windows.launch.handle-inheritance-enable-failed");
                }
                foreach (var handle in inheritedHandleLease.RawHandles)
                {
                    if (!GetHandleInformation(handle, out var flags) ||
                        (flags & HandleFlagInherit) != HandleFlagInherit)
                    {
                        throw Failure("helper.windows.launch.handle-not-inheritable");
                    }
                }

                nuint attributeBytes = 0;
                var sizeQuerySucceeded = InitializeProcThreadAttributeList(
                    nint.Zero,
                    attributePlan.AttributeCount,
                    0,
                    ref attributeBytes);
                var sizeQueryError = Marshal.GetLastPInvokeError();
                if (sizeQuerySucceeded || attributeBytes == 0 ||
                    sizeQueryError != ErrorInsufficientBuffer)
                {
                    throw Failure("helper.windows.launch.attribute-size-failed");
                }

                cancellationToken.ThrowIfCancellationRequested();
                var attributeList = Marshal.AllocHGlobal(checked((nint)attributeBytes));
                var initialized = false;
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!InitializeProcThreadAttributeList(
                            attributeList,
                            attributePlan.AttributeCount,
                            0,
                            ref attributeBytes))
                        throw Failure("helper.windows.launch.attribute-init-failed");
                    initialized = true;

                    cancellationToken.ThrowIfCancellationRequested();
                    var retainedJobHandle = jobHandle.DangerousGetHandle();
                    if (!UpdateProcThreadAttribute(
                            attributeList,
                            0,
                            ProcThreadAttributeJobList,
                            (nint)(&retainedJobHandle),
                            (nuint)IntPtr.Size,
                            nint.Zero,
                            nint.Zero))
                    {
                        throw Failure("helper.windows.launch.attribute-update-failed");
                    }

                    fixed (nint* inheritedHandles = inheritedHandleLease.RawHandles)
                    {
                        if (attributePlan.InheritHandles &&
                            !UpdateProcThreadAttribute(
                                attributeList,
                                0,
                                ProcThreadAttributeHandleList,
                                (nint)inheritedHandles,
                                checked((nuint)(inheritedHandleLease.RawHandles.Length * IntPtr.Size)),
                                nint.Zero,
                                nint.Zero))
                        {
                            throw Failure("helper.windows.launch.handle-list-update-failed");
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                        var mutableCommandLine = (specification.CommandLine + '\0').ToCharArray();
                        var environment = specification.EnvironmentBlock?.ToCharArray();
                        var startup = new StartupInfoEx
                        {
                            StartupInfo = new StartupInfo
                            {
                                Size = (uint)Marshal.SizeOf<StartupInfoEx>(),
                            },
                            AttributeList = attributeList,
                        };
                        var creationFlags = BuildCreationFlags(environment is not null);

                        fixed (char* commandLine = mutableCommandLine)
                        fixed (char* environmentBlock = environment)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!CreateProcess(
                                    specification.ApplicationPath,
                                    (nint)commandLine,
                                    nint.Zero,
                                    nint.Zero,
                                    attributePlan.InheritHandles,
                                    creationFlags,
                                    (nint)environmentBlock,
                                    null,
                                    ref startup,
                                    out var processInformation))
                            {
                                throw Failure("helper.windows.launch.create-failed");
                            }

                            // This is deliberately the first operation after CreateProcessW succeeds.
                            ownershipSink.CaptureCreatedProcess(
                                new SafeWindowsHandle(processInformation.Process, _closer),
                                new SafeWindowsHandle(processInformation.Thread, _closer),
                                processInformation.ProcessId,
                                processInformation.ThreadId);
                        }
                    }
                }
                finally
                {
                    if (initialized)
                        DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
            }
            finally
            {
                var inheritanceClearFailed = false;
                foreach (var handle in inheritedHandleLease.RawHandles)
                {
                    if (!SetHandleInformation(handle, HandleFlagInherit, 0))
                        inheritanceClearFailed = true;
                }
                if (inheritanceClearFailed)
                    throw Failure("helper.windows.launch.handle-inheritance-clear-failed");
            }
        }
    }

    public uint GetProcessId(SafeWindowsHandle processHandle)
    {
        var processId = GetProcessIdNative(processHandle);
        if (processId == 0)
            throw Failure("helper.windows.process.identity-query-failed");
        return processId;
    }

    public uint GetThreadId(SafeWindowsHandle threadHandle)
    {
        var threadId = GetThreadIdNative(threadHandle);
        if (threadId == 0)
            throw Failure("helper.windows.process.identity-query-failed");
        return threadId;
    }

    public long GetProcessCreationFileTime(SafeWindowsHandle processHandle)
    {
        if (!GetProcessTimes(processHandle, out var creation, out _, out _, out _))
            throw Failure("helper.windows.process.identity-query-failed");
        return creation.ToLong();
    }

    public uint ResumePrimaryThread(SafeWindowsHandle threadHandle) => ResumeThread(threadHandle);

    public void TerminateProcess(SafeWindowsHandle processHandle)
    {
        if (!TerminateProcessNative(processHandle, 1))
            throw Failure("helper.windows.process.terminate-failed");
    }

    internal static uint BuildCreationFlags(bool explicitEnvironment) =>
        CreateSuspended |
        ExtendedStartupInfoPresent |
        CreateNoWindow |
        (explicitEnvironment ? CreateUnicodeEnvironment : 0);

    private static HelperBoundaryException Failure(string code)
    {
        _ = Marshal.GetLastPInvokeError();
        return new(code);
    }

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(
        nint handle,
        out uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(
        nint handle,
        uint mask,
        uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
    private static partial uint GetProcessIdNative(SafeWindowsHandle processHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetThreadId", SetLastError = true)]
    private static partial uint GetThreadIdNative(SafeWindowsHandle threadHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeWindowsHandle processHandle,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(SafeWindowsHandle threadHandle);

    [LibraryImport("kernel32.dll", EntryPoint = "TerminateProcess", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcessNative(
        SafeWindowsHandle processHandle,
        uint exitCode);

#pragma warning disable CS0649 // Fields are populated by Win32 interop.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        internal uint Low;
        internal uint High;

        internal readonly long ToLong() => unchecked((long)(((ulong)High << 32) | Low));
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StartupInfo
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
    internal struct StartupInfoEx
    {
        internal StartupInfo StartupInfo;
        internal nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        internal nint Process;
        internal nint Thread;
        internal uint ProcessId;
        internal uint ThreadId;
    }
#pragma warning restore CS0649
}
