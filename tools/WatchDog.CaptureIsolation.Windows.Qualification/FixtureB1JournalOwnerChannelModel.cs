using System.Collections.ObjectModel;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureB1JournalEndpointState
{
    Pending,
    ParentReady,
    Ready,
    WritableIdle,
    Poisoned,
    ForensicOnly,
    Closed,
}

internal enum FixtureB1JournalChannelDirection
{
    OwnerToWriter,
    WriterToOwner,
}

internal enum FixtureB1JournalResourceKind
{
    TrustedParent,
    PrivateRootRead,
    FramesRead,
    AnchorRead,
}

internal enum FixtureB1JournalDiagnosticCode
{
    None,
    InvalidBootstrap,
    BootstrapAuthorityRejected,
    CustodyStoreFailure,
    OrderViolation,
    InvalidTransfer,
    TransferAuthenticationRejected,
    OperationReplay,
    WrongObject,
    ExcessRights,
    InheritableHandle,
    AckAuthenticationRejected,
    AckLost,
    ReleaseReplay,
    ReleaseAuthenticationRejected,
    SourceLeaseDrift,
    SourceCloseBarrier,
    StopObserved,
    OwnerDied,
    WriterDiedBeforeCustody,
    OwnerCustodyLost,
    UncertainClose,
    PrematureWrite,
}

internal enum FixtureB1JournalCustodyEvent
{
    TransferIntentRecorded,
    TransferSuccessRecorded,
    ReceiverAdopted,
    AckIssued,
    AckAccepted,
    AckLost,
    CustodyRecorded,
    LateCompletionRecorded,
    CloseConfirmed,
    CloseUncertain,
    WriterDeathObserved,
}

internal enum FixtureB1JournalCloseDisposition
{
    Confirmed,
    Uncertain,
}

internal sealed record FixtureB1JournalChannelBinding(
    int ProtocolVersion,
    Guid RunNonce,
    Guid LaunchId,
    Guid WrapperInstance,
    long SessionEpoch,
    long ConnectionGeneration,
    FixtureHandoffProcessIdentity ExactOwner,
    long OwnerProcessLeaseGeneration,
    FixtureHandoffProcessIdentity ExactWriter,
    long WriterProcessLeaseGeneration,
    long TrustedParentLeaseGeneration,
    FixtureArtifactIdentity TrustedParentIdentity,
    string TrustedParentFinalPath,
    string PrivateRootName,
    string FramesName,
    string AnchorName);

internal sealed record FixtureB1JournalObjectObservation(
    FixtureArtifactIdentity Identity,
    FixtureArtifactIdentity? ParentIdentity,
    string Name,
    string FinalPath,
    uint EffectiveGrantedAccess,
    bool NonInheritable,
    bool SecurityMatches,
    uint NumberOfLinks,
    bool DeletePending,
    bool HasUnexpectedStreams);

internal sealed record FixtureB1JournalTransferDescriptor(
    FixtureB1JournalChannelBinding Binding,
    Guid OperationId,
    Guid SuccessInstanceId,
    long Sequence,
    FixtureB1JournalChannelDirection Direction,
    FixtureB1JournalResourceKind ResourceKind,
    FixtureHandoffProcessIdentity Source,
    long SourceProcessLeaseGeneration,
    FixtureHandoffProcessIdentity Target,
    long TargetProcessLeaseGeneration,
    long SourceHandleCoordinate,
    long SourceHandleLeaseGeneration,
    uint IntendedAccess);

internal sealed record FixtureB1JournalTransferSuccess(
    FixtureB1JournalTransferDescriptor Transfer,
    long TargetLocalCoordinate,
    long TargetResourceReceipt);

internal sealed record FixtureB1JournalChannelAck(
    FixtureB1JournalChannelBinding Binding,
    Guid OperationId,
    Guid SuccessInstanceId,
    long Sequence,
    FixtureB1JournalResourceKind ResourceKind,
    FixtureHandoffProcessIdentity AdoptingProcess,
    long TargetLocalCoordinate,
    long TargetResourceReceipt,
    Guid ReceiptId);

internal sealed record FixtureB1JournalAckAcceptance(
    FixtureB1JournalChannelAck Ack,
    Guid ReceiptId);

internal sealed record FixtureB1JournalSourceLease(
    FixtureB1JournalResourceKind ResourceKind,
    long SourceHandleCoordinate,
    long SourceHandleLeaseGeneration,
    long ResourceReceipt,
    FixtureB1JournalObjectObservation Observation);

internal sealed record FixtureB1JournalWriteRelease(
    FixtureB1JournalChannelBinding Binding,
    Guid ReceiptId,
    IReadOnlyList<FixtureB1JournalSourceLease> Sources);

internal readonly record struct FixtureB1JournalCleanupHandleKey(
    FixtureHandoffProcessIdentity Owner,
    long LocalCoordinate,
    long ResourceReceipt);

internal sealed record FixtureB1JournalCustodyEntry(
    long Ordinal,
    FixtureB1JournalCustodyEvent Event,
    Guid OperationId,
    Guid? SuccessInstanceId,
    FixtureB1JournalResourceKind ResourceKind,
    FixtureB1JournalChannelDirection Direction,
    long TargetLocalCoordinate,
    long TargetResourceReceipt,
    Guid? ReceiptId);

internal interface IFixtureB1JournalOwnerCustodyStore
{
    // Admission and acknowledged appends must be independent of the frame/anchor journal.
    // Either returning false closes release even when earlier appends succeeded.
    bool IsIndependentlyAdmitted(FixtureB1JournalChannelBinding binding);
    bool TryAppendAcknowledged(FixtureB1JournalCustodyEntry entry);
}

/// <summary>
/// External proof surface for the pure O/W model. Implementations must derive their answers
/// from retained process and object authority. Typed coordinates, PIDs, paths and records are
/// deliberately insufficient by themselves.
/// </summary>
internal interface IFixtureB1JournalOwnerChannelAuthority
{
    bool IsBootstrapAuthenticated(FixtureB1JournalChannelBinding binding);
    bool IsTransferAuthenticated(FixtureB1JournalTransferDescriptor transfer);
    bool IsExactParentAdoption(
        FixtureB1JournalTransferSuccess success,
        FixtureB1JournalObjectObservation observation);
    bool IsExactReadAdoption(
        FixtureB1JournalTransferSuccess success,
        FixtureB1JournalObjectObservation observation,
        FixtureB1JournalSourceLease source);
    bool IsAckAuthenticated(FixtureB1JournalChannelAck ack);
    bool IsAckAcceptanceAuthenticated(FixtureB1JournalAckAcceptance acceptance);
    bool IsExactWriterSourceSet(
        FixtureB1JournalChannelBinding binding,
        IReadOnlyList<FixtureB1JournalSourceLease> sources);
    bool IsWriteReleaseAuthenticated(FixtureB1JournalWriteRelease release);
    bool IsWriteBoundaryLiveAndOwned(
        FixtureB1JournalChannelBinding binding,
        IReadOnlyList<FixtureB1JournalSourceLease> sources);
    bool IsOwnerReadCustodyLive(
        FixtureB1JournalChannelBinding binding,
        IReadOnlyList<FixtureB1JournalSourceLease> sources);
    bool IsExactProcessTerminated(FixtureHandoffProcessIdentity process);
}

internal sealed record FixtureB1JournalOwnerSnapshot(
    FixtureB1JournalEndpointState State,
    FixtureB1JournalDiagnosticCode DiagnosticCode,
    int AdoptedReadCount,
    int OutstandingLocalCloseCount,
    int OutstandingRemoteCloseCount,
    int EmergencyObligationCount,
    int UnconfirmedSuccessCount,
    int UncertainCloseCount,
    bool CanReadMetadata,
    bool CanReadForensicBytes,
    IReadOnlyList<FixtureB1JournalCleanupHandleKey> UnresolvedCleanupHandles,
    IReadOnlyList<FixtureB1JournalCustodyEntry> Custody)
{
    internal bool NativeAuthorityProven => false;
    internal bool NativeDurabilityProven => false;
}

