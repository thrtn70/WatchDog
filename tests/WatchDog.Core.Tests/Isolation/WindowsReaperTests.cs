using WatchDog.CaptureIsolation;
using WatchDog.CaptureIsolation.Windows;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Isolation;

public sealed class WindowsReaperTests
{
    [Flags]
    public enum CloseFailure
    {
        None = 0,
        Process = 1,
        Thread = 2,
        Job = 4,
        Both = Process | Job,
    }

    [Theory]
    [InlineData((int)WindowsWaitResult.Signaled, 1u, true, false)]
    [InlineData((int)WindowsWaitResult.Timeout, 0u, false, true)]
    [InlineData((int)WindowsWaitResult.Timeout, 1u, false, false)]
    public async Task ReaperRequiresBothExactProcessExitAndEmptyJob(
        int waitResultValue,
        uint activeProcesses,
        bool expectedProcessExited,
        bool expectedJobEmpty)
    {
        var api = new FakeWindowsContainmentApi();
        var owned = await WindowsContainmentTests.CreateAssignedAsync(api);
        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        api.WaitResults.Enqueue((WindowsWaitResult)waitResultValue);
        api.ActiveProcessCounts.Enqueue(activeProcesses);

        var observation = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.Equal(owned.Process, observation.Process);
        Assert.Equal(owned.Job, observation.Job);
        Assert.Equal(expectedProcessExited, observation.ProcessExited);
        Assert.Equal(expectedJobEmpty, observation.JobEmpty);
        await Assert.ThrowsAsync<HelperBoundaryException>(() => reaper.DisposeAsync().AsTask());
        Assert.Equal(0, api.TotalCloseCalls);

        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
        await owned.Boundary.DisposeAsync();
    }

    [Fact]
    public async Task PositiveBudgetDelaysOnceThenTakesFinalNonblockingSnapshot()
    {
        var api = new FakeWindowsContainmentApi();
        var delay = new FakeWindowsReapDelay
        {
            OnDelay = () =>
            {
                api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
                api.ActiveProcessCounts.Enqueue(0);
            },
        };
        var owned = await WindowsContainmentTests.CreateAssignedAsync(api, delay);
        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        api.WaitResults.Enqueue(WindowsWaitResult.Timeout);
        api.ActiveProcessCounts.Enqueue(1);
        var budget = TimeSpan.FromMilliseconds(7);

        var terminal = await reaper.ObserveAsync(budget, CancellationToken.None);

        Assert.True(terminal.ProcessExited);
        Assert.True(terminal.JobEmpty);
        Assert.Equal(1, delay.Calls);
        Assert.Equal(budget, delay.LastDelay);
        Assert.Equal(2, api.WaitCalls);
        Assert.Equal(2, api.AccountingCalls);
        await reaper.DisposeAsync();
        await owned.Boundary.DisposeAsync();
    }

    [Fact]
    public async Task CancellationDuringBudgetDelayRetainsReaperForRetry()
    {
        using var cancellation = new CancellationTokenSource();
        var api = new FakeWindowsContainmentApi();
        var delay = new FakeWindowsReapDelay { OnDelay = cancellation.Cancel };
        var owned = await WindowsContainmentTests.CreateAssignedAsync(api, delay);
        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        api.WaitResults.Enqueue(WindowsWaitResult.Timeout);
        api.ActiveProcessCounts.Enqueue(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reaper.ObserveAsync(TimeSpan.FromMilliseconds(7), cancellation.Token).AsTask());

        await Assert.ThrowsAsync<HelperBoundaryException>(() => reaper.DisposeAsync().AsTask());
        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
        await owned.Boundary.DisposeAsync();
    }

    [Fact]
    public async Task CancellationWhileWaitingForReaperGateMakesNoNativeCall()
    {
        var api = new FakeWindowsContainmentApi();
        var delay = new BlockingWindowsReapDelay();
        var owned = await WindowsContainmentTests.CreateAssignedAsync(api, delay);
        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        api.WaitResults.Enqueue(WindowsWaitResult.Timeout);
        api.ActiveProcessCounts.Enqueue(1);
        var first = reaper.ObserveAsync(TimeSpan.FromMilliseconds(7), CancellationToken.None)
            .AsTask();
        await delay.Entered;
        using var cancellation = new CancellationTokenSource();
        var callsBeforeQueuedObservation = api.Calls.Count;
        var queued = reaper.ObserveAsync(TimeSpan.Zero, cancellation.Token).AsTask();

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(callsBeforeQueuedObservation, api.Calls.Count);
        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        delay.Release();
        var terminal = await first;
        Assert.True(terminal.ProcessExited);
        Assert.True(terminal.JobEmpty);
        await reaper.DisposeAsync();
        await owned.Boundary.DisposeAsync();
    }

