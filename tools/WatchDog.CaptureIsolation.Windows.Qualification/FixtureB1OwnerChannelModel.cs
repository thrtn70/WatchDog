using System.Collections.ObjectModel;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureB1OwnerResourceKind
{
    RunnerJob,
    RunnerProcess,
}

internal enum FixtureB1OwnerChannelDirection
{
    WrapperToLauncher,
    LauncherToWrapper,
}

internal enum FixtureB1OwnerAdoptionDisposition
{
    Adopted,
    AlreadyOwned,
    ProviderRetainsNewResource,
}

internal enum FixtureB1OwnerChannelState
{
    Pending,
    Ready,
    Rejected,
}

internal enum FixtureB1OwnerChannelDiagnosticCode
{
    None,
    InvalidBootstrap,
    BootstrapAuthorityRejected,
    ReservationAuthorityRejected,
    InvalidFrame,
    BindingMismatch,
    FrameAuthenticationRejected,
    SequenceMismatch,
    OperationReplay,
    OrderViolation,
    WrongObject,
    ResourceAlias,
    AckAuthenticationRejected,
    AckReplay,
    AckLost,
    PeerDied,
    ReleaseAuthorityRejected,
    ReleaseReplay,
    JournalFailure,
}

internal enum FixtureB1OwnerJournalEvent
{
    RemoteCoordinateJournaled,
    Adopted,
    AckIssued,
    Acknowledged,
    AckLost,
    ProviderRetainedRejected,
    ProviderRetainedClosed,
    PeerDeathObserved,
    ReleaseAuthorized,
    ProtocolRejected,
}

internal sealed record FixtureB1OwnerChannelBinding(
    Guid RunNonce,
    Guid LaunchId,
    FixtureHandoffProcessIdentity ExactWrapper,
    FixtureHandoffProcessIdentity ExactLauncher,
    long ConnectionReceipt);

internal sealed record FixtureB1OwnerJobReservation(
    FixtureHandoffProcessIdentity Owner,
    long ResourceReceipt,
    long OwnerLeaseReceipt,
    bool NonInheritable,
    bool KillOnLastHandleClose,
    bool InitiallyEmpty);

/// <summary>
/// One-use identity reserved before the provider starts a remote DuplicateHandle call. It has
/// no target coordinate yet. A later native-success receipt must match this reservation exactly.
/// </summary>
internal sealed record FixtureB1OwnerRemoteDuplicateReservation(
    Guid OperationId,
    Guid SuccessInstanceId,
    FixtureB1OwnerChannelDirection Direction,
    FixtureB1OwnerResourceKind ResourceKind,
    FixtureHandoffProcessIdentity Source,
    FixtureHandoffProcessIdentity Target,
    FixtureHandoffProcessIdentity? ExactRunner);

/// <summary>
/// Provider-owned receipt captured immediately after remote duplication succeeds. It is
/// deliberately separate from the untrusted owner-channel frame so cleanup custody can be
/// journaled before frame serialization, IPC, authentication, or other fallible validation.
/// </summary>
internal sealed record FixtureB1OwnerRemoteDuplicateReceipt(
    Guid OperationId,
    Guid SuccessInstanceId,
    FixtureB1OwnerChannelDirection Direction,
    FixtureB1OwnerResourceKind ResourceKind,
    FixtureHandoffProcessIdentity Source,
    FixtureHandoffProcessIdentity Target,
    FixtureHandoffProcessIdentity? ExactRunner,
    long TargetLocalCoordinate,
    long ResourceReceipt,
    bool NonInheritable);

/// <summary>
/// Typed owner-channel offer recorded after remote duplication succeeds. Sequence zero is
/// W-to-L runner-job custody and sequence two is L-to-W exact-runner custody. The gaps are
/// reserved for their one-use acknowledgements.
/// </summary>
internal sealed record FixtureB1OwnerChannelFrame(
    FixtureB1OwnerChannelBinding Binding,
    Guid OperationId,
    Guid SuccessInstanceId,
    long Sequence,
    FixtureB1OwnerChannelDirection Direction,
    FixtureB1OwnerResourceKind ResourceKind,
    FixtureHandoffProcessIdentity Source,
    FixtureHandoffProcessIdentity Target,
    FixtureHandoffProcessIdentity? ExactRunner,
    long TargetLocalCoordinate,
    long ResourceReceipt,
    bool NonInheritable);

/// <summary>
/// Receiver-owned resource published immediately after the provider adopts the target-local
/// coordinate. ResourceReceipt is a fake/native-observer identity, never a copied handle value.
/// </summary>
internal sealed record FixtureB1OwnerChannelAdoption(
    Guid OperationId,
    FixtureB1OwnerResourceKind ResourceKind,
    FixtureHandoffProcessIdentity Owner,
    long TargetLocalCoordinate,
    long ResourceReceipt,
    FixtureHandoffProcessIdentity? ExactProcess,
    long? ContainingJobReceipt,
    bool NonInheritable);

internal sealed record FixtureB1OwnerRunnerRegistration(
    FixtureHandoffProcessIdentity ExactRunner,
    FixtureHandoffProcessIdentity Creator,
    long RunnerJobReceipt,
    bool CreatedSuspended,
    int InheritedHandleCount);

internal sealed record FixtureB1OwnerChannelAck(
    FixtureB1OwnerChannelBinding Binding,
    Guid OperationId,
    Guid SuccessInstanceId,
    long Sequence,
    FixtureB1OwnerResourceKind ResourceKind,
    FixtureHandoffProcessIdentity Source,
    FixtureHandoffProcessIdentity Target,
    FixtureHandoffProcessIdentity? ExactRunner,
    long TargetLocalCoordinate,
    long ResourceReceipt,
    Guid ReceiptId);

