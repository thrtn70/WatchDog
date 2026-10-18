using System.Collections.ObjectModel;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureB1EndpointRole
{
    Wrapper,
    Launcher,
}

internal enum FixtureB1EndpointState
{
    Active,
    Rejected,
}

internal enum FixtureB1EndpointDiagnosticCode
{
    None,
    InvalidBootstrap,
    BootstrapAuthorityRejected,
    InvalidIntent,
    IntentReplay,
    JournalFailure,
    NativeSuccessMismatch,
    SuccessGenerationAlias,
    CoordinateAlias,
    InboundAuthorityRejected,
    AckLost,
    AckReplay,
    AckWithoutSuccess,
    AckAuthorityRejected,
    PeerDrift,
    RecoveryAmbiguous,
    RecoveryCorrupt,
    Stopped,
}

internal enum FixtureB1EndpointJournalEvent
{
    IntentCommitted,
    SuccessObligationCommitted,
    SuccessValidated,
    PeerIntentObserved,
    SourceSuccessObserved,
    InboundCustodyAccepted,
    InboundRejectedRetained,
    AckIssued,
    AckAccepted,
    AckLost,
    Stopped,
    ProtocolRejected,
}

internal enum FixtureB1EndpointSuccessDisposition
{
    Recorded,
    ReplayIgnored,
    ObligationRetainedRejected,
}

internal enum FixtureB1EndpointInboundDisposition
{
    Accepted,
    AlreadyAccepted,
    ProviderRetainsRejectedResource,
}

internal readonly record struct FixtureB1LeaseGeneration(Guid Value)
{
    internal bool IsValid => Value != Guid.Empty;
}

internal readonly record struct FixtureB1SuccessGeneration(Guid Value)
{
    internal bool IsValid => Value != Guid.Empty;
}

internal sealed record FixtureB1EndpointBinding(
    Guid RunNonce,
    Guid LaunchId,
    FixtureHandoffProcessIdentity ExactWrapper,
    FixtureHandoffProcessIdentity ExactLauncher,
    long ConnectionReceipt);

/// <summary>
/// W-owned durable pre-call record. SuccessGeneration and TargetLeaseGeneration are allocated
/// before the native call so a later callback can be distinguished from a second invocation.
/// </summary>
internal sealed record FixtureB1EndpointDuplicateIntent(
    FixtureB1EndpointBinding Binding,
    Guid OperationId,
    FixtureB1SuccessGeneration SuccessGeneration,
    FixtureB1LeaseGeneration SourceLeaseGeneration,
    FixtureB1LeaseGeneration TargetLeaseGeneration,
    FixtureB1OwnerChannelDirection Direction,
    FixtureB1OwnerResourceKind ResourceKind,
    FixtureHandoffProcessIdentity Source,
    FixtureHandoffProcessIdentity Target,
    FixtureHandoffProcessIdentity? ExactRunner,
    long Sequence);

/// <summary>
/// W-owned post-success obligation. It is journaled before validation or channel delivery.
/// The numeric coordinate is evidence scoped by TargetLeaseGeneration, never global authority.
/// </summary>
internal sealed record FixtureB1EndpointSuccessReceipt(
    FixtureB1EndpointBinding Binding,
    Guid OperationId,
    FixtureB1SuccessGeneration SuccessGeneration,
    FixtureB1LeaseGeneration SourceLeaseGeneration,
    FixtureB1LeaseGeneration TargetLeaseGeneration,
    FixtureB1OwnerChannelDirection Direction,
    FixtureB1OwnerResourceKind ResourceKind,
    FixtureHandoffProcessIdentity Source,
    FixtureHandoffProcessIdentity Target,
    FixtureHandoffProcessIdentity? ExactRunner,
    long Sequence,
    long TargetLocalCoordinate,
    long ResourceReceipt,
    bool NonInheritable);

internal sealed record FixtureB1EndpointAck(
    FixtureB1EndpointBinding Binding,
    Guid OperationId,
    FixtureB1SuccessGeneration SuccessGeneration,
    FixtureB1LeaseGeneration SourceLeaseGeneration,
    FixtureB1LeaseGeneration TargetLeaseGeneration,
    FixtureB1OwnerChannelDirection Direction,
    FixtureB1OwnerResourceKind ResourceKind,
    FixtureHandoffProcessIdentity Source,
    FixtureHandoffProcessIdentity Target,
    FixtureHandoffProcessIdentity? ExactRunner,
    long Sequence,
    long TargetLocalCoordinate,
    long ResourceReceipt,
    Guid AckReceiptId);

internal sealed record FixtureB1EndpointJournalRecord(
    FixtureB1EndpointRole Owner,
    long Ordinal,
    FixtureB1EndpointJournalEvent Event,
    FixtureB1EndpointDiagnosticCode DiagnosticCode,
    FixtureB1EndpointDuplicateIntent? Intent,
    FixtureB1EndpointSuccessReceipt? Success,
    FixtureB1EndpointAck? Ack);

/// <summary>
/// Return true only after the endpoint-specific record is durably committed. ReadCommitted
/// returns that endpoint's ordered stream after a simulated or real process restart.
/// </summary>
internal interface IFixtureB1EndpointJournal
{
    IReadOnlyList<FixtureB1EndpointJournalRecord> ReadCommitted(
        FixtureB1EndpointRole owner);
    bool TryAppend(FixtureB1EndpointJournalRecord record);
}

