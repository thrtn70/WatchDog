using System.Runtime.InteropServices;
using WatchDog.CaptureIsolation;
using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.Core.Tests.Isolation;

public sealed class WindowsContainmentTests
{
    [Fact]
    public void Win64InteropLayoutsMatchJobObjectContracts()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(64, Marshal.SizeOf<WindowsContainmentApi.JobObjectBasicLimitInformation>());
        Assert.Equal(16, OffsetOf<WindowsContainmentApi.JobObjectBasicLimitInformation>(
            nameof(WindowsContainmentApi.JobObjectBasicLimitInformation.LimitFlags)));
        Assert.Equal(24, OffsetOf<WindowsContainmentApi.JobObjectBasicLimitInformation>(
            nameof(WindowsContainmentApi.JobObjectBasicLimitInformation.MinimumWorkingSetSize)));
        Assert.Equal(40, OffsetOf<WindowsContainmentApi.JobObjectBasicLimitInformation>(
            nameof(WindowsContainmentApi.JobObjectBasicLimitInformation.ActiveProcessLimit)));
        Assert.Equal(48, OffsetOf<WindowsContainmentApi.JobObjectBasicLimitInformation>(
            nameof(WindowsContainmentApi.JobObjectBasicLimitInformation.Affinity)));
        Assert.Equal(144, Marshal.SizeOf<WindowsContainmentApi.JobObjectExtendedLimitInformation>());
        Assert.Equal(64, OffsetOf<WindowsContainmentApi.JobObjectExtendedLimitInformation>(
            nameof(WindowsContainmentApi.JobObjectExtendedLimitInformation.IoInfo)));
        Assert.Equal(112, OffsetOf<WindowsContainmentApi.JobObjectExtendedLimitInformation>(
            nameof(WindowsContainmentApi.JobObjectExtendedLimitInformation.ProcessMemoryLimit)));
        Assert.Equal(48,
            Marshal.SizeOf<WindowsContainmentApi.JobObjectBasicAccountingInformationValue>());
        Assert.Equal(40, OffsetOf<WindowsContainmentApi.JobObjectBasicAccountingInformationValue>(
            nameof(WindowsContainmentApi.JobObjectBasicAccountingInformationValue.ActiveProcesses)));
        Assert.Equal(8, WindowsContainmentApi.ProcessIdListHeaderBytes);
        Assert.Equal(2, WindowsContainmentApi.AdmissionProcessIdCapacity);
    }

    [Theory]
    [InlineData(true, 0, 1u, 1u, false)]
    [InlineData(false, WindowsContainmentApi.ErrorMoreData, 3u, 2u, true)]
    public void ProcessIdListParserDistinguishesAssignedListedAndTruncatedCounts(
        bool querySucceeded,
        int error,
        uint assignedCount,
        uint listedCount,
        bool expectedTruncated)
    {
        var buffer = ProcessIdListBuffer(assignedCount, listedCount, 77, 88);
        try
        {
            var parsed = WindowsContainmentApi.ParseProcessIdList(
                buffer,
                querySucceeded,
                error);

            Assert.Equal(assignedCount, parsed.AssignedProcessCount);
            Assert.Equal(listedCount, parsed.MemberProcessCount);
            Assert.Equal(expectedTruncated, parsed.Truncated);
            Assert.Equal(listedCount, (uint)parsed.ProcessIds.Count);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(false, 5, 1u, 1u)]
    [InlineData(true, 0, 3u, 3u)]
    public void ProcessIdListParserRejectsErrorsAndImpossibleCapacity(
        bool querySucceeded,
        int error,
        uint assignedCount,
        uint listedCount)
    {
        var buffer = ProcessIdListBuffer(assignedCount, listedCount, 77, 88);
        try
        {
            var exception = Assert.Throws<HelperBoundaryException>(() =>
                WindowsContainmentApi.ParseProcessIdList(buffer, querySucceeded, error));
            Assert.Equal("helper.windows.job.membership-query-failed", exception.FailureCode);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public async Task CanceledCreateMakesNoNativeCall()
    {
        var api = new FakeWindowsContainmentApi();
        await using var boundary = new WindowsJobBoundary(api);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            boundary.CreateAsync(Guid.NewGuid(), cancellation.Token).AsTask());

        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task JobIsConfiguredAndQueriedBeforeIdentityIsExposed()
    {
        var api = new FakeWindowsContainmentApi();
        await using var boundary = new WindowsJobBoundary(api);

        var job = await boundary.CreateAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.NotEqual(Guid.Empty, job.OpaqueHandleIdentity);
        Assert.Equal(["job.create", "job.configure", "job.policy"], api.Calls);
        await CompleteJobOnlyAsync(boundary, api, job);
    }

    [Theory]
    [InlineData("job.configure")]
    [InlineData("job.policy")]
    public async Task FailedConfigurationOrPolicyQueryClosesEmptyJob(string operation)
    {
        var api = new FakeWindowsContainmentApi();
        api.Failures.Add(operation);
        await using var boundary = new WindowsJobBoundary(api);

        await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            boundary.CreateAsync(Guid.NewGuid(), CancellationToken.None).AsTask());

        Assert.Equal(1, api.TotalCloseCalls);
    }

    [Fact]
    public async Task FailedEmptyJobCloseIsBoundedAndNotReportedAsReleased()
    {
        var api = new FakeWindowsContainmentApi { FailJobClose = true };
        api.Failures.Add("job.configure");
        await using var boundary = new WindowsJobBoundary(api);

        var exception = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            boundary.CreateAsync(Guid.NewGuid(), CancellationToken.None).AsTask());

        Assert.Equal("helper.windows.job.close-failed", exception.FailureCode);
        Assert.Equal(1, api.CloseCount(api.JobHandleValue));
        var callsBeforeBlockedCreate = api.Calls.Count;
        var blocked = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            boundary.CreateAsync(Guid.NewGuid(), CancellationToken.None).AsTask());
        Assert.Equal("helper.windows.job.close-pending", blocked.FailureCode);
        Assert.Equal(callsBeforeBlockedCreate, api.Calls.Count);
        api.FailJobClose = false;
        await boundary.DisposeAsync();
        Assert.Equal(2, api.CloseCount(api.JobHandleValue));
    }

    [Theory]
    [InlineData(HelperJobContainmentPolicy.None)]
    [InlineData(HelperJobContainmentPolicy.KillOnJobClose |
        HelperJobContainmentPolicy.BreakawayAllowed)]
    [InlineData(HelperJobContainmentPolicy.KillOnJobClose |
        HelperJobContainmentPolicy.SilentBreakawayAllowed)]
    [InlineData(HelperJobContainmentPolicy.KillOnJobClose |
        HelperJobContainmentPolicy.Unknown)]
    public async Task UnsafeOrUnknownConfiguredPolicyClosesEmptyJob(
        HelperJobContainmentPolicy policy)
    {
        var api = new FakeWindowsContainmentApi { Policy = policy };
        await using var boundary = new WindowsJobBoundary(api);

        var exception = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            boundary.CreateAsync(Guid.NewGuid(), CancellationToken.None).AsTask());

        Assert.Equal("helper.windows.job.policy-mismatch", exception.FailureCode);
        Assert.Equal(1, api.TotalCloseCalls);
    }

    [Fact]
    public async Task CancellationAfterNativeOwnershipCannotDiscardJob()
    {
        using var cancellation = new CancellationTokenSource();
        var api = new FakeWindowsContainmentApi
        {
            AfterJobCreated = cancellation.Cancel,
        };
        await using var boundary = new WindowsJobBoundary(api);

        var job = await boundary.CreateAsync(Guid.NewGuid(), cancellation.Token);

        Assert.NotEqual(Guid.Empty, job.OpaqueHandleIdentity);
        Assert.Equal(0, api.TotalCloseCalls);
        await CompleteJobOnlyAsync(boundary, api, job);
    }

    [Fact]
    public async Task AtomicLaunchReservationPrecedesProcessCreationAndCanCancelUncreatedOwnership()
    {
        var api = new FakeWindowsContainmentApi();
        await using var boundary = new WindowsJobBoundary(api);
        var helperId = Guid.NewGuid();
        var job = await boundary.CreateAsync(helperId, CancellationToken.None);
        var callsBeforeReservation = api.Calls.Count;

        var reservation = boundary.ReserveAtomicLaunch(job, helperId, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, reservation.Process.Identity.OpaqueHandleIdentity);
        Assert.False(reservation.Process.Lease.IsCreated);
        Assert.Equal(callsBeforeReservation, api.Calls.Count);
        boundary.CancelAtomicLaunch(reservation);
        await CompleteJobOnlyAsync(boundary, api, job);
    }

    [Fact]
    public async Task CanceledAtomicReservationMakesNoNativeCall()
    {
        var api = new FakeWindowsContainmentApi();
        await using var boundary = new WindowsJobBoundary(api);
        var helperId = Guid.NewGuid();
        var job = await boundary.CreateAsync(helperId, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var callCount = api.Calls.Count;

        Assert.ThrowsAny<OperationCanceledException>(() =>
            boundary.ReserveAtomicLaunch(job, helperId, cancellation.Token));

        Assert.Equal(callCount, api.Calls.Count);
        await CompleteJobOnlyAsync(boundary, api, job);
    }

    [Fact]
    public async Task CreatedAtomicReservationCannotBeCanceledAndRemainsExactlyReapable()
    {
        var api = new FakeWindowsContainmentApi();
        var owned = await CreateAssignedAsync(api);
        var reservation = Assert.IsType<WindowsJobBoundary.AtomicLaunchReservation>(
            owned.Reservation);

        var exception = Assert.Throws<HelperBoundaryException>(() =>
            owned.Boundary.CancelAtomicLaunch(reservation));

        Assert.Equal("helper.windows.launch.reservation-invalid", exception.FailureCode);
        await CompleteAsync(owned, api);
    }

    [Fact]
    public async Task ForeignAndTransferredIdentitiesMakeNoNativeCall()
    {
        var api = new FakeWindowsContainmentApi();
        var owned = await CreateAssignedAsync(api);
        var beforeForeign = api.Calls.Count;

        await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            owned.Boundary.TerminateAsync(
                owned.Job with { OpaqueHandleIdentity = Guid.NewGuid() },
                CancellationToken.None).AsTask());
        Assert.Equal(beforeForeign, api.Calls.Count);

        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        var callsBeforeMismatch = api.Calls.Count;
        Assert.Throws<HelperBoundaryException>(() => owned.Boundary.CreateReaper(
            owned.Job,
            owned.Process with { OpaqueHandleIdentity = Guid.NewGuid() }));
        Assert.Equal(callsBeforeMismatch, api.Calls.Count);
        var beforeTransferred = api.Calls.Count;
        await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            owned.Boundary.TerminateAsync(owned.Job, CancellationToken.None).AsTask());
        Assert.Equal(beforeTransferred, api.Calls.Count);

        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
        await owned.Boundary.DisposeAsync();
        var beforeDisposed = api.Calls.Count;
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            owned.Boundary.TerminateAsync(owned.Job, CancellationToken.None).AsTask());
        Assert.Equal(beforeDisposed, api.Calls.Count);
    }

    [Theory]
    [InlineData(false, false, 1u, 1u, 77u, HelperJobMembershipStatus.NotMember)]
    [InlineData(true, true, 2u, 1u, 77u, HelperJobMembershipStatus.Truncated)]
    [InlineData(true, false, 2u, 2u, 77u, HelperJobMembershipStatus.MultipleMembers)]
    [InlineData(true, false, 1u, 1u, 88u, HelperJobMembershipStatus.ForeignMember)]
    [InlineData(true, false, 1u, 1u, 77u, HelperJobMembershipStatus.Confirmed)]
    public async Task MembershipObservationReportsExactNeutralFacts(
        bool isMember,
        bool truncated,
        uint assignedCount,
        uint memberCount,
        uint listedProcessId,
        HelperJobMembershipStatus expectedStatus)
    {
        var api = new FakeWindowsContainmentApi
        {
            IsMember = isMember,
            ProcessIds = new(
                assignedCount,
                memberCount,
                truncated,
                ListedIds(memberCount, listedProcessId)),
        };
        var owned = await CreateAssignedAsync(api);

        var observation = await owned.Boundary.ObserveAssignmentAsync(
            owned.Job,
            owned.Process,
            ProtocolFramingTests.Correlation(),
            CancellationToken.None);

        Assert.Equal(expectedStatus, observation.Status);
        Assert.Equal(assignedCount, observation.AssignedProcessCount);
        Assert.Equal(memberCount, observation.MemberProcessCount);
        Assert.Equal(api.Policy, observation.Policy);
        await CompleteAsync(owned, api);
    }

    [Theory]
    [InlineData("job.policy", "helper.windows.job.policy-query-failed")]
    [InlineData("job.is-member", "helper.windows.job.membership-query-failed")]
    [InlineData("job.process-ids", "helper.windows.job.membership-query-failed")]
    public async Task MembershipQueryFailureIsBoundedAndCannotFabricateEvidence(
        string operation,
        string expectedCode)
    {
        var api = new FakeWindowsContainmentApi();
        var owned = await CreateAssignedAsync(api);
        api.Failures.Add(operation);

        var exception = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            owned.Boundary.ObserveAssignmentAsync(
                owned.Job,
                owned.Process,
                ProtocolFramingTests.Correlation(),
                CancellationToken.None).AsTask());

        Assert.Equal(expectedCode, exception.FailureCode);
        api.Failures.Remove(operation);
        await CompleteAsync(owned, api);
    }

    [Fact]
    public async Task TerminationTargetsExactRetainedJobAndFailureRemainsOwned()
    {
        var api = new FakeWindowsContainmentApi();
        var owned = await CreateAssignedAsync(api);
        api.Failures.Add("job.terminate");

        var exception = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            owned.Boundary.TerminateAsync(owned.Job, CancellationToken.None).AsTask());

        Assert.Equal("helper.windows.job.terminate-failed", exception.FailureCode);
        Assert.Equal(api.JobHandleValue, api.LastTerminatedJobHandle);
        api.Failures.Remove("job.terminate");
        await CompleteAsync(owned, api);
    }

    [Fact]
    public async Task BoundaryCannotDisposeWhileItOwnsOrHasTransferredHandles()
    {
        var api = new FakeWindowsContainmentApi();
        var owned = await CreateAssignedAsync(api);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owned.Boundary.DisposeAsync().AsTask());

        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owned.Boundary.DisposeAsync().AsTask());
        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
        await owned.Boundary.DisposeAsync();
    }

    internal static async Task<OwnedContainment> CreateAssignedAsync(
        FakeWindowsContainmentApi api,
        IWindowsReapDelay? reapDelay = null)
    {
        var boundary = new WindowsJobBoundary(api, reapDelay);
        var correlation = ProtocolFramingTests.Correlation();
        var job = await boundary.CreateAsync(
            correlation.HelperInstanceId,
            CancellationToken.None);
        var reservation = boundary.ReserveAtomicLaunch(
            job,
            correlation.HelperInstanceId,
            CancellationToken.None);
        api.CaptureCreatedProcess(reservation.Process.Lease);
        Assert.True(reservation.Process.Lease.TryVerifyMetadata(77, 177, 123456789));
        return new(
            boundary,
            job,
            reservation.Process.Identity,
            reservation.Process.Lease,
            reservation);
    }

    private static IReadOnlyList<uint> ListedIds(uint count, uint first) => count switch
    {
        0 => [],
        1 => [first],
        _ => [first, 99],
    };

    internal static async Task CompleteAsync(
        OwnedContainment owned,
        FakeWindowsContainmentApi api)
    {
        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
        await owned.Boundary.DisposeAsync();
    }

    private static async Task CompleteJobOnlyAsync(
        WindowsJobBoundary boundary,
        FakeWindowsContainmentApi api,
        RetainedJobIdentity job)
    {
        var reaper = boundary.CreateReaper(job, null);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
    }

    internal sealed record OwnedContainment(
        WindowsJobBoundary Boundary,
        RetainedJobIdentity Job,
        RetainedProcessIdentity Process,
        WindowsProcessLease ProcessLease,
        WindowsJobBoundary.AtomicLaunchReservation Reservation);

    private static int OffsetOf<T>(string fieldName) where T : struct =>
        checked((int)Marshal.OffsetOf<T>(fieldName));

    private static nint ProcessIdListBuffer(
        uint assignedCount,
        uint listedCount,
        params uint[] processIds)
    {
        var size = WindowsContainmentApi.ProcessIdListHeaderBytes +
            WindowsContainmentApi.AdmissionProcessIdCapacity * IntPtr.Size;
        var buffer = Marshal.AllocHGlobal(size);
        for (var offset = 0; offset < size; offset += sizeof(int))
            Marshal.WriteInt32(buffer, offset, 0);
        Marshal.WriteInt32(buffer, 0, unchecked((int)assignedCount));
        Marshal.WriteInt32(buffer, sizeof(uint), unchecked((int)listedCount));
        for (var index = 0;
             index < processIds.Length && index < WindowsContainmentApi.AdmissionProcessIdCapacity;
             index++)
        {
            Marshal.WriteIntPtr(
                buffer,
                WindowsContainmentApi.ProcessIdListHeaderBytes + index * IntPtr.Size,
                checked((nint)processIds[index]));
        }
        return buffer;
    }
}

