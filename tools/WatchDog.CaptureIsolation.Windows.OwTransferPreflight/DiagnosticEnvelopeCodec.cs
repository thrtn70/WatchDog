using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal enum DiagnosticEnvelopeKind
{
    PreparedContext,
    CustodyAcknowledgment,
    Command,
    FirstRawReturn,
    ValueRowBatch,
    DeliveryObservation,
    AbortExitFinalReport,
}

internal enum DiagnosticFirstReturnDisposition { Stored, Duplicate, Conflict, Overflow }

internal sealed record DiagnosticProofClaims(
    bool NativeCallsPerformed,
    bool NativeCapturePerformed,
    bool PeerAuthenticated,
    bool NativeAuthorityProven,
    bool NativeDurabilityProven,
    bool CompleteHandleInventoryProven,
    bool ZeroExtraInheritanceProven,
    bool ProductionAdmissionGranted,
    bool B1WriteReleaseIssued,
    bool B1JournalWritesPerformed,
    bool ReleaseApproved)
{
    internal static readonly DiagnosticProofClaims False = new(
        false, false, false, false, false, false, false, false, false, false, false);

    internal bool HasPositiveClaim => NativeCallsPerformed || NativeCapturePerformed || PeerAuthenticated ||
        NativeAuthorityProven || NativeDurabilityProven || CompleteHandleInventoryProven ||
        ZeroExtraInheritanceProven || ProductionAdmissionGranted || B1WriteReleaseIssued ||
        B1JournalWritesPerformed || ReleaseApproved;
}

internal sealed record DiagnosticEnvelopeHeader(
    string Schema,
    int Version,
    DiagnosticEnvelopeKind Kind,
    string ContextDigest,
    long Epoch,
    long Sequence,
    Guid EventId,
    Guid OperationId);

internal sealed record DiagnosticPreparedContext(
    Guid RunId,
    Guid SessionId,
    int Attempt,
    InventoryAnchor Anchor,
    string SelectedResource,
    string SourceDigest,
    string BinaryDigest,
    string SdkDigest,
    string OsExpectation,
    InventoryProcessIdentity Controller,
    InventoryProcessIdentity Owner,
    InventoryProcessIdentity Writer,
    InventoryProcessIdentity Target,
    SupervisionTargetContext TargetContext,
    string TargetSlotId,
    InventoryMeasurementProvenance Provenance,
    string CustodyDigest,
    bool ValuesOnly,
    IReadOnlyList<SupervisionCapabilitySlot> ExpectedCustody,
    SupervisionPolicy Policy,
    string PClockDomain,
    string OClockDomain,
    DiagnosticProofClaims Claims);

internal sealed record DiagnosticCustodyAcknowledgment(
    InventoryProcessIdentity Actor,
    string CustodyDigest,
    IReadOnlyList<SupervisionCapabilitySlot> AcknowledgedCustody,
    DiagnosticProofClaims Claims);

internal sealed record DiagnosticCommand(
    InventoryProcessIdentity Issuer,
    InventoryProcessIdentity Actor,
    InventoryProcessIdentity Target,
    SupervisionCallKind Kind,
    ObserverDescriptor? Descriptor,
    SupervisionPayloadBudget ReservedPayload,
    long IssuanceTicks,
    long DeadlineTicks,
    string ClockDomain,
    DiagnosticProofClaims Claims);

internal sealed record DiagnosticFirstRawReturn(
    InventoryProcessIdentity Owner,
    SupervisionCallKind Kind,
    uint ApiStatus,
    IReadOnlyList<long> PositiveCoordinates,
    ObserverDescriptor? Descriptor,
    long OwnerObservedTicks,
    string OwnerClockDomain,
    long OwnerClockFrequency,
    string? IncompleteReason,
    DiagnosticProofClaims Claims);

internal sealed record DiagnosticValueRow(long Ordinal, string Value);

internal sealed record DiagnosticValueRowBatch(
    InventoryProcessIdentity Owner,
    int BatchIndex,
    bool IsFinal,
    IReadOnlyList<DiagnosticValueRow> Rows,
    DiagnosticProofClaims Claims);

internal sealed record DiagnosticDeliveryObservation(
    InventoryProcessIdentity Actor,
    SupervisionCallKind Kind,
    bool Delivered,
    long IngestionTicks,
    string IngestionClockDomain,
    DiagnosticProofClaims Claims);

internal sealed record DiagnosticAbortExitFinalReport(
    CollectorSupervisionState State,
    string Reason,
    IReadOnlyList<SupervisionUnknownObligation> Unknowns,
    SupervisionPendingCompletionSnapshot? Pending,
    bool UnknownOverflow,
    IReadOnlyList<string> Tickets,
    SupervisionTerminationEvidence? Termination,
    SupervisionJobObservation JobObservation,
    DiagnosticProofClaims Claims);

internal sealed record DiagnosticDecodeContext(
    string ContextDigest,
    long Epoch,
    Guid RunId,
    Guid SessionId,
    int Attempt,
    InventoryAnchor Anchor,
    string SourceDigest,
    string BinaryDigest,
    InventoryProcessIdentity Controller,
    InventoryProcessIdentity Owner,
    InventoryProcessIdentity Writer,
    InventoryProcessIdentity Target,
    SupervisionTargetContext TargetContext,
    string TargetSlotId,
    string PClockDomain = "p-clock",
    string OClockDomain = "o-clock",
    string? SelectedResource = null,
    string? SdkDigest = null,
    string? OsExpectation = null,
    string? CustodyDigest = null,
    IReadOnlyList<SupervisionCapabilitySlot>? ExpectedCustody = null,
    SupervisionPolicy? ExpectedPolicy = null,
    long OwnerClockFrequency = 1_000,
    bool IsFrozen = false)
{
    internal DiagnosticDecodeContext Freeze() => DiagnosticValidation.CopyDecodeContext(this);
}

internal sealed record DiagnosticDecodeObservation(
    int PayloadAllocationRequestedBytes,
    bool ProtectedCapacityClaimRejected)
{
    internal static readonly DiagnosticDecodeObservation None = new(0, false);
}

internal sealed record DiagnosticTranscriptSnapshot(
    int Calls,
    int OrdinaryEvents,
    int ReleaseEvents,
    int AbortFinalEvents,
    int ActualRows,
    long CopiedBytes,
    long ActualEncodedBytes,
    IReadOnlyList<SupervisionUnknownObligation> UnknownObligations,
    bool UnknownOverflow,
    IReadOnlyDictionary<string, int> RejectedSummary)
{
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

internal sealed record DiagnosticEnvelope(DiagnosticEnvelopeHeader Header, object Body)
{
    internal DiagnosticEnvelopeKind Kind => Header.Kind;

    internal static DiagnosticEnvelope Create(DiagnosticEnvelopeHeader header, object body)
    {
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(body);
        DiagnosticValidation.ValidateHeader(header);
        return new(DiagnosticValidation.CopyHeader(header), DiagnosticValidation.CopyBody(header.Kind, body));
    }
}

internal sealed class DiagnosticFrameReservation
{
    private readonly DiagnosticTranscript _owner;
    private int _used;

    internal DiagnosticFrameReservation(
        DiagnosticTranscript owner,
        Guid token,
        Guid eventId,
        DiagnosticEnvelopeKind kind,
        DiagnosticReservationPartition partition,
        bool committedCommand,
        bool consumesHeld,
        DiagnosticEnvelope expectedEnvelope)
    {
        _owner = owner;
        Token = token;
        EventId = eventId;
        Kind = kind;
        Partition = partition;
        CommittedCommand = committedCommand;
        ConsumesHeld = consumesHeld;
        ExpectedEnvelope = DiagnosticEnvelope.Create(expectedEnvelope.Header, expectedEnvelope.Body);
    }

    internal Guid Token { get; }
    internal Guid EventId { get; }
    internal DiagnosticEnvelopeKind Kind { get; }
    internal DiagnosticReservationPartition Partition { get; }
    internal bool CommittedCommand { get; }
    internal bool ConsumesHeld { get; }
    internal DiagnosticEnvelope ExpectedEnvelope { get; }

    internal bool TryCommit(DiagnosticEnvelope envelope, int actualBytes, out string error)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
        {
            error = "reservation-already-used";
            return false;
        }

        if (envelope.Header.EventId != EventId || envelope.Kind != Kind ||
            !DiagnosticValidation.ExactEnvelope(envelope, ExpectedEnvelope))
        {
            _owner.Cancel(this);
            error = "reservation-envelope-mismatch";
            return false;
        }

        return _owner.Commit(this, actualBytes, out error);
    }
}

internal enum DiagnosticReservationPartition { Ordinary, Release, AbortFinal }

internal sealed class DiagnosticHeldReturnAdmission
{
    private int _inspectionLease;
    internal DiagnosticHeldReturnAdmission(DiagnosticTranscript owner, Guid operationId)
    {
        Owner = owner;
        OperationId = operationId;
    }

    internal DiagnosticTranscript Owner { get; }
    internal Guid OperationId { get; }
    internal bool TryAcquireInspectionLease() => Interlocked.CompareExchange(ref _inspectionLease, 1, 0) == 0;
    internal void ReleaseInspectionLease() => Interlocked.Exchange(ref _inspectionLease, 0);
}

internal sealed class DiagnosticTranscript
{
    internal const int MaximumRejectedSummaryKinds = 16;
    internal const int MaximumHeldInspectionFrames = 8;
    private const int MaximumNonFreeCalls = SupervisionPolicy.MaximumCalls - SupervisionPolicy.ReservedReleaseCalls;

    private readonly object _gate = new();
    private readonly HashSet<Guid> _operationTokens = [];
    private readonly Dictionary<Guid, CommittedOperation> _operations = [];
    private readonly Dictionary<Guid, InboundOperation> _inboundOperations = [];
    private readonly HashSet<Guid> _inboundEvents = [];
    private readonly List<SupervisionUnknownObligation> _unknown = [];
    private readonly Dictionary<string, int> _rejected = new(StringComparer.Ordinal);
    private int _calls;
    private int _nonFreeCalls;
    private int _freeCalls;
    private int _ordinaryEvents;
    private int _releaseEvents;
    private int _abortFinalEvents;
    private int _heldOrdinaryEvents;
    private int _heldReleaseEvents;
    private long _actualBytes;
    private int _actualRows;
    private long _copiedBytes;
    private long _ordinaryBytes;
    private long _releaseBytes;
    private long _abortFinalBytes;
    private long _heldOrdinaryBytes;
    private long _heldReleaseBytes;
    private int _heldInspectionEvents = MaximumHeldInspectionFrames;
    private long _heldInspectionBytes = MaximumHeldInspectionFrames *
        (long)DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
    private bool _ordinaryStopped;
    private bool _abortFinalActivated;
    private bool _unknownOverflow;
    private long _nextInboundSequence = 1;
    private DiagnosticDecodeContext? _pinnedExpected;
    private bool _preparedSeen;
    private bool _acknowledgmentSeen;

    internal DiagnosticTranscript(DiagnosticDecodeContext? expected = null)
    {
        if (expected is null) return;
        DiagnosticValidation.ValidateDecodeContext(expected);
        _pinnedExpected = DiagnosticValidation.CopyDecodeContext(expected);
    }

    internal bool TryPinExpected(
        DiagnosticDecodeContext supplied,
        out DiagnosticDecodeContext pinned,
        out string error)
    {
        lock (_gate)
        {
            DiagnosticValidation.ValidateDecodeContext(supplied);
            var copy = DiagnosticValidation.CopyDecodeContext(supplied);
            if (_pinnedExpected is null)
            {
                _pinnedExpected = copy;
                pinned = DiagnosticValidation.CopyDecodeContext(copy);
                error = string.Empty;
                return true;
            }
            pinned = DiagnosticValidation.CopyDecodeContext(_pinnedExpected);
            if (!DiagnosticValidation.ExactDecodeContext(copy, _pinnedExpected))
            {
                error = "decode-context-drift";
                return false;
            }
            error = string.Empty;
            return true;
        }
    }