/// <summary>
/// Append-only custody evidence. TryAppend may return true only after the entry is durable for
/// the owner represented by the caller. This pure contract neither selects nor implements a
/// filesystem, registry, pipe, or shared-memory journal.
/// </summary>
internal interface IFixtureB1OwnerChannelJournal
{
    bool TryAppend(FixtureB1OwnerJournalEntry entry);
}

/// <summary>
/// External authority required by the pure model. A native implementation must bind the owner
/// channel to retained W/L process objects, authenticate frames and ACKs with separately owned
/// key material, and observe exact job/process objects. Typed records alone are not authority.
/// </summary>
internal interface IFixtureB1OwnerChannelAuthority
{
    bool IsBootstrapAuthenticated(FixtureB1OwnerChannelBinding binding);
    bool IsExactWrapperOwnedJobReservation(
        FixtureB1OwnerChannelBinding binding,
        FixtureB1OwnerJobReservation reservation);
    bool IsFrameAuthenticated(FixtureB1OwnerChannelFrame frame);
    bool IsAckAuthenticated(FixtureB1OwnerChannelAck ack);
    bool IsExactRunnerJob(
        FixtureB1OwnerChannelAdoption adoption,
        FixtureB1OwnerJobReservation reservation);
    bool IsExactRunnerRegistration(
        FixtureB1OwnerRunnerRegistration registration,
        FixtureB1OwnerJobReservation reservation);
    bool IsExactRunnerProcess(
        FixtureB1OwnerChannelAdoption adoption,
        FixtureB1OwnerRunnerRegistration registration,
        FixtureB1OwnerJobReservation reservation);
    bool IsProviderRetainedResourceClosed(
        FixtureB1OwnerChannelAdoption adoption,
        Guid closeReceipt);
    bool IsExactPeerTerminated(FixtureHandoffProcessIdentity peer);
    bool IsReleaseBoundaryLiveAndOwned(
        FixtureB1OwnerChannelBinding binding,
        FixtureB1OwnerRunnerRegistration runner);
}

internal sealed record FixtureB1OwnerJournalEntry(
    long Ordinal,
    FixtureB1OwnerJournalEvent Event,
    Guid OperationId,
    Guid? SuccessInstanceId,
    long Sequence,
    FixtureB1OwnerResourceKind ResourceKind,
    FixtureB1OwnerChannelDirection Direction,
    FixtureHandoffProcessIdentity Source,
    FixtureHandoffProcessIdentity Target,
    long TargetLocalCoordinate,
    long ResourceReceipt,
    Guid? ReceiptId);

internal sealed class FixtureB1OwnerChannelResult
{
    internal FixtureB1OwnerChannelResult(
        FixtureB1OwnerChannelState state,
        FixtureB1OwnerChannelDiagnosticCode diagnosticCode,
        int acknowledgedTransferCount,
        int journaledRemoteObligationCount,
        int unconfirmedRemoteObligationCount,
        int providerRetainedRejectCount,
        int outstandingProviderRetainedRejectCount,
        bool releaseAuthorized,
        IReadOnlyList<FixtureB1OwnerJournalEntry> journal)
    {
        State = state;
        DiagnosticCode = diagnosticCode;
        AcknowledgedTransferCount = acknowledgedTransferCount;
        JournaledRemoteObligationCount = journaledRemoteObligationCount;
        UnconfirmedRemoteObligationCount = unconfirmedRemoteObligationCount;
        ProviderRetainedRejectCount = providerRetainedRejectCount;
        OutstandingProviderRetainedRejectCount =
            outstandingProviderRetainedRejectCount;
        ReleaseAuthorized = releaseAuthorized;
        Journal = journal;
    }

    internal FixtureB1OwnerChannelState State { get; }
    internal FixtureB1OwnerChannelDiagnosticCode DiagnosticCode { get; }
    internal int AcknowledgedTransferCount { get; }
    internal int JournaledRemoteObligationCount { get; }
    internal int UnconfirmedRemoteObligationCount { get; }
    internal int ProviderRetainedRejectCount { get; }
    internal int OutstandingProviderRetainedRejectCount { get; }
    internal bool ReleaseAuthorized { get; }
    internal IReadOnlyList<FixtureB1OwnerJournalEntry> Journal { get; }
    internal bool CanDeliverRunnerSlots =>
        State == FixtureB1OwnerChannelState.Ready && ReleaseAuthorized;
    internal bool CanResumeRunner =>
        State == FixtureB1OwnerChannelState.Ready && ReleaseAuthorized;
    internal bool ExternalBootstrapAuthorityRequired => true;
    internal bool NativeQualificationEstablished => false;
    internal bool NativeReleaseSynchronizationEstablished => false;
}

/// <summary>
/// Pure, unregistered W/L custody contract. W must create and retain the runner job, L must
/// adopt W's noninheritable remote duplicate before its ACK, and W must adopt an exact-R process
/// duplicate before any seven-slot delivery or resume. Release requires one fresh external W/L
/// liveness/ownership proof. This is a contract/test oracle only: it performs no IPC,
/// cryptography, DuplicateHandle, process creation, job query, atomic native release, event
/// delivery, or native proof.
/// </summary>
internal sealed class FixtureB1OwnerChannelModel
{
    private enum Stage
    {
        AwaitingJobOffer,
        AwaitingJobFrame,
        AwaitingJobAdoption,
        AwaitingJobAck,
        AwaitingRunnerRegistration,
        AwaitingProcessOffer,
        AwaitingProcessFrame,
        AwaitingProcessAdoption,
        AwaitingProcessAck,
        Ready,
        Rejected,
    }

