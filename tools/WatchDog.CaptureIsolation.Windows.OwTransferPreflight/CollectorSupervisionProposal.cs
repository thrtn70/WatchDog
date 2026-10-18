using System.Collections.ObjectModel;
using System.Text;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal enum CollectorSupervisionState
{
    Unsupported,
    Prepared,
    CallInFlight,
    Aborted,
    TerminationRequested,
    ExactExited,
    ReapedWithUnknownResources,
    CleanupUnresolved,
}

internal enum SupervisionCallKind
{
    AcquireMarker,
    AcquireSnapshot,
    Walk,
    ReleaseMarker,
    ReleaseSnapshot,
}

internal enum SupervisionJobObservation
{
    NotObserved,
    EmptyWhileOwned,
    NonEmptyWhileOwned,
    UnknownAfterLastHandleClose,
}

internal enum SupervisionTargetContext
{
    OwnerCurrentProcess,
    OwnerOriginalWriterSlot,
}

internal sealed record SupervisionPolicy(
    long MonotonicFrequency,
    long PerCallTicks,
    long TotalSampleTicks,
    long LateCompletionGraceTicks,
    long ReapObservationTicks)
{
    internal const int MaximumCalls = 60;
    internal const int ReservedReleaseCalls = 2;
    internal const int MaximumEvents = 128;
    internal const int OrdinaryEventLimit = 116;
    internal const int ReservedReleaseEvents = 4;
    internal const int ReservedAbortAndFinalEvents = 8;
    internal const int MaximumUnknownReturns = 8;
    internal const int MaximumRows = 16_384;
    internal const long MaximumCopiedBytes = 8L * 1024 * 1024;
    internal const long MaximumEncodedBytes = 8L * 1024 * 1024;
    internal const int MaximumFrameBytes = 64 * 1024;
    internal const long ReservedCleanupAndFinalEncodedBytes =
        (ReservedReleaseEvents + ReservedAbortAndFinalEvents) * (long)MaximumFrameBytes;
    internal const long MaximumOrdinaryEncodedBytes = MaximumEncodedBytes - ReservedCleanupAndFinalEncodedBytes;

    internal static SupervisionPolicy FromFrequency(long frequency)
    {
        if (frequency <= 0)
            throw new ArgumentOutOfRangeException(nameof(frequency));

        return new(
            frequency,
            checked(frequency * 5),
            checked(frequency * 15),
            checked(frequency * 5),
            checked(frequency * 10));
    }
}

internal sealed record SupervisionPayloadBudget(int Rows, long CopiedBytes, long EncodedBytes, int MaximumFrameBytes)
{
    internal static readonly SupervisionPayloadBudget Empty = new(0, 0, 0, 0);
}

// Both the trusted expectation and candidate ACK are copied before comparison. A caller cannot
// mutate either side into agreement after preparation.
internal sealed record SupervisionCapabilitySlot(
    string LogicalSlotId,
    InventoryProcessIdentity Holder,
    InventoryRole Creator,
    InventoryRole? InvokingDuplicator,
    InventoryRole? Target,
    InventoryObjectClass ObjectClass,
    InventoryCoordinateKind CoordinateKind,
    long? Coordinate,
    InventoryProcessIdentity CoordinateOwner,
    InventoryObjectIdentity ObjectIdentity,
    uint? RequestedAccess,
    uint? GrantedAccess,
    bool? Inheritable,
    InventoryMeasurementProvenance Provenance);

internal sealed record CollectorSupervisionEvent(
    Guid RunId,
    Guid SessionId,
    InventoryAnchor Anchor,
    int Attempt,
    long Sequence,
    long MonotonicTicks,
    Guid EventId,
    Guid OperationId,
    InventoryProcessIdentity Actor,
    InventoryProcessIdentity Issuer);

internal sealed record CollectorSupervisionBinding(
    Guid RunId,
    Guid SessionId,
    InventoryAnchor Anchor,
    int Attempt,
    InventoryProcessIdentity Controller,
    InventoryProcessIdentity Owner,
    InventoryProcessIdentity Writer,
    InventoryProcessIdentity Target,
    SupervisionTargetContext TargetContext,
    string TargetSlotId,
    string SelectedResource,
    IReadOnlyList<SupervisionCapabilitySlot> RequiredCustody,
    SupervisionPolicy Policy);

internal sealed record SupervisionUnknownObligation(
    ObserverResourceKind? Kind,
    InventoryProcessIdentity ObservedOwner,
    Guid OperationId,
    uint? ApiStatus,
    long? Coordinate,
    string EvidenceSource,
    string MissingResultReason,
    bool OwnerConfirmedExited,
    long ObservedMonotonicTicks);

internal sealed record SupervisionTerminationEvidence(
    InventoryProcessIdentity Target,
    uint? RequestResult,
    long? RequestMonotonicTicks,
    bool ProcessSignaled,
    uint? ExitCode,
    long? ExitFileTime,
    long? ExitMonotonicTicks,
    SupervisionJobObservation JobObservation,
    long? JobObservationMonotonicTicks);

internal sealed record SupervisionEventRecord(
    long Sequence,
    long MonotonicTicks,
    Guid EventId,
    Guid OperationId,
    string Action,
    string Reason);

internal sealed record SupervisionPendingCompletionSnapshot(
    SupervisionCallKind Kind,
    Guid OperationId,
    long OriginalMonotonicTicks,
    uint ApiStatus,
    long? Coordinate,
    ObserverDescriptor? Descriptor,
    SupervisionPayloadBudget Payload);

internal sealed record CollectorSupervisionSnapshot(
    CollectorSupervisionState State,
    CollectorSupervisionBinding Binding,
    bool CustodyAcknowledged,
    IReadOnlyList<SupervisionCapabilitySlot> ExpectedCustody,
    IReadOnlyList<SupervisionCapabilitySlot> AcknowledgedCustody,
    SupervisionCallKind? InFlightKind,
    Guid? InFlightOperationId,
    long? InFlightStartedMonotonicTicks,
    long? InFlightDeadlineMonotonicTicks,
    long? OriginalPendingCompletionMonotonicTicks,
    SupervisionPendingCompletionSnapshot? PendingCompletion,
    int BegunCalls,
    int OrdinaryEvents,
    int ReleaseEvents,
    int AbortAndFinalEvents,
    int SuppliedRows,
    long CopiedBytes,
    long EncodedBytes,
    int RejectedEventCount,
    bool RejectedSummaryOverflow,
    bool UnknownReturnOverflow,
    IReadOnlyList<SupervisionUnknownObligation> UnknownObligations,
    IReadOnlyDictionary<string, int> RejectedReasons,
    IReadOnlyList<SupervisionEventRecord> Events,
    ObserverLifecycleSnapshot Observer,
    SupervisionTerminationEvidence? Termination)
{
    internal bool SyntheticOnly => true;
    internal bool NativeCallsPerformed => false;
    internal bool NativeCapturePerformed => false;
    internal bool NativeCaptureProven => false;
    internal bool NativeAuthorityProven => false;
    internal bool NativeDurabilityProven => false;
    internal bool CompleteHandleInventoryProven => false;
    internal bool ZeroExtraInheritanceProven => false;
    internal bool ProductionAdmissionGranted => false;
    internal bool B1WriteReleaseIssued => false;
    internal bool B1JournalWritesPerformed => false;
    internal bool ReleaseApproved => false;
}

internal sealed record CollectorSupervisionTransition(bool Accepted, string Reason, CollectorSupervisionSnapshot State);

