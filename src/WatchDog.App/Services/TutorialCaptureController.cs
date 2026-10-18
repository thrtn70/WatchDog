using WatchDog.Core.Capture;
using WatchDog.Core.Tutorial;

namespace WatchDog.App.Services;

/// <summary>Actual lifecycle receipts, including cleanup after a revoked Start.</summary>
public sealed record TutorialStartResult(
    CaptureTransitionResult StartTransition,
    CaptureTransitionResult? ReconciliationStopTransition,
    CaptureSessionContext? RetainedCleanupContext,
    bool Revoked);

/// <summary>One atomic bookkeeping observation; revisions are not native admission evidence.</summary>
public sealed record TutorialCaptureState(
    long ConfirmationRevision,
    bool ConfirmationAvailable,
    bool AdmissionClosed,
    bool IsOperationInFlight,
    long? OperationRevision,
    long? RetainedRevision,
    CaptureSessionContext? RetainedCleanupContext);

/// <summary>Headless command bookkeeping. The coordinator alone owns capture resources.</summary>
public sealed class TutorialCaptureController
{
    // Reuse the pure binding validator for shape only. Host/configuration authority remains
    // the backend's serialized ValidateStart, before replacement of an existing owner.
    private static readonly CaptureStartupBaselineResult ShapeValidator = CaptureStartupBaselineResult.Create(null);
    private readonly object _sync = new();
    private readonly Func<bool> _admissionClosed;
    private readonly Func<CaptureLifecycleSnapshot> _snapshot;
    private readonly Func<CaptureRequest, CancellationToken, Task<CaptureTransitionResult>> _start;
    private readonly Func<CaptureSessionContext, CancellationToken, Task<CaptureTransitionResult>> _stop;
    private TutorialRecordingPlan? _confirmation;
    private long _revision;
    private long _consumedRevision;
    private bool _closed;
    private Task? _inFlight;
    private StartOperation? _starting;
    private CaptureSessionContext? _retained;
    private long? _retainedRevision;
    private long? _operationRevision;
    private TutorialStartResult? _lastStart;
    private CaptureTransitionResult? _lastStop;
    private CaptureTransitionResult? _lastSourceLossStop;
    private CaptureSessionContext? _lossDuringStop;

    internal TutorialCaptureController(Func<bool> admissionClosed,
        Func<CaptureLifecycleSnapshot> snapshot,
        Func<CaptureRequest, CancellationToken, Task<CaptureTransitionResult>> start,
        Func<CaptureSessionContext, CancellationToken, Task<CaptureTransitionResult>> stop) =>
        (_admissionClosed, _snapshot, _start, _stop) = (admissionClosed, snapshot, start, stop);

    public CaptureSessionContext? RetainedCleanupContext { get { lock (_sync) return _retained; } }
    public TutorialStartResult? LastStartResult { get { lock (_sync) return _lastStart; } }
    public CaptureTransitionResult? LastStopTransition { get { lock (_sync) return _lastStop; } }
    public CaptureTransitionResult? LastSourceLossStopTransition { get { lock (_sync) return _lastSourceLossStop; } }
    public bool IsOperationInFlight { get { lock (_sync) return _inFlight is not null; } }

    public TutorialCaptureState State
    {
        get
        {
            lock (_sync)
                return new(_revision, _confirmation is not null && _revision > _consumedRevision,
                    _closed || _admissionClosed(), _inFlight is not null, _operationRevision,
                    _retainedRevision, _retained);
        }
    }

    /// <summary>Invalidation only. Observers must reread State; delivery order is not a state version.</summary>
    public event Action? StateChanged;

    /// <summary>A refresh/replacement grants a new revision but never discards returned cleanup.</summary>
    public long Confirm(TutorialRecordingPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        CancellationTokenSource? cancellation;
        long revision;
        lock (_sync)
        {
            cancellation = RevokeUnderLock();
            revision = checked(++_revision);
            _confirmation = _closed ? null : plan;
        }
        Cancel(cancellation);
        NotifyStateChanged();
        return revision;
    }

    /// <summary>Prepare a workspace draft without revoking another workspace's submitted work.</summary>
    internal long? ConfirmForWorkspace(TutorialRecordingPlan plan, long? ownedRevision)
    {
        ArgumentNullException.ThrowIfNull(plan);
        CancellationTokenSource? cancellation;
        long revision;
        lock (_sync)
        {
            if ((_inFlight is not null && _operationRevision != ownedRevision) ||
                (_retained is not null && _retainedRevision != ownedRevision)) return null;
            cancellation = RevokeUnderLock();
            revision = checked(++_revision);
            _confirmation = _closed ? null : plan;
        }
        Cancel(cancellation);
        NotifyStateChanged();
        return revision;
    }

