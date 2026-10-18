namespace WatchDog.CaptureIsolation.Windows;

internal enum WindowsReaperLifecycle
{
    Active = 0,
    Disposing = 1,
    Disposed = 2,
}

internal sealed class WindowsJobReaper : IHelperReaper
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IWindowsContainmentApi _api;
    private readonly IWindowsReapDelay _delay;
    private readonly WindowsJobBoundary.JobLease _job;
    private readonly WindowsJobBoundary.AdoptedProcessLease? _process;
    private readonly Action<Guid> _onDisposed;
    private HelperReapObservation? _terminalObservation;
    private bool _processClosed;
    private bool _primaryThreadClosed;
    private bool _jobClosed;
    private int _lifecycleState;

    internal WindowsJobReaper(
        IWindowsContainmentApi api,
        IWindowsReapDelay delay,
        WindowsJobBoundary.JobLease job,
        WindowsJobBoundary.AdoptedProcessLease? process,
        Action<Guid> onDisposed)
    {
        _api = api;
        _delay = delay;
        _job = job;
        _process = process;
        _onDisposed = onDisposed;
        _processClosed = process is null;
        _primaryThreadClosed = process is null;
        Identity = NewIdentity();
    }

    public Guid Identity { get; }
    internal RetainedJobIdentity JobIdentity => _job.Identity;
    internal RetainedProcessIdentity? ProcessIdentity => _process?.Identity;
    internal bool IsActive => Lifecycle == WindowsReaperLifecycle.Active;

    public async ValueTask<HelperReapObservation> ObserveAsync(
        TimeSpan waitBudget,
        CancellationToken cancellationToken)
    {
        if (waitBudget < TimeSpan.Zero || waitBudget == Timeout.InfiniteTimeSpan ||
            waitBudget.TotalMilliseconds > uint.MaxValue - 1)
        {
            throw new ArgumentOutOfRangeException(nameof(waitBudget));
        }
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (_terminalObservation is not null)
                return _terminalObservation;

            // Windows containment observation is deliberately nonblocking. The supervisor owns
            // retry cadence and passes its admitted budget; each native proof remains the
            // required WaitForSingleObject(process, 0) snapshot. A pending first snapshot waits
            // at most that admitted budget, then takes one final snapshot without a poller. The
            // budget does not claim to preempt or bound the synchronous Win32 snapshot calls.
            var observation = ObserveSnapshot();
            if (IsTerminal(observation) || waitBudget == TimeSpan.Zero)
                return observation;

            await _delay.DelayAsync(waitBudget, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ObserveSnapshot();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (Lifecycle == WindowsReaperLifecycle.Disposed)
                return;
            if (_terminalObservation is null)
            {
                throw new HelperBoundaryException("helper.windows.reaper.not-terminal");
            }

            SetLifecycle(WindowsReaperLifecycle.Disposing);
            if (!_processClosed || !_primaryThreadClosed)
            {
                var processClose = _process!.Lease.ReleaseAfterTerminal();
                _processClosed = processClose.ProcessClosed;
                _primaryThreadClosed = processClose.PrimaryThreadClosed;
            }
            if (!_jobClosed)
                _jobClosed = _job.Handle.CloseOnce();
            if (!_processClosed || !_primaryThreadClosed || !_jobClosed)
            {
                SetLifecycle(WindowsReaperLifecycle.Active);
                throw new HelperBoundaryException(CloseFailureCode(
                    _processClosed,
                    _primaryThreadClosed,
                    _jobClosed));
            }
            // The registry contains this owner only while Active or Disposing. Removal is
            // nonthrowing and happens before publishing Disposed, so CreateReaper can reject a
            // concurrent Disposing owner without ever taking this reaper gate.
            _onDisposed(_job.Identity.OpaqueHandleIdentity);
            SetLifecycle(WindowsReaperLifecycle.Disposed);
        }
        finally
        {
            _gate.Release();
        }
    }

    private HelperReapObservation ObserveSnapshot()
    {
        var processExited = _process is null ||
            (_api.WaitForProcess(_process.Lease.ProcessHandle) switch
            {
                WindowsWaitResult.Signaled => true,
                WindowsWaitResult.Timeout => false,
                _ => throw new HelperBoundaryException("helper.windows.reaper.wait-unknown"),
            });
        var jobEmpty = _api.QueryActiveProcessCount(_job.Handle) == 0;
        var observation = new HelperReapObservation(
            _process?.Identity,
            _job.Identity,
            processExited,
            jobEmpty);
        if (IsTerminal(observation))
            _terminalObservation = observation;
        return observation;
    }

    private static bool IsTerminal(HelperReapObservation observation) =>
        observation.ProcessExited && observation.JobEmpty;

    private static string CloseFailureCode(
        bool processClosed,
        bool threadClosed,
        bool jobClosed) => (processClosed, threadClosed, jobClosed) switch
        {
            (false, false, false) => "helper.windows.reaper.process-thread-and-job-close-failed",
            (false, false, true) => "helper.windows.reaper.process-and-thread-close-failed",
            (false, true, false) => "helper.windows.reaper.process-and-job-close-failed",
            (true, false, false) => "helper.windows.reaper.thread-and-job-close-failed",
            (false, true, true) => "helper.windows.reaper.process-close-failed",
            (true, false, true) => "helper.windows.reaper.thread-close-failed",
            _ => "helper.windows.reaper.job-close-failed",
        };

    private static Guid NewIdentity()
    {
        Guid identity;
        do
        {
            identity = Guid.NewGuid();
        }
        while (identity == Guid.Empty);
        return identity;
    }

    private WindowsReaperLifecycle Lifecycle =>
        (WindowsReaperLifecycle)Volatile.Read(ref _lifecycleState);

    private void SetLifecycle(WindowsReaperLifecycle lifecycle) =>
        Volatile.Write(ref _lifecycleState, (int)lifecycle);

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Lifecycle == WindowsReaperLifecycle.Disposed, this);
}