    private sealed class Transfer(FixtureB1OwnerChannelFrame frame)
    {
        internal FixtureB1OwnerChannelFrame Frame { get; } = frame;
        internal FixtureB1OwnerChannelAdoption? Adoption { get; set; }
        internal FixtureB1OwnerChannelAck? Ack { get; set; }
    }

    private readonly object _gate = new();
    private readonly FixtureB1OwnerChannelBinding _binding;
    private readonly FixtureB1OwnerJobReservation _reservation;
    private readonly IFixtureB1OwnerChannelAuthority _authority;
    private readonly IFixtureB1OwnerChannelJournal _journal;
    private readonly List<FixtureB1OwnerJournalEntry> _entries = [];
    private readonly HashSet<Guid> _operationIds = [];
    private readonly HashSet<Guid> _reservedSuccessInstanceIds = [];
    private readonly Dictionary<Guid, FixtureB1OwnerRemoteDuplicateReceipt>
        _successfulDuplicates = [];
    private readonly HashSet<Guid> _issuedReceiptIds = [];
    private readonly HashSet<Guid> _acceptedReceiptIds = [];
    private readonly HashSet<Guid> _acceptedOperationIds = [];
    private readonly HashSet<FixtureB1OwnerChannelAdoption> _ownedAdoptions = [];
    private readonly HashSet<(FixtureHandoffProcessIdentity Owner, long Coordinate)>
        _ownedCoordinates = [];
    private readonly HashSet<long> _ownedResourceReceipts = [];
    private readonly Dictionary<FixtureB1OwnerChannelAdoption, bool>
        _providerRetained = [];
    private Stage _stage = Stage.AwaitingJobOffer;
    private FixtureB1OwnerChannelDiagnosticCode _diagnostic;
    private Transfer? _current;
    private FixtureB1OwnerRemoteDuplicateReservation? _pendingReservation;
    private FixtureB1OwnerRemoteDuplicateReceipt? _pendingDuplicate;
    private FixtureB1OwnerRunnerRegistration? _runner;
    private long _nextOrdinal;
    private int _acknowledgedTransfers;
    private int _journaledRemoteObligations;
    private int _unconfirmedRemoteObligations;
    private bool _releaseAuthorizationConsumed;
    private bool _releaseAuthorized;

    internal FixtureB1OwnerChannelModel(
        FixtureB1OwnerChannelBinding binding,
        FixtureB1OwnerJobReservation reservation,
        IFixtureB1OwnerChannelAuthority authority,
        IFixtureB1OwnerChannelJournal journal)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(reservation);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(journal);
        _binding = binding;
        _reservation = reservation;
        _authority = authority;
        _journal = journal;

