using System.Collections.ObjectModel;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal enum ObserverResourceKind { Snapshot, Marker }

internal enum ObserverResourceState { Absent, Acquiring, Acquired, Releasing, Released, UnknownCustody }

internal enum ObserverOperationKind { AcquireSnapshot, AcquireMarker, Walk, ReleaseMarker, ReleaseSnapshot }

internal sealed record ObserverRunBinding(
    Guid RunId,
    Guid SessionId,
    InventoryAnchor Anchor,
    int Attempt,
    InventoryProcessIdentity Collector,
    InventoryProcessIdentity ResourceOwner,
    InventoryProcessIdentity CommandIssuer,
    InventoryProcessIdentity Target,
    InventoryProcessIdentity ControllerProcess,
    InventoryProcessIdentity OwnerProcess,
    InventoryProcessIdentity WriterProcess);

internal sealed record ObserverEventIdentity(
    Guid RunId,
    Guid SessionId,
    InventoryAnchor Anchor,
    int Attempt,
    long Sequence,
    long MonotonicTicks,
    Guid EventId,
    Guid OperationId,
    InventoryProcessIdentity ActingOwner,
    InventoryProcessIdentity CommandIssuer);

internal sealed record ObserverLivenessEvidence(
    ObserverEventIdentity Event,
    InventoryProcessIdentity Collector,
    InventoryProcessIdentity CommandIssuer,
    InventoryProcessIdentity Target,
    bool CollectorLive,
    bool CommandIssuerLive,
    bool TargetLive,
    ObserverRoleLiveness Controller,
    ObserverRoleLiveness Owner,
    ObserverRoleLiveness Writer);

internal sealed record ObserverRoleLiveness(InventoryProcessIdentity Identity, bool Live, long ObservedMonotonicTicks);

internal sealed record ObserverDescriptor(
    ObserverResourceKind Kind,
    long Coordinate,
    InventoryProcessIdentity Owner,
    Guid AcquisitionOperationId,
    bool CleanupOnly);

internal sealed record ObserverUnknownReturn(ObserverResourceKind Kind, long Coordinate, Guid OperationId, string Reason,
    uint? ApiStatus = null);

internal sealed record ObserverEventRecord(
    long Sequence, long MonotonicTicks, Guid EventId, Guid OperationId, string Action, bool Accepted, string Reason,
    uint? ApiStatus = null, ObserverResourceKind? ResourceKind = null, long? Coordinate = null,
    Guid? AcquisitionOperationId = null);

internal sealed record ObserverLifecycleSnapshot(
    ObserverRunBinding Binding,
    ObserverResourceState SnapshotState,
    ObserverResourceState MarkerState,
    ObserverDescriptor? Snapshot,
    ObserverDescriptor? Marker,
    bool WalkExhausted,
    bool TimeoutObserved,
    bool OperationFailed,
    bool InvalidTransitionObserved,
    bool InvalidWalkTerminalObserved,
    bool UnknownReturnOverflow,
    Guid? InFlightOperationId,
    ObserverOperationKind? InFlightOperationKind,
    IReadOnlyList<ObserverUnknownReturn> UnknownReturns,
    IReadOnlyList<ObserverEventRecord> Events,
    IReadOnlyList<string> Failures)
{
    internal bool NativeCapturePerformed => false;
    internal bool NativeCaptureProven => false;
    internal bool NativeAuthorityProven => false;
    internal bool AdmissionGranted => false;
}

internal sealed record ObserverTransitionResult(bool Accepted, string Reason, ObserverLifecycleSnapshot State);

internal sealed record ObserverGateResult(
    bool CanTriggerSyntheticTransition,
    IReadOnlyList<string> Blockers,
    ObserverLifecycleSnapshot State)
{
    internal bool NativeCaptureProven => false;
    internal bool NativeAuthorityProven => false;
    internal bool AdmissionGranted => false;
}

// A single caller supplies serialized synthetic events. This is not a CLR-thread synchronization primitive.
internal sealed class ObserverResourceLifecycle
{
    internal const uint Success = 0;
    internal const uint NoMoreItems = 259;
    internal const int MaximumOperations = 128;
    internal const int MaximumUnknownReturns = 8;

