using WatchDog.Core.Devices;

namespace WatchDog.Core.Capture;

/// <summary>
/// Serializes lifecycle commands and owns admission, session identity, and truthful state.
/// Backend implementations remain responsible only for the physical capture operations.
/// </summary>
public sealed class CaptureLifecycleCoordinator : IDisposable, IAsyncDisposable
{
    private readonly ICaptureLifecycleBackend _backend;
    private readonly ICaptureLivenessFailureClaimSource? _livenessClaims;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _shutdownSync = new();
    private CaptureLifecycleSnapshot _snapshot = CaptureLifecycleSnapshot.Initial;
    private ManualReservation? _manualOwner;
    private ManualReservation? _pendingManualReservation;
    private Task<CaptureTransitionResult>? _shutdownTask;
    private long _generation;
    private int _admissionClosed;
    private int _storageAdmissionBlocked;
    private CaptureFailure? _storageFailure;
    private int _disposeRequested;

    public CaptureLifecycleCoordinator(
        ICaptureLifecycleBackend backend,
        ICaptureLivenessFailureClaimSource? livenessClaims = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _livenessClaims = livenessClaims;
    }

    public CaptureLifecycleSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Publishes every owned lifecycle snapshot after it becomes current. Subscribers
    /// are observational only; a failing subscriber can never break capture ownership.
    /// </summary>
    public event Action<CaptureLifecycleSnapshot>? SnapshotChanged;

    public bool IsAdmissionClosed => Volatile.Read(ref _admissionClosed) != 0;

    public bool IsStorageAdmissionBlocked => Volatile.Read(ref _storageAdmissionBlocked) != 0;

    private bool HasManualProtection =>
        Volatile.Read(ref _manualOwner) is not null ||
        Volatile.Read(ref _pendingManualReservation) is not null;

    public Task<CaptureTransitionResult> StartAsync(
        CaptureRequest request,
        CancellationToken cancellationToken = default) =>
        BeginAsync(request, CaptureOperationKind.Start, cancellationToken);

    public Task<CaptureTransitionResult> SwitchAsync(
        CaptureRequest request,
        CancellationToken cancellationToken = default) =>
        BeginAsync(request, CaptureOperationKind.Switch, cancellationToken);

    public Task<CaptureTransitionResult> StopAsync(
        CaptureInitiation initiatedBy,
        CancellationToken cancellationToken = default) =>
        StopCoreAsync(initiatedBy, null, cancellationToken);

    /// <summary>
    /// Stops only the exact manual window, display or region context supplied by a caller. The identity check
    /// occurs under the lifecycle's existing serialization, before any reservation release.
    /// </summary>
    public Task<CaptureTransitionResult> StopOwnedManualAsync(
        CaptureSessionContext expected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return StopCoreAsync(CaptureInitiation.Manual, expected, cancellationToken);
    }