/// <summary>
/// Injected bootstrap, peer, authentication and exact-object authority. The pure model does
/// not prove OS handle identity, effective rights, process liveness, key custody or IPC origin.
/// </summary>
internal interface IFixtureB1EndpointAuthority
{
    bool IsBootstrapAuthenticated(
        FixtureB1EndpointRole endpoint,
        FixtureB1EndpointBinding binding);
    bool IsNativeIntentAuthorized(FixtureB1EndpointDuplicateIntent intent);
    bool IsNativeSuccessExact(
        FixtureB1EndpointDuplicateIntent intent,
        FixtureB1EndpointSuccessReceipt success);
    bool IsPeerIntentAuthenticated(
        FixtureB1EndpointRole endpoint,
        FixtureB1EndpointDuplicateIntent intent);
    bool IsInboundPeerAuthenticated(
        FixtureB1EndpointRole endpoint,
        FixtureB1EndpointSuccessReceipt success);
    bool IsSourceReceiptAuthenticated(
        FixtureB1EndpointRole endpoint,
        FixtureB1EndpointSuccessReceipt success);
    bool IsAckPeerAuthenticated(
        FixtureB1EndpointRole endpoint,
        FixtureB1EndpointAck ack);
}

internal sealed class FixtureB1EndpointSnapshot
{
    internal FixtureB1EndpointSnapshot(
        FixtureB1EndpointRole role,
        FixtureB1EndpointState state,
        FixtureB1EndpointDiagnosticCode diagnosticCode,
        int committedIntentCount,
        int successObligationCount,
        int validatedSuccessCount,
        int unconfirmedSuccessCount,
        int acceptedInboundCount,
        int providerRetainedInboundCount,
        int issuedAckCount,
        int acceptedAckCount,
        IReadOnlyList<FixtureB1EndpointJournalRecord> records)
    {
        Role = role;
        State = state;
        DiagnosticCode = diagnosticCode;
        CommittedIntentCount = committedIntentCount;
        SuccessObligationCount = successObligationCount;
        ValidatedSuccessCount = validatedSuccessCount;
        UnconfirmedSuccessCount = unconfirmedSuccessCount;
        AcceptedInboundCount = acceptedInboundCount;
        ProviderRetainedInboundCount = providerRetainedInboundCount;
        IssuedAckCount = issuedAckCount;
        AcceptedAckCount = acceptedAckCount;
        Records = records;
    }

    internal FixtureB1EndpointRole Role { get; }
    internal FixtureB1EndpointState State { get; }
    internal FixtureB1EndpointDiagnosticCode DiagnosticCode { get; }
    internal int CommittedIntentCount { get; }
    internal int SuccessObligationCount { get; }
    internal int ValidatedSuccessCount { get; }
    internal int UnconfirmedSuccessCount { get; }
    internal int AcceptedInboundCount { get; }
    internal int ProviderRetainedInboundCount { get; }
    internal int IssuedAckCount { get; }
    internal int AcceptedAckCount { get; }
    internal IReadOnlyList<FixtureB1EndpointJournalRecord> Records { get; }
    internal bool DurabilityProven => false;
    internal bool NativeAuthorityProven => false;
}

/// <summary>
/// One endpoint's pure managed state. W and L must use different instances and locks. W is the
/// only native duplicate invoker for both directions; immutable success/ACK values cross the
/// endpoint boundary. Journal and authority implementations remain injected and unqualified.
/// </summary>
internal sealed class FixtureB1EndpointJournalModel
{
    private readonly object _gate = new();
    private readonly FixtureB1EndpointRole _role;
    private readonly FixtureB1EndpointBinding _binding;
    private readonly IFixtureB1EndpointAuthority _authority;
    private readonly IFixtureB1EndpointJournal _journal;
    private readonly List<FixtureB1EndpointJournalRecord> _records = [];
    private readonly Dictionary<Guid, FixtureB1EndpointDuplicateIntent> _intents = [];
    private readonly Dictionary<Guid, FixtureB1EndpointDuplicateIntent> _peerIntents = [];
    private readonly Dictionary<FixtureB1SuccessGeneration,
        FixtureB1EndpointSuccessReceipt> _successes = [];
    private readonly List<FixtureB1EndpointSuccessReceipt> _successObligations = [];
    private readonly HashSet<FixtureB1SuccessGeneration> _validatedSuccesses = [];
    private readonly Dictionary<FixtureB1SuccessGeneration,
        FixtureB1EndpointSuccessReceipt> _sourceObserved = [];
    private readonly Dictionary<FixtureB1SuccessGeneration,
        FixtureB1EndpointSuccessReceipt> _inbound = [];
    private readonly Dictionary<FixtureB1SuccessGeneration,
        FixtureB1EndpointSuccessReceipt> _providerRetainedInbound = [];
    private readonly Dictionary<FixtureB1SuccessGeneration, FixtureB1EndpointAck>
        _issuedAcks = [];
    private readonly HashSet<Guid> _acceptedAckReceipts = [];
    private readonly HashSet<FixtureB1SuccessGeneration> _acceptedAckSuccesses = [];
    private FixtureB1EndpointState _state = FixtureB1EndpointState.Active;
    private FixtureB1EndpointDiagnosticCode _diagnostic;
    private long _nextOrdinal;
    private int _unconfirmedSuccesses;