internal sealed class CollectorSupervisionProposal
{
    private const int MaximumRejectedReasonKinds = 16;
    private static readonly string[] RequiredSupervisorSlots =
    [
        "p:o-process-original",
        "p:o-primary-thread-original",
        "p:outer-job",
        "p:w-process-retained",
    ];

    private readonly CollectorSupervisionBinding _binding;
    private readonly SupervisionPolicy _policy;
    private readonly ObserverResourceLifecycle _observer;
    private readonly SupervisionCapabilitySlot[] _expectedCustody;
    private SupervisionCapabilitySlot[] _acknowledgedCustody = [];
    private readonly List<SupervisionUnknownObligation> _unknown = [];
    private readonly List<SupervisionEventRecord> _events = [];
    private readonly Dictionary<string, int> _rejectedReasons = new(StringComparer.Ordinal);
    private CollectorSupervisionState _state = CollectorSupervisionState.Unsupported;
    private InFlightCall? _inFlight;
    private PendingCompletion? _pendingCompletion;
    private SupervisionTerminationEvidence? _termination;
    private long _nextEnvelopeSequence = 1;
    private long _lastEnvelopeTicks;
    private long _nextObserverSequence = 1;
    private long? _sampleStartedTicks;
    private long? _sampleDeadlineTicks;
    private int _begunCalls;
    private int _ordinaryEvents;
    private int _releaseEvents;
    private int _abortAndFinalEvents;
    private int _rows;
    private long _copiedBytes;
    private long _encodedBytes;
    private int _rejectedEventCount;
    private bool _rejectedSummaryOverflow;
    private bool _unknownOverflow;
    private bool _abortLatched;
    private long? _abortObservedTicks;
    private bool _custodyAcknowledged;

    internal CollectorSupervisionProposal(CollectorSupervisionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ValidateBinding(binding);
        _binding = CopyBinding(binding);
        _policy = binding.Policy with { };
        _expectedCustody = CopySlots(binding.RequiredCustody);
        _observer = new ObserverResourceLifecycle(new ObserverRunBinding(
            binding.RunId,
            binding.SessionId,
            binding.Anchor,
            binding.Attempt,
            CopyIdentity(binding.Owner),
            CopyIdentity(binding.Owner),
            CopyIdentity(binding.Controller),
            CopyIdentity(binding.Target),
            CopyIdentity(binding.Controller),
            CopyIdentity(binding.Owner),
            CopyIdentity(binding.Writer)));
    }

    internal CollectorSupervisionTransition AcknowledgeCustody(IReadOnlyList<SupervisionCapabilitySlot>? candidate)
    {
        if (_state != CollectorSupervisionState.Unsupported || _begunCalls != 0)
            return Reject("custody-ack-not-one-use");
        if (_binding.Anchor == InventoryAnchor.B0)
            return Reject("b0-independent-w-custody-unsupported");
        if (candidate is null)
            return Reject("cleanup-custody-ack-missing");
        if (candidate.Count is 0 or > 64 || candidate.Any(slot => slot is null))
            return Reject("cleanup-custody-ack-malformed");
        if (candidate.Any(slot => !ValidSlot(slot)))
            return Reject("cleanup-custody-ack-malformed");

        var copied = CopySlots(candidate);
        if (!ExactSlots(_expectedCustody, copied))
            return Reject("cleanup-custody-ack-mismatch");

        _acknowledgedCustody = copied;
        _custodyAcknowledged = true;
        _state = CollectorSupervisionState.Prepared;
        return Accept("prepared");
    }

    internal CollectorSupervisionTransition BeginCall(
        SupervisionCallKind kind,
        CollectorSupervisionEvent identity,
        SupervisionPayloadBudget anticipatedPayload)
    {
        ArgumentNullException.ThrowIfNull(anticipatedPayload);
        if (!Enum.IsDefined(kind)) return Reject("call-kind-invalid");
        if (!ValidatePayload(anticipatedPayload)) return Reject("payload-reservation-invalid");
        if (_termination is not null) return Reject("call-after-termination-request");
        if (_state != CollectorSupervisionState.Prepared && _state != CollectorSupervisionState.Aborted)
            return Reject("call-state-not-prepared");
        if (_inFlight is not null) return Reject("one-call-exclusion");
        if (_state == CollectorSupervisionState.Aborted && !IsRelease(kind))
            return Reject("aborted-allows-cleanup-only");
        if (_state == CollectorSupervisionState.Aborted && IsRelease(kind))
        {
            var observerState = _observer.Snapshot();
            if (!observerState.WalkExhausted && !observerState.TimeoutObserved &&
                !observerState.OperationFailed && !observerState.InvalidTransitionObserved)
            {
                _state = CollectorSupervisionState.CleanupUnresolved;
                return Reject("outer-abort-without-observer-release-eligibility");
            }
        }
        if (!HasPayloadCapacity(anticipatedPayload)) return AbortWithoutEvent("payload-budget-exhausted");
        if (!HasCallCapacity(kind)) return AbortWithoutEvent("call-budget-exhausted");
        if (!HasEventCapacity(kind, 2)) return AbortWithoutEvent("event-budget-exhausted");
        if (!TryAcceptEvent(identity, ExpectedActor(kind), EventClass(kind), $"begin-{kind}", out var failure))
            return Reject(failure);

        if (_sampleStartedTicks is null)
        {
            _sampleStartedTicks = identity.MonotonicTicks;
            _sampleDeadlineTicks = CheckedAdd(identity.MonotonicTicks, _policy.TotalSampleTicks);
        }
        var sampleCutoff = IsRelease(kind) && _abortLatched
            ? CheckedAdd(_abortObservedTicks ?? _lastEnvelopeTicks, _policy.LateCompletionGraceTicks)
            : _sampleDeadlineTicks!.Value;
        if (identity.MonotonicTicks >= sampleCutoff)
            return AbortAccepted(identity, "sample-deadline-reached-before-call");

        var deadline = Math.Min(
            CheckedAdd(identity.MonotonicTicks, _policy.PerCallTicks),
            sampleCutoff);
        var observerResult = ProjectBegin(kind, identity);
        if (!observerResult.Accepted)
            return AbortAccepted(identity, $"observer-{observerResult.Reason}");

        _begunCalls++;
        _inFlight = new(kind, identity.OperationId, identity.MonotonicTicks, deadline, anticipatedPayload with { });
        _state = CollectorSupervisionState.CallInFlight;
        return Accept("call-in-flight");
    }

    internal CollectorSupervisionTransition CompleteAcquisition(
        SupervisionCallKind kind,
        CollectorSupervisionEvent identity,
        uint apiStatus,
        long returnedCoordinate,
        SupervisionPayloadBudget actualPayload)
    {
        if (identity is null)
            return PreserveRejectedReturnAndReject(kind, identity!, apiStatus, returnedCoordinate, "event-identity-invalid");
        if (kind is not (SupervisionCallKind.AcquireMarker or SupervisionCallKind.AcquireSnapshot))
            return PreserveRejectedReturnAndReject(kind, identity, apiStatus, returnedCoordinate, "completion-kind-invalid");
        return Complete(identity, new PendingCompletion(kind, identity.MonotonicTicks, apiStatus,
            returnedCoordinate, null, CopyPayload(actualPayload)), actualPayload);
    }