    internal bool TryReserveOrdinary(
        DiagnosticEnvelope envelope,
        out DiagnosticFrameReservation? reservation,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            return Reserve(envelope, DiagnosticReservationPartition.Ordinary, false, out reservation, out error);
        }
    }

    internal bool TryCommitCommand(
        DiagnosticEnvelope envelope,
        ObserverDescriptor? expectedDescriptor,
        out DiagnosticFrameReservation? reservation,
        out string error) => TryCommitCommandCore(envelope, expectedDescriptor, false, out reservation, out error);

    internal bool TryCommitFreeCommand(
        DiagnosticEnvelope envelope,
        ObserverDescriptor exactDescriptor,
        out DiagnosticFrameReservation? reservation,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(exactDescriptor);
        return TryCommitCommandCore(envelope, exactDescriptor, true, out reservation, out error);
    }

    private bool TryCommitCommandCore(
        DiagnosticEnvelope envelope,
        ObserverDescriptor? expectedDescriptor,
        bool free,
        out DiagnosticFrameReservation? reservation,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            reservation = null;
            if (envelope.Kind != DiagnosticEnvelopeKind.Command || envelope.Body is not DiagnosticCommand command)
            {
                error = "command-record-required";
                return false;
            }
            if (envelope.Header.OperationId == Guid.Empty || _operationTokens.Contains(envelope.Header.OperationId))
            {
                error = "operation-token-replayed";
                return false;
            }
            if (_ordinaryStopped && !free)
            {
                error = "ordinary-ingestion-stopped";
                return false;
            }
            if (free)
            {
                if (!IsRelease(command.Kind) || !ExactDescriptor(command.Descriptor, expectedDescriptor) ||
                    _freeCalls >= SupervisionPolicy.ReservedReleaseCalls ||
                    _releaseEvents + _heldReleaseEvents + 2 > SupervisionPolicy.ReservedReleaseEvents)
                {
                    error = "free-command-not-authorized";
                    return false;
                }
            }
            else if (_nonFreeCalls >= MaximumNonFreeCalls ||
                     _ordinaryEvents + _heldOrdinaryEvents + _heldInspectionEvents + 2 >
                     SupervisionPolicy.OrdinaryEventLimit ||
                     _ordinaryBytes + _heldOrdinaryBytes + _heldInspectionBytes +
                     2L * DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes >
                     SupervisionPolicy.MaximumOrdinaryEncodedBytes)
            {
                _ordinaryStopped = true;
                error = "ordinary-command-capacity-exhausted";
                return false;
            }

            _operationTokens.Add(envelope.Header.OperationId);
            _calls++;
            if (free)
            {
                _freeCalls++;
                _heldReleaseEvents += 2;
                _heldReleaseBytes += 2L * DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
            }
            else
            {
                _nonFreeCalls++;
                _heldOrdinaryEvents += 2;
                _heldOrdinaryBytes += 2L * DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
            }
            _operations.Add(envelope.Header.OperationId,
                new(envelope, DiagnosticValidation.CopyDescriptor(expectedDescriptor), free));
            reservation = new(this, envelope.Header.OperationId, envelope.Header.EventId, envelope.Kind,
                free ? DiagnosticReservationPartition.Release : DiagnosticReservationPartition.Ordinary, true, true,
                envelope);
            error = string.Empty;
            return true;
        }
    }

    internal bool TryReserveFirstReturn(
        DiagnosticEnvelope envelope,
        out DiagnosticFrameReservation? reservation,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            reservation = null;
            if (envelope.Kind != DiagnosticEnvelopeKind.FirstRawReturn ||
                envelope.Body is not DiagnosticFirstRawReturn result ||
                !_operations.TryGetValue(envelope.Header.OperationId, out var operation) || operation.ReturnClaimed)
            {
                error = "matching-first-return-not-held";
                return false;
            }
            if (!MatchesCommittedReturn(envelope, result, operation))
            {
                RecordUnknownCore(result.PositiveCoordinates, result.Descriptor?.Kind,
                    DiagnosticValidation.CopyIdentity(operation.CommandBody.Actor), envelope.Header.OperationId,
                    result.ApiStatus, result.OwnerObservedTicks,
                    "rejected-local-first-return-binding");
                error = "matching-first-return-invalid";
                return false;
            }

            operation.ReturnClaimed = true;
            operation.FirstReturn = (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(
                DiagnosticEnvelopeKind.FirstRawReturn, result);
            reservation = new(this, envelope.Header.OperationId, envelope.Header.EventId, envelope.Kind,
                operation.Free ? DiagnosticReservationPartition.Release : DiagnosticReservationPartition.Ordinary,
                false, true, envelope);
            error = string.Empty;
            return true;
        }
    }

    internal bool TryCreateHeldReturnAdmission(
        Guid operationId,
        out DiagnosticHeldReturnAdmission? admission,
        out string error)
    {
        lock (_gate)
        {
            admission = null;
            if (!_operations.TryGetValue(operationId, out var operation) || operation.ReturnClaimed ||
                operation.AdmissionCreated)
            {
                error = "held-return-operation-not-available";
                return false;
            }
            operation.AdmissionCreated = true;
            admission = new(this, operationId);
            error = string.Empty;
            return true;
        }
    }

    internal bool TryBeginHeldInspection(DiagnosticHeldReturnAdmission admission, int actualBytes)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(admission.Owner, this) ||
                !_operations.ContainsKey(admission.OperationId) ||
                actualBytes is < 4 or > DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes ||
                _heldInspectionEvents <= 0 ||
                _heldInspectionBytes < DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes ||
                !admission.TryAcquireInspectionLease())
            {
                return false;
            }

            _heldInspectionEvents--;
            _heldInspectionBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
            _ordinaryEvents++;
            _ordinaryBytes += actualBytes;
            _actualBytes += actualBytes;
            return true;
        }
    }

    internal void CompleteRejectedHeldInspection(DiagnosticHeldReturnAdmission admission)
    {
        lock (_gate)
        {
            if (ReferenceEquals(admission.Owner, this)) admission.ReleaseInspectionLease();
        }
    }

    internal void ActivateAbortFinal(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || Encoding.UTF8.GetByteCount(reason) > DiagnosticValidation.MaximumTextBytes)
            throw new ArgumentException("A bounded local reason is required.", nameof(reason));
        lock (_gate)
        {
            _abortFinalActivated = true;
            _ordinaryStopped = true;
        }
    }

    internal DiagnosticFirstRawReturn? HeldFirstReturnSnapshot(Guid operationId)
    {
        lock (_gate)
        {
            return _operations.TryGetValue(operationId, out var operation) && operation.FirstReturn is not null
                ? (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(
                    DiagnosticEnvelopeKind.FirstRawReturn, operation.FirstReturn)
                : null;
        }
    }

    internal DiagnosticFirstRawReturn? InboundFirstReturnSnapshot(Guid operationId)
    {
        lock (_gate)
        {
            return _inboundOperations.TryGetValue(operationId, out var operation) && operation.FirstReturn is not null
                ? (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(
                    DiagnosticEnvelopeKind.FirstRawReturn, operation.FirstReturn)
                : null;
        }
    }

    internal bool TryReserveAbortFinal(
        DiagnosticEnvelope envelope,
        out DiagnosticFrameReservation? reservation,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            reservation = null;
            if (!_abortFinalActivated || envelope.Kind is not (DiagnosticEnvelopeKind.AbortExitFinalReport or
                    DiagnosticEnvelopeKind.DeliveryObservation) ||
                _abortFinalEvents >= SupervisionPolicy.ReservedAbortAndFinalEvents)
            {
                error = "trusted-local-final-capacity-not-active";
                return false;
            }
            reservation = new(this, Guid.NewGuid(), envelope.Header.EventId, envelope.Kind,
                DiagnosticReservationPartition.AbortFinal, false, false, envelope);
            error = string.Empty;
            return true;
        }
    }

    internal DiagnosticTranscriptSnapshot Snapshot()
    {
        lock (_gate)
        {
            return new(_calls, _ordinaryEvents, _releaseEvents, _abortFinalEvents, _actualRows, _copiedBytes,
                _actualBytes,
                new ReadOnlyCollection<SupervisionUnknownObligation>(_unknown.Select(DiagnosticValidation.CopyUnknown).ToArray()),
                _unknownOverflow,
                new ReadOnlyDictionary<string, int>(new Dictionary<string, int>(_rejected, StringComparer.Ordinal)));
        }
    }

    internal void RecordUnknown(DiagnosticFirstRawReturn value, Guid operationId, string reason)
    {
        lock (_gate)
        {
            RecordUnknownCore(value.PositiveCoordinates, value.Descriptor?.Kind,
                DiagnosticValidation.CopyIdentity(value.Owner), operationId, value.ApiStatus,
                value.OwnerObservedTicks, reason);
        }
    }

    internal void RecordRejectedReturn(
        DiagnosticFirstRawReturn value,
        InventoryProcessIdentity expectedOwner,
        Guid operationId,
        string reason)
    {
        lock (_gate)
        {
            RecordUnknownCore(value.PositiveCoordinates ?? [], value.Descriptor?.Kind,
                DiagnosticValidation.CopyIdentity(expectedOwner), operationId, value.ApiStatus,
                value.OwnerObservedTicks > 0 ? value.OwnerObservedTicks : 1, reason);
        }
    }

    private void RecordUnknownCore(
        IReadOnlyList<long> coordinates,
        ObserverResourceKind? kind,
        InventoryProcessIdentity owner,
        Guid operationId,
        uint status,
        long observedTicks,
        string reason)
    {
        if (coordinates.Count == 0)
        {
            if (_unknown.Count >= SupervisionPolicy.MaximumUnknownReturns)
            {
                _unknownOverflow = true;
                return;
            }
            _unknown.Add(new(kind, owner, operationId,
                status, null, "diagnostic-first-return", reason, false, observedTicks));
            return;
        }
        foreach (var coordinate in coordinates)
        {
            if (coordinate <= 0) continue;
            if (_unknown.Count >= SupervisionPolicy.MaximumUnknownReturns)
            {
                _unknownOverflow = true;
                return;
            }
            _unknown.Add(new(kind, owner, operationId,
                status, coordinate, "diagnostic-first-return", reason, false, observedTicks));
        }
    }

    internal void RecordRejected(string reason, int actualBytes)
    {
        lock (_gate)
        {
            if (!_ordinaryStopped &&
                _ordinaryEvents + _heldOrdinaryEvents + _heldInspectionEvents <
                SupervisionPolicy.OrdinaryEventLimit &&
                _ordinaryBytes + _heldOrdinaryBytes + _heldInspectionBytes + actualBytes <=
                SupervisionPolicy.MaximumOrdinaryEncodedBytes)
            {
                _ordinaryEvents++;
                _actualBytes += actualBytes;
                _ordinaryBytes += actualBytes;
            }
            else
            {
                _ordinaryStopped = true;
            }
            RecordRejectedSummaryCore(reason);
        }
    }

    internal void RecordRejectedSummary(string reason)
    {
        lock (_gate) RecordRejectedSummaryCore(reason);
    }

    internal bool CanAcceptInbound(int actualBytes)
    {
        lock (_gate)
        {
            return !_ordinaryStopped &&
                _ordinaryEvents + _heldOrdinaryEvents + _heldInspectionEvents <
                SupervisionPolicy.OrdinaryEventLimit &&
                _ordinaryBytes + _heldOrdinaryBytes + _heldInspectionBytes + actualBytes <=
                SupervisionPolicy.MaximumOrdinaryEncodedBytes;
        }
    }

    internal bool RecordDecoded(
        DiagnosticEnvelope envelope,
        int actualBytes,
        out bool protectedClaimRejected,
        out string error)
    {
        lock (_gate)
        {
            protectedClaimRejected = envelope.Kind is DiagnosticEnvelopeKind.AbortExitFinalReport ||
                (envelope.Kind is DiagnosticEnvelopeKind.Command && envelope.Body is DiagnosticCommand command &&
                 IsRelease(command.Kind)) ||
                (envelope.Kind is DiagnosticEnvelopeKind.FirstRawReturn &&
                 envelope.Body is DiagnosticFirstRawReturn result && IsRelease(result.Kind));
            if (_ordinaryStopped ||
                _ordinaryEvents + _heldOrdinaryEvents + _heldInspectionEvents >=
                SupervisionPolicy.OrdinaryEventLimit ||
                _ordinaryBytes + _heldOrdinaryBytes + _heldInspectionBytes + actualBytes >
                SupervisionPolicy.MaximumOrdinaryEncodedBytes)
            {
                PreserveRejectedDecodedReturn(envelope, "rejected-decoded-first-return-capacity");
                _ordinaryStopped = true;
                error = "ordinary-ingestion-stopped";
                return false;
            }
            var (rows, copiedBytes) = PayloadDelta(envelope);
            if (_actualRows + rows > SupervisionPolicy.MaximumRows ||
                _copiedBytes + copiedBytes > SupervisionPolicy.MaximumCopiedBytes)
            {
                PreserveRejectedDecodedReturn(envelope, "rejected-decoded-first-return-payload-budget");
                error = "actual-row-or-copy-budget-exhausted";
                return false;
            }
            if (envelope.Header.Sequence != _nextInboundSequence || !_inboundEvents.Add(envelope.Header.EventId))
            {
                PreserveRejectedDecodedReturn(envelope, "rejected-decoded-first-return-sequence-or-event");
                error = "decoded-identity-replay-or-sequence-invalid";
                return false;
            }
            if (!AcceptInboundOperation(envelope, out error))
            {
                PreserveRejectedDecodedReturn(envelope, "rejected-decoded-first-return-transition");
                return false;
            }
            _nextInboundSequence++;
            _ordinaryEvents++;
            _actualBytes += actualBytes;
            _ordinaryBytes += actualBytes;
            _actualRows += rows;
            _copiedBytes += copiedBytes;
            error = string.Empty;
            return true;
        }
    }

    internal bool RecordDecodedHeldReturn(
        DiagnosticEnvelope envelope,
        int actualBytes,
        DiagnosticHeldReturnAdmission admission,
        bool inspectionCharged,
        out bool protectedClaimRejected,
        out string error)
    {
        lock (_gate)
        {
            protectedClaimRejected = false;
            if (envelope.Kind != DiagnosticEnvelopeKind.FirstRawReturn ||
                envelope.Body is not DiagnosticFirstRawReturn result)
            {
                error = "held-first-return-record-required";
                return false;
            }
            if (!ReferenceEquals(admission.Owner, this) || admission.OperationId != envelope.Header.OperationId ||
                !_operations.TryGetValue(admission.OperationId, out var operation) || operation.ReturnClaimed ||
                !MatchesCommittedReturn(envelope, result, operation))
            {
                RecordUnknownCore(result.PositiveCoordinates, result.Descriptor?.Kind,
                    DiagnosticValidation.CopyIdentity(result.Owner), envelope.Header.OperationId, result.ApiStatus,
                    result.OwnerObservedTicks,
                    "rejected-held-first-return-binding");
                error = "held-first-return-binding-invalid";
                return false;
            }
            if (envelope.Header.Sequence != _nextInboundSequence || !_inboundEvents.Add(envelope.Header.EventId))
            {
                RecordUnknownCore(result.PositiveCoordinates, result.Descriptor?.Kind,
                    DiagnosticValidation.CopyIdentity(result.Owner), envelope.Header.OperationId, result.ApiStatus,
                    result.OwnerObservedTicks, "rejected-held-first-return-replay");
                error = "decoded-identity-replay-or-sequence-invalid";
                return false;
            }
            if (operation.Free)
            {
                if (_heldReleaseEvents <= 0 || _heldReleaseBytes < DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes)
                {
                    error = "release-reservation-not-held";
                    return false;
                }
                _heldReleaseEvents--;
                _heldReleaseBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
            }
            else
            {
                if (_heldOrdinaryEvents <= 0 || _heldOrdinaryBytes < DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes)
                {
                    error = "ordinary-completion-reservation-not-held";
                    return false;
                }
                _heldOrdinaryEvents--;
                _heldOrdinaryBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
            }
            if (inspectionCharged)
            {
                _ordinaryEvents--;
                _ordinaryBytes -= actualBytes;
                _actualBytes -= actualBytes;
                _heldInspectionEvents++;
                _heldInspectionBytes += DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
                admission.ReleaseInspectionLease();
            }
            if (operation.Free)
            {
                _releaseEvents++;
                _releaseBytes += actualBytes;
            }
            else
            {
                _ordinaryEvents++;
                _ordinaryBytes += actualBytes;
            }
            _actualBytes += actualBytes;
            operation.ReturnClaimed = true;
            operation.FirstReturn = (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(
                DiagnosticEnvelopeKind.FirstRawReturn, result);
            _nextInboundSequence++;
            error = string.Empty;
            return true;
        }
    }

    internal bool Commit(DiagnosticFrameReservation reservation, int actualBytes, out string error)
    {
        lock (_gate)
        {
            if (actualBytes is < 4 or > DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes)
            {
                error = "encoded-frame-size-invalid";
                return false;
            }
            var (rows, copiedBytes) = PayloadDelta(reservation.ExpectedEnvelope);
            if (_actualRows + rows > SupervisionPolicy.MaximumRows ||
                _copiedBytes + copiedBytes > SupervisionPolicy.MaximumCopiedBytes)
            {
                error = "actual-row-or-copy-budget-exhausted";
                return false;
            }
            switch (reservation.Partition)
            {
                case DiagnosticReservationPartition.Ordinary:
                    if (reservation.ConsumesHeld)
                    {
                        if (_heldOrdinaryEvents <= 0 || _heldOrdinaryBytes < DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes)
                        {
                            error = "ordinary-completion-reservation-not-held";
                            return false;
                        }
                        _heldOrdinaryEvents--;
                        _heldOrdinaryBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
                    }
                    else if (_ordinaryEvents + _heldOrdinaryEvents + _heldInspectionEvents >=
                             SupervisionPolicy.OrdinaryEventLimit ||
                             _ordinaryBytes + _heldOrdinaryBytes + _heldInspectionBytes + actualBytes >
                             SupervisionPolicy.MaximumOrdinaryEncodedBytes)
                    {
                        _ordinaryStopped = true;
                        error = "ordinary-capacity-exhausted";
                        return false;
                    }
                    _ordinaryEvents++;
                    _actualBytes += actualBytes;
                    _ordinaryBytes += actualBytes;
                    break;
                case DiagnosticReservationPartition.Release:
                    if (_heldReleaseEvents <= 0 || _heldReleaseBytes < DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes)
                    {
                        error = "release-reservation-not-held";
                        return false;
                    }
                    _heldReleaseEvents--;
                    _heldReleaseBytes -= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes;
                    _releaseEvents++;
                    _actualBytes += actualBytes;
                    _releaseBytes += actualBytes;
                    if (_releaseBytes > SupervisionPolicy.ReservedReleaseEvents * (long)DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes)
                        throw new InvalidOperationException("Release byte partition exceeded its frozen bound.");
                    break;
                case DiagnosticReservationPartition.AbortFinal:
                    if (!_abortFinalActivated || _abortFinalEvents >= SupervisionPolicy.ReservedAbortAndFinalEvents)
                    {
                        error = "abort-final-capacity-not-active";
                        return false;
                    }
                    _abortFinalEvents++;
                    _actualBytes += actualBytes;
                    _abortFinalBytes += actualBytes;
                    if (_abortFinalBytes > SupervisionPolicy.ReservedAbortAndFinalEvents * (long)DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes)
                        throw new InvalidOperationException("Abort/final byte partition exceeded its frozen bound.");
                    break;
                default:
                    error = "reservation-partition-invalid";
                    return false;
            }
            if (_actualBytes > SupervisionPolicy.MaximumEncodedBytes)
                throw new InvalidOperationException("Diagnostic byte accounting exceeded the frozen total.");
            _actualRows += rows;
            _copiedBytes += copiedBytes;
            error = string.Empty;
            return true;
        }
    }

    internal void Cancel(DiagnosticFrameReservation reservation)
    {
        // A committed command and its completion hold are deliberately never refunded.
        if (reservation.CommittedCommand) return;
    }

    private bool Reserve(
        DiagnosticEnvelope envelope,
        DiagnosticReservationPartition partition,
        bool committed,
        out DiagnosticFrameReservation? reservation,
        out string error)
    {
        reservation = null;
        if (partition != DiagnosticReservationPartition.Ordinary || _ordinaryStopped ||
            _ordinaryEvents + _heldOrdinaryEvents + _heldInspectionEvents >= SupervisionPolicy.OrdinaryEventLimit ||
            _ordinaryBytes + _heldOrdinaryBytes + _heldInspectionBytes +
            DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes >
            SupervisionPolicy.MaximumOrdinaryEncodedBytes)
        {
            error = "ordinary-capacity-exhausted";
            return false;
        }
        reservation = new(this, Guid.NewGuid(), envelope.Header.EventId, envelope.Kind, partition, committed, false,
            envelope);
        error = string.Empty;
        return true;
    }

    private static bool IsRelease(SupervisionCallKind kind) =>
        kind is SupervisionCallKind.ReleaseMarker or SupervisionCallKind.ReleaseSnapshot;

    private static bool ExactDescriptor(ObserverDescriptor? left, ObserverDescriptor? right) => left is not null && right is not null &&
        left.Kind == right.Kind && left.Coordinate == right.Coordinate && left.Owner == right.Owner &&
        left.AcquisitionOperationId == right.AcquisitionOperationId && left.CleanupOnly == right.CleanupOnly;

    private static (int Rows, long CopiedBytes) PayloadDelta(DiagnosticEnvelope envelope)
    {
        if (envelope.Body is not DiagnosticValueRowBatch batch) return (0, 0);
        long copiedBytes = 0;
        foreach (var row in batch.Rows)
            copiedBytes = checked(copiedBytes + Encoding.UTF8.GetByteCount(row.Value));
        return (batch.Rows.Count, copiedBytes);
    }

    private void PreserveRejectedDecodedReturn(DiagnosticEnvelope envelope, string reason)
    {
        if (envelope.Body is not DiagnosticFirstRawReturn rejectedReturn) return;
        RecordUnknownCore(rejectedReturn.PositiveCoordinates, rejectedReturn.Descriptor?.Kind,
            DiagnosticValidation.CopyIdentity(rejectedReturn.Owner), envelope.Header.OperationId,
            rejectedReturn.ApiStatus, rejectedReturn.OwnerObservedTicks, reason);
    }

    private void RecordRejectedSummaryCore(string reason)
    {
        if (_rejected.TryGetValue(reason, out var count)) _rejected[reason] = count + 1;
        else if (_rejected.Count < MaximumRejectedSummaryKinds) _rejected.Add(reason, 1);
    }

    private static bool MatchesCommittedReturn(
        DiagnosticEnvelope envelope,
        DiagnosticFirstRawReturn result,
        CommittedOperation operation) =>
        result.Kind == operation.Kind && envelope.Header.ContextDigest == operation.Command.Header.ContextDigest &&
        envelope.Header.Epoch == operation.Command.Header.Epoch && result.Owner == operation.CommandBody.Actor &&
        result.OwnerClockDomain != operation.CommandBody.ClockDomain &&
        (!operation.Free || ExactDescriptor(result.Descriptor, operation.Descriptor));

    private bool AcceptInboundOperation(DiagnosticEnvelope envelope, out string error)
    {
        if (envelope.Kind == DiagnosticEnvelopeKind.PreparedContext)
        {
            if (_preparedSeen)
            {
                error = "decoded-prepared-context-not-one-use";
                return false;
            }
            _preparedSeen = true;
        }
        else if (envelope.Kind == DiagnosticEnvelopeKind.CustodyAcknowledgment)
        {
            if (_acknowledgmentSeen)
            {
                error = "decoded-custody-acknowledgment-not-one-use";
                return false;
            }
            _acknowledgmentSeen = true;
        }
        if (envelope.Kind == DiagnosticEnvelopeKind.Command && envelope.Body is DiagnosticCommand command)
        {
            if (_inboundOperations.ContainsKey(envelope.Header.OperationId))
            {
                error = "decoded-command-operation-replay";
                return false;
            }
            _inboundOperations.Add(envelope.Header.OperationId, new(command));
            error = string.Empty;
            return true;
        }
        if (envelope.Kind == DiagnosticEnvelopeKind.FirstRawReturn && envelope.Body is DiagnosticFirstRawReturn result)
        {
            if (!_inboundOperations.TryGetValue(envelope.Header.OperationId, out var operation) ||
                operation.Stage != InboundOperationStage.Command || operation.Kind != result.Kind ||
                operation.Owner != result.Owner || operation.ClockDomain == result.OwnerClockDomain)
            {
                error = "decoded-first-return-transition-invalid";
                return false;
            }
            operation.Stage = InboundOperationStage.FirstReturn;
            operation.FirstStatus = result.ApiStatus;
            operation.FirstReturn = (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(
                DiagnosticEnvelopeKind.FirstRawReturn, result);
            error = string.Empty;
            return true;
        }
        if (envelope.Kind == DiagnosticEnvelopeKind.ValueRowBatch && envelope.Body is DiagnosticValueRowBatch batch)
        {
            if (!_inboundOperations.TryGetValue(envelope.Header.OperationId, out var operation) ||
                operation.Kind != SupervisionCallKind.Walk || operation.Owner != batch.Owner ||
                operation.Stage is not (InboundOperationStage.FirstReturn or InboundOperationStage.Rows) ||
                operation.FirstStatus != 0 || operation.RowsFinal || batch.BatchIndex != operation.NextBatchIndex)
            {
                error = "decoded-row-batch-transition-invalid";
                return false;
            }
            operation.NextBatchIndex++;
            operation.RowsFinal = batch.IsFinal;
            operation.Stage = InboundOperationStage.Rows;
            error = string.Empty;
            return true;
        }
        if (envelope.Kind == DiagnosticEnvelopeKind.DeliveryObservation &&
            envelope.Body is DiagnosticDeliveryObservation delivery)
        {
            if (!_inboundOperations.TryGetValue(envelope.Header.OperationId, out var operation) ||
                operation.Kind != delivery.Kind || operation.Issuer != delivery.Actor ||
                operation.Stage is not (InboundOperationStage.FirstReturn or InboundOperationStage.Rows) ||
                operation.Kind == SupervisionCallKind.Walk && operation.FirstStatus == 0 && !operation.RowsFinal)
            {
                error = "decoded-delivery-transition-invalid";
                return false;
            }
            operation.Stage = InboundOperationStage.Delivered;
            error = string.Empty;
            return true;
        }
        if (_inboundOperations.ContainsKey(envelope.Header.OperationId))
        {
            error = "decoded-operation-kind-transition-invalid";
            return false;
        }
        _inboundOperations.Add(envelope.Header.OperationId, new((SupervisionCallKind?)null));
        error = string.Empty;
        return true;
    }

    private sealed class CommittedOperation(
        DiagnosticEnvelope command,
        ObserverDescriptor? descriptor,
        bool free)
    {
        internal DiagnosticEnvelope Command { get; } = DiagnosticEnvelope.Create(command.Header, command.Body);
        internal DiagnosticCommand CommandBody { get; } = (DiagnosticCommand)DiagnosticValidation.CopyBody(
            DiagnosticEnvelopeKind.Command, command.Body);
        internal SupervisionCallKind Kind => CommandBody.Kind;
        internal ObserverDescriptor? Descriptor { get; } = descriptor;
        internal bool Free { get; } = free;
        internal bool ReturnClaimed { get; set; }
        internal DiagnosticFirstRawReturn? FirstReturn { get; set; }
        internal bool AdmissionCreated { get; set; }
    }

    private enum InboundOperationStage { Standalone, Command, FirstReturn, Rows, Delivered }

    private sealed class InboundOperation
    {
        internal InboundOperation(DiagnosticCommand command)
        {
            Kind = command.Kind;
            Owner = DiagnosticValidation.CopyIdentity(command.Actor);
            Issuer = DiagnosticValidation.CopyIdentity(command.Issuer);
            Target = DiagnosticValidation.CopyIdentity(command.Target);
            ClockDomain = new string(command.ClockDomain.AsSpan());
            Stage = InboundOperationStage.Command;
        }

        internal InboundOperation(SupervisionCallKind? kind)
        {
            Kind = kind;
            Owner = null;
            Issuer = null;
            Target = null;
            ClockDomain = null;
            Stage = InboundOperationStage.Standalone;
        }

        internal SupervisionCallKind? Kind { get; }
        internal InventoryProcessIdentity? Owner { get; }
        internal InventoryProcessIdentity? Issuer { get; }
        internal InventoryProcessIdentity? Target { get; }
        internal string? ClockDomain { get; }
        internal InboundOperationStage Stage { get; set; }
        internal int NextBatchIndex { get; set; }
        internal bool RowsFinal { get; set; }
        internal uint? FirstStatus { get; set; }
        internal DiagnosticFirstRawReturn? FirstReturn { get; set; }
    }
}

internal sealed class DiagnosticFirstReturnSlot
{
    private readonly Guid _operationId;
    private readonly SupervisionCallKind _kind;
    private readonly InventoryProcessIdentity _owner;
    private DiagnosticFirstRawReturn? _first;

    internal DiagnosticFirstReturnSlot(
        string contextDigest,
        long epoch,
        Guid operationId,
        SupervisionCallKind kind,
        InventoryProcessIdentity owner)
    {
        ContextDigest = DiagnosticValidation.CopyText(contextDigest, nameof(contextDigest));
        if (epoch <= 0) throw new ArgumentOutOfRangeException(nameof(epoch));
        if (operationId == Guid.Empty) throw new ArgumentException("Operation token is required.", nameof(operationId));
        Epoch = epoch;
        _operationId = operationId;
        _kind = kind;
        _owner = DiagnosticValidation.CopyIdentity(owner);
    }

    internal string ContextDigest { get; }
    internal long Epoch { get; }

    internal bool TryStore(
        DiagnosticEnvelope candidateEnvelope,
        DiagnosticTranscript transcript,
        out DiagnosticFirstRawReturn retained,
        out DiagnosticFirstReturnDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(candidateEnvelope);
        ArgumentNullException.ThrowIfNull(transcript);
        if (candidateEnvelope.Kind != DiagnosticEnvelopeKind.FirstRawReturn ||
            candidateEnvelope.Body is not DiagnosticFirstRawReturn candidate)
            throw new ArgumentException("first-return-body-required", nameof(candidateEnvelope));
        var copy = (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(DiagnosticEnvelopeKind.FirstRawReturn, candidate);
        if (
            candidateEnvelope.Header.ContextDigest != ContextDigest || candidateEnvelope.Header.Epoch != Epoch ||
            candidateEnvelope.Header.OperationId != _operationId)
        {
            transcript.RecordUnknown(copy, candidateEnvelope.Header.OperationId,
                "first-return-envelope-identity-mismatch");
            retained = _first ?? copy;
            disposition = DiagnosticFirstReturnDisposition.Conflict;
            return false;
        }
        if (copy.Kind != _kind || copy.Owner != _owner)
        {
            transcript.RecordUnknown(copy, _operationId, "first-return-identity-mismatch");
            retained = _first ?? copy;
            disposition = DiagnosticFirstReturnDisposition.Conflict;
            return false;
        }
        if (_first is null)
        {
            _first = copy;
            retained = copy;
            disposition = DiagnosticFirstReturnDisposition.Stored;
            return true;
        }
        retained = _first;
        if (_first == copy || ExactReturn(_first, copy))
        {
            disposition = DiagnosticFirstReturnDisposition.Duplicate;
            return false;
        }
        transcript.RecordUnknown(copy, _operationId, "conflicting-duplicate-first-return");
        disposition = DiagnosticFirstReturnDisposition.Conflict;
        return false;
    }

    internal DiagnosticFirstRawReturn? Snapshot() => _first is null
        ? null
        : (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(DiagnosticEnvelopeKind.FirstRawReturn, _first);

    private static bool ExactReturn(DiagnosticFirstRawReturn left, DiagnosticFirstRawReturn right) =>
        left.Owner == right.Owner && left.Kind == right.Kind && left.ApiStatus == right.ApiStatus &&
        left.PositiveCoordinates.SequenceEqual(right.PositiveCoordinates) && left.Descriptor == right.Descriptor &&
        left.OwnerObservedTicks == right.OwnerObservedTicks && left.OwnerClockDomain == right.OwnerClockDomain &&
        left.OwnerClockFrequency == right.OwnerClockFrequency && left.IncompleteReason == right.IncompleteReason;
}

internal static class DiagnosticEnvelopeCodec
{
    internal const string Schema = "watchdog.ow-diagnostic-envelope";
    internal const int Version = 1;
    internal const int MaximumCompleteFrameBytes = SupervisionPolicy.MaximumFrameBytes;
    internal const int MaximumPayloadBytes = MaximumCompleteFrameBytes - sizeof(uint);

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    internal static bool TryEncode(
        DiagnosticEnvelope envelope,
        DiagnosticFrameReservation reservation,
        Span<byte> destination,
        out int written,
        out string error)
    {
        written = 0;
        error = string.Empty;
        if (envelope is null || reservation is null)
        {
            error = "envelope-and-reservation-required";
            return false;
        }
        try
        {
            var frozenEnvelope = DiagnosticEnvelope.Create(envelope.Header, envelope.Body);
            var scratch = new byte[MaximumCompleteFrameBytes];
            using var stream = new FixedMemoryWriteStream(scratch, sizeof(uint), MaximumPayloadBytes);
            SerializeWire(stream, frozenEnvelope);
            var payloadBytes = stream.WrittenCount;
            var completeBytes = checked(payloadBytes + sizeof(uint));
            if (payloadBytes > MaximumPayloadBytes || destination.Length < completeBytes)
            {
                error = "encoded-frame-too-large";
                return false;
            }
            BinaryPrimitives.WriteUInt32LittleEndian(scratch, (uint)payloadBytes);
            if (!reservation.TryCommit(frozenEnvelope, completeBytes, out error)) return false;
            scratch.AsSpan(0, completeBytes).CopyTo(destination);
            written = completeBytes;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException)
        {
            error = $"encode-rejected:{exception.Message}";
            return false;
        }
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> frame,
        DiagnosticDecodeContext expected,
        DiagnosticTranscript transcript,
        out DiagnosticEnvelope? envelope,
        out string error,
        out DiagnosticDecodeObservation observation) =>
        TryDecodeCore(frame, expected, transcript, null, out envelope, out error, out observation);

    internal static bool TryDecodeHeldReturn(
        ReadOnlySpan<byte> frame,
        DiagnosticDecodeContext expected,
        DiagnosticTranscript transcript,
        DiagnosticHeldReturnAdmission admission,
        out DiagnosticEnvelope? envelope,
        out string error,
        out DiagnosticDecodeObservation observation) =>
        TryDecodeCore(frame, expected, transcript, admission, out envelope, out error, out observation);

    private static bool TryDecodeCore(
        ReadOnlySpan<byte> frame,
        DiagnosticDecodeContext expected,
        DiagnosticTranscript transcript,
        DiagnosticHeldReturnAdmission? heldAdmission,
        out DiagnosticEnvelope? envelope,
        out string error,
        out DiagnosticDecodeObservation observation)
    {
        envelope = null;
        error = string.Empty;
        observation = DiagnosticDecodeObservation.None;
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(transcript);
        if (!expected.IsFrozen)
            return RejectDecode(transcript, "decode-context-not-frozen", frame.Length, out error);
        if (frame.Length < sizeof(uint))
            return RejectDecode(transcript, "frame-prefix-missing", frame.Length, out error);
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        if (payloadLength > MaximumPayloadBytes)
            return RejectDecode(transcript, "frame-prefix-oversized", frame.Length, out error);
        if (frame.Length != sizeof(uint) + payloadLength)
            return RejectDecode(transcript, "frame-length-mismatch", frame.Length, out error);
        var heldInspectionCharged = false;
        if (!transcript.CanAcceptInbound(frame.Length))
        {
            if (heldAdmission is null || !transcript.TryBeginHeldInspection(heldAdmission, frame.Length))
                return RejectDecode(transcript, "ordinary-ingestion-stopped", frame.Length, out error);
            heldInspectionCharged = true;
        }
        var payload = frame[sizeof(uint)..];
        if (payload.Length >= 3 && payload[0] == 0xEF && payload[1] == 0xBB && payload[2] == 0xBF)
            return RejectDecode(transcript, "utf8-bom-rejected", frame.Length, heldAdmission,
                heldInspectionCharged, out error);
        DiagnosticFirstRawReturn? rejectedRawReturn = null;
        var rejectedOperationId = Guid.Empty;
        try
        {
            DiagnosticValidation.ValidateDecodeContext(expected);
            if (!ValidateJsonTokens(payload, out var kind, out var scanError))
                return RejectDecode(transcript, scanError, frame.Length, heldAdmission,
                    heldInspectionCharged, out error);
            ValidateExactSchema(payload, kind);
            object? wire = kind switch
            {
                DiagnosticEnvelopeKind.PreparedContext => Deserialize<DiagnosticPreparedContext>(payload),
                DiagnosticEnvelopeKind.CustodyAcknowledgment => Deserialize<DiagnosticCustodyAcknowledgment>(payload),
                DiagnosticEnvelopeKind.Command => Deserialize<DiagnosticCommand>(payload),
                DiagnosticEnvelopeKind.FirstRawReturn => Deserialize<DiagnosticFirstRawReturn>(payload),
                DiagnosticEnvelopeKind.ValueRowBatch => Deserialize<DiagnosticValueRowBatch>(payload),
                DiagnosticEnvelopeKind.DeliveryObservation => Deserialize<DiagnosticDeliveryObservation>(payload),
                DiagnosticEnvelopeKind.AbortExitFinalReport => Deserialize<DiagnosticAbortExitFinalReport>(payload),
                _ => null,
            };
            if (wire is not IEnvelopeWire decoded || decoded.BodyObject is null)
                return RejectDecode(transcript, "decoded-envelope-missing", frame.Length, heldAdmission,
                    heldInspectionCharged, out error);
            rejectedRawReturn = decoded.BodyObject as DiagnosticFirstRawReturn;
            rejectedOperationId = decoded.ToHeader().OperationId;
            var candidate = DiagnosticEnvelope.Create(decoded.ToHeader(), decoded.BodyObject);
            if (!transcript.TryPinExpected(expected, out var pinnedExpected, out var pinError))
            {
                if (rejectedRawReturn is not null)
                {
                    transcript.RecordRejectedReturn(rejectedRawReturn, pinnedExpected.Owner, rejectedOperationId,
                        "decode-context-drift");
                }
                return RejectDecode(transcript, pinError, frame.Length, heldAdmission,
                    heldInspectionCharged, out error);
            }
            DiagnosticValidation.ValidateExpected(candidate, pinnedExpected);
            bool protectedRejected;
            string recordError;
            var recorded = heldAdmission is null
                ? transcript.RecordDecoded(candidate, frame.Length, out protectedRejected, out recordError)
                : transcript.RecordDecodedHeldReturn(candidate, frame.Length, heldAdmission, heldInspectionCharged,
                    out protectedRejected, out recordError);
            if (!recorded)
                return RejectDecode(transcript, recordError, frame.Length, heldAdmission,
                    heldInspectionCharged, out error);
            observation = new(0, protectedRejected);
            envelope = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException or OverflowException)
        {
            if (rejectedRawReturn is not null)
            {
                transcript.RecordRejectedReturn(rejectedRawReturn, expected.Owner, rejectedOperationId,
                    "rejected-first-return-envelope");
            }
            return RejectDecode(transcript, $"decode-rejected:{exception.Message}", frame.Length, heldAdmission,
                heldInspectionCharged, out error);
        }
    }

    private static bool RejectDecode(
        DiagnosticTranscript transcript,
        string reason,
        int actualBytes,
        DiagnosticHeldReturnAdmission? heldAdmission,
        bool heldInspectionCharged,
        out string error)
    {
        error = reason;
        if (heldInspectionCharged && heldAdmission is not null)
        {
            transcript.CompleteRejectedHeldInspection(heldAdmission);
            transcript.RecordRejectedSummary(reason);
        }
        else
        {
            transcript.RecordRejected(reason, actualBytes);
        }
        return false;
    }

    private static bool RejectDecode(
        DiagnosticTranscript transcript,
        string reason,
        int actualBytes,
        out string error)
    {
        error = reason;
        transcript.RecordRejected(reason, actualBytes);
        return false;
    }

    private static void SerializeWire(Stream stream, DiagnosticEnvelope envelope)
    {
        switch (envelope.Kind)
        {
            case DiagnosticEnvelopeKind.PreparedContext:
                JsonSerializer.Serialize(stream, Wire(envelope, (DiagnosticPreparedContext)envelope.Body), JsonOptions);
                break;
            case DiagnosticEnvelopeKind.CustodyAcknowledgment:
                JsonSerializer.Serialize(stream, Wire(envelope, (DiagnosticCustodyAcknowledgment)envelope.Body), JsonOptions);
                break;
            case DiagnosticEnvelopeKind.Command:
                JsonSerializer.Serialize(stream, Wire(envelope, (DiagnosticCommand)envelope.Body), JsonOptions);
                break;
            case DiagnosticEnvelopeKind.FirstRawReturn:
                JsonSerializer.Serialize(stream, Wire(envelope, (DiagnosticFirstRawReturn)envelope.Body), JsonOptions);
                break;
            case DiagnosticEnvelopeKind.ValueRowBatch:
                JsonSerializer.Serialize(stream, Wire(envelope, (DiagnosticValueRowBatch)envelope.Body), JsonOptions);
                break;
            case DiagnosticEnvelopeKind.DeliveryObservation:
                JsonSerializer.Serialize(stream, Wire(envelope, (DiagnosticDeliveryObservation)envelope.Body), JsonOptions);
                break;
            case DiagnosticEnvelopeKind.AbortExitFinalReport:
                JsonSerializer.Serialize(stream, Wire(envelope, (DiagnosticAbortExitFinalReport)envelope.Body), JsonOptions);
                break;
            default: throw new ArgumentException("Unknown diagnostic envelope kind.");
        }
    }

    private static EnvelopeWire<T> Wire<T>(DiagnosticEnvelope envelope, T body) => new(
        envelope.Header.Schema, envelope.Header.Version, envelope.Header.Kind, envelope.Header.ContextDigest,
        envelope.Header.Epoch, envelope.Header.Sequence, envelope.Header.EventId, envelope.Header.OperationId, body);

    private static EnvelopeWire<T>? Deserialize<T>(ReadOnlySpan<byte> payload) =>
        JsonSerializer.Deserialize<EnvelopeWire<T>>(payload, JsonOptions);

    private static bool ValidateJsonTokens(
        ReadOnlySpan<byte> payload,
        out DiagnosticEnvelopeKind kind,
        out string error)
    {
        kind = default;
        error = string.Empty;
        var reader = new Utf8JsonReader(payload, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = DiagnosticValidation.MaximumDepth,
        });
        var containers = new Stack<JsonContainerState>();
        var tokens = 0;
        var rootKindFound = false;
        while (reader.Read())
        {
            if (++tokens > DiagnosticValidation.MaximumTokens)
            {
                error = "json-token-limit";
                return false;
            }
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    CountParentArrayElement(containers);
                    ClearParentProperty(containers);
                    containers.Push(new(true, 0));
                    break;
                case JsonTokenType.EndObject:
                    if (containers.Count == 0 || !containers.Pop().IsObject)
                    {
                        error = "json-object-balance-invalid";
                        return false;
                    }
                    break;
                case JsonTokenType.StartArray:
                    var arrayProperty = containers.Count > 0 && containers.Peek().IsObject
                        ? containers.Peek().PendingProperty
                        : null;
                    CountParentArrayElement(containers);
                    ClearParentProperty(containers);
                    containers.Push(new(false, ArrayLimit(arrayProperty)));
                    break;
                case JsonTokenType.EndArray:
                    if (containers.Count == 0 || containers.Pop().IsObject)
                    {
                        error = "json-array-balance-invalid";
                        return false;
                    }
                    break;
                case JsonTokenType.PropertyName:
                    if (reader.HasValueSequence || reader.ValueSpan.Length > DiagnosticValidation.MaximumNameBytes)
                    {
                        error = "json-property-name-width";
                        return false;
                    }
                    var name = reader.GetString() ?? string.Empty;
                    if (Encoding.UTF8.GetByteCount(name) > DiagnosticValidation.MaximumNameBytes ||
                        ContainsInvalidText(name))
                    {
                        error = "json-property-name-invalid";
                        return false;
                    }
                    if (containers.Count == 0 || !containers.Peek().IsObject ||
                        containers.Peek().Properties.Count >= DiagnosticValidation.MaximumProperties ||
                        !containers.Peek().Properties.Add(name))
                    {
                        error = "json-duplicate-property";
                        return false;
                    }
                    containers.Peek().PendingProperty = name;
                    break;
                case JsonTokenType.String:
                    if (reader.HasValueSequence || reader.ValueSpan.Length > DiagnosticValidation.MaximumTextBytes)
                    {
                        error = "json-string-width";
                        return false;
                    }
                    var value = reader.GetString() ?? string.Empty;
                    if (Encoding.UTF8.GetByteCount(value) > DiagnosticValidation.MaximumTextBytes || ContainsInvalidText(value))
                    {
                        error = "json-string-invalid";
                        return false;
                    }
                    if (containers.Count == 1 && containers.Peek().IsObject &&
                        containers.Peek().PendingProperty == "kind")
                    {
                        if (!Enum.TryParse(value, false, out kind) || !Enum.IsDefined(kind))
                        {
                            error = "record-kind-invalid";
                            return false;
                        }
                        rootKindFound = true;
                    }
                    CompleteScalar(containers, out error);
                    if (error.Length != 0) return false;
                    break;
                case JsonTokenType.Number:
                case JsonTokenType.True:
                case JsonTokenType.False:
                case JsonTokenType.Null:
                    CompleteScalar(containers, out error);
                    if (error.Length != 0) return false;
                    break;
            }
        }
        if (!rootKindFound)
        {
            error = "record-kind-missing";
            return false;
        }
        return true;
    }

    private static int ArrayLimit(string? property) => property switch
    {
        "expectedCustody" or "acknowledgedCustody" => 64,
        "positiveCoordinates" or "unknowns" => SupervisionPolicy.MaximumUnknownReturns,
        "tickets" => 128,
        "rows" => SupervisionPolicy.MaximumRows,
        _ => SupervisionPolicy.MaximumRows,
    };

    private static void CountParentArrayElement(Stack<JsonContainerState> containers)
    {
        if (containers.Count == 0 || containers.Peek().IsObject) return;
        var parent = containers.Peek();
        parent.ElementCount++;
        if (parent.ElementCount > parent.ArrayLimit) throw new JsonException("json-array-count-limit");
    }

    private static void ClearParentProperty(Stack<JsonContainerState> containers)
    {
        if (containers.Count > 0 && containers.Peek().IsObject) containers.Peek().PendingProperty = null;
    }

    private static void CompleteScalar(Stack<JsonContainerState> containers, out string error)
    {
        error = string.Empty;
        if (containers.Count == 0)
        {
            error = "json-root-scalar-invalid";
            return;
        }
        if (containers.Peek().IsObject) containers.Peek().PendingProperty = null;
        else
        {
            var array = containers.Peek();
            array.ElementCount++;
            if (array.ElementCount > array.ArrayLimit) error = "json-array-count-limit";
        }
    }

    private static bool ContainsInvalidText(string value)
    {
        return DiagnosticValidation.HasInvalidUtf16(value);
    }

    private static void ValidateExactSchema(ReadOnlySpan<byte> payload, DiagnosticEnvelopeKind kind)
    {
        using var document = JsonDocument.Parse(payload.ToArray());
        var root = document.RootElement;
        RequireObject(root, "schema", "version", "kind", "contextDigest", "epoch", "sequence", "eventId",
            "operationId", "body");
        var body = root.GetProperty("body");
        switch (kind)
        {
            case DiagnosticEnvelopeKind.PreparedContext:
                RequireObject(body, "runId", "sessionId", "attempt", "anchor", "selectedResource", "sourceDigest",
                    "binaryDigest", "sdkDigest", "osExpectation", "controller", "owner", "writer", "target",
                    "targetContext", "targetSlotId", "provenance", "custodyDigest", "valuesOnly", "expectedCustody",
                    "policy", "pClockDomain", "oClockDomain", "claims");
                RequireIdentity(body.GetProperty("controller"));
                RequireIdentity(body.GetProperty("owner"));
                RequireIdentity(body.GetProperty("writer"));
                RequireIdentity(body.GetProperty("target"));
                RequireArray(body.GetProperty("expectedCustody"), RequireSlot);
                RequireObject(body.GetProperty("policy"), "monotonicFrequency", "perCallTicks", "totalSampleTicks",
                    "lateCompletionGraceTicks", "reapObservationTicks");
                RequireClaims(body.GetProperty("claims"));
                break;
            case DiagnosticEnvelopeKind.CustodyAcknowledgment:
                RequireObject(body, "actor", "custodyDigest", "acknowledgedCustody", "claims");
                RequireIdentity(body.GetProperty("actor"));
                RequireArray(body.GetProperty("acknowledgedCustody"), RequireSlot);
                RequireClaims(body.GetProperty("claims"));
                break;
            case DiagnosticEnvelopeKind.Command:
                RequireObject(body, "issuer", "actor", "target", "kind", "descriptor", "reservedPayload",
                    "issuanceTicks", "deadlineTicks", "clockDomain", "claims");
                RequireIdentity(body.GetProperty("issuer"));
                RequireIdentity(body.GetProperty("actor"));
                RequireIdentity(body.GetProperty("target"));
                RequireNullable(body.GetProperty("descriptor"), RequireDescriptor);
                RequirePayload(body.GetProperty("reservedPayload"));
                RequireClaims(body.GetProperty("claims"));
                break;
            case DiagnosticEnvelopeKind.FirstRawReturn:
                RequireObject(body, "owner", "kind", "apiStatus", "positiveCoordinates", "descriptor",
                    "ownerObservedTicks", "ownerClockDomain", "ownerClockFrequency", "incompleteReason", "claims");
                RequireIdentity(body.GetProperty("owner"));
                RequireArray(body.GetProperty("positiveCoordinates"), static item => RequireKind(item, JsonValueKind.Number));
                RequireNullable(body.GetProperty("descriptor"), RequireDescriptor);
                RequireClaims(body.GetProperty("claims"));
                break;
            case DiagnosticEnvelopeKind.ValueRowBatch:
                RequireObject(body, "owner", "batchIndex", "isFinal", "rows", "claims");
                RequireIdentity(body.GetProperty("owner"));
                RequireArray(body.GetProperty("rows"), static row => RequireObject(row, "ordinal", "value"));
                RequireClaims(body.GetProperty("claims"));
                break;
            case DiagnosticEnvelopeKind.DeliveryObservation:
                RequireObject(body, "actor", "kind", "delivered", "ingestionTicks", "ingestionClockDomain", "claims");
                RequireIdentity(body.GetProperty("actor"));
                RequireClaims(body.GetProperty("claims"));
                break;
            case DiagnosticEnvelopeKind.AbortExitFinalReport:
                RequireObject(body, "state", "reason", "unknowns", "pending", "unknownOverflow", "tickets",
                    "termination", "jobObservation", "claims");
                RequireArray(body.GetProperty("unknowns"), RequireUnknown);
                RequireNullable(body.GetProperty("pending"), RequirePending);
                RequireArray(body.GetProperty("tickets"), static item => RequireKind(item, JsonValueKind.String));
                RequireNullable(body.GetProperty("termination"), RequireTermination);
                RequireClaims(body.GetProperty("claims"));
                break;
            default:
                throw new JsonException("record-kind-invalid");
        }
    }

    private static void RequireSlot(JsonElement value)
    {
        RequireObject(value, "logicalSlotId", "holder", "creator", "invokingDuplicator", "target", "objectClass",
            "coordinateKind", "coordinate", "coordinateOwner", "objectIdentity", "requestedAccess", "grantedAccess",
            "inheritable", "provenance");
        RequireIdentity(value.GetProperty("holder"));
        RequireIdentity(value.GetProperty("coordinateOwner"));
        RequireObject(value.GetProperty("objectIdentity"), "stableId", "resource", "name", "finalPath",
            "volumeSerialNumber", "fileId", "isDirectory", "isReparsePoint", "targetProcessId",
            "targetCreationFileTime", "threadId", "jobSlot", "leaseId", "generation");
    }

    private static void RequireIdentity(JsonElement value) =>
        RequireObject(value, "role", "processId", "creationFileTime");

    private static void RequireDescriptor(JsonElement value)
    {
        RequireObject(value, "kind", "coordinate", "owner", "acquisitionOperationId", "cleanupOnly");
        RequireIdentity(value.GetProperty("owner"));
    }

    private static void RequirePayload(JsonElement value) =>
        RequireObject(value, "rows", "copiedBytes", "encodedBytes", "maximumFrameBytes");

    private static void RequireClaims(JsonElement value) => RequireObject(value,
        "nativeCallsPerformed", "nativeCapturePerformed", "peerAuthenticated", "nativeAuthorityProven",
        "nativeDurabilityProven", "completeHandleInventoryProven", "zeroExtraInheritanceProven",
        "productionAdmissionGranted", "b1WriteReleaseIssued", "b1JournalWritesPerformed", "releaseApproved");

    private static void RequireUnknown(JsonElement value)
    {
        RequireObject(value, "kind", "observedOwner", "operationId", "apiStatus", "coordinate", "evidenceSource",
            "missingResultReason", "ownerConfirmedExited", "observedMonotonicTicks");
        RequireIdentity(value.GetProperty("observedOwner"));
    }

    private static void RequirePending(JsonElement value)
    {
        RequireObject(value, "kind", "operationId", "originalMonotonicTicks", "apiStatus", "coordinate", "descriptor",
            "payload");
        RequireNullable(value.GetProperty("descriptor"), RequireDescriptor);
        RequirePayload(value.GetProperty("payload"));
    }

    private static void RequireTermination(JsonElement value)
    {
        RequireObject(value, "target", "requestResult", "requestMonotonicTicks", "processSignaled", "exitCode",
            "exitFileTime", "exitMonotonicTicks", "jobObservation", "jobObservationMonotonicTicks");
        RequireIdentity(value.GetProperty("target"));
    }

    private static void RequireNullable(JsonElement value, Action<JsonElement> validate)
    {
        if (value.ValueKind == JsonValueKind.Null) return;
        validate(value);
    }

    private static void RequireArray(JsonElement value, Action<JsonElement> validate)
    {
        RequireKind(value, JsonValueKind.Array);
        foreach (var item in value.EnumerateArray()) validate(item);
    }

    private static void RequireObject(JsonElement value, params string[] names)
    {
        RequireKind(value, JsonValueKind.Object);
        var actual = value.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length || !actual.ToHashSet(StringComparer.Ordinal).SetEquals(names))
            throw new JsonException("object-property-set-invalid");
    }

    private static void RequireKind(JsonElement value, JsonValueKind kind)
    {
        if (value.ValueKind != kind) throw new JsonException("json-value-kind-invalid");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
            MaxDepth = DiagnosticValidation.MaximumDepth,
            PropertyNameCaseInsensitive = false,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        return options;
    }

    private interface IEnvelopeWire
    {
        object? BodyObject { get; }
        DiagnosticEnvelopeHeader ToHeader();
    }

    private sealed record EnvelopeWire<T>(
        string Schema,
        int Version,
        DiagnosticEnvelopeKind Kind,
        string ContextDigest,
        long Epoch,
        long Sequence,
        Guid EventId,
        Guid OperationId,
        T Body) : IEnvelopeWire
    {
        [JsonIgnore]
        public object? BodyObject => Body;
        public DiagnosticEnvelopeHeader ToHeader() =>
            new(Schema, Version, Kind, ContextDigest, Epoch, Sequence, EventId, OperationId);
    }

    private sealed class JsonContainerState(bool isObject, int arrayLimit)
    {
        internal bool IsObject { get; } = isObject;
        internal int ArrayLimit { get; } = arrayLimit;
        internal int ElementCount { get; set; }
        internal string? PendingProperty { get; set; }
        internal HashSet<string> Properties { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FixedMemoryWriteStream(byte[] buffer, int offset, int capacity) : Stream
    {
        private int _written;
        internal int WrittenCount => _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] target, int targetOffset, int count) => throw new NotSupportedException();
        public override long Seek(long seekOffset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] source, int sourceOffset, int count) =>
            Write(source.AsSpan(sourceOffset, count));
        public override void Write(ReadOnlySpan<byte> source)
        {
            if (source.Length > capacity - _written) throw new InvalidOperationException("fixed-buffer-overflow");
            source.CopyTo(buffer.AsSpan(offset + _written, source.Length));
            _written += source.Length;
        }
    }
}

