using System.Collections.ObjectModel;
using System.Text;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal enum InMemorySupervisionOperationStage
{
    Committed,
    FirstReturnHeld,
    Rows,
    CompletionPending,
    Completed,
    Unresolved,
}

internal sealed record InMemorySuppliedFrame(
    byte[] CompleteFrame,
    Guid? ExpectedOperationId = null,
    int AnticipatedRows = 0,
    long AnticipatedCopiedBytes = 0);

internal sealed record InMemorySupervisionAttempt(
    int Ordinal,
    string Action,
    bool Accepted,
    string Reason,
    long? SuppliedPTicks,
    Guid? OperationId);

internal sealed record InMemorySupervisionOperationSnapshot(
    Guid OperationId,
    SupervisionCallKind Kind,
    InMemorySupervisionOperationStage Stage,
    long CommitmentTicks,
    long DeadlineTicks,
    DiagnosticEnvelopeHeader CommandHeader,
    DiagnosticEnvelopeHeader? OriginalReturnHeader,
    DiagnosticFirstRawReturn? OriginalReturn,
    DiagnosticEnvelopeHeader? OriginalDeliveryHeader,
    ObserverDescriptor? CleanupDescriptor,
    int Rows,
    long CopiedBytes,
    long CompletionEncodedBytes,
    int MaximumFrameBytes,
    bool ReturnReservationHeld,
    bool DeliveryReservationHeld,
    string Ticket);

internal sealed record InMemorySupervisionAccountingSnapshot(
    int OrdinaryActualEvents,
    int OrdinaryHeldEvents,
    int InspectionFramesAvailable,
    long OrdinaryActualBytes,
    long OrdinaryHeldBytes,
    long InspectionBytesAvailable,
    int FreeActualEvents,
    int FreeHeldEvents,
    long FreeActualBytes,
    long FreeHeldBytes,
    int FinalActualEvents,
    int FinalHeldEvents,
    long FinalActualBytes,
    long FinalHeldBytes,
    int Rows,
    int HeldRows,
    long CopiedBytes,
    long HeldCopiedBytes);

internal sealed record InMemoryRawUncertainty(
    Guid? OperationId,
    ObserverResourceKind? Kind,
    uint? ApiStatus,
    long? Coordinate,
    long RawObservedTicks,
    string EvidenceSource,
    string Reason);

internal sealed record InMemoryUndeliveredRawReturn(
    Guid OperationId,
    DiagnosticEnvelopeHeader OriginalHeader,
    DiagnosticFirstRawReturn OriginalReturn);

internal sealed record InMemorySupervisionMailbox(
    long AvailableTicks,
    string Reason,
    CollectorSupervisionState State,
    IReadOnlyList<SupervisionUnknownObligation> Unknowns,
    SupervisionPendingCompletionSnapshot? Pending,
    bool UnknownOverflow,
    IReadOnlyList<string> Tickets,
    SupervisionTerminationEvidence? Termination,
    SupervisionJobObservation JobObservation,
    IReadOnlyList<InMemorySupervisionOperationSnapshot> OutstandingOperations,
    IReadOnlyList<InMemoryUndeliveredRawReturn> UndeliveredRawReturns,
    IReadOnlyList<InMemoryRawUncertainty> RawUncertainties,
    bool WireSerializationSucceeded,
    string WireError,
    byte[]? WireFrame)
{
    internal bool SyntheticOnly => true;
    internal bool NativeCallsPerformed => false;
    internal bool NativeCapturePerformed => false;
    internal bool PeerAuthenticated => false;
    internal bool NativeAuthorityProven => false;
    internal bool NativeDurabilityProven => false;
    internal bool ProductionAdmissionGranted => false;
    internal bool B1WriteReleaseIssued => false;
    internal bool B1JournalWritesPerformed => false;
    internal bool ReleaseApproved => false;
}

internal sealed record InMemorySupervisionDriverSnapshot(
    bool CustodyAcknowledged,
    bool StopRequested,
    bool AdmissionClosed,
    bool StickyIncomplete,
    bool AttemptOverflow,
    bool UnknownOverflow,
    long? SampleStartedTicks,
    long? FirstAbortTicks,
    long? CleanupGraceCutoffTicks,
    long? OuterReportCutoffTicks,
    long LastSuppliedPTicks,
    long NextWireSequence,
    long NextPublicationAuxiliarySequence,
    long NextModelSequence,
    int QueuedFrames,
    long QueuedBytes,
    IReadOnlyList<InMemorySupervisionAttempt> Attempts,
    IReadOnlyList<InMemorySupervisionOperationSnapshot> Operations,
    IReadOnlyList<SupervisionUnknownObligation> Unknowns,
    IReadOnlyList<InMemoryRawUncertainty> RawUncertainties,
    IReadOnlyList<string> Tickets,
    InMemorySupervisionAccountingSnapshot Accounting,
    CollectorSupervisionSnapshot Supervision,
    DiagnosticTranscriptSnapshot PublicationTranscript,
    DiagnosticTranscriptSnapshot ValidationTranscript,
    InMemorySupervisionMailbox? Mailbox)
{
    internal bool SyntheticOnly => true;
    internal bool NativeCallsPerformed => false;
    internal bool NativeCapturePerformed => false;
    internal bool PeerAuthenticated => false;
    internal bool NativeAuthorityProven => false;
    internal bool NativeDurabilityProven => false;
    internal bool CompleteHandleInventoryProven => false;
    internal bool ZeroExtraInheritanceProven => false;
    internal bool ProductionAdmissionGranted => false;
    internal bool B1WriteReleaseIssued => false;
    internal bool B1JournalWritesPerformed => false;
    internal bool ReleaseApproved => false;
}

internal sealed record InMemorySupervisionDriverTransition(
    bool Accepted,
    string Reason,
    InMemorySupervisionDriverSnapshot State,
    byte[]? PublishedFrame = null);

// A deterministic reducer over supplied bytes and supplied P-clock observations. It deliberately
// contains no transport, timer, scheduler, process, native call, retry, or authority promotion.
internal sealed class InMemorySupervisionDriver
{
    internal const int MaximumQueuedFrames = 128;
    internal const long MaximumQueuedBytes = 8L * 1024 * 1024;
    internal const int MaximumAttemptRecords = 256;
    internal const int MaximumRetainedOperations = SupervisionPolicy.MaximumCalls;
    internal const int MaximumOrdinaryEvents = 104;
    internal const long MaximumOrdinaryBytes = 6_815_744;
    internal const int MaximumInspectionFrames = 8;
    internal const int MaximumFreeEvents = SupervisionPolicy.ReservedReleaseEvents;
    internal const int MaximumFinalEvents = SupervisionPolicy.ReservedAbortAndFinalEvents;

    private readonly DiagnosticDecodeContext _expected;
    private readonly CollectorSupervisionBinding _binding;
    private readonly CollectorSupervisionProposal _supervision;
    private readonly DiagnosticTranscript _publication;
    private readonly DiagnosticTranscript _validation;
    private readonly Queue<QueuedFrame> _queue = new();
    private readonly Dictionary<Guid, OperationState> _operations = [];
    private readonly List<InMemorySupervisionAttempt> _attempts = [];
    private readonly Dictionary<string, int> _rejected = new(StringComparer.Ordinal);
    private readonly List<SupervisionUnknownObligation> _aggregateUnknowns = [];
    private readonly HashSet<string> _aggregateUnknownKeys = new(StringComparer.Ordinal);
    private readonly List<InMemoryRawUncertainty> _rawUncertainties = [];
    private readonly List<string> _tickets = [];
    private long _queuedBytes;
    private long _lastPTicks;
    private long _nextWireSequence = 1;
    private long _nextPublicationAuxiliarySequence = 1;
    private long _nextModelSequence = 1;
    private long? _sampleStartedTicks;
    private long? _firstAbortTicks;
    private long? _cleanupGraceCutoffTicks;
    private long? _outerReportCutoffTicks;
    private bool _custodyAcknowledged;
    private bool _stopRequested;
    private bool _admissionClosed;
    private bool _stickyIncomplete;
    private bool _attemptOverflow;
    private bool _unknownOverflow;
    private bool _projectionClosed;
    private bool _dispatchClosed;
    private bool _publicationAbortFinalActivated;
    private InMemorySupervisionMailbox? _mailbox;
    private bool _mailboxWireSucceeded;
    private string _mailboxWireError = "wire-not-attempted";
    private byte[]? _mailboxWireFrame;

    private int _ordinaryActualEvents;
    private int _ordinaryHeldEvents;
    private int _inspectionFramesAvailable = MaximumInspectionFrames;
    private long _ordinaryActualBytes;
    private long _ordinaryHeldBytes;
    private int _freeActualEvents;
    private int _freeHeldEvents;
    private long _freeActualBytes;
    private long _freeHeldBytes;
    private int _finalActualEvents;
    private int _finalHeldEvents;
    private long _finalActualBytes;
    private long _finalHeldBytes;
    private int _rows;
    private int _heldRows;
    private long _copiedBytes;
    private long _heldCopiedBytes;

    internal InMemorySupervisionDriver(
        DiagnosticDecodeContext expected,
        CollectorSupervisionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(binding);
        DiagnosticValidation.ValidateDecodeContext(expected);
        if (!expected.IsFrozen)
            throw new ArgumentException("The driver requires a frozen decode context.", nameof(expected));
        ValidateComposition(expected, binding);
        _expected = DiagnosticValidation.CopyDecodeContext(expected);
        _binding = CopyBinding(binding);
        _supervision = new CollectorSupervisionProposal(binding);
        _publication = new DiagnosticTranscript(_expected);
        _validation = new DiagnosticTranscript(_expected);
    }