    internal CollectorSupervisionTransition CompleteWalk(
        CollectorSupervisionEvent identity,
        uint apiStatus,
        SupervisionPayloadBudget actualPayload)
    {
        if (identity is null) return Reject("event-identity-invalid");
        return Complete(identity, new PendingCompletion(SupervisionCallKind.Walk, identity.MonotonicTicks,
            apiStatus, null, null, CopyPayload(actualPayload)), actualPayload);
    }

    internal CollectorSupervisionTransition CompleteRelease(
        SupervisionCallKind kind,
        CollectorSupervisionEvent identity,
        ObserverDescriptor descriptor,
        uint apiStatus)
    {
        if (identity is null) return Reject("event-identity-invalid");
        if (kind is not (SupervisionCallKind.ReleaseMarker or SupervisionCallKind.ReleaseSnapshot))
            return Reject("release-kind-invalid");
        ArgumentNullException.ThrowIfNull(descriptor);
        return Complete(identity, new PendingCompletion(kind, identity.MonotonicTicks,
            apiStatus, null, descriptor with { Owner = CopyIdentity(descriptor.Owner) }, SupervisionPayloadBudget.Empty),
            SupervisionPayloadBudget.Empty);
    }

    internal CollectorSupervisionTransition ObserveDeadline(CollectorSupervisionEvent identity)
    {
        if (identity is null) return Reject("event-identity-invalid");
        if (IsTerminalPhase(_state)) return RejectTerminalEvent("deadline-after-terminal-supervision-state");
        if (_inFlight is null) return Reject("deadline-without-inflight-call");
        if (_observer.Snapshot().TimeoutObserved)
        {
            RecordRejected("timeout-already-observed");
            return Reject("timeout-already-observed");
        }
        if (!TryAcceptEvent(identity, _binding.Controller, EnvelopeEventClass.AbortAndFinal,
                "deadline-observed", out var failure)) return Reject(failure);
        if (identity.OperationId != _inFlight.OperationId) return AbortAccepted(identity, "deadline-operation-mismatch");
        if (identity.MonotonicTicks < _inFlight.DeadlineTicks) return AbortAccepted(identity, "deadline-observed-too-early");

        var projected = _observer.RecordTimeout(ProjectObserverEvent(identity));
        LatchAbort(identity.MonotonicTicks);
        _state = CollectorSupervisionState.Aborted;
        return new(projected.Accepted, projected.Accepted ? "sticky-timeout" : $"observer-{projected.Reason}", Snapshot());
    }

    internal CollectorSupervisionTransition DeliverPendingCompletion(CollectorSupervisionEvent identity)
    {
        if (_pendingCompletion is null || _inFlight is null) return Reject("no-pending-completion");
        if (_state is CollectorSupervisionState.TerminationRequested or CollectorSupervisionState.ExactExited or
            CollectorSupervisionState.ReapedWithUnknownResources or CollectorSupervisionState.CleanupUnresolved)
            return Reject("pending-delivery-after-termination");
        if (!TryAcceptEvent(identity, _binding.Controller, EnvelopeEventClass.AbortAndFinal,
                "deliver-pending-completion", out var failure)) return Reject(failure);
        if (identity.OperationId != _pendingCompletion.OperationId || identity.MonotonicTicks <= _pendingCompletion.OriginalTicks)
            return AbortAccepted(identity, "pending-completion-identity-or-time-mismatch");

        var completion = _pendingCompletion;
        _pendingCompletion = null;
        return ProjectCompletionAndFinish(identity, completion, "deferred-late-completion-delivered");
    }

    internal CollectorSupervisionTransition RecordTransportLoss(
        CollectorSupervisionEvent identity,
        ObserverResourceKind? possiblyAcquiredKind,
        bool unpublishedDescriptorPossible)
    {
        if (identity is null) return Reject("event-identity-invalid");
        if (possiblyAcquiredKind is { } suppliedKind && !Enum.IsDefined(suppliedKind))
            return Reject("resource-kind-invalid");
        if (IsTerminalPhase(_state))
        {
            if (unpublishedDescriptorPossible)
                PreserveUnknown(possiblyAcquiredKind, _binding.Owner, identity.OperationId, null, null,
                    "transport", "terminal-transport-loss-unpublished-descriptor", HasConfirmedExactExit(), identity.MonotonicTicks);
            return RejectTerminalEvent("transport-loss-after-terminal-supervision-state");
        }
        if (_inFlight is null) return Reject("transport-loss-without-inflight-call");
        if (_observer.Snapshot().TimeoutObserved)
        {
            if (unpublishedDescriptorPossible)
                PreserveUnknown(possiblyAcquiredKind, _binding.Owner, identity.OperationId, null, null,
                    "transport", "transport-loss-after-timeout", false, identity.MonotonicTicks);
            RecordRejected("abort-already-observed");
            return Reject("abort-already-observed");
        }
        if (!TryAcceptEvent(identity, _binding.Controller, EnvelopeEventClass.AbortAndFinal,
                "transport-loss", out var failure)) return Reject(failure);
        if (identity.OperationId != _inFlight.OperationId) return AbortAccepted(identity, "transport-loss-operation-mismatch");

        var timeout = _observer.RecordTimeout(ProjectObserverEvent(identity));
        if (unpublishedDescriptorPossible)
            PreserveUnknown(possiblyAcquiredKind, _binding.Owner, identity.OperationId, null, null,
                "transport", "possible-unpublished-descriptor", false, identity.MonotonicTicks);
        LatchAbort(identity.MonotonicTicks);
        _state = CollectorSupervisionState.Aborted;
        return new(timeout.Accepted, timeout.Accepted ? "transport-loss-unknown-custody" : $"observer-{timeout.Reason}", Snapshot());
    }

    internal CollectorSupervisionTransition EvaluatePostReleaseLiveness(
        CollectorSupervisionEvent identity,
        ObserverRoleLiveness controller,
        ObserverRoleLiveness owner,
        ObserverRoleLiveness writer)
    {
        if (identity is null) return Reject("event-identity-invalid");
        if (_abortLatched || _state != CollectorSupervisionState.Prepared || _inFlight is not null ||
            _pendingCompletion is not null || HasUnknownCustody())
            return Reject("outer-supervision-state-not-clean");
        if (!TryAcceptEvent(identity, _binding.Controller, EnvelopeEventClass.AbortAndFinal,
                "post-release-liveness", out var failure)) return Reject(failure);
        var projected = ProjectObserverEvent(identity);
        var result = _observer.EvaluateSyntheticTransition(new ObserverLivenessEvidence(
            projected,
            CopyIdentity(_binding.Owner),
            CopyIdentity(_binding.Controller),
            CopyIdentity(_binding.Target),
            true,
            true,
            true,
            controller,
            owner,
            writer));
        if (!result.CanTriggerSyntheticTransition)
        {
            LatchAbort(identity.MonotonicTicks);
            _state = CollectorSupervisionState.Aborted;
            return Reject("post-release-liveness-rejected");
        }
        return Accept("synthetic-cleanup-liveness-only-no-admission");
    }