    public void Revoke()
    {
        CancellationTokenSource? cancellation;
        lock (_sync) cancellation = RevokeUnderLock();
        Cancel(cancellation);
        NotifyStateChanged();
    }

    /// <summary>A stale workspace cannot revoke a newer confirmation or submitted operation.</summary>
    public void Revoke(long revision)
    {
        CancellationTokenSource? cancellation = null;
        lock (_sync)
        {
            if (_revision == revision) _confirmation = null;
            if (_starting is { } operation && operation.Revision == revision)
            {
                operation.Revoked = true;
                cancellation = operation.Cancellation;
            }
        }
        Cancel(cancellation);
        NotifyStateChanged();
    }

    public Task<TutorialStartResult> StartAsync(long confirmationRevision, CancellationToken cancellationToken = default)
    {
        StartOperation operation;
        lock (_sync)
        {
            CaptureFailure? failure = _closed || _admissionClosed()
                ? new(CaptureFailureKind.AdmissionClosed, "tutorial.admission-closed", "Recording admission is closed.")
                : _inFlight is not null || _retained is not null
                    ? new(CaptureFailureKind.InvalidState, "tutorial.cleanup-outstanding", "Finish the current command and its exact cleanup before starting again.")
                    : _confirmation is null || confirmationRevision != _revision || confirmationRevision <= _consumedRevision
                        ? new(CaptureFailureKind.Superseded, "tutorial.confirmation-stale", "Confirm a fresh tutorial plan before starting.")
                        : ValidatePlan(_confirmation.Request);
            if (failure is not null)
                return Task.FromResult(new TutorialStartResult(Rejected(CaptureOperationKind.Start, failure), null, _retained, false));

            _consumedRevision = confirmationRevision;
            operation = new(_confirmation!.Request, confirmationRevision, cancellationToken);
            _starting = operation;
            _inFlight = operation.Completion.Task;
            _operationRevision = operation.Revision;
        }
        NotifyStateChanged();
        _ = RunStartAsync(operation);
        return operation.Completion.Task;
    }