    private async Task<CaptureTransitionResult> StopCoreAsync(
        CaptureInitiation initiatedBy,
        CaptureSessionContext? expected,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        if (!await EnterSerialAsync(cancellationToken).ConfigureAwait(false))
        {
            return CancelledTransition(operationId, CaptureOperationKind.Stop);
        }

        try
        {
            var before = Snapshot;
            if (IsAdmissionClosed || before.Status == CaptureLifecycleStatus.Shutdown)
            {
                return RejectedTransition(
                    operationId,
                    CaptureOperationKind.Stop,
                    before,
                    AdmissionClosedFailure());
            }

            if (expected is not null)
            {
                if (expected.Request.Target is not (WindowCaptureTarget or DisplayCaptureTarget or RegionCaptureTarget) ||
                    !ManualWindowCapturePolicy.MatchesOwnedContext(expected, before.Context))
                {
                    return RejectedTransition(
                        operationId,
                        CaptureOperationKind.Stop,
                        before,
                        new CaptureFailure(CaptureFailureKind.Superseded, "capture.manual.owner-changed",
                            "The selected manual source no longer owns this capture."));
                }
                if (before.Readiness != CaptureReadiness.Available ||
                    before.Status is not (CaptureLifecycleStatus.Active or CaptureLifecycleStatus.Faulted))
                {
                    return RejectedTransition(
                        operationId,
                        CaptureOperationKind.Stop,
                        before,
                        new CaptureFailure(CaptureFailureKind.InvalidState, "capture.manual.stop-unavailable",
                            "The selected manual source is not available for Stop."));
                }
            }

            if (HasManualProtection && initiatedBy != CaptureInitiation.Manual)
            {
                return RejectedTransition(
                    operationId,
                    CaptureOperationKind.Stop,
                    before,
                    ManualPriorityFailure());
            }

            if (before.Context is null)
            {
                ReleaseManualOwner(null);
                var idle = new CaptureLifecycleSnapshot(
                    CaptureLifecycleStatus.Idle,
                    CaptureReadiness.Available,
                    CaptureHealth.Healthy,
                    null,
                    null);
                SetSnapshot(idle);
                return CompletedTransition(operationId, CaptureOperationKind.Stop, before, idle);
            }

            SetSnapshot(before with
            {
                Status = CaptureLifecycleStatus.Stopping,
                Readiness = CaptureReadiness.Busy,
                LastFailure = null,
            });

            var backendResult = await StopBackendAsync(before.Context, cancellationToken).ConfigureAwait(false);
            if (!backendResult.Succeeded)
            {
                var failure = backendResult.Failure ?? InvalidBackendResponse("stop");
                if (backendResult.CleanupRequired)
                {
                    var retained = before with
                    {
                        Status = CaptureLifecycleStatus.Faulted,
                        Readiness = CaptureReadiness.Available,
                        Health = CaptureHealth.Failed,
                        LastFailure = failure,
                    };
                    SetSnapshot(retained);
                    return FailedTransition(operationId, CaptureOperationKind.Stop, before, retained, failure);
                }

                ReleaseManualOwner(before.Context);
                var released = new CaptureLifecycleSnapshot(
                    CaptureLifecycleStatus.Faulted,
                    CaptureReadiness.Available,
                    CaptureHealth.Degraded,
                    null,
                    failure);
                SetSnapshot(released);
                return FailedTransition(operationId, CaptureOperationKind.Stop, before, released, failure);
            }

            ReleaseManualOwner(before.Context);
            var after = new CaptureLifecycleSnapshot(
                CaptureLifecycleStatus.Idle,
                CaptureReadiness.Available,
                CaptureHealth.Healthy,
                null,
                null);
            SetSnapshot(after);
            return CompletedTransition(operationId, CaptureOperationKind.Stop, before, after);
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<CaptureTransitionResult> HandleSourceLostAsync(
        CaptureSourceLoss sourceLoss,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sourceLoss);
        var operationId = Guid.NewGuid();
        if (!await EnterSerialAsync(cancellationToken).ConfigureAwait(false))
        {
            return CancelledTransition(operationId, CaptureOperationKind.SourceLost);
        }

        try
        {
            var before = Snapshot;
            var context = before.Context;
            if (before.Status != CaptureLifecycleStatus.Active ||
                context is null ||
                context.SessionId != sourceLoss.SessionId ||
                context.Generation != sourceLoss.Generation ||
                !HasExactIdentity(context.ActualTarget, sourceLoss.Target))
            {
                return new CaptureTransitionResult(
                    operationId,
                    CaptureOperationKind.SourceLost,
                    CaptureCommandDisposition.Ignored,
                    before,
                    before,
                    null);
            }

            SetSnapshot(before with
            {
                Status = CaptureLifecycleStatus.Stopping,
                Readiness = CaptureReadiness.Busy,
                LastFailure = null,
            });

            var backendResult = await StopBackendAsync(context, cancellationToken).ConfigureAwait(false);
            if (!backendResult.Succeeded)
            {
                var failure = backendResult.Failure ?? InvalidBackendResponse("source-loss stop");
                var faulted = backendResult.CleanupRequired
                    ? before with
                    {
                        Status = CaptureLifecycleStatus.Faulted,
                        Readiness = CaptureReadiness.Available,
                        Health = CaptureHealth.Failed,
                        LastFailure = failure,
                    }
                    : new CaptureLifecycleSnapshot(
                        CaptureLifecycleStatus.Faulted,
                        CaptureReadiness.Available,
                        CaptureHealth.Degraded,
                        null,
                        failure);
                SetSnapshot(faulted);
                return FailedTransition(operationId, CaptureOperationKind.SourceLost, before, faulted, failure);
            }

            // Manual ownership is intentionally retained. Only an explicit manual stop or
            // a newer manual request can release or replace the reservation.
            var after = new CaptureLifecycleSnapshot(
                CaptureLifecycleStatus.Idle,
                CaptureReadiness.Available,
                CaptureHealth.Healthy,
                null,
                null);
            SetSnapshot(after);
            return CompletedTransition(operationId, CaptureOperationKind.SourceLost, before, after);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>
    /// Stops only the exact capture/device admission named by a metadata observation. Device
    /// observations are independent from target-source loss and from frame/sample liveness.
    /// </summary>
    public async Task<CaptureTransitionResult> HandleDeviceLostAsync(
        CaptureDeviceLossObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var operationId = Guid.NewGuid();
        if (!await EnterSerialAsync(cancellationToken).ConfigureAwait(false))
            return CancelledTransition(operationId, CaptureOperationKind.DeviceLost);

        try
        {
            var before = Snapshot;
            var context = before.Context;
            var admitted = context?.AdmittedDevices.Entries.SingleOrDefault(
                device => device.Role == observation.Role);
            if (IsAdmissionClosed ||
                before.Status is not (CaptureLifecycleStatus.Active or CaptureLifecycleStatus.Saving) ||
                context is null ||
                context.SessionId != observation.SessionId ||
                context.Request.RequestId != observation.RequestId ||
                context.Generation != observation.Generation ||
                admitted is null ||
                admitted.SelectionMode != observation.SelectionMode ||
                !StringComparer.Ordinal.Equals(admitted.RequestedDeviceId, observation.RequestedDeviceId))
            {
                return new CaptureTransitionResult(
                    operationId,
                    CaptureOperationKind.DeviceLost,
                    CaptureCommandDisposition.Ignored,
                    before,
                    before,
                    null);
            }

            var cause = DeviceLossFailure(observation);
            SetSnapshot(before with
            {
                Status = CaptureLifecycleStatus.Stopping,
                Readiness = CaptureReadiness.Busy,
                LastFailure = null,
            });

            // Cancellation may prevent an observation from entering the serialized owner. Once the
            // exact current admission is accepted, safety cleanup must finish independently of the
            // observer-loop token so hosted-service drain cannot strand native ownership.
            var backendResult = await StopBackendAsync(context, CancellationToken.None).ConfigureAwait(false);
            if (!backendResult.Succeeded)
            {
                var cleanup = backendResult.Failure ?? InvalidBackendResponse("active-device stop");
                var retainedCause = cause.WithCleanupFailure(cleanup);
                var faulted = backendResult.CleanupRequired
                    ? before with
                    {
                        Status = CaptureLifecycleStatus.Faulted,
                        Readiness = CaptureReadiness.Available,
                        Health = CaptureHealth.Failed,
                        LastFailure = retainedCause,
                    }
                    : new CaptureLifecycleSnapshot(
                        CaptureLifecycleStatus.Faulted,
                        CaptureReadiness.Available,
                        CaptureHealth.Degraded,
                        null,
                        retainedCause);
                SetSnapshot(faulted);
                return FailedTransition(
                    operationId,
                    CaptureOperationKind.DeviceLost,
                    before,
                    faulted,
                    retainedCause);
            }

            // Preserve any manual reservation exactly as source-loss handling does. A deliberate
            // later manual start may replace it and clears this warning after successful admission.
            var after = new CaptureLifecycleSnapshot(
                CaptureLifecycleStatus.Faulted,
                CaptureReadiness.Available,
                CaptureHealth.Degraded,
                null,
                cause);
            SetSnapshot(after);
            return CompletedTransition(operationId, CaptureOperationKind.DeviceLost, before, after);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>
    /// Stops only the exact active capture named by freshly revalidated fail-closed counter
    /// evidence. Counter failure does not prove a binding error, black/silent media, or A/V drift.
    /// </summary>
    public async Task<CaptureTransitionResult> HandleLivenessFailureAsync(
        CaptureLivenessFailureCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var operationId = Guid.NewGuid();
        if (!await EnterSerialAsync(cancellationToken).ConfigureAwait(false))
            return CancelledTransition(operationId, CaptureOperationKind.LivenessFailed);

        try
        {
            var before = Snapshot;
            var context = before.Context;
            if (_livenessClaims is null ||
                before.Status is not (CaptureLifecycleStatus.Active or CaptureLifecycleStatus.Saving) ||
                context is null ||
                candidate.Evidence.Identity != candidate.Identity ||
                !IsFailClosedLiveness(candidate.Evidence.Status) ||
                context.SessionId != candidate.Identity.SessionId ||
                context.Request.RequestId != candidate.Identity.RequestId ||
                context.Generation != candidate.Identity.Generation ||
                !HasExactIdentity(context.ActualTarget, candidate.Identity.ActualTarget) ||
                !MatchesLivenessAdmission(context, candidate))
            {
                return IgnoredTransition(operationId, CaptureOperationKind.LivenessFailed, before);
            }

            CaptureLivenessFailureClaim? claim;
            try
            {
                claim = await _livenessClaims.TryClaimAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return CancelledTransition(operationId, CaptureOperationKind.LivenessFailed);
            }

            if (claim is null ||
                claim.Candidate.RegistrationId != candidate.RegistrationId ||
                claim.Candidate.Owner != candidate.Owner ||
                claim.Candidate.Identity != candidate.Identity ||
                claim.Candidate.AdmittedAudioDevice != candidate.AdmittedAudioDevice ||
                claim.Candidate.Evidence.Identity != claim.Candidate.Identity ||
                !IsFailClosedLiveness(claim.Candidate.Evidence.Status) ||
                claim.Candidate.Identity.SessionId != context.SessionId ||
                claim.Candidate.Identity.RequestId != context.Request.RequestId ||
                claim.Candidate.Identity.Generation != context.Generation ||
                !HasExactIdentity(context.ActualTarget, claim.Candidate.Identity.ActualTarget) ||
                !MatchesLivenessAdmission(context, claim.Candidate))
            {
                return IgnoredTransition(operationId, CaptureOperationKind.LivenessFailed, before);
            }

            var cause = LivenessFailure(claim.Candidate);
            SetSnapshot(before with
            {
                Status = CaptureLifecycleStatus.Stopping,
                Readiness = CaptureReadiness.Busy,
                LastFailure = null,
            });

            // Once fresh evidence is atomically claimed, cleanup is safety work and must finish
            // even if the observer loop is cancelled during hosted-service drain.
            var backendResult = await StopBackendAsync(context, CancellationToken.None).ConfigureAwait(false);
            if (!backendResult.Succeeded)
            {
                var cleanup = backendResult.Failure ?? InvalidBackendResponse("liveness stop");
                var retainedCause = cause.WithCleanupFailure(cleanup);
                var faulted = backendResult.CleanupRequired
                    ? before with
                    {
                        Status = CaptureLifecycleStatus.Faulted,
                        Readiness = CaptureReadiness.Available,
                        Health = CaptureHealth.Failed,
                        LastFailure = retainedCause,
                    }
                    : new CaptureLifecycleSnapshot(
                        CaptureLifecycleStatus.Faulted,
                        CaptureReadiness.Available,
                        CaptureHealth.Degraded,
                        null,
                        retainedCause);
                SetSnapshot(faulted);
                return FailedTransition(
                    operationId,
                    CaptureOperationKind.LivenessFailed,
                    before,
                    faulted,
                    retainedCause);
            }

            // Manual ownership is deliberately retained. Only an explicit manual action may
            // release or replace that reservation after a fail-closed liveness stop.
            var after = new CaptureLifecycleSnapshot(
                CaptureLifecycleStatus.Faulted,
                CaptureReadiness.Available,
                CaptureHealth.Degraded,
                null,
                cause);
            SetSnapshot(after);
            return CompletedTransition(
                operationId,
                CaptureOperationKind.LivenessFailed,
                before,
                after);
        }
        finally
        {
            _serial.Release();
        }
    }

    /// <summary>
    /// Closes new capture admission immediately and, when the observation still matches
    /// the active generation, stops that capture without bypassing serialized ownership.
    /// A stale observation never stops a newer capture; the storage owner must observe
    /// the current snapshot and submit a new correlated pressure signal.
    /// </summary>
    public async Task<CaptureTransitionResult> HandleStoragePressureAsync(
        CaptureStoragePressure pressure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pressure);
        if (string.IsNullOrWhiteSpace(pressure.Reason))
            throw new ArgumentException("Storage pressure requires a visible reason.", nameof(pressure));

        var storageFailure = StoragePressureFailure(pressure.Reason);
        Volatile.Write(ref _storageFailure, storageFailure);
        Interlocked.Exchange(ref _storageAdmissionBlocked, 1);

        var operationId = Guid.NewGuid();
        if (!await EnterSerialAsync(cancellationToken).ConfigureAwait(false))
            return CancelledTransition(operationId, CaptureOperationKind.StoragePressure);

        try
        {
            var before = Snapshot;
            if (IsAdmissionClosed || before.Status == CaptureLifecycleStatus.Shutdown)
            {
                return RejectedTransition(
                    operationId,
                    CaptureOperationKind.StoragePressure,
                    before,
                    AdmissionClosedFailure());
            }

            var context = before.Context;
            if (context is null)
            {
                var afterIdle = before with
                {
                    Status = CaptureLifecycleStatus.Idle,
                    Readiness = CaptureReadiness.Closed,
                    Health = CaptureHealth.Degraded,
                    LastFailure = storageFailure,
                };
                SetSnapshot(afterIdle);
                return CompletedTransition(
                    operationId,
                    CaptureOperationKind.StoragePressure,
                    before,
                    afterIdle);
            }

            if (pressure.SessionId != context.SessionId ||
                pressure.Generation != context.Generation)
            {
                return new CaptureTransitionResult(
                    operationId,
                    CaptureOperationKind.StoragePressure,
                    CaptureCommandDisposition.Ignored,
                    before,
                    before,
                    null);
            }

            SetSnapshot(before with
            {
                Status = CaptureLifecycleStatus.Stopping,
                Readiness = CaptureReadiness.Busy,
                LastFailure = null,
            });

            var backendResult = await StopBackendAsync(context, cancellationToken).ConfigureAwait(false);
            if (!backendResult.Succeeded)
            {
                var failure = backendResult.Failure ?? InvalidBackendResponse("storage-pressure stop");
                var faulted = backendResult.CleanupRequired
                    ? before with
                    {
                        Status = CaptureLifecycleStatus.Faulted,
                        Readiness = CaptureReadiness.Closed,
                        Health = CaptureHealth.Failed,
                        LastFailure = failure,
                    }
                    : new CaptureLifecycleSnapshot(
                        CaptureLifecycleStatus.Faulted,
                        CaptureReadiness.Closed,
                        CaptureHealth.Degraded,
                        null,
                        failure);
                SetSnapshot(faulted);
                return FailedTransition(
                    operationId,
                    CaptureOperationKind.StoragePressure,
                    before,
                    faulted,
                    failure);
            }

            // Preserve manual ownership after a safety stop so recovery cannot silently
            // restart automatic capture. A later explicit manual request may replace it.
            var after = new CaptureLifecycleSnapshot(
                CaptureLifecycleStatus.Idle,
                CaptureReadiness.Closed,
                CaptureHealth.Degraded,
                null,
                storageFailure);
            SetSnapshot(after);
            return CompletedTransition(
                operationId,
                CaptureOperationKind.StoragePressure,
                before,
                after);
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<CaptureTransitionResult> ClearStoragePressureAsync(
        CancellationToken cancellationToken = default)
    {
        var operationId = Guid.NewGuid();
        if (!await EnterSerialAsync(cancellationToken).ConfigureAwait(false))
            return CancelledTransition(operationId, CaptureOperationKind.StoragePressure);

        try
        {
            var before = Snapshot;
            if (IsAdmissionClosed || before.Status == CaptureLifecycleStatus.Shutdown)
            {
                return RejectedTransition(
                    operationId,
                    CaptureOperationKind.StoragePressure,
                    before,
                    AdmissionClosedFailure());
            }

            Volatile.Write(ref _storageFailure, null);
            Interlocked.Exchange(ref _storageAdmissionBlocked, 0);

            var after = before;
            if (before.Context is null && before.LastFailure?.Kind == CaptureFailureKind.Storage)
            {
                after = new CaptureLifecycleSnapshot(
                    CaptureLifecycleStatus.Idle,
                    CaptureReadiness.Available,
                    CaptureHealth.Healthy,
                    null,
                    null);
                SetSnapshot(after);
            }

            return CompletedTransition(
                operationId,
                CaptureOperationKind.StoragePressure,
                before,
                after);
        }
        finally
        {
            _serial.Release();
        }
    }

    public async Task<CaptureTransitionResult> ReportDegradedAsync(
        CaptureSessionContext expectedContext,
        CaptureFailure failure,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedContext);
        ArgumentNullException.ThrowIfNull(failure);
        var operationId = Guid.NewGuid();
        if (!await EnterSerialAsync(cancellationToken).ConfigureAwait(false))
            return CancelledTransition(operationId, CaptureOperationKind.HealthChanged);

        try
        {
            var before = Snapshot;
            if (before.Status != CaptureLifecycleStatus.Active || before.Context != expectedContext)
            {
                return new CaptureTransitionResult(
                    operationId,
                    CaptureOperationKind.HealthChanged,
                    CaptureCommandDisposition.Ignored,
                    before,
                    before,
                    null);
            }

            var after = before with
            {
                Health = CaptureHealth.Degraded,
                LastFailure = failure,
            };
            SetSnapshot(after);
            return CompletedTransition(operationId, CaptureOperationKind.HealthChanged, before, after);
        }
        finally
        {
            _serial.Release();
        }
    }

    public Task<CaptureSaveResult> SaveAsync(CancellationToken cancellationToken = default) =>
        SaveCoreAsync(expectedContext: null, cancellationToken);

    public Task<CaptureSaveResult> SaveAsync(
        CaptureSessionContext expectedContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expectedContext);
        return SaveCoreAsync(expectedContext, cancellationToken);
    }

    private async Task<CaptureSaveResult> SaveCoreAsync(
        CaptureSessionContext? expectedContext,
        CancellationToken cancellationToken)
    {
        var operationId = Guid.NewGuid();
        var admissionFailure = CurrentAdmissionFailure();
        if (admissionFailure is not null)
        {
            return RejectedSave(operationId, admissionFailure);
        }

        if (!await EnterSerialAsync(cancellationToken).ConfigureAwait(false))
        {
            return RejectedSave(operationId, CancelledFailure());
        }

        try
        {
            admissionFailure = CurrentAdmissionFailure();
            if (admissionFailure is not null)
            {
                return RejectedSave(operationId, admissionFailure);
            }

            var before = Snapshot;
            var context = before.Context;
            if (before.Status != CaptureLifecycleStatus.Active || context is null)
            {
                return RejectedSave(
                    operationId,
                    new CaptureFailure(
                        CaptureFailureKind.InvalidState,
                        "capture.save.not-active",
                        "A replay can only be saved while capture is active."));
            }

            if (expectedContext is not null && context != expectedContext)
            {
                return RejectedSave(
                    operationId,
                    new CaptureFailure(
                        CaptureFailureKind.Superseded,
                        "capture.save.context-superseded",
                        "The capture context changed before the replay save was admitted."));
            }

            SetSnapshot(before with
            {
                Status = CaptureLifecycleStatus.Saving,
                Readiness = CaptureReadiness.Busy,
            });

            var backendResult = await SaveBackendAsync(context, cancellationToken).ConfigureAwait(false);
            if (!backendResult.Succeeded || string.IsNullOrWhiteSpace(backendResult.OutputPath))
            {
                var failure = backendResult.Failure ?? InvalidBackendResponse("save");
                if (failure.Kind == CaptureFailureKind.Unsupported)
                {
                    SetSnapshot(before);
                    return new CaptureSaveResult(
                        operationId,
                        CaptureCommandDisposition.Unsupported,
                        context,
                        null,
                        failure);
                }

                SetSnapshot(before with
                {
                    Health = CaptureHealth.Degraded,
                    LastFailure = failure,
                });
                return new CaptureSaveResult(
                    operationId,
                    CaptureCommandDisposition.Failed,
                    context,
                    backendResult.OutputPath,
                    failure,
                    backendResult.Receipt);
            }

            // Saving a replay proves only the save operation. It must not erase an
            // unrelated active degradation (for example a session-recorder warning).
            SetSnapshot(before);
            return new CaptureSaveResult(
                operationId,
                CaptureCommandDisposition.Completed,
                context,
                backendResult.OutputPath,
                null,
                backendResult.Receipt);
        }
        finally
        {
            _serial.Release();
        }
    }

    public Task<CaptureTransitionResult> PauseAsync(CancellationToken cancellationToken = default) =>
        UnsupportedAsync(CaptureOperationKind.Pause, cancellationToken);

    public Task<CaptureTransitionResult> ResumeAsync(CancellationToken cancellationToken = default) =>
        UnsupportedAsync(CaptureOperationKind.Resume, cancellationToken);

    public Task<CaptureTransitionResult> SplitAsync(CancellationToken cancellationToken = default) =>
        UnsupportedAsync(CaptureOperationKind.Split, cancellationToken);

    public Task<CaptureTransitionResult> ShutdownAsync()
    {
        Interlocked.Exchange(ref _admissionClosed, 1);
        lock (_shutdownSync)
        {
            if (_shutdownTask is null ||
                (_shutdownTask.IsCompleted && Snapshot is
                {
                    Status: CaptureLifecycleStatus.Faulted,
                    Context: not null,
                }))
            {
                _shutdownTask = RunShutdownAsync(Guid.NewGuid());
            }

            return _shutdownTask;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) == 0)
        {
            _ = ShutdownAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposeRequested, 1);
        await ShutdownAsync().ConfigureAwait(false);
    }

    private Task<CaptureTransitionResult> BeginAsync(
        CaptureRequest request,
        CaptureOperationKind operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operationId = Guid.NewGuid();
        var validationFailure = Validate(request);
        if (validationFailure is not null)
        {
            return Task.FromResult(RejectedTransition(operationId, operation, Snapshot, validationFailure));
        }

        var admissionFailure = CurrentAdmissionFailure();
        if (admissionFailure is not null)
        {
            return Task.FromResult(RejectedTransition(
                operationId,
                operation,
                Snapshot,
                admissionFailure));
        }

        ManualReservation? reservation = null;
        if (request.Initiation == CaptureInitiation.Manual)
        {
            reservation = new ManualReservation(request.RequestId);
            Interlocked.Exchange(ref _pendingManualReservation, reservation);
        }
        else if (HasManualProtection)
        {
            return Task.FromResult(RejectedTransition(
                operationId,
                operation,
                Snapshot,
                ManualPriorityFailure()));
        }

        return BeginSerializedAsync(
            request,
            operation,
            operationId,
            reservation,
            cancellationToken);
    }

    private async Task<CaptureTransitionResult> BeginSerializedAsync(
        CaptureRequest request,
        CaptureOperationKind operation,
        Guid operationId,
        ManualReservation? reservation,
        CancellationToken cancellationToken)
    {
        if (!await EnterSerialAsync(cancellationToken).ConfigureAwait(false))
        {
            ReleasePendingManualReservation(reservation);
            return CancelledTransition(operationId, operation);
        }

        try
        {
            var before = Snapshot;
            var admissionFailure = CurrentAdmissionFailure();
            if (admissionFailure is not null)
            {
                ReleasePendingManualReservation(reservation);
                return RejectedTransition(operationId, operation, before, admissionFailure);
            }

            var currentReservation = Volatile.Read(ref _pendingManualReservation);
            if (reservation is not null && !ReferenceEquals(currentReservation, reservation))
            {
                return RejectedTransition(
                    operationId,
                    operation,
                    before,
                    new CaptureFailure(
                        CaptureFailureKind.Superseded,
                        "capture.manual.superseded",
                        "A newer manual capture request superseded this request."));
            }

            if (reservation is null && HasManualProtection)
            {
                return RejectedTransition(operationId, operation, before, ManualPriorityFailure());
            }

            CaptureFailure? preflightFailure;
            try { preflightFailure = _backend.ValidateStart(request); }
            catch (Exception ex) { preflightFailure = UnexpectedBackendFailure("start preflight", ex); }
            if (preflightFailure is not null)
            {
                ReleasePendingManualReservation(reservation);
                return RejectedTransition(operationId, operation, before, preflightFailure);
            }

            if (before.Status == CaptureLifecycleStatus.Active && before.Context is not null)
            {
                SetSnapshot(before with
                {
                    Status = CaptureLifecycleStatus.Stopping,
                    Readiness = CaptureReadiness.Busy,
                    LastFailure = null,
                });

                var stopResult = await StopBackendAsync(before.Context, cancellationToken).ConfigureAwait(false);
                if (!stopResult.Succeeded)
                {
                    var stopFailure = stopResult.Failure ?? InvalidBackendResponse("replacement stop");
                    var faulted = stopResult.CleanupRequired
                        ? before with
                        {
                            Status = CaptureLifecycleStatus.Faulted,
                            Readiness = CaptureReadiness.Available,
                            Health = CaptureHealth.Failed,
                            LastFailure = stopFailure,
                        }
                        : new CaptureLifecycleSnapshot(
                            CaptureLifecycleStatus.Faulted,
                            CaptureReadiness.Available,
                            CaptureHealth.Degraded,
                            null,
                            stopFailure);
                    if (!stopResult.CleanupRequired)
                        ReleaseManualOwner(before.Context);
                    SetSnapshot(faulted);
                    ReleasePendingManualReservation(reservation);
                    return FailedTransition(operationId, operation, before, faulted, stopFailure);
                }

                ReleaseManualOwner(before.Context);
            }
            else if (before.Context is not null)
            {
                ReleasePendingManualReservation(reservation);
                return RejectedTransition(operationId, operation, before, CleanupRequiredFailure());
            }
            else if (before.Status is CaptureLifecycleStatus.Shutdown or
                     CaptureLifecycleStatus.Starting or
                     CaptureLifecycleStatus.Saving or
                     CaptureLifecycleStatus.Stopping)
            {
                var failure = new CaptureFailure(
                    CaptureFailureKind.InvalidState,
                    "capture.start.invalid-state",
                    $"Capture cannot start while the lifecycle is {before.Status}.");
                ReleasePendingManualReservation(reservation);
                return RejectedTransition(operationId, operation, before, failure);
            }

            if (reservation is not null)
            {
                Volatile.Write(ref _manualOwner, reservation);
                ReleasePendingManualReservation(reservation);
            }

            var generation = checked(++_generation);
            var provisionalContext = new CaptureSessionContext(
                Guid.NewGuid(),
                generation,
                request,
                request.Target,
                request.Target);
            SetSnapshot(new CaptureLifecycleSnapshot(
                CaptureLifecycleStatus.Starting,
                CaptureReadiness.Busy,
                CaptureHealth.Healthy,
                null,
                null));

            var startResult = await StartBackendAsync(provisionalContext, cancellationToken).ConfigureAwait(false);
            if (!startResult.Succeeded)
            {
                var failure = startResult.Failure ?? InvalidBackendResponse("start");
                var retainedContext = startResult.CleanupRequired
                    ? provisionalContext with
                    {
                        ActualTarget = startResult.ActualTarget ?? provisionalContext.ActualTarget,
                    }
                    : null;
                if (startResult.CleanupRequired && startResult.ActualTarget is null)
                {
                    failure = InvalidBackendResponse("failed start cleanup ownership");
                }
                var faulted = new CaptureLifecycleSnapshot(
                    CaptureLifecycleStatus.Faulted,
                    CaptureReadiness.Available,
                    startResult.CleanupRequired ? CaptureHealth.Failed : CaptureHealth.Degraded,
                    retainedContext,
                    failure);
                if (!startResult.CleanupRequired)
                    ReleaseManualOwner(provisionalContext);
                SetSnapshot(faulted);
                return FailedTransition(operationId, operation, before, faulted, failure);
            }

            if (startResult.ActualTarget is null)
            {
                var failure = InvalidBackendResponse("start");
                var faulted = new CaptureLifecycleSnapshot(
                    CaptureLifecycleStatus.Faulted,
                    CaptureReadiness.Available,
                    CaptureHealth.Degraded,
                    null,
                    failure);
                ReleaseManualOwner(provisionalContext);
                SetSnapshot(faulted);
                return FailedTransition(operationId, operation, before, faulted, failure);
            }

            var actualTarget = startResult.ActualTarget;
            var boundTargetChanged = request.StartupExpectation is not null && request.Target != actualTarget;
            if (boundTargetChanged || !IsPermittedActualTarget(request.Target, actualTarget))
            {
                var invalidFallback = new CaptureFailure(
                    CaptureFailureKind.Backend,
                    "capture.start.unapproved-target",
                    "The backend started an unapproved physical target; implicit display fallback is not allowed.");
                var startedContext = provisionalContext with
                {
                    ActualTarget = actualTarget,
                    AdmittedDevices = startResult.AdmittedDevices,
                };
                var cleanup = await StopBackendAsync(startedContext, CancellationToken.None).ConfigureAwait(false);
                var failure = cleanup.Succeeded
                    ? invalidFallback
                    : invalidFallback.WithCleanupFailure(
                        cleanup.Failure ?? InvalidBackendResponse("unapproved-target cleanup"));
                var faulted = new CaptureLifecycleSnapshot(
                    CaptureLifecycleStatus.Faulted,
                    CaptureReadiness.Available,
                    !cleanup.Succeeded && cleanup.CleanupRequired
                        ? CaptureHealth.Failed
                        : CaptureHealth.Degraded,
                    !cleanup.Succeeded && cleanup.CleanupRequired ? startedContext : null,
                    failure);
                if (cleanup.Succeeded || !cleanup.CleanupRequired)
                    ReleaseManualOwner(startedContext);
                SetSnapshot(faulted);
                return FailedTransition(operationId, operation, before, faulted, failure);
            }

            var context = provisionalContext with
            {
                ActualTarget = actualTarget,
                AdmittedDevices = startResult.AdmittedDevices,
            };
            var after = new CaptureLifecycleSnapshot(
                CaptureLifecycleStatus.Active,
                CaptureReadiness.Available,
                CaptureHealth.Healthy,
                context,
                null);
            SetSnapshot(after);
            return CompletedTransition(operationId, operation, before, after);
        }
        finally
        {
            _serial.Release();
        }
    }

    private Task<CaptureTransitionResult> UnsupportedAsync(
        CaptureOperationKind operation,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        var snapshot = Snapshot;
        var failure = new CaptureFailure(
            CaptureFailureKind.Unsupported,
            $"capture.{operation.ToString().ToLowerInvariant()}.unsupported",
            $"{operation} is not supported by this lifecycle backend contract.");
        return Task.FromResult(new CaptureTransitionResult(
            Guid.NewGuid(),
            operation,
            CaptureCommandDisposition.Unsupported,
            snapshot,
            snapshot,
            failure));
    }

    private async Task<CaptureTransitionResult> RunShutdownAsync(Guid operationId)
    {
        await _serial.WaitAsync().ConfigureAwait(false);
        try
        {
            var before = Snapshot;
            if (before.Status == CaptureLifecycleStatus.Shutdown)
            {
                return CompletedTransition(operationId, CaptureOperationKind.Shutdown, before, before);
            }

            CaptureFailure? failure = null;
            var cleanupRequired = false;
            if (before.Context is not null)
            {
                SetSnapshot(before with
                {
                    Status = CaptureLifecycleStatus.Stopping,
                    Readiness = CaptureReadiness.Busy,
                    LastFailure = null,
                });
                var stopResult = await StopBackendAsync(before.Context, CancellationToken.None).ConfigureAwait(false);
                if (!stopResult.Succeeded)
                {
                    failure = stopResult.Failure ?? InvalidBackendResponse("shutdown stop");
                    cleanupRequired = stopResult.CleanupRequired;
                }
            }

            Interlocked.Exchange(ref _manualOwner, null);
            Interlocked.Exchange(ref _pendingManualReservation, null);
            var after = new CaptureLifecycleSnapshot(
                failure is null || !cleanupRequired
                    ? CaptureLifecycleStatus.Shutdown
                    : CaptureLifecycleStatus.Faulted,
                CaptureReadiness.Closed,
                failure is null
                    ? CaptureHealth.Healthy
                    : cleanupRequired ? CaptureHealth.Failed : CaptureHealth.Degraded,
                cleanupRequired ? before.Context : null,
                failure);
            SetSnapshot(after);
            // A stop warning after all ownership was released must not trap the app in a
            // non-exitable shutdown loop. Preserve it in the terminal snapshot, but complete.
            return failure is null || !cleanupRequired
                ? CompletedTransition(operationId, CaptureOperationKind.Shutdown, before, after)
                : FailedTransition(operationId, CaptureOperationKind.Shutdown, before, after, failure);
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<bool> EnterSerialAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _serial.WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task<CaptureBackendStartResult> StartBackendAsync(
        CaptureSessionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _backend.StartAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CaptureBackendStartResult.Failed(CancelledFailure());
        }
        catch (Exception ex)
        {
            return CaptureBackendStartResult.Failed(UnexpectedBackendFailure("start", ex));
        }
    }

    private async Task<CaptureBackendCommandResult> StopBackendAsync(
        CaptureSessionContext context,
        CancellationToken cancellationToken)
    {
        CaptureBackendCommandResult result;
        try
        {
            result = await _backend.StopAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result = CaptureBackendCommandResult.Failed(CancelledFailure());
        }
        catch (Exception ex)
        {
            result = CaptureBackendCommandResult.Failed(UnexpectedBackendFailure("stop", ex));
        }

        if (result.Succeeded || !result.CleanupRequired)
            NotifyLivenessCaptureReleased(context);
        return result;
    }

    private void NotifyLivenessCaptureReleased(CaptureSessionContext context)
    {
        if (_livenessClaims is null)
            return;

        try
        {
            _livenessClaims.CompleteCapture(CaptureLivenessContext.From(context));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError(
                $"Capture liveness cleanup notification failed: {ex.Message}");
        }
    }

    private async Task<CaptureBackendSaveResult> SaveBackendAsync(
        CaptureSessionContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _backend.SaveAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return CaptureBackendSaveResult.Failed(CancelledFailure());
        }
        catch (Exception ex)
        {
            return CaptureBackendSaveResult.Failed(UnexpectedBackendFailure("save", ex));
        }
    }

    private static CaptureFailure? Validate(CaptureRequest request)
    {
        if (request.RequestId == Guid.Empty)
        {
            return InvalidRequest("capture.request.id-empty", "Capture request ID must not be empty.");
        }

        return request.Target switch
        {
            GameCaptureTarget game when game.ProcessId <= 0 ||
                                            string.IsNullOrWhiteSpace(game.GameId) ||
                                            string.IsNullOrWhiteSpace(game.ExecutableName) ||
                                            game.ProcessStartedAt is null =>
                InvalidRequest(
                    "capture.target.game-invalid",
                    "Game targets require an ID, executable, process ID, and process start time."),
            WindowCaptureTarget window when window.ProcessId <= 0 || window.WindowHandle == IntPtr.Zero =>
                InvalidRequest("capture.target.window-invalid", "Window targets require an exact process ID and window handle."),
            DisplayCaptureTarget display when string.IsNullOrWhiteSpace(display.StableDisplayId) =>
                InvalidRequest("capture.target.display-invalid", "Display targets require a stable display ID."),
            RegionCaptureTarget region when string.IsNullOrWhiteSpace(region.OwningDisplayId) ||
                                            region.PhysicalBounds.Width <= 0 ||
                                            region.PhysicalBounds.Height <= 0 ||
                                            !RegionCaptureGeometry.TryGetDisplayLocalBounds(region, out _) =>
                InvalidRequest("capture.target.region-invalid", "Region targets require an owning display and positive physical bounds."),
            null => InvalidRequest("capture.target.missing", "A physical capture target is required."),
            _ => null,
        };
    }

    private static bool IsPermittedActualTarget(CaptureTarget requested, CaptureTarget actual) =>
        requested switch
        {
            GameCaptureTarget game => actual switch
            {
                GameCaptureTarget actualGame => HasExactIdentity(game, actualGame),
                WindowCaptureTarget actualWindow => game.ProcessId == actualWindow.ProcessId,
                _ => false,
            },
            _ => HasExactIdentity(requested, actual),
        };

    private static bool HasExactIdentity(CaptureTarget left, CaptureTarget right) =>
        (left, right) switch
        {
            (GameCaptureTarget a, GameCaptureTarget b) =>
                a.GameId == b.GameId &&
                a.ProcessId == b.ProcessId &&
                a.ProcessStartedAt is not null &&
                b.ProcessStartedAt is not null &&
                a.ProcessStartedAt == b.ProcessStartedAt,
            (WindowCaptureTarget a, WindowCaptureTarget b) =>
                a.ProcessId == b.ProcessId && a.WindowHandle == b.WindowHandle,
            (DisplayCaptureTarget a, DisplayCaptureTarget b) =>
                StringComparer.Ordinal.Equals(a.StableDisplayId, b.StableDisplayId),
            (RegionCaptureTarget a, RegionCaptureTarget b) =>
                StringComparer.Ordinal.Equals(a.OwningDisplayId, b.OwningDisplayId) &&
                a.PhysicalBounds == b.PhysicalBounds &&
                a.OwningDisplayBounds == b.OwningDisplayBounds,
            _ => false,
        };

    private static CaptureFailure DeviceLossFailure(CaptureDeviceLossObservation observation)
    {
        var role = observation.Role switch
        {
            CaptureDeviceRole.DesktopAudio => (Code: "desktop-audio", Name: "desktop audio device"),
            CaptureDeviceRole.Microphone => (Code: "microphone", Name: "microphone"),
            CaptureDeviceRole.Webcam => (Code: "webcam", Name: "webcam"),
            _ => throw new ArgumentOutOfRangeException(nameof(observation)),
        };
        var reason = observation.Reason switch
        {
            CaptureDeviceClosedReason.ExactSelectionUnavailable => (
                Kind: CaptureFailureKind.SourceLost,
                Code: "selection-unavailable",
                Message: $"The selected {role.Name} is no longer present in its strict physical-device catalog."),
            CaptureDeviceClosedReason.CatalogUnavailable => (
                Kind: CaptureFailureKind.Backend,
                Code: "catalog-unavailable",
                Message: $"WatchDog could not read the active {role.Name} catalog, so its identity is unobservable."),
            CaptureDeviceClosedReason.CatalogInvalid => (
                Kind: CaptureFailureKind.Backend,
                Code: "catalog-invalid",
                Message: $"The active {role.Name} catalog returned invalid identity metadata, so continuity is unobservable."),
            CaptureDeviceClosedReason.DefaultUnavailable => (
                Kind: CaptureFailureKind.SourceLost,
                Code: "default-unavailable",
                Message: $"The Windows default {role.Name} metadata is unavailable, so WatchDog could not verify capture continuity."),
            CaptureDeviceClosedReason.DefaultChanged => (
                Kind: CaptureFailureKind.SourceLost,
                Code: "default-changed",
                Message: $"Windows changed the default {role.Name}. Recording stopped because WatchDog could not " +
                         "verify that capture stayed on the same device. Start a new recording to use the current default."),
            _ => throw new ArgumentOutOfRangeException(nameof(observation)),
        };
        return new CaptureFailure(
            reason.Kind,
            $"capture.device.{role.Code}.active.{reason.Code}",
            reason.Message,
            IsRetryable: true);
    }

    private static CaptureFailure LivenessFailure(CaptureLivenessFailureCandidate candidate)
    {
        var owner = candidate.Owner switch
        {
            CaptureLivenessPipelineOwner.Replay => "replay",
            CaptureLivenessPipelineOwner.SessionSegment => "session-segment",
            CaptureLivenessPipelineOwner.WebcamIntermediate => "webcam-intermediate",
            CaptureLivenessPipelineOwner.AdmittedDesktopAudio => "desktop-audio",
            CaptureLivenessPipelineOwner.AdmittedMicrophone => "microphone",
            _ => throw new ArgumentOutOfRangeException(nameof(candidate)),
        };
        var reason = candidate.Evidence.Reason switch
        {
            CaptureLivenessReason.StartupTimeout => "startup-timeout",
            CaptureLivenessReason.StallTimeout => "stalled",
            CaptureLivenessReason.TelemetryMissing => "telemetry-missing",
            CaptureLivenessReason.TelemetryFailed => "telemetry-failed",
            CaptureLivenessReason.TelemetryTimeout => "telemetry-timeout",
            CaptureLivenessReason.ProviderExecutionTimeout => "provider-execution-timeout",
            CaptureLivenessReason.CounterDecreased => "counter-decreased",
            _ => "unverified",
        };
        return new CaptureFailure(
            CaptureFailureKind.Liveness,
            $"capture.liveness.{owner}.{reason}",
            $"The {owner} progress counter became {reason}. Capture stopped because counter " +
            "progress could not be verified. This does not establish source or device binding, " +
            "visual content or change, audible audio, or audio/video synchronization.",
            IsRetryable: true);
    }

    private static bool MatchesLivenessAdmission(
        CaptureSessionContext context,
        CaptureLivenessFailureCandidate candidate)
    {
        var role = candidate.Owner switch
        {
            CaptureLivenessPipelineOwner.AdmittedDesktopAudio => CaptureDeviceRole.DesktopAudio,
            CaptureLivenessPipelineOwner.AdmittedMicrophone => CaptureDeviceRole.Microphone,
            _ => (CaptureDeviceRole?)null,
        };
        if (role is null)
            return candidate.AdmittedAudioDevice is null;

        return candidate.AdmittedAudioDevice is not null &&
               candidate.AdmittedAudioDevice.Role == role &&
               context.AdmittedDevices.Entries.SingleOrDefault(device => device.Role == role) ==
               candidate.AdmittedAudioDevice;
    }

    private static bool IsFailClosedLiveness(CaptureLivenessStatus status) =>
        status is CaptureLivenessStatus.StartupTimedOut or
            CaptureLivenessStatus.Stalled or
            CaptureLivenessStatus.Unobservable or
            CaptureLivenessStatus.InvalidTelemetry;

    private void ReleasePendingManualReservation(ManualReservation? reservation)
    {
        if (reservation is not null)
        {
            Interlocked.CompareExchange(ref _pendingManualReservation, null, reservation);
        }
    }

    private void ReleaseManualOwner(CaptureSessionContext? context)
    {
        var owner = Volatile.Read(ref _manualOwner);
        if (owner is null)
            return;

        if (context is null || owner.RequestId == context.Request.RequestId)
            Interlocked.CompareExchange(ref _manualOwner, null, owner);
    }

    private void SetSnapshot(CaptureLifecycleSnapshot snapshot)
    {
        Volatile.Write(ref _snapshot, snapshot);
        var subscribers = SnapshotChanged;
        if (subscribers is null)
            return;

        foreach (Action<CaptureLifecycleSnapshot> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(snapshot); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError(
                    $"Capture lifecycle snapshot subscriber failed: {ex.Message}");
            }
        }
    }

    private CaptureSaveResult RejectedSave(Guid operationId, CaptureFailure failure) => new(
        operationId,
        CaptureCommandDisposition.Rejected,
        Snapshot.Context,
        null,
        failure);

    private CaptureTransitionResult CancelledTransition(Guid operationId, CaptureOperationKind operation)
    {
        var snapshot = Snapshot;
        return FailedTransition(operationId, operation, snapshot, snapshot, CancelledFailure());
    }

    private static CaptureTransitionResult CompletedTransition(
        Guid operationId,
        CaptureOperationKind operation,
        CaptureLifecycleSnapshot before,
        CaptureLifecycleSnapshot after) => new(
            operationId,
            operation,
            CaptureCommandDisposition.Completed,
            before,
            after,
            null);

    private static CaptureTransitionResult RejectedTransition(
        Guid operationId,
        CaptureOperationKind operation,
        CaptureLifecycleSnapshot snapshot,
        CaptureFailure failure) => new(
            operationId,
            operation,
            CaptureCommandDisposition.Rejected,
            snapshot,
            snapshot,
            failure);

    private static CaptureTransitionResult IgnoredTransition(
        Guid operationId,
        CaptureOperationKind operation,
        CaptureLifecycleSnapshot snapshot) => new(
            operationId,
            operation,
            CaptureCommandDisposition.Ignored,
            snapshot,
            snapshot,
            null);

    private static CaptureTransitionResult FailedTransition(
        Guid operationId,
        CaptureOperationKind operation,
        CaptureLifecycleSnapshot before,
        CaptureLifecycleSnapshot after,
        CaptureFailure failure) => new(
            operationId,
            operation,
            CaptureCommandDisposition.Failed,
            before,
            after,
            failure);

    private static CaptureFailure InvalidRequest(string code, string message) => new(
        CaptureFailureKind.InvalidRequest,
        code,
        message);

    private static CaptureFailure AdmissionClosedFailure() => new(
        CaptureFailureKind.AdmissionClosed,
        "capture.admission.closed",
        "Capture lifecycle admission is closed for shutdown.");

    private CaptureFailure? CurrentAdmissionFailure()
    {
        if (IsAdmissionClosed)
            return AdmissionClosedFailure();

        if (!IsStorageAdmissionBlocked)
            return null;

        return Volatile.Read(ref _storageFailure) ?? StoragePressureFailure(
            "The recording volume is below its safety reserve.");
    }

    private static CaptureFailure StoragePressureFailure(string reason) => new(
        CaptureFailureKind.Storage,
        "capture.storage.reserve-low",
        reason,
        true);

    private static CaptureFailure ManualPriorityFailure() => new(
        CaptureFailureKind.ManualPriority,
        "capture.manual.reserved",
        "Automatic and configured capture cannot replace reserved manual ownership.");

    private static CaptureFailure CleanupRequiredFailure() => new(
        CaptureFailureKind.InvalidState,
        "capture.start.cleanup-required",
        "Capture cannot start until the retained backend context is stopped explicitly.");

    private static CaptureFailure CancelledFailure() => new(
        CaptureFailureKind.Cancelled,
        "capture.operation.cancelled",
        "The capture operation was cancelled.",
        true);

    private static CaptureFailure InvalidBackendResponse(string operation) => new(
        CaptureFailureKind.Backend,
        $"capture.backend.{operation}.invalid-result",
        $"The backend returned an invalid {operation} result.");

    private static CaptureFailure UnexpectedBackendFailure(string operation, Exception exception) => new(
        CaptureFailureKind.Unexpected,
        $"capture.backend.{operation}.exception",
        $"The backend threw during {operation}: {exception.Message}",
        true);

    private sealed record ManualReservation(Guid RequestId);
}