    internal CollectorSupervisionTransition RequestTermination(
        CollectorSupervisionEvent identity,
        InventoryProcessIdentity target,
        uint requestResult)
    {
        if (identity is null) return Reject("event-identity-invalid");
        if (_state != CollectorSupervisionState.Aborted) return Reject("termination-before-abort");
        if (_pendingCompletion is not null && (_abortObservedTicks is null ||
            identity.MonotonicTicks < CheckedAdd(_abortObservedTicks.Value, _policy.LateCompletionGraceTicks)))
            return Reject("termination-before-late-completion-grace");
        if (!TryAcceptEvent(identity, _binding.Controller, EnvelopeEventClass.AbortAndFinal,
                "termination-requested", out var failure)) return Reject(failure);
        if (!SameIdentity(target, _binding.Owner)) return AbortAccepted(identity, "termination-target-identity-mismatch");

        if (_pendingCompletion is { } abandoned)
        {
            PreserveUnknown(ResourceKind(abandoned.Kind), _binding.Owner, abandoned.OperationId, abandoned.ApiStatus,
                abandoned.Coordinate ?? abandoned.Descriptor?.Coordinate, "pending-completion",
                "pending-completion-unverified-before-termination", false, abandoned.OriginalTicks);
        }

        _termination = new(CopyIdentity(target), requestResult, identity.MonotonicTicks, false, null, null, null,
            SupervisionJobObservation.NotObserved, null);
        LatchAbort(identity.MonotonicTicks);
        _state = CollectorSupervisionState.TerminationRequested;
        return Accept(requestResult == 0 ? "termination-request-accepted-not-exit" : "termination-request-failed");
    }

    internal CollectorSupervisionTransition RecordExactExit(
        CollectorSupervisionEvent identity,
        InventoryProcessIdentity observed,
        bool processSignaled,
        uint? exitCode,
        long? exitFileTime,
        SupervisionJobObservation jobObservation)
    {
        if (identity is null) return Reject("event-identity-invalid");
        if (_state != CollectorSupervisionState.TerminationRequested || _termination is null)
            return Reject("exit-before-termination-request");
        if (!TryAcceptEvent(identity, _binding.Controller, EnvelopeEventClass.AbortAndFinal,
                "exact-exit-observed", out var failure)) return Reject(failure);
        if (!SameIdentity(observed, _termination.Target))
        {
            _state = CollectorSupervisionState.CleanupUnresolved;
            return Reject("exit-identity-mismatch");
        }
        if (identity.MonotonicTicks >= CheckedAdd(_termination.RequestMonotonicTicks!.Value, _policy.ReapObservationTicks))
        {
            _state = CollectorSupervisionState.CleanupUnresolved;
            return Reject("reap-observation-deadline-reached");
        }

        _termination = _termination with
        {
            ProcessSignaled = processSignaled,
            ExitCode = processSignaled ? exitCode : null,
            ExitFileTime = processSignaled ? exitFileTime : null,
            ExitMonotonicTicks = processSignaled ? identity.MonotonicTicks : null,
            JobObservation = jobObservation,
            JobObservationMonotonicTicks = identity.MonotonicTicks,
        };
        if (!processSignaled || exitCode is null || exitFileTime is null or <= 0)
        {
            _state = CollectorSupervisionState.CleanupUnresolved;
            return Reject("termination-request-did-not-prove-exit");
        }

        var unresolved = HasUnknownCustody() || _inFlight is not null ||
            jobObservation != SupervisionJobObservation.EmptyWhileOwned;
        _state = unresolved ? CollectorSupervisionState.ReapedWithUnknownResources : CollectorSupervisionState.ExactExited;
        return Accept(unresolved ? "exact-exit-with-unresolved-resources" : "exact-exit-separate-from-release");
    }