internal static class DiagnosticValidation
{
    internal const int MaximumDepth = 24;
    internal const int MaximumTokens = 65_536;
    internal const int MaximumProperties = 64;
    internal const int MaximumNameBytes = 128;
    internal const int MaximumTextBytes = 64 * 1024;
    internal const int MaximumIdentifierBytes = 512;
    internal const int MaximumPathBytes = 4 * 1024;
    internal const int MaximumFinalReasonBytes = 128;
    internal const int MaximumEvidenceBytes = 128;
    internal const int MaximumTicketBytes = 48;
    private const int MaximumSlots = 64;
    private const int MaximumRows = SupervisionPolicy.MaximumRows;
    private const int MaximumCoordinates = 8;
    private const int MaximumTickets = 128;

    internal static void ValidateEnvelope(DiagnosticEnvelope envelope)
    {
        ValidateHeader(envelope.Header);
        _ = CopyBody(envelope.Kind, envelope.Body);
    }

    internal static bool ExactEnvelope(DiagnosticEnvelope left, DiagnosticEnvelope right)
    {
        if (left.Header != right.Header || left.Kind != right.Kind) return false;
        return (left.Body, right.Body) switch
        {
            (DiagnosticPreparedContext a, DiagnosticPreparedContext b) =>
                (a with { ExpectedCustody = [] }) == (b with { ExpectedCustody = [] }) &&
                a.ExpectedCustody.SequenceEqual(b.ExpectedCustody),
            (DiagnosticCustodyAcknowledgment a, DiagnosticCustodyAcknowledgment b) =>
                (a with { AcknowledgedCustody = [] }) == (b with { AcknowledgedCustody = [] }) &&
                a.AcknowledgedCustody.SequenceEqual(b.AcknowledgedCustody),
            (DiagnosticCommand a, DiagnosticCommand b) => a == b,
            (DiagnosticFirstRawReturn a, DiagnosticFirstRawReturn b) =>
                (a with { PositiveCoordinates = [] }) == (b with { PositiveCoordinates = [] }) &&
                a.PositiveCoordinates.SequenceEqual(b.PositiveCoordinates),
            (DiagnosticValueRowBatch a, DiagnosticValueRowBatch b) =>
                (a with { Rows = [] }) == (b with { Rows = [] }) &&
                a.Rows.SequenceEqual(b.Rows),
            (DiagnosticDeliveryObservation a, DiagnosticDeliveryObservation b) => a == b,
            (DiagnosticAbortExitFinalReport a, DiagnosticAbortExitFinalReport b) =>
                (a with { Unknowns = [], Tickets = [] }) == (b with { Unknowns = [], Tickets = [] }) &&
                a.Unknowns.SequenceEqual(b.Unknowns) && a.Tickets.SequenceEqual(b.Tickets, StringComparer.Ordinal),
            _ => false,
        };
    }

