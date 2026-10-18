using System.Collections.ObjectModel;
using System.Text;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal enum RuntimeBoundaryLane
{
    ManagementP,
    OwnerO,
}

internal enum RuntimeBoundaryEventKind
{
    OperationCommit,
    PStop,
    LocalOStop,
    DeadlineObserved,
    CleanupGraceElapsed,
    OwnerPhaseStarted,
    OwnerPhaseEnded,
    WriterPhaseStarted,
    WriterPhaseEnded,
    OwnerExit,
    OAdmission,
    EntryObservation,
    FirstRawReturn,
    Publication,
    Delivery,
    LeaseAcquired,
    CancellationRequest,
    Timeout,
    UseCompletion,
    Drain,
    ReportFrozen,
    OutputFailure,
}

internal enum RuntimeBoundaryOperationStage
{
    Committed,
    OwnerAdmitted,
    EntryObserved,
    EntryMissing,
    Returned,
    Published,
    PublicationFailed,
    Delivered,
    Unresolved,
}

internal enum RuntimeBoundaryEntryDisposition
{
    Unobserved,
    Observed,
    Missing,
}

internal enum RuntimeBoundaryPublicationDisposition
{
    Unobserved,
    Completed,
    Failed,
    Lost,
}

internal enum RuntimeBoundaryDeliveryDisposition
{
    Unobserved,
    Delivered,
    DeliveredAfterDeadline,
    DeliveredAfterCutoff,
}

internal enum RuntimeBoundaryLocalOClosure
{
    Missing,
    ObservedBeforeAdmission,
    ObservedAfterAdmission,
}

internal enum RuntimeBoundaryPhaseKind
{
    Owner,
    Writer,
}

internal enum RuntimeBoundaryPhaseDisposition
{
    NotStarted,
    Active,
    Completed,
    Missing,
    SkippedExpired,
}

internal enum RuntimeBoundaryWriterDisposition
{
    Unrepresented,
}

internal enum RuntimeBoundaryLeasePurpose
{
    OwnerObservation,
    WriterObservation,
    PublicationObservation,
    ExitObservation,
}

internal sealed record RuntimeBoundaryObservation(
    Guid RunId,
    Guid SessionId,
    InventoryAnchor Anchor,
    int Attempt,
    string ContextDigest,
    long Epoch,
    Guid EventId,
    RuntimeBoundaryLane Lane,
    InventoryProcessIdentity Actor,
    long ObservedTicks,
    string ClockDomain,
    long ClockFrequency,
    RuntimeBoundaryEventKind Kind,
    Guid? OperationId = null,
    string? TicketId = null);

internal sealed record RuntimeBoundarySlotReference(
    string LogicalSlotId,
    Guid LeaseId,
    long Generation,
    InventoryProcessIdentity Holder,
    InventoryProcessIdentity CoordinateOwner);

internal sealed record RuntimeBoundaryOperationCommit(
    RuntimeBoundaryObservation Observation,
    Guid OperationId,
    SupervisionCallKind Kind,
    RuntimeBoundarySlotReference Slot);

internal sealed record RuntimeBoundaryOwnerAdmission(
    RuntimeBoundaryObservation Observation,
    Guid OperationId,
    RuntimeBoundarySlotReference Slot);

internal sealed record RuntimeBoundaryLeaseRequest(
    RuntimeBoundaryObservation Observation,
    string TicketId,
    Guid OperationId,
    RuntimeBoundarySlotReference Slot,
    RuntimeBoundaryLane BorrowerLane,
    InventoryProcessIdentity Borrower,
    RuntimeBoundaryLeasePurpose Purpose);

internal sealed record RuntimeBoundaryEventRecord(
    int Ordinal,
    RuntimeBoundaryEventKind Kind,
    Guid EventId,
    Guid? OperationId,
    string? TicketId,
    RuntimeBoundaryLane Lane,
    long ObservedTicks,
    bool Accepted,
    string Reason);

internal sealed record RuntimeBoundaryUnknown(
    Guid OperationId,
    ObserverResourceKind? Kind,
    InventoryProcessIdentity Owner,
    uint ApiStatus,
    long? Coordinate,
    long OwnerObservedTicks,
    string OwnerClockDomain,
    long OwnerClockFrequency,
    string Reason);

internal sealed record RuntimeBoundaryOperationSnapshot(
    Guid OperationId,
    SupervisionCallKind Kind,
    RuntimeBoundaryOperationStage Stage,
    RuntimeBoundarySlotReference Slot,
    long CommitmentTicks,
    long DeadlineTicks,
    bool OwnerAdmitted,
    RuntimeBoundaryEntryDisposition Entry,
    DiagnosticFirstRawReturn? FirstRawReturn,
    DiagnosticFirstRawReturn? FirstAdverseRawReturn,
    RuntimeBoundaryPublicationDisposition Publication,
    RuntimeBoundaryDeliveryDisposition Delivery,
    long? DeliveryTicks,
    int RemainingTerminalHolds);

internal sealed record RuntimeBoundaryLeaseSnapshot(
    string TicketId,
    Guid OperationId,
    RuntimeBoundarySlotReference Slot,
    RuntimeBoundaryLane BorrowerLane,
    InventoryProcessIdentity Borrower,
    RuntimeBoundaryLeasePurpose Purpose,
    bool CancellationRequested,
    bool TimeoutObserved,
    bool UseCompleted,
    bool Drained,
    int RemainingTerminalHolds);

internal sealed record RuntimeBoundaryPhaseSnapshot(
    RuntimeBoundaryPhaseKind Kind,
    RuntimeBoundaryPhaseDisposition Disposition,
    long? StartedTicks,
    long? AnchoredCeilingTicks,
    long? ActualEndTicks);

internal sealed record RuntimeBoundaryPendingSnapshot(
    Guid OperationId,
    DiagnosticFirstRawReturn RawReturn,
    long DeliveryTicks,
    RuntimeBoundaryDeliveryDisposition Disposition);

internal sealed record RuntimeBoundaryReport(
    long FrozenTicks,
    long? SampleStartedTicks,
    long? FirstAbortTicks,
    long? OuterCutoffTicks,
    long? ReportReservationTicks,
    long? CleanupCeilingTicks,
    RuntimeBoundaryLocalOClosure LocalOClosure,
    RuntimeBoundaryWriterDisposition WriterDisposition,
    bool OwnerExitObserved,
    bool UnknownOverflow,
    IReadOnlyList<RuntimeBoundaryOperationSnapshot> Operations,
    IReadOnlyList<RuntimeBoundaryLeaseSnapshot> Leases,
    IReadOnlyList<RuntimeBoundaryUnknown> Unknowns,
    RuntimeBoundaryPendingSnapshot? Pending,
    string Reason)
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

internal sealed record RuntimeBoundarySnapshot(
    bool PAdmissionClosed,
    bool LeaseAdmissionClosed,
    RuntimeBoundaryLocalOClosure LocalOClosure,
    RuntimeBoundaryWriterDisposition WriterDisposition,
    long LastPTicks,
    long? LastOTicks,
    long? SampleStartedTicks,
    long? FirstAbortTicks,
    long? OuterCutoffTicks,
    long? ReportReservationTicks,
    long? CleanupCeilingTicks,
    long? OwnerPhaseCeilingTicks,
    long? WriterPhaseCeilingTicks,
    RuntimeBoundaryPhaseSnapshot OwnerPhase,
    RuntimeBoundaryPhaseSnapshot WriterPhase,
    bool OwnerExitObserved,
    bool UnknownOverflow,
    bool OutputFailureObserved,
    bool DescriptorReleaseSupported,
    int RetainedEvents,
    int RemainingFixedHolds,
    int RemainingOperationTerminalHolds,
    int RemainingLeaseTerminalHolds,
    int OrdinaryRecords,
    IReadOnlyList<RuntimeBoundaryOperationSnapshot> Operations,
    IReadOnlyList<RuntimeBoundaryLeaseSnapshot> Leases,
    IReadOnlyList<RuntimeBoundaryEventRecord> Events,
    IReadOnlyList<RuntimeBoundaryUnknown> Unknowns,
    RuntimeBoundaryPendingSnapshot? Pending,
    RuntimeBoundaryReport? Report)
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

internal sealed record RuntimeBoundaryTransition(
    bool Accepted,
    string Reason,
    RuntimeBoundarySnapshot State);

// A pure reducer over manually supplied metadata. It performs no work on a capability and has no
// callback, clock, scheduler, transport, wire, native, process, task, or thread dependency.
internal sealed class RuntimeBoundaryReducer
{
    internal const int MaximumOperations = SupervisionPolicy.MaximumCalls;
    internal const int MaximumLeases = SupervisionPolicy.MaximumEvents;
    internal const int MaximumEvents = 256;
    internal const int MaximumOrdinaryRecords = 64;
    internal const int MaximumUnknowns = SupervisionPolicy.MaximumUnknownReturns;
    internal const int FixedManagementHolds = 12;
    internal const int OperationTerminalHolds = 5;
    internal const int LeaseTerminalHolds = 4;