    internal InMemorySupervisionDriverTransition Enqueue(InMemorySuppliedFrame supplied)
    {
        ArgumentNullException.ThrowIfNull(supplied);
        if (supplied.CompleteFrame is null)
        {
            FailClosedRaw(supplied.ExpectedOperationId, null, null, null, _lastPTicks,
                "queue", "queue-frame-missing");
            return Finish(false, "queue-frame-missing", "enqueue", null, supplied.ExpectedOperationId);
        }
        var length = supplied.CompleteFrame.Length;
        if (length is < sizeof(uint) or > DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes ||
            supplied.AnticipatedRows < 0 || supplied.AnticipatedCopiedBytes < 0)
        {
            FailClosedRaw(supplied.ExpectedOperationId, null, null, null, _lastPTicks,
                "queue", "queue-frame-extent-invalid");
            return Finish(false, "queue-frame-extent-invalid", "enqueue", null, supplied.ExpectedOperationId);
        }
        if (_mailbox is not null)
            return Finish(false, "report-mailbox-already-available", "enqueue", null, supplied.ExpectedOperationId);
        if (_queue.Count >= MaximumQueuedFrames || _queuedBytes > MaximumQueuedBytes - length)
        {
            FailClosedRaw(supplied.ExpectedOperationId, null, null, null, _lastPTicks,
                "queue", "bounded-queue-overflow");
            return Finish(false, "bounded-queue-overflow", "enqueue", null, supplied.ExpectedOperationId);
        }
        _queue.Enqueue(new((byte[])supplied.CompleteFrame.Clone(), supplied.ExpectedOperationId,
            supplied.AnticipatedRows, supplied.AnticipatedCopiedBytes));
        _queuedBytes += length;
        return Finish(true, "queued", "enqueue", null, supplied.ExpectedOperationId);
    }

    internal InMemorySupervisionDriverTransition DrainNext(long suppliedPTicks)
    {
        if (!TryAcceptPClock(suppliedPTicks, out var clockError))
            return Finish(false, clockError, "drain", suppliedPTicks, null, record: false);
        if (_mailbox is not null)
            return Finish(false, "report-mailbox-already-available", "drain", suppliedPTicks, null);
        if (_queue.Count == 0)
        {
            FailClosedRaw(null, null, null, null, suppliedPTicks, "queue", "queue-empty");
            return Finish(false, "queue-empty", "drain", suppliedPTicks, null);
        }

        var queued = _queue.Dequeue();
        _queuedBytes -= queued.Frame.Length;
        if (_attemptOverflow)
        {
            FailClosedRaw(queued.ExpectedOperationId, null, null, null, suppliedPTicks,
                "queue", "ordinary-attempt-capacity-exhausted");
            return Finish(false, "ordinary-attempt-capacity-exhausted", "drain", suppliedPTicks,
                queued.ExpectedOperationId, record: false);
        }

        ParseEntitlement entitlement;
        if (!TryFundParse(queued, out entitlement, out var entitlementError))
        {
            FailClosedRaw(queued.ExpectedOperationId, null, null, null, suppliedPTicks,
                "preparse", entitlementError);
            return Finish(false, entitlementError, "drain", suppliedPTicks, queued.ExpectedOperationId);
        }

        if (!DiagnosticEnvelopeCodec.TryDecode(queued.Frame, _expected, _validation,
                out var decoded, out var decodeError, out _))
        {
            CompleteRejectedEntitlement(entitlement, queued.Frame.Length);
            FailClosedRaw(queued.ExpectedOperationId, null, null, null, suppliedPTicks,
                "decode", decodeError);
            RefreshUnknownEvidence();
            return Finish(false, decodeError, "drain", suppliedPTicks, queued.ExpectedOperationId);
        }

        if (decoded is null)
        {
            CompleteRejectedEntitlement(entitlement, queued.Frame.Length);
            FailClosedRaw(queued.ExpectedOperationId, null, null, null, suppliedPTicks,
                "decode", "decoded-envelope-missing");
            return Finish(false, "decoded-envelope-missing", "drain", suppliedPTicks,
                queued.ExpectedOperationId);
        }
        if (decoded.Header.Sequence != _nextWireSequence)
        {
            CompleteRejectedEntitlement(entitlement, queued.Frame.Length);
            FailClosedRaw(decoded.Header.OperationId, null, null, null, suppliedPTicks,
                "decode", "driver-wire-sequence-drift");
            return Finish(false, "driver-wire-sequence-drift", "drain", suppliedPTicks,
                queued.ExpectedOperationId);
        }
        _nextWireSequence++;

        _operations.TryGetValue(decoded.Header.OperationId, out var operation);

        var accepted = decoded.Kind switch
        {
            DiagnosticEnvelopeKind.CustodyAcknowledgment => AcceptCustody(decoded, entitlement,
                queued.Frame.Length, suppliedPTicks, out decodeError),
            DiagnosticEnvelopeKind.FirstRawReturn => AcceptFirstReturn(decoded, operation, entitlement,
                queued.Frame.Length, suppliedPTicks, out decodeError),
            DiagnosticEnvelopeKind.ValueRowBatch => AcceptRows(decoded, operation, entitlement,
                queued, suppliedPTicks, out decodeError),
            DiagnosticEnvelopeKind.DeliveryObservation => AcceptDelivery(decoded, operation, entitlement,
                queued.Frame.Length, suppliedPTicks, out decodeError),
            _ => RejectDecodedKind(entitlement, queued.Frame.Length, suppliedPTicks,
                "driver-inbound-kind-not-authorized", out decodeError),
        };
        RefreshUnknownEvidence();
        return Finish(accepted, decodeError, "drain", suppliedPTicks, decoded.Header.OperationId);
    }

    internal InMemorySupervisionDriverTransition CommitCommand(
        Guid operationId,
        Guid eventId,
        SupervisionCallKind kind,
        SupervisionPayloadBudget reservedPayload,
        long suppliedPTicks,
        ObserverDescriptor? cleanupDescriptor = null,
        int destinationLength = DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes)
    {
        ArgumentNullException.ThrowIfNull(reservedPayload);
        if (!TryAcceptPClock(suppliedPTicks, out var clockError))
            return Finish(false, clockError, "commit-command", suppliedPTicks, operationId, record: false);
        if (!_custodyAcknowledged)
            return Finish(false, "command-before-custody-ack", "commit-command", suppliedPTicks, operationId);
        if (_operations.ContainsKey(operationId) || operationId == Guid.Empty || eventId == Guid.Empty)
            return Finish(false, "operation-token-replayed-or-invalid", "commit-command", suppliedPTicks, operationId);
        if (!Enum.IsDefined(kind))
            return Finish(false, "command-kind-invalid", "commit-command", suppliedPTicks, operationId);
        var cleanup = IsRelease(kind);
        if (_mailbox is not null || _projectionClosed || _dispatchClosed || (_admissionClosed && !cleanup))
            return Finish(false, "driver-admission-closed", "commit-command", suppliedPTicks, operationId);
        if ((_stopRequested || _firstAbortTicks is not null) && !cleanup)
            return Finish(false, "abort-allows-cleanup-only", "commit-command", suppliedPTicks, operationId);
        if (cleanup && cleanupDescriptor is null)
            return Finish(false, "cleanup-descriptor-required", "commit-command", suppliedPTicks, operationId);
        if (!cleanup && cleanupDescriptor is not null)
            return Finish(false, "ordinary-command-descriptor-forbidden", "commit-command", suppliedPTicks, operationId);
        if (_operations.Count >= MaximumRetainedOperations)
        {
            LatchAbort(suppliedPTicks, "retained-operation-capacity-exhausted");
            return Finish(false, "retained-operation-capacity-exhausted", "commit-command", suppliedPTicks,
                operationId);
        }

        if (!TryCalculateCommandDeadline(suppliedPTicks, cleanup, out var deadline, out var horizonError))
            return Finish(false, horizonError, "commit-command", suppliedPTicks, operationId, record: false);
        if (cleanup && _firstAbortTicks is not null &&
            (_cleanupGraceCutoffTicks is null || _outerReportCutoffTicks is null ||
             suppliedPTicks >= _cleanupGraceCutoffTicks || suppliedPTicks >= _outerReportCutoffTicks))
            return Finish(false, "cleanup-dispatch-at-or-after-cutoff", "commit-command", suppliedPTicks,
                operationId);
        if (_sampleStartedTicks is { } sampleStarted && _firstAbortTicks is null &&
            suppliedPTicks >= CheckedAdd(sampleStarted, _binding.Policy.TotalSampleTicks))
        {
            LatchAbort(suppliedPTicks, "dispatch-at-or-after-sample-cutoff");
            return Finish(false, "dispatch-at-or-after-sample-cutoff", "commit-command", suppliedPTicks,
                operationId);
        }
        if (!TryReserveOperationCapacity(cleanup, reservedPayload, out var capacityError))
            return Finish(false, capacityError, "commit-command", suppliedPTicks, operationId);

        var header = Header(DiagnosticEnvelopeKind.Command, operationId, eventId, _nextWireSequence);
        var command = DiagnosticEnvelope.Create(header, new DiagnosticCommand(
            CopyIdentity(_binding.Controller), CopyIdentity(_binding.Owner), CopyIdentity(_binding.Target), kind,
            cleanupDescriptor is null ? null : CopyDescriptor(cleanupDescriptor), reservedPayload with { },
            suppliedPTicks, deadline, _expected.PClockDomain, DiagnosticProofClaims.False));
        var ticket = $"op:{operationId:N}:{(int)kind}";
        AddTicket(ticket);
        var operation = new OperationState(operationId, kind, suppliedPTicks, deadline, header,
            cleanupDescriptor is null ? null : CopyDescriptor(cleanupDescriptor), reservedPayload with { }, ticket,
            cleanup);
        _operations.Add(operationId, operation);

        DiagnosticFrameReservation? reservation;
        string publicationError;
        var publicationCommitted = cleanup
            ? _publication.TryCommitFreeCommand(command, cleanupDescriptor!, out reservation, out publicationError)
            : _publication.TryCommitCommand(command, null, out reservation, out publicationError);
        if (!publicationCommitted || reservation is null)
        {
            MarkOperationUnresolved(operation, $"publication-command-commit:{publicationError}", suppliedPTicks);
            return Finish(false, publicationError, "commit-command", suppliedPTicks, operationId);
        }
        if (!_publication.TryCreateHeldReturnAdmission(operationId, out var heldAdmission, out var admissionError) ||
            heldAdmission is null)
        {
            MarkOperationUnresolved(operation, $"held-return-admission:{admissionError}", suppliedPTicks);
            return Finish(false, admissionError, "commit-command", suppliedPTicks, operationId);
        }
        operation.HeldAdmission = heldAdmission;

        var begin = _supervision.BeginCall(kind,
            ModelEvent(operationId, eventId, suppliedPTicks, _binding.Owner), reservedPayload);
        if (!begin.Accepted)
        {
            MarkOperationUnresolved(operation, $"pure-begin:{begin.Reason}", suppliedPTicks);
            return Finish(false, begin.Reason, "commit-command", suppliedPTicks, operationId);
        }
        if (_sampleStartedTicks is null)
        {
            _sampleStartedTicks = suppliedPTicks;
            _outerReportCutoffTicks = CheckedAdd(suppliedPTicks, CheckedMultiply(_binding.Policy.MonotonicFrequency, 45));
        }

        var boundedDestination = destinationLength < 0 ? 0 : Math.Min(destinationLength,
            DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes);
        var destination = new byte[boundedDestination];
        if (!DiagnosticEnvelopeCodec.TryEncode(command, reservation, destination, out var written, out var encodeError))
        {
            MarkOperationUnresolved(operation, $"command-encode:{encodeError}", suppliedPTicks);
            return Finish(false, encodeError, "commit-command", suppliedPTicks, operationId);
        }
        var frame = destination.AsSpan(0, written).ToArray();
        ConvertCommandHoldToActual(operation, written);
        if (!DiagnosticEnvelopeCodec.TryDecode(frame, _expected, _validation,
                out var mirrored, out var mirrorError, out _) || mirrored is null)
        {
            MarkOperationUnresolved(operation, $"command-validation-mirror:{mirrorError}", suppliedPTicks);
            return Finish(false, mirrorError, "commit-command", suppliedPTicks, operationId);
        }
        _nextWireSequence++;
        return Finish(true, "command-committed", "commit-command", suppliedPTicks, operationId, frame);
    }