    internal static void ValidateHeader(DiagnosticEnvelopeHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        if (!string.Equals(header.Schema, DiagnosticEnvelopeCodec.Schema, StringComparison.Ordinal))
            throw new ArgumentException("schema-invalid");
        if (header.Version != DiagnosticEnvelopeCodec.Version) throw new ArgumentException("version-invalid");
        if (!Enum.IsDefined(header.Kind)) throw new ArgumentException("kind-invalid");
        ValidateDigest(header.ContextDigest, nameof(header.ContextDigest));
        if (header.Epoch <= 0 || header.Sequence <= 0 || header.EventId == Guid.Empty || header.OperationId == Guid.Empty)
            throw new ArgumentException("header-identity-invalid");
    }

    internal static void ValidateExpected(DiagnosticEnvelope envelope, DiagnosticDecodeContext expected)
    {
        ValidateDigest(expected.ContextDigest, nameof(expected.ContextDigest));
        if (envelope.Header.ContextDigest != expected.ContextDigest || envelope.Header.Epoch != expected.Epoch)
            throw new ArgumentException("context-or-epoch-mismatch");
        switch (envelope.Body)
        {
            case DiagnosticPreparedContext prepared:
                if (expected.SelectedResource is null || expected.SdkDigest is null ||
                    expected.OsExpectation is null || expected.CustodyDigest is null || expected.ExpectedCustody is null ||
                    prepared.RunId != expected.RunId || prepared.SessionId != expected.SessionId ||
                    prepared.Attempt != expected.Attempt || prepared.Anchor != expected.Anchor ||
                    prepared.SourceDigest != expected.SourceDigest || prepared.BinaryDigest != expected.BinaryDigest ||
                    prepared.SelectedResource != expected.SelectedResource || prepared.SdkDigest != expected.SdkDigest ||
                    prepared.OsExpectation != expected.OsExpectation || prepared.CustodyDigest != expected.CustodyDigest ||
                    !prepared.ExpectedCustody.SequenceEqual(expected.ExpectedCustody) ||
                    expected.ExpectedPolicy is null || prepared.Policy != expected.ExpectedPolicy ||
                    prepared.Controller != expected.Controller || prepared.Owner != expected.Owner ||
                    prepared.Writer != expected.Writer || prepared.Target != expected.Target ||
                    prepared.TargetContext != expected.TargetContext || prepared.TargetSlotId != expected.TargetSlotId ||
                    prepared.PClockDomain != expected.PClockDomain || prepared.OClockDomain != expected.OClockDomain)
                    throw new ArgumentException("prepared-context-mismatch");
                break;
            case DiagnosticCustodyAcknowledgment acknowledgement when acknowledgement.Actor != expected.Controller ||
                    expected.CustodyDigest is null || expected.ExpectedCustody is null ||
                    acknowledgement.CustodyDigest != expected.CustodyDigest ||
                    !acknowledgement.AcknowledgedCustody.SequenceEqual(expected.ExpectedCustody):
                throw new ArgumentException("custody-acknowledgment-mismatch");
            case DiagnosticCommand command when command.Issuer != expected.Controller || command.Actor != expected.Owner ||
                                                command.Target != expected.Target || command.ClockDomain != expected.PClockDomain:
                throw new ArgumentException("command-actor-mismatch");
            case DiagnosticCommand command when command.Descriptor is not null && command.Descriptor.Owner != expected.Owner:
                throw new ArgumentException("command-descriptor-owner-mismatch");
            case DiagnosticFirstRawReturn result when result.Owner != expected.Owner ||
                                                      result.OwnerClockDomain != expected.OClockDomain ||
                                                      result.OwnerClockFrequency != expected.OwnerClockFrequency:
                throw new ArgumentException("return-owner-mismatch");
            case DiagnosticFirstRawReturn result when result.Descriptor is not null && result.Descriptor.Owner != expected.Owner:
                throw new ArgumentException("return-descriptor-owner-mismatch");
            case DiagnosticValueRowBatch batch when batch.Owner != expected.Owner:
                throw new ArgumentException("row-owner-mismatch");
            case DiagnosticDeliveryObservation delivery when delivery.Actor != expected.Controller ||
                                                               delivery.IngestionClockDomain != expected.PClockDomain:
                throw new ArgumentException("delivery-actor-mismatch");
            case DiagnosticAbortExitFinalReport final when !FinalIdentitiesMatch(final, expected):
                throw new ArgumentException("final-context-identity-mismatch");
        }
    }