    private const int MaximumRejectedSummaries = 16;
    private readonly DiagnosticDecodeContext _expected;
    private readonly SupervisionPolicy _policy;
    private readonly RuntimeBoundaryOperationState?[] _operations = new RuntimeBoundaryOperationState[MaximumOperations];
    private readonly RuntimeBoundaryLeaseState?[] _leases = new RuntimeBoundaryLeaseState[MaximumLeases];
    private readonly RuntimeBoundaryEventRecord?[] _events = new RuntimeBoundaryEventRecord[MaximumEvents];
    private readonly RuntimeBoundaryUnknown?[] _unknowns = new RuntimeBoundaryUnknown[MaximumUnknowns];
    private readonly RejectionSummary?[] _rejections = new RejectionSummary[MaximumRejectedSummaries];
    private readonly bool[] _fixedConsumed = new bool[FixedManagementHolds];
    private int _operationCount;
    private int _leaseCount;
    private int _eventCount;
    private int _unknownCount;
    private int _rejectionCount;
    private int _ordinaryRecords;
    private long _lastPTicks;
    private long? _lastOTicks;
    private long? _sampleStartedTicks;
    private long? _firstAbortTicks;
    private long? _outerCutoffTicks;
    private long? _reportReservationTicks;
    private long? _cleanupCeilingTicks;
    private long? _ownerPhaseCeilingTicks;
    private long? _writerPhaseCeilingTicks;
    private bool _pAdmissionClosed;
    private bool _leaseAdmissionClosed;
    private bool _projectionClosed;
    private bool _unknownOverflow;
    private bool _ownerExitObserved;
    private bool _outputFailureObserved;
    private RuntimeBoundaryLocalOClosure _localOClosure;
    private RuntimeBoundaryPhaseState _ownerPhase = new(RuntimeBoundaryPhaseKind.Owner);
    private RuntimeBoundaryPhaseState _writerPhase = new(RuntimeBoundaryPhaseKind.Writer);
    private RuntimeBoundaryPendingSnapshot? _pending;
    private RuntimeBoundaryReport? _report;