internal sealed record FixtureB1JournalWriterSnapshot(
    FixtureB1JournalEndpointState State,
    FixtureB1JournalDiagnosticCode DiagnosticCode,
    bool ParentAcknowledged,
    int RegisteredSourceCount,
    int AcknowledgedReadCount,
    bool WriteInFlight,
    bool CanCreateJournal,
    bool CanWrite,
    int OutstandingRejectedParentCloseCount,
    int UnconfirmedParentSuccessCount,
    int UncertainRejectedParentCloseCount,
    IReadOnlyList<FixtureB1JournalCleanupHandleKey>
        UnresolvedRejectedParentCleanupHandles)
{
    internal bool NativeAuthorityProven => false;
    internal bool NativeDurabilityProven => false;
}

internal static class FixtureB1JournalAccessPolicy
{
    internal const uint WriterParent =
        (uint)(FixtureB1NativeJournalHandleAccess.ReadDataOrListDirectory |
               FixtureB1NativeJournalHandleAccess.AppendDataOrAddSubdirectory |
               FixtureB1NativeJournalHandleAccess.ExecuteOrTraverse |
               FixtureB1NativeJournalHandleAccess.ReadAttributes |
               FixtureB1NativeJournalHandleAccess.ReadControl |
               FixtureB1NativeJournalHandleAccess.Synchronize);

    internal const uint OwnerRootRead =
        (uint)(FixtureB1NativeJournalHandleAccess.ReadDataOrListDirectory |
               FixtureB1NativeJournalHandleAccess.ExecuteOrTraverse |
               FixtureB1NativeJournalHandleAccess.ReadAttributes |
               FixtureB1NativeJournalHandleAccess.ReadControl |
               FixtureB1NativeJournalHandleAccess.Synchronize);

    internal const uint OwnerFileRead =
        (uint)(FixtureB1NativeJournalHandleAccess.ReadDataOrListDirectory |
               FixtureB1NativeJournalHandleAccess.ReadAttributes |
               FixtureB1NativeJournalHandleAccess.ReadControl |
               FixtureB1NativeJournalHandleAccess.Synchronize);

    internal static bool IsExact(uint actual, uint expected) => actual == expected;
}

/// <summary>
/// Pure O endpoint. Its append-only custody store is independent of the journal being created.
/// This model performs no process launch, IPC, cryptography, DuplicateHandle, file I/O or native
/// observation and remains unregistered.
/// </summary>
internal sealed class FixtureB1JournalOwnerEndpoint
{
    private static readonly FixtureB1JournalResourceKind[] ReadOrder =
    [
        FixtureB1JournalResourceKind.PrivateRootRead,
        FixtureB1JournalResourceKind.FramesRead,
        FixtureB1JournalResourceKind.AnchorRead,
    ];

    private readonly object _gate = new();
    private readonly FixtureB1JournalChannelBinding _binding;
    private readonly IFixtureB1JournalOwnerChannelAuthority _authority;
    private readonly IFixtureB1JournalOwnerCustodyStore _store;
    private readonly List<FixtureB1JournalCustodyEntry> _custody = [];
    private readonly HashSet<Guid> _operationIds = [];
    private readonly HashSet<Guid> _successIds = [];
    private readonly HashSet<Guid> _receiptIds = [];
    private readonly Dictionary<FixtureB1JournalResourceKind,
        FixtureB1JournalTransferSuccess> _ownedReadResults = [];
    private readonly Dictionary<FixtureB1JournalResourceKind,
        FixtureB1JournalSourceLease> _adoptedSources = [];
    private readonly HashSet<FixtureB1JournalTransferSuccess> _reportedSuccesses = [];
    private readonly Dictionary<FixtureB1JournalCleanupHandleKey,
        FixtureB1JournalTransferSuccess> _cleanupObligations = [];
    private readonly HashSet<FixtureB1JournalCleanupHandleKey> _closeAttemptedObligations = [];
    private readonly HashSet<FixtureB1JournalCleanupHandleKey> _confirmedClosedObligations = [];
    private readonly HashSet<FixtureB1JournalCleanupHandleKey> _uncertainCloseObligations = [];
    private readonly HashSet<FixtureB1JournalCleanupHandleKey> _settledRemoteObligations = [];
    private readonly HashSet<FixtureB1JournalResourceKind> _adoptedReads = [];
    private readonly HashSet<FixtureB1JournalResourceKind> _closedReads = [];
    private FixtureB1JournalEndpointState _state = FixtureB1JournalEndpointState.Pending;
    private FixtureB1JournalDiagnosticCode _diagnostic;
    private FixtureB1JournalTransferDescriptor? _parentIntent;
    private FixtureB1JournalTransferSuccess? _parentSuccess;
    private FixtureB1JournalTransferDescriptor? _readIntent;
    private FixtureB1JournalTransferSuccess? _readSuccess;
    private int _nextRead;
    private long _nextOrdinal;
    private int _emergencyObligations;
    private int _unconfirmedSuccesses;
    private bool _parentAckAccepted;
    private bool _releaseAttempted;

    internal FixtureB1JournalOwnerEndpoint(
        FixtureB1JournalChannelBinding binding,
        IFixtureB1JournalOwnerChannelAuthority authority,
        IFixtureB1JournalOwnerCustodyStore store)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(store);
        _binding = binding;
        _authority = authority;
        _store = store;