    internal static void ValidateDecodeContext(DiagnosticDecodeContext expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ValidateDigest(expected.ContextDigest, nameof(expected.ContextDigest));
        ValidateDigest(expected.SourceDigest, nameof(expected.SourceDigest));
        ValidateDigest(expected.BinaryDigest, nameof(expected.BinaryDigest));
        if (!expected.IsFrozen || expected.Epoch <= 0 || expected.RunId == Guid.Empty || expected.SessionId == Guid.Empty ||
            expected.OwnerClockFrequency <= 0 ||
            expected.Attempt <= 0 || expected.Anchor == InventoryAnchor.B0)
            throw new ArgumentException("decode-context-invalid");
        _ = CopyIdentity(expected.Controller);
        _ = CopyIdentity(expected.Owner);
        _ = CopyIdentity(expected.Writer);
        _ = CopyIdentity(expected.Target);
        _ = CopyText(expected.TargetSlotId, nameof(expected.TargetSlotId));
        _ = CopyText(expected.PClockDomain, nameof(expected.PClockDomain));
        _ = CopyText(expected.OClockDomain, nameof(expected.OClockDomain));
        if (expected.PClockDomain == expected.OClockDomain)
            throw new ArgumentException("clock-domains-must-be-distinct");
        if (expected.SelectedResource is not null)
            _ = CopyBoundedText(expected.SelectedResource, nameof(expected.SelectedResource), MaximumPathBytes, false);
        if (expected.SdkDigest is not null) ValidateDigest(expected.SdkDigest, nameof(expected.SdkDigest));
        if (expected.OsExpectation is not null)
            _ = CopyBoundedText(expected.OsExpectation, nameof(expected.OsExpectation), MaximumIdentifierBytes, false);
        if (expected.CustodyDigest is not null) ValidateDigest(expected.CustodyDigest, nameof(expected.CustodyDigest));
        if (expected.ExpectedCustody is not null) _ = CopySlots(expected.ExpectedCustody);
        if (expected.ExpectedPolicy is not null) _ = CopyPolicy(expected.ExpectedPolicy);
    }

