using WatchDog.Core.Capture;
using WatchDog.Core.Tutorial;

namespace WatchDog.App.Services;

/// <summary>
/// Per-workspace command bookkeeping over the manager's existing controller. It owns no
/// capture resources. Production construction cannot admit an unqualified native Start.
/// </summary>
public sealed class TutorialWorkspaceCapture : IDisposable
{
    private const string QualificationReason = "Tutorial recording is unavailable while recording safety checks are incomplete.";
    private readonly object _sync = new();
    private readonly TutorialCaptureController _controller;
    private readonly bool _startAdmitted;
    private object? _confirmationAttempt;
    private long? _confirmationRevision;
    private TutorialRecordingPlan? _confirmedPlan;
    private long? _submittedRevision;
    private TutorialRecordingPlan? _submittedPlan;
    private bool _submittedRevoked;
    private Task<TutorialStartResult?>? _startTask;
    private Task<CaptureTransitionResult?>? _stopTask;
    private Task<bool>? _closeTask;
    private bool _closing;
    private bool _disposed;
    private string? _failure;

    public TutorialWorkspaceCapture(TutorialCaptureController controller) : this(controller, startAdmitted: false) { }

    // Only fake-backed integration tests admit Start in this slice. No persisted/user switch.
    internal TutorialWorkspaceCapture(TutorialCaptureController controller, bool startAdmitted)
    {
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _startAdmitted = startAdmitted;
        _controller.StateChanged += OnStateChanged;
    }

    /// <summary>Invalidation only; reread properties on the UI synchronization context.</summary>
    public event Action? StateChanged;

    public bool StartAvailable
    {
        get
        {
            lock (_sync)
            {
                var state = _controller.State;
                return !_disposed && !_closing && _startAdmitted && _confirmedPlan is not null &&
                    _confirmationRevision == state.ConfirmationRevision && state.ConfirmationAvailable &&
                    !state.AdmissionClosed && !state.IsOperationInFlight && state.RetainedCleanupContext is null &&
                    _startTask?.IsCompleted != false && _stopTask?.IsCompleted != false;
            }
        }
    }

    public string StartReason
    {
        get
        {
            lock (_sync)
            {
                if (!_startAdmitted) return QualificationReason;
                if (_disposed || _closing || _controller.State.AdmissionClosed) return "Recording admission is closed.";
                if (IsOperationInFlight) return "Wait for the current recording command to finish.";
                if (_controller.State.RetainedCleanupContext is not null) return "Stop the current recording and finish cleanup before starting another.";
                return StartAvailable ? "Start the confirmed tutorial recording." : "Confirm the current source and camera choices before starting.";
            }
        }
    }

    public CaptureSessionContext? RetainedCleanupContext
    {
        get
        {
            lock (_sync)
            {
                var state = _controller.State;
                return _submittedRevision is not null && state.RetainedRevision == _submittedRevision
                    ? state.RetainedCleanupContext : null;
            }
        }
    }

    public bool IsOperationInFlight
    {
        get
        {
            lock (_sync)
            {
                var state = _controller.State;
                return _startTask?.IsCompleted == false || _stopTask?.IsCompleted == false ||
                    (_submittedRevision is not null && state.IsOperationInFlight && state.OperationRevision == _submittedRevision);
            }
        }
    }

    public bool StopAvailable
    {
        get { lock (_sync) return !_disposed && RetainedCleanupContext is not null && !IsOperationInFlight; }
    }
    public bool CancelAvailable
    {
        get { lock (_sync) return !_disposed && _startTask?.IsCompleted == false; }
    }
    public TutorialRecordingPlan? ActivePlan
    {
        get { lock (_sync) return IsOperationInFlight || RetainedCleanupContext is not null ? _submittedPlan : null; }
    }
    public string? StatusText
    {
        get
        {
            lock (_sync)
                return IsOperationInFlight ? "Waiting for the recording command and any required cleanup to finish." : _failure;
        }
    }

    public void Confirm(TutorialRecordingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var attempt = new object();
        (long? Confirmation, long? Submitted) revoked;
        long observedRevision;
        lock (_sync)
        {
            if (_disposed || _closing) return;
            _confirmationAttempt = attempt;
            observedRevision = _controller.State.ConfirmationRevision;
            revoked = RevokeUnderLock();
        }
        RevokeRevisions(revoked);
        lock (_sync)
        {
            // Initial revocation can synchronously confirm newer intent, including
            // another workspace's draft. Never replace that callback's confirmation.
            if (!IsCurrentAttempt(attempt) || _controller.State.ConfirmationRevision != observedRevision) return;
            // Check foreign ownership and publish the confirmation atomically in the controller.
            var revision = _controller.ConfirmForWorkspace(plan, _submittedRevision);
            // Controller callbacks run before its revision is returned. Retire only
            // this obsolete result; a nested confirmation/start keeps its own custody.
            if (!IsCurrentAttempt(attempt) ||
                (revision is { } returned && _controller.State.ConfirmationRevision != returned))
            {
                if (revision is { } stale) _controller.Revoke(stale);
                return;
            }
            _confirmationRevision = revision;
            _confirmedPlan = revision is null ? null : plan;
            _failure = null;
        }
        NotifyStateChanged();
    }

    private bool IsCurrentAttempt(object attempt) =>
        !_disposed && !_closing && ReferenceEquals(_confirmationAttempt, attempt);

    public void Revoke()
    {
        (long? Confirmation, long? Submitted) revoked;
        lock (_sync)
        {
            _confirmationAttempt = null;
            revoked = RevokeUnderLock();
        }
        RevokeRevisions(revoked);
    }