    internal FixtureB1EndpointJournalModel(
        FixtureB1EndpointRole role,
        FixtureB1EndpointBinding binding,
        IFixtureB1EndpointAuthority authority,
        IFixtureB1EndpointJournal journal)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(journal);
        _role = role;
        _binding = binding;
        _authority = authority;
        _journal = journal;

        if (!ValidBinding(binding))
            SetRejected(FixtureB1EndpointDiagnosticCode.InvalidBootstrap);
        else if (!Observe(() => authority.IsBootstrapAuthenticated(role, binding)))
            SetRejected(FixtureB1EndpointDiagnosticCode.BootstrapAuthorityRejected);
        else
            Recover();
    }

    internal bool ReserveNativeDuplicate(FixtureB1EndpointDuplicateIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        lock (_gate)
        {
            if (_state == FixtureB1EndpointState.Rejected)
                return false;
            if (_role != FixtureB1EndpointRole.Wrapper || !ValidIntent(intent) ||
                !Observe(() => _authority.IsNativeIntentAuthorized(intent)))
                return Reject(FixtureB1EndpointDiagnosticCode.InvalidIntent, intent: intent);
            if (_intents.ContainsKey(intent.OperationId) ||
                _intents.Values.Any(value =>
                    value.SuccessGeneration == intent.SuccessGeneration ||
                    value.TargetLeaseGeneration == intent.TargetLeaseGeneration))
                return Reject(FixtureB1EndpointDiagnosticCode.IntentReplay, intent: intent);

            var record = Record(
                FixtureB1EndpointJournalEvent.IntentCommitted,
                FixtureB1EndpointDiagnosticCode.None,
                intent: intent);
            if (!Persist(record))
                return SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
            _intents.Add(intent.OperationId, intent);
            return true;
        }
    }

    internal FixtureB1EndpointSuccessDisposition RecordNativeSuccess(
        FixtureB1EndpointSuccessReceipt success)
    {
        ArgumentNullException.ThrowIfNull(success);
        lock (_gate)
        {
            if (_successObligations.Contains(success))
                return _validatedSuccesses.Contains(success.SuccessGeneration)
                    ? FixtureB1EndpointSuccessDisposition.ReplayIgnored
                    : FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected;
            var generationAlias = success.SuccessGeneration.IsValid &&
                _successes.ContainsKey(success.SuccessGeneration);

            // Native success already exists. Commit its close obligation before validation,
            // endpoint state checks, channel work, or callback dispatch.
            var obligation = Record(
                FixtureB1EndpointJournalEvent.SuccessObligationCommitted,
                FixtureB1EndpointDiagnosticCode.None,
                success: success);
            if (!Persist(obligation))
            {
                _unconfirmedSuccesses++;
                SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
                return FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected;
            }
            _successObligations.Add(success);
            if (success.SuccessGeneration.IsValid && !generationAlias)
                _successes.Add(success.SuccessGeneration, success);

            if (!success.SuccessGeneration.IsValid)
            {
                _unconfirmedSuccesses++;
                Reject(FixtureB1EndpointDiagnosticCode.NativeSuccessMismatch,
                    success: success);
                return FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected;
            }
            if (_state == FixtureB1EndpointState.Rejected)
                return FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected;
            if (generationAlias)
            {
                Reject(FixtureB1EndpointDiagnosticCode.SuccessGenerationAlias,
                    success: success);
                return FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected;
            }
            if (_role != FixtureB1EndpointRole.Wrapper ||
                !_intents.TryGetValue(success.OperationId, out var intent) ||
                !SuccessMatchesIntent(success, intent) ||
                !Observe(() => _authority.IsNativeSuccessExact(intent, success)))
            {
                Reject(FixtureB1EndpointDiagnosticCode.NativeSuccessMismatch,
                    success: success);
                return FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected;
            }
            if (_successObligations.Any(value =>
                    value != success &&
                    value.Target == success.Target &&
                    value.TargetLocalCoordinate == success.TargetLocalCoordinate))
            {
                Reject(FixtureB1EndpointDiagnosticCode.CoordinateAlias,
                    success: success);
                return FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected;
            }
            var validated = Record(
                FixtureB1EndpointJournalEvent.SuccessValidated,
                FixtureB1EndpointDiagnosticCode.None,
                success: success);
            if (!Persist(validated))
            {
                SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
                return FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected;
            }
            _validatedSuccesses.Add(success.SuccessGeneration);
            return FixtureB1EndpointSuccessDisposition.Recorded;
        }
    }

    internal bool ObservePeerIntent(FixtureB1EndpointDuplicateIntent intent)
    {
        ArgumentNullException.ThrowIfNull(intent);
        lock (_gate)
        {
            if (_state == FixtureB1EndpointState.Rejected)
                return false;
            if (_role != FixtureB1EndpointRole.Launcher || !ValidIntent(intent) ||
                !Observe(() => _authority.IsPeerIntentAuthenticated(_role, intent)))
                return Reject(FixtureB1EndpointDiagnosticCode.PeerDrift,
                    intent: intent);
            if (_peerIntents.TryGetValue(intent.OperationId, out var prior))
                return prior == intent || Reject(
                    FixtureB1EndpointDiagnosticCode.IntentReplay,
                    intent: intent);
            if (_peerIntents.Values.Any(value =>
                    value.SuccessGeneration == intent.SuccessGeneration ||
                    value.TargetLeaseGeneration == intent.TargetLeaseGeneration))
                return Reject(FixtureB1EndpointDiagnosticCode.IntentReplay,
                    intent: intent);
            if (!Persist(Record(
                    FixtureB1EndpointJournalEvent.PeerIntentObserved,
                    FixtureB1EndpointDiagnosticCode.None,
                    intent: intent)))
                return SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
            _peerIntents.Add(intent.OperationId, intent);
            return true;
        }
    }

    internal bool ObserveSourceSuccess(FixtureB1EndpointSuccessReceipt success)
    {
        ArgumentNullException.ThrowIfNull(success);
        lock (_gate)
        {
            if (_state == FixtureB1EndpointState.Rejected)
                return false;
            if (_role != FixtureB1EndpointRole.Launcher ||
                success.ResourceKind != FixtureB1OwnerResourceKind.RunnerProcess ||
                !ValidSuccessShape(success) || success.Source != LocalIdentity() ||
                !SuccessMatchesKnownIntent(success) ||
                !Observe(() => _authority.IsSourceReceiptAuthenticated(_role, success)))
                return Reject(FixtureB1EndpointDiagnosticCode.PeerDrift,
                    success: success);
            if (_sourceObserved.TryGetValue(success.SuccessGeneration, out var prior))
                return prior == success || Reject(
                    FixtureB1EndpointDiagnosticCode.SuccessGenerationAlias,
                    success: success);
            if (!Persist(Record(
                    FixtureB1EndpointJournalEvent.SourceSuccessObserved,
                    FixtureB1EndpointDiagnosticCode.None,
                    success: success)))
                return SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
            _sourceObserved.Add(success.SuccessGeneration, success);
            return true;
        }
    }

    internal FixtureB1EndpointInboundDisposition AcceptInboundSuccess(
        FixtureB1EndpointSuccessReceipt success)
    {
        ArgumentNullException.ThrowIfNull(success);
        lock (_gate)
        {
            if (_inbound.TryGetValue(success.SuccessGeneration, out var prior) &&
                prior == success)
                return FixtureB1EndpointInboundDisposition.AlreadyAccepted;

            var invalid = _state == FixtureB1EndpointState.Rejected ||
                !SuccessTargetsThisEndpoint(success) ||
                !SuccessMatchesKnownIntent(success) ||
                !IsWrapperProcessCorrelated(success) ||
                !Observe(() => _authority.IsInboundPeerAuthenticated(_role, success));
            var alias = _inbound.Values.Any(value =>
                value.TargetLocalCoordinate == success.TargetLocalCoordinate &&
                value.SuccessGeneration != success.SuccessGeneration);
            if (invalid || alias ||
                _inbound.ContainsKey(success.SuccessGeneration))
            {
                var rejection = Record(
                    FixtureB1EndpointJournalEvent.InboundRejectedRetained,
                    alias
                        ? FixtureB1EndpointDiagnosticCode.CoordinateAlias
                        : FixtureB1EndpointDiagnosticCode.InboundAuthorityRejected,
                    success: success);
                if (!Persist(rejection))
                    SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
                else
                {
                    _providerRetainedInbound.TryAdd(success.SuccessGeneration, success);
                    SetRejected(alias
                        ? FixtureB1EndpointDiagnosticCode.CoordinateAlias
                        : FixtureB1EndpointDiagnosticCode.InboundAuthorityRejected);
                }
                return FixtureB1EndpointInboundDisposition.ProviderRetainsRejectedResource;
            }

            var accepted = Record(
                FixtureB1EndpointJournalEvent.InboundCustodyAccepted,
                FixtureB1EndpointDiagnosticCode.None,
                success: success);
            if (!Persist(accepted))
            {
                _providerRetainedInbound.TryAdd(success.SuccessGeneration, success);
                SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
                return FixtureB1EndpointInboundDisposition.ProviderRetainsRejectedResource;
            }
            _inbound.Add(success.SuccessGeneration, success);
            return FixtureB1EndpointInboundDisposition.Accepted;
        }
    }

    internal FixtureB1EndpointAck? CreateAck(
        FixtureB1SuccessGeneration successGeneration,
        Guid ackReceiptId)
    {
        lock (_gate)
        {
            if (_state == FixtureB1EndpointState.Rejected ||
                ackReceiptId == Guid.Empty ||
                !_inbound.TryGetValue(successGeneration, out var success))
                return null;
            if (_issuedAcks.ContainsKey(successGeneration) ||
                _issuedAcks.Values.Any(value => value.AckReceiptId == ackReceiptId))
            {
                Reject(FixtureB1EndpointDiagnosticCode.AckReplay, success: success);
                return null;
            }

            var ack = new FixtureB1EndpointAck(
                success.Binding,
                success.OperationId,
                success.SuccessGeneration,
                success.SourceLeaseGeneration,
                success.TargetLeaseGeneration,
                success.Direction,
                success.ResourceKind,
                success.Source,
                success.Target,
                success.ExactRunner,
                success.Sequence + 1,
                success.TargetLocalCoordinate,
                success.ResourceReceipt,
                ackReceiptId);
            if (!Persist(Record(
                    FixtureB1EndpointJournalEvent.AckIssued,
                    FixtureB1EndpointDiagnosticCode.None,
                    success: success,
                    ack: ack)))
            {
                SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
                return null;
            }
            _issuedAcks.Add(successGeneration, ack);
            return ack;
        }
    }

    internal bool AcceptAck(FixtureB1EndpointAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        lock (_gate)
        {
            if (_acceptedAckReceipts.Contains(ack.AckReceiptId) ||
                _acceptedAckSuccesses.Contains(ack.SuccessGeneration))
                return Reject(FixtureB1EndpointDiagnosticCode.AckReplay, ack: ack);
            if (_state == FixtureB1EndpointState.Rejected)
                return false;
            if (!AckReturnsToThisEndpoint(ack))
                return Reject(FixtureB1EndpointDiagnosticCode.PeerDrift, ack: ack);
            if (!TryGetExpectedAckSuccess(ack, out var expected) ||
                !AckMatchesSuccess(ack, expected))
                return Reject(FixtureB1EndpointDiagnosticCode.AckWithoutSuccess,
                    ack: ack);
            if (!Observe(() => _authority.IsAckPeerAuthenticated(_role, ack)))
                return Reject(FixtureB1EndpointDiagnosticCode.AckAuthorityRejected,
                    ack: ack);

            if (!Persist(Record(
                    FixtureB1EndpointJournalEvent.AckAccepted,
                    FixtureB1EndpointDiagnosticCode.None,
                    ack: ack)))
                return SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
            _acceptedAckReceipts.Add(ack.AckReceiptId);
            _acceptedAckSuccesses.Add(ack.SuccessGeneration);
            return true;
        }
    }

    internal bool ReportAckLost(FixtureB1EndpointSuccessReceipt success)
    {
        ArgumentNullException.ThrowIfNull(success);
        lock (_gate)
        {
            if (_state == FixtureB1EndpointState.Rejected)
                return false;
            var hasExpected =
                (_successes.TryGetValue(
                     success.SuccessGeneration, out var nativeSuccess) &&
                 _validatedSuccesses.Contains(success.SuccessGeneration) &&
                 nativeSuccess == success) ||
                (_sourceObserved.TryGetValue(
                     success.SuccessGeneration, out var observedSuccess) &&
                 observedSuccess == success);
            if (success.Binding != _binding || success.Source != LocalIdentity() ||
                !hasExpected)
                return Reject(FixtureB1EndpointDiagnosticCode.PeerDrift,
                    success: success);
            if (!Persist(Record(
                    FixtureB1EndpointJournalEvent.AckLost,
                    FixtureB1EndpointDiagnosticCode.AckLost,
                    success: success)))
                return SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
            SetRejected(FixtureB1EndpointDiagnosticCode.AckLost);
            return true;
        }
    }

    internal bool Stop()
    {
        lock (_gate)
        {
            if (_state == FixtureB1EndpointState.Rejected)
                return false;
            if (!Persist(Record(
                    FixtureB1EndpointJournalEvent.Stopped,
                    FixtureB1EndpointDiagnosticCode.Stopped)))
                return SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
            SetRejected(FixtureB1EndpointDiagnosticCode.Stopped);
            return true;
        }
    }

    internal FixtureB1EndpointSnapshot Snapshot()
    {
        lock (_gate)
            return new(
                _role,
                _state,
                _diagnostic,
                _intents.Count,
                _successObligations.Count,
                _validatedSuccesses.Count,
                _unconfirmedSuccesses,
                _inbound.Count,
                _providerRetainedInbound.Count,
                _issuedAcks.Count,
                _acceptedAckReceipts.Count,
                new ReadOnlyCollection<FixtureB1EndpointJournalRecord>([.. _records]));
    }

    private void Recover()
    {
        IReadOnlyList<FixtureB1EndpointJournalRecord> committed;
        try { committed = _journal.ReadCommitted(_role); }
        catch
        {
            SetRejected(FixtureB1EndpointDiagnosticCode.JournalFailure);
            return;
        }

        foreach (var record in committed)
        {
            if (record.Owner != _role || record.Ordinal != _nextOrdinal + 1)
            {
                SetRejected(FixtureB1EndpointDiagnosticCode.RecoveryCorrupt);
                return;
            }
            _nextOrdinal = record.Ordinal;
            _records.Add(record);
            if (record.Event ==
                FixtureB1EndpointJournalEvent.SuccessObligationCommitted)
            {
                if (!RecoverSuccessObligation(record))
                    return;
                continue;
            }
            if (!JournalPayloadBound(record))
            {
                SetRejected(FixtureB1EndpointDiagnosticCode.RecoveryCorrupt);
                return;
            }
            switch (record.Event)
            {
                case FixtureB1EndpointJournalEvent.IntentCommitted
                    when record.Intent is not null && record.Success is null &&
                         record.Ack is null && _role == FixtureB1EndpointRole.Wrapper &&
                         ValidIntent(record.Intent) &&
                         !_intents.ContainsKey(record.Intent.OperationId) &&
                         !_intents.Values.Any(value =>
                             value.SuccessGeneration == record.Intent.SuccessGeneration ||
                             value.TargetLeaseGeneration ==
                                record.Intent.TargetLeaseGeneration):
                    _intents.Add(record.Intent.OperationId, record.Intent);
                    break;
                case FixtureB1EndpointJournalEvent.SuccessValidated
                    when record.Intent is null && record.Success is not null &&
                         record.Ack is null && _role == FixtureB1EndpointRole.Wrapper &&
                         _successes.TryGetValue(
                             record.Success.SuccessGeneration, out var obligated) &&
                         obligated == record.Success &&
                         _intents.TryGetValue(
                             record.Success.OperationId, out var intent) &&
                         SuccessMatchesIntent(record.Success, intent) &&
                         Observe(() => _authority.IsNativeSuccessExact(
                             intent, record.Success)) &&
                         _validatedSuccesses.Add(record.Success.SuccessGeneration):
                    break;
                case FixtureB1EndpointJournalEvent.PeerIntentObserved
                    when record.Intent is not null && record.Success is null &&
                         record.Ack is null &&
                         _role == FixtureB1EndpointRole.Launcher &&
                         ValidIntent(record.Intent) &&
                         Observe(() => _authority.IsPeerIntentAuthenticated(
                             _role, record.Intent)) &&
                         !_peerIntents.ContainsKey(record.Intent.OperationId) &&
                         !_peerIntents.Values.Any(value =>
                             value.SuccessGeneration ==
                                record.Intent.SuccessGeneration ||
                             value.TargetLeaseGeneration ==
                                record.Intent.TargetLeaseGeneration):
                    _peerIntents.Add(record.Intent.OperationId, record.Intent);
                    break;
                case FixtureB1EndpointJournalEvent.SourceSuccessObserved
                    when record.Intent is null && record.Success is not null &&
                         record.Ack is null &&
                         _role == FixtureB1EndpointRole.Launcher &&
                         record.Success.ResourceKind ==
                            FixtureB1OwnerResourceKind.RunnerProcess &&
                         ValidSuccessShape(record.Success) &&
                         record.Success.Source == LocalIdentity() &&
                         SuccessMatchesKnownIntent(record.Success) &&
                         Observe(() => _authority.IsSourceReceiptAuthenticated(
                             _role, record.Success)) &&
                         _sourceObserved.TryAdd(
                             record.Success.SuccessGeneration, record.Success):
                    break;
                case FixtureB1EndpointJournalEvent.InboundCustodyAccepted
                    when record.Intent is null && record.Success is not null &&
                         record.Ack is null && SuccessTargetsThisEndpoint(record.Success) &&
                         SuccessMatchesKnownIntent(record.Success) &&
                         IsWrapperProcessCorrelated(record.Success) &&
                         Observe(() => _authority.IsInboundPeerAuthenticated(
                             _role, record.Success)) &&
                         _inbound.TryAdd(
                             record.Success.SuccessGeneration, record.Success):
                    break;
                case FixtureB1EndpointJournalEvent.InboundRejectedRetained
                    when record.Intent is null && record.Success is not null &&
                         record.Ack is null && ValidSuccessShape(record.Success) &&
                         _providerRetainedInbound.TryAdd(
                             record.Success.SuccessGeneration, record.Success):
                    SetRejected(record.DiagnosticCode ==
                        FixtureB1EndpointDiagnosticCode.None
                            ? FixtureB1EndpointDiagnosticCode.InboundAuthorityRejected
                            : record.DiagnosticCode);
                    break;
                case FixtureB1EndpointJournalEvent.AckIssued
                    when record.Intent is null && record.Success is not null &&
                         record.Ack is not null &&
                         _inbound.TryGetValue(
                             record.Ack.SuccessGeneration, out var inbound) &&
                         inbound == record.Success &&
                         AckMatchesSuccess(record.Ack, inbound) &&
                         _issuedAcks.TryAdd(
                             record.Ack.SuccessGeneration, record.Ack):
                    break;
                case FixtureB1EndpointJournalEvent.AckAccepted
                    when record.Intent is null && record.Success is null &&
                         record.Ack is not null && AckReturnsToThisEndpoint(record.Ack) &&
                         TryGetExpectedAckSuccess(record.Ack, out var expected) &&
                         AckMatchesSuccess(record.Ack, expected) &&
                         Observe(() => _authority.IsAckPeerAuthenticated(
                             _role, record.Ack)) &&
                         _acceptedAckReceipts.Add(record.Ack.AckReceiptId) &&
                         _acceptedAckSuccesses.Add(record.Ack.SuccessGeneration):
                    break;
                case FixtureB1EndpointJournalEvent.AckLost
                    when record.Intent is null && record.Success is not null &&
                         record.Ack is null && ValidSuccessShape(record.Success):
                case FixtureB1EndpointJournalEvent.Stopped
                    when record.Intent is null && record.Success is null &&
                         record.Ack is null:
                case FixtureB1EndpointJournalEvent.ProtocolRejected:
                    SetRejected(record.DiagnosticCode);
                    break;
                default:
                    SetRejected(FixtureB1EndpointDiagnosticCode.RecoveryCorrupt);
                    return;
            }
        }

        if (_state == FixtureB1EndpointState.Active &&
            (_intents.Values.Any(intent =>
                !_validatedSuccesses.Any(generation =>
                    _successes.TryGetValue(generation, out var success) &&
                    success.OperationId == intent.OperationId)) ||
             _successes.Keys.Any(generation =>
                 !_validatedSuccesses.Contains(generation))))
            SetRejected(FixtureB1EndpointDiagnosticCode.RecoveryAmbiguous);
    }

    private bool RecoverSuccessObligation(
        FixtureB1EndpointJournalRecord record)
    {
        if (_role != FixtureB1EndpointRole.Wrapper || record.Success is null)
        {
            SetRejected(FixtureB1EndpointDiagnosticCode.RecoveryCorrupt);
            return false;
        }

        // A committed post-native-call record is cleanup inventory even when its
        // protocol fields are malformed. Retain it before semantic rejection.
        _successObligations.Add(record.Success);
        if (record.Intent is not null || record.Ack is not null)
        {
            SetRejected(FixtureB1EndpointDiagnosticCode.RecoveryCorrupt);
            return false;
        }
        if (!ValidSuccessShape(record.Success))
        {
            _unconfirmedSuccesses++;
            SetRejected(FixtureB1EndpointDiagnosticCode.NativeSuccessMismatch);
            return true;
        }
        if (_successes.TryGetValue(
                record.Success.SuccessGeneration, out var priorSuccess))
        {
            if (priorSuccess == record.Success)
            {
                SetRejected(FixtureB1EndpointDiagnosticCode.RecoveryCorrupt);
                return false;
            }
            SetRejected(FixtureB1EndpointDiagnosticCode.SuccessGenerationAlias);
        }
        else
            _successes.Add(record.Success.SuccessGeneration, record.Success);
        return true;
    }

    private bool ValidIntent(FixtureB1EndpointDuplicateIntent intent)
    {
        if (intent.Binding != _binding || intent.OperationId == Guid.Empty ||
            !intent.SuccessGeneration.IsValid ||
            !intent.SourceLeaseGeneration.IsValid ||
            !intent.TargetLeaseGeneration.IsValid ||
            intent.SourceLeaseGeneration == intent.TargetLeaseGeneration)
            return false;

        return intent.ResourceKind switch
        {
            FixtureB1OwnerResourceKind.RunnerJob =>
                intent.Direction == FixtureB1OwnerChannelDirection.WrapperToLauncher &&
                intent.Source == _binding.ExactWrapper &&
                intent.Target == _binding.ExactLauncher &&
                intent.ExactRunner is null && intent.Sequence == 0,
            FixtureB1OwnerResourceKind.RunnerProcess =>
                intent.Direction == FixtureB1OwnerChannelDirection.LauncherToWrapper &&
                intent.Source == _binding.ExactLauncher &&
                intent.Target == _binding.ExactWrapper &&
                intent.ExactRunner is { IsValid: true } &&
                intent.ExactRunner != _binding.ExactWrapper &&
                intent.ExactRunner != _binding.ExactLauncher && intent.Sequence == 2,
            _ => false,
        };
    }

    private static bool SuccessMatchesIntent(
        FixtureB1EndpointSuccessReceipt success,
        FixtureB1EndpointDuplicateIntent intent) =>
        success.Binding == intent.Binding &&
        success.OperationId == intent.OperationId &&
        success.SuccessGeneration == intent.SuccessGeneration &&
        success.SourceLeaseGeneration == intent.SourceLeaseGeneration &&
        success.TargetLeaseGeneration == intent.TargetLeaseGeneration &&
        success.Direction == intent.Direction &&
        success.ResourceKind == intent.ResourceKind &&
        success.Source == intent.Source && success.Target == intent.Target &&
        success.ExactRunner == intent.ExactRunner &&
        success.Sequence == intent.Sequence &&
        success.TargetLocalCoordinate > 0 && success.ResourceReceipt > 0 &&
        success.NonInheritable;

    private bool ValidSuccessShape(FixtureB1EndpointSuccessReceipt success)
    {
        if (success.Binding != _binding || success.OperationId == Guid.Empty ||
            !success.SuccessGeneration.IsValid ||
            !success.SourceLeaseGeneration.IsValid ||
            !success.TargetLeaseGeneration.IsValid ||
            success.SourceLeaseGeneration == success.TargetLeaseGeneration ||
            success.TargetLocalCoordinate <= 0 || success.ResourceReceipt <= 0 ||
            !success.NonInheritable)
            return false;

        return success.ResourceKind switch
        {
            FixtureB1OwnerResourceKind.RunnerJob =>
                success.Direction ==
                    FixtureB1OwnerChannelDirection.WrapperToLauncher &&
                success.Source == _binding.ExactWrapper &&
                success.Target == _binding.ExactLauncher &&
                success.ExactRunner is null && success.Sequence == 0,
            FixtureB1OwnerResourceKind.RunnerProcess =>
                success.Direction ==
                    FixtureB1OwnerChannelDirection.LauncherToWrapper &&
                success.Source == _binding.ExactLauncher &&
                success.Target == _binding.ExactWrapper &&
                success.ExactRunner is { IsValid: true } &&
                success.ExactRunner != _binding.ExactWrapper &&
                success.ExactRunner != _binding.ExactLauncher &&
                success.Sequence == 2,
            _ => false,
        };
    }

    private bool SuccessTargetsThisEndpoint(FixtureB1EndpointSuccessReceipt success) =>
        ValidSuccessShape(success) && success.Target == LocalIdentity() &&
        success.Source == PeerIdentity();

    private bool SuccessMatchesKnownIntent(
        FixtureB1EndpointSuccessReceipt success)
    {
        if (_intents.TryGetValue(success.OperationId, out var localIntent))
            return SuccessMatchesIntent(success, localIntent);
        return _peerIntents.TryGetValue(success.OperationId, out var peerIntent) &&
            SuccessMatchesIntent(success, peerIntent);
    }

    private bool IsWrapperProcessCorrelated(FixtureB1EndpointSuccessReceipt success) =>
        _role != FixtureB1EndpointRole.Wrapper ||
        success.ResourceKind != FixtureB1OwnerResourceKind.RunnerProcess ||
        (_successes.TryGetValue(success.SuccessGeneration, out var localSuccess) &&
         localSuccess == success &&
         _validatedSuccesses.Contains(success.SuccessGeneration));

    private bool AckReturnsToThisEndpoint(FixtureB1EndpointAck ack) =>
        ack.Binding == _binding && ack.Source == LocalIdentity() &&
        ack.Target == PeerIdentity() && ack.OperationId != Guid.Empty &&
        ack.SuccessGeneration.IsValid && ack.SourceLeaseGeneration.IsValid &&
        ack.TargetLeaseGeneration.IsValid && ack.AckReceiptId != Guid.Empty &&
        ack.TargetLocalCoordinate > 0 && ack.ResourceReceipt > 0 &&
        ack.Sequence == (ack.ResourceKind == FixtureB1OwnerResourceKind.RunnerJob ? 1 : 3);

    private static bool AckMatchesSuccess(
        FixtureB1EndpointAck ack,
        FixtureB1EndpointSuccessReceipt success) =>
        ack.Binding == success.Binding && ack.OperationId == success.OperationId &&
        ack.SuccessGeneration == success.SuccessGeneration &&
        ack.SourceLeaseGeneration == success.SourceLeaseGeneration &&
        ack.TargetLeaseGeneration == success.TargetLeaseGeneration &&
        ack.Direction == success.Direction && ack.ResourceKind == success.ResourceKind &&
        ack.Source == success.Source && ack.Target == success.Target &&
        ack.ExactRunner == success.ExactRunner && ack.Sequence == success.Sequence + 1 &&
        ack.TargetLocalCoordinate == success.TargetLocalCoordinate &&
        ack.ResourceReceipt == success.ResourceReceipt;

    private bool TryGetExpectedAckSuccess(
        FixtureB1EndpointAck ack,
        out FixtureB1EndpointSuccessReceipt success)
    {
        if (_successes.TryGetValue(ack.SuccessGeneration, out var nativeSuccess) &&
            _validatedSuccesses.Contains(ack.SuccessGeneration))
        {
            success = nativeSuccess;
            return true;
        }
        if (_sourceObserved.TryGetValue(
                ack.SuccessGeneration, out var observedSuccess))
        {
            success = observedSuccess;
            return true;
        }
        success = null!;
        return false;
    }

    private bool JournalPayloadBound(FixtureB1EndpointJournalRecord record) =>
        (record.Intent is null || record.Intent.Binding == _binding) &&
        (record.Success is null || record.Success.Binding == _binding) &&
        (record.Ack is null || record.Ack.Binding == _binding);

    private FixtureHandoffProcessIdentity LocalIdentity() =>
        _role == FixtureB1EndpointRole.Wrapper
            ? _binding.ExactWrapper
            : _binding.ExactLauncher;

    private FixtureHandoffProcessIdentity PeerIdentity() =>
        _role == FixtureB1EndpointRole.Wrapper
            ? _binding.ExactLauncher
            : _binding.ExactWrapper;

    private bool Reject(
        FixtureB1EndpointDiagnosticCode diagnostic,
        FixtureB1EndpointDuplicateIntent? intent = null,
        FixtureB1EndpointSuccessReceipt? success = null,
        FixtureB1EndpointAck? ack = null)
    {
        if (_state == FixtureB1EndpointState.Rejected)
            return false;
        if (!Persist(Record(
                FixtureB1EndpointJournalEvent.ProtocolRejected,
                diagnostic,
                intent,
                success,
                ack)))
            diagnostic = FixtureB1EndpointDiagnosticCode.JournalFailure;
        return SetRejected(diagnostic);
    }

    private bool SetRejected(FixtureB1EndpointDiagnosticCode diagnostic)
    {
        _state = FixtureB1EndpointState.Rejected;
        if (_diagnostic == FixtureB1EndpointDiagnosticCode.None)
            _diagnostic = diagnostic;
        return false;
    }

    private FixtureB1EndpointJournalRecord Record(
        FixtureB1EndpointJournalEvent @event,
        FixtureB1EndpointDiagnosticCode diagnostic,
        FixtureB1EndpointDuplicateIntent? intent = null,
        FixtureB1EndpointSuccessReceipt? success = null,
        FixtureB1EndpointAck? ack = null) => new(
            _role,
            _nextOrdinal + 1,
            @event,
            diagnostic,
            intent,
            success,
            ack);

    private bool Persist(FixtureB1EndpointJournalRecord record)
    {
        if (record.Ordinal != _nextOrdinal + 1)
            return false;
        bool appended;
        try { appended = _journal.TryAppend(record); }
        catch { appended = false; }
        if (!appended)
            return false;
        _nextOrdinal = record.Ordinal;
        _records.Add(record);
        return true;
    }

    private static bool ValidBinding(FixtureB1EndpointBinding binding) =>
        binding.RunNonce != Guid.Empty && binding.LaunchId != Guid.Empty &&
        binding.RunNonce != binding.LaunchId &&
        binding.ExactWrapper.IsValid && binding.ExactLauncher.IsValid &&
        binding.ExactWrapper != binding.ExactLauncher &&
        binding.ConnectionReceipt > 0;

    private static bool Observe(Func<bool> observation)
    {
        try { return observation(); }
        catch { return false; }
    }
}