        if (!ValidBootstrapShape(binding, reservation))
            SetRejected(FixtureB1OwnerChannelDiagnosticCode.InvalidBootstrap);
        else if (!Observe(() => authority.IsBootstrapAuthenticated(binding)))
            SetRejected(FixtureB1OwnerChannelDiagnosticCode.BootstrapAuthorityRejected);
        else if (!Observe(() => authority.IsExactWrapperOwnedJobReservation(
                     binding, reservation)))
            SetRejected(FixtureB1OwnerChannelDiagnosticCode.ReservationAuthorityRejected);
    }

    internal bool CanDeliverRunnerSlots
    {
        // Actionable only after the one-use release-boundary observation succeeds.
        get { lock (_gate) return _stage == Stage.Ready && _releaseAuthorized; }
    }

    internal bool CanResumeRunner
    {
        get { lock (_gate) return _stage == Stage.Ready && _releaseAuthorized; }
    }

    internal bool ReserveRemoteDuplicate(
        FixtureB1OwnerRemoteDuplicateReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_gate)
        {
            if (_stage == Stage.Rejected)
                return false;
            var expectedJob = _stage == Stage.AwaitingJobOffer;
            var expectedProcess = _stage == Stage.AwaitingProcessOffer;
            if ((!expectedJob && !expectedProcess) || _pendingReservation is not null)
                return Reject(FixtureB1OwnerChannelDiagnosticCode.OrderViolation,
                    FrameFrom(reservation));
            if (reservation.OperationId == Guid.Empty ||
                reservation.SuccessInstanceId == Guid.Empty)
                return Reject(FixtureB1OwnerChannelDiagnosticCode.InvalidFrame,
                    FrameFrom(reservation));
            if (_operationIds.Contains(reservation.OperationId))
                return Reject(FixtureB1OwnerChannelDiagnosticCode.OperationReplay,
                    FrameFrom(reservation));
            if (_reservedSuccessInstanceIds.Contains(reservation.SuccessInstanceId) ||
                _successfulDuplicates.ContainsKey(reservation.SuccessInstanceId))
                return Reject(FixtureB1OwnerChannelDiagnosticCode.ResourceAlias,
                    FrameFrom(reservation));
            if ((expectedJob &&
                    (reservation.Direction !=
                        FixtureB1OwnerChannelDirection.WrapperToLauncher ||
                     reservation.ResourceKind != FixtureB1OwnerResourceKind.RunnerJob ||
                     reservation.Source != _binding.ExactWrapper ||
                     reservation.Target != _binding.ExactLauncher ||
                     reservation.ExactRunner is not null)) ||
                (expectedProcess &&
                    (reservation.Direction !=
                        FixtureB1OwnerChannelDirection.LauncherToWrapper ||
                     reservation.ResourceKind != FixtureB1OwnerResourceKind.RunnerProcess ||
                     reservation.Source != _binding.ExactLauncher ||
                     reservation.Target != _binding.ExactWrapper ||
                     _runner is null ||
                     reservation.ExactRunner != _runner.ExactRunner)))
                return Reject(FixtureB1OwnerChannelDiagnosticCode.InvalidFrame,
                    FrameFrom(reservation));

            _operationIds.Add(reservation.OperationId);
            _reservedSuccessInstanceIds.Add(reservation.SuccessInstanceId);
            _pendingReservation = reservation;
            return true;
        }
    }

    internal bool RegisterRemoteDuplicate(
        FixtureB1OwnerRemoteDuplicateReceipt duplicate)
    {
        ArgumentNullException.ThrowIfNull(duplicate);
        lock (_gate)
        {
            if (duplicate.SuccessInstanceId == Guid.Empty)
            {
                _unconfirmedRemoteObligations++;
                return SetRejected(FixtureB1OwnerChannelDiagnosticCode.InvalidFrame);
            }
            if (_successfulDuplicates.TryGetValue(
                    duplicate.SuccessInstanceId, out var prior))
            {
                if (prior == duplicate)
                    return true;
                return Reject(FixtureB1OwnerChannelDiagnosticCode.ResourceAlias,
                    FrameFrom(duplicate));
            }

            // Native success has already happened. Persist its remote cleanup obligation
            // before a frame is constructed, serialized, sent, or authenticated.
            if (!Persist(Entry(duplicate)))
            {
                _unconfirmedRemoteObligations++;
                return SetRejected(FixtureB1OwnerChannelDiagnosticCode.JournalFailure);
            }
            _journaledRemoteObligations++;
            _successfulDuplicates.Add(duplicate.SuccessInstanceId, duplicate);

            if (_stage == Stage.Rejected)
                return false;

            var expectedJob = _stage == Stage.AwaitingJobOffer;
            var expectedProcess = _stage == Stage.AwaitingProcessOffer;
            if (!expectedJob && !expectedProcess)
                return Reject(FixtureB1OwnerChannelDiagnosticCode.OrderViolation,
                    FrameFrom(duplicate));

            if (_pendingReservation is null ||
                !ReceiptMatchesReservation(duplicate, _pendingReservation) ||
                duplicate.OperationId == Guid.Empty ||
                duplicate.TargetLocalCoordinate <= 0 ||
                duplicate.ResourceReceipt <= 0 || !duplicate.NonInheritable ||
                (expectedJob &&
                    (duplicate.Direction !=
                        FixtureB1OwnerChannelDirection.WrapperToLauncher ||
                     duplicate.ResourceKind != FixtureB1OwnerResourceKind.RunnerJob ||
                     duplicate.Source != _binding.ExactWrapper ||
                     duplicate.Target != _binding.ExactLauncher ||
                     duplicate.ExactRunner is not null)) ||
                (expectedProcess &&
                    (duplicate.Direction !=
                        FixtureB1OwnerChannelDirection.LauncherToWrapper ||
                     duplicate.ResourceKind != FixtureB1OwnerResourceKind.RunnerProcess ||
                     duplicate.Source != _binding.ExactLauncher ||
                     duplicate.Target != _binding.ExactWrapper ||
                     _runner is null ||
                     duplicate.ExactRunner != _runner.ExactRunner)))
                return Reject(FixtureB1OwnerChannelDiagnosticCode.InvalidFrame,
                    FrameFrom(duplicate));

            _pendingDuplicate = duplicate;
            _pendingReservation = null;
            _stage = expectedJob ? Stage.AwaitingJobFrame : Stage.AwaitingProcessFrame;
            return true;
        }
    }

    internal bool AdmitFrame(FixtureB1OwnerChannelFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        lock (_gate)
        {
            if (_stage == Stage.Rejected)
                return false;
            var expectedJob = _stage == Stage.AwaitingJobFrame;
            var expectedProcess = _stage == Stage.AwaitingProcessFrame;
            if ((!expectedJob && !expectedProcess) || _pendingDuplicate is null)
                return Reject(FixtureB1OwnerChannelDiagnosticCode.OrderViolation, frame);

            var duplicate = _pendingDuplicate;
            if (duplicate.OperationId != frame.OperationId ||
                duplicate.SuccessInstanceId != frame.SuccessInstanceId ||
                duplicate.Direction != frame.Direction ||
                duplicate.ResourceKind != frame.ResourceKind ||
                duplicate.Source != frame.Source || duplicate.Target != frame.Target ||
                duplicate.ExactRunner != frame.ExactRunner ||
                duplicate.TargetLocalCoordinate != frame.TargetLocalCoordinate ||
                duplicate.ResourceReceipt != frame.ResourceReceipt ||
                duplicate.NonInheritable != frame.NonInheritable ||
                frame.OperationId == Guid.Empty ||
                frame.TargetLocalCoordinate <= 0 || frame.ResourceReceipt <= 0 ||
                !frame.NonInheritable)
                return Reject(FixtureB1OwnerChannelDiagnosticCode.InvalidFrame, frame);
            if (frame.Binding != _binding ||
                (expectedJob && frame.ExactRunner is not null) ||
                (expectedProcess &&
                    (_runner is null || frame.ExactRunner != _runner.ExactRunner)))
                return Reject(FixtureB1OwnerChannelDiagnosticCode.BindingMismatch, frame);

            var expectedSequence = expectedJob ? 0 : 2;
            if (frame.Sequence != expectedSequence)
                return Reject(FixtureB1OwnerChannelDiagnosticCode.SequenceMismatch, frame);
            if (!Observe(() => _authority.IsFrameAuthenticated(frame)))
                return Reject(
                    FixtureB1OwnerChannelDiagnosticCode.FrameAuthenticationRejected, frame);

            _current = new(frame);
            _pendingDuplicate = null;
            _stage = expectedJob ? Stage.AwaitingJobAdoption : Stage.AwaitingProcessAdoption;
            return true;
        }
    }

    internal bool FailFrameAdmission(Guid operationId)
    {
        lock (_gate)
        {
            if (_stage == Stage.Rejected)
                return false;
            var pending = _pendingDuplicate;
            if (pending is null ||
                (_stage != Stage.AwaitingJobFrame &&
                 _stage != Stage.AwaitingProcessFrame) ||
                pending.OperationId != operationId)
                return Reject(FixtureB1OwnerChannelDiagnosticCode.OrderViolation,
                    pending is null ? null : FrameFrom(pending));

            var frame = FrameFrom(pending);
            if (!Persist(Entry(FixtureB1OwnerJournalEvent.ProtocolRejected, frame)))
                return SetRejected(FixtureB1OwnerChannelDiagnosticCode.JournalFailure);
            SetRejected(FixtureB1OwnerChannelDiagnosticCode.InvalidFrame);
            return true;
        }
    }

    internal FixtureB1OwnerAdoptionDisposition TryAdopt(
        FixtureB1OwnerChannelAdoption adoption)
    {
        ArgumentNullException.ThrowIfNull(adoption);
        lock (_gate)
        {
            if (_ownedAdoptions.Contains(adoption))
                return FixtureB1OwnerAdoptionDisposition.AlreadyOwned;
            var handleKey = (adoption.Owner, adoption.TargetLocalCoordinate);
            if (_ownedCoordinates.Contains(handleKey))
            {
                Reject(FixtureB1OwnerChannelDiagnosticCode.ResourceAlias, _current?.Frame);
                return FixtureB1OwnerAdoptionDisposition.AlreadyOwned;
            }
            if (_stage == Stage.Rejected || _current is null ||
                (_stage != Stage.AwaitingJobAdoption &&
                 _stage != Stage.AwaitingProcessAdoption))
                return RetainAndReject(
                    adoption, FixtureB1OwnerChannelDiagnosticCode.OrderViolation);
            if (_current.Adoption is not null)
                return RetainAndReject(
                    adoption, FixtureB1OwnerChannelDiagnosticCode.ResourceAlias);
            var frame = _current.Frame;
            if (_ownedResourceReceipts.Contains(adoption.ResourceReceipt))
                return RetainAndReject(
                    adoption, FixtureB1OwnerChannelDiagnosticCode.ResourceAlias);

            var commonValid = adoption.OperationId == frame.OperationId &&
                adoption.ResourceKind == frame.ResourceKind &&
                adoption.Owner == frame.Target &&
                adoption.TargetLocalCoordinate == frame.TargetLocalCoordinate &&
                adoption.ResourceReceipt == frame.ResourceReceipt &&
                adoption.NonInheritable;
            var objectValid = frame.ResourceKind switch
            {
                FixtureB1OwnerResourceKind.RunnerJob =>
                    adoption.ExactProcess is null &&
                    adoption.ContainingJobReceipt is null &&
                    Observe(() => _authority.IsExactRunnerJob(adoption, _reservation)),
                FixtureB1OwnerResourceKind.RunnerProcess =>
                    _runner is not null &&
                    adoption.ExactProcess == _runner.ExactRunner &&
                    frame.ExactRunner == _runner.ExactRunner &&
                    adoption.ContainingJobReceipt == _reservation.ResourceReceipt &&
                    Observe(() => _authority.IsExactRunnerProcess(
                        adoption, _runner, _reservation)),
                _ => false,
            };
            if (!commonValid || !objectValid)
                return RetainAndReject(
                    adoption, FixtureB1OwnerChannelDiagnosticCode.WrongObject);

            var entry = Entry(FixtureB1OwnerJournalEvent.Adopted, frame, adoption);
            if (!Persist(entry))
            {
                TrackProviderRetained(adoption);
                SetRejected(FixtureB1OwnerChannelDiagnosticCode.JournalFailure);
                return FixtureB1OwnerAdoptionDisposition.ProviderRetainsNewResource;
            }

            _ownedCoordinates.Add(handleKey);
            _ownedResourceReceipts.Add(adoption.ResourceReceipt);
            _ownedAdoptions.Add(adoption);
            _current.Adoption = adoption;
            _stage = frame.ResourceKind == FixtureB1OwnerResourceKind.RunnerJob
                ? Stage.AwaitingJobAck
                : Stage.AwaitingProcessAck;
            return FixtureB1OwnerAdoptionDisposition.Adopted;
        }
    }

    internal FixtureB1OwnerChannelAck? CreateAck(Guid operationId, Guid receiptId)
    {
        lock (_gate)
        {
            if (_stage == Stage.Rejected)
                return null;
            if (_current is null || _current.Adoption is null ||
                (_stage != Stage.AwaitingJobAck && _stage != Stage.AwaitingProcessAck))
            {
                Reject(FixtureB1OwnerChannelDiagnosticCode.OrderViolation, _current?.Frame);
                return null;
            }
            if (_current.Ack is not null || receiptId == Guid.Empty ||
                _issuedReceiptIds.Contains(receiptId) ||
                _acceptedReceiptIds.Contains(receiptId))
            {
                Reject(FixtureB1OwnerChannelDiagnosticCode.AckReplay, _current.Frame);
                return null;
            }
            if (operationId != _current.Frame.OperationId)
            {
                Reject(FixtureB1OwnerChannelDiagnosticCode.BindingMismatch, _current.Frame);
                return null;
            }

            var frame = _current.Frame;
            var ack = new FixtureB1OwnerChannelAck(
                _binding, frame.OperationId, frame.SuccessInstanceId,
                frame.Sequence + 1, frame.ResourceKind,
                frame.Target, frame.Source, frame.ExactRunner,
                frame.TargetLocalCoordinate,
                frame.ResourceReceipt, receiptId);
            if (!Persist(Entry(FixtureB1OwnerJournalEvent.AckIssued, frame,
                    _current.Adoption, receiptId, ack.Sequence)))
            {
                SetRejected(FixtureB1OwnerChannelDiagnosticCode.JournalFailure);
                return null;
            }

            _issuedReceiptIds.Add(receiptId);
            _current.Ack = ack;
            return ack;
        }
    }

    internal bool AcceptAck(FixtureB1OwnerChannelAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        lock (_gate)
        {
            if (_acceptedOperationIds.Contains(ack.OperationId) ||
                _acceptedReceiptIds.Contains(ack.ReceiptId))
                return Reject(FixtureB1OwnerChannelDiagnosticCode.AckReplay, _current?.Frame);
            if (_stage == Stage.Rejected)
                return false;
            if (_current?.Ack is null ||
                (_stage != Stage.AwaitingJobAck && _stage != Stage.AwaitingProcessAck))
                return Reject(
                    FixtureB1OwnerChannelDiagnosticCode.OrderViolation, _current?.Frame);
            if (ack != _current.Ack)
                return Reject(
                    FixtureB1OwnerChannelDiagnosticCode.BindingMismatch, _current.Frame);
            if (!Observe(() => _authority.IsAckAuthenticated(ack)))
                return Reject(
                    FixtureB1OwnerChannelDiagnosticCode.AckAuthenticationRejected,
                    _current.Frame);
            if (!Persist(Entry(FixtureB1OwnerJournalEvent.Acknowledged,
                    _current.Frame, _current.Adoption, ack.ReceiptId, ack.Sequence)))
                return SetRejected(FixtureB1OwnerChannelDiagnosticCode.JournalFailure);

            _acceptedOperationIds.Add(ack.OperationId);
            _acceptedReceiptIds.Add(ack.ReceiptId);
            _acknowledgedTransfers++;
            if (_current.Frame.ResourceKind == FixtureB1OwnerResourceKind.RunnerJob)
                _stage = Stage.AwaitingRunnerRegistration;
            else
                _stage = Stage.Ready;
            return true;
        }
    }

    internal bool AbandonAck(Guid operationId)
    {
        lock (_gate)
        {
            if (_stage == Stage.Rejected)
                return false;
            if (_current?.Ack is null || _current.Frame.OperationId != operationId ||
                (_stage != Stage.AwaitingJobAck && _stage != Stage.AwaitingProcessAck))
                return Reject(
                    FixtureB1OwnerChannelDiagnosticCode.OrderViolation, _current?.Frame);
            if (!Persist(Entry(FixtureB1OwnerJournalEvent.AckLost,
                    _current.Frame, _current.Adoption, _current.Ack.ReceiptId,
                    _current.Ack.Sequence)))
                return SetRejected(FixtureB1OwnerChannelDiagnosticCode.JournalFailure);
            SetRejected(FixtureB1OwnerChannelDiagnosticCode.AckLost);
            return true;
        }
    }

    internal bool RegisterSuspendedRunner(FixtureB1OwnerRunnerRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_gate)
        {
            if (_stage == Stage.Rejected)
                return false;
            if (_stage != Stage.AwaitingRunnerRegistration)
                return Reject(
                    FixtureB1OwnerChannelDiagnosticCode.OrderViolation, _current?.Frame);
            if (!registration.ExactRunner.IsValid ||
                registration.ExactRunner == _binding.ExactWrapper ||
                registration.ExactRunner == _binding.ExactLauncher ||
                registration.Creator != _binding.ExactLauncher ||
                registration.RunnerJobReceipt != _reservation.ResourceReceipt ||
                !registration.CreatedSuspended || registration.InheritedHandleCount != 0 ||
                !Observe(() => _authority.IsExactRunnerRegistration(
                    registration, _reservation)))
                return Reject(
                    FixtureB1OwnerChannelDiagnosticCode.WrongObject, _current?.Frame);

            _runner = registration;
            _stage = Stage.AwaitingProcessOffer;
            return true;
        }
    }

    internal bool TryAuthorizeRunnerRelease()
    {
        lock (_gate)
        {
            if (_stage == Stage.Rejected)
                return false;
            if (_stage != Stage.Ready || _runner is null)
                return Reject(
                    FixtureB1OwnerChannelDiagnosticCode.OrderViolation,
                    _current?.Frame);
            if (_releaseAuthorizationConsumed)
                return Reject(
                    FixtureB1OwnerChannelDiagnosticCode.ReleaseReplay,
                    _current?.Frame);

            // The grant attempt itself is one-use. A false/throwing external observation
            // cannot be retried against a later or substituted W/L state.
            _releaseAuthorizationConsumed = true;
            if (!Observe(() => _authority.IsReleaseBoundaryLiveAndOwned(
                    _binding, _runner)))
                return Reject(
                    FixtureB1OwnerChannelDiagnosticCode.ReleaseAuthorityRejected,
                    _current?.Frame);
            if (_current is null ||
                !Persist(Entry(
                    FixtureB1OwnerJournalEvent.ReleaseAuthorized, _current.Frame)))
                return SetRejected(FixtureB1OwnerChannelDiagnosticCode.JournalFailure);

            _releaseAuthorized = true;
            return true;
        }
    }

    /// <summary>
    /// Records externally proved death of exact W or L. PID, EOF, copied coordinates and
    /// status records are insufficient; the authority must observe the retained process object.
    /// Death at any point before release permanently closes the managed gate.
    /// </summary>
    internal bool ReportPeerDeath(FixtureHandoffProcessIdentity peer)
    {
        ArgumentNullException.ThrowIfNull(peer);
        lock (_gate)
        {
            if (_stage == Stage.Rejected)
                return false;
            if ((peer != _binding.ExactWrapper && peer != _binding.ExactLauncher) ||
                !Observe(() => _authority.IsExactPeerTerminated(peer)))
                return Reject(
                    FixtureB1OwnerChannelDiagnosticCode.WrongObject, _current?.Frame);

            var frame = _current?.Frame ?? DeathFrame(peer);
            var entry = Entry(FixtureB1OwnerJournalEvent.PeerDeathObserved, frame) with
            {
                Target = peer,
            };
            if (!Persist(entry))
                return SetRejected(FixtureB1OwnerChannelDiagnosticCode.JournalFailure);
            SetRejected(FixtureB1OwnerChannelDiagnosticCode.PeerDied);
            return true;
        }
    }

    internal bool ConfirmProviderRetainedClose(
        FixtureB1OwnerChannelAdoption adoption,
        Guid closeReceipt)
    {
        ArgumentNullException.ThrowIfNull(adoption);
        lock (_gate)
        {
            if (closeReceipt == Guid.Empty || !_providerRetained.TryGetValue(adoption, out var closed) ||
                closed || _issuedReceiptIds.Contains(closeReceipt) ||
                _acceptedReceiptIds.Contains(closeReceipt) ||
                !Observe(() => _authority.IsProviderRetainedResourceClosed(
                    adoption, closeReceipt)))
                return false;

            var frame = _current?.Frame ?? FrameFrom(adoption);
            if (!Persist(Entry(FixtureB1OwnerJournalEvent.ProviderRetainedClosed,
                    frame, adoption, closeReceipt)))
            {
                if (_diagnostic == FixtureB1OwnerChannelDiagnosticCode.None)
                    _diagnostic = FixtureB1OwnerChannelDiagnosticCode.JournalFailure;
                return false;
            }
            _providerRetained[adoption] = true;
            _issuedReceiptIds.Add(closeReceipt);
            return true;
        }
    }

    internal FixtureB1OwnerChannelResult Snapshot()
    {
        lock (_gate)
        {
            var state = _stage switch
            {
                Stage.Ready => FixtureB1OwnerChannelState.Ready,
                Stage.Rejected => FixtureB1OwnerChannelState.Rejected,
                _ => FixtureB1OwnerChannelState.Pending,
            };
            return new(
                state,
                _diagnostic,
                _acknowledgedTransfers,
                _journaledRemoteObligations,
                _unconfirmedRemoteObligations,
                _providerRetained.Count,
                _providerRetained.Count(pair => !pair.Value),
                _releaseAuthorized,
                new ReadOnlyCollection<FixtureB1OwnerJournalEntry>([.. _entries]));
        }
    }

    private FixtureB1OwnerAdoptionDisposition RetainAndReject(
        FixtureB1OwnerChannelAdoption adoption,
        FixtureB1OwnerChannelDiagnosticCode diagnostic)
    {
        TrackProviderRetained(adoption);
        var frame = _current?.Frame ?? FrameFrom(adoption);
        if (!Persist(Entry(
                FixtureB1OwnerJournalEvent.ProviderRetainedRejected, frame, adoption)))
            SetRejected(FixtureB1OwnerChannelDiagnosticCode.JournalFailure);
        else
            SetRejected(diagnostic);
        return FixtureB1OwnerAdoptionDisposition.ProviderRetainsNewResource;
    }

    private void TrackProviderRetained(FixtureB1OwnerChannelAdoption adoption)
    {
        _providerRetained.TryAdd(adoption, false);
    }

    private bool Reject(
        FixtureB1OwnerChannelDiagnosticCode diagnostic,
        FixtureB1OwnerChannelFrame? frame)
    {
        if (_stage == Stage.Rejected)
            return false;
        if (frame is not null &&
            !Persist(Entry(FixtureB1OwnerJournalEvent.ProtocolRejected, frame)))
            diagnostic = FixtureB1OwnerChannelDiagnosticCode.JournalFailure;
        return SetRejected(diagnostic);
    }

    private bool SetRejected(FixtureB1OwnerChannelDiagnosticCode diagnostic)
    {
        _stage = Stage.Rejected;
        if (_diagnostic == FixtureB1OwnerChannelDiagnosticCode.None)
            _diagnostic = diagnostic;
        return false;
    }

    private bool Persist(FixtureB1OwnerJournalEntry entry)
    {
        bool appended;
        try { appended = _journal.TryAppend(entry); }
        catch { appended = false; }
        if (!appended)
            return false;
        _entries.Add(entry);
        return true;
    }

    private FixtureB1OwnerJournalEntry Entry(
        FixtureB1OwnerJournalEvent @event,
        FixtureB1OwnerChannelFrame frame,
        FixtureB1OwnerChannelAdoption? adoption = null,
        Guid? receiptId = null,
        long? sequence = null) => new(
            ++_nextOrdinal,
            @event,
            frame.OperationId,
            frame.SuccessInstanceId == Guid.Empty ? null : frame.SuccessInstanceId,
            sequence ?? frame.Sequence,
            frame.ResourceKind,
            frame.Direction,
            frame.Source,
            frame.Target,
            adoption?.TargetLocalCoordinate ?? frame.TargetLocalCoordinate,
            adoption?.ResourceReceipt ?? frame.ResourceReceipt,
            receiptId);

    private FixtureB1OwnerJournalEntry Entry(
        FixtureB1OwnerRemoteDuplicateReceipt duplicate) => new(
            ++_nextOrdinal,
            FixtureB1OwnerJournalEvent.RemoteCoordinateJournaled,
            duplicate.OperationId,
            duplicate.SuccessInstanceId,
            -1,
            duplicate.ResourceKind,
            duplicate.Direction,
            duplicate.Source,
            duplicate.Target,
            duplicate.TargetLocalCoordinate,
            duplicate.ResourceReceipt,
            null);

    private FixtureB1OwnerChannelFrame FrameFrom(
        FixtureB1OwnerRemoteDuplicateReservation reservation) => new(
            _binding,
            reservation.OperationId,
            reservation.SuccessInstanceId,
            reservation.ResourceKind == FixtureB1OwnerResourceKind.RunnerJob ? 0 : 2,
            reservation.Direction,
            reservation.ResourceKind,
            reservation.Source,
            reservation.Target,
            reservation.ExactRunner,
            0,
            0,
            true);

    private FixtureB1OwnerChannelFrame FrameFrom(
        FixtureB1OwnerRemoteDuplicateReceipt duplicate) => new(
            _binding,
            duplicate.OperationId,
            duplicate.SuccessInstanceId,
            duplicate.ResourceKind == FixtureB1OwnerResourceKind.RunnerJob ? 0 : 2,
            duplicate.Direction,
            duplicate.ResourceKind,
            duplicate.Source,
            duplicate.Target,
            duplicate.ExactRunner,
            duplicate.TargetLocalCoordinate,
            duplicate.ResourceReceipt,
            duplicate.NonInheritable);

    private FixtureB1OwnerChannelFrame FrameFrom(
        FixtureB1OwnerChannelAdoption adoption) => new(
            _binding,
            adoption.OperationId,
            Guid.Empty,
            -1,
            adoption.ResourceKind == FixtureB1OwnerResourceKind.RunnerJob
                ? FixtureB1OwnerChannelDirection.WrapperToLauncher
                : FixtureB1OwnerChannelDirection.LauncherToWrapper,
            adoption.ResourceKind,
            adoption.ResourceKind == FixtureB1OwnerResourceKind.RunnerJob
                ? _binding.ExactWrapper
                : _binding.ExactLauncher,
            adoption.Owner,
            adoption.ResourceKind == FixtureB1OwnerResourceKind.RunnerProcess
                ? adoption.ExactProcess
                : null,
            adoption.TargetLocalCoordinate,
            adoption.ResourceReceipt,
            adoption.NonInheritable);

    private FixtureB1OwnerChannelFrame DeathFrame(
        FixtureHandoffProcessIdentity peer) => new(
            _binding,
            Guid.Empty,
            Guid.Empty,
            -1,
            peer == _binding.ExactWrapper
                ? FixtureB1OwnerChannelDirection.WrapperToLauncher
                : FixtureB1OwnerChannelDirection.LauncherToWrapper,
            FixtureB1OwnerResourceKind.RunnerJob,
            peer,
            peer,
            _runner?.ExactRunner,
            0,
            _reservation.ResourceReceipt,
            true);

    private static bool ReceiptMatchesReservation(
        FixtureB1OwnerRemoteDuplicateReceipt duplicate,
        FixtureB1OwnerRemoteDuplicateReservation reservation) =>
        duplicate.OperationId == reservation.OperationId &&
        duplicate.SuccessInstanceId == reservation.SuccessInstanceId &&
        duplicate.Direction == reservation.Direction &&
        duplicate.ResourceKind == reservation.ResourceKind &&
        duplicate.Source == reservation.Source &&
        duplicate.Target == reservation.Target &&
        duplicate.ExactRunner == reservation.ExactRunner;

    private static bool ValidBootstrapShape(
        FixtureB1OwnerChannelBinding binding,
        FixtureB1OwnerJobReservation reservation) =>
        binding.RunNonce != Guid.Empty && binding.LaunchId != Guid.Empty &&
        binding.RunNonce != binding.LaunchId &&
        binding.ExactWrapper.IsValid && binding.ExactLauncher.IsValid &&
        binding.ExactWrapper != binding.ExactLauncher &&
        binding.ConnectionReceipt > 0 &&
        reservation.Owner == binding.ExactWrapper &&
        reservation.ResourceReceipt > 0 && reservation.OwnerLeaseReceipt > 0 &&
        reservation.ResourceReceipt != reservation.OwnerLeaseReceipt &&
        reservation.NonInheritable && reservation.KillOnLastHandleClose &&
        reservation.InitiallyEmpty;

    private static bool Observe(Func<bool> observation)
    {
        try { return observation(); }
        catch { return false; }
    }
}