    internal RuntimeBoundaryReducer(
        DiagnosticDecodeContext frozenExpected,
        long initialAuthoritativePTicks,
        string initialPClockDomain,
        long initialPClockFrequency)
    {
        ArgumentNullException.ThrowIfNull(frozenExpected);
        DiagnosticValidation.ValidateDecodeContext(frozenExpected);
        if (!frozenExpected.IsFrozen || frozenExpected.ExpectedPolicy is null ||
            frozenExpected.ExpectedCustody is null || frozenExpected.ExpectedCustody.Count == 0)
            throw new ArgumentException("runtime-boundary-context-incomplete", nameof(frozenExpected));
        if (initialAuthoritativePTicks <= 0 || initialPClockFrequency <= 0 ||
            initialPClockDomain != frozenExpected.PClockDomain ||
            initialPClockFrequency != frozenExpected.ExpectedPolicy.MonotonicFrequency ||
            frozenExpected.ExpectedPolicy != SupervisionPolicy.FromFrequency(initialPClockFrequency))
            throw new ArgumentException("initial-authoritative-p-clock-invalid", nameof(initialAuthoritativePTicks));

        _ = CheckedAdd(initialAuthoritativePTicks, CheckedSeconds(initialPClockFrequency, 45));
        _ = CheckedSeconds(initialPClockFrequency, 5);
        _ = CheckedSeconds(initialPClockFrequency, 10);
        _ = CheckedSeconds(initialPClockFrequency, 15);
        _ = CheckedSeconds(initialPClockFrequency, 25);
        _ = CheckedSeconds(initialPClockFrequency, 30);

        var copied = DiagnosticValidation.CopyDecodeContext(frozenExpected);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in copied.ExpectedCustody!)
        {
            if (!seen.Add(slot.LogicalSlotId) || slot.ObjectIdentity.LeaseId is not { } leaseId ||
                leaseId == Guid.Empty || slot.ObjectIdentity.Generation is not > 0)
                throw new ArgumentException("runtime-boundary-custody-generation-or-lease-invalid",
                    nameof(frozenExpected));
        }
        _expected = copied;
        _policy = copied.ExpectedPolicy!;
        _lastPTicks = initialAuthoritativePTicks;
    }

    internal RuntimeBoundaryTransition CommitOperation(RuntimeBoundaryOperationCommit command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Observation);
        ArgumentNullException.ThrowIfNull(command.Slot);
        if (!TryValidateObservation(command.Observation, RuntimeBoundaryEventKind.OperationCommit,
                RuntimeBoundaryLane.ManagementP, command.OperationId, null, out var error))
            return Reject(command.Observation, error);
        if (command.OperationId == Guid.Empty || !Enum.IsDefined(command.Kind) ||
            FindOperation(command.OperationId) is not null)
            return Reject(command.Observation, "operation-token-invalid-or-replayed");
        if (!TryResolveSlot(command.Slot, out _, out error))
            return Reject(command.Observation, error);
        AdvanceDuePolicy(command.Observation, null);
        if (_sampleStartedTicks is { } existingSampleStart &&
            command.Observation.ObservedTicks >= CheckedAdd(existingSampleStart, _policy.TotalSampleTicks))
        {
            CloseFreshAdmission();
            _projectionClosed = true;
        }
        if (_pAdmissionClosed || _report is not null || _projectionClosed)
            return Reject(command.Observation, "p-operation-admission-closed");
        if (ActiveOperation() is not null)
            return Reject(command.Observation, "single-operation-already-outstanding");
        if (_operationCount >= MaximumOperations ||
            !CanReserveOrdinary(1, OperationTerminalHolds))
        {
            CloseFreshAdmission();
            return Reject(command.Observation, "operation-metadata-capacity-exhausted");
        }

        long sampleStart;
        long deadline;
        long outerCutoff;
        try
        {
            sampleStart = _sampleStartedTicks ?? command.Observation.ObservedTicks;
            deadline = Math.Min(
                CheckedAdd(command.Observation.ObservedTicks, _policy.PerCallTicks),
                CheckedAdd(sampleStart, _policy.TotalSampleTicks));
            outerCutoff = _outerCutoffTicks ??
                CheckedAdd(sampleStart, CheckedSeconds(_policy.MonotonicFrequency, 45));
        }
        catch (OverflowException)
        {
            return Reject(command.Observation, "operation-policy-horizon-overflow");
        }
        if (command.Observation.ObservedTicks >= deadline)
            return Reject(command.Observation, "operation-commitment-at-or-after-deadline");

        var operation = new RuntimeBoundaryOperationState(command.OperationId, command.Kind,
            CopySlot(command.Slot), command.Observation.ObservedTicks, deadline);
        _operations[_operationCount++] = operation;
        _sampleStartedTicks ??= sampleStart;
        _outerCutoffTicks ??= outerCutoff;
        _reportReservationTicks ??= CheckedSubtract(outerCutoff,
            CheckedSeconds(_policy.MonotonicFrequency, 5));
        _ordinaryRecords++;
        AcceptObservation(command.Observation, "operation-committed");
        return Success("operation-committed");
    }

    internal RuntimeBoundaryTransition ObservePStop(RuntimeBoundaryObservation observation)
    {
        if (!TryValidateObservation(observation, RuntimeBoundaryEventKind.PStop,
                RuntimeBoundaryLane.ManagementP, null, null, out var error))
            return Reject(observation, error);
        if (_report is not null || IsFixedConsumed(FixedControl.PStop))
            return Reject(observation, "p-stop-replayed-or-after-report");
        if (!CanConsumeFixed(FixedControl.PStop, _firstAbortTicks is null ? FixedControl.FirstAbort : null))
            return Reject(observation, "p-stop-fixed-capacity-unavailable");
        if (!TryCalculateAbort(observation.ObservedTicks, out var abort, out error))
            return Reject(observation, error);

        ConsumeFixed(FixedControl.PStop);
        if (_firstAbortTicks is null) ConsumeFixed(FixedControl.FirstAbort);
        ApplyAbort(abort);
        AcceptObservation(observation, "p-stop-observed");
        return Success("p-stop-observed-remote-o-closure-unchanged");
    }

    internal RuntimeBoundaryTransition ObserveDeadline(RuntimeBoundaryObservation observation)
    {
        if (observation is null) return Reject(null, "observation-missing");
        if (!TryValidateObservation(observation, RuntimeBoundaryEventKind.DeadlineObserved,
                RuntimeBoundaryLane.ManagementP, observation.OperationId, null, out var error))
            return Reject(observation, error);
        var operation = observation.OperationId is { } operationId ? FindOperation(operationId) : null;
        if (operation is null || observation.ObservedTicks < operation.DeadlineTicks ||
            IsFixedConsumed(FixedControl.DeadlineObserved) || _report is not null)
            return Reject(observation, "deadline-missing-early-replayed-or-after-report");
        if (!CanConsumeFixed(FixedControl.DeadlineObserved,
                _firstAbortTicks is null ? FixedControl.FirstAbort : null))
            return Reject(observation, "deadline-fixed-capacity-unavailable");
        if (!TryCalculateAbort(observation.ObservedTicks, out var abort, out error))
            return Reject(observation, error);

        ConsumeFixed(FixedControl.DeadlineObserved);
        if (_firstAbortTicks is null) ConsumeFixed(FixedControl.FirstAbort);
        ApplyAbort(abort);
        operation.Stage = RuntimeBoundaryOperationStage.Unresolved;
        AcceptObservation(observation, "deadline-observed");
        return Success("deadline-observed-sticky");
    }

    internal RuntimeBoundaryTransition ObserveCleanupGraceElapsed(RuntimeBoundaryObservation observation)
    {
        if (!TryValidateObservation(observation, RuntimeBoundaryEventKind.CleanupGraceElapsed,
                RuntimeBoundaryLane.ManagementP, null, null, out var error))
            return Reject(observation, error);
        if (_cleanupCeilingTicks is null || observation.ObservedTicks < _cleanupCeilingTicks ||
            IsFixedConsumed(FixedControl.CleanupGraceElapsed) || _report is not null)
            return Reject(observation, "cleanup-grace-not-due-replayed-or-after-report");
        ConsumeFixed(FixedControl.CleanupGraceElapsed);
        _pAdmissionClosed = true;
        _leaseAdmissionClosed = true;
        AcceptObservation(observation, "cleanup-grace-elapsed");
        return Success("cleanup-grace-elapsed-no-cleanup-free-admitted");
    }

    internal RuntimeBoundaryTransition ObserveLocalOStop(RuntimeBoundaryObservation observation)
    {
        if (!TryValidateObservation(observation, RuntimeBoundaryEventKind.LocalOStop,
                RuntimeBoundaryLane.OwnerO, null, null, out var error))
            return Reject(observation, error);
        if (IsFixedConsumed(FixedControl.LocalOStop))
            return Reject(observation, "local-o-stop-replayed");
        ConsumeFixed(FixedControl.LocalOStop);
        _localOClosure = OwnerAdmissionObserved()
            ? RuntimeBoundaryLocalOClosure.ObservedAfterAdmission
            : RuntimeBoundaryLocalOClosure.ObservedBeforeAdmission;
        AcceptObservation(observation, _report is null
            ? "local-o-stop-observed"
            : "local-o-stop-observed-after-report-live-only");
        return Success(_localOClosure == RuntimeBoundaryLocalOClosure.ObservedBeforeAdmission
            ? "local-o-stop-prevents-later-admission"
            : "local-o-stop-after-admission-proves-no-prevention");
    }

    internal RuntimeBoundaryTransition AdmitOwner(RuntimeBoundaryOwnerAdmission admission)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(admission.Observation);
        ArgumentNullException.ThrowIfNull(admission.Slot);
        if (!TryValidateObservation(admission.Observation, RuntimeBoundaryEventKind.OAdmission,
                RuntimeBoundaryLane.OwnerO, admission.OperationId, null, out var error))
            return Reject(admission.Observation, error);
        var operation = FindOperation(admission.OperationId);
        if (operation is null || operation.OwnerAdmitted ||
            !operation.Holds[(int)OperationTerminal.OAdmission])
            return Reject(admission.Observation, "owner-admission-operation-missing-or-replayed");
        if (_localOClosure != RuntimeBoundaryLocalOClosure.Missing)
            return Reject(admission.Observation, "local-o-admission-closed");
        if (!TryResolveSlot(admission.Slot, out _, out error) || admission.Slot != operation.Slot)
            return Reject(admission.Observation, error.Length == 0 ? "owner-admission-slot-mismatch" : error);

        ConsumeOperationHold(operation, OperationTerminal.OAdmission);
        operation.OwnerAdmitted = true;
        operation.Stage = _firstAbortTicks is not null || _report is not null
            ? RuntimeBoundaryOperationStage.Unresolved
            : RuntimeBoundaryOperationStage.OwnerAdmitted;
        AcceptObservation(admission.Observation, _report is null
            ? "owner-token-admitted"
            : "owner-token-admitted-after-cutoff-from-precommit");
        return Success(_report is null
            ? "owner-token-admitted"
            : "owner-token-admitted-after-cutoff-from-precommit");
    }

    internal RuntimeBoundaryTransition ObserveEntry(
        RuntimeBoundaryObservation observation,
        RuntimeBoundaryEntryDisposition disposition)
    {
        if (observation is null) return Reject(null, "observation-missing");
        if (!TryValidateObservation(observation, RuntimeBoundaryEventKind.EntryObservation,
                RuntimeBoundaryLane.OwnerO, observation.OperationId, null, out var error))
            return Reject(observation, error);
        var operation = observation.OperationId is { } operationId ? FindOperation(operationId) : null;
        if (operation is null || !operation.OwnerAdmitted || operation.Entry != RuntimeBoundaryEntryDisposition.Unobserved ||
            disposition is not (RuntimeBoundaryEntryDisposition.Observed or RuntimeBoundaryEntryDisposition.Missing) ||
            !operation.Holds[(int)OperationTerminal.EntryObservation])
            return Reject(observation, "entry-observation-invalid-or-replayed");

        ConsumeOperationHold(operation, OperationTerminal.EntryObservation);
        operation.Entry = disposition;
        if (_firstAbortTicks is not null || _report is not null)
        {
            operation.Stage = RuntimeBoundaryOperationStage.Unresolved;
        }
        else if (operation.Stage == RuntimeBoundaryOperationStage.OwnerAdmitted)
        {
            operation.Stage = disposition == RuntimeBoundaryEntryDisposition.Observed
                ? RuntimeBoundaryOperationStage.EntryObserved
                : RuntimeBoundaryOperationStage.EntryMissing;
        }
        AcceptObservation(observation, disposition == RuntimeBoundaryEntryDisposition.Observed
            ? "api-entry-observed"
            : "api-entry-explicitly-missing");
        return Success(disposition == RuntimeBoundaryEntryDisposition.Observed
            ? "api-entry-observed"
            : "api-entry-explicitly-missing");
    }

    internal RuntimeBoundaryTransition RetainFirstRawReturn(
        RuntimeBoundaryObservation observation,
        DiagnosticFirstRawReturn supplied)
    {
        if (observation is null) return Reject(null, "observation-missing");
        ArgumentNullException.ThrowIfNull(supplied);
        if (!TryValidateObservation(observation, RuntimeBoundaryEventKind.FirstRawReturn,
                RuntimeBoundaryLane.OwnerO, observation.OperationId, null, out var error))
            return Reject(observation, error);
        DiagnosticFirstRawReturn copy;
        try
        {
            copy = (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(
                DiagnosticEnvelopeKind.FirstRawReturn, supplied);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           OverflowException)
        {
            return Reject(observation, $"raw-return-invalid:{BoundReason(exception.Message)}");
        }
        var operation = observation.OperationId is { } operationId ? FindOperation(operationId) : null;
        if (operation is null || !operation.OwnerAdmitted)
            return RejectWithAdverse(operation, observation, copy, "raw-return-without-owner-admission");
        if (copy.Kind != operation.Kind || copy.Owner != _expected.Owner ||
            copy.OwnerObservedTicks != observation.ObservedTicks ||
            copy.OwnerClockDomain != _expected.OClockDomain ||
            copy.OwnerClockFrequency != _expected.OwnerClockFrequency)
            return RejectWithAdverse(operation, observation, copy, "raw-return-binding-mismatch");
        if (operation.FirstRawReturn is not null)
        {
            var exact = ExactRaw(operation.FirstRawReturn, copy);
            return RejectWithAdverse(operation, observation, copy,
                exact ? "raw-return-replay" : "raw-return-conflict");
        }
        if (!operation.Holds[(int)OperationTerminal.FirstRawReturn])
            return RejectWithAdverse(operation, observation, copy, "raw-return-credit-unavailable");

        ConsumeOperationHold(operation, OperationTerminal.FirstRawReturn);
        operation.FirstRawReturn = copy;
        operation.Stage = _firstAbortTicks is not null || _report is not null
            ? RuntimeBoundaryOperationStage.Unresolved
            : RuntimeBoundaryOperationStage.Returned;
        if (ShouldPreserveUnknown(copy))
            PreserveUnknowns(operation.OperationId, copy, "raw-return-custody");
        AcceptObservation(observation, "first-raw-return-retained");
        return Success("first-raw-return-retained-before-publication");
    }

    internal RuntimeBoundaryTransition RecordPublication(
        RuntimeBoundaryObservation observation,
        RuntimeBoundaryPublicationDisposition disposition)
    {
        if (observation is null) return Reject(null, "observation-missing");
        if (!TryValidateObservation(observation, RuntimeBoundaryEventKind.Publication,
                RuntimeBoundaryLane.ManagementP, observation.OperationId, null, out var error))
            return Reject(observation, error);
        var operation = observation.OperationId is { } operationId ? FindOperation(operationId) : null;
        if (operation is null || operation.FirstRawReturn is null ||
            operation.Publication != RuntimeBoundaryPublicationDisposition.Unobserved ||
            !Enum.IsDefined(disposition) || disposition is RuntimeBoundaryPublicationDisposition.Unobserved ||
            !operation.Holds[(int)OperationTerminal.Publication])
            return Reject(observation, "publication-invalid-or-replayed");

        AdvanceDuePolicy(observation, observation.OperationId);
        ConsumeOperationHold(operation, OperationTerminal.Publication);
        operation.Publication = disposition;
        operation.Stage = _firstAbortTicks is not null || _report is not null
            ? RuntimeBoundaryOperationStage.Unresolved
            : disposition == RuntimeBoundaryPublicationDisposition.Completed
                ? RuntimeBoundaryOperationStage.Published
                : RuntimeBoundaryOperationStage.PublicationFailed;
        AcceptObservation(observation, disposition == RuntimeBoundaryPublicationDisposition.Completed
            ? "publication-completed"
            : "publication-failed-or-lost");
        return Success(disposition == RuntimeBoundaryPublicationDisposition.Completed
            ? "publication-completed"
            : "publication-failed-or-lost-returned-custody-retained");
    }

    internal RuntimeBoundaryTransition ObserveDelivery(RuntimeBoundaryObservation observation)
    {
        if (observation is null) return Reject(null, "observation-missing");
        if (!TryValidateObservation(observation, RuntimeBoundaryEventKind.Delivery,
                RuntimeBoundaryLane.ManagementP, observation.OperationId, null, out var error))
            return Reject(observation, error);
        var operation = observation.OperationId is { } operationId ? FindOperation(operationId) : null;
        if (operation is null || operation.FirstRawReturn is null ||
            operation.Publication != RuntimeBoundaryPublicationDisposition.Completed ||
            operation.Delivery != RuntimeBoundaryDeliveryDisposition.Unobserved ||
            !operation.Holds[(int)OperationTerminal.Delivery])
            return Reject(observation, "delivery-invalid-or-replayed");

        AdvanceDuePolicy(observation, observation.OperationId);
        ConsumeOperationHold(operation, OperationTerminal.Delivery);
        var disposition = _outerCutoffTicks is { } cutoff && observation.ObservedTicks >= cutoff
            ? RuntimeBoundaryDeliveryDisposition.DeliveredAfterCutoff
            : _firstAbortTicks is not null || observation.ObservedTicks >= operation.DeadlineTicks
                ? RuntimeBoundaryDeliveryDisposition.DeliveredAfterDeadline
                : RuntimeBoundaryDeliveryDisposition.Delivered;
        operation.Delivery = disposition;
        operation.DeliveryTicks = observation.ObservedTicks;
        operation.Stage = disposition == RuntimeBoundaryDeliveryDisposition.Delivered
            ? RuntimeBoundaryOperationStage.Delivered
            : RuntimeBoundaryOperationStage.Unresolved;
        if (disposition != RuntimeBoundaryDeliveryDisposition.Delivered && _pending is null)
            _pending = new(operation.OperationId, CopyRaw(operation.FirstRawReturn), observation.ObservedTicks,
                disposition);
        AcceptObservation(observation, disposition switch
        {
            RuntimeBoundaryDeliveryDisposition.Delivered => "delivery-admitted",
            RuntimeBoundaryDeliveryDisposition.DeliveredAfterDeadline => "delivery-retained-after-deadline",
            _ => "delivery-retained-after-cutoff",
        });
        return Success(disposition switch
        {
            RuntimeBoundaryDeliveryDisposition.Delivered => "delivery-admitted",
            RuntimeBoundaryDeliveryDisposition.DeliveredAfterDeadline => "delivery-retained-after-deadline",
            _ => "delivery-retained-after-cutoff",
        });
    }

    internal RuntimeBoundaryTransition AcquireLease(RuntimeBoundaryLeaseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Observation);
        ArgumentNullException.ThrowIfNull(request.Slot);
        if (!TryValidateObservation(request.Observation, RuntimeBoundaryEventKind.LeaseAcquired,
                RuntimeBoundaryLane.ManagementP, request.OperationId, request.TicketId, out var error))
            return Reject(request.Observation, error);
        if (!TryCopyTicket(request.TicketId, out var ticket, out error) || FindLease(ticket) is not null ||
            !Enum.IsDefined(request.BorrowerLane) || request.BorrowerLane == RuntimeBoundaryLane.ManagementP &&
            request.Borrower != _expected.Controller || request.BorrowerLane == RuntimeBoundaryLane.OwnerO &&
            request.Borrower != _expected.Owner || !Enum.IsDefined(request.Purpose))
            return Reject(request.Observation, error.Length == 0 ? "lease-binding-invalid-or-replayed" : error);
        var operation = FindOperation(request.OperationId);
        if (operation is null || !TryResolveSlot(request.Slot, out var trustedSlot, out error))
            return Reject(request.Observation, operation is null ? "lease-operation-missing" : error);
        if (request.Borrower != trustedSlot.Holder)
            return Reject(request.Observation, "lease-borrower-is-not-trusted-slot-holder");
        AdvanceDuePolicy(request.Observation, request.OperationId);
        if (_leaseAdmissionClosed || _pAdmissionClosed || _report is not null || _projectionClosed)
            return Reject(request.Observation, "lease-admission-closed");
        if (_leaseCount >= MaximumLeases || !CanReserveOrdinary(1, LeaseTerminalHolds))
        {
            CloseFreshAdmission();
            return Reject(request.Observation, "lease-metadata-capacity-exhausted");
        }

        _leases[_leaseCount++] = new(ticket, request.OperationId, CopySlot(request.Slot),
            request.BorrowerLane, DiagnosticValidation.CopyIdentity(request.Borrower), request.Purpose);
        _ordinaryRecords++;
        AcceptObservation(request.Observation, "observation-lease-acquired");
        return Success("observation-lease-acquired-no-work-executed");
    }

    internal RuntimeBoundaryTransition RequestLeaseCancellation(RuntimeBoundaryObservation observation) =>
        SetLeaseTerminal(observation, RuntimeBoundaryEventKind.CancellationRequest,
            LeaseTerminal.CancellationRequest, "lease-cancellation-requested");

    internal RuntimeBoundaryTransition ObserveLeaseTimeout(RuntimeBoundaryObservation observation) =>
        SetLeaseTerminal(observation, RuntimeBoundaryEventKind.Timeout,
            LeaseTerminal.Timeout, "lease-timeout-observed");

    internal RuntimeBoundaryTransition ObserveUseCompletion(RuntimeBoundaryObservation observation) =>
        SetLeaseTerminal(observation, RuntimeBoundaryEventKind.UseCompletion,
            LeaseTerminal.UseCompletion, "lease-use-completed");

    internal RuntimeBoundaryTransition ObserveLeaseDrain(RuntimeBoundaryObservation observation)
        => SetLeaseTerminal(observation, RuntimeBoundaryEventKind.Drain,
            LeaseTerminal.Drain, "lease-drained");

    internal bool SyntheticUseEligible(RuntimeBoundarySlotReference slot)
    {
        ArgumentNullException.ThrowIfNull(slot);
        if (!TryResolveSlot(slot, out _, out _)) return false;
        for (var index = 0; index < _leaseCount; index++)
        {
            var lease = _leases[index]!;
            if (lease.Slot == slot && !lease.Drained) return false;
        }
        return true;
    }

    internal RuntimeBoundaryTransition BeginPhase(
        RuntimeBoundaryObservation observation,
        RuntimeBoundaryPhaseKind kind)
    {
        if (!Enum.IsDefined(kind)) return Reject(observation, "phase-kind-invalid");
        var expectedKind = kind == RuntimeBoundaryPhaseKind.Owner
            ? RuntimeBoundaryEventKind.OwnerPhaseStarted
            : RuntimeBoundaryEventKind.WriterPhaseStarted;
        if (!TryValidateObservation(observation, expectedKind, RuntimeBoundaryLane.ManagementP,
                null, null, out var error))
            return Reject(observation, error);
        if (_report is not null || _firstAbortTicks is null || _reportReservationTicks is null ||
            !Enum.IsDefined(kind))
            return Reject(observation, "phase-start-without-abort-or-after-report");
        var phase = kind == RuntimeBoundaryPhaseKind.Owner ? _ownerPhase : _writerPhase;
        if (phase.Disposition != RuntimeBoundaryPhaseDisposition.NotStarted)
            return Reject(observation, "phase-start-replayed");
        if (kind == RuntimeBoundaryPhaseKind.Writer &&
            _ownerPhase.Disposition is not (RuntimeBoundaryPhaseDisposition.Completed or
                RuntimeBoundaryPhaseDisposition.Missing or RuntimeBoundaryPhaseDisposition.SkippedExpired))
            return Reject(observation, "writer-phase-before-owner-phase-ended");
        AdvanceDuePolicy(observation, null);
        var anchored = kind == RuntimeBoundaryPhaseKind.Owner
            ? _ownerPhaseCeilingTicks
            : _writerPhaseCeilingTicks;
        if (anchored is null || observation.ObservedTicks >= anchored ||
            observation.ObservedTicks >= _reportReservationTicks ||
            _outerCutoffTicks is { } cutoff && observation.ObservedTicks >= cutoff)
        {
            phase.Disposition = RuntimeBoundaryPhaseDisposition.SkippedExpired;
            _pAdmissionClosed = true;
            _leaseAdmissionClosed = true;
            _lastPTicks = observation.ObservedTicks;
            return Reject(observation, "phase-window-already-expired");
        }
        var control = kind == RuntimeBoundaryPhaseKind.Owner
            ? FixedControl.OwnerPhaseStarted
            : FixedControl.WriterPhaseStarted;
        if (IsFixedConsumed(control)) return Reject(observation, "phase-start-fixed-credit-replayed");
        long actualEnd;
        try
        {
            actualEnd = AddClampedTo(observation.ObservedTicks,
                CheckedSeconds(_policy.MonotonicFrequency, 10), anchored.Value);
        }
        catch (OverflowException)
        {
            return Reject(observation, "phase-window-overflow");
        }
        ConsumeFixed(control);
        phase.Disposition = RuntimeBoundaryPhaseDisposition.Active;
        phase.StartedTicks = observation.ObservedTicks;
        phase.AnchoredCeilingTicks = anchored;
        phase.ActualEndTicks = actualEnd;
        AcceptObservation(observation, kind == RuntimeBoundaryPhaseKind.Owner
            ? "owner-phase-started"
            : "writer-phase-started-unrepresented");
        return Success(kind == RuntimeBoundaryPhaseKind.Owner
            ? "owner-phase-started"
            : "writer-phase-started-w-remains-unrepresented");
    }

    internal RuntimeBoundaryTransition EndPhase(
        RuntimeBoundaryObservation observation,
        RuntimeBoundaryPhaseKind kind,
        bool evidenceObserved)
    {
        if (!Enum.IsDefined(kind)) return Reject(observation, "phase-kind-invalid");
        var expectedKind = kind == RuntimeBoundaryPhaseKind.Owner
            ? RuntimeBoundaryEventKind.OwnerPhaseEnded
            : RuntimeBoundaryEventKind.WriterPhaseEnded;
        if (!TryValidateObservation(observation, expectedKind, RuntimeBoundaryLane.ManagementP,
                null, null, out var error))
            return Reject(observation, error);
        if (_report is not null)
            return Reject(observation, "phase-end-after-report");
        var phase = kind == RuntimeBoundaryPhaseKind.Owner ? _ownerPhase : _writerPhase;
        if (phase.Disposition != RuntimeBoundaryPhaseDisposition.Active || phase.ActualEndTicks is null)
            return Reject(observation, "phase-end-without-active-phase");
        var control = kind == RuntimeBoundaryPhaseKind.Owner
            ? FixedControl.OwnerPhaseEnded
            : FixedControl.WriterPhaseEnded;
        if (IsFixedConsumed(control)) return Reject(observation, "phase-end-fixed-credit-replayed");
        ConsumeFixed(control);
        phase.Disposition = evidenceObserved && observation.ObservedTicks < phase.ActualEndTicks
            ? RuntimeBoundaryPhaseDisposition.Completed
            : RuntimeBoundaryPhaseDisposition.Missing;
        AcceptObservation(observation, phase.Disposition == RuntimeBoundaryPhaseDisposition.Completed
            ? "phase-completed-from-supplied-evidence"
            : "phase-ended-with-missing-evidence");
        return Success(phase.Disposition == RuntimeBoundaryPhaseDisposition.Completed
            ? "phase-completed-from-supplied-evidence"
            : "phase-ended-with-missing-evidence");
    }

    internal RuntimeBoundaryTransition ObserveOwnerExit(RuntimeBoundaryObservation observation)
    {
        if (!TryValidateObservation(observation, RuntimeBoundaryEventKind.OwnerExit,
                RuntimeBoundaryLane.ManagementP, null, null, out var error))
            return Reject(observation, error);
        if (_report is not null || IsFixedConsumed(FixedControl.OwnerExit))
            return Reject(observation, "owner-exit-replayed-or-after-report");
        AdvanceDuePolicy(observation, null);
        if (_reportReservationTicks is { } reservation && observation.ObservedTicks >= reservation ||
            _outerCutoffTicks is { } cutoff && observation.ObservedTicks >= cutoff)
            return Reject(observation, "owner-exit-observation-at-or-after-reserved-report-window");
        ConsumeFixed(FixedControl.OwnerExit);
        _ownerExitObserved = true;
        AcceptObservation(observation, "owner-exit-observed-w-unrepresented");
        return Success("owner-exit-observed-w-unrepresented");
    }

    internal RuntimeBoundaryTransition FreezeReport(RuntimeBoundaryObservation observation, string reason)
    {
        if (!TryValidateObservation(observation, RuntimeBoundaryEventKind.ReportFrozen,
                RuntimeBoundaryLane.ManagementP, null, null, out var error))
            return Reject(observation, error);
        if (_report is not null || _outerCutoffTicks is null || observation.ObservedTicks < _outerCutoffTicks ||
            IsFixedConsumed(FixedControl.ReportFrozen) || !TryCopyReason(reason, out var boundedReason, out error))
            return Reject(observation, error.Length == 0 ? "report-not-due-replayed-or-invalid" : error);
        AdvanceDuePolicy(observation, null);
        ConsumeFixed(FixedControl.ReportFrozen);
        _pAdmissionClosed = true;
        _leaseAdmissionClosed = true;
        _projectionClosed = true;
        _report = new(
            observation.ObservedTicks,
            _sampleStartedTicks,
            _firstAbortTicks,
            _outerCutoffTicks,
            _reportReservationTicks,
            _cleanupCeilingTicks,
            _localOClosure,
            RuntimeBoundaryWriterDisposition.Unrepresented,
            _ownerExitObserved,
            _unknownOverflow,
            OperationSnapshots(),
            LeaseSnapshots(),
            UnknownSnapshots(),
            CopyPending(_pending),
            boundedReason);
        AcceptObservation(observation, "report-frozen-before-output");
        return Success("report-frozen-before-output");
    }

    internal RuntimeBoundaryTransition ObserveOutputFailure(RuntimeBoundaryObservation observation)
    {
        if (!TryValidateObservation(observation, RuntimeBoundaryEventKind.OutputFailure,
                RuntimeBoundaryLane.ManagementP, null, null, out var error))
            return Reject(observation, error);
        if (_report is null || IsFixedConsumed(FixedControl.OutputFailure))
            return Reject(observation, "output-failure-before-report-or-replayed");
        ConsumeFixed(FixedControl.OutputFailure);
        _outputFailureObserved = true;
        AcceptObservation(observation, "output-failure-retained-report-unchanged");
        return Success("output-failure-retained-report-unchanged");
    }

    internal RuntimeBoundarySnapshot Snapshot() => new(
        _pAdmissionClosed,
        _leaseAdmissionClosed,
        _localOClosure,
        RuntimeBoundaryWriterDisposition.Unrepresented,
        _lastPTicks,
        _lastOTicks,
        _sampleStartedTicks,
        _firstAbortTicks,
        _outerCutoffTicks,
        _reportReservationTicks,
        _cleanupCeilingTicks,
        _ownerPhaseCeilingTicks,
        _writerPhaseCeilingTicks,
        _ownerPhase.Snapshot(),
        _writerPhase.Snapshot(),
        _ownerExitObserved,
        _unknownOverflow,
        _outputFailureObserved,
        false,
        _eventCount,
        RemainingFixedHolds(),
        RemainingOperationHolds(),
        RemainingLeaseHolds(),
        _ordinaryRecords,
        OperationSnapshots(),
        LeaseSnapshots(),
        EventSnapshots(),
        UnknownSnapshots(),
        CopyPending(_pending),
        CopyReport(_report));

    private RuntimeBoundaryTransition SetLeaseTerminal(
        RuntimeBoundaryObservation observation,
        RuntimeBoundaryEventKind expectedKind,
        LeaseTerminal terminal,
        string acceptedReason)
    {
        var lease = observation?.TicketId is { } ticket ? FindLease(ticket) : null;
        var expectedLane = terminal is LeaseTerminal.CancellationRequest or LeaseTerminal.Timeout
            ? RuntimeBoundaryLane.ManagementP
            : lease?.BorrowerLane;
        if (observation is null || lease is null || expectedLane is null)
            return Reject(observation, "lease-terminal-binding-invalid-or-replayed");
        if (!TryValidateObservation(observation, expectedKind, expectedLane.Value,
                lease.OperationId, lease.TicketId, out var error))
            return Reject(observation, error);
        if (!lease.Holds[(int)terminal])
            return Reject(observation, "lease-terminal-binding-invalid-or-replayed");
        if (terminal == LeaseTerminal.Drain && !lease.UseCompleted)
            return Reject(observation, "lease-drain-before-use-completion");

        if (observation.Lane == RuntimeBoundaryLane.ManagementP)
            AdvanceDuePolicy(observation, observation.OperationId);
        ConsumeLeaseHold(lease, terminal);
        switch (terminal)
        {
            case LeaseTerminal.CancellationRequest: lease.CancellationRequested = true; break;
            case LeaseTerminal.Timeout: lease.TimeoutObserved = true; break;
            case LeaseTerminal.UseCompletion: lease.UseCompleted = true; break;
            case LeaseTerminal.Drain: lease.Drained = true; break;
        }
        AcceptObservation(observation, acceptedReason);
        return Success(acceptedReason);
    }

    private bool TryValidateObservation(
        RuntimeBoundaryObservation observation,
        RuntimeBoundaryEventKind expectedKind,
        RuntimeBoundaryLane expectedLane,
        Guid? expectedOperation,
        string? expectedTicket,
        out string error)
    {
        error = string.Empty;
        if (observation is null)
        {
            error = "observation-missing";
            return false;
        }
        if (observation.RunId != _expected.RunId || observation.SessionId != _expected.SessionId ||
            observation.Anchor != _expected.Anchor || observation.Attempt != _expected.Attempt ||
            observation.ContextDigest != _expected.ContextDigest || observation.Epoch != _expected.Epoch)
        {
            error = "observation-context-mismatch";
            return false;
        }
        if (observation.EventId == Guid.Empty || observation.Kind != expectedKind ||
            observation.Lane != expectedLane || observation.OperationId != expectedOperation ||
            observation.TicketId != expectedTicket || EventSeen(observation.EventId))
        {
            error = "observation-stage-event-or-correlation-invalid";
            return false;
        }
        var expectedActor = expectedLane == RuntimeBoundaryLane.ManagementP
            ? _expected.Controller
            : _expected.Owner;
        var expectedDomain = expectedLane == RuntimeBoundaryLane.ManagementP
            ? _expected.PClockDomain
            : _expected.OClockDomain;
        var expectedFrequency = expectedLane == RuntimeBoundaryLane.ManagementP
            ? _policy.MonotonicFrequency
            : _expected.OwnerClockFrequency;
        var lastTicks = expectedLane == RuntimeBoundaryLane.ManagementP ? _lastPTicks : _lastOTicks ?? 0;
        if (observation.Actor != expectedActor || observation.ClockDomain != expectedDomain ||
            observation.ClockFrequency != expectedFrequency || observation.ObservedTicks <= 0 ||
            observation.ObservedTicks < lastTicks)
        {
            error = "observation-actor-clock-or-order-invalid";
            return false;
        }
        return true;
    }

    private bool TryCalculateAbort(long observedTicks, out AbortCalculation value, out string error)
    {
        value = default;
        error = string.Empty;
        try
        {
            if (_firstAbortTicks is not null)
            {
                value = new(_firstAbortTicks.Value, _outerCutoffTicks!.Value,
                    _reportReservationTicks!.Value, _cleanupCeilingTicks!.Value,
                    _ownerPhaseCeilingTicks!.Value, _writerPhaseCeilingTicks!.Value);
                return true;
            }
            if (_outerCutoffTicks is { } existingOuter && existingOuter <= observedTicks)
            {
                var existingReservation = CheckedSubtract(existingOuter,
                    CheckedSeconds(_policy.MonotonicFrequency, 5));
                value = new(observedTicks, existingOuter, existingReservation, existingOuter,
                    existingReservation, existingReservation);
                return true;
            }
            var abortInterval = CheckedSeconds(_policy.MonotonicFrequency, 30);
            var outer = _outerCutoffTicks is { } retainedOuter
                ? AddClampedTo(observedTicks, abortInterval, retainedOuter)
                : CheckedAdd(observedTicks, abortInterval);
            var reservation = CheckedSubtract(outer, CheckedSeconds(_policy.MonotonicFrequency, 5));
            var cleanup = AddClampedTo(observedTicks, CheckedSeconds(_policy.MonotonicFrequency, 5), outer);
            var owner = AddClampedTo(observedTicks, CheckedSeconds(_policy.MonotonicFrequency, 15), reservation);
            var writer = AddClampedTo(observedTicks, CheckedSeconds(_policy.MonotonicFrequency, 25), reservation);
            value = new(observedTicks, outer, reservation, cleanup, owner, writer);
            return true;
        }
        catch (OverflowException)
        {
            error = "abort-policy-horizon-overflow";
            return false;
        }
    }

    private void AdvanceDuePolicy(RuntimeBoundaryObservation observation, Guid? relatedOperationId)
    {
        if (observation.Lane != RuntimeBoundaryLane.ManagementP) return;
        var active = relatedOperationId is { } operationId
            ? FindOperation(operationId)
            : ActiveOperation();
        if (_firstAbortTicks is null && active is not null &&
            observation.ObservedTicks >= active.DeadlineTicks &&
            !IsFixedConsumed(FixedControl.DeadlineObserved) &&
            !IsFixedConsumed(FixedControl.FirstAbort))
        {
            if (TryCalculateAbort(observation.ObservedTicks, out var abort, out _))
            {
                ConsumeFixed(FixedControl.DeadlineObserved);
                ConsumeFixed(FixedControl.FirstAbort);
                ApplyAbort(abort);
            }
            else
            {
                CloseFreshAdmission();
                _projectionClosed = true;
            }
            active.Stage = RuntimeBoundaryOperationStage.Unresolved;
            _lastPTicks = observation.ObservedTicks;
        }
        if (_outerCutoffTicks is { } cutoff && observation.ObservedTicks >= cutoff)
        {
            _pAdmissionClosed = true;
            _leaseAdmissionClosed = true;
            _projectionClosed = true;
            _lastPTicks = observation.ObservedTicks;
        }
    }

    private void ApplyAbort(AbortCalculation value)
    {
        if (_firstAbortTicks is null)
        {
            _firstAbortTicks = value.FirstAbortTicks;
            _outerCutoffTicks = value.OuterCutoffTicks;
            _reportReservationTicks = value.ReportReservationTicks;
            _cleanupCeilingTicks = value.CleanupCeilingTicks;
            _ownerPhaseCeilingTicks = value.OwnerPhaseCeilingTicks;
            _writerPhaseCeilingTicks = value.WriterPhaseCeilingTicks;
        }
        _pAdmissionClosed = true;
        _leaseAdmissionClosed = true;
    }

    private RuntimeBoundaryTransition RejectWithAdverse(
        RuntimeBoundaryOperationState? operation,
        RuntimeBoundaryObservation observation,
        DiagnosticFirstRawReturn copy,
        string reason)
    {
        if (operation is not null)
        {
            operation.FirstAdverseRawReturn ??= CopyRaw(copy);
            operation.Stage = RuntimeBoundaryOperationStage.Unresolved;
            PreserveUnknowns(operation.OperationId, copy, reason);
        }
        return Reject(observation, reason);
    }

    private void PreserveUnknowns(Guid operationId, DiagnosticFirstRawReturn raw, string reason)
    {
        if (raw.PositiveCoordinates.Count == 0)
        {
            AddUnknown(new(operationId, ResourceKind(raw.Kind), DiagnosticValidation.CopyIdentity(raw.Owner),
                raw.ApiStatus, null, raw.OwnerObservedTicks, new string(raw.OwnerClockDomain.AsSpan()),
                raw.OwnerClockFrequency, BoundReason(reason)));
            return;
        }
        foreach (var coordinate in raw.PositiveCoordinates)
            AddUnknown(new(operationId, ResourceKind(raw.Kind), DiagnosticValidation.CopyIdentity(raw.Owner),
                raw.ApiStatus, coordinate, raw.OwnerObservedTicks, new string(raw.OwnerClockDomain.AsSpan()),
                raw.OwnerClockFrequency, BoundReason(reason)));
    }

    private static bool ShouldPreserveUnknown(DiagnosticFirstRawReturn raw) =>
        raw.ApiStatus != ObserverResourceLifecycle.Success || raw.PositiveCoordinates.Count > 1 ||
        raw.Kind == SupervisionCallKind.Walk && raw.PositiveCoordinates.Count > 0 ||
        raw.Kind is SupervisionCallKind.AcquireMarker or SupervisionCallKind.AcquireSnapshot &&
        raw.PositiveCoordinates.Count != 1 ||
        raw.Kind is SupervisionCallKind.ReleaseMarker or SupervisionCallKind.ReleaseSnapshot &&
        (raw.Descriptor is null || raw.PositiveCoordinates.Any(value => value != raw.Descriptor.Coordinate));

    private void AddUnknown(RuntimeBoundaryUnknown candidate)
    {
        for (var index = 0; index < _unknownCount; index++)
        {
            var existing = _unknowns[index]!;
            if (existing.OperationId == candidate.OperationId && existing.Kind == candidate.Kind &&
                existing.Owner == candidate.Owner && existing.ApiStatus == candidate.ApiStatus &&
                existing.Coordinate == candidate.Coordinate)
                return;
        }
        if (_unknownCount >= MaximumUnknowns)
        {
            _unknownOverflow = true;
            CloseFreshAdmission();
            _projectionClosed = true;
            return;
        }
        _unknowns[_unknownCount++] = CopyUnknown(candidate);
    }

    private bool TryResolveSlot(
        RuntimeBoundarySlotReference reference,
        out SupervisionCapabilitySlot slot,
        out string error)
    {
        slot = null!;
        error = string.Empty;
        if (reference is null || string.IsNullOrWhiteSpace(reference.LogicalSlotId) ||
            Encoding.UTF8.GetByteCount(reference.LogicalSlotId) > DiagnosticValidation.MaximumIdentifierBytes ||
            reference.LeaseId == Guid.Empty || reference.Generation <= 0)
        {
            error = "slot-reference-invalid";
            return false;
        }
        slot = _expected.ExpectedCustody!.SingleOrDefault(item =>
            string.Equals(item.LogicalSlotId, reference.LogicalSlotId, StringComparison.Ordinal))!;
        if (slot is null || slot.ObjectIdentity.LeaseId != reference.LeaseId ||
            slot.ObjectIdentity.Generation != reference.Generation || slot.Holder != reference.Holder ||
            slot.CoordinateOwner != reference.CoordinateOwner)
        {
            error = "slot-generation-lease-or-owner-mismatch";
            return false;
        }
        return true;
    }

    private bool CanReserveOrdinary(int retainedEvents, int terminalHolds) =>
        _ordinaryRecords + retainedEvents <= MaximumOrdinaryRecords &&
        TotalReservedMetadata() + retainedEvents + terminalHolds <= MaximumEvents;

    private bool CanConsumeFixed(FixedControl first, FixedControl? second)
    {
        if (IsFixedConsumed(first) || second is { } supplied && IsFixedConsumed(supplied)) return false;
        return _eventCount < MaximumEvents;
    }

    private int TotalReservedMetadata() =>
        _eventCount + RemainingFixedHolds() + RemainingOperationHolds() + RemainingLeaseHolds();

    private int RemainingFixedHolds() => _fixedConsumed.Count(consumed => !consumed);

    private int RemainingOperationHolds()
    {
        var result = 0;
        for (var index = 0; index < _operationCount; index++)
            result += _operations[index]!.Holds.Count(held => held);
        return result;
    }

    private int RemainingLeaseHolds()
    {
        var result = 0;
        for (var index = 0; index < _leaseCount; index++)
            result += _leases[index]!.Holds.Count(held => held);
        return result;
    }

    private void ConsumeFixed(FixedControl control) => _fixedConsumed[(int)control] = true;
    private bool IsFixedConsumed(FixedControl control) => _fixedConsumed[(int)control];

    private static void ConsumeOperationHold(RuntimeBoundaryOperationState operation, OperationTerminal terminal) =>
        operation.Holds[(int)terminal] = false;

    private static void ConsumeLeaseHold(RuntimeBoundaryLeaseState lease, LeaseTerminal terminal) =>
        lease.Holds[(int)terminal] = false;

    private RuntimeBoundaryOperationState? FindOperation(Guid operationId)
    {
        for (var index = 0; index < _operationCount; index++)
            if (_operations[index]!.OperationId == operationId) return _operations[index];
        return null;
    }

    private RuntimeBoundaryOperationState? ActiveOperation()
    {
        for (var index = 0; index < _operationCount; index++)
            if (_operations[index]!.Holds.Any(held => held)) return _operations[index];
        return null;
    }

    private bool OwnerAdmissionObserved()
    {
        for (var index = 0; index < _operationCount; index++)
            if (_operations[index]!.OwnerAdmitted) return true;
        return false;
    }

    private RuntimeBoundaryLeaseState? FindLease(string ticket)
    {
        for (var index = 0; index < _leaseCount; index++)
            if (string.Equals(_leases[index]!.TicketId, ticket, StringComparison.Ordinal)) return _leases[index];
        return null;
    }

    private bool EventSeen(Guid eventId)
    {
        for (var index = 0; index < _eventCount; index++)
            if (_events[index]!.Accepted && _events[index]!.EventId == eventId) return true;
        return false;
    }

    private void AcceptObservation(RuntimeBoundaryObservation observation, string reason)
    {
        if (observation.Lane == RuntimeBoundaryLane.ManagementP) _lastPTicks = observation.ObservedTicks;
        else _lastOTicks = observation.ObservedTicks;
        var ordinal = _eventCount + 1;
        _events[_eventCount] = new(ordinal, observation.Kind, observation.EventId,
            observation.OperationId, CopyOptionalTicket(observation.TicketId), observation.Lane,
            observation.ObservedTicks, true, BoundReason(reason));
        _eventCount++;
    }

    private RuntimeBoundaryTransition Reject(RuntimeBoundaryObservation? observation, string reason)
    {
        var bounded = BoundReason(reason);
        RecordRejection(bounded);
        if (_ordinaryRecords < MaximumOrdinaryRecords && TotalReservedMetadata() < MaximumEvents)
        {
            _ordinaryRecords++;
            var ordinal = _eventCount + 1;
            _events[_eventCount] = new(ordinal,
                observation?.Kind ?? RuntimeBoundaryEventKind.OperationCommit,
                observation is null || observation.EventId == Guid.Empty || EventSeen(observation.EventId)
                    ? Guid.Empty
                    : observation.EventId,
                observation?.OperationId,
                CopyBoundedOptionalTicket(observation?.TicketId),
                observation?.Lane ?? RuntimeBoundaryLane.ManagementP,
                observation?.ObservedTicks ?? 0,
                false,
                bounded);
            _eventCount++;
        }
        else
        {
            CloseFreshAdmission();
        }
        return new(false, bounded, Snapshot());
    }

    private RuntimeBoundaryTransition Success(string reason) => new(true, BoundReason(reason), Snapshot());

    private void RecordRejection(string reason)
    {
        for (var index = 0; index < _rejectionCount; index++)
        {
            if (!string.Equals(_rejections[index]!.Reason, reason, StringComparison.Ordinal)) continue;
            _rejections[index]!.Count = _rejections[index]!.Count == int.MaxValue
                ? int.MaxValue
                : _rejections[index]!.Count + 1;
            return;
        }
        if (_rejectionCount < MaximumRejectedSummaries)
            _rejections[_rejectionCount++] = new(reason, 1);
    }

    private void CloseFreshAdmission()
    {
        _pAdmissionClosed = true;
        _leaseAdmissionClosed = true;
    }

    private ReadOnlyCollection<RuntimeBoundaryOperationSnapshot> OperationSnapshots()
    {
        var values = new RuntimeBoundaryOperationSnapshot[_operationCount];
        for (var index = 0; index < _operationCount; index++) values[index] = _operations[index]!.Snapshot();
        return Array.AsReadOnly(values);
    }

    private ReadOnlyCollection<RuntimeBoundaryLeaseSnapshot> LeaseSnapshots()
    {
        var values = new RuntimeBoundaryLeaseSnapshot[_leaseCount];
        for (var index = 0; index < _leaseCount; index++) values[index] = _leases[index]!.Snapshot();
        return Array.AsReadOnly(values);
    }

    private ReadOnlyCollection<RuntimeBoundaryEventRecord> EventSnapshots()
    {
        var values = new RuntimeBoundaryEventRecord[_eventCount];
        for (var index = 0; index < _eventCount; index++)
            values[index] = _events[index]! with
            {
                TicketId = CopyOptionalTicket(_events[index]!.TicketId),
                Reason = new string(_events[index]!.Reason.AsSpan()),
            };
        return Array.AsReadOnly(values);
    }

    private ReadOnlyCollection<RuntimeBoundaryUnknown> UnknownSnapshots()
    {
        var values = new RuntimeBoundaryUnknown[_unknownCount];
        for (var index = 0; index < _unknownCount; index++) values[index] = CopyUnknown(_unknowns[index]!);
        return Array.AsReadOnly(values);
    }

    private static RuntimeBoundaryReport? CopyReport(RuntimeBoundaryReport? value) => value is null
        ? null
        : value with
        {
            Operations = Array.AsReadOnly(value.Operations.Select(CopyOperation).ToArray()),
            Leases = Array.AsReadOnly(value.Leases.Select(CopyLease).ToArray()),
            Unknowns = Array.AsReadOnly(value.Unknowns.Select(CopyUnknown).ToArray()),
            Pending = CopyPending(value.Pending),
            Reason = new string(value.Reason.AsSpan()),
        };

    private static RuntimeBoundaryPendingSnapshot? CopyPending(RuntimeBoundaryPendingSnapshot? value) => value is null
        ? null
        : value with { RawReturn = CopyRaw(value.RawReturn) };

    private static RuntimeBoundaryOperationSnapshot CopyOperation(RuntimeBoundaryOperationSnapshot value) => value with
    {
        Slot = CopySlot(value.Slot),
        FirstRawReturn = value.FirstRawReturn is null ? null : CopyRaw(value.FirstRawReturn),
        FirstAdverseRawReturn = value.FirstAdverseRawReturn is null ? null : CopyRaw(value.FirstAdverseRawReturn),
    };

    private static RuntimeBoundaryLeaseSnapshot CopyLease(RuntimeBoundaryLeaseSnapshot value) => value with
    {
        TicketId = new string(value.TicketId.AsSpan()),
        Slot = CopySlot(value.Slot),
        Borrower = DiagnosticValidation.CopyIdentity(value.Borrower),
    };

    private static RuntimeBoundaryUnknown CopyUnknown(RuntimeBoundaryUnknown value) => value with
    {
        Owner = DiagnosticValidation.CopyIdentity(value.Owner),
        OwnerClockDomain = new string(value.OwnerClockDomain.AsSpan()),
        Reason = new string(value.Reason.AsSpan()),
    };

    private static RuntimeBoundarySlotReference CopySlot(RuntimeBoundarySlotReference value) => value with
    {
        LogicalSlotId = new string(value.LogicalSlotId.AsSpan()),
        Holder = DiagnosticValidation.CopyIdentity(value.Holder),
        CoordinateOwner = DiagnosticValidation.CopyIdentity(value.CoordinateOwner),
    };

    private static DiagnosticFirstRawReturn CopyRaw(DiagnosticFirstRawReturn value) =>
        (DiagnosticFirstRawReturn)DiagnosticValidation.CopyBody(DiagnosticEnvelopeKind.FirstRawReturn, value);

    private static bool ExactRaw(DiagnosticFirstRawReturn left, DiagnosticFirstRawReturn right) =>
        left.Owner == right.Owner && left.Kind == right.Kind && left.ApiStatus == right.ApiStatus &&
        left.PositiveCoordinates.SequenceEqual(right.PositiveCoordinates) && left.Descriptor == right.Descriptor &&
        left.OwnerObservedTicks == right.OwnerObservedTicks && left.OwnerClockDomain == right.OwnerClockDomain &&
        left.OwnerClockFrequency == right.OwnerClockFrequency && left.IncompleteReason == right.IncompleteReason;

    private bool TryCopyTicket(string value, out string copy, out string error)
    {
        copy = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            Encoding.UTF8.GetByteCount(value) > DiagnosticValidation.MaximumTicketBytes ||
            DiagnosticValidation.HasInvalidUtf16(value))
        {
            error = "ticket-invalid";
            return false;
        }
        copy = new string(value.AsSpan());
        return true;
    }

    private bool TryCopyReason(string value, out string copy, out string error)
    {
        copy = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(value) ||
            Encoding.UTF8.GetByteCount(value) > DiagnosticValidation.MaximumFinalReasonBytes ||
            DiagnosticValidation.HasInvalidUtf16(value))
        {
            error = "reason-invalid";
            return false;
        }
        copy = new string(value.AsSpan());
        return true;
    }

    private static string? CopyOptionalTicket(string? value) => value is null
        ? null
        : new string(value.AsSpan());

    private static string? CopyBoundedOptionalTicket(string? value) => value is null ||
        string.IsNullOrWhiteSpace(value) || DiagnosticValidation.HasInvalidUtf16(value) ||
        Encoding.UTF8.GetByteCount(value) > DiagnosticValidation.MaximumTicketBytes
            ? null
            : new string(value.AsSpan());

    private static string BoundReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || DiagnosticValidation.HasInvalidUtf16(reason) ||
            Encoding.UTF8.GetByteCount(reason) > DiagnosticValidation.MaximumFinalReasonBytes)
            return "bounded-rejection";
        return new string(reason.AsSpan());
    }

    private static ObserverResourceKind? ResourceKind(SupervisionCallKind kind) => kind switch
    {
        SupervisionCallKind.AcquireMarker or SupervisionCallKind.ReleaseMarker => ObserverResourceKind.Marker,
        SupervisionCallKind.AcquireSnapshot or SupervisionCallKind.ReleaseSnapshot => ObserverResourceKind.Snapshot,
        _ => null,
    };

    private static long CheckedSeconds(long frequency, int seconds) => checked(frequency * seconds);
    private static long AddClampedTo(long start, long interval, long ceiling) =>
        start >= ceiling || interval >= checked(ceiling - start) ? ceiling : checked(start + interval);
    private static long CheckedAdd(long left, long right) => checked(left + right);
    private static long CheckedSubtract(long left, long right) => checked(left - right);

    private enum FixedControl
    {
        PStop,
        LocalOStop,
        OwnerExit,
        FirstAbort,
        DeadlineObserved,
        CleanupGraceElapsed,
        OwnerPhaseStarted,
        OwnerPhaseEnded,
        WriterPhaseStarted,
        WriterPhaseEnded,
        ReportFrozen,
        OutputFailure,
    }

    private enum OperationTerminal
    {
        OAdmission,
        EntryObservation,
        FirstRawReturn,
        Publication,
        Delivery,
    }

    private enum LeaseTerminal
    {
        CancellationRequest,
        Timeout,
        UseCompletion,
        Drain,
    }

    private readonly record struct AbortCalculation(
        long FirstAbortTicks,
        long OuterCutoffTicks,
        long ReportReservationTicks,
        long CleanupCeilingTicks,
        long OwnerPhaseCeilingTicks,
        long WriterPhaseCeilingTicks);

    private sealed class RuntimeBoundaryOperationState
    {
        internal RuntimeBoundaryOperationState(
            Guid operationId,
            SupervisionCallKind kind,
            RuntimeBoundarySlotReference slot,
            long commitmentTicks,
            long deadlineTicks)
        {
            OperationId = operationId;
            Kind = kind;
            Slot = slot;
            CommitmentTicks = commitmentTicks;
            DeadlineTicks = deadlineTicks;
        }

        internal Guid OperationId { get; }
        internal SupervisionCallKind Kind { get; }
        internal RuntimeBoundarySlotReference Slot { get; }
        internal long CommitmentTicks { get; }
        internal long DeadlineTicks { get; }
        internal RuntimeBoundaryOperationStage Stage { get; set; } = RuntimeBoundaryOperationStage.Committed;
        internal bool OwnerAdmitted { get; set; }
        internal RuntimeBoundaryEntryDisposition Entry { get; set; }
        internal DiagnosticFirstRawReturn? FirstRawReturn { get; set; }
        internal DiagnosticFirstRawReturn? FirstAdverseRawReturn { get; set; }
        internal RuntimeBoundaryPublicationDisposition Publication { get; set; }
        internal RuntimeBoundaryDeliveryDisposition Delivery { get; set; }
        internal long? DeliveryTicks { get; set; }
        internal bool[] Holds { get; } = [true, true, true, true, true];

        internal RuntimeBoundaryOperationSnapshot Snapshot() => new(
            OperationId,
            Kind,
            Stage,
            CopySlot(Slot),
            CommitmentTicks,
            DeadlineTicks,
            OwnerAdmitted,
            Entry,
            FirstRawReturn is null ? null : CopyRaw(FirstRawReturn),
            FirstAdverseRawReturn is null ? null : CopyRaw(FirstAdverseRawReturn),
            Publication,
            Delivery,
            DeliveryTicks,
            Holds.Count(held => held));
    }

    private sealed class RuntimeBoundaryLeaseState
    {
        internal RuntimeBoundaryLeaseState(
            string ticketId,
            Guid operationId,
            RuntimeBoundarySlotReference slot,
            RuntimeBoundaryLane borrowerLane,
            InventoryProcessIdentity borrower,
            RuntimeBoundaryLeasePurpose purpose)
        {
            TicketId = ticketId;
            OperationId = operationId;
            Slot = slot;
            BorrowerLane = borrowerLane;
            Borrower = borrower;
            Purpose = purpose;
        }

        internal string TicketId { get; }
        internal Guid OperationId { get; }
        internal RuntimeBoundarySlotReference Slot { get; }
        internal RuntimeBoundaryLane BorrowerLane { get; }
        internal InventoryProcessIdentity Borrower { get; }
        internal RuntimeBoundaryLeasePurpose Purpose { get; }
        internal bool CancellationRequested { get; set; }
        internal bool TimeoutObserved { get; set; }
        internal bool UseCompleted { get; set; }
        internal bool Drained { get; set; }
        internal bool[] Holds { get; } = [true, true, true, true];

        internal RuntimeBoundaryLeaseSnapshot Snapshot() => new(
            new string(TicketId.AsSpan()),
            OperationId,
            CopySlot(Slot),
            BorrowerLane,
            DiagnosticValidation.CopyIdentity(Borrower),
            Purpose,
            CancellationRequested,
            TimeoutObserved,
            UseCompleted,
            Drained,
            Holds.Count(held => held));
    }

    private sealed class RuntimeBoundaryPhaseState(RuntimeBoundaryPhaseKind kind)
    {
        internal RuntimeBoundaryPhaseKind Kind { get; } = kind;
        internal RuntimeBoundaryPhaseDisposition Disposition { get; set; }
        internal long? StartedTicks { get; set; }
        internal long? AnchoredCeilingTicks { get; set; }
        internal long? ActualEndTicks { get; set; }

        internal RuntimeBoundaryPhaseSnapshot Snapshot() => new(
            Kind,
            Disposition,
            StartedTicks,
            AnchoredCeilingTicks,
            ActualEndTicks);
    }

    private sealed class RejectionSummary(string reason, int count)
    {
        internal string Reason { get; } = reason;
        internal int Count { get; set; } = count;
    }
}
