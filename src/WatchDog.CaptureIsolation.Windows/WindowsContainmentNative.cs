using System.Runtime.InteropServices;
using WatchDog.CaptureIsolation;

namespace WatchDog.CaptureIsolation.Windows;

internal enum WindowsWaitResult
{
    Signaled = 1,
    Timeout = 2,
}

internal sealed class WindowsProcessIdList
{
    internal WindowsProcessIdList(
        uint assignedProcessCount,
        uint memberProcessCount,
        bool truncated,
        IEnumerable<uint> processIds)
    {
        AssignedProcessCount = assignedProcessCount;
        MemberProcessCount = memberProcessCount;
        Truncated = truncated;
        ProcessIds = Array.AsReadOnly(processIds.ToArray());
    }

    internal uint AssignedProcessCount { get; }
    internal uint MemberProcessCount { get; }
    internal bool Truncated { get; }
    internal IReadOnlyList<uint> ProcessIds { get; }
}

internal interface IWindowsContainmentApi : IWindowsHandleCloser
{
    SafeWindowsHandle CreateUnnamedJob();
    void ConfigureKillOnJobClose(SafeWindowsHandle job);
    HelperJobContainmentPolicy QueryContainmentPolicy(SafeWindowsHandle job);
    uint GetProcessId(SafeWindowsHandle process);
    long GetProcessCreationFileTime(SafeWindowsHandle process);
    bool IsProcessInJob(SafeWindowsHandle process, SafeWindowsHandle job);
    WindowsProcessIdList QueryProcessIds(SafeWindowsHandle job);
    void TerminateJob(SafeWindowsHandle job);
    WindowsWaitResult WaitForProcess(SafeWindowsHandle process);
    uint QueryActiveProcessCount(SafeWindowsHandle job);
}

internal sealed partial class WindowsContainmentApi : IWindowsContainmentApi
{
    private const int JobObjectBasicAccountingInformation = 1;
    private const int JobObjectBasicProcessIdList = 3;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const uint JobObjectLimitBreakawayOk = 0x00000800;
    private const uint JobObjectLimitSilentBreakawayOk = 0x00001000;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint KnownObservedLimitFlags =
        JobObjectLimitBreakawayOk |
        JobObjectLimitSilentBreakawayOk |
        JobObjectLimitKillOnJobClose;
    internal const int ErrorMoreData = 234;
    private const uint WaitObject0 = 0;
    private const uint WaitTimeout = 258;
    private const uint WaitFailed = uint.MaxValue;
    internal const int AdmissionProcessIdCapacity = 2;
    internal const int ProcessIdListHeaderBytes = sizeof(uint) * 2;

    internal static WindowsContainmentApi Instance { get; } = new();

    private WindowsContainmentApi()
    {
    }

    public SafeWindowsHandle CreateUnnamedJob()
    {
        var rawHandle = CreateJobObject(nint.Zero, null);
        if (rawHandle is 0 or -1)
            throw Failure("helper.windows.job.create-failed");
        return new(rawHandle, this);
    }