        if (!FixtureB1JournalChannelValidation.ValidBinding(binding))
            Poison(FixtureB1JournalDiagnosticCode.InvalidBootstrap);
        else if (!Observe(() => authority.IsBootstrapAuthenticated(binding)))
            Poison(FixtureB1JournalDiagnosticCode.BootstrapAuthorityRejected);
        else if (!Observe(() => store.IsIndependentlyAdmitted(binding)))
            Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
    }

    internal bool ReserveParentTransfer(FixtureB1JournalTransferDescriptor transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        lock (_gate)
        {
            if (_state == FixtureB1JournalEndpointState.Poisoned)
                return false;
            if (_parentIntent is not null || !ValidateTransfer(
                    transfer, 0, FixtureB1JournalChannelDirection.OwnerToWriter,
                    FixtureB1JournalResourceKind.TrustedParent,
                    _binding.ExactOwner, _binding.OwnerProcessLeaseGeneration,
                    _binding.ExactWriter, _binding.WriterProcessLeaseGeneration,
                    _binding.TrustedParentLeaseGeneration,
                    FixtureB1JournalAccessPolicy.WriterParent))
                return Poison(FixtureB1JournalDiagnosticCode.InvalidTransfer);
            if (!RememberIdentity(transfer))
                return Poison(FixtureB1JournalDiagnosticCode.OperationReplay);
            if (!Observe(() => _authority.IsTransferAuthenticated(transfer)))
                return Poison(FixtureB1JournalDiagnosticCode.TransferAuthenticationRejected);
            if (!Persist(Entry(FixtureB1JournalCustodyEvent.TransferIntentRecorded,
                    transfer)))
                return Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
            _parentIntent = transfer;
            return true;
        }
    }

    internal bool RecordParentTransferSuccess(FixtureB1JournalTransferSuccess success)
    {
        ArgumentNullException.ThrowIfNull(success);
        lock (_gate)
        {
            var late = _state is FixtureB1JournalEndpointState.Poisoned or
                FixtureB1JournalEndpointState.ForensicOnly or
                FixtureB1JournalEndpointState.Closed;
            if (!CaptureReportedSuccess(success, late, _parentIntent))
            {
                Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
                return false;
            }
            if (late)
                return false;
            if (_parentIntent is null || _parentSuccess is not null ||
                !ValidSuccess(success, _parentIntent))
                return Poison(FixtureB1JournalDiagnosticCode.InvalidTransfer);
            _parentSuccess = success;
            return true;
        }
    }

    internal FixtureB1JournalAckAcceptance? AcceptParentAck(
        FixtureB1JournalChannelAck ack,
        Guid acceptanceReceiptId)
    {
        ArgumentNullException.ThrowIfNull(ack);
        lock (_gate)
        {
            if (_state == FixtureB1JournalEndpointState.Poisoned)
                return null;
            if (_parentSuccess is null || _parentAckAccepted ||
                !AckMatches(ack, _parentSuccess, _binding.ExactWriter) ||
                ack.Sequence != 1 || acceptanceReceiptId == Guid.Empty ||
                !_receiptIds.Add(acceptanceReceiptId))
            {
                Poison(FixtureB1JournalDiagnosticCode.OrderViolation);
                return null;
            }
            if (!Observe(() => _authority.IsAckAuthenticated(ack)))
            {
                Poison(FixtureB1JournalDiagnosticCode.AckAuthenticationRejected);
                return null;
            }
            if (!Persist(Entry(FixtureB1JournalCustodyEvent.AckAccepted,
                    _parentSuccess, ack.ReceiptId)))
            {
                Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
                return null;
            }
            _parentAckAccepted = true;
            if (TryCapturedCleanupKey(_parentSuccess, out var cleanupKey))
                _settledRemoteObligations.Add(cleanupKey);
            return new(ack, acceptanceReceiptId);
        }
    }

    internal bool ReportParentAckLost(Guid operationId)
    {
        lock (_gate)
        {
            if (_parentSuccess is null || _parentAckAccepted ||
                _parentSuccess.Transfer.OperationId != operationId)
                return Poison(FixtureB1JournalDiagnosticCode.OrderViolation);
            if (!Persist(Entry(FixtureB1JournalCustodyEvent.AckLost,
                    _parentSuccess)))
                return Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
            return Poison(FixtureB1JournalDiagnosticCode.AckLost);
        }
    }

    internal bool ReserveReadTransfer(FixtureB1JournalTransferDescriptor transfer)
    {
        ArgumentNullException.ThrowIfNull(transfer);
        lock (_gate)
        {
            if (_state is FixtureB1JournalEndpointState.Poisoned or
                FixtureB1JournalEndpointState.ForensicOnly)
                return false;
            if (!_parentAckAccepted || _nextRead >= ReadOrder.Length ||
                _readIntent is not null || !ValidateTransfer(
                    transfer, 2 + (_nextRead * 2),
                    FixtureB1JournalChannelDirection.WriterToOwner,
                    ReadOrder[_nextRead],
                    _binding.ExactWriter, _binding.WriterProcessLeaseGeneration,
                    _binding.ExactOwner, _binding.OwnerProcessLeaseGeneration,
                    transfer.SourceHandleLeaseGeneration,
                    AccessFor(ReadOrder[_nextRead])))
                return Poison(FixtureB1JournalDiagnosticCode.InvalidTransfer);
            if (!RememberIdentity(transfer))
                return Poison(FixtureB1JournalDiagnosticCode.OperationReplay);
            if (!Observe(() => _authority.IsTransferAuthenticated(transfer)))
                return Poison(FixtureB1JournalDiagnosticCode.TransferAuthenticationRejected);
            if (!Persist(Entry(FixtureB1JournalCustodyEvent.TransferIntentRecorded,
                    transfer)))
                return Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
            _readIntent = transfer;
            return true;
        }
    }

    internal bool RecordReadTransferSuccess(FixtureB1JournalTransferSuccess success)
    {
        ArgumentNullException.ThrowIfNull(success);
        lock (_gate)
        {
            var late = _state is FixtureB1JournalEndpointState.Poisoned or
                FixtureB1JournalEndpointState.ForensicOnly or
                FixtureB1JournalEndpointState.Closed;
            if (!CaptureReportedSuccess(success, late, _readIntent))
            {
                Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
                return false;
            }
            if (late)
                return false;
            if (_readIntent is null || _readSuccess is not null ||
                !ValidSuccess(success, _readIntent))
                return Poison(FixtureB1JournalDiagnosticCode.InvalidTransfer);

            _ownedReadResults[success.Transfer.ResourceKind] = success;
            _readSuccess = success;
            return true;
        }
    }

    internal FixtureB1JournalChannelAck? AdoptRead(
        FixtureB1JournalTransferSuccess success,
        FixtureB1JournalObjectObservation observation,
        FixtureB1JournalSourceLease source,
        Guid ackReceiptId)
    {
        ArgumentNullException.ThrowIfNull(success);
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(source);
        lock (_gate)
        {
            if (_state is FixtureB1JournalEndpointState.Poisoned or
                FixtureB1JournalEndpointState.ForensicOnly)
                return null;
            if (_readSuccess != success || _readIntent is null ||
                source.ResourceKind != success.Transfer.ResourceKind ||
                source.SourceHandleCoordinate != success.Transfer.SourceHandleCoordinate ||
                source.SourceHandleLeaseGeneration !=
                    success.Transfer.SourceHandleLeaseGeneration ||
                ackReceiptId == Guid.Empty || !_receiptIds.Add(ackReceiptId))
            {
                Poison(FixtureB1JournalDiagnosticCode.InvalidTransfer);
                return null;
            }
            if (!ValidReadObservation(observation, source))
            {
                Poison(observation.NonInheritable
                    ? FixtureB1JournalDiagnosticCode.ExcessRights
                    : FixtureB1JournalDiagnosticCode.InheritableHandle);
                return null;
            }
            if (!Observe(() => _authority.IsExactReadAdoption(
                    success, observation, source)))
            {
                Poison(FixtureB1JournalDiagnosticCode.WrongObject);
                return null;
            }
            if (!Persist(Entry(FixtureB1JournalCustodyEvent.ReceiverAdopted,
                    success)))
            {
                Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
                return null;
            }
            var ack = new FixtureB1JournalChannelAck(
                _binding, success.Transfer.OperationId,
                success.Transfer.SuccessInstanceId,
                success.Transfer.Sequence + 1,
                success.Transfer.ResourceKind, _binding.ExactOwner,
                success.TargetLocalCoordinate, success.TargetResourceReceipt,
                ackReceiptId);
            if (!Persist(Entry(FixtureB1JournalCustodyEvent.AckIssued,
                    success, ackReceiptId)))
            {
                Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
                return null;
            }
            _adoptedReads.Add(success.Transfer.ResourceKind);
            _adoptedSources.Add(success.Transfer.ResourceKind, source);
            _readIntent = null;
            _readSuccess = null;
            _nextRead++;
            if (_nextRead == ReadOrder.Length)
                _state = FixtureB1JournalEndpointState.Ready;
            return ack;
        }
    }

    internal FixtureB1JournalWriteRelease? CreateWriteRelease(
        Guid receiptId,
        IReadOnlyList<FixtureB1JournalSourceLease> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        lock (_gate)
        {
            if (_state is FixtureB1JournalEndpointState.Poisoned or
                FixtureB1JournalEndpointState.ForensicOnly or
                FixtureB1JournalEndpointState.Closed)
                return null;
            if (_releaseAttempted)
            {
                Poison(FixtureB1JournalDiagnosticCode.ReleaseReplay);
                return null;
            }
            _releaseAttempted = true;
            if (_state != FixtureB1JournalEndpointState.Ready ||
                _adoptedReads.Count != ReadOrder.Length || receiptId == Guid.Empty ||
                !_receiptIds.Add(receiptId) || !ExactSourceSet(sources))
            {
                Poison(FixtureB1JournalDiagnosticCode.OrderViolation);
                return null;
            }
            if (!CustodyStoreAdmitted())
            {
                Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
                return null;
            }
            if (!RequiredOwnerHandlesOpen(sources) ||
                !Observe(() => _authority.IsOwnerReadCustodyLive(_binding, sources)))
            {
                Poison(FixtureB1JournalDiagnosticCode.OwnerCustodyLost);
                return null;
            }
            var release = new FixtureB1JournalWriteRelease(
                _binding, receiptId, new ReadOnlyCollection<FixtureB1JournalSourceLease>(
                    [.. sources]));
            if (!Persist(new(++_nextOrdinal,
                    FixtureB1JournalCustodyEvent.CustodyRecorded,
                    Guid.Empty, null, FixtureB1JournalResourceKind.TrustedParent,
                    FixtureB1JournalChannelDirection.OwnerToWriter, 0, 0, receiptId)))
            {
                Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
                return null;
            }
            return release;
        }
    }

    internal bool ObserveWriterDeath()
    {
        lock (_gate)
        {
            if (!Observe(() => _authority.IsExactProcessTerminated(
                    _binding.ExactWriter)))
                return Poison(FixtureB1JournalDiagnosticCode.WrongObject);
            if (!Persist(new(++_nextOrdinal,
                    FixtureB1JournalCustodyEvent.WriterDeathObserved,
                    Guid.Empty, null, FixtureB1JournalResourceKind.TrustedParent,
                    FixtureB1JournalChannelDirection.OwnerToWriter, 0, 0, null)))
                return Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
            var sources = ReadOrder
                .Select(resource => _adoptedSources.GetValueOrDefault(resource))
                .Where(source => source is not null)
                .Cast<FixtureB1JournalSourceLease>()
                .ToArray();
            if (!CustodyStoreAdmitted())
                return Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
            if (_adoptedReads.Count == ReadOrder.Length &&
                RequiredOwnerHandlesOpen(sources) &&
                Observe(() => _authority.IsOwnerReadCustodyLive(_binding, sources)))
            {
                _state = FixtureB1JournalEndpointState.ForensicOnly;
                return true;
            }
            return Poison(_closedReads.Count > 0
                ? FixtureB1JournalDiagnosticCode.OwnerCustodyLost
                : FixtureB1JournalDiagnosticCode.WriterDiedBeforeCustody);
        }
    }

    internal bool ObserveStop()
    {
        lock (_gate)
            return Poison(FixtureB1JournalDiagnosticCode.StopObserved);
    }

    internal bool ReportReadClose(
        FixtureB1JournalResourceKind resource,
        FixtureB1JournalCloseDisposition disposition,
        Guid receiptId)
    {
        lock (_gate)
        {
            if (!_ownedReadResults.TryGetValue(resource, out var success))
                return Poison(FixtureB1JournalDiagnosticCode.OrderViolation);
            return ReportCleanupCloseCore(success, disposition, receiptId);
        }
    }

    internal bool ReportCleanupClose(
        FixtureB1JournalTransferSuccess success,
        FixtureB1JournalCloseDisposition disposition,
        Guid receiptId)
    {
        ArgumentNullException.ThrowIfNull(success);
        lock (_gate)
            return ReportCleanupCloseCore(success, disposition, receiptId);
    }

    internal FixtureB1JournalOwnerSnapshot Snapshot()
    {
        lock (_gate)
        {
            var outstandingLocal = _cleanupObligations.Count(pair =>
                pair.Value.Transfer.Direction ==
                    FixtureB1JournalChannelDirection.WriterToOwner &&
                !_confirmedClosedObligations.Contains(pair.Key));
            var outstandingRemote = _cleanupObligations.Count(pair =>
                pair.Value.Transfer.Direction ==
                    FixtureB1JournalChannelDirection.OwnerToWriter &&
                !_settledRemoteObligations.Contains(pair.Key));
            var unresolved = _cleanupObligations
                .Where(pair =>
                    pair.Value.Transfer.Direction ==
                        FixtureB1JournalChannelDirection.WriterToOwner
                        ? !_confirmedClosedObligations.Contains(pair.Key)
                        : !_settledRemoteObligations.Contains(pair.Key))
                .Select(pair => pair.Key)
                .ToArray();
            return new(
                _state, _diagnostic, _adoptedReads.Count,
                outstandingLocal,
                outstandingRemote, _emergencyObligations,
                _unconfirmedSuccesses,
                _uncertainCloseObligations.Count,
                _adoptedReads.Any(resource => !_closedReads.Contains(resource)),
                _state == FixtureB1JournalEndpointState.ForensicOnly &&
                _closedReads.Count == 0,
                new ReadOnlyCollection<FixtureB1JournalCleanupHandleKey>(unresolved),
                new ReadOnlyCollection<FixtureB1JournalCustodyEntry>([.. _custody]));
        }
    }

    private bool CaptureReportedSuccess(
        FixtureB1JournalTransferSuccess success,
        bool late,
        FixtureB1JournalTransferDescriptor? expectedIntent)
    {
        // The native result exists before any typed protocol check. Capture the physical
        // cleanup duty first, including malformed and post-stop callbacks.
        if (!_reportedSuccesses.Add(success))
            return true;

        if (TryCleanupKey(success, expectedIntent, out var key))
            _cleanupObligations.TryAdd(key, success);
        else
        {
            _unconfirmedSuccesses++;
            _emergencyObligations++;
        }

        var @event = late
            ? FixtureB1JournalCustodyEvent.LateCompletionRecorded
            : FixtureB1JournalCustodyEvent.TransferSuccessRecorded;
        if (Persist(Entry(@event, success)))
            return true;

        _emergencyObligations++;
        return false;
    }

    private bool ReportCleanupCloseCore(
        FixtureB1JournalTransferSuccess success,
        FixtureB1JournalCloseDisposition disposition,
        Guid receiptId)
    {
        if (!TryCapturedCleanupKey(success, out var key) ||
            !_cleanupObligations.ContainsKey(key) ||
            _closeAttemptedObligations.Contains(key) || receiptId == Guid.Empty ||
            !_receiptIds.Add(receiptId))
            return Poison(FixtureB1JournalDiagnosticCode.OrderViolation);

        var @event = disposition == FixtureB1JournalCloseDisposition.Confirmed
            ? FixtureB1JournalCustodyEvent.CloseConfirmed
            : FixtureB1JournalCustodyEvent.CloseUncertain;
        // The physical attempt and outcome exist before the fallible evidence append.
        // Never retry the raw handle even when that append is not acknowledged.
        _closeAttemptedObligations.Add(key);
        if (disposition == FixtureB1JournalCloseDisposition.Confirmed)
            _confirmedClosedObligations.Add(key);
        else
            _uncertainCloseObligations.Add(key);
        if (success.Transfer.Direction ==
                FixtureB1JournalChannelDirection.WriterToOwner &&
            success.Transfer.ResourceKind is
                FixtureB1JournalResourceKind.PrivateRootRead or
                FixtureB1JournalResourceKind.FramesRead or
                FixtureB1JournalResourceKind.AnchorRead)
            _closedReads.Add(success.Transfer.ResourceKind);

        if (disposition == FixtureB1JournalCloseDisposition.Uncertain)
            Poison(FixtureB1JournalDiagnosticCode.UncertainClose);
        else
        {
            if (_diagnostic == FixtureB1JournalDiagnosticCode.None)
                _diagnostic = FixtureB1JournalDiagnosticCode.OwnerCustodyLost;
            _state = _state == FixtureB1JournalEndpointState.ForensicOnly
                ? FixtureB1JournalEndpointState.Closed
                : FixtureB1JournalEndpointState.Poisoned;
        }

        if (!Persist(Entry(@event, success, receiptId)))
        {
            _emergencyObligations++;
            Poison(FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
            return false;
        }
        return disposition == FixtureB1JournalCloseDisposition.Confirmed;
    }

    private bool RequiredOwnerHandlesOpen(
        IReadOnlyList<FixtureB1JournalSourceLease> sources)
    {
        if (sources.Count != ReadOrder.Length || _closedReads.Count != 0)
            return false;
        foreach (var resource in ReadOrder)
        {
            if (!_ownedReadResults.TryGetValue(resource, out var success) ||
                !TryCapturedCleanupKey(success, out var key) ||
                !_cleanupObligations.ContainsKey(key) ||
                _closeAttemptedObligations.Contains(key))
                return false;
        }
        return true;
    }

    private bool CustodyStoreAdmitted() =>
        Observe(() => _store.IsIndependentlyAdmitted(_binding));

    private static bool TryCleanupKey(
        FixtureB1JournalTransferSuccess success,
        FixtureB1JournalTransferDescriptor? expectedIntent,
        out FixtureB1JournalCleanupHandleKey key)
    {
        if (expectedIntent is not null &&
            success.Transfer == expectedIntent &&
            success.TargetLocalCoordinate > 0 && success.TargetResourceReceipt > 0)
        {
            key = new(expectedIntent.Target, success.TargetLocalCoordinate,
                success.TargetResourceReceipt);
            return true;
        }
        key = default;
        return false;
    }

    private bool TryCapturedCleanupKey(
        FixtureB1JournalTransferSuccess success,
        out FixtureB1JournalCleanupHandleKey key)
    {
        foreach (var pair in _cleanupObligations)
        {
            if (pair.Value == success)
            {
                key = pair.Key;
                return true;
            }
        }
        key = default;
        return false;
    }

    private bool ValidateTransfer(
        FixtureB1JournalTransferDescriptor transfer,
        long sequence,
        FixtureB1JournalChannelDirection direction,
        FixtureB1JournalResourceKind resource,
        FixtureHandoffProcessIdentity source,
        long sourceProcessLease,
        FixtureHandoffProcessIdentity target,
        long targetProcessLease,
        long sourceHandleLease,
        uint intendedAccess) =>
        transfer.Binding == _binding && transfer.OperationId != Guid.Empty &&
        transfer.SuccessInstanceId != Guid.Empty && transfer.Sequence == sequence &&
        transfer.Direction == direction && transfer.ResourceKind == resource &&
        transfer.Source == source &&
        transfer.SourceProcessLeaseGeneration == sourceProcessLease &&
        transfer.Target == target &&
        transfer.TargetProcessLeaseGeneration == targetProcessLease &&
        transfer.SourceHandleCoordinate > 0 &&
        transfer.SourceHandleLeaseGeneration == sourceHandleLease &&
        transfer.IntendedAccess == intendedAccess;

    private bool RememberIdentity(FixtureB1JournalTransferDescriptor transfer) =>
        _operationIds.Add(transfer.OperationId) &&
        _successIds.Add(transfer.SuccessInstanceId);

    private bool ExactSourceSet(IReadOnlyList<FixtureB1JournalSourceLease> sources)
    {
        if (sources.Count != ReadOrder.Length)
            return false;
        foreach (var resource in ReadOrder)
        {
            if (!_ownedReadResults.TryGetValue(resource, out var result))
                return false;
            var source = sources.SingleOrDefault(item => item.ResourceKind == resource);
            if (source is null ||
                source.SourceHandleCoordinate != result.Transfer.SourceHandleCoordinate ||
                source.SourceHandleLeaseGeneration !=
                    result.Transfer.SourceHandleLeaseGeneration)
                return false;
        }
        return true;
    }

    private static bool ValidReadObservation(
        FixtureB1JournalObjectObservation observation,
        FixtureB1JournalSourceLease source)
    {
        var expectedAccess = AccessFor(source.ResourceKind);
        return observation.Identity == source.Observation.Identity &&
            observation.ParentIdentity == source.Observation.ParentIdentity &&
            StringComparer.Ordinal.Equals(observation.Name, source.Observation.Name) &&
            StringComparer.OrdinalIgnoreCase.Equals(
                observation.FinalPath, source.Observation.FinalPath) &&
            observation.SecurityMatches && observation.NonInheritable &&
            !observation.Identity.IsReparse && !observation.DeletePending &&
            !observation.HasUnexpectedStreams &&
            (observation.Identity.Kind != FixtureArtifactKind.File ||
                observation.NumberOfLinks == 1) &&
            FixtureB1JournalAccessPolicy.IsExact(
                observation.EffectiveGrantedAccess, expectedAccess);
    }

    private static uint AccessFor(FixtureB1JournalResourceKind resource) =>
        resource == FixtureB1JournalResourceKind.PrivateRootRead
            ? FixtureB1JournalAccessPolicy.OwnerRootRead
            : FixtureB1JournalAccessPolicy.OwnerFileRead;

    private static bool ValidSuccess(
        FixtureB1JournalTransferSuccess success,
        FixtureB1JournalTransferDescriptor expected) =>
        success.Transfer == expected && success.TargetLocalCoordinate > 0 &&
        success.TargetResourceReceipt > 0;

    private static bool AckMatches(
        FixtureB1JournalChannelAck ack,
        FixtureB1JournalTransferSuccess success,
        FixtureHandoffProcessIdentity adopter) =>
        ack.Binding == success.Transfer.Binding &&
        ack.OperationId == success.Transfer.OperationId &&
        ack.SuccessInstanceId == success.Transfer.SuccessInstanceId &&
        ack.ResourceKind == success.Transfer.ResourceKind &&
        ack.AdoptingProcess == adopter &&
        ack.TargetLocalCoordinate == success.TargetLocalCoordinate &&
        ack.TargetResourceReceipt == success.TargetResourceReceipt &&
        ack.ReceiptId != Guid.Empty;

    private FixtureB1JournalCustodyEntry Entry(
        FixtureB1JournalCustodyEvent @event,
        FixtureB1JournalTransferDescriptor transfer) => new(
        ++_nextOrdinal, @event, transfer.OperationId, transfer.SuccessInstanceId,
        transfer.ResourceKind, transfer.Direction, 0, 0, null);

    private FixtureB1JournalCustodyEntry Entry(
        FixtureB1JournalCustodyEvent @event,
        FixtureB1JournalTransferSuccess success,
        Guid? receiptId = null) => new(
        ++_nextOrdinal, @event, success.Transfer.OperationId,
        success.Transfer.SuccessInstanceId, success.Transfer.ResourceKind,
        success.Transfer.Direction, success.TargetLocalCoordinate,
        success.TargetResourceReceipt, receiptId);

    private bool Persist(FixtureB1JournalCustodyEntry entry)
    {
        bool appended;
        try
        {
            appended = _store.IsIndependentlyAdmitted(_binding) &&
                _store.TryAppendAcknowledged(entry);
        }
        catch { appended = false; }
        if (!appended)
            return false;
        _custody.Add(entry);
        return true;
    }

    private bool Poison(FixtureB1JournalDiagnosticCode diagnostic)
    {
        _state = FixtureB1JournalEndpointState.Poisoned;
        if (_diagnostic == FixtureB1JournalDiagnosticCode.None)
            _diagnostic = diagnostic;
        return false;
    }

    private static bool Observe(Func<bool> observation)
    {
        try { return observation(); }
        catch { return false; }
    }
}