    internal InMemorySupervisionDriverTransition RequestStop(Guid eventId, long suppliedPTicks)
    {
        if (!TryAcceptPClock(suppliedPTicks, out var clockError))
            return Finish(false, clockError, "stop", suppliedPTicks, null, record: false);
        if (eventId == Guid.Empty)
            return Finish(false, "stop-event-id-invalid", "stop", suppliedPTicks, null);
        if (_mailbox is not null)
            return Finish(false, "report-mailbox-already-available", "stop", suppliedPTicks, null);
        if (_stopRequested)
            return Finish(false, "stop-already-requested", "stop", suppliedPTicks, null);
        _stopRequested = true;
        _admissionClosed = true;
        LatchAbort(suppliedPTicks, "supplied-stop-request");
        return Finish(true, _operations.Count == 0 ? "stop-before-commitment" : "stop-after-commitment",
            "stop", suppliedPTicks, null);
    }

    internal InMemorySupervisionDriverTransition ObserveDeadline(
        Guid operationId,
        Guid eventId,
        long suppliedPTicks)
    {
        if (!TryAcceptPClock(suppliedPTicks, out var clockError))
            return Finish(false, clockError, "deadline", suppliedPTicks, operationId, record: false);
        if (!_operations.TryGetValue(operationId, out var operation))
            return Finish(false, "deadline-operation-unknown", "deadline", suppliedPTicks, operationId);
        if (_projectionClosed || _dispatchClosed ||
            (_outerReportCutoffTicks is not null && suppliedPTicks >= _outerReportCutoffTicks))
            return Finish(false, "deadline-projection-closed-or-at-cutoff", "deadline", suppliedPTicks,
                operationId);
        if (suppliedPTicks < operation.DeadlineTicks)
            return Finish(false, "deadline-observed-too-early", "deadline", suppliedPTicks, operationId);
        var transition = _supervision.ObserveDeadline(
            ModelEvent(operationId, eventId, suppliedPTicks, _binding.Controller));
        LatchAbort(suppliedPTicks, "deadline-observed");
        return Finish(transition.Accepted, transition.Reason, "deadline", suppliedPTicks, operationId);
    }

    internal InMemorySupervisionDriverTransition DeliverPending(
        Guid operationId,
        Guid eventId,
        long suppliedPTicks)
    {
        if (!TryAcceptPClock(suppliedPTicks, out var clockError))
            return Finish(false, clockError, "deliver-pending", suppliedPTicks, operationId, record: false);
        if (!_operations.TryGetValue(operationId, out var operation) ||
            operation.Stage != InMemorySupervisionOperationStage.CompletionPending ||
            operation.DeliveryTicks is null || suppliedPTicks <= operation.DeliveryTicks)
            return Finish(false, "pending-delivery-missing-or-not-strictly-later", "deliver-pending",
                suppliedPTicks, operationId);
        if (_projectionClosed || _dispatchClosed || !BeforeCleanupCutoffs(suppliedPTicks))
            return Finish(false, "pending-delivery-at-or-after-cutoff", "deliver-pending", suppliedPTicks,
                operationId);
        if (!HasAcquisitionProjectionCapacity(operation))
        {
            MarkOperationUnresolved(operation, "unknown-return-capacity-exhausted-before-projection", suppliedPTicks);
            return Finish(false, "unknown-return-capacity-exhausted-before-projection", "deliver-pending",
                suppliedPTicks, operationId);
        }
        var transition = _supervision.DeliverPendingCompletion(
            ModelEvent(operationId, eventId, suppliedPTicks, _binding.Controller));
        operation.Stage = transition.State.PendingCompletion is null && transition.Accepted
            ? InMemorySupervisionOperationStage.Completed
            : InMemorySupervisionOperationStage.Unresolved;
        if (!transition.Accepted) LatchAbort(suppliedPTicks, $"pending-delivery:{transition.Reason}");
        return Finish(transition.Accepted, transition.Reason, "deliver-pending", suppliedPTicks, operationId);
    }

    internal InMemorySupervisionDriverTransition EscalateOwnerTermination(
        Guid operationId,
        Guid eventId,
        long suppliedPTicks,
        uint requestResult)
    {
        if (!TryAcceptPClock(suppliedPTicks, out var clockError))
            return Finish(false, clockError, "escalate", suppliedPTicks, operationId, record: false);
        if (_projectionClosed || _unknownOverflow || _dispatchClosed)
            return Finish(false, "termination-projection-closed", "escalate", suppliedPTicks, operationId);
        if (_firstAbortTicks is null || _cleanupGraceCutoffTicks is null ||
            suppliedPTicks < _cleanupGraceCutoffTicks)
            return Finish(false, "termination-before-cleanup-grace-expiry", "escalate", suppliedPTicks,
                operationId);
        if (_outerReportCutoffTicks is not null && suppliedPTicks >= _outerReportCutoffTicks)
            return Finish(false, "termination-at-or-after-report-cutoff", "escalate", suppliedPTicks,
                operationId);
        _admissionClosed = true;
        _dispatchClosed = true;
        var transition = _supervision.RequestTermination(
            ModelEvent(operationId, eventId, suppliedPTicks, _binding.Controller),
            CopyIdentity(_binding.Owner), requestResult);
        if (!transition.Accepted) _stickyIncomplete = true;
        RefreshUnknownEvidence();
        return Finish(transition.Accepted, transition.Reason, "escalate", suppliedPTicks, operationId);
    }

    internal InMemorySupervisionDriverTransition ObserveExactOwnerExit(
        Guid operationId,
        Guid eventId,
        long suppliedPTicks,
        bool processSignaled,
        uint? exitCode,
        long? exitFileTime,
        SupervisionJobObservation jobObservation)
    {
        if (!TryAcceptPClock(suppliedPTicks, out var clockError))
            return Finish(false, clockError, "owner-exit", suppliedPTicks, operationId, record: false);
        if (_projectionClosed || !_dispatchClosed ||
            (_outerReportCutoffTicks is not null && suppliedPTicks >= _outerReportCutoffTicks))
            return Finish(false, "owner-exit-projection-closed-or-outside-escalation", "owner-exit",
                suppliedPTicks, operationId);
        var transition = _supervision.RecordExactExit(
            ModelEvent(operationId, eventId, suppliedPTicks, _binding.Controller),
            CopyIdentity(_binding.Owner), processSignaled, exitCode, exitFileTime, jobObservation);
        if (!transition.Accepted) _stickyIncomplete = true;
        return Finish(transition.Accepted, transition.Reason, "owner-exit", suppliedPTicks, operationId);
    }

    internal InMemorySupervisionDriverTransition RecordOpaqueObligation(
        Guid operationId,
        Guid eventId,
        long suppliedPTicks,
        ObserverResourceKind? kind,
        long? coordinate,
        string reason,
        uint? apiStatus = null)
    {
        if (!TryAcceptPClock(suppliedPTicks, out var clockError))
            return Finish(false, clockError, "opaque-obligation", suppliedPTicks, operationId, record: false);
        if (eventId == Guid.Empty || operationId == Guid.Empty || string.IsNullOrWhiteSpace(reason) ||
            Encoding.UTF8.GetByteCount(reason) > DiagnosticValidation.MaximumTextBytes ||
            (kind is { } suppliedKind && !Enum.IsDefined(suppliedKind)))
            return Finish(false, "opaque-obligation-invalid", "opaque-obligation", suppliedPTicks, operationId);
        PreserveRawAndNormalized(operationId, kind, apiStatus, coordinate, suppliedPTicks,
            "supplied-opaque", reason);
        AddTicket($"opaque:{operationId:N}");
        LatchAbort(suppliedPTicks, reason);
        RefreshUnknownEvidence();
        return Finish(true, "opaque-obligation-retained", "opaque-obligation", suppliedPTicks, operationId);
    }