    internal static DiagnosticDecodeContext CopyDecodeContext(DiagnosticDecodeContext value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value with
        {
            ContextDigest = CopyDigest(value.ContextDigest),
            SourceDigest = CopyDigest(value.SourceDigest),
            BinaryDigest = CopyDigest(value.BinaryDigest),
            Controller = CopyIdentity(value.Controller),
            Owner = CopyIdentity(value.Owner),
            Writer = CopyIdentity(value.Writer),
            Target = CopyIdentity(value.Target),
            TargetSlotId = CopyBoundedText(value.TargetSlotId, nameof(value.TargetSlotId), MaximumIdentifierBytes, false),
            PClockDomain = CopyBoundedText(value.PClockDomain, nameof(value.PClockDomain), MaximumIdentifierBytes, false),
            OClockDomain = CopyBoundedText(value.OClockDomain, nameof(value.OClockDomain), MaximumIdentifierBytes, false),
            SelectedResource = value.SelectedResource is null ? null : CopyBoundedText(value.SelectedResource,
                nameof(value.SelectedResource), MaximumPathBytes, false),
            SdkDigest = value.SdkDigest is null ? null : CopyDigest(value.SdkDigest),
            OsExpectation = value.OsExpectation is null ? null : CopyBoundedText(value.OsExpectation,
                nameof(value.OsExpectation), MaximumIdentifierBytes, false),
            CustodyDigest = value.CustodyDigest is null ? null : CopyDigest(value.CustodyDigest),
            ExpectedCustody = value.ExpectedCustody is null ? null : CopySlots(value.ExpectedCustody),
            ExpectedPolicy = value.ExpectedPolicy is null ? null : CopyPolicy(value.ExpectedPolicy),
            OwnerClockFrequency = value.OwnerClockFrequency,
            IsFrozen = true,
        };
    }