/// <summary>
/// Pure W endpoint. Source coordinates are pinned by opaque lease generation until their
/// matching O ACK is accepted. Stop and write start share this endpoint-local irreversible gate.
/// </summary>
internal sealed class FixtureB1JournalWriterEndpoint
{
    private static readonly FixtureB1JournalResourceKind[] ReadOrder =
    [
        FixtureB1JournalResourceKind.PrivateRootRead,
        FixtureB1JournalResourceKind.FramesRead,
        FixtureB1JournalResourceKind.AnchorRead,
    ];

    private readonly object _gate = new();
    private readonly FixtureB1JournalChannelBinding _binding;
    private readonly IFixtureB1JournalOwnerChannelAuthority _authority;
    private readonly Dictionary<FixtureB1JournalResourceKind,
        FixtureB1JournalSourceLease> _sources = [];
    private readonly HashSet<Guid> _operationIds = [];
    private readonly HashSet<Guid> _successIds = [];
    private readonly HashSet<Guid> _receiptIds = [];
    private readonly HashSet<FixtureB1JournalTransferSuccess>
        _reportedParentSuccesses = [];
    private readonly Dictionary<FixtureB1JournalCleanupHandleKey,
        FixtureB1JournalTransferSuccess> _rejectedParentObligations = [];
    private readonly HashSet<FixtureB1JournalCleanupHandleKey>
        _attemptedRejectedParentCloseObligations = [];
    private readonly HashSet<FixtureB1JournalCleanupHandleKey>
        _confirmedClosedRejectedParentObligations = [];
    private readonly HashSet<FixtureB1JournalCleanupHandleKey>
        _uncertainRejectedParentCloseObligations = [];
    private FixtureB1JournalEndpointState _state = FixtureB1JournalEndpointState.Pending;
    private FixtureB1JournalDiagnosticCode _diagnostic;
    private FixtureB1JournalTransferSuccess? _parentSuccess;
    private FixtureB1JournalChannelAck? _parentAck;
    private FixtureB1JournalTransferDescriptor? _pinnedOffer;
    private int _acknowledgedReads;
    private bool _parentAcknowledged;
    private bool _releaseAttempted;
    private Guid? _writeOperation;
    private int _unconfirmedParentSuccesses;
    private FixtureB1JournalWriteRelease? _acceptedRelease;