    public void ConfigureKillOnJobClose(SafeWindowsHandle job)
    {
        var information = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        if (!SetInformationJobObject(
                job,
                JobObjectExtendedLimitInformationClass,
                in information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            throw Failure("helper.windows.job.configure-failed");
        }
    }

    public HelperJobContainmentPolicy QueryContainmentPolicy(SafeWindowsHandle job)
    {
        if (!QueryInformationJobObject(
                job,
                JobObjectExtendedLimitInformationClass,
                out JobObjectExtendedLimitInformation information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>(),
                out _))
        {
            throw Failure("helper.windows.job.policy-query-failed");
        }

        var flags = information.BasicLimitInformation.LimitFlags;
        var policy = HelperJobContainmentPolicy.None;
        if ((flags & JobObjectLimitKillOnJobClose) != 0)
            policy |= HelperJobContainmentPolicy.KillOnJobClose;
        if ((flags & JobObjectLimitBreakawayOk) != 0)
            policy |= HelperJobContainmentPolicy.BreakawayAllowed;
        if ((flags & JobObjectLimitSilentBreakawayOk) != 0)
            policy |= HelperJobContainmentPolicy.SilentBreakawayAllowed;
        if ((flags & ~KnownObservedLimitFlags) != 0)
            policy |= HelperJobContainmentPolicy.Unknown;
        return policy;
    }

    public uint GetProcessId(SafeWindowsHandle process)
    {
        var processId = GetProcessIdNative(process);
        if (processId == 0)
            throw Failure("helper.windows.process.identity-query-failed");
        return processId;
    }

    public long GetProcessCreationFileTime(SafeWindowsHandle process)
    {
        if (!GetProcessTimes(process, out var creation, out _, out _, out _))
            throw Failure("helper.windows.process.identity-query-failed");
        return creation.ToLong();
    }

    public bool IsProcessInJob(SafeWindowsHandle process, SafeWindowsHandle job)
    {
        if (!IsProcessInJobNative(process, job, out var result))
            throw Failure("helper.windows.job.membership-query-failed");
        return result;
    }

    public unsafe WindowsProcessIdList QueryProcessIds(SafeWindowsHandle job)
    {
        var bufferSize = checked((uint)(ProcessIdListHeaderBytes +
            IntPtr.Size * AdmissionProcessIdCapacity));
        var buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            new Span<byte>((void*)buffer, (int)bufferSize).Clear();
            var succeeded = QueryInformationJobObjectBuffer(
                job,
                JobObjectBasicProcessIdList,
                buffer,
                bufferSize,
                out _);
            var error = succeeded ? 0 : Marshal.GetLastPInvokeError();
            return ParseProcessIdList(buffer, succeeded, error);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static WindowsProcessIdList ParseProcessIdList(
        nint buffer,
        bool querySucceeded,
        int error)
    {
        if (buffer == nint.Zero || (!querySucceeded && error != ErrorMoreData))
            throw new HelperBoundaryException("helper.windows.job.membership-query-failed");

        // JobObjectBasicProcessIdList calls this NumberOfAssignedProcesses. It is not the
        // accounting ActiveProcesses value used by the reaper.
        var assignedCount = unchecked((uint)Marshal.ReadInt32(buffer, 0));
        var listedCount = unchecked((uint)Marshal.ReadInt32(buffer, sizeof(uint)));
        if (listedCount > AdmissionProcessIdCapacity)
            throw new HelperBoundaryException("helper.windows.job.membership-query-failed");
        var processIds = new uint[checked((int)listedCount)];
        for (var index = 0; index < processIds.Length; index++)
        {
            var processId = Marshal.ReadIntPtr(
                buffer,
                ProcessIdListHeaderBytes + index * IntPtr.Size).ToInt64();
            if (processId <= 0 || processId > uint.MaxValue)
                throw new HelperBoundaryException("helper.windows.job.membership-query-failed");
            processIds[index] = (uint)processId;
        }
        return new(
            assignedCount,
            listedCount,
            !querySucceeded || listedCount < assignedCount,
            processIds);
    }

    public void TerminateJob(SafeWindowsHandle job)
    {
        if (!TerminateJobObject(job, 1))
            throw Failure("helper.windows.job.terminate-failed");
    }

    public WindowsWaitResult WaitForProcess(SafeWindowsHandle process)
    {
        var result = WaitForSingleObject(process, 0);
        return result switch
        {
            WaitObject0 => WindowsWaitResult.Signaled,
            WaitTimeout => WindowsWaitResult.Timeout,
            WaitFailed => throw Failure("helper.windows.reaper.wait-failed"),
            _ => throw Failure("helper.windows.reaper.wait-unknown"),
        };
    }

    public uint QueryActiveProcessCount(SafeWindowsHandle job)
    {
        if (!QueryInformationJobObject(
                job,
                JobObjectBasicAccountingInformation,
                out JobObjectBasicAccountingInformationValue information,
                (uint)Marshal.SizeOf<JobObjectBasicAccountingInformationValue>(),
                out _))
        {
            throw Failure("helper.windows.reaper.accounting-query-failed");
        }
        return information.ActiveProcesses;
    }

    public bool Close(nint handle) => CloseHandle(handle);

    private static HelperBoundaryException Failure(string code)
    {
        _ = Marshal.GetLastPInvokeError();
        return new(code);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObject(nint jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeWindowsHandle job,
        int informationClass,
        in JobObjectExtendedLimitInformation information,
        uint informationLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        SafeWindowsHandle job,
        int informationClass,
        out JobObjectExtendedLimitInformation information,
        uint informationLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObject(
        SafeWindowsHandle job,
        int informationClass,
        out JobObjectBasicAccountingInformationValue information,
        uint informationLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryInformationJobObject", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryInformationJobObjectBuffer(
        SafeWindowsHandle job,
        int informationClass,
        nint information,
        uint informationLength,
        out uint returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "IsProcessInJob", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJobNative(
        SafeWindowsHandle process,
        SafeWindowsHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcessId", SetLastError = true)]
    private static partial uint GetProcessIdNative(SafeWindowsHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(
        SafeWindowsHandle process,
        out FileTime creation,
        out FileTime exit,
        out FileTime kernel,
        out FileTime user);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(SafeWindowsHandle job, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(SafeWindowsHandle handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

#pragma warning disable CS0649 // Fields are populated by Win32 interop.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        internal uint Low;
        internal uint High;

        internal readonly long ToLong() => unchecked((long)(((ulong)High << 32) | Low));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicLimitInformation
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
    internal struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal nuint ProcessMemoryLimit;
        internal nuint JobMemoryLimit;
        internal nuint PeakProcessMemoryUsed;
        internal nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JobObjectBasicAccountingInformationValue
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
#pragma warning restore CS0649
}