    [Theory]
    [InlineData("process.wait", "helper.windows.reaper.wait-failed")]
    [InlineData("job.accounting", "helper.windows.reaper.accounting-query-failed")]
    public async Task WaitOrAccountingFailureIsBoundedAndRetainsOwnership(
        string failedOperation,
        string expectedCode)
    {
        var api = new FakeWindowsContainmentApi();
        var owned = await WindowsContainmentTests.CreateAssignedAsync(api);
        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        api.Failures.Add(failedOperation);

        var exception = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None).AsTask());

        Assert.Equal(expectedCode, exception.FailureCode);
        Assert.DoesNotContain(api.Calls, call => call.Contains("path", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<HelperBoundaryException>(() => reaper.DisposeAsync().AsTask());
        api.Failures.Remove(failedOperation);
        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
        await owned.Boundary.DisposeAsync();
    }

    [Fact]
    public async Task UnknownWaitResultFailsClosedWithoutTerminalEvidence()
    {
        var api = new FakeWindowsContainmentApi();
        var owned = await WindowsContainmentTests.CreateAssignedAsync(api);
        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        api.WaitResults.Enqueue((WindowsWaitResult)999);

        var exception = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None).AsTask());

        Assert.Equal("helper.windows.reaper.wait-unknown", exception.FailureCode);
        await Assert.ThrowsAsync<HelperBoundaryException>(() => reaper.DisposeAsync().AsTask());
        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
        await owned.Boundary.DisposeAsync();
    }

    [Fact]
    public async Task RepeatedTerminalObservationDoesNotRepeatNativeProof()
    {
        var api = new FakeWindowsContainmentApi();
        var owned = await WindowsContainmentTests.CreateAssignedAsync(api);
        var first = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        var same = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        Assert.Same(first, same);
        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);

        var terminal = await first.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        var repeated = await first.ObserveAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(terminal, repeated);
        Assert.Equal(1, api.WaitCalls);
        Assert.Equal(1, api.AccountingCalls);
        await first.DisposeAsync();
        await first.DisposeAsync();
        Assert.Equal(1, api.CloseCount(api.ProcessHandleValue));
        Assert.Equal(1, api.CloseCount(api.ThreadHandleValue));
        Assert.Equal(1, api.CloseCount(api.JobHandleValue));
        await owned.Boundary.DisposeAsync();
    }

    [Fact]
    public async Task CanceledObservationMakesNoNativeCallAndDoesNotDiscardOwnership()
    {
        var api = new FakeWindowsContainmentApi();
        var owned = await WindowsContainmentTests.CreateAssignedAsync(api);
        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var callCount = api.Calls.Count;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reaper.ObserveAsync(TimeSpan.Zero, cancellation.Token).AsTask());

        Assert.Equal(callCount, api.Calls.Count);
        Assert.Equal(0, api.TotalCloseCalls);
        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        await reaper.DisposeAsync();
        await owned.Boundary.DisposeAsync();
    }

    [Fact]
    public async Task ConcurrentTerminalDisposeClosesEveryHandleExactlyOnce()
    {
        var api = new FakeWindowsContainmentApi();
        var owned = await WindowsContainmentTests.CreateAssignedAsync(api);
        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);

        Parallel.For(
            0,
            8,
            _ => reaper.DisposeAsync().AsTask().GetAwaiter().GetResult());

        Assert.Equal(1, api.CloseCount(api.ProcessHandleValue));
        Assert.Equal(1, api.CloseCount(api.ThreadHandleValue));
        Assert.Equal(1, api.CloseCount(api.JobHandleValue));
        await owned.Boundary.DisposeAsync();
    }

    [Fact]
    public async Task CreateReaperRejectsOwnerWhileTerminalDisposeIsClosingHandles()
    {
        using var closeEntered = new ManualResetEventSlim();
        using var releaseClose = new ManualResetEventSlim();
        var api = new FakeWindowsContainmentApi
        {
            BeforeClose = _ =>
            {
                closeEntered.Set();
                releaseClose.Wait();
            },
        };
        var owned = await WindowsContainmentTests.CreateAssignedAsync(api);
        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        Exception? disposeFailure = null;
        var disposeThread = new Thread(() =>
        {
            try
            {
                reaper.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                disposeFailure = exception;
            }
        })
        {
            IsBackground = true,
        };

        disposeThread.Start();
        try
        {
            Assert.True(closeEntered.Wait(TestTimeouts.Signal));
            var unavailable = Assert.Throws<HelperBoundaryException>(() =>
                owned.Boundary.CreateReaper(owned.Job, owned.Process));
            Assert.Equal("helper.windows.reaper.unavailable", unavailable.FailureCode);
        }
        finally
        {
            releaseClose.Set();
            Assert.True(disposeThread.Join(TestTimeouts.Signal));
        }
        Assert.Null(disposeFailure);
        await owned.Boundary.DisposeAsync();
    }

    [Theory]
    [InlineData(CloseFailure.Process, "helper.windows.reaper.process-close-failed")]
    [InlineData(CloseFailure.Thread, "helper.windows.reaper.thread-close-failed")]
    [InlineData(CloseFailure.Job, "helper.windows.reaper.job-close-failed")]
    [InlineData(CloseFailure.Both, "helper.windows.reaper.process-and-job-close-failed")]
    [InlineData(CloseFailure.Process | CloseFailure.Thread,
        "helper.windows.reaper.process-and-thread-close-failed")]
    [InlineData(CloseFailure.Thread | CloseFailure.Job,
        "helper.windows.reaper.thread-and-job-close-failed")]
    [InlineData(CloseFailure.Process | CloseFailure.Thread | CloseFailure.Job,
        "helper.windows.reaper.process-thread-and-job-close-failed")]
    public async Task CloseFailureRetainsOwnershipAndRetriesOnlyFailedHandles(
        CloseFailure failure,
        string expectedCode)
    {
        var api = new FakeWindowsContainmentApi
        {
            FailProcessClose = failure.HasFlag(CloseFailure.Process),
            FailThreadClose = failure.HasFlag(CloseFailure.Thread),
            FailJobClose = failure.HasFlag(CloseFailure.Job),
        };
        var owned = await WindowsContainmentTests.CreateAssignedAsync(api);
        var reaper = owned.Boundary.CreateReaper(owned.Job, owned.Process);
        api.WaitResults.Enqueue(WindowsWaitResult.Signaled);
        api.ActiveProcessCounts.Enqueue(0);
        _ = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);

        var first = await Assert.ThrowsAsync<HelperBoundaryException>(() =>
            reaper.DisposeAsync().AsTask());
        Assert.Equal(expectedCode, first.FailureCode);
        Assert.Equal(1, api.CloseCount(api.ProcessHandleValue));
        Assert.Equal(1, api.CloseCount(api.ThreadHandleValue));
        Assert.Equal(1, api.CloseCount(api.JobHandleValue));
        Assert.Same(reaper, owned.Boundary.CreateReaper(owned.Job, owned.Process));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            owned.Boundary.DisposeAsync().AsTask());

        api.FailProcessClose = false;
        api.FailThreadClose = false;
        api.FailJobClose = false;
        await reaper.DisposeAsync();

        Assert.Equal(failure.HasFlag(CloseFailure.Process) ? 2 : 1,
            api.CloseCount(api.ProcessHandleValue));
        Assert.Equal(failure.HasFlag(CloseFailure.Thread) ? 2 : 1,
            api.CloseCount(api.ThreadHandleValue));
        Assert.Equal(failure.HasFlag(CloseFailure.Job) ? 2 : 1,
            api.CloseCount(api.JobHandleValue));
        await owned.Boundary.DisposeAsync();
    }

    [Fact]
    public async Task JobOnlyReaperRequiresEmptyAccountingAndClosesJobOnce()
    {
        var api = new FakeWindowsContainmentApi();
        await using var boundary = new WindowsJobBoundary(api);
        var job = await boundary.CreateAsync(Guid.NewGuid(), CancellationToken.None);
        var reaper = boundary.CreateReaper(job, null);
        api.ActiveProcessCounts.Enqueue(1);

        var pending = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);

        Assert.True(pending.ProcessExited);
        Assert.False(pending.JobEmpty);
        Assert.Null(pending.Process);
        await Assert.ThrowsAsync<HelperBoundaryException>(() => reaper.DisposeAsync().AsTask());
        api.ActiveProcessCounts.Enqueue(0);
        var terminal = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None);
        Assert.True(terminal.ProcessExited);
        Assert.True(terminal.JobEmpty);
        await reaper.DisposeAsync();
        Assert.Equal(1, api.CloseCount(api.JobHandleValue));
    }
}

internal sealed class BlockingWindowsReapDelay : IWindowsReapDelay
{
    private readonly TaskCompletionSource _entered = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal Task Entered => _entered.Task;

    public async ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        _entered.TrySetResult();
        await _release.Task.WaitAsync(cancellationToken);
    }

    internal void Release() => _release.TrySetResult();
}