    internal FixtureB1JournalWriterEndpoint(
        FixtureB1JournalChannelBinding binding,
        IFixtureB1JournalOwnerChannelAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(authority);
        _binding = binding;
        _authority = authority;

        if (!FixtureB1JournalChannelValidation.ValidBinding(binding))
            Poison(FixtureB1JournalDiagnosticCode.InvalidBootstrap);
        else if (!Observe(() => authority.IsBootstrapAuthenticated(binding)))
            Poison(FixtureB1JournalDiagnosticCode.BootstrapAuthorityRejected);
    }

    internal FixtureB1JournalChannelAck? AdoptParent(
        FixtureB1JournalTransferSuccess success,
        FixtureB1JournalObjectObservation observation,
        Guid ackReceiptId)
    {
        ArgumentNullException.ThrowIfNull(success);
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            var transfer = success.Transfer;
            CaptureParentSuccess(success);
            if (_state == FixtureB1JournalEndpointState.Poisoned)
            {
                MarkRejectedParent(success);
                return null;
            }
            if (_parentSuccess is not null || !ValidParentTransfer(transfer) ||
                success.TargetLocalCoordinate <= 0 || success.TargetResourceReceipt <= 0 ||
                ackReceiptId == Guid.Empty || !_receiptIds.Add(ackReceiptId))
            {
                RejectParent(success, FixtureB1JournalDiagnosticCode.InvalidTransfer);
                return null;
            }
            if (!observation.NonInheritable)
            {
                RejectParent(success, FixtureB1JournalDiagnosticCode.InheritableHandle);
                return null;
            }
            if (!FixtureB1JournalAccessPolicy.IsExact(
                    observation.EffectiveGrantedAccess,
                    FixtureB1JournalAccessPolicy.WriterParent))
            {
                RejectParent(success, FixtureB1JournalDiagnosticCode.ExcessRights);
                return null;
            }
            if (observation.Identity != _binding.TrustedParentIdentity ||
                observation.Identity.Kind != FixtureArtifactKind.Directory ||
                observation.Identity.IsReparse || observation.DeletePending ||
                !observation.SecurityMatches ||
                !StringComparer.OrdinalIgnoreCase.Equals(
                    observation.FinalPath, _binding.TrustedParentFinalPath) ||
                !Observe(() => _authority.IsExactParentAdoption(success, observation)))
            {
                RejectParent(success, FixtureB1JournalDiagnosticCode.WrongObject);
                return null;
            }
            _operationIds.Add(transfer.OperationId);
            _successIds.Add(transfer.SuccessInstanceId);
            _parentSuccess = success;
            _parentAck = new(
                _binding, transfer.OperationId, transfer.SuccessInstanceId, 1,
                FixtureB1JournalResourceKind.TrustedParent, _binding.ExactWriter,
                success.TargetLocalCoordinate, success.TargetResourceReceipt,
                ackReceiptId);
            return _parentAck;
        }
    }

    internal bool ReportRejectedParentClose(
        FixtureB1JournalTransferSuccess success,
        FixtureB1JournalCloseDisposition disposition,
        Guid receiptId)
    {
        ArgumentNullException.ThrowIfNull(success);
        lock (_gate)
        {
            if (!TryRejectedParentCleanupKey(success, out var key) ||
                !_rejectedParentObligations.ContainsKey(key) ||
                _attemptedRejectedParentCloseObligations.Contains(key) ||
                receiptId == Guid.Empty || !_receiptIds.Add(receiptId))
                return Poison(FixtureB1JournalDiagnosticCode.OrderViolation);

            _attemptedRejectedParentCloseObligations.Add(key);
            if (disposition == FixtureB1JournalCloseDisposition.Uncertain)
            {
                _uncertainRejectedParentCloseObligations.Add(key);
                return Poison(FixtureB1JournalDiagnosticCode.UncertainClose);
            }
            _confirmedClosedRejectedParentObligations.Add(key);
            return true;
        }
    }

    internal bool AcceptParentAck(FixtureB1JournalAckAcceptance acceptance)
    {
        ArgumentNullException.ThrowIfNull(acceptance);
        lock (_gate)
        {
            if (_state == FixtureB1JournalEndpointState.Poisoned)
                return false;
            if (_parentAck is null || acceptance.Ack != _parentAck ||
                acceptance.ReceiptId == Guid.Empty ||
                !_receiptIds.Add(acceptance.ReceiptId))
                return Poison(FixtureB1JournalDiagnosticCode.OrderViolation);
            if (!Observe(() => _authority.IsAckAcceptanceAuthenticated(acceptance)))
                return Poison(FixtureB1JournalDiagnosticCode.AckAuthenticationRejected);
            _parentAcknowledged = true;
            _state = FixtureB1JournalEndpointState.ParentReady;
            return true;
        }
    }

    internal bool RegisterWriterSources(
        IReadOnlyList<FixtureB1JournalSourceLease> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        lock (_gate)
        {
            if (_state != FixtureB1JournalEndpointState.ParentReady ||
                !_parentAcknowledged || _sources.Count != 0 ||
                !ValidWriterSources(sources) ||
                !Observe(() => _authority.IsExactWriterSourceSet(_binding, sources)))
                return Poison(FixtureB1JournalDiagnosticCode.WrongObject);
            foreach (var source in sources)
                _sources.Add(source.ResourceKind, source);
            return true;
        }
    }

    internal FixtureB1JournalTransferDescriptor? CreateReadOffer(
        FixtureB1JournalResourceKind resource,
        Guid operationId,
        Guid successInstanceId)
    {
        lock (_gate)
        {
            if (_state == FixtureB1JournalEndpointState.Poisoned)
                return null;
            if (_sources.Count != ReadOrder.Length ||
                _acknowledgedReads >= ReadOrder.Length ||
                resource != ReadOrder[_acknowledgedReads] || _pinnedOffer is not null ||
                operationId == Guid.Empty || successInstanceId == Guid.Empty ||
                !_operationIds.Add(operationId) || !_successIds.Add(successInstanceId) ||
                !_sources.TryGetValue(resource, out var source))
            {
                Poison(FixtureB1JournalDiagnosticCode.OrderViolation);
                return null;
            }
            _pinnedOffer = new(
                _binding, operationId, successInstanceId,
                2 + (_acknowledgedReads * 2),
                FixtureB1JournalChannelDirection.WriterToOwner, resource,
                _binding.ExactWriter, _binding.WriterProcessLeaseGeneration,
                _binding.ExactOwner, _binding.OwnerProcessLeaseGeneration,
                source.SourceHandleCoordinate, source.SourceHandleLeaseGeneration,
                resource == FixtureB1JournalResourceKind.PrivateRootRead
                    ? FixtureB1JournalAccessPolicy.OwnerRootRead
                    : FixtureB1JournalAccessPolicy.OwnerFileRead);
            return _pinnedOffer;
        }
    }

    internal bool AcceptReadAck(FixtureB1JournalChannelAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        lock (_gate)
        {
            if (_state == FixtureB1JournalEndpointState.Poisoned)
                return false;
            if (_pinnedOffer is null || ack.Binding != _binding ||
                ack.OperationId != _pinnedOffer.OperationId ||
                ack.SuccessInstanceId != _pinnedOffer.SuccessInstanceId ||
                ack.Sequence != _pinnedOffer.Sequence + 1 ||
                ack.ResourceKind != _pinnedOffer.ResourceKind ||
                ack.AdoptingProcess != _binding.ExactOwner ||
                ack.TargetLocalCoordinate <= 0 || ack.TargetResourceReceipt <= 0 ||
                ack.ReceiptId == Guid.Empty || !_receiptIds.Add(ack.ReceiptId))
                return Poison(FixtureB1JournalDiagnosticCode.InvalidTransfer);
            if (!_sources.TryGetValue(_pinnedOffer.ResourceKind, out var source) ||
                source.SourceHandleCoordinate != _pinnedOffer.SourceHandleCoordinate ||
                source.SourceHandleLeaseGeneration !=
                    _pinnedOffer.SourceHandleLeaseGeneration)
                return Poison(FixtureB1JournalDiagnosticCode.SourceLeaseDrift);
            if (!Observe(() => _authority.IsAckAuthenticated(ack)))
                return Poison(FixtureB1JournalDiagnosticCode.AckAuthenticationRejected);
            _pinnedOffer = null;
            _acknowledgedReads++;
            if (_acknowledgedReads == ReadOrder.Length)
                _state = FixtureB1JournalEndpointState.Ready;
            return true;
        }
    }

    internal bool ReportReadAckLost(Guid operationId)
    {
        lock (_gate)
        {
            if (_pinnedOffer is null || _pinnedOffer.OperationId != operationId)
                return Poison(FixtureB1JournalDiagnosticCode.OrderViolation);
            return Poison(FixtureB1JournalDiagnosticCode.AckLost);
        }
    }

    internal bool ObserveSourceLease(
        FixtureB1JournalResourceKind resource,
        long coordinate,
        long leaseGeneration,
        FixtureArtifactIdentity identity)
    {
        lock (_gate)
        {
            if (!_sources.TryGetValue(resource, out var source) ||
                source.SourceHandleCoordinate != coordinate ||
                source.SourceHandleLeaseGeneration != leaseGeneration ||
                source.Observation.Identity != identity)
                return Poison(FixtureB1JournalDiagnosticCode.SourceLeaseDrift);
            return true;
        }
    }

    internal bool RequestSourceClose(FixtureB1JournalResourceKind resource)
    {
        lock (_gate)
        {
            if (_pinnedOffer?.ResourceKind == resource)
                return Poison(FixtureB1JournalDiagnosticCode.SourceCloseBarrier);
            return Poison(FixtureB1JournalDiagnosticCode.SourceLeaseDrift);
        }
    }

    internal bool AcceptWriteRelease(FixtureB1JournalWriteRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        lock (_gate)
        {
            if (_state == FixtureB1JournalEndpointState.Poisoned)
                return false;
            if (_releaseAttempted)
                return Poison(FixtureB1JournalDiagnosticCode.ReleaseReplay);
            _releaseAttempted = true;
            if (_state != FixtureB1JournalEndpointState.Ready ||
                _acknowledgedReads != ReadOrder.Length || release.Binding != _binding ||
                release.ReceiptId == Guid.Empty || !_receiptIds.Add(release.ReceiptId) ||
                !ExactSources(release.Sources))
                return Poison(FixtureB1JournalDiagnosticCode.OrderViolation);
            if (!Observe(() => _authority.IsWriteReleaseAuthenticated(release)))
                return Poison(FixtureB1JournalDiagnosticCode.ReleaseAuthenticationRejected);
            if (!Observe(() => _authority.IsOwnerReadCustodyLive(
                    _binding, release.Sources)))
                return Poison(FixtureB1JournalDiagnosticCode.OwnerCustodyLost);
            if (!Observe(() => _authority.IsWriteBoundaryLiveAndOwned(
                    _binding, release.Sources)))
                return Poison(FixtureB1JournalDiagnosticCode.ReleaseAuthenticationRejected);
            _acceptedRelease = release;
            _state = FixtureB1JournalEndpointState.WritableIdle;
            return true;
        }
    }

    internal bool TryStartWrite(Guid operationId)
    {
        lock (_gate)
        {
            if (_state != FixtureB1JournalEndpointState.WritableIdle ||
                operationId == Guid.Empty || _writeOperation is not null ||
                _acceptedRelease is null)
                return Poison(FixtureB1JournalDiagnosticCode.PrematureWrite);
            if (!Observe(() => _authority.IsOwnerReadCustodyLive(
                    _binding, _acceptedRelease.Sources)))
                return Poison(FixtureB1JournalDiagnosticCode.OwnerCustodyLost);
            if (!Observe(() => _authority.IsWriteBoundaryLiveAndOwned(
                    _binding, _acceptedRelease.Sources)))
                return Poison(FixtureB1JournalDiagnosticCode.ReleaseAuthenticationRejected);
            _writeOperation = operationId;
            return true;
        }
    }

    internal bool CompleteWrite(Guid operationId)
    {
        lock (_gate)
        {
            if (_writeOperation != operationId)
                return Poison(FixtureB1JournalDiagnosticCode.OrderViolation);
            _writeOperation = null;
            return _state == FixtureB1JournalEndpointState.WritableIdle;
        }
    }

    internal bool ObserveStop()
    {
        lock (_gate)
            return Poison(FixtureB1JournalDiagnosticCode.StopObserved);
    }

    internal bool ObserveOwnerDeath()
    {
        lock (_gate)
        {
            if (!Observe(() => _authority.IsExactProcessTerminated(
                    _binding.ExactOwner)))
                return Poison(FixtureB1JournalDiagnosticCode.WrongObject);
            return Poison(FixtureB1JournalDiagnosticCode.OwnerDied);
        }
    }

    internal FixtureB1JournalWriterSnapshot Snapshot()
    {
        lock (_gate)
            return new(
                _state, _diagnostic, _parentAcknowledged, _sources.Count,
                _acknowledgedReads, _writeOperation is not null,
                _parentAcknowledged && _state != FixtureB1JournalEndpointState.Poisoned,
                _state == FixtureB1JournalEndpointState.WritableIdle,
                _rejectedParentObligations.Count(pair =>
                    !_confirmedClosedRejectedParentObligations.Contains(pair.Key)),
                _unconfirmedParentSuccesses,
                _uncertainRejectedParentCloseObligations.Count,
                new ReadOnlyCollection<FixtureB1JournalCleanupHandleKey>(
                    [.. _rejectedParentObligations.Keys.Where(key =>
                        !_confirmedClosedRejectedParentObligations.Contains(key))]));
    }

    private void CaptureParentSuccess(FixtureB1JournalTransferSuccess success)
    {
        if (!_reportedParentSuccesses.Add(success))
            return;
        if (!TryExpectedParentCleanupKey(success, out _))
            _unconfirmedParentSuccesses++;
    }

    private void MarkRejectedParent(FixtureB1JournalTransferSuccess success)
    {
        if (TryExpectedParentCleanupKey(success, out var key))
            _rejectedParentObligations.TryAdd(key, success);
    }

    private void RejectParent(
        FixtureB1JournalTransferSuccess success,
        FixtureB1JournalDiagnosticCode diagnostic)
    {
        MarkRejectedParent(success);
        Poison(diagnostic);
    }

    private bool TryExpectedParentCleanupKey(
        FixtureB1JournalTransferSuccess success,
        out FixtureB1JournalCleanupHandleKey key)
    {
        if (success.Transfer.Binding == _binding &&
            success.Transfer.OperationId != Guid.Empty &&
            success.Transfer.SuccessInstanceId != Guid.Empty &&
            success.Transfer.Direction ==
                FixtureB1JournalChannelDirection.OwnerToWriter &&
            success.Transfer.ResourceKind == FixtureB1JournalResourceKind.TrustedParent &&
            success.Transfer.Target == _binding.ExactWriter &&
            success.Transfer.TargetProcessLeaseGeneration ==
                _binding.WriterProcessLeaseGeneration &&
            success.TargetLocalCoordinate > 0 && success.TargetResourceReceipt > 0)
        {
            key = new(_binding.ExactWriter, success.TargetLocalCoordinate,
                success.TargetResourceReceipt);
            return true;
        }
        key = default;
        return false;
    }

    private bool TryRejectedParentCleanupKey(
        FixtureB1JournalTransferSuccess success,
        out FixtureB1JournalCleanupHandleKey key)
    {
        foreach (var pair in _rejectedParentObligations)
        {
            if (pair.Value == success)
            {
                key = pair.Key;
                return true;
            }
        }
        key = default;
        return false;
    }

    private bool ValidParentTransfer(FixtureB1JournalTransferDescriptor transfer) =>
        transfer.Binding == _binding && transfer.OperationId != Guid.Empty &&
        transfer.SuccessInstanceId != Guid.Empty && transfer.Sequence == 0 &&
        transfer.Direction == FixtureB1JournalChannelDirection.OwnerToWriter &&
        transfer.ResourceKind == FixtureB1JournalResourceKind.TrustedParent &&
        transfer.Source == _binding.ExactOwner &&
        transfer.SourceProcessLeaseGeneration == _binding.OwnerProcessLeaseGeneration &&
        transfer.Target == _binding.ExactWriter &&
        transfer.TargetProcessLeaseGeneration == _binding.WriterProcessLeaseGeneration &&
        transfer.SourceHandleCoordinate > 0 &&
        transfer.SourceHandleLeaseGeneration == _binding.TrustedParentLeaseGeneration &&
        transfer.IntendedAccess == FixtureB1JournalAccessPolicy.WriterParent &&
        Observe(() => _authority.IsTransferAuthenticated(transfer));

    private bool ValidWriterSources(IReadOnlyList<FixtureB1JournalSourceLease> sources)
    {
        if (sources.Count != ReadOrder.Length ||
            sources.Select(source => source.ResourceKind).Distinct().Count() !=
                ReadOrder.Length ||
            sources.Select(source => source.SourceHandleCoordinate).Distinct().Count() !=
                ReadOrder.Length ||
            sources.Select(source => source.ResourceReceipt).Distinct().Count() !=
                ReadOrder.Length ||
            sources.Select(source => source.Observation.Identity).Distinct().Count() !=
                ReadOrder.Length)
            return false;

        var root = sources.SingleOrDefault(source =>
            source.ResourceKind == FixtureB1JournalResourceKind.PrivateRootRead);
        var frames = sources.SingleOrDefault(source =>
            source.ResourceKind == FixtureB1JournalResourceKind.FramesRead);
        var anchor = sources.SingleOrDefault(source =>
            source.ResourceKind == FixtureB1JournalResourceKind.AnchorRead);
        if (root is null || frames is null || anchor is null)
            return false;
        return ValidSource(root, FixtureArtifactKind.Directory,
                _binding.TrustedParentIdentity, _binding.PrivateRootName) &&
            ValidSource(frames, FixtureArtifactKind.File,
                root.Observation.Identity, _binding.FramesName) &&
            ValidSource(anchor, FixtureArtifactKind.File,
                root.Observation.Identity, _binding.AnchorName);
    }

    private static bool ValidSource(
        FixtureB1JournalSourceLease source,
        FixtureArtifactKind kind,
        FixtureArtifactIdentity parent,
        string name) =>
        source.SourceHandleCoordinate > 0 &&
        source.SourceHandleLeaseGeneration > 0 && source.ResourceReceipt > 0 &&
        source.Observation.Identity.Kind == kind &&
        !source.Observation.Identity.IsReparse &&
        source.Observation.ParentIdentity == parent &&
        StringComparer.Ordinal.Equals(source.Observation.Name, name) &&
        source.Observation.NonInheritable && source.Observation.SecurityMatches &&
        !source.Observation.DeletePending &&
        !source.Observation.HasUnexpectedStreams &&
        (kind != FixtureArtifactKind.File || source.Observation.NumberOfLinks == 1);

    private bool ExactSources(IReadOnlyList<FixtureB1JournalSourceLease> sources)
    {
        if (sources.Count != _sources.Count)
            return false;
        foreach (var pair in _sources)
        {
            var source = sources.SingleOrDefault(item => item.ResourceKind == pair.Key);
            if (source != pair.Value)
                return false;
        }
        return true;
    }

    private bool Poison(FixtureB1JournalDiagnosticCode diagnostic)
    {
        _state = FixtureB1JournalEndpointState.Poisoned;
        if (_diagnostic == FixtureB1JournalDiagnosticCode.None)
            _diagnostic = diagnostic;
        return false;
    }

    private static bool Observe(Func<bool> observation)
    {
        try { return observation(); }
        catch { return false; }
    }
}