    private (long? Confirmation, long? Submitted) RevokeUnderLock()
    {
        var revoked = (_confirmationRevision, _submittedRevision);
        if (_startTask?.IsCompleted == false) _submittedRevoked = true;
        _confirmationRevision = null;
        _confirmedPlan = null;
        return revoked;
    }

    private void RevokeRevisions((long? Confirmation, long? Submitted) revoked)
    {
        var (confirmation, submitted) = revoked;
        if (confirmation is { } revision) _controller.Revoke(revision);
        if (submitted is { } owned && owned != confirmation) _controller.Revoke(owned);
        NotifyStateChanged();
    }

    public Task<TutorialStartResult?> StartAsync()
    {
        TaskCompletionSource<TutorialStartResult?> completion;
        long revision;
        lock (_sync)
        {
            // Recheck the gate for direct Execute/API calls; CanExecute is presentation only.
            if (!StartAvailable) return Task.FromResult<TutorialStartResult?>(null);
            revision = _confirmationRevision!.Value;
            _submittedRevision = revision;
            _submittedPlan = _confirmedPlan;
            _submittedRevoked = false;
            _confirmationRevision = null;
            _confirmedPlan = null;
            _failure = null;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _startTask = completion.Task;
        }
        NotifyStateChanged();
        _ = CompleteStartAsync(revision, completion);
        return completion.Task;
    }

    private async Task CompleteStartAsync(long revision, TaskCompletionSource<TutorialStartResult?> completion)
    {
        TutorialStartResult? result = null;
        try
        {
            result = await _controller.StartAsync(revision).ConfigureAwait(false);
            lock (_sync)
            {
                // Cancel can arrive after the controller committed its receipt but before
                // this adapter observed it. Preserve intent across that completion gap.
                // Do not silently retry a cleanup the controller already attempted.
                if (!_submittedRevoked || result.Revoked || RetainedCleanupContext is null)
                {
                    CompleteStartUnderLock(result, completion);
                }
            }
            if (!completion.Task.IsCompleted)
            {
                var stop = await _controller.StopAsync(revision, CancellationToken.None).ConfigureAwait(false);
                lock (_sync)
                {
                    result = result with
                    {
                        ReconciliationStopTransition = stop,
                        RetainedCleanupContext = RetainedCleanupContext,
                        Revoked = true
                    };
                    CompleteStartUnderLock(result, completion);
                }
            }
        }
        catch (Exception)
        {
            lock (_sync)
            {
                _failure = "Recording needs attention. Cleanup ownership has not been discarded.";
                completion.TrySetResult(result);
            }
        }
        NotifyStateChanged();
    }

    // Checking cancellation and completing the adapter task share one lock. Otherwise
    // a Cancel could be accepted between the last check and completion, then be lost.
    private void CompleteStartUnderLock(TutorialStartResult result, TaskCompletionSource<TutorialStartResult?> completion)
    {
        result = result with { Revoked = result.Revoked || _submittedRevoked };
        _failure = result.RetainedCleanupContext is not null &&
            (result.Revoked || !result.StartTransition.Succeeded)
            ? "Recording needs cleanup. Use Stop to retry before closing."
            : !result.StartTransition.Succeeded ? "Recording could not start. Confirm the source again before retrying." : null;
        completion.TrySetResult(result);
    }

    public Task<CaptureTransitionResult?> StopAsync()
    {
        TaskCompletionSource<CaptureTransitionResult?> completion;
        long revision;
        lock (_sync)
        {
            if (!StopAvailable) return Task.FromResult<CaptureTransitionResult?>(null);
            revision = _submittedRevision!.Value;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
        }
        NotifyStateChanged();
        _ = CompleteStopAsync(revision, completion);
        return completion.Task;
    }

    private async Task CompleteStopAsync(long revision, TaskCompletionSource<CaptureTransitionResult?> completion)
    {
        CaptureTransitionResult? result = null;
        try { result = await _controller.StopAsync(revision, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception) { /* Keep controller custody; render a fixed warning below. */ }
        lock (_sync)
            _failure = RetainedCleanupContext is not null
                ? "Recording cleanup is incomplete. Use Stop to retry before closing."
                : result?.Failure is not null ? "Recording stopped with a warning. Check recording diagnostics." : null;
        completion.TrySetResult(result);
        NotifyStateChanged();
    }

    public Task<bool> CloseAsync()
    {
        TaskCompletionSource<bool> completion;
        lock (_sync)
        {
            if (_closeTask?.IsCompleted == false) return _closeTask;
            _closing = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _closeTask = completion.Task;
        }
        Revoke();
        _ = CompleteCloseAsync(completion);
        return completion.Task;
    }

    private async Task CompleteCloseAsync(TaskCompletionSource<bool> completion)
    {
        var released = false;
        try
        {
            Task? start;
            Task? stop;
            lock (_sync) { start = _startTask; stop = _stopTask; }
            if (start is not null) await start.ConfigureAwait(false);
            if (stop is not null) await stop.ConfigureAwait(false);
            if (StopAvailable) await StopAsync().ConfigureAwait(false);
            released = !IsOperationInFlight && RetainedCleanupContext is null;
        }
        catch (Exception) { /* Failed observation cannot prove release. */ }
        lock (_sync)
        {
            if (!released)
            {
                _closing = false;
                _failure = "Recording cleanup is incomplete. Use Stop or Close to retry.";
            }
        }
        completion.TrySetResult(released);
        NotifyStateChanged();
    }

    private void OnStateChanged() => NotifyStateChanged();

    private void NotifyStateChanged()
    {
        if (_disposed || StateChanged is not { } observers) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception) { /* A view cannot interrupt command observation. */ }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Revoke();
        _controller.StateChanged -= OnStateChanged;
        // Ordinary close awaits CloseAsync first. Host shutdown retains manager-owned drain.
    }
}