    private async Task RunStartAsync(StartOperation operation)
    {
        CaptureTransitionResult start;
        try
        {
            start = operation.Cancellation.IsCancellationRequested
                ? Rejected(CaptureOperationKind.Start, CancelledFailure())
                : await _start(operation.Request, operation.Cancellation.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The manager/coordinator callbacks return typed effects. An unexpected adapter
            // failure is not allowed to abandon observation of this submitted operation.
            start = Rejected(CaptureOperationKind.Start, new(CaptureFailureKind.Unexpected,
                "tutorial.dispatch-failed", "Tutorial dispatch did not return a lifecycle receipt."));
        }

        CaptureSessionContext? cleanup;
        lock (_sync)
        {
            cleanup = AcquiredContext(operation.Request, start);
            _retained = cleanup;
            _retainedRevision = cleanup is null ? null : operation.Revision;
            if (operation.Losses.Any(loss => MatchesContext(cleanup, loss)))
                operation.Revoked = true;
            operation.Revoked |= operation.Cancellation.IsCancellationRequested;
            if (operation.Revoked && _revision == operation.Revision)
                _confirmation = null;
            if (!operation.Revoked || cleanup is null)
            {
                CompleteStartUnderLock(operation, start, null);
                cleanup = null;
            }
        }
        NotifyStateChanged();

        if (cleanup is not null)
        {
            // Cleanup must outlive revoked consent and the original caller's cancellation.
            var stop = await StopExactAsync(cleanup, CancellationToken.None).ConfigureAwait(false);
            lock (_sync)
            {
                ApplyStopUnderLock(cleanup, stop);
                CompleteStartUnderLock(operation, start, stop);
            }
            NotifyStateChanged();
        }
        operation.Cancellation.Dispose();
    }

    private void CompleteStartUnderLock(StartOperation operation, CaptureTransitionResult start, CaptureTransitionResult? stop)
    {
        _lastStart = new(start, stop, _retained, operation.Revoked);
        _starting = null;
        _inFlight = null;
        _operationRevision = null;
        operation.Completion.TrySetResult(_lastStart);
    }

    public Task<CaptureTransitionResult> StopAsync(CancellationToken cancellationToken = default) =>
        RequestStop(cancellationToken, null);

    public Task<CaptureTransitionResult> StopAsync(long ownedRevision, CancellationToken cancellationToken = default) =>
        RequestStop(cancellationToken, null, ownedRevision);

    private Task<CaptureTransitionResult> RequestStop(CancellationToken cancellationToken, CaptureSessionContext? sourceLoss,
        long? ownedRevision = null)
    {
        TaskCompletionSource<CaptureTransitionResult> completion;
        CaptureSessionContext expected;
        lock (_sync)
        {
            if (ownedRevision is not null && ownedRevision != _retainedRevision)
                return Task.FromResult(Rejected(CaptureOperationKind.Stop, new(CaptureFailureKind.Superseded,
                    "tutorial.stop-stale", "This workspace no longer owns the retained recording.")));
            if (sourceLoss is not null && MatchesContext(_retained, sourceLoss) &&
                _inFlight is Task<CaptureTransitionResult> existingStop)
            {
                _lossDuringStop = sourceLoss;
                return existingStop;
            }
            if (sourceLoss is not null && !MatchesContext(_retained, sourceLoss))
                return Task.FromResult(Rejected(CaptureOperationKind.Stop, new(CaptureFailureKind.Superseded,
                    "tutorial.loss-stale", "The source-loss observation no longer matches retained tutorial cleanup.")));
            if (_inFlight is not null || _retained is null)
                return Task.FromResult(Rejected(CaptureOperationKind.Stop, new(CaptureFailureKind.InvalidState,
                    "tutorial.stop-unavailable", "Wait for the current command or retain an exact tutorial context before Stop.")));
            expected = _retained;
            if (ownedRevision is null || _revision == ownedRevision) _confirmation = null;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _inFlight = completion.Task;
            _operationRevision = _retainedRevision;
        }
        NotifyStateChanged();
        _ = RunStopAsync(expected, cancellationToken, completion);
        return completion.Task;
    }

    private async Task RunStopAsync(CaptureSessionContext expected, CancellationToken token,
        TaskCompletionSource<CaptureTransitionResult> completion)
    {
        var result = await StopExactAsync(expected, token).ConfigureAwait(false);
        bool reconcileLoss;
        lock (_sync)
        {
            ApplyStopUnderLock(expected, result);
            reconcileLoss = MatchesContext(_lossDuringStop, expected) && _retained is not null;
            _lossDuringStop = null;
            if (!reconcileLoss)
            {
                _inFlight = null;
                _operationRevision = null;
                completion.TrySetResult(result);
            }
        }
        NotifyStateChanged();
        if (!reconcileLoss) return;

        // An intercepted loss cannot disappear with a caller-cancelled queued Stop.
        // Keep that Stop's receipt and separately record one uncancelled safety retry.
        var lossStop = await StopExactAsync(expected, CancellationToken.None).ConfigureAwait(false);
        lock (_sync)
        {
            _lastSourceLossStop = lossStop;
            if (lossStop.Operation == CaptureOperationKind.Stop && ProvesRelease(expected, lossStop))
            {
                _retained = null;
                _retainedRevision = null;
            }
            _lossDuringStop = null;
            _inFlight = null;
            _operationRevision = null;
            completion.TrySetResult(result);
        }
        NotifyStateChanged();
    }

    private async Task<CaptureTransitionResult> StopExactAsync(CaptureSessionContext expected, CancellationToken token)
    {
        try { return await _stop(expected, token).ConfigureAwait(false); }
        catch (Exception)
        {
            return Rejected(CaptureOperationKind.Stop, new(CaptureFailureKind.Unexpected,
                "tutorial.stop-failed", "Tutorial Stop did not return a lifecycle receipt; cleanup remains retained."));
        }
    }

    private void ApplyStopUnderLock(CaptureSessionContext expected, CaptureTransitionResult result)
    {
        _lastStop = result;
        if (result.Operation == CaptureOperationKind.Stop && ProvesRelease(expected, result))
        {
            _retained = null;
            _retainedRevision = null;
        }
    }

    /// <summary>Intercept only tutorial-correlated loss; foreign events keep their existing route.</summary>
    internal Task<bool>? HandleSourceLoss(CaptureSessionContext context)
    {
        lock (_sync)
        {
            if (_starting is { } starting && MatchesRequest(starting.Request, context))
            {
                // Starting has no current snapshot context. Correlate the complete event with
                // the returned session/generation before letting a stale loss revoke consent.
                starting.Losses.Add(context);
                return ObservePendingLossAsync(starting, context);
            }
            if (!MatchesContext(_retained, context)) return null;
            _confirmation = null;
            if (_inFlight is { } stop)
            {
                _lossDuringStop = context;
                return ObserveLossStopAsync(stop);
            }
        }
        return ObserveLossStopAsync(RequestStop(CancellationToken.None, context));
    }

    private static async Task<bool> ObservePendingLossAsync(StartOperation operation, CaptureSessionContext loss)
    {
        var result = await operation.Completion.Task.ConfigureAwait(false);
        // A matching request can already belong to another session. Its event still needs
        // the manager's ordinary exact-loss handling if this Start did not acquire it.
        return MatchesContext(AcquiredContext(operation.Request, result.StartTransition), loss);
    }

    private static async Task<bool> ObserveLossStopAsync(Task stop)
    {
        await stop.ConfigureAwait(false);
        return true;
    }

    internal void CloseAdmission()
    {
        CancellationTokenSource? cancellation;
        lock (_sync) { _closed = true; cancellation = RevokeUnderLock(); }
        Cancel(cancellation);
        NotifyStateChanged();
    }

    internal Task DrainAsync() { lock (_sync) return _inFlight ?? Task.CompletedTask; }

    internal void ObserveShutdown(CaptureTransitionResult result)
    {
        lock (_sync)
        {
            if (_retained is { } expected && result.Operation == CaptureOperationKind.Shutdown &&
                result.After.Readiness == CaptureReadiness.Closed && ProvesRelease(expected, result))
            {
                _retained = null;
                _retainedRevision = null;
            }
        }
        NotifyStateChanged();
    }

    private void NotifyStateChanged()
    {
        if (StateChanged is not { } observers) return;
        foreach (Action observer in observers.GetInvocationList())
        {
            try { observer(); }
            catch (Exception) { /* Presentation failures must not interrupt owned cleanup or receipts. */ }
        }
    }

    private CancellationTokenSource? RevokeUnderLock()
    {
        _confirmation = null;
        if (_starting is not { } operation) return null;
        operation.Revoked = true;
        return operation.Cancellation;
    }

    private static void Cancel(CancellationTokenSource? cancellation)
    {
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { } // Completion won the race; its receipt remains retained.
    }

    private static bool MatchesRequest(CaptureRequest request, CaptureSessionContext? context) =>
        context is not null && context.SessionId != Guid.Empty && context.Generation > 0 &&
        context.Request == request && context.RequestedTarget == request.Target;

    private static CaptureSessionContext? AcquiredContext(CaptureRequest request, CaptureTransitionResult result) =>
        result.Operation is CaptureOperationKind.Start or CaptureOperationKind.Switch &&
        result.Disposition is CaptureCommandDisposition.Completed or CaptureCommandDisposition.Failed &&
        MatchesRequest(request, result.After.Context) &&
        (result.Before.Context?.SessionId != result.After.Context!.SessionId ||
         result.Before.Context?.Generation != result.After.Context.Generation)
            ? result.After.Context : null;

    private static bool MatchesContext(CaptureSessionContext? expected, CaptureSessionContext? actual) =>
        expected is not null && actual is not null && MatchesRequest(expected.Request, actual) &&
        expected.SessionId == actual.SessionId && expected.Generation == actual.Generation &&
        expected.RequestedTarget == actual.RequestedTarget && expected.ActualTarget == actual.ActualTarget;

    private static bool ProvesRelease(CaptureSessionContext expected, CaptureTransitionResult result) =>
        result.Disposition is CaptureCommandDisposition.Completed or CaptureCommandDisposition.Failed &&
        MatchesContext(expected, result.Before.Context) && result.After.Context is null;

    private static CaptureFailure? ValidatePlan(CaptureRequest request)
    {
        if (request.StartupExpectation is null)
            return new(CaptureFailureKind.InvalidRequest, "tutorial.binding-required", "A bound tutorial plan is required.");
        var shapeFailure = ShapeValidator.Validate(request);
        return shapeFailure?.Kind == CaptureFailureKind.InvalidRequest ? shapeFailure : null;
    }

    private CaptureTransitionResult Rejected(CaptureOperationKind operation, CaptureFailure failure)
    {
        var snapshot = _snapshot();
        return new(Guid.NewGuid(), operation, CaptureCommandDisposition.Rejected, snapshot, snapshot, failure);
    }

    private static CaptureFailure CancelledFailure() => new(CaptureFailureKind.Cancelled,
        "tutorial.cancelled", "Tutorial recording intent was cancelled.");

    private sealed class StartOperation(CaptureRequest request, long revision, CancellationToken cancellationToken)
    {
        public CaptureRequest Request { get; } = request;
        public long Revision { get; } = revision;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        public TaskCompletionSource<TutorialStartResult> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<CaptureSessionContext> Losses { get; } = [];
        public bool Revoked { get; set; }
    }
}