internal sealed class FakeWindowsReapDelay : IWindowsReapDelay
{
    internal int Calls { get; private set; }
    internal TimeSpan LastDelay { get; private set; }
    internal Action? OnDelay { get; init; }

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        Calls++;
        LastDelay = delay;
        OnDelay?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}

internal sealed class FakeWindowsContainmentApi : IWindowsContainmentApi
{
    private nint _nextHandle = 100;
    private readonly Dictionary<nint, int> _closeCounts = [];

    internal List<string> Calls { get; } = [];
    internal HashSet<string> Failures { get; } = [];
    internal Queue<WindowsWaitResult> WaitResults { get; } = [];
    internal Queue<uint> ActiveProcessCounts { get; } = [];
    internal HelperJobContainmentPolicy Policy { get; set; } =
        HelperJobContainmentPolicy.KillOnJobClose;
    internal uint ObservedProcessId { get; set; } = 77;
    internal long ObservedCreationFileTime { get; set; } = 123456789;
    internal bool IsMember { get; set; } = true;
    internal bool FailJobClose { get; set; }
    internal bool FailProcessClose { get; set; }
    internal bool FailThreadClose { get; set; }
    internal Action<nint>? BeforeClose { get; init; }
    internal WindowsProcessIdList ProcessIds { get; set; } =
        new(1, 1, false, [77]);
    internal Action? AfterJobCreated { get; init; }
    internal nint JobHandleValue { get; private set; }
    internal nint ProcessHandleValue { get; private set; }
    internal nint ThreadHandleValue { get; private set; }
    internal nint LastTerminatedJobHandle { get; private set; }
    internal int WaitCalls { get; private set; }
    internal int AccountingCalls { get; private set; }
    internal int TotalCloseCalls => _closeCounts.Values.Sum();