    internal CollectorSupervisionSnapshot Snapshot()
    {
        var observer = _observer.Snapshot();
        return new(
            _state,
            CopyBinding(_binding),
            _custodyAcknowledged,
            new ReadOnlyCollection<SupervisionCapabilitySlot>(CopySlots(_expectedCustody)),
            new ReadOnlyCollection<SupervisionCapabilitySlot>(CopySlots(_acknowledgedCustody)),
            _inFlight?.Kind,
            _inFlight?.OperationId,
            _inFlight?.StartedTicks,
            _inFlight?.DeadlineTicks,
            _pendingCompletion?.OriginalTicks,
            _pendingCompletion is null ? null : new(
                _pendingCompletion.Kind,
                _pendingCompletion.OperationId,
                _pendingCompletion.OriginalTicks,
                _pendingCompletion.ApiStatus,
                _pendingCompletion.Coordinate,
                _pendingCompletion.Descriptor is null ? null : _pendingCompletion.Descriptor with
                {
                    Owner = CopyIdentity(_pendingCompletion.Descriptor.Owner),
                },
                _pendingCompletion.Payload with { }),
            _begunCalls,
            _ordinaryEvents,
            _releaseEvents,
            _abortAndFinalEvents,
            _rows,
            _copiedBytes,
            _encodedBytes,
            _rejectedEventCount,
            _rejectedSummaryOverflow,
            _unknownOverflow || observer.UnknownReturnOverflow ||
                observer.UnknownReturns.Count + _unknown.Count > SupervisionPolicy.MaximumUnknownReturns,
            new ReadOnlyCollection<SupervisionUnknownObligation>(_unknown.Select(CopyUnknown).ToArray()),
            new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(_rejectedReasons, StringComparer.Ordinal)),
            new ReadOnlyCollection<SupervisionEventRecord>(_events.ToArray()),
            observer,
            _termination is null ? null : _termination with { Target = CopyIdentity(_termination.Target) });
    }

    private CollectorSupervisionTransition Complete(
        CollectorSupervisionEvent identity,
        PendingCompletion completion,
        SupervisionPayloadBudget actualPayload)
    {
        ArgumentNullException.ThrowIfNull(actualPayload);
        if (_state is CollectorSupervisionState.TerminationRequested or CollectorSupervisionState.ExactExited or
            CollectorSupervisionState.ReapedWithUnknownResources or CollectorSupervisionState.CleanupUnresolved)
            return PreserveRejectedCompletionAndReject(identity, completion, "completion-after-terminal-supervision-state");
        if (_inFlight is null) return PreserveRejectedCompletionAndReject(identity, completion, "completion-without-inflight-call");
        if (_pendingCompletion is not null)
            return PreserveRejectedCompletionAndReject(identity, completion, "pending-completion-already-retained");
        if (!ValidatePayload(actualPayload)) return PreserveRejectedCompletionAndReject(identity, completion, "completion-payload-invalid");
        if (!TryAcceptEvent(identity, _binding.Owner, EventClass(completion.Kind),
                $"complete-{completion.Kind}", out var failure))
            return PreserveRejectedCompletionAndReject(identity, completion, failure);
        if (identity.OperationId != _inFlight.OperationId || completion.Kind != _inFlight.Kind)
            return PreserveRejectedCompletionAndReject(identity, completion, "completion-does-not-match-inflight-call");
        if (!PayloadWithin(actualPayload, _inFlight.ReservedPayload) || !HasPayloadCapacity(actualPayload))
            return PreserveRejectedCompletionAndReject(identity, completion, "completion-payload-exceeded-reservation-or-bound");

        CommitPayload(actualPayload);
        var observerBeforeCompletion = _observer.Snapshot();
        var finalNonterminalWalk = completion.Kind == SupervisionCallKind.Walk &&
            completion.ApiStatus == ObserverResourceLifecycle.Success &&
            _begunCalls >= SupervisionPolicy.MaximumCalls - SupervisionPolicy.ReservedReleaseCalls;
        var outerAbortNeedsObserverProjection = _abortLatched && !IsRelease(completion.Kind) &&
            !observerBeforeCompletion.TimeoutObserved;
        if (outerAbortNeedsObserverProjection || finalNonterminalWalk || identity.MonotonicTicks >= _inFlight.DeadlineTicks)
        {
            var timeout = _observer.RecordTimeout(ProjectObserverEvent(identity));
            _pendingCompletion = completion with { OperationId = identity.OperationId };
            LatchAbort(identity.MonotonicTicks);
            _state = CollectorSupervisionState.Aborted;
            var reason = finalNonterminalWalk
                ? "final-nonterminal-walk-deferred-for-cleanup"
                : "completion-at-or-after-deadline-deferred";
            return new(timeout.Accepted, timeout.Accepted ? reason : $"observer-{timeout.Reason}", Snapshot());
        }

        return ProjectCompletionAndFinish(identity, completion with { OperationId = identity.OperationId }, "completed-before-deadline");
    }

    private CollectorSupervisionTransition ProjectCompletionAndFinish(
        CollectorSupervisionEvent projectionEvent,
        PendingCompletion completion,
        string acceptedReason)
    {
        // The pending-result slot is separately bounded to one. Keep it intact instead of
        // allowing the private observer to append a ninth aggregate unknown obligation.
        if (completion.Kind is SupervisionCallKind.AcquireMarker or SupervisionCallKind.AcquireSnapshot &&
            completion.Coordinate is > 0 &&
            _unknown.Count + _observer.Snapshot().UnknownReturns.Count >= SupervisionPolicy.MaximumUnknownReturns)
        {
            _unknownOverflow = true;
            _pendingCompletion = completion;
            LatchAbort(projectionEvent.MonotonicTicks);
            _state = CollectorSupervisionState.Aborted;
            return Reject("unknown-return-capacity-exhausted-before-projection");
        }

        ObserverTransitionResult observerResult = completion.Kind switch
        {
            SupervisionCallKind.AcquireMarker => _observer.CompleteAcquisition(
                ObserverResourceKind.Marker, ProjectObserverEvent(projectionEvent), completion.ApiStatus, completion.Coordinate ?? 0),
            SupervisionCallKind.AcquireSnapshot => _observer.CompleteAcquisition(
                ObserverResourceKind.Snapshot, ProjectObserverEvent(projectionEvent), completion.ApiStatus, completion.Coordinate ?? 0),
            SupervisionCallKind.Walk => _observer.CompleteWalk(ProjectObserverEvent(projectionEvent), completion.ApiStatus),
            SupervisionCallKind.ReleaseMarker => _observer.CompleteRelease(
                ObserverResourceKind.Marker, ProjectObserverEvent(projectionEvent), completion.Descriptor!, completion.ApiStatus),
            SupervisionCallKind.ReleaseSnapshot => _observer.CompleteRelease(
                ObserverResourceKind.Snapshot, ProjectObserverEvent(projectionEvent), completion.Descriptor!, completion.ApiStatus),
            _ => throw new InvalidOperationException("Unsupported completion kind."),
        };

        _inFlight = null;
        _pendingCompletion = null;
        if (!observerResult.Accepted || observerResult.State.TimeoutObserved || observerResult.State.OperationFailed ||
            observerResult.State.InvalidTransitionObserved || observerResult.State.UnknownReturnOverflow)
            LatchAbort(projectionEvent.MonotonicTicks);
        if (_abortLatched)
            _state = CollectorSupervisionState.Aborted;
        else
            _state = CollectorSupervisionState.Prepared;
        return new(observerResult.Accepted, observerResult.Accepted ? acceptedReason : $"observer-{observerResult.Reason}", Snapshot());
    }

    private ObserverTransitionResult ProjectBegin(SupervisionCallKind kind, CollectorSupervisionEvent identity) => kind switch
    {
        SupervisionCallKind.AcquireMarker => _observer.BeginAcquisition(ObserverResourceKind.Marker, ProjectObserverEvent(identity)),
        SupervisionCallKind.AcquireSnapshot => _observer.BeginAcquisition(ObserverResourceKind.Snapshot, ProjectObserverEvent(identity)),
        SupervisionCallKind.Walk => _observer.BeginWalk(ProjectObserverEvent(identity)),
        SupervisionCallKind.ReleaseMarker => BeginRelease(ObserverResourceKind.Marker, identity),
        SupervisionCallKind.ReleaseSnapshot => BeginRelease(ObserverResourceKind.Snapshot, identity),
        _ => throw new InvalidOperationException("Unsupported call kind."),
    };

    private ObserverTransitionResult BeginRelease(ObserverResourceKind kind, CollectorSupervisionEvent identity)
    {
        var state = _observer.Snapshot();
        var descriptor = kind == ObserverResourceKind.Marker ? state.Marker : state.Snapshot;
        if (descriptor is null) return new(false, "release-descriptor-absent", state);
        return _observer.BeginRelease(kind, ProjectObserverEvent(identity), descriptor);
    }

    private ObserverEventIdentity ProjectObserverEvent(CollectorSupervisionEvent source) => new(
        _binding.RunId,
        _binding.SessionId,
        _binding.Anchor,
        _binding.Attempt,
        _nextObserverSequence++,
        source.MonotonicTicks,
        source.EventId,
        source.OperationId,
        CopyIdentity(_binding.Owner),
        CopyIdentity(_binding.Controller));

    private bool TryAcceptEvent(
        CollectorSupervisionEvent identity,
        InventoryProcessIdentity expectedActor,
        EnvelopeEventClass eventClass,
        string action,
        out string failure)
    {
        failure = ValidateEvent(identity, expectedActor);
        if (failure.Length != 0)
        {
            RecordRejected(failure);
            return false;
        }
        if (!HasEventCapacity(eventClass, 1))
        {
            failure = "event-budget-exhausted";
            RecordRejected(failure);
            LatchAbort(identity.MonotonicTicks);
            if (!IsTerminalPhase(_state)) _state = CollectorSupervisionState.Aborted;
            return false;
        }

        _nextEnvelopeSequence++;
        _lastEnvelopeTicks = identity.MonotonicTicks;
        IncrementEventClass(eventClass);
        _events.Add(new(identity.Sequence, identity.MonotonicTicks, identity.EventId, identity.OperationId, action, "accepted"));
        return true;
    }

    private string ValidateEvent(CollectorSupervisionEvent? identity, InventoryProcessIdentity expectedActor)
    {
        if (identity is null || identity.EventId == Guid.Empty || identity.OperationId == Guid.Empty)
            return "event-identity-invalid";
        if (identity.RunId != _binding.RunId || identity.SessionId != _binding.SessionId ||
            identity.Anchor != _binding.Anchor || identity.Attempt != _binding.Attempt)
            return "event-binding-mismatch";
        if (identity.Sequence != _nextEnvelopeSequence || identity.MonotonicTicks <= _lastEnvelopeTicks)
            return "event-sequence-or-time-invalid";
        var requiredHorizon = Math.Max(
            Math.Max(_policy.PerCallTicks, _policy.TotalSampleTicks),
            Math.Max(_policy.LateCompletionGraceTicks, _policy.ReapObservationTicks));
        if (identity.MonotonicTicks > long.MaxValue - requiredHorizon)
            return "event-time-horizon-overflow";
        if (!SameIdentity(identity.Actor, expectedActor)) return "event-actor-identity-mismatch";
        if (!SameIdentity(identity.Issuer, _binding.Controller)) return "event-issuer-identity-mismatch";
        if (_events.Any(item => item.EventId == identity.EventId)) return "event-id-replayed";
        return string.Empty;
    }

    private CollectorSupervisionTransition PreserveRejectedReturnAndReject(
        SupervisionCallKind kind,
        CollectorSupervisionEvent identity,
        uint status,
        long coordinate,
        string reason)
    {
        if (coordinate > 0)
            PreserveUnknown(ResourceKind(kind), identity?.Actor ?? _binding.Owner, identity?.OperationId ?? Guid.Empty,
                status, coordinate, "rejected-completion", reason, false, identity?.MonotonicTicks ?? 0);
        else
            LatchAbort(_lastEnvelopeTicks);
        if (!IsTerminalPhase(_state)) _state = CollectorSupervisionState.Aborted;
        return Reject(reason);
    }

    private CollectorSupervisionTransition PreserveRejectedCompletionAndReject(
        CollectorSupervisionEvent identity,
        PendingCompletion completion,
        string reason)
    {
        var rawCoordinate = completion.Coordinate ?? completion.Descriptor?.Coordinate;
        if (rawCoordinate is > 0)
            PreserveUnknown(ResourceKind(completion.Kind), identity?.Actor ?? _binding.Owner,
                identity?.OperationId ?? Guid.Empty, completion.ApiStatus, rawCoordinate,
                "rejected-completion", reason, false, identity?.MonotonicTicks ?? completion.OriginalTicks);
        else
            LatchAbort(_lastEnvelopeTicks);
        if (!IsTerminalPhase(_state)) _state = CollectorSupervisionState.Aborted;
        return Reject(reason);
    }

    private void PreserveUnknown(
        ObserverResourceKind? kind,
        InventoryProcessIdentity observedOwner,
        Guid operationId,
        uint? status,
        long? coordinate,
        string source,
        string reason,
        bool exited,
        long ticks)
    {
        if (_unknown.Count + _observer.Snapshot().UnknownReturns.Count < SupervisionPolicy.MaximumUnknownReturns)
            _unknown.Add(new(kind, CopyIdentity(observedOwner), operationId, status, coordinate, source, reason, exited, ticks));
        else
            _unknownOverflow = true;
        LatchAbort(_lastEnvelopeTicks);
        if (!IsTerminalPhase(_state)) _state = CollectorSupervisionState.Aborted;
    }

    private void RecordRejected(string reason)
    {
        if (_rejectedEventCount < int.MaxValue) _rejectedEventCount++;
        if (_rejectedReasons.TryGetValue(reason, out var count))
            _rejectedReasons[reason] = count == int.MaxValue ? int.MaxValue : count + 1;
        else if (_rejectedReasons.Count < MaximumRejectedReasonKinds)
            _rejectedReasons.Add(reason, 1);
        else
            _rejectedSummaryOverflow = true;
    }

    private bool HasCallCapacity(SupervisionCallKind kind) => IsRelease(kind)
        ? _begunCalls < SupervisionPolicy.MaximumCalls
        : _begunCalls < SupervisionPolicy.MaximumCalls - SupervisionPolicy.ReservedReleaseCalls;

    private bool HasConfirmedExactExit() =>
        _termination is { ProcessSignaled: true, ExitCode: not null, ExitFileTime: > 0, ExitMonotonicTicks: not null } &&
        SameIdentity(_termination.Target, _binding.Owner);

    private bool HasEventCapacity(SupervisionCallKind kind, int required) =>
        HasEventCapacity(EventClass(kind), required);

    private bool HasEventCapacity(EnvelopeEventClass eventClass, int required) => eventClass switch
    {
        EnvelopeEventClass.Ordinary => _ordinaryEvents + required <= SupervisionPolicy.OrdinaryEventLimit,
        EnvelopeEventClass.Release => _releaseEvents + required <= SupervisionPolicy.ReservedReleaseEvents,
        EnvelopeEventClass.AbortAndFinal => _abortAndFinalEvents + required <= SupervisionPolicy.ReservedAbortAndFinalEvents,
        _ => false,
    };

    private void IncrementEventClass(EnvelopeEventClass eventClass)
    {
        if (eventClass == EnvelopeEventClass.Ordinary) _ordinaryEvents++;
        else if (eventClass == EnvelopeEventClass.Release) _releaseEvents++;
        else _abortAndFinalEvents++;
    }

    private bool HasPayloadCapacity(SupervisionPayloadBudget payload) =>
        payload.Rows <= SupervisionPolicy.MaximumRows - _rows &&
        payload.CopiedBytes <= SupervisionPolicy.MaximumCopiedBytes - _copiedBytes &&
        payload.EncodedBytes <= SupervisionPolicy.MaximumOrdinaryEncodedBytes - _encodedBytes;

    private static bool PayloadWithin(SupervisionPayloadBudget actual, SupervisionPayloadBudget reserved) =>
        actual.Rows <= reserved.Rows && actual.CopiedBytes <= reserved.CopiedBytes &&
        actual.EncodedBytes <= reserved.EncodedBytes && actual.MaximumFrameBytes <= reserved.MaximumFrameBytes;

    private static bool ValidatePayload(SupervisionPayloadBudget payload) =>
        payload.Rows is >= 0 and <= SupervisionPolicy.MaximumRows &&
        payload.CopiedBytes is >= 0 and <= SupervisionPolicy.MaximumCopiedBytes &&
        payload.EncodedBytes is >= 0 and <= SupervisionPolicy.MaximumEncodedBytes &&
        payload.MaximumFrameBytes is >= 0 and <= SupervisionPolicy.MaximumFrameBytes;

    private void CommitPayload(SupervisionPayloadBudget payload)
    {
        _rows += payload.Rows;
        _copiedBytes += payload.CopiedBytes;
        _encodedBytes += payload.EncodedBytes;
    }

    private bool HasUnknownCustody()
    {
        var observer = _observer.Snapshot();
        return _unknown.Count > 0 || _unknownOverflow || observer.UnknownReturns.Count > 0 ||
            observer.UnknownReturnOverflow || observer.MarkerState == ObserverResourceState.UnknownCustody ||
            observer.SnapshotState == ObserverResourceState.UnknownCustody ||
            observer.MarkerState is ObserverResourceState.Acquiring or ObserverResourceState.Acquired or ObserverResourceState.Releasing ||
            observer.SnapshotState is ObserverResourceState.Acquiring or ObserverResourceState.Acquired or ObserverResourceState.Releasing ||
            observer.Marker?.CleanupOnly == true || observer.Snapshot?.CleanupOnly == true;
    }

    private CollectorSupervisionTransition AbortAccepted(CollectorSupervisionEvent identity, string reason)
    {
        LatchAbort(identity.MonotonicTicks);
        _state = CollectorSupervisionState.Aborted;
        if (_events.Count > 0)
            _events[^1] = _events[^1] with { Reason = reason };
        return new(false, reason, Snapshot());
    }

    private CollectorSupervisionTransition AbortWithoutEvent(string reason)
    {
        LatchAbort(_lastEnvelopeTicks);
        _state = CollectorSupervisionState.Aborted;
        RecordRejected(reason);
        return Reject(reason);
    }

    private CollectorSupervisionTransition Reject(string reason) => new(false, reason, Snapshot());
    private CollectorSupervisionTransition Accept(string reason) => new(true, reason, Snapshot());

    private CollectorSupervisionTransition RejectTerminalEvent(string reason)
    {
        RecordRejected(reason);
        return Reject(reason);
    }

    private void LatchAbort(long ticks)
    {
        _abortLatched = true;
        if (ticks >= 0 && (_abortObservedTicks is null || ticks < _abortObservedTicks))
            _abortObservedTicks = ticks;
    }

    private InventoryProcessIdentity ExpectedActor(SupervisionCallKind _) => _binding.Owner;

    private static bool IsRelease(SupervisionCallKind kind) =>
        kind is SupervisionCallKind.ReleaseMarker or SupervisionCallKind.ReleaseSnapshot;

    private static bool IsTerminalPhase(CollectorSupervisionState state) => state is
        CollectorSupervisionState.TerminationRequested or CollectorSupervisionState.ExactExited or
        CollectorSupervisionState.ReapedWithUnknownResources or CollectorSupervisionState.CleanupUnresolved;

    private static EnvelopeEventClass EventClass(SupervisionCallKind kind) =>
        IsRelease(kind) ? EnvelopeEventClass.Release : EnvelopeEventClass.Ordinary;

    private static ObserverResourceKind? ResourceKind(SupervisionCallKind kind) => kind switch
    {
        SupervisionCallKind.AcquireMarker or SupervisionCallKind.ReleaseMarker => ObserverResourceKind.Marker,
        SupervisionCallKind.AcquireSnapshot or SupervisionCallKind.ReleaseSnapshot => ObserverResourceKind.Snapshot,
        _ => null,
    };

    private static long CheckedAdd(long left, long right)
    {
        if (left < 0 || right <= 0) throw new ArgumentOutOfRangeException(nameof(left));
        return checked(left + right);
    }

    private static void ValidateBinding(CollectorSupervisionBinding binding)
    {
        if (binding.RunId == Guid.Empty || binding.SessionId == Guid.Empty || binding.RunId == binding.SessionId ||
            !Enum.IsDefined(binding.Anchor) || binding.Attempt is < 1 or > 3 || binding.Policy is null)
            throw new ArgumentException("Supervision binding is invalid.", nameof(binding));
        ValidateIdentity(binding.Controller, InventoryRole.Controller, nameof(binding.Controller));
        ValidateIdentity(binding.Owner, InventoryRole.Owner, nameof(binding.Owner));
        ValidateIdentity(binding.Writer, InventoryRole.Writer, nameof(binding.Writer));
        if (binding.Controller.ProcessId == binding.Owner.ProcessId || binding.Controller.ProcessId == binding.Writer.ProcessId ||
            binding.Owner.ProcessId == binding.Writer.ProcessId)
            throw new ArgumentException("P/O/W process identities must be distinct.", nameof(binding));
        if (binding.Target is null || (!SameIdentity(binding.Target, binding.Owner) && !SameIdentity(binding.Target, binding.Writer)))
            throw new ArgumentException("Only exact O or W diagnostic targets are proposed.", nameof(binding));
        if (binding.SelectedResource is not ("trusted-parent" or "private-root" or "frames" or "anchor"))
            throw new ArgumentException("The selected resource is not canonical.", nameof(binding));
        var expectedTargetContext = binding.Target.Role == InventoryRole.Owner
            ? SupervisionTargetContext.OwnerCurrentProcess
            : SupervisionTargetContext.OwnerOriginalWriterSlot;
        if (binding.TargetContext != expectedTargetContext)
            throw new ArgumentException("The target context does not match the exact target identity.", nameof(binding));
        var expectedTargetSlot = binding.TargetContext == SupervisionTargetContext.OwnerCurrentProcess
            ? "o:self-current-process"
            : "o:w-process-original";
        if (!string.Equals(binding.TargetSlotId, expectedTargetSlot, StringComparison.Ordinal))
            throw new ArgumentException("The target slot does not match the selected exact target.", nameof(binding));
        if (binding.RequiredCustody is null || binding.RequiredCustody.Count is 0 or > 64 ||
            binding.RequiredCustody.Any(slot => slot is null))
            throw new ArgumentException("Required custody is missing.", nameof(binding));
        if (binding.RequiredCustody.Any(slot => !ValidSlot(slot)))
            throw new ArgumentException("Required custody is malformed.", nameof(binding));
        var copied = CopySlots(binding.RequiredCustody);
        var requiredIds = RequiredSlotIds(binding);
        if (copied.Select(item => item.LogicalSlotId).Distinct(StringComparer.Ordinal).Count() != copied.Length ||
            copied.GroupBy(item => (item.Holder.Role, item.Holder.ProcessId, item.Holder.CreationFileTime, item.Coordinate))
                .Any(group => group.Count() != 1) ||
            requiredIds.Any(required => !copied.Any(item => item.LogicalSlotId == required)) ||
            copied.Any(item => !requiredIds.Contains(item.LogicalSlotId, StringComparer.Ordinal)))
            throw new ArgumentException("Required exact supervisor/target slots are incomplete or duplicated.", nameof(binding));
        foreach (var slot in copied)
        {
            ValidateSlot(slot);
            ValidateCanonicalSlot(slot, binding);
        }
        _ = SupervisionPolicy.FromFrequency(binding.Policy.MonotonicFrequency);
        if (binding.Policy != SupervisionPolicy.FromFrequency(binding.Policy.MonotonicFrequency))
            throw new ArgumentException("Policy windows must be the fixed 5/15/5/10 second proposal.", nameof(binding));
    }

    private static void ValidateSlot(SupervisionCapabilitySlot slot)
    {
        if (!ValidSlot(slot))
            throw new ArgumentException("A required exact capability slot is malformed.", nameof(slot));
    }

    private static bool ValidSlot(SupervisionCapabilitySlot? slot)
    {
        if (slot is null || string.IsNullOrWhiteSpace(slot.LogicalSlotId) ||
            Encoding.UTF8.GetByteCount(slot.LogicalSlotId) > 256 || slot.ObjectIdentity is null ||
            string.IsNullOrWhiteSpace(slot.ObjectIdentity.StableId) ||
            Encoding.UTF8.GetByteCount(slot.ObjectIdentity.StableId) > 1024 ||
            !BoundedObjectStrings(slot.ObjectIdentity) ||
            slot.CoordinateKind != InventoryCoordinateKind.Owned || slot.Coordinate is null or <= 0 ||
            !Enum.IsDefined(slot.Creator) || (slot.InvokingDuplicator is { } invoking && !Enum.IsDefined(invoking)) ||
            (slot.Target is { } target && !Enum.IsDefined(target)) || !Enum.IsDefined(slot.ObjectClass) ||
            !Enum.IsDefined(slot.Provenance) ||
            slot.Provenance is InventoryMeasurementProvenance.Missing or InventoryMeasurementProvenance.QueryFailed or
                InventoryMeasurementProvenance.Unsupported || slot.Inheritable is null || slot.Inheritable.Value ||
            slot.RequestedAccess is null or 0 || slot.GrantedAccess is null or 0 ||
            slot.RequestedAccess != slot.GrantedAccess || slot.Holder is null || slot.CoordinateOwner is null)
            return false;
        try
        {
            ValidateIdentity(slot.Holder, slot.Holder.Role, nameof(slot.Holder));
            ValidateIdentity(slot.CoordinateOwner, slot.CoordinateOwner.Role, nameof(slot.CoordinateOwner));
        }
        catch (ArgumentException)
        {
            return false;
        }
        return true;
    }

    private static bool BoundedObjectStrings(InventoryObjectIdentity identity)
    {
        foreach (var value in new[]
                 { identity.Resource, identity.Name, identity.FinalPath, identity.FileId, identity.JobSlot })
        {
            if (value is not null && Encoding.UTF8.GetByteCount(value) > 64 * 1024)
                return false;
        }
        return true;
    }

    private static IReadOnlyList<string> RequiredSlotIds(CollectorSupervisionBinding binding)
    {
        var required = new List<string>(RequiredSupervisorSlots)
        {
            "p:fixture-base",
            "p:run-root",
            "p:trusted-parent",
        };
        if (binding.Anchor == InventoryAnchor.B2 && binding.SelectedResource != "trusted-parent")
            required.AddRange(["p:private-root", "p:frames", "p:anchor"]);
        if (binding.Target.Role == InventoryRole.Writer)
            required.Add("o:w-process-original");
        return required;
    }

    private static void ValidateCanonicalSlot(SupervisionCapabilitySlot slot, CollectorSupervisionBinding binding)
    {
        var p = binding.Controller;
        var o = binding.Owner;
        var w = binding.Writer;
        var id = slot.LogicalSlotId;
        var expectedHolder = id == "o:w-process-original" ? o : p;
        var expectedOwner = expectedHolder;
        if (!SameIdentity(slot.Holder, expectedHolder) || !SameIdentity(slot.CoordinateOwner, expectedOwner))
            throw new ArgumentException("A capability slot is not owned in its canonical process.", nameof(binding));

        var valid = id switch
        {
            "p:o-process-original" => slot.Creator == InventoryRole.Controller && slot.InvokingDuplicator is null &&
                slot.Target == InventoryRole.Owner && slot.ObjectClass == InventoryObjectClass.Process &&
                HasAccess(slot, 0x00101001) && ExactProcessObject(slot.ObjectIdentity, o),
            "p:o-primary-thread-original" => slot.Creator == InventoryRole.Controller && slot.InvokingDuplicator is null &&
                slot.Target == InventoryRole.Owner && slot.ObjectClass == InventoryObjectClass.Thread &&
                HasAccess(slot, 0x00100040) && ExactProcessObject(slot.ObjectIdentity, o) && slot.ObjectIdentity.ThreadId is > 0,
            "p:outer-job" => slot.Creator == InventoryRole.Controller && slot.InvokingDuplicator is null &&
                slot.Target == InventoryRole.Owner && slot.ObjectClass == InventoryObjectClass.Job &&
                HasAccess(slot, 0x0010000C) && !string.IsNullOrWhiteSpace(slot.ObjectIdentity.JobSlot),
            "p:w-process-retained" => slot.Creator == InventoryRole.Owner && slot.InvokingDuplicator == InventoryRole.Controller &&
                slot.Target == InventoryRole.Controller && slot.ObjectClass == InventoryObjectClass.Process &&
                slot.RequestedAccess == 0x00101041 && ExactProcessObject(slot.ObjectIdentity, w),
            "o:w-process-original" => slot.Creator == InventoryRole.Owner && slot.InvokingDuplicator is null &&
                slot.Target == InventoryRole.Writer && slot.ObjectClass == InventoryObjectClass.Process &&
                HasAccess(slot, 0x00101001) && ExactProcessObject(slot.ObjectIdentity, w),
            "p:fixture-base" or "p:run-root" or "p:trusted-parent" =>
                slot.RequestedAccess == InventoryContract.WritableDirectoryAccess &&
                CanonicalCleanupObject(slot, InventoryRole.Owner, InventoryObjectClass.Directory, true),
            "p:private-root" => slot.RequestedAccess == InventoryContract.RootReadAccess &&
                CanonicalCleanupObject(slot, InventoryRole.Writer, InventoryObjectClass.Directory, true),
            "p:frames" or "p:anchor" => slot.RequestedAccess == InventoryContract.FileReadAccess &&
                CanonicalCleanupObject(slot, InventoryRole.Writer, InventoryObjectClass.File, false),
            _ => false,
        };
        if (!valid)
            throw new ArgumentException("A capability slot does not match its canonical role/object/access provenance.", nameof(binding));
    }

    private static bool CanonicalCleanupObject(
        SupervisionCapabilitySlot slot,
        InventoryRole creator,
        InventoryObjectClass objectClass,
        bool directory) =>
        slot.Creator == creator && slot.InvokingDuplicator == InventoryRole.Controller &&
        slot.Target == InventoryRole.Controller && slot.ObjectClass == objectClass &&
        slot.ObjectIdentity.VolumeSerialNumber is not null && !string.IsNullOrWhiteSpace(slot.ObjectIdentity.FileId) &&
        slot.ObjectIdentity.IsDirectory == directory && slot.ObjectIdentity.IsReparsePoint == false;

    private static bool ExactProcessObject(InventoryObjectIdentity identity, InventoryProcessIdentity process) =>
        identity.TargetProcessId == process.ProcessId && identity.TargetCreationFileTime == process.CreationFileTime;

    private static bool HasAccess(SupervisionCapabilitySlot slot, uint required) =>
        (slot.GrantedAccess!.Value & required) == required;

    private static void ValidateIdentity(InventoryProcessIdentity identity, InventoryRole role, string name)
    {
        if (identity is null || identity.Role != role || identity.ProcessId <= 0 || identity.CreationFileTime <= 0)
            throw new ArgumentException("Process identity is invalid.", name);
    }

    private static bool ExactSlots(IReadOnlyList<SupervisionCapabilitySlot> expected, IReadOnlyList<SupervisionCapabilitySlot> actual)
    {
        if (expected.Count != actual.Count) return false;
        var expectedById = expected.OrderBy(item => item.LogicalSlotId, StringComparer.Ordinal).ToArray();
        var actualById = actual.OrderBy(item => item.LogicalSlotId, StringComparer.Ordinal).ToArray();
        return expectedById.SequenceEqual(actualById);
    }

    private static SupervisionCapabilitySlot[] CopySlots(IReadOnlyList<SupervisionCapabilitySlot> slots) =>
        slots.Select(slot => slot with
        {
            Holder = CopyIdentity(slot.Holder),
            CoordinateOwner = CopyIdentity(slot.CoordinateOwner),
            ObjectIdentity = slot.ObjectIdentity with { },
        }).ToArray();

    private static SupervisionUnknownObligation CopyUnknown(SupervisionUnknownObligation value) =>
        value with { ObservedOwner = CopyIdentity(value.ObservedOwner) };

    private static SupervisionPayloadBudget CopyPayload(SupervisionPayloadBudget value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value with { };
    }

    private static CollectorSupervisionBinding CopyBinding(CollectorSupervisionBinding value) => value with
    {
        Controller = CopyIdentity(value.Controller),
        Owner = CopyIdentity(value.Owner),
        Writer = CopyIdentity(value.Writer),
        Target = CopyIdentity(value.Target),
        RequiredCustody = new ReadOnlyCollection<SupervisionCapabilitySlot>(CopySlots(value.RequiredCustody)),
        Policy = value.Policy with { },
    };

    private static InventoryProcessIdentity CopyIdentity(InventoryProcessIdentity value) => value with { };
    private static bool SameIdentity(InventoryProcessIdentity? left, InventoryProcessIdentity? right) =>
        left is not null && right is not null && left == right;

    private enum EnvelopeEventClass { Ordinary, Release, AbortAndFinal }

    private sealed record InFlightCall(
        SupervisionCallKind Kind,
        Guid OperationId,
        long StartedTicks,
        long DeadlineTicks,
        SupervisionPayloadBudget ReservedPayload);

    private sealed record PendingCompletion(
        SupervisionCallKind Kind,
        long OriginalTicks,
        uint ApiStatus,
        long? Coordinate,
        ObserverDescriptor? Descriptor,
        SupervisionPayloadBudget Payload,
        Guid OperationId = default);
}