    private readonly ObserverRunBinding _binding;
    private readonly int _maximumEvents;
    private readonly HashSet<Guid> _eventIds = new();
    private readonly HashSet<Guid> _startedOperationIds = new();
    private readonly List<ObserverEventRecord> _events = new();
    private readonly List<ObserverUnknownReturn> _unknownReturns = new();
    private readonly List<string> _failures = new();
    private long _nextSequence = 1;
    private long _lastMonotonicTicks;
    private long _lastReleaseSequence;
    private long _lastReleaseMonotonicTicks;
    private ObserverResourceState _snapshotState;
    private ObserverResourceState _markerState;
    private ObserverDescriptor? _snapshot;
    private ObserverDescriptor? _marker;
    private Guid? _inFlightOperationId;
    private ObserverOperationKind? _inFlightOperationKind;
    private bool _walkExhausted;
    private bool _timeoutObserved;
    private bool _operationFailed;
    private bool _invalidTransitionObserved;
    private bool _invalidWalkTerminalObserved;
    private bool _unknownReturnOverflow;
    private bool _livenessConsumed;

    internal ObserverResourceLifecycle(ObserverRunBinding binding, int maximumEvents = MaximumOperations)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (maximumEvents is < 1 or > MaximumOperations)
            throw new ArgumentOutOfRangeException(nameof(maximumEvents));
        ValidateBinding(binding);
        _binding = CopyBinding(binding);
        _maximumEvents = maximumEvents;
    }

    internal ObserverTransitionResult BeginAcquisition(ObserverResourceKind kind, ObserverEventIdentity identity)
    {
        if (!ValidKind(kind)) return RejectInvalid("resource-kind-invalid");
        var operationKind = AcquireKind(kind);
        if (!TryStart(identity, operationKind, $"begin-{kind}", out var failure)) return Reject(failure);
        if (_inFlightOperationId is not null) return RejectRecord(identity, $"begin-{kind}", "operation-already-in-flight");
        if (Aborted) return RejectRecord(identity, $"begin-{kind}", "scenario-already-aborted");

        if (kind == ObserverResourceKind.Marker)
        {
            if (_markerState != ObserverResourceState.Absent || _snapshotState != ObserverResourceState.Absent)
                return RejectRecord(identity, "begin-Marker", "marker-acquisition-not-one-use");
            _markerState = ObserverResourceState.Acquiring;
        }
        else
        {
            if (_markerState != ObserverResourceState.Acquired || _snapshotState != ObserverResourceState.Absent)
                return RejectRecord(identity, "begin-Snapshot", "snapshot-requires-acquired-marker");
            _snapshotState = ObserverResourceState.Acquiring;
        }

        SetInflight(identity.OperationId, operationKind);
        Record(identity, $"begin-{kind}", true, "accepted");
        return Accept();
    }

    internal ObserverTransitionResult CompleteAcquisition(
        ObserverResourceKind kind, ObserverEventIdentity identity, uint apiStatus, long returnedCoordinate)
    {
        if (!ValidKind(kind))
        {
            PreserveReturnedCoordinate(kind, returnedCoordinate, identity?.OperationId ?? Guid.Empty, "resource-kind-invalid", apiStatus);
            Record(identity, $"complete-{kind}", false, "resource-kind-invalid", apiStatus, kind, returnedCoordinate, identity?.OperationId);
            return RejectInvalid("resource-kind-invalid");
        }

        if (!TryComplete(identity, AcquireKind(kind), $"complete-{kind}", out var failure))
        {
            PreserveReturnedCoordinate(kind, returnedCoordinate, identity?.OperationId ?? Guid.Empty, failure, apiStatus);
            Record(identity, $"complete-{kind}", false, failure, apiStatus, kind, returnedCoordinate, identity?.OperationId);
            return Reject(failure);
        }

        ClearInflight();
        var cleanupOnly = Aborted;
        if (apiStatus == Success && ValidCoordinate(returnedCoordinate))
        {
            if ((_snapshot?.Coordinate == returnedCoordinate) || (_marker?.Coordinate == returnedCoordinate))
            {
                PreserveReturnedCoordinate(kind, returnedCoordinate, identity.OperationId, "descriptor-coordinate-alias", apiStatus);
                Invalidate("descriptor-coordinate-alias");
                Record(identity, $"complete-{kind}", false, "descriptor-coordinate-alias", apiStatus, kind, returnedCoordinate, identity.OperationId);
                return Reject("descriptor-coordinate-alias");
            }

            var descriptor = new ObserverDescriptor(kind, returnedCoordinate, CopyIdentity(_binding.ResourceOwner), identity.OperationId, cleanupOnly);
            if (kind == ObserverResourceKind.Snapshot) { _snapshot = descriptor; _snapshotState = ObserverResourceState.Acquired; }
            else { _marker = descriptor; _markerState = ObserverResourceState.Acquired; }
            Record(identity, $"complete-{kind}", true, cleanupOnly ? "late-cleanup-only" : "accepted", apiStatus, kind, returnedCoordinate, identity.OperationId);
            return Accept(cleanupOnly ? "late-cleanup-only" : "accepted");
        }

        _operationFailed = true;
        if (apiStatus != Success && ValidCoordinate(returnedCoordinate))
        {
            PreserveReturnedCoordinate(kind, returnedCoordinate, identity.OperationId, "error-returned-coordinate", apiStatus);
            Invalidate("error-returned-coordinate-unknown-custody");
            Record(identity, $"complete-{kind}", false, "error-returned-coordinate-unknown-custody", apiStatus, kind, returnedCoordinate, identity.OperationId);
            return Reject("error-returned-coordinate-unknown-custody");
        }

        if (apiStatus == Success)
        {
            SetState(kind, ObserverResourceState.UnknownCustody);
            Invalidate("success-with-invalid-coordinate");
            Record(identity, $"complete-{kind}", false, "success-with-invalid-coordinate", apiStatus, kind, returnedCoordinate, identity.OperationId);
            return Reject("success-with-invalid-coordinate");
        }

        SetState(kind, ObserverResourceState.Absent);
        Record(identity, $"complete-{kind}", false, "acquisition-failed-without-resource", apiStatus, kind, returnedCoordinate, identity.OperationId);
        return Reject("acquisition-failed-without-resource");
    }

    internal ObserverTransitionResult BeginWalk(ObserverEventIdentity identity)
    {
        if (!TryStart(identity, ObserverOperationKind.Walk, "begin-walk", out var failure)) return Reject(failure);
        if (_inFlightOperationId is not null) return RejectRecord(identity, "begin-walk", "operation-already-in-flight");
        if (_snapshotState != ObserverResourceState.Acquired || _markerState != ObserverResourceState.Acquired || _walkExhausted || Aborted)
            return RejectRecord(identity, "begin-walk", "walk-preconditions-failed");
        SetInflight(identity.OperationId, ObserverOperationKind.Walk);
        Record(identity, "begin-walk", true, "accepted");
        return Accept();
    }

    internal ObserverTransitionResult CompleteWalk(ObserverEventIdentity identity, uint apiStatus)
    {
        if (!TryComplete(identity, ObserverOperationKind.Walk, "complete-walk", out var failure))
        {
            Record(identity, "complete-walk", false, failure, apiStatus);
            return Reject(failure);
        }
        ClearInflight();
        if (apiStatus == Success)
        {
            Record(identity, "complete-walk", true, Aborted ? "late-row-cleanup-only" : "row-returned", apiStatus);
            return Accept(Aborted ? "late-row-cleanup-only" : "row-returned");
        }
        if (apiStatus == NoMoreItems)
        {
            _walkExhausted = true;
            Record(identity, "complete-walk", true, Aborted ? "late-terminal-cleanup-only" : "exhausted", apiStatus);
            return Accept(Aborted ? "late-terminal-cleanup-only" : "exhausted");
        }
        _operationFailed = true;
        _invalidWalkTerminalObserved = true;
        Record(identity, "complete-walk", false, "invalid-terminal-status", apiStatus);
        return Reject("invalid-terminal-status");
    }

    internal ObserverTransitionResult BeginRelease(ObserverResourceKind kind, ObserverEventIdentity identity, ObserverDescriptor descriptor)
    {
        if (!ValidKind(kind)) return RejectInvalid("resource-kind-invalid");
        var operationKind = ReleaseKind(kind);
        if (!TryStart(identity, operationKind, $"begin-release-{kind}", out var failure)) return Reject(failure);
        if (_inFlightOperationId is not null) return RejectRecord(identity, $"begin-release-{kind}", "operation-already-in-flight");
        if (!_walkExhausted && !Aborted) return RejectRecord(identity, $"begin-release-{kind}", "release-before-terminal-walk");
        if (State(kind) != ObserverResourceState.Acquired)
            return RejectRecord(identity, $"begin-release-{kind}", State(kind) == ObserverResourceState.Released ? "successful-release-replayed" : "resource-not-acquired");
        if (!ExactDescriptor(kind, descriptor))
            return RejectRecord(identity, $"begin-release-{kind}", "release-descriptor-mismatch");
        if (kind == ObserverResourceKind.Snapshot && _markerState != ObserverResourceState.Released)
            return RejectRecord(identity, "begin-release-Snapshot", "marker-must-be-released-first");
        SetState(kind, ObserverResourceState.Releasing);
        SetInflight(identity.OperationId, operationKind);
        Record(identity, $"begin-release-{kind}", true, "accepted", resourceKind: kind,
            coordinate: descriptor.Coordinate, acquisitionOperationId: descriptor.AcquisitionOperationId);
        return Accept();
    }

    internal ObserverTransitionResult CompleteRelease(ObserverResourceKind kind, ObserverEventIdentity identity, ObserverDescriptor descriptor, uint apiStatus)
    {
        if (!ValidKind(kind))
        {
            RecordRelease(identity, kind, descriptor, apiStatus, false, "resource-kind-invalid");
            return RejectInvalid("resource-kind-invalid");
        }
        if (!TryComplete(identity, ReleaseKind(kind), $"complete-release-{kind}", out var failure))
        {
            RecordRelease(identity, kind, descriptor, apiStatus, false, failure);
            return Reject(failure);
        }
        ClearInflight();
        if (!ExactDescriptor(kind, descriptor))
        {
            SetState(kind, ObserverResourceState.UnknownCustody);
            Invalidate("release-descriptor-mismatch");
            RecordRelease(identity, kind, descriptor, apiStatus, false, "release-descriptor-mismatch");
            return Reject("release-descriptor-mismatch");
        }
        if (apiStatus != Success)
        {
            _operationFailed = true;
            SetState(kind, ObserverResourceState.UnknownCustody);
            RecordRelease(identity, kind, descriptor, apiStatus, false, "release-failed-unknown-custody");
            return Reject("release-failed-unknown-custody");
        }
        SetState(kind, ObserverResourceState.Released);
        _lastReleaseSequence = identity.Sequence;
        _lastReleaseMonotonicTicks = identity.MonotonicTicks;
        RecordRelease(identity, kind, descriptor, apiStatus, true, Aborted ? "late-release-cleanup-only" : "released");
        return Accept(Aborted ? "late-release-cleanup-only" : "released");
    }

    internal ObserverTransitionResult RecordTimeout(ObserverEventIdentity identity)
    {
        if (!TryEvent(identity, "timeout", allowKnownOperation: true, out var failure)) return Reject(failure);
        if (_inFlightOperationId != identity.OperationId) return RejectRecord(identity, "timeout", "timeout-does-not-match-inflight-operation");
        _timeoutObserved = true;
        _operationFailed = true;
        Record(identity, "timeout", true, "sticky-scenario-abort");
        return Accept("sticky-scenario-abort");
    }

    internal ObserverGateResult EvaluateSyntheticTransition(ObserverLivenessEvidence evidence)
    {
        var blockers = new List<string>();
        if (evidence is null || evidence.Event is null)
        {
            blockers.Add("liveness-evidence-invalid");
            Invalidate("liveness-evidence-invalid");
            return new ObserverGateResult(false, blockers.AsReadOnly(), Snapshot());
        }

        if (_livenessConsumed) blockers.Add("liveness-evidence-already-consumed");
        if (!TryEvent(evidence.Event, "liveness-gate", allowKnownOperation: false, out var eventFailure)) blockers.Add(eventFailure);
        else _livenessConsumed = true;
        if (!SameIdentity(evidence.Collector, _binding.Collector) ||
            !SameIdentity(evidence.CommandIssuer, _binding.CommandIssuer) ||
            !SameIdentity(evidence.Target, _binding.Target)) blockers.Add("bound-liveness-identity-failed");
        if (!evidence.CollectorLive || !evidence.CommandIssuerLive || !evidence.TargetLive) blockers.Add("bound-role-not-live");
        CheckRoleLiveness(evidence.Controller, _binding.ControllerProcess, evidence.Event, blockers);
        CheckRoleLiveness(evidence.Owner, _binding.OwnerProcess, evidence.Event, blockers);
        CheckRoleLiveness(evidence.Writer, _binding.WriterProcess, evidence.Event, blockers);
        if (evidence.Event.Sequence <= _lastReleaseSequence || evidence.Event.MonotonicTicks <= _lastReleaseMonotonicTicks)
            blockers.Add("liveness-not-after-final-release");
        if (_inFlightOperationId is not null) blockers.Add("operation-in-flight");
        if (_snapshotState != ObserverResourceState.Released || _markerState != ObserverResourceState.Released) blockers.Add("resources-not-released");
        if (_unknownReturns.Count > 0 || _unknownReturnOverflow ||
            _snapshotState == ObserverResourceState.UnknownCustody || _markerState == ObserverResourceState.UnknownCustody)
            blockers.Add("unknown-resource-custody");
        if (!_walkExhausted) blockers.Add("walk-not-exhausted");
        if (Aborted || _invalidWalkTerminalObserved) blockers.Add("scenario-aborted-or-invalid");
        if (blockers.Count == 0) Record(evidence.Event, "liveness-gate", true, "synthetic-transition-eligible");
        return new ObserverGateResult(blockers.Count == 0, blockers.AsReadOnly(), Snapshot());
    }

    internal ObserverLifecycleSnapshot Snapshot() => new(
        CopyBinding(_binding), _snapshotState, _markerState,
        _snapshot is null ? null : _snapshot with { Owner = CopyIdentity(_snapshot.Owner) },
        _marker is null ? null : _marker with { Owner = CopyIdentity(_marker.Owner) },
        _walkExhausted, _timeoutObserved, _operationFailed, _invalidTransitionObserved,
        _invalidWalkTerminalObserved, _unknownReturnOverflow, _inFlightOperationId, _inFlightOperationKind,
        new ReadOnlyCollection<ObserverUnknownReturn>(_unknownReturns.ToArray()),
        new ReadOnlyCollection<ObserverEventRecord>(_events.ToArray()),
        new ReadOnlyCollection<string>(_failures.ToArray()));

    private bool TryStart(ObserverEventIdentity identity, ObserverOperationKind kind, string action, out string failure)
    {
        if (!TryEvent(identity, action, allowKnownOperation: false, out failure)) return false;
        if (!_startedOperationIds.Add(identity.OperationId)) { failure = "operation-id-replayed"; Invalidate(failure); return false; }
        return true;
    }

    private bool TryComplete(ObserverEventIdentity identity, ObserverOperationKind kind, string action, out string failure)
    {
        if (!TryEvent(identity, action, allowKnownOperation: true, out failure)) return false;
        if (!_startedOperationIds.Contains(identity.OperationId) || _inFlightOperationId != identity.OperationId || _inFlightOperationKind != kind)
        { failure = "completion-does-not-match-inflight-operation"; Invalidate(failure); return false; }
        return true;
    }

    private bool TryEvent(ObserverEventIdentity identity, string action, bool allowKnownOperation, out string failure)
    {
        failure = string.Empty;
        if (identity is null)
        {
            failure = "event-identity-invalid";
            Invalidate(failure);
            return false;
        }

        if (identity.EventId == Guid.Empty || identity.OperationId == Guid.Empty) failure = "event-identity-invalid";
        else if (identity.RunId != _binding.RunId || identity.SessionId != _binding.SessionId || identity.Anchor != _binding.Anchor || identity.Attempt != _binding.Attempt)
            failure = "event-binding-mismatch";
        else if (identity.Sequence != _nextSequence || identity.MonotonicTicks <= _lastMonotonicTicks) failure = "event-sequence-or-time-invalid";
        else if (!SameIdentity(identity.ActingOwner, _binding.ResourceOwner)) failure = "wrong-resource-owner";
        else if (!SameIdentity(identity.CommandIssuer, _binding.CommandIssuer)) failure = "wrong-command-issuer";
        else if (_eventIds.Contains(identity.EventId)) failure = "event-id-replayed";
        else if (!allowKnownOperation && _startedOperationIds.Contains(identity.OperationId)) failure = "operation-id-replayed";
        else if (_eventIds.Count >= _maximumEvents) failure = "event-bound-exceeded";
        if (failure.Length != 0) { Invalidate(failure); return false; }
        _eventIds.Add(identity.EventId); _nextSequence++; _lastMonotonicTicks = identity.MonotonicTicks; return true;
    }

    private void PreserveReturnedCoordinate(ObserverResourceKind kind, long coordinate, Guid operationId, string reason, uint? apiStatus = null)
    {
        if (!ValidCoordinate(coordinate)) return;
        if (_unknownReturns.Count < MaximumUnknownReturns) _unknownReturns.Add(new(kind, coordinate, operationId, reason, apiStatus));
        else _unknownReturnOverflow = true;
        _operationFailed = true;
        if (ValidKind(kind)) SetState(kind, ObserverResourceState.UnknownCustody);
    }

    private ObserverTransitionResult RejectRecord(ObserverEventIdentity id, string action, string reason)
    { Invalidate(reason); Record(id, action, false, reason); return Reject(reason); }
    private ObserverTransitionResult RejectInvalid(string reason) { Invalidate(reason); return Reject(reason); }
    private ObserverTransitionResult Reject(string reason) => new(false, reason, Snapshot());
    private ObserverTransitionResult Accept(string reason = "accepted") => new(true, reason, Snapshot());
    private void Record(ObserverEventIdentity? id, string action, bool accepted, string reason,
        uint? apiStatus = null, ObserverResourceKind? resourceKind = null, long? coordinate = null,
        Guid? acquisitionOperationId = null)
    {
        if (_events.Count < _maximumEvents)
            _events.Add(new(id?.Sequence ?? 0, id?.MonotonicTicks ?? 0, id?.EventId ?? Guid.Empty,
                id?.OperationId ?? Guid.Empty, action, accepted, reason, apiStatus, resourceKind,
                coordinate, acquisitionOperationId));
        else
            Invalidate("event-record-bound-exceeded");
    }

    private void RecordRelease(ObserverEventIdentity? id, ObserverResourceKind kind, ObserverDescriptor? descriptor,
        uint status, bool accepted, string reason) => Record(id, $"complete-release-{kind}", accepted, reason,
        status, kind, descriptor?.Coordinate, descriptor?.AcquisitionOperationId);

    private bool ExactDescriptor(ObserverResourceKind kind, ObserverDescriptor? descriptor) =>
        descriptor is not null && descriptor.Kind == kind && descriptor == (kind == ObserverResourceKind.Marker ? _marker : _snapshot);

    private void CheckRoleLiveness(ObserverRoleLiveness? observed, InventoryProcessIdentity expected,
        ObserverEventIdentity gateEvent, List<string> blockers)
    {
        if (observed is null || !SameIdentity(observed.Identity, expected) || !observed.Live ||
            observed.ObservedMonotonicTicks <= _lastReleaseMonotonicTicks ||
            observed.ObservedMonotonicTicks > gateEvent.MonotonicTicks)
            blockers.Add($"role-liveness-invalid-{expected.Role}");
    }
    private void Invalidate(string reason)
    { _invalidTransitionObserved = true; if (_failures.Count < MaximumOperations && !_failures.Contains(reason, StringComparer.Ordinal)) _failures.Add(reason); }
    private void SetInflight(Guid id, ObserverOperationKind kind) { _inFlightOperationId = id; _inFlightOperationKind = kind; }
    private void ClearInflight() { _inFlightOperationId = null; _inFlightOperationKind = null; }
    private ObserverResourceState State(ObserverResourceKind kind) => kind == ObserverResourceKind.Snapshot ? _snapshotState : _markerState;
    private void SetState(ObserverResourceKind kind, ObserverResourceState state)
    { if (kind == ObserverResourceKind.Snapshot) _snapshotState = state; else _markerState = state; }
    private bool Aborted => _timeoutObserved || _operationFailed || _invalidTransitionObserved;
    private static bool ValidCoordinate(long coordinate) => coordinate > 0;
    private static bool ValidKind(ObserverResourceKind kind) => Enum.IsDefined(kind);
    private static ObserverOperationKind AcquireKind(ObserverResourceKind kind) => kind == ObserverResourceKind.Snapshot ? ObserverOperationKind.AcquireSnapshot : ObserverOperationKind.AcquireMarker;
    private static ObserverOperationKind ReleaseKind(ObserverResourceKind kind) => kind == ObserverResourceKind.Snapshot ? ObserverOperationKind.ReleaseSnapshot : ObserverOperationKind.ReleaseMarker;

    private static void ValidateBinding(ObserverRunBinding binding)
    {
        if (binding.RunId == Guid.Empty || binding.SessionId == Guid.Empty || binding.RunId == binding.SessionId || binding.Attempt is < 1 or > 3 || !Enum.IsDefined(binding.Anchor))
            throw new ArgumentException("Observer binding is invalid.", nameof(binding));
        ValidateIdentity(binding.Collector, nameof(binding.Collector)); ValidateIdentity(binding.ResourceOwner, nameof(binding.ResourceOwner));
        ValidateIdentity(binding.CommandIssuer, nameof(binding.CommandIssuer)); ValidateIdentity(binding.Target, nameof(binding.Target));
        ValidateIdentity(binding.ControllerProcess, nameof(binding.ControllerProcess));
        ValidateIdentity(binding.OwnerProcess, nameof(binding.OwnerProcess));
        ValidateIdentity(binding.WriterProcess, nameof(binding.WriterProcess));
        if (binding.ControllerProcess.Role != InventoryRole.Controller || binding.OwnerProcess.Role != InventoryRole.Owner ||
            binding.WriterProcess.Role != InventoryRole.Writer ||
            binding.ControllerProcess.ProcessId == binding.OwnerProcess.ProcessId ||
            binding.ControllerProcess.ProcessId == binding.WriterProcess.ProcessId ||
            binding.OwnerProcess.ProcessId == binding.WriterProcess.ProcessId)
            throw new ArgumentException("The complete role identity graph must be distinct and canonical.", nameof(binding));
        foreach (var identity in new[] { binding.Collector, binding.ResourceOwner, binding.CommandIssuer, binding.Target })
        {
            var expected = identity.Role switch
            {
                InventoryRole.Controller => binding.ControllerProcess,
                InventoryRole.Owner => binding.OwnerProcess,
                _ => binding.WriterProcess,
            };
            if (!SameIdentity(identity, expected))
                throw new ArgumentException("Selected identities must match the trusted role graph.", nameof(binding));
        }
        if (!SameIdentity(binding.Collector, binding.ResourceOwner))
            throw new ArgumentException("Resource owner must be the actual collector.", nameof(binding));
        if (binding.CommandIssuer.Role != InventoryRole.Controller)
            throw new ArgumentException("Command issuer must be the bound controller.", nameof(binding));

        var identities = new[] { binding.Collector, binding.ResourceOwner, binding.CommandIssuer, binding.Target };
        for (var left = 0; left < identities.Length; left++)
        {
            for (var right = left + 1; right < identities.Length; right++)
            {
                if (identities[left].Role == identities[right].Role && !SameIdentity(identities[left], identities[right]))
                    throw new ArgumentException("One role is bound to incompatible process identities.", nameof(binding));
                if (identities[left].Role != identities[right].Role && identities[left].ProcessId == identities[right].ProcessId)
                    throw new ArgumentException("Different roles cannot alias one process identifier.", nameof(binding));
            }
        }
    }

    private static void ValidateIdentity(InventoryProcessIdentity identity, string name)
    { if (identity is null || !Enum.IsDefined(identity.Role) || identity.ProcessId <= 0 || identity.CreationFileTime <= 0) throw new ArgumentException("Process identity is invalid.", name); }
    private static bool SameIdentity(InventoryProcessIdentity? left, InventoryProcessIdentity? right) => left is not null && right is not null && left == right;
    private static InventoryProcessIdentity CopyIdentity(InventoryProcessIdentity value) => value with { };
    private static ObserverRunBinding CopyBinding(ObserverRunBinding value) => value with
    {
        Collector = CopyIdentity(value.Collector),
        ResourceOwner = CopyIdentity(value.ResourceOwner),
        CommandIssuer = CopyIdentity(value.CommandIssuer),
        Target = CopyIdentity(value.Target),
        ControllerProcess = CopyIdentity(value.ControllerProcess),
        OwnerProcess = CopyIdentity(value.OwnerProcess),
        WriterProcess = CopyIdentity(value.WriterProcess),
    };
}