    internal void CaptureCreatedProcess(
        IWindowsProcessOwnershipSink ownershipSink,
        uint processId = 77,
        uint threadId = 177)
    {
        ProcessHandleValue = ++_nextHandle;
        ThreadHandleValue = ++_nextHandle;
        ownershipSink.CaptureCreatedProcess(
            new SafeWindowsHandle(ProcessHandleValue, this),
            new SafeWindowsHandle(ThreadHandleValue, this),
            processId,
            threadId);
    }

    public SafeWindowsHandle CreateUnnamedJob()
    {
        Calls.Add("job.create");
        MaybeFail("job.create", "helper.windows.job.create-failed");
        JobHandleValue = ++_nextHandle;
        var result = new SafeWindowsHandle(JobHandleValue, this);
        AfterJobCreated?.Invoke();
        return result;
    }

    public void ConfigureKillOnJobClose(SafeWindowsHandle job)
    {
        Calls.Add("job.configure");
        MaybeFail("job.configure", "helper.windows.job.configure-failed");
    }

    public HelperJobContainmentPolicy QueryContainmentPolicy(SafeWindowsHandle job)
    {
        Calls.Add("job.policy");
        MaybeFail("job.policy", "helper.windows.job.policy-query-failed");
        return Policy;
    }

    public uint GetProcessId(SafeWindowsHandle process)
    {
        Calls.Add("process.id");
        MaybeFail("process.id", "helper.windows.process.identity-query-failed");
        return ObservedProcessId;
    }