    internal static bool ExactDecodeContext(DiagnosticDecodeContext left, DiagnosticDecodeContext right)
    {
        var leftSlots = left.ExpectedCustody;
        var rightSlots = right.ExpectedCustody;
        return (left with { ExpectedCustody = null }) == (right with { ExpectedCustody = null }) &&
            (leftSlots is null && rightSlots is null || leftSlots is not null && rightSlots is not null &&
                leftSlots.SequenceEqual(rightSlots));
    }

    private static bool FinalIdentitiesMatch(DiagnosticAbortExitFinalReport final, DiagnosticDecodeContext expected) =>
        final.Unknowns.All(item => item.ObservedOwner == expected.Owner) &&
        (final.Pending?.Descriptor is null || final.Pending.Descriptor.Owner == expected.Owner) &&
        (final.Termination is null || final.Termination.Target == expected.Owner);

    internal static DiagnosticEnvelopeHeader CopyHeader(DiagnosticEnvelopeHeader header) => header with
    {
        Schema = CopyText(header.Schema, nameof(header.Schema)),
        ContextDigest = CopyText(header.ContextDigest, nameof(header.ContextDigest)),
    };

    internal static object CopyBody(DiagnosticEnvelopeKind kind, object body)
    {
        object copied = kind switch
        {
            DiagnosticEnvelopeKind.PreparedContext when body is DiagnosticPreparedContext value => CopyPrepared(value),
            DiagnosticEnvelopeKind.CustodyAcknowledgment when body is DiagnosticCustodyAcknowledgment value =>
                value with
                {
                    Actor = CopyIdentity(value.Actor),
                    CustodyDigest = CopyDigest(value.CustodyDigest),
                    AcknowledgedCustody = CopySlots(value.AcknowledgedCustody),
                    Claims = CopyClaims(value.Claims)
                },
            DiagnosticEnvelopeKind.Command when body is DiagnosticCommand value => value with
            {
                Issuer = CopyIdentity(value.Issuer),
                Actor = CopyIdentity(value.Actor),
                Target = CopyIdentity(value.Target),
                Descriptor = CopyDescriptor(value.Descriptor),
                ReservedPayload = CopyPayload(value.ReservedPayload),
                ClockDomain = CopyText(value.ClockDomain, nameof(value.ClockDomain)),
                Claims = CopyClaims(value.Claims),
            },
            DiagnosticEnvelopeKind.FirstRawReturn when body is DiagnosticFirstRawReturn value => value with
            {
                Owner = CopyIdentity(value.Owner),
                PositiveCoordinates = CopyCoordinates(value.PositiveCoordinates),
                Descriptor = CopyDescriptor(value.Descriptor),
                OwnerClockDomain = CopyText(value.OwnerClockDomain, nameof(value.OwnerClockDomain)),
                IncompleteReason = CopyOptionalText(value.IncompleteReason, MaximumFinalReasonBytes),
                Claims = CopyClaims(value.Claims),
            },
            DiagnosticEnvelopeKind.ValueRowBatch when body is DiagnosticValueRowBatch value => value with
            {
                Owner = CopyIdentity(value.Owner),
                Rows = CopyRows(value.Rows),
                Claims = CopyClaims(value.Claims),
            },
            DiagnosticEnvelopeKind.DeliveryObservation when body is DiagnosticDeliveryObservation value => value with
            {
                Actor = CopyIdentity(value.Actor),
                IngestionClockDomain = CopyText(value.IngestionClockDomain, nameof(value.IngestionClockDomain)),
                Claims = CopyClaims(value.Claims),
            },
            DiagnosticEnvelopeKind.AbortExitFinalReport when body is DiagnosticAbortExitFinalReport value => CopyFinal(value),
            _ => throw new ArgumentException("Envelope body does not match its kind.", nameof(body)),
        };
        ValidateBody(kind, copied);
        return copied;
    }