    internal InMemorySupervisionDriverTransition PublishReport(
        Guid operationId,
        Guid eventId,
        long suppliedPTicks,
        string reason,
        int destinationLength = DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes)
    {
        if (!TryAcceptPClock(suppliedPTicks, out var clockError))
            return Finish(false, clockError, "publish-report", suppliedPTicks, operationId, record: false);
        if (_mailbox is not null)
            return Finish(false, "report-mailbox-already-available", "publish-report", suppliedPTicks,
                operationId);
        if (_outerReportCutoffTicks is null || suppliedPTicks < _outerReportCutoffTicks)
            return Finish(false, "report-before-outer-cutoff", "publish-report", suppliedPTicks, operationId);
        if (operationId == Guid.Empty || eventId == Guid.Empty || string.IsNullOrWhiteSpace(reason) ||
            Encoding.UTF8.GetByteCount(reason) > DiagnosticValidation.MaximumFinalReasonBytes)
            return Finish(false, "report-input-invalid", "publish-report", suppliedPTicks, operationId);
        _admissionClosed = true;
        _dispatchClosed = true;
        RefreshUnknownEvidence();
        var supervision = _supervision.Snapshot();
        var unknowns = AggregateUnknownSnapshot();
        var pending = supervision.PendingCompletion;
        var overflow = _unknownOverflow || supervision.UnknownReturnOverflow;
        var tickets = new ReadOnlyCollection<string>(_tickets.ToArray());
        var jobObservation = supervision.Termination?.JobObservation ?? SupervisionJobObservation.NotObserved;
        var outstanding = OutstandingOperationSnapshot();
        var undelivered = UndeliveredRawReturnSnapshot();
        var raw = RawUncertaintySnapshot();
        _mailbox = new(
            suppliedPTicks,
            new string(reason.AsSpan()),
            supervision.State,
            unknowns,
            pending,
            overflow,
            tickets,
            supervision.Termination,
            jobObservation,
            outstanding,
            undelivered,
            raw,
            false,
            "wire-not-attempted",
            null);
        var wireSucceeded = false;
        var wireError = string.Empty;
        byte[]? frame = null;
        try
        {
            var body = new DiagnosticAbortExitFinalReport(
                supervision.State,
                new string(reason.AsSpan()),
                unknowns,
                pending,
                overflow,
                tickets,
                supervision.Termination,
                jobObservation,
                DiagnosticProofClaims.False);
            var envelope = DiagnosticEnvelope.Create(
                Header(DiagnosticEnvelopeKind.AbortExitFinalReport, operationId, eventId, _nextWireSequence), body);
            if (TryReserveFinalFrame(out wireError))
            {
                ActivatePublicationAbortFinal("outer-report-cutoff");
                if (_publication.TryReserveAbortFinal(envelope, out var reservation, out wireError) &&
                    reservation is not null)
                {
                    var boundedDestination = destinationLength < 0 ? 0 : Math.Min(destinationLength,
                        DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes);
                    var destination = new byte[boundedDestination];
                    if (DiagnosticEnvelopeCodec.TryEncode(envelope, reservation, destination, out var written,
                            out wireError))
                    {
                        frame = destination.AsSpan(0, written).ToArray();
                        ConvertFinalHoldToActual(written);
                        _nextWireSequence++;
                        wireSucceeded = true;
                    }
                    else
                    {
                        ConsumeFailedFinalReservation();
                    }
                }
                else
                {
                    ConsumeFailedFinalReservation();
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           OverflowException)
        {
            wireError = $"final-envelope-rejected:{exception.Message}";
        }
        _mailboxWireSucceeded = wireSucceeded;
        _mailboxWireError = wireError;
        _mailboxWireFrame = frame is null ? null : (byte[])frame.Clone();
        return Finish(true, wireSucceeded ? "report-mailbox-and-wire-available" : "report-mailbox-wire-failed",
            "publish-report", suppliedPTicks, operationId, frame);
    }

    internal InMemorySupervisionDriverSnapshot Snapshot()
    {
        RefreshUnknownEvidence();
        var attempts = new ReadOnlyCollection<InMemorySupervisionAttempt>(_attempts.Select(item => item with { }).ToArray());
        var operations = new ReadOnlyCollection<InMemorySupervisionOperationSnapshot>(_operations.Values
            .OrderBy(item => item.CommandHeader.Sequence)
            .Select(item => item.Snapshot())
            .ToArray());
        return new(
            _custodyAcknowledged,
            _stopRequested,
            _admissionClosed,
            _stickyIncomplete,
            _attemptOverflow,
            _unknownOverflow,
            _sampleStartedTicks,
            _firstAbortTicks,
            _cleanupGraceCutoffTicks,
            _outerReportCutoffTicks,
            _lastPTicks,
            _nextWireSequence,
            _nextPublicationAuxiliarySequence,
            _nextModelSequence,
            _queue.Count,
            _queuedBytes,
            attempts,
            operations,
            AggregateUnknownSnapshot(),
            RawUncertaintySnapshot(),
            new ReadOnlyCollection<string>(_tickets.Select(item => new string(item.AsSpan())).ToArray()),
            AccountingSnapshot(),
            _supervision.Snapshot(),
            _publication.Snapshot(),
            _validation.Snapshot(),
            MailboxSnapshot());
    }

    private bool AcceptCustody(
        DiagnosticEnvelope envelope,
        ParseEntitlement entitlement,
        int actualBytes,
        long suppliedPTicks,
        out string reason)
    {
        if (_admissionClosed || _custodyAcknowledged ||
            envelope.Body is not DiagnosticCustodyAcknowledgment acknowledgment)
            return RejectAcceptedEnvelope(entitlement, actualBytes, suppliedPTicks,
                "custody-ack-not-one-use", out reason);
        var transition = _supervision.AcknowledgeCustody(acknowledgment.AcknowledgedCustody);
        if (!transition.Accepted)
            return RejectAcceptedEnvelope(entitlement, actualBytes, suppliedPTicks, transition.Reason, out reason);
        CompleteAcceptedEntitlement(entitlement, actualBytes, 0, 0);
        _custodyAcknowledged = true;
        reason = "custody-acknowledged";
        return true;
    }

    private bool AcceptFirstReturn(
        DiagnosticEnvelope envelope,
        OperationState? operation,
        ParseEntitlement entitlement,
        int actualBytes,
        long suppliedPTicks,
        out string reason)
    {
        if (operation is null || envelope.Body is not DiagnosticFirstRawReturn result ||
            envelope.Header.OperationId != operation.OperationId ||
            operation.Stage != InMemorySupervisionOperationStage.Committed ||
            operation.HeldAdmission is null)
            return RejectAcceptedEnvelope(entitlement, actualBytes, suppliedPTicks,
                "first-return-driver-correlation-invalid", out reason);

        operation.OriginalReturnHeader = DiagnosticValidation.CopyHeader(envelope.Header);
        operation.OriginalReturn = (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(
            DiagnosticEnvelopeKind.FirstRawReturn, result);
        if (_projectionClosed || _unknownOverflow || _dispatchClosed ||
            (_outerReportCutoffTicks is not null && suppliedPTicks >= _outerReportCutoffTicks) ||
            (_firstAbortTicks is not null && !BeforeCleanupCutoffs(suppliedPTicks)))
        {
            CompleteAcceptedEntitlement(entitlement, actualBytes, 0, 0);
            MarkOperationUnresolved(operation, "first-return-outside-projection-horizon", suppliedPTicks);
            reason = "first-return-outside-projection-horizon";
            return false;
        }

        var heldAdmission = operation.HeldAdmission!;
        var publicationInspectionCharged = entitlement.Kind == EntitlementKind.Inspection;
        if (publicationInspectionCharged &&
            !_publication.TryBeginHeldInspection(heldAdmission, actualBytes))
        {
            CompleteRejectedEntitlement(entitlement, actualBytes);
            MarkOperationUnresolved(operation, "publication-inspection-unavailable", suppliedPTicks);
            reason = "publication-inspection-unavailable";
            return false;
        }
        var auxiliaryHeader = envelope.Header with
        {
            Sequence = _nextPublicationAuxiliarySequence,
        };
        var auxiliary = DiagnosticEnvelope.Create(auxiliaryHeader, result);
        if (!_publication.RecordDecodedHeldReturn(auxiliary, actualBytes, heldAdmission,
                publicationInspectionCharged, out _, out var projectionError))
        {
            if (publicationInspectionCharged)
                _publication.CompleteRejectedHeldInspection(heldAdmission);
            CompleteRejectedEntitlement(entitlement, actualBytes);
            MarkOperationUnresolved(operation, $"held-return-projection:{projectionError}", suppliedPTicks);
            reason = projectionError;
            return false;
        }
        _nextPublicationAuxiliarySequence++;
        TransferParseToReturnHold(entitlement, operation, actualBytes);
        operation.Stage = InMemorySupervisionOperationStage.FirstReturnHeld;
        operation.CompletionEncodedBytes = actualBytes;
        operation.MaximumFrameBytes = actualBytes;
        if (operation.Kind is SupervisionCallKind.AcquireMarker or SupervisionCallKind.AcquireSnapshot &&
            (result.PositiveCoordinates.Count > 1 ||
             (result.ApiStatus == ObserverResourceLifecycle.Success &&
              (result.PositiveCoordinates.Count != 1 || result.PositiveCoordinates[0] <= 0))))
        {
            foreach (var coordinate in result.PositiveCoordinates)
                PreserveRawAndNormalized(operation.OperationId, ResourceKind(operation.Kind), result.ApiStatus,
                    coordinate, result.OwnerObservedTicks, "raw-first-return", "unsafe-acquisition-coordinate-set");
            if (result.PositiveCoordinates.Count == 0)
                PreserveRawAndNormalized(operation.OperationId, ResourceKind(operation.Kind), result.ApiStatus,
                    null, result.OwnerObservedTicks, "raw-first-return", "missing-acquisition-coordinate");
            MarkOperationUnresolved(operation, "unsafe-acquisition-coordinate-set", suppliedPTicks);
            reason = "unsafe-acquisition-coordinate-set";
            return false;
        }
        if ((operation.Kind == SupervisionCallKind.Walk && result.PositiveCoordinates.Count != 0) ||
            (IsRelease(operation.Kind) && result.PositiveCoordinates.Any(coordinate =>
                coordinate != result.Descriptor?.Coordinate)))
        {
            foreach (var coordinate in result.PositiveCoordinates)
                PreserveRawAndNormalized(operation.OperationId, ResourceKind(operation.Kind), result.ApiStatus,
                    coordinate, result.OwnerObservedTicks, "raw-first-return", "unexpected-return-coordinate");
            MarkOperationUnresolved(operation, "unexpected-return-coordinate", suppliedPTicks);
            reason = "unexpected-return-coordinate";
            return false;
        }
        reason = "first-return-held";
        return true;
    }

    private bool AcceptRows(
        DiagnosticEnvelope envelope,
        OperationState? operation,
        ParseEntitlement entitlement,
        QueuedFrame queued,
        long suppliedPTicks,
        out string reason)
    {
        if (operation is null || envelope.Body is not DiagnosticValueRowBatch batch ||
            envelope.Header.OperationId != operation.OperationId ||
            operation.Kind != SupervisionCallKind.Walk ||
            operation.Stage is not (InMemorySupervisionOperationStage.FirstReturnHeld or
                InMemorySupervisionOperationStage.Rows) || _firstAbortTicks is not null)
            return RejectAcceptedEnvelope(entitlement, queued.Frame.Length, suppliedPTicks,
                "row-driver-correlation-invalid", out reason);
        if (_projectionClosed || _unknownOverflow || _dispatchClosed ||
            (_outerReportCutoffTicks is not null && suppliedPTicks >= _outerReportCutoffTicks))
        {
            CompleteAcceptedEntitlement(entitlement, queued.Frame.Length, 0, 0);
            MarkOperationUnresolved(operation, "rows-outside-projection-horizon", suppliedPTicks);
            reason = "rows-outside-projection-horizon";
            return false;
        }
        var copiedBytes = CopiedBytes(batch.Rows);
        if (batch.Rows.Count > queued.AnticipatedRows || copiedBytes > queued.AnticipatedCopiedBytes)
            return RejectAcceptedEnvelope(entitlement, queued.Frame.Length, suppliedPTicks,
                "row-payload-exceeded-supplied-reservation", out reason);
        CompleteAcceptedEntitlement(entitlement, queued.Frame.Length, batch.Rows.Count, copiedBytes);
        foreach (var row in batch.Rows)
            operation.Rows.Add(new DiagnosticValueRow(row.Ordinal, new string(row.Value.AsSpan())));
        operation.CopiedBytes = checked(operation.CopiedBytes + copiedBytes);
        operation.CompletionEncodedBytes = checked(operation.CompletionEncodedBytes + queued.Frame.Length);
        operation.MaximumFrameBytes = Math.Max(operation.MaximumFrameBytes, queued.Frame.Length);
        operation.RowsFinal = batch.IsFinal;
        operation.Stage = InMemorySupervisionOperationStage.Rows;
        reason = "row-batch-retained";
        return true;
    }

    private bool AcceptDelivery(
        DiagnosticEnvelope envelope,
        OperationState? operation,
        ParseEntitlement entitlement,
        int actualBytes,
        long suppliedPTicks,
        out string reason)
    {
        if (operation is null || envelope.Body is not DiagnosticDeliveryObservation delivery ||
            envelope.Header.OperationId != operation.OperationId || !delivery.Delivered ||
            delivery.IngestionTicks != suppliedPTicks || delivery.IngestionClockDomain != _expected.PClockDomain ||
            operation.OriginalReturn is null ||
            operation.Stage is not (InMemorySupervisionOperationStage.FirstReturnHeld or
                InMemorySupervisionOperationStage.Rows))
            return RejectAcceptedEnvelope(entitlement, actualBytes, suppliedPTicks,
                "delivery-driver-correlation-invalid", out reason);
        operation.OriginalDeliveryHeader = DiagnosticValidation.CopyHeader(envelope.Header);
        operation.DeliveryTicks = suppliedPTicks;
        operation.CompletionEncodedBytes = checked(operation.CompletionEncodedBytes + actualBytes);
        operation.MaximumFrameBytes = Math.Max(operation.MaximumFrameBytes, actualBytes);
        TransferParseToDeliveryHold(entitlement, operation, actualBytes);
        var release = IsRelease(operation.Kind);
        var modelTimedOut = _supervision.Snapshot().Observer.TimeoutObserved;
        if (_projectionClosed || _unknownOverflow || _dispatchClosed ||
            (_outerReportCutoffTicks is not null && suppliedPTicks >= _outerReportCutoffTicks) ||
            (_firstAbortTicks is not null && !BeforeCleanupCutoffs(suppliedPTicks)) ||
            (_stopRequested && !release && !modelTimedOut))
        {
            MarkOperationUnresolved(operation, _stopRequested && !release && !modelTimedOut
                ? "stopped-nonrelease-delivery-without-model-timeout"
                : "delivery-projection-closed-or-outside-horizon", suppliedPTicks);
            reason = _stopRequested && !release && !modelTimedOut
                ? "stopped-nonrelease-delivery-without-model-timeout"
                : "delivery-projection-closed-or-outside-horizon";
            return false;
        }
        if (!HasAcquisitionProjectionCapacity(operation))
            return MarkDeliveryUnresolved(operation, "unknown-return-capacity-exhausted-before-projection",
                suppliedPTicks, out reason);
        var result = operation.OriginalReturn;
        CollectorSupervisionTransition transition;
        var modelIdentity = ModelEvent(operation.OperationId, envelope.Header.EventId, suppliedPTicks, _binding.Owner);
        switch (operation.Kind)
        {
            case SupervisionCallKind.AcquireMarker:
            case SupervisionCallKind.AcquireSnapshot:
                var coordinate = result.PositiveCoordinates.Count > 0 ? result.PositiveCoordinates[0] : 0;
                transition = _supervision.CompleteAcquisition(operation.Kind, modelIdentity,
                    result.ApiStatus, coordinate, CompletionPayload(operation));
                break;
            case SupervisionCallKind.Walk:
                transition = _supervision.CompleteWalk(modelIdentity, result.ApiStatus, CompletionPayload(operation));
                break;
            case SupervisionCallKind.ReleaseMarker:
            case SupervisionCallKind.ReleaseSnapshot:
                if (result.Descriptor is null)
                    return MarkDeliveryUnresolved(operation, "release-return-descriptor-missing", suppliedPTicks,
                        out reason);
                transition = _supervision.CompleteRelease(operation.Kind, modelIdentity,
                    result.Descriptor, result.ApiStatus);
                break;
            default:
                return MarkDeliveryUnresolved(operation, "delivery-kind-invalid", suppliedPTicks, out reason);
        }
        if (transition.State.PendingCompletion is not null)
        {
            operation.Stage = InMemorySupervisionOperationStage.CompletionPending;
            LatchAbort(suppliedPTicks, transition.Reason);
        }
        else if (transition.Accepted)
        {
            operation.Stage = InMemorySupervisionOperationStage.Completed;
        }
        else
        {
            operation.Stage = InMemorySupervisionOperationStage.Unresolved;
            LatchAbort(suppliedPTicks, transition.Reason);
        }
        reason = transition.Reason;
        return transition.Accepted;
    }

    private bool MarkDeliveryUnresolved(
        OperationState operation,
        string failure,
        long suppliedPTicks,
        out string reason)
    {
        MarkOperationUnresolved(operation, failure, suppliedPTicks);
        reason = failure;
        return false;
    }

    private bool RejectDecodedKind(
        ParseEntitlement entitlement,
        int actualBytes,
        long suppliedPTicks,
        string failure,
        out string reason) => RejectAcceptedEnvelope(entitlement, actualBytes, suppliedPTicks, failure, out reason);

    private bool RejectAcceptedEnvelope(
        ParseEntitlement entitlement,
        int actualBytes,
        long suppliedPTicks,
        string failure,
        out string reason)
    {
        CompleteRejectedEntitlement(entitlement, actualBytes);
        LatchAbort(suppliedPTicks, failure);
        reason = failure;
        return false;
    }

    private bool TryFundParse(
        QueuedFrame queued,
        out ParseEntitlement entitlement,
        out string error)
    {
        entitlement = default;
        error = string.Empty;
        if (_firstAbortTicks is not null || _stopRequested)
        {
            if (_inspectionFramesAvailable <= 0)
            {
                error = "funded-held-inspection-unavailable";
                return false;
            }
            _inspectionFramesAvailable--;
            entitlement = new(EntitlementKind.Inspection, null, 0, 0);
            return true;
        }
        if (!TryReserveOrdinary(1, DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes,
                queued.AnticipatedRows, queued.AnticipatedCopiedBytes, out error)) return false;
        entitlement = new(EntitlementKind.OrdinaryDynamic, null, queued.AnticipatedRows,
            queued.AnticipatedCopiedBytes);
        return true;
    }

    private bool TryReserveOperationCapacity(
        bool cleanup,
        SupervisionPayloadBudget reservedPayload,
        out string error)
    {
        if (cleanup)
        {
            if (_freeActualEvents + _freeHeldEvents + 2 > MaximumFreeEvents ||
                _freeActualBytes + _freeHeldBytes + 2L * DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes >
                MaximumFreeEvents * (long)DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes)
            {
                error = "protected-cleanup-capacity-exhausted";
                return false;
            }
            if (!TryReserveOrdinary(1, DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes, 0, 0, out error))
                return false;
            _freeHeldEvents += 2;
            _freeHeldBytes += 2L * DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
            error = string.Empty;
            return true;
        }
        return TryReserveOrdinary(3, 3L * DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes, 0, 0, out error);
    }

    private bool TryReserveOrdinary(
        int events,
        long bytes,
        int rows,
        long copiedBytes,
        out string error)
    {
        if (events < 0 || bytes < 0 || rows < 0 || copiedBytes < 0 ||
            events > MaximumOrdinaryEvents - _ordinaryActualEvents - _ordinaryHeldEvents - _inspectionFramesAvailable ||
            bytes > MaximumOrdinaryBytes - _ordinaryActualBytes - _ordinaryHeldBytes -
            _inspectionFramesAvailable * (long)DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes ||
            rows > SupervisionPolicy.MaximumRows - _rows - _heldRows ||
            copiedBytes > SupervisionPolicy.MaximumCopiedBytes - _copiedBytes - _heldCopiedBytes)
        {
            _admissionClosed = true;
            _stickyIncomplete = true;
            error = "driver-ordinary-capacity-exhausted";
            return false;
        }
        _ordinaryHeldEvents += events;
        _ordinaryHeldBytes += bytes;
        _heldRows += rows;
        _heldCopiedBytes += copiedBytes;
        error = string.Empty;
        return true;
    }

    private void ConvertCommandHoldToActual(OperationState operation, int actualBytes)
    {
        if (operation.Cleanup)
        {
            _freeHeldEvents--;
            _freeHeldBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
            _freeActualEvents++;
            _freeActualBytes += actualBytes;
        }
        else
        {
            _ordinaryHeldEvents--;
            _ordinaryHeldBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
            _ordinaryActualEvents++;
            _ordinaryActualBytes += actualBytes;
        }
    }

    private void CompleteAcceptedEntitlement(ParseEntitlement entitlement, int actualBytes, int rows, long copiedBytes)
    {
        switch (entitlement.Kind)
        {
            case EntitlementKind.OrdinaryDynamic:
                _ordinaryHeldEvents--;
                _ordinaryHeldBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
                _heldRows -= entitlement.ReservedRows;
                _heldCopiedBytes -= entitlement.ReservedCopiedBytes;
                _ordinaryActualEvents++;
                _ordinaryActualBytes += actualBytes;
                _rows += rows;
                _copiedBytes += copiedBytes;
                break;
            case EntitlementKind.Inspection:
                _ordinaryActualEvents++;
                _ordinaryActualBytes += actualBytes;
                _rows += rows;
                _copiedBytes += copiedBytes;
                break;
            default: throw new InvalidOperationException("Unknown parse entitlement.");
        }
    }

    private void CompleteRejectedEntitlement(ParseEntitlement entitlement, int actualBytes)
    {
        switch (entitlement.Kind)
        {
            case EntitlementKind.OrdinaryDynamic:
                _ordinaryHeldEvents--;
                _ordinaryHeldBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
                _heldRows -= entitlement.ReservedRows;
                _heldCopiedBytes -= entitlement.ReservedCopiedBytes;
                _ordinaryActualEvents++;
                _ordinaryActualBytes += actualBytes;
                break;
            case EntitlementKind.Inspection:
                _ordinaryActualEvents++;
                _ordinaryActualBytes += actualBytes;
                break;
            default: throw new InvalidOperationException("Unknown parse entitlement.");
        }
        _stickyIncomplete = true;
    }

    private void TransferParseToReturnHold(
        ParseEntitlement entitlement,
        OperationState operation,
        int actualBytes)
    {
        ReleaseParseEntitlement(entitlement);
        if (operation.Cleanup) ConsumeFreeReturn(operation, actualBytes);
        else ConsumeOperationOrdinaryHold(operation, actualBytes, returned: true);
    }

    private void TransferParseToDeliveryHold(
        ParseEntitlement entitlement,
        OperationState operation,
        int actualBytes)
    {
        ReleaseParseEntitlement(entitlement);
        ConsumeOperationOrdinaryHold(operation, actualBytes, returned: false);
    }

    private void ReleaseParseEntitlement(ParseEntitlement entitlement)
    {
        switch (entitlement.Kind)
        {
            case EntitlementKind.OrdinaryDynamic:
                _ordinaryHeldEvents--;
                _ordinaryHeldBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
                _heldRows -= entitlement.ReservedRows;
                _heldCopiedBytes -= entitlement.ReservedCopiedBytes;
                break;
            case EntitlementKind.Inspection:
                _inspectionFramesAvailable++;
                break;
            default:
                throw new InvalidOperationException("Only independent parse entitlements can be transferred.");
        }
    }

    private void ConsumeOperationOrdinaryHold(OperationState operation, int actualBytes, bool returned)
    {
        _ordinaryHeldEvents--;
        _ordinaryHeldBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
        _ordinaryActualEvents++;
        _ordinaryActualBytes += actualBytes;
        if (returned) operation.ReturnReservationHeld = false;
        else operation.DeliveryReservationHeld = false;
    }

    private void ConsumeFreeReturn(OperationState operation, int actualBytes)
    {
        _freeHeldEvents--;
        _freeHeldBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
        _freeActualEvents++;
        _freeActualBytes += actualBytes;
        operation.ReturnReservationHeld = false;
    }

    private bool TryReserveFinalFrame(out string error)
    {
        if (_finalActualEvents + _finalHeldEvents + 1 > MaximumFinalEvents)
        {
            error = "driver-final-capacity-exhausted";
            return false;
        }
        _finalHeldEvents++;
        _finalHeldBytes += DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
        error = string.Empty;
        return true;
    }

    private void ConvertFinalHoldToActual(int actualBytes)
    {
        _finalHeldEvents--;
        _finalHeldBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
        _finalActualEvents++;
        _finalActualBytes += actualBytes;
    }

    private void ConsumeFailedFinalReservation()
    {
        _finalHeldEvents--;
        _finalHeldBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
        _finalActualEvents++;
        _finalActualBytes += DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
        _stickyIncomplete = true;
    }

    private bool TryCalculateCommandDeadline(
        long suppliedPTicks,
        bool cleanup,
        out long deadline,
        out string error)
    {
        deadline = 0;
        error = string.Empty;
        try
        {
            var fiveSeconds = CheckedMultiply(_binding.Policy.MonotonicFrequency, 5);
            if (cleanup && _firstAbortTicks is not null)
            {
                if (_outerReportCutoffTicks is null)
                {
                    error = "cleanup-before-abort";
                    return false;
                }
                deadline = Math.Min(CheckedAdd(_firstAbortTicks.Value, fiveSeconds),
                    _outerReportCutoffTicks.Value);
            }
            else
            {
                var sampleStart = _sampleStartedTicks ?? suppliedPTicks;
                deadline = Math.Min(CheckedAdd(suppliedPTicks, fiveSeconds),
                    CheckedAdd(sampleStart, CheckedMultiply(_binding.Policy.MonotonicFrequency, 15)));
                _ = CheckedAdd(sampleStart, CheckedMultiply(_binding.Policy.MonotonicFrequency, 45));
            }
            return true;
        }
        catch (OverflowException)
        {
            error = "command-time-horizon-overflow";
            return false;
        }
    }

    private bool TryAcceptPClock(long suppliedPTicks, out string error)
    {
        error = string.Empty;
        if (suppliedPTicks <= _lastPTicks)
        {
            error = "supplied-p-clock-not-strictly-increasing";
            return false;
        }
        try
        {
            _ = CheckedAdd(suppliedPTicks, CheckedMultiply(_binding.Policy.MonotonicFrequency, 45));
        }
        catch (OverflowException)
        {
            error = "supplied-p-clock-horizon-overflow";
            return false;
        }
        _lastPTicks = suppliedPTicks;
        return true;
    }

    private void LatchAbort(long suppliedPTicks, string reason)
    {
        _stickyIncomplete = true;
        _admissionClosed = true;
        if (_firstAbortTicks is null && suppliedPTicks > 0)
        {
            _firstAbortTicks = suppliedPTicks;
            try
            {
                _cleanupGraceCutoffTicks = CheckedAdd(suppliedPTicks,
                    CheckedMultiply(_binding.Policy.MonotonicFrequency, 5));
                var abortCutoff = CheckedAdd(suppliedPTicks,
                    CheckedMultiply(_binding.Policy.MonotonicFrequency, 30));
                _outerReportCutoffTicks = _outerReportCutoffTicks is null
                    ? abortCutoff
                    : Math.Min(_outerReportCutoffTicks.Value, abortCutoff);
            }
            catch (OverflowException)
            {
                _projectionClosed = true;
            }
        }
        ActivatePublicationAbortFinal(reason);
    }

    private void ActivatePublicationAbortFinal(string reason)
    {
        if (_publicationAbortFinalActivated) return;
        _publication.ActivateAbortFinal(BoundedReason(reason));
        _publicationAbortFinalActivated = true;
    }

    private bool BeforeCleanupCutoffs(long suppliedPTicks) =>
        _cleanupGraceCutoffTicks is not null && _outerReportCutoffTicks is not null &&
        suppliedPTicks < _cleanupGraceCutoffTicks && suppliedPTicks < _outerReportCutoffTicks;

    private void MarkOperationUnresolved(OperationState operation, string reason, long suppliedPTicks)
    {
        operation.Stage = InMemorySupervisionOperationStage.Unresolved;
        AddTicket($"unresolved:{operation.OperationId:D}:{BoundedReason(reason)}");
        LatchAbort(suppliedPTicks, reason);
    }

    private void FailClosedRaw(
        Guid? operationId,
        ObserverResourceKind? kind,
        uint? apiStatus,
        long? coordinate,
        long rawTicks,
        string source,
        string reason)
    {
        PreserveRawAndNormalized(operationId, kind, apiStatus, coordinate, rawTicks, source, reason);
        LatchAbort(rawTicks, reason);
    }

    private void PreserveRawAndNormalized(
        Guid? operationId,
        ObserverResourceKind? kind,
        uint? apiStatus,
        long? coordinate,
        long rawTicks,
        string source,
        string reason)
    {
        var boundedSource = BoundedEvidence(source);
        var boundedReason = BoundedFinalReason(reason);
        if (_rawUncertainties.Count < MaximumAttemptRecords)
        {
            _rawUncertainties.Add(new(operationId, kind, apiStatus, coordinate, rawTicks,
                boundedSource, boundedReason));
        }
        else
        {
            _unknownOverflow = true;
        }
        if (operationId is not { } exactOperation || exactOperation == Guid.Empty || rawTicks <= 0)
            return;
        var normalizedCoordinate = coordinate is > 0 ? coordinate : null;
        AppendUnknown(new SupervisionUnknownObligation(
            kind,
            CopyIdentity(_binding.Owner),
            exactOperation,
            apiStatus,
            normalizedCoordinate,
            boundedSource,
            boundedReason,
            false,
            rawTicks));
    }

    private void RefreshUnknownEvidence()
    {
        foreach (var candidate in _publication.Snapshot().UnknownObligations)
            AppendUnknown(candidate);
        foreach (var candidate in _validation.Snapshot().UnknownObligations)
            AppendUnknown(candidate);
        var supervision = _supervision.Snapshot();
        foreach (var candidate in supervision.UnknownObligations)
            AppendUnknown(candidate, derivedModelClock: true);
        foreach (var candidate in supervision.Observer.UnknownReturns.Select(item => new SupervisionUnknownObligation(
            item.Kind,
            CopyIdentity(_binding.Owner),
            item.OperationId,
            item.ApiStatus,
            item.Coordinate,
            "observer-model",
            new string(item.Reason.AsSpan()),
            false,
            _lastPTicks)))
            AppendUnknown(candidate, derivedModelClock: true);
        _unknownOverflow |= _publication.Snapshot().UnknownOverflow || _validation.Snapshot().UnknownOverflow ||
            supervision.UnknownReturnOverflow;
        if (_unknownOverflow)
        {
            _projectionClosed = true;
            _admissionClosed = true;
            _stickyIncomplete = true;
        }
    }

    private ReadOnlyCollection<SupervisionUnknownObligation> AggregateUnknownSnapshot() =>
        new(_aggregateUnknowns.Select(DiagnosticValidation.CopyUnknown).Take(SupervisionPolicy.MaximumUnknownReturns)
            .ToArray());

    private bool HasAcquisitionProjectionCapacity(OperationState operation)
    {
        if (operation.Kind is not (SupervisionCallKind.AcquireMarker or SupervisionCallKind.AcquireSnapshot) ||
            operation.OriginalReturn is not { } result || !result.PositiveCoordinates.Any(coordinate => coordinate > 0))
            return true;
        RefreshUnknownEvidence();
        if (_unknownOverflow) return false;
        var keys = result.PositiveCoordinates.Where(coordinate => coordinate > 0).Select(coordinate => UnknownKey(
            new SupervisionUnknownObligation(ResourceKind(operation.Kind), _binding.Owner, operation.OperationId,
                result.ApiStatus, coordinate, "raw-first-return", "projection-capacity", false,
                result.OwnerObservedTicks))).Distinct(StringComparer.Ordinal);
        if (keys.Count(key => !_aggregateUnknownKeys.Contains(key)) <=
            SupervisionPolicy.MaximumUnknownReturns - _aggregateUnknowns.Count)
            return true;
        _unknownOverflow = true;
        _projectionClosed = true;
        _admissionClosed = true;
        _stickyIncomplete = true;
        return false;
    }

    private void AppendUnknown(SupervisionUnknownObligation candidate, bool derivedModelClock = false)
    {
        var copy = DiagnosticValidation.CopyUnknown(candidate);
        if (copy.Kind is null && copy.ObservedOwner == _binding.Owner &&
            _operations.TryGetValue(copy.OperationId, out var operation) &&
            operation.Kind is SupervisionCallKind.AcquireMarker or SupervisionCallKind.AcquireSnapshot)
            copy = copy with { Kind = ResourceKind(operation.Kind) };
        if (derivedModelClock && copy.ObservedOwner == _binding.Owner && copy.Coordinate is { } coordinate &&
            _operations.TryGetValue(copy.OperationId, out var rawOperation) &&
            copy.Kind == ResourceKind(rawOperation.Kind) && rawOperation.OriginalReturn is { } rawReturn &&
            rawReturn.ApiStatus == copy.ApiStatus && rawReturn.PositiveCoordinates.Contains(coordinate))
            copy = copy with { ObservedMonotonicTicks = rawReturn.OwnerObservedTicks };
        var key = UnknownKey(copy);
        if (_aggregateUnknownKeys.Contains(key))
        {
            var first = _aggregateUnknowns.First(item => UnknownKey(item) == key);
            if (!derivedModelClock && first.ObservedMonotonicTicks != copy.ObservedMonotonicTicks)
                AddTicket($"clock:{copy.OperationId:N}");
            return;
        }
        if (_aggregateUnknowns.Count >= SupervisionPolicy.MaximumUnknownReturns)
        {
            _unknownOverflow = true;
            _projectionClosed = true;
            _admissionClosed = true;
            _stickyIncomplete = true;
            return;
        }
        _aggregateUnknownKeys.Add(key);
        _aggregateUnknowns.Add(copy);
    }

    private static string UnknownKey(SupervisionUnknownObligation value) =>
        $"{value.ObservedOwner.Role}:{value.ObservedOwner.ProcessId}:{value.ObservedOwner.CreationFileTime}:" +
        $"{value.OperationId:D}:{value.Kind}:{value.Coordinate}:{value.ApiStatus}";

    private void AddTicket(string ticket)
    {
        if (_tickets.Count >= 128)
        {
            _stickyIncomplete = true;
            _admissionClosed = true;
            _dispatchClosed = true;
            return;
        }
        var boundedTicket = Encoding.UTF8.GetByteCount(ticket) <= DiagnosticValidation.MaximumTicketBytes
            ? ticket
            : $"ticket-over-bound:{_tickets.Count}";
        if (!_tickets.Contains(boundedTicket, StringComparer.Ordinal))
            _tickets.Add(new string(boundedTicket.AsSpan()));
    }

    private InMemorySupervisionDriverTransition Finish(
        bool accepted,
        string reason,
        string action,
        long? suppliedPTicks,
        Guid? operationId,
        byte[]? publishedFrame = null,
        bool record = true)
    {
        if (record) RecordAttempt(action, accepted, reason, suppliedPTicks, operationId);
        if (!accepted) RecordRejected(reason);
        return new(accepted, new string(reason.AsSpan()), Snapshot(),
            publishedFrame is null ? null : (byte[])publishedFrame.Clone());
    }

    private void RecordAttempt(
        string action,
        bool accepted,
        string reason,
        long? suppliedPTicks,
        Guid? operationId)
    {
        if (_attempts.Count >= MaximumAttemptRecords)
        {
            _attemptOverflow = true;
            _stickyIncomplete = true;
            _admissionClosed = true;
            _dispatchClosed = true;
            return;
        }
        _attempts.Add(new(_attempts.Count + 1, new string(action.AsSpan()), accepted,
            BoundedReason(reason), suppliedPTicks, operationId));
    }

    private void RecordRejected(string reason)
    {
        if (_rejected.TryGetValue(reason, out var count))
            _rejected[reason] = count == int.MaxValue ? int.MaxValue : count + 1;
        else if (_rejected.Count < DiagnosticTranscript.MaximumRejectedSummaryKinds)
            _rejected.Add(new string(reason.AsSpan()), 1);
    }

    private CollectorSupervisionEvent ModelEvent(
        Guid operationId,
        Guid eventId,
        long suppliedPTicks,
        InventoryProcessIdentity actor)
    {
        var result = new CollectorSupervisionEvent(
            _binding.RunId,
            _binding.SessionId,
            _binding.Anchor,
            _binding.Attempt,
            _nextModelSequence,
            suppliedPTicks,
            eventId,
            operationId,
            CopyIdentity(actor),
            CopyIdentity(_binding.Controller));
        _nextModelSequence++;
        return result;
    }

    private DiagnosticEnvelopeHeader Header(
        DiagnosticEnvelopeKind kind,
        Guid operationId,
        Guid eventId,
        long sequence) => new(
        DiagnosticEnvelopeCodec.Schema,
        DiagnosticEnvelopeCodec.Version,
        kind,
        _expected.ContextDigest,
        _expected.Epoch,
        sequence,
        eventId,
        operationId);

    private SupervisionPayloadBudget CompletionPayload(OperationState operation) => new(
        operation.Rows.Count,
        operation.CopiedBytes,
        operation.CompletionEncodedBytes,
        operation.MaximumFrameBytes);

    private InMemorySupervisionAccountingSnapshot AccountingSnapshot() => new(
        _ordinaryActualEvents,
        _ordinaryHeldEvents,
        _inspectionFramesAvailable,
        _ordinaryActualBytes,
        _ordinaryHeldBytes,
        _inspectionFramesAvailable * (long)DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes,
        _freeActualEvents,
        _freeHeldEvents,
        _freeActualBytes,
        _freeHeldBytes,
        _finalActualEvents,
        _finalHeldEvents,
        _finalActualBytes,
        _finalHeldBytes,
        _rows,
        _heldRows,
        _copiedBytes,
        _heldCopiedBytes);

    private static long CopiedBytes(IReadOnlyList<DiagnosticValueRow> rows)
    {
        long result = 0;
        foreach (var row in rows) result = checked(result + Encoding.UTF8.GetByteCount(row.Value));
        return result;
    }

    private static string BoundedReason(string reason)
    {
        var value = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        if (Encoding.UTF8.GetByteCount(value) <= DiagnosticValidation.MaximumTextBytes)
            return new string(value.AsSpan());
        return "reason-exceeded-bound";
    }

    private static string BoundedFinalReason(string reason)
    {
        var value = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason;
        return Encoding.UTF8.GetByteCount(value) <= DiagnosticValidation.MaximumFinalReasonBytes
            ? new string(value.AsSpan())
            : "reason-exceeded-bound";
    }

    private static string BoundedEvidence(string source) =>
        string.IsNullOrWhiteSpace(source) ||
        Encoding.UTF8.GetByteCount(source) > DiagnosticValidation.MaximumEvidenceBytes
            ? "bounded-driver-evidence"
            : new string(source.AsSpan());

    private static ObserverResourceKind? ResourceKind(SupervisionCallKind kind) => kind switch
    {
        SupervisionCallKind.AcquireMarker or SupervisionCallKind.ReleaseMarker => ObserverResourceKind.Marker,
        SupervisionCallKind.AcquireSnapshot or SupervisionCallKind.ReleaseSnapshot => ObserverResourceKind.Snapshot,
        _ => null,
    };

    private static bool IsRelease(SupervisionCallKind kind) =>
        kind is SupervisionCallKind.ReleaseMarker or SupervisionCallKind.ReleaseSnapshot;

    private static long CheckedAdd(long left, long right) => checked(left + right);
    private static long CheckedMultiply(long left, long right) => checked(left * right);

    private static InventoryProcessIdentity CopyIdentity(InventoryProcessIdentity value) => value with { };

    private static ObserverDescriptor CopyDescriptor(ObserverDescriptor value) => value with
    {
        Owner = CopyIdentity(value.Owner),
    };

    private static CollectorSupervisionBinding CopyBinding(CollectorSupervisionBinding value) => value with
    {
        Controller = CopyIdentity(value.Controller),
        Owner = CopyIdentity(value.Owner),
        Writer = CopyIdentity(value.Writer),
        Target = CopyIdentity(value.Target),
        RequiredCustody = new ReadOnlyCollection<SupervisionCapabilitySlot>(value.RequiredCustody.Select(slot => slot with
        {
            Holder = CopyIdentity(slot.Holder),
            CoordinateOwner = CopyIdentity(slot.CoordinateOwner),
            ObjectIdentity = slot.ObjectIdentity with { },
        }).ToArray()),
        Policy = value.Policy with { },
    };

    private static void ValidateComposition(
        DiagnosticDecodeContext expected,
        CollectorSupervisionBinding binding)
    {
        if (expected.RunId != binding.RunId || expected.SessionId != binding.SessionId ||
            expected.Attempt != binding.Attempt || expected.Anchor != binding.Anchor ||
            expected.Controller != binding.Controller || expected.Owner != binding.Owner ||
            expected.Writer != binding.Writer || expected.Target != binding.Target ||
            expected.TargetContext != binding.TargetContext || expected.TargetSlotId != binding.TargetSlotId ||
            expected.SelectedResource != binding.SelectedResource || expected.ExpectedPolicy != binding.Policy ||
            expected.OwnerClockFrequency <= 0 || expected.ExpectedCustody is null ||
            !ExactSlots(expected.ExpectedCustody, binding.RequiredCustody))
            throw new ArgumentException("The codec context and supervision binding are not exact.", nameof(binding));
    }

    private static bool ExactSlots(
        IReadOnlyList<SupervisionCapabilitySlot> left,
        IReadOnlyList<SupervisionCapabilitySlot> right)
    {
        if (left.Count != right.Count) return false;
        for (var index = 0; index < left.Count; index++)
        {
            if (left[index] != right[index]) return false;
        }
        return true;
    }

    private ReadOnlyCollection<InMemorySupervisionOperationSnapshot> OutstandingOperationSnapshot() => new(
        _operations.Values
            .Where(item => item.Stage != InMemorySupervisionOperationStage.Completed)
            .OrderBy(item => item.CommandHeader.Sequence)
            .Select(item => item.Snapshot())
            .ToArray());

    private ReadOnlyCollection<InMemoryUndeliveredRawReturn> UndeliveredRawReturnSnapshot() => new(
        _operations.Values
            .Where(item => item.OriginalReturn is not null && item.OriginalReturnHeader is not null &&
                           item.Stage is not (InMemorySupervisionOperationStage.Completed or
                               InMemorySupervisionOperationStage.CompletionPending))
            .OrderBy(item => item.CommandHeader.Sequence)
            .Select(item => new InMemoryUndeliveredRawReturn(
                item.OperationId,
                DiagnosticValidation.CopyHeader(item.OriginalReturnHeader!),
                (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(
                    DiagnosticEnvelopeKind.FirstRawReturn, item.OriginalReturn!)))
            .ToArray());

    private ReadOnlyCollection<InMemoryRawUncertainty> RawUncertaintySnapshot() => new(
        _rawUncertainties.Select(item => item with
        {
            EvidenceSource = new string(item.EvidenceSource.AsSpan()),
            Reason = new string(item.Reason.AsSpan()),
        }).ToArray());

    private InMemorySupervisionMailbox? MailboxSnapshot() => _mailbox is null
        ? null
        : CopyMailbox(_mailbox with
        {
            WireSerializationSucceeded = _mailboxWireSucceeded,
            WireError = new string(_mailboxWireError.AsSpan()),
            WireFrame = _mailboxWireFrame is null ? null : (byte[])_mailboxWireFrame.Clone(),
        });

    private static InMemorySupervisionMailbox? CopyMailbox(InMemorySupervisionMailbox? value) => value is null
        ? null
        : value with
        {
            Unknowns = new ReadOnlyCollection<SupervisionUnknownObligation>(
                value.Unknowns.Select(DiagnosticValidation.CopyUnknown).ToArray()),
            Pending = value.Pending is null ? null : value.Pending with
            {
                Descriptor = value.Pending.Descriptor is null ? null : CopyDescriptor(value.Pending.Descriptor),
                Payload = value.Pending.Payload with { },
            },
            Tickets = new ReadOnlyCollection<string>(value.Tickets.Select(item => new string(item.AsSpan())).ToArray()),
            Termination = value.Termination is null ? null : value.Termination with
            {
                Target = CopyIdentity(value.Termination.Target),
            },
            OutstandingOperations = new ReadOnlyCollection<InMemorySupervisionOperationSnapshot>(
                value.OutstandingOperations.Select(CopyOperationSnapshot).ToArray()),
            UndeliveredRawReturns = new ReadOnlyCollection<InMemoryUndeliveredRawReturn>(
                value.UndeliveredRawReturns.Select(item => item with
                {
                    OriginalHeader = DiagnosticValidation.CopyHeader(item.OriginalHeader),
                    OriginalReturn = (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(
                        DiagnosticEnvelopeKind.FirstRawReturn, item.OriginalReturn),
                }).ToArray()),
            RawUncertainties = new ReadOnlyCollection<InMemoryRawUncertainty>(
                value.RawUncertainties.Select(item => item with
                {
                    EvidenceSource = new string(item.EvidenceSource.AsSpan()),
                    Reason = new string(item.Reason.AsSpan()),
                }).ToArray()),
            WireFrame = value.WireFrame is null ? null : (byte[])value.WireFrame.Clone(),
        };

    private static InMemorySupervisionOperationSnapshot CopyOperationSnapshot(
        InMemorySupervisionOperationSnapshot value) => value with
        {
            CommandHeader = DiagnosticValidation.CopyHeader(value.CommandHeader),
            OriginalReturnHeader = value.OriginalReturnHeader is null
            ? null
            : DiagnosticValidation.CopyHeader(value.OriginalReturnHeader),
            OriginalReturn = value.OriginalReturn is null
            ? null
            : (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(
                DiagnosticEnvelopeKind.FirstRawReturn, value.OriginalReturn),
            OriginalDeliveryHeader = value.OriginalDeliveryHeader is null
            ? null
            : DiagnosticValidation.CopyHeader(value.OriginalDeliveryHeader),
            CleanupDescriptor = value.CleanupDescriptor is null ? null : CopyDescriptor(value.CleanupDescriptor),
            Ticket = new string(value.Ticket.AsSpan()),
        };

    private enum EntitlementKind
    {
        OrdinaryDynamic,
        Inspection,
    }

    private readonly record struct ParseEntitlement(
        EntitlementKind Kind,
        OperationState? Operation,
        int ReservedRows,
        long ReservedCopiedBytes);

    private sealed record QueuedFrame(
        byte[] Frame,
        Guid? ExpectedOperationId,
        int AnticipatedRows,
        long AnticipatedCopiedBytes);

    private sealed class OperationState
    {
        internal OperationState(
            Guid operationId,
            SupervisionCallKind kind,
            long commitmentTicks,
            long deadlineTicks,
            DiagnosticEnvelopeHeader commandHeader,
            ObserverDescriptor? cleanupDescriptor,
            SupervisionPayloadBudget reservedPayload,
            string ticket,
            bool cleanup)
        {
            OperationId = operationId;
            Kind = kind;
            CommitmentTicks = commitmentTicks;
            DeadlineTicks = deadlineTicks;
            CommandHeader = DiagnosticValidation.CopyHeader(commandHeader);
            CleanupDescriptor = cleanupDescriptor is null ? null : CopyDescriptor(cleanupDescriptor);
            ReservedPayload = reservedPayload with { };
            Ticket = new string(ticket.AsSpan());
            Cleanup = cleanup;
        }

        internal Guid OperationId { get; }
        internal SupervisionCallKind Kind { get; }
        internal long CommitmentTicks { get; }
        internal long DeadlineTicks { get; }
        internal DiagnosticEnvelopeHeader CommandHeader { get; }
        internal ObserverDescriptor? CleanupDescriptor { get; }
        internal SupervisionPayloadBudget ReservedPayload { get; }
        internal string Ticket { get; }
        internal bool Cleanup { get; }
        internal DiagnosticHeldReturnAdmission? HeldAdmission { get; set; }
        internal DiagnosticEnvelopeHeader? OriginalReturnHeader { get; set; }
        internal DiagnosticFirstRawReturn? OriginalReturn { get; set; }
        internal DiagnosticEnvelopeHeader? OriginalDeliveryHeader { get; set; }
        internal InMemorySupervisionOperationStage Stage { get; set; } = InMemorySupervisionOperationStage.Committed;
        internal List<DiagnosticValueRow> Rows { get; } = [];
        internal bool RowsFinal { get; set; }
        internal long CopiedBytes { get; set; }
        internal long CompletionEncodedBytes { get; set; }
        internal int MaximumFrameBytes { get; set; }
        internal bool ReturnReservationHeld { get; set; } = true;
        internal bool DeliveryReservationHeld { get; set; } = true;
        internal long? DeliveryTicks { get; set; }

        internal InMemorySupervisionOperationSnapshot Snapshot() => new(
            OperationId,
            Kind,
            Stage,
            CommitmentTicks,
            DeadlineTicks,
            DiagnosticValidation.CopyHeader(CommandHeader),
            OriginalReturnHeader is null ? null : DiagnosticValidation.CopyHeader(OriginalReturnHeader),
            OriginalReturn is null ? null : (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(
                DiagnosticEnvelopeKind.FirstRawReturn, OriginalReturn),
            OriginalDeliveryHeader is null ? null : DiagnosticValidation.CopyHeader(OriginalDeliveryHeader),
            CleanupDescriptor is null ? null : CopyDescriptor(CleanupDescriptor),
            Rows.Count,
            CopiedBytes,
            CompletionEncodedBytes,
            MaximumFrameBytes,
            ReturnReservationHeld,
            DeliveryReservationHeld,
            new string(Ticket.AsSpan()));
    }
}