    public long GetProcessCreationFileTime(SafeWindowsHandle process)
    {
        Calls.Add("process.creation");
        MaybeFail("process.creation", "helper.windows.process.identity-query-failed");
        return ObservedCreationFileTime;
    }

    public bool IsProcessInJob(SafeWindowsHandle process, SafeWindowsHandle job)
    {
        Calls.Add("job.is-member");
        MaybeFail("job.is-member", "helper.windows.job.membership-query-failed");
        return IsMember;
    }

    public WindowsProcessIdList QueryProcessIds(SafeWindowsHandle job)
    {
        Calls.Add("job.process-ids");
        MaybeFail("job.process-ids", "helper.windows.job.membership-query-failed");
        return ProcessIds;
    }

    public void TerminateJob(SafeWindowsHandle job)
    {
        Calls.Add("job.terminate");
        LastTerminatedJobHandle = job.DangerousGetHandle();
        MaybeFail("job.terminate", "helper.windows.job.terminate-failed");
    }

    public WindowsWaitResult WaitForProcess(SafeWindowsHandle process)
    {
        Calls.Add("process.wait");
        WaitCalls++;
        MaybeFail("process.wait", "helper.windows.reaper.wait-failed");
        return WaitResults.Count == 0 ? WindowsWaitResult.Timeout : WaitResults.Dequeue();
    }

    public uint QueryActiveProcessCount(SafeWindowsHandle job)
    {
        Calls.Add("job.accounting");
        AccountingCalls++;
        MaybeFail("job.accounting", "helper.windows.reaper.accounting-query-failed");
        return ActiveProcessCounts.Count == 0 ? 1 : ActiveProcessCounts.Dequeue();
    }

    public bool Close(nint handle)
    {
        BeforeClose?.Invoke(handle);
        Calls.Add("handle.close");
        _closeCounts[handle] = _closeCounts.GetValueOrDefault(handle) + 1;
        return !(handle == JobHandleValue && FailJobClose) &&
            !(handle == ProcessHandleValue && FailProcessClose) &&
            !(handle == ThreadHandleValue && FailThreadClose);
    }

    internal int CloseCount(nint handle) => _closeCounts.GetValueOrDefault(handle);

    private void MaybeFail(string operation, string failureCode)
    {
        if (Failures.Contains(operation))
            throw new HelperBoundaryException(failureCode);
    }
}