internal static class FixtureB1JournalChannelValidation
{
    internal static bool ValidBinding(FixtureB1JournalChannelBinding binding) =>
        binding.ProtocolVersion > 0 && binding.RunNonce != Guid.Empty &&
        binding.LaunchId != Guid.Empty && binding.WrapperInstance != Guid.Empty &&
        binding.RunNonce != binding.LaunchId &&
        binding.ExactOwner.IsValid && binding.ExactWriter.IsValid &&
        binding.ExactOwner != binding.ExactWriter &&
        binding.SessionEpoch > 0 && binding.ConnectionGeneration > 0 &&
        binding.OwnerProcessLeaseGeneration > 0 &&
        binding.WriterProcessLeaseGeneration > 0 &&
        binding.TrustedParentLeaseGeneration > 0 &&
        binding.TrustedParentIdentity.Kind == FixtureArtifactKind.Directory &&
        !binding.TrustedParentIdentity.IsReparse &&
        !string.IsNullOrWhiteSpace(binding.TrustedParentFinalPath) &&
        ValidComponent(binding.PrivateRootName) &&
        ValidComponent(binding.FramesName) && ValidComponent(binding.AnchorName) &&
        !StringComparer.OrdinalIgnoreCase.Equals(
            binding.PrivateRootName, binding.FramesName) &&
        !StringComparer.OrdinalIgnoreCase.Equals(
            binding.PrivateRootName, binding.AnchorName) &&
        !StringComparer.OrdinalIgnoreCase.Equals(binding.FramesName, binding.AnchorName);

    private static bool ValidComponent(string value) =>
        value is { Length: >= 1 and <= 64 } &&
        IsAsciiAlphaNumeric(value[0]) &&
        value.All(character => IsAsciiAlphaNumeric(character) ||
            character is '.' or '_' or '-') &&
        value[^1] is not '.' and not ' ';

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9';
}