    internal static InventoryProcessIdentity CopyIdentity(InventoryProcessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!Enum.IsDefined(identity.Role) || identity.ProcessId <= 0 || identity.CreationFileTime <= 0)
            throw new ArgumentException("process-identity-invalid");
        return identity with { };
    }

    internal static ObserverDescriptor? CopyDescriptor(ObserverDescriptor? descriptor)
    {
        if (descriptor is null) return null;
        if (!Enum.IsDefined(descriptor.Kind) || descriptor.Coordinate <= 0 ||
            descriptor.AcquisitionOperationId == Guid.Empty)
            throw new ArgumentException("descriptor-invalid");
        return descriptor with { Owner = CopyIdentity(descriptor.Owner) };
    }

    internal static SupervisionUnknownObligation CopyUnknown(SupervisionUnknownObligation value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if ((value.Kind is not null && !Enum.IsDefined(value.Kind.Value)) || value.Coordinate is <= 0 ||
            value.ObservedMonotonicTicks <= 0)
            throw new ArgumentException("unknown-obligation-invalid");
        return value with
        {
            ObservedOwner = CopyIdentity(value.ObservedOwner),
            EvidenceSource = CopyBoundedText(value.EvidenceSource, nameof(value.EvidenceSource),
                MaximumEvidenceBytes, false),
            MissingResultReason = CopyBoundedText(value.MissingResultReason, nameof(value.MissingResultReason),
                MaximumFinalReasonBytes, false),
        };
    }

    internal static string CopyText(string value, string parameter)
    {
        return CopyBoundedText(value, parameter, MaximumTextBytes, false);
    }

    private static string CopyDigest(string value)
    {
        ValidateDigest(value, nameof(value));
        return new string(value.AsSpan());
    }

    private static void ValidateDigest(string value, string parameter)
    {
        if (value is null || value.Length != 64 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException("digest-invalid", parameter);
    }

    private static string? CopyOptionalText(string? value, int maximumBytes) => value is null
        ? null
        : CopyBoundedText(value, nameof(value), maximumBytes, false);

    private static string CopyBoundedText(string value, string parameter, int maximumBytes, bool allowEmpty)
    {
        ArgumentNullException.ThrowIfNull(value, parameter);
        if (HasInvalidUtf16(value)) throw new ArgumentException("text-invalid", parameter);
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if ((!allowEmpty && byteCount == 0) || byteCount > maximumBytes)
            throw new ArgumentException("text-invalid", parameter);
        return new string(value.AsSpan());
    }

    private static long ConservativeEscapedByteCount(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (HasInvalidUtf16(value)) throw new ArgumentException("text-invalid");
        return checked(value.Length * 6L);
    }

    private static DiagnosticPreparedContext CopyPrepared(DiagnosticPreparedContext value) => value with
    {
        SelectedResource = CopyBoundedText(value.SelectedResource, nameof(value.SelectedResource), MaximumPathBytes, false),
        SourceDigest = CopyDigest(value.SourceDigest),
        BinaryDigest = CopyDigest(value.BinaryDigest),
        SdkDigest = CopyDigest(value.SdkDigest),
        OsExpectation = CopyBoundedText(value.OsExpectation,
            nameof(value.OsExpectation), MaximumIdentifierBytes, false),
        Controller = CopyIdentity(value.Controller),
        Owner = CopyIdentity(value.Owner),
        Writer = CopyIdentity(value.Writer),
        Target = CopyIdentity(value.Target),
        TargetSlotId = CopyBoundedText(value.TargetSlotId,
            nameof(value.TargetSlotId), MaximumIdentifierBytes, false),
        CustodyDigest = CopyDigest(value.CustodyDigest),
        ExpectedCustody = CopySlots(value.ExpectedCustody),
        Policy = CopyPolicy(value.Policy),
        PClockDomain = CopyText(value.PClockDomain, nameof(value.PClockDomain)),
        OClockDomain = CopyText(value.OClockDomain, nameof(value.OClockDomain)),
        Claims = CopyClaims(value.Claims),
    };

    private static DiagnosticAbortExitFinalReport CopyFinal(DiagnosticAbortExitFinalReport value)
    {
        if (value.Unknowns is null || value.Unknowns.Count > SupervisionPolicy.MaximumUnknownReturns ||
            value.Tickets is null || value.Tickets.Count > MaximumTickets)
            throw new ArgumentException("final-collection-bound-invalid");
        if (value.Unknowns.Any(item => item.OperationId == Guid.Empty))
            throw new ArgumentException("final-unknown-operation-identity-invalid");
        if (value.Termination is not null && value.Termination.JobObservation != value.JobObservation)
            throw new ArgumentException("final-job-observation-mismatch");
        long conservativeWireTextBytes = ConservativeEscapedByteCount(value.Reason);
        foreach (var unknown in value.Unknowns)
        {
            if (unknown is null) throw new ArgumentException("final-unknown-null");
            conservativeWireTextBytes = checked(conservativeWireTextBytes +
                ConservativeEscapedByteCount(unknown.EvidenceSource) +
                ConservativeEscapedByteCount(unknown.MissingResultReason));
        }
        foreach (var ticket in value.Tickets)
            conservativeWireTextBytes = checked(conservativeWireTextBytes + ConservativeEscapedByteCount(ticket));
        if (conservativeWireTextBytes > DiagnosticEnvelopeCodec.MaximumPayloadBytes - 8_192)
            throw new ArgumentException("final-conservative-wire-text-limit");
        return value with
        {
            Reason = CopyBoundedText(value.Reason, nameof(value.Reason), MaximumFinalReasonBytes, false),
            Unknowns = new ReadOnlyCollection<SupervisionUnknownObligation>(value.Unknowns.Select(CopyUnknown).ToArray()),
            Pending = CopyPending(value.Pending),
            Tickets = new ReadOnlyCollection<string>(value.Tickets.Select(item => CopyBoundedText(item,
                nameof(value.Tickets), MaximumTicketBytes, false)).ToArray()),
            Termination = CopyTermination(value.Termination),
            Claims = CopyClaims(value.Claims),
        };
    }

    private static IReadOnlyList<SupervisionCapabilitySlot> CopySlots(IReadOnlyList<SupervisionCapabilitySlot> slots)
    {
        if (slots is null || slots.Count > MaximumSlots) throw new ArgumentException("slot-count-invalid");
        var result = new SupervisionCapabilitySlot[slots.Count];
        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index] ?? throw new ArgumentException("slot-null");
            if (!Enum.IsDefined(slot.Creator) || (slot.InvokingDuplicator is not null &&
                    !Enum.IsDefined(slot.InvokingDuplicator.Value)) || (slot.Target is not null &&
                    !Enum.IsDefined(slot.Target.Value)) || !Enum.IsDefined(slot.ObjectClass) ||
                !Enum.IsDefined(slot.CoordinateKind) || slot.Provenance != InventoryMeasurementProvenance.Synthetic ||
                (slot.CoordinateKind == InventoryCoordinateKind.Owned && slot.Coordinate is <= 0) ||
                slot.RequestedAccess is null || slot.GrantedAccess is null || slot.Inheritable is null)
                throw new ArgumentException("slot-semantics-invalid");
            result[index] = slot with
            {
                LogicalSlotId = CopyBoundedText(slot.LogicalSlotId, nameof(slot.LogicalSlotId),
                    MaximumIdentifierBytes, false),
                Holder = CopyIdentity(slot.Holder),
                CoordinateOwner = CopyIdentity(slot.CoordinateOwner),
                ObjectIdentity = CopyObjectIdentity(slot.ObjectIdentity),
            };
        }
        return new ReadOnlyCollection<SupervisionCapabilitySlot>(result);
    }

    private static InventoryObjectIdentity CopyObjectIdentity(InventoryObjectIdentity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value with
        {
            StableId = CopyBoundedText(value.StableId, nameof(value.StableId), MaximumIdentifierBytes, false),
            Resource = CopyOptionalText(value.Resource, MaximumIdentifierBytes),
            Name = CopyOptionalText(value.Name, MaximumIdentifierBytes),
            FinalPath = CopyOptionalText(value.FinalPath, MaximumPathBytes),
            FileId = CopyOptionalText(value.FileId, MaximumIdentifierBytes),
            JobSlot = CopyOptionalText(value.JobSlot, MaximumIdentifierBytes),
        };
    }

    private static IReadOnlyList<long> CopyCoordinates(IReadOnlyList<long> values)
    {
        if (values is null || values.Count > MaximumCoordinates || values.Any(value => value <= 0))
            throw new ArgumentException("positive-coordinate-bound-invalid");
        return Array.AsReadOnly(values.ToArray());
    }

    private static IReadOnlyList<DiagnosticValueRow> CopyRows(IReadOnlyList<DiagnosticValueRow> rows)
    {
        if (rows is null || rows.Count > MaximumRows) throw new ArgumentException("row-count-invalid");
        long totalBytes = 0;
        foreach (var row in rows)
        {
            if (row is null || HasInvalidUtf16(row.Value)) throw new ArgumentException("row-text-invalid");
            totalBytes = checked(totalBytes + Encoding.UTF8.GetByteCount(row.Value));
            if (totalBytes > DiagnosticEnvelopeCodec.MaximumPayloadBytes)
                throw new ArgumentException("row-aggregate-byte-limit");
        }
        var copied = new DiagnosticValueRow[rows.Count];
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index] ?? throw new ArgumentException("row-null");
            if (row.Ordinal < 0) throw new ArgumentException("row-ordinal-invalid");
            copied[index] = row with { Value = CopyTextAllowEmpty(row.Value, nameof(row.Value)) };
        }
        return Array.AsReadOnly(copied);
    }

    private static SupervisionPayloadBudget CopyPayload(SupervisionPayloadBudget payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Rows < 0 || payload.Rows > SupervisionPolicy.MaximumRows || payload.CopiedBytes < 0 ||
            payload.CopiedBytes > SupervisionPolicy.MaximumCopiedBytes || payload.EncodedBytes < 0 ||
            payload.EncodedBytes > SupervisionPolicy.MaximumEncodedBytes || payload.MaximumFrameBytes < 0 ||
            payload.MaximumFrameBytes > SupervisionPolicy.MaximumFrameBytes)
            throw new ArgumentException("payload-budget-invalid");
        return payload with { };
    }

    private static string CopyTextAllowEmpty(string value, string parameter) =>
        CopyBoundedText(value, parameter, MaximumTextBytes, true);

    internal static bool HasInvalidUtf16(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (char.IsHighSurrogate(value[index]))
            {
                if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1])) return true;
                index++;
            }
            else if (char.IsLowSurrogate(value[index]))
            {
                return true;
            }
        }
        return false;
    }

    private static SupervisionPendingCompletionSnapshot? CopyPending(SupervisionPendingCompletionSnapshot? value)
    {
        if (value is null) return null;
        if (!Enum.IsDefined(value.Kind) || value.OperationId == Guid.Empty || value.OriginalMonotonicTicks <= 0 ||
            value.Descriptor is not null &&
            ResourceKindFor(value.Kind) != value.Descriptor.Kind)
            throw new ArgumentException("pending-completion-invalid");
        return value with { Descriptor = CopyDescriptor(value.Descriptor), Payload = CopyPayload(value.Payload) };
    }

    private static SupervisionTerminationEvidence? CopyTermination(SupervisionTerminationEvidence? value)
    {
        if (value is null) return null;
        if (!Enum.IsDefined(value.JobObservation) || value.RequestMonotonicTicks is <= 0 || value.ExitFileTime is <= 0 ||
            value.ExitMonotonicTicks is <= 0 || value.JobObservationMonotonicTicks is <= 0 ||
            (!value.ProcessSignaled && (value.ExitCode is not null || value.ExitFileTime is not null ||
                value.ExitMonotonicTicks is not null)))
            throw new ArgumentException("termination-evidence-invalid");
        return value with { Target = CopyIdentity(value.Target) };
    }

    private static SupervisionPolicy CopyPolicy(SupervisionPolicy value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var expected = SupervisionPolicy.FromFrequency(value.MonotonicFrequency);
        if (value != expected) throw new ArgumentException("supervision-policy-invalid");
        return value with { };
    }

    private static DiagnosticProofClaims CopyClaims(DiagnosticProofClaims claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        if (claims.HasPositiveClaim) throw new ArgumentException("positive-proof-claim-rejected");
        return claims with { };
    }

    private static void ValidateBody(DiagnosticEnvelopeKind kind, object body)
    {
        DiagnosticProofClaims claims = body switch
        {
            DiagnosticPreparedContext value => value.Claims,
            DiagnosticCustodyAcknowledgment value => value.Claims,
            DiagnosticCommand value => value.Claims,
            DiagnosticFirstRawReturn value => value.Claims,
            DiagnosticValueRowBatch value => value.Claims,
            DiagnosticDeliveryObservation value => value.Claims,
            DiagnosticAbortExitFinalReport value => value.Claims,
            _ => throw new ArgumentException("body-type-invalid"),
        };
        _ = CopyClaims(claims);
        switch (body)
        {
            case DiagnosticPreparedContext value:
                if (value.RunId == Guid.Empty || value.SessionId == Guid.Empty || value.Attempt <= 0 ||
                    value.Anchor == InventoryAnchor.B0 || !Enum.IsDefined(value.Provenance) ||
                    value.Provenance is InventoryMeasurementProvenance.Unsupported or
                        InventoryMeasurementProvenance.QueryFailed or InventoryMeasurementProvenance.Missing ||
                    !value.ValuesOnly || value.PClockDomain == value.OClockDomain)
                    throw new ArgumentException("prepared-context-invalid");
                break;
            case DiagnosticCommand value:
                if (!Enum.IsDefined(value.Kind) || value.IssuanceTicks <= 0 || value.DeadlineTicks <= value.IssuanceTicks ||
                    value.ClockDomain.Length == 0) throw new ArgumentException("command-invalid");
                if (value.Descriptor is not null && ResourceKindFor(value.Kind) != value.Descriptor.Kind)
                    throw new ArgumentException("command-descriptor-kind-invalid");
                break;
            case DiagnosticFirstRawReturn value:
                if (!Enum.IsDefined(value.Kind) || value.OwnerObservedTicks <= 0 || value.OwnerClockFrequency <= 0)
                    throw new ArgumentException("first-return-invalid");
                if (value.Descriptor is not null && ResourceKindFor(value.Kind) != value.Descriptor.Kind)
                    throw new ArgumentException("return-descriptor-kind-invalid");
                break;
            case DiagnosticValueRowBatch value when value.BatchIndex < 0:
                throw new ArgumentException("row-batch-invalid");
            case DiagnosticDeliveryObservation value when !Enum.IsDefined(value.Kind) || value.IngestionTicks <= 0:
                throw new ArgumentException("delivery-invalid");
            case DiagnosticAbortExitFinalReport value when !Enum.IsDefined(value.State) ||
                                                               !Enum.IsDefined(value.JobObservation) ||
                                                               value.State is CollectorSupervisionState.Unsupported or
                                                                   CollectorSupervisionState.Prepared or
                                                                   CollectorSupervisionState.CallInFlight:
                throw new ArgumentException("final-report-invalid");
        }
    }

    private static ObserverResourceKind? ResourceKindFor(SupervisionCallKind kind) => kind switch
    {
        SupervisionCallKind.AcquireMarker or SupervisionCallKind.ReleaseMarker => ObserverResourceKind.Marker,
        SupervisionCallKind.AcquireSnapshot or SupervisionCallKind.ReleaseSnapshot => ObserverResourceKind.Snapshot,
        SupervisionCallKind.Walk => null,
        _ => null,
    };
}
