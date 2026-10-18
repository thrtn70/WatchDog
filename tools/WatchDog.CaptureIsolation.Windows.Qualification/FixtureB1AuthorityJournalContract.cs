using System.Security.Cryptography;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureB1AuthorityObjectKind
{
    WrapperProcess,
    LauncherProcess,
    RunnerProcess,
    SafetyJob,
    RunnerJob,
}

internal enum FixtureB1JournalAction : byte
{
    Intent = 1,
    SuccessObligation = 2,
    SuccessValidated = 3,
    AckAccepted = 4,
}

internal enum FixtureB1JournalInspectionState
{
    Empty,
    ManagedConsistent,
    CleanupOnly,
    Corrupt,
    ForensicOnly,
}

internal sealed record FixtureB1AuthoritySession(
    Guid RunNonce,
    Guid LaunchId,
    Guid WrapperInstance,
    Guid SessionEpoch,
    long ConnectionGeneration);

internal sealed record FixtureB1ExactObjectObservation(
    FixtureB1AuthorityObjectKind Kind,
    FixtureB1EndpointRole Owner,
    FixtureB1EndpointRole Creator,
    Guid ExactObjectReceipt,
    Guid ObservationReceipt,
    Guid EffectiveRightsReceipt,
    Guid LeaseGeneration,
    bool Retained,
    bool NonInheritable);

internal interface IFixtureB1ExactObjectAuthority
{
    // Must be backed by a retained exact object, not a copied PID, path, or handle number.
    bool IsExactRetainedObject(
        FixtureB1AuthoritySession session,
        FixtureB1ExactObjectObservation observation);
}

internal sealed class FixtureB1AuthorityToken
{
    internal FixtureB1AuthorityToken(Guid id) => Id = id;
    internal Guid Id { get; }
}

internal sealed class FixtureB1OriginalInvocation
{
    internal FixtureB1OriginalInvocation(Guid id, FixtureB1AuthorityToken source,
        FixtureB1AuthorityObjectKind sourceKind)
    {
        Id = id;
        Source = source;
        SourceKind = sourceKind;
    }

    internal Guid Id { get; }
    internal FixtureB1AuthorityToken Source { get; }
    internal FixtureB1AuthorityObjectKind SourceKind { get; }
}

internal sealed class FixtureB1EmergencyObligation
{
    internal FixtureB1EmergencyObligation(
        Guid id,
        FixtureB1OriginalInvocation invocation,
        byte[] callbackPayload)
    {
        Id = id;
        Invocation = invocation;
        _callbackPayload = (byte[])callbackPayload.Clone();
    }

    internal Guid Id { get; }
    internal FixtureB1OriginalInvocation Invocation { get; }
    private readonly byte[] _callbackPayload;
    internal byte[] CallbackPayload => (byte[])_callbackPayload.Clone();
}

/// <summary>
/// Fixture-private, in-memory authority oracle. A future native adapter must supply exact
/// retained-object observations and assign the completion identity before reading callback
/// fields. Tokens and journal bytes cannot reconstruct a lost W process authority.
/// </summary>
internal sealed class FixtureB1AuthorityRegistry
{
    private readonly object _gate = new();
    private readonly FixtureB1AuthoritySession _session;
    private readonly IFixtureB1ExactObjectAuthority _observer;
    private readonly Dictionary<Guid, (FixtureB1AuthorityToken Token,
        FixtureB1ExactObjectObservation Observation)> _leases = [];
    private readonly Dictionary<Guid, FixtureB1OriginalInvocation> _invocations = [];
    private readonly Dictionary<Guid, FixtureB1EmergencyObligation> _obligations = [];
    private readonly HashSet<Guid> _completedInvocations = [];
    private bool _wrapperDied;
    private bool _authorityLost;
    private bool _peerDied;
    private bool _duplicateOriginalCompletion;
    private long _committedJournalOrdinal;
    private byte[] _committedJournalDigest = new byte[32];

    internal FixtureB1AuthorityRegistry(
        FixtureB1AuthoritySession session,
        IFixtureB1ExactObjectAuthority observer)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(observer);
        if (session.RunNonce == Guid.Empty || session.LaunchId == Guid.Empty ||
            session.WrapperInstance == Guid.Empty || session.SessionEpoch == Guid.Empty ||
            session.ConnectionGeneration <= 0)
            throw new ArgumentException("Exact session identity is required.", nameof(session));
        _session = session;
        _observer = observer;
    }

    internal FixtureB1AuthoritySession Session => _session;
    internal bool IsLive { get { lock (_gate) return !_wrapperDied; } }
    internal bool AdmissionOpen
    {
        get { lock (_gate) return !_wrapperDied && !_authorityLost && !_peerDied; }
    }
    internal bool PeerDied { get { lock (_gate) return _peerDied; } }
    internal bool DuplicateOriginalCompletion
    {
        get { lock (_gate) return _duplicateOriginalCompletion; }
    }
    internal IReadOnlyList<FixtureB1EmergencyObligation> EmergencyObligations
    {
        get { lock (_gate) return [.. _obligations.Values]; }
    }
    internal IReadOnlyList<FixtureB1OriginalInvocation> OriginalInvocations
    {
        get { lock (_gate) return [.. _invocations.Values]; }
    }
    internal FixtureB1JournalAnchor CommittedJournalHead
    {
        get
        {
            lock (_gate)
                return new(_committedJournalOrdinal,
                    (byte[])_committedJournalDigest.Clone());
        }
    }

    internal void RememberCommittedJournalHead(FixtureB1JournalAnchor head)
    {
        lock (_gate)
        {
            if (!_wrapperDied && head.Ordinal == _committedJournalOrdinal + 1 &&
                head.Digest.Length == 32)
            {
                _committedJournalOrdinal = head.Ordinal;
                _committedJournalDigest = (byte[])head.Digest.Clone();
            }
        }
    }

    internal FixtureB1AuthorityToken? Register(FixtureB1ExactObjectObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            if (_wrapperDied || _authorityLost || !ValidObservation(observation) ||
                !ValidProvenance(observation) ||
                _leases.Values.Any(existing =>
                    existing.Observation.LeaseGeneration == observation.LeaseGeneration) ||
                !Observe(() => _observer.IsExactRetainedObject(_session, observation)))
                return null;
            var token = new FixtureB1AuthorityToken(Guid.NewGuid());
            _leases.Add(token.Id, (token, observation));
            return token;
        }
    }

    internal bool Owns(
        FixtureB1AuthorityToken token,
        FixtureB1AuthoritySession session,
        FixtureB1AuthorityObjectKind kind,
        FixtureB1EndpointRole owner,
        Guid exactObjectReceipt,
        Guid leaseGeneration)
    {
        lock (_gate)
            return !_wrapperDied && !_authorityLost && session == _session &&
                _leases.TryGetValue(token.Id, out var lease) &&
                ReferenceEquals(lease.Token, token) &&
                lease.Observation.Kind == kind && lease.Observation.Owner == owner &&
                lease.Observation.ExactObjectReceipt == exactObjectReceipt &&
                lease.Observation.LeaseGeneration == leaseGeneration &&
                Observe(() => _observer.IsExactRetainedObject(_session, lease.Observation));
    }

    internal FixtureB1OriginalInvocation? BeginOriginal(
        FixtureB1AuthorityToken source,
        FixtureB1AuthoritySession session)
    {
        lock (_gate)
        {
            if (_wrapperDied || _authorityLost || _peerDied ||
                session != _session ||
                !_leases.TryGetValue(source.Id, out var lease) ||
                !ReferenceEquals(lease.Token, source) ||
                !CanDuplicateSource(lease.Observation) ||
                !Observe(() => _observer.IsExactRetainedObject(_session, lease.Observation)))
                return null;
            var invocation = new FixtureB1OriginalInvocation(Guid.NewGuid(), source,
                lease.Observation.Kind);
            _invocations.Add(invocation.Id, invocation);
            return invocation;
        }
    }

    internal FixtureB1EmergencyObligation? CaptureNativeSuccess(
        FixtureB1OriginalInvocation invocation,
        byte[] callbackPayload)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(callbackPayload);
        lock (_gate)
        {
            if (_wrapperDied || !_invocations.TryGetValue(invocation.Id, out var original) ||
                !ReferenceEquals(original, invocation))
                return null;
            // Each observed physical completion gets its own trusted identity. Equal or
            // malformed callback bytes do not deduplicate distinct native successes.
            var obligation = new FixtureB1EmergencyObligation(
                Guid.NewGuid(), invocation, callbackPayload);
            _obligations.Add(obligation.Id, obligation);
            if (!_completedInvocations.Add(invocation.Id))
                _duplicateOriginalCompletion = true;
            return obligation;
        }
    }

    internal bool IsOriginal(FixtureB1OriginalInvocation invocation)
    {
        lock (_gate)
            return CanAdmitOriginal(invocation);
    }

    internal bool WithOriginalAdmission(
        FixtureB1OriginalInvocation invocation,
        bool requirePeer,
        bool requireUniqueCompletion,
        Func<bool> action)
    {
        lock (_gate)
        {
            if (!CanAdmitOriginal(invocation) ||
                (requirePeer && _peerDied) ||
                (requireUniqueCompletion && _duplicateOriginalCompletion))
                return false;
            return action();
        }
    }

    private bool CanAdmitOriginal(FixtureB1OriginalInvocation invocation)
    {
        if (_wrapperDied || _authorityLost ||
            !_invocations.TryGetValue(invocation.Id, out var original) ||
            !ReferenceEquals(original, invocation) ||
            !_leases.TryGetValue(invocation.Source.Id, out var lease) ||
            !ReferenceEquals(lease.Token, invocation.Source) ||
            !Observe(() => _observer.IsExactRetainedObject(_session, lease.Observation)))
        {
            _authorityLost = true;
            return false;
        }
        return true;
    }

    internal bool IsObligation(FixtureB1EmergencyObligation obligation)
    {
        lock (_gate)
            return _obligations.TryGetValue(obligation.Id, out var original) &&
                ReferenceEquals(original, obligation);
    }

    internal bool RefreshRetainedAuthority()
    {
        lock (_gate)
        {
            if (_wrapperDied || _authorityLost) return false;
            foreach (var lease in _leases.Values)
            {
                if (!Observe(() => _observer.IsExactRetainedObject(
                        _session, lease.Observation)))
                {
                    _authorityLost = true;
                    return false;
                }
            }
            return true;
        }
    }

    internal void LoseRetainedObject(FixtureB1AuthorityToken token)
    {
        lock (_gate)
            if (_leases.TryGetValue(token.Id, out var lease) &&
                ReferenceEquals(lease.Token, token))
                _authorityLost = true;
    }

    internal void WrapperDied()
    {
        lock (_gate)
            _wrapperDied = true;
    }

    internal void LauncherDied()
    {
        lock (_gate)
            _peerDied = true;
    }

    private static bool ValidObservation(FixtureB1ExactObjectObservation observation) =>
        Enum.IsDefined(observation.Kind) && Enum.IsDefined(observation.Owner) &&
        Enum.IsDefined(observation.Creator) && observation.ExactObjectReceipt != Guid.Empty &&
        observation.ObservationReceipt != Guid.Empty &&
        observation.EffectiveRightsReceipt != Guid.Empty &&
        observation.LeaseGeneration != Guid.Empty && observation.Retained &&
        observation.NonInheritable;

    private bool ValidProvenance(FixtureB1ExactObjectObservation observation)
    {
        var shape = observation.Kind switch
        {
            FixtureB1AuthorityObjectKind.WrapperProcess or
                FixtureB1AuthorityObjectKind.LauncherProcess or
                FixtureB1AuthorityObjectKind.SafetyJob =>
                observation.Owner == FixtureB1EndpointRole.Wrapper &&
                observation.Creator == FixtureB1EndpointRole.Wrapper,
            FixtureB1AuthorityObjectKind.RunnerJob =>
                observation.Creator == FixtureB1EndpointRole.Wrapper,
            FixtureB1AuthorityObjectKind.RunnerProcess =>
                observation.Creator == FixtureB1EndpointRole.Launcher,
            _ => false,
        };
        if (!shape) return false;
        if (observation.Kind == FixtureB1AuthorityObjectKind.RunnerJob &&
            observation.Owner == FixtureB1EndpointRole.Launcher)
            return _leases.Values.Any(lease =>
                lease.Observation.Kind == FixtureB1AuthorityObjectKind.RunnerJob &&
                lease.Observation.Owner == FixtureB1EndpointRole.Wrapper &&
                lease.Observation.ExactObjectReceipt == observation.ExactObjectReceipt);
        if (observation.Kind == FixtureB1AuthorityObjectKind.RunnerProcess &&
            observation.Owner == FixtureB1EndpointRole.Wrapper)
            return _leases.Values.Any(lease =>
                lease.Observation.Kind == FixtureB1AuthorityObjectKind.RunnerProcess &&
                lease.Observation.Owner == FixtureB1EndpointRole.Launcher &&
                lease.Observation.ExactObjectReceipt == observation.ExactObjectReceipt);
        return true;
    }

    private static bool CanDuplicateSource(
        FixtureB1ExactObjectObservation observation) =>
        (observation.Kind == FixtureB1AuthorityObjectKind.RunnerJob &&
            observation.Owner == FixtureB1EndpointRole.Wrapper) ||
        (observation.Kind == FixtureB1AuthorityObjectKind.RunnerProcess &&
            observation.Owner == FixtureB1EndpointRole.Launcher);

    private static bool Observe(Func<bool> observation)
    {
        try { return observation(); }
        catch { return false; }
    }
}

internal sealed record FixtureB1JournalStorageLease(
    Guid JournalId,
    Guid RootIdentity,
    Guid FileIdentity,
    Guid AclReceipt,
    Guid ExclusiveWriterReceipt,
    Guid WrapperInstance,
    Guid SessionEpoch,
    long ConnectionGeneration);

internal interface IFixtureB1JournalRootAuthority
{
    // Must verify retained root/file identity, local private ACL, no reparse substitution,
    // and exclusive W writer. These are injected claims in this managed model.
    bool IsAdmitted(FixtureB1AuthoritySession session, FixtureB1JournalStorageLease lease);
}

internal interface IFixtureB1JournalPeerAuthority
{
    bool IsExactAckAuthenticated(
        FixtureB1AuthoritySession session,
        Guid invocationId,
        Guid completionId,
        Guid sourceTokenId,
        FixtureB1AuthorityObjectKind sourceKind,
        FixtureB1EndpointRole origin);
}

internal sealed record FixtureB1JournalAnchor(long Ordinal, byte[] Digest);

internal interface IFixtureB1FramedJournalStore
{
    // A true return means bytes were flushed through the store's durable-append contract.
    // A false return may still have written some or all bytes; callers must stop admission.
    bool TryAppendAndFlush(byte[] frame);
    // The independent retained high-water anchor is flushed after the frame.
    bool TryAdvanceAndFlushAnchor(FixtureB1JournalAnchor anchor);
    byte[] ReadFrames();
    FixtureB1JournalAnchor? ReadAnchor();
}

internal sealed record FixtureB1JournalRecord(
    FixtureB1JournalAction Action,
    FixtureB1EndpointRole Origin,
    FixtureB1AuthorityObjectKind SourceKind,
    Guid InvocationId,
    Guid CompletionId,
    Guid SourceLeaseTokenId,
    byte[] Payload);

internal sealed record FixtureB1JournalInspection(
    FixtureB1JournalInspectionState State,
    IReadOnlyList<FixtureB1JournalRecord> VerifiedRecords,
    IReadOnlyList<FixtureB1JournalRecord> KnownSuccessObligations)
{
    internal bool NativeDurabilityProven => false;
    internal bool NativeAuthorityProven => false;
}

/// <summary>
/// W-only canonical-frame protocol oracle. It never opens files and does not prove OS flush,
/// ACL, hash-key custody, native object identity, or W-death containment.
/// </summary>
internal sealed class FixtureB1FramedJournalContract
{
    private const int Magic = 0x314A3142;
    private const byte Version = 1;
    private const int DigestSize = 32;
    private const int MaxPayload = 2048;
    private const int MaxBody = 4096;
    private readonly object _gate = new();
    private readonly FixtureB1AuthorityRegistry _registry;
    private readonly FixtureB1JournalStorageLease _lease;
    private readonly IFixtureB1JournalRootAuthority _root;
    private readonly IFixtureB1JournalPeerAuthority _peer;
    private readonly IFixtureB1FramedJournalStore _store;
    private byte[] _head = new byte[DigestSize];
    private readonly Dictionary<Guid,
        (Guid Source, FixtureB1AuthorityObjectKind Kind)> _committedIntents = [];
    private readonly Dictionary<Guid,
        (Guid Invocation, Guid Source, FixtureB1AuthorityObjectKind Kind)>
        _committedSuccesses = [];
    private readonly HashSet<Guid> _validated = [];
    private readonly HashSet<Guid> _accepted = [];
    private long _ordinal;
    private bool _poisoned;

    internal FixtureB1FramedJournalContract(
        FixtureB1AuthorityRegistry registry,
        FixtureB1JournalStorageLease lease,
        IFixtureB1JournalRootAuthority root,
        IFixtureB1JournalPeerAuthority peer,
        IFixtureB1FramedJournalStore store)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(store);
        if (lease.JournalId == Guid.Empty || lease.RootIdentity == Guid.Empty ||
            lease.FileIdentity == Guid.Empty || lease.AclReceipt == Guid.Empty ||
            lease.ExclusiveWriterReceipt == Guid.Empty ||
            lease.WrapperInstance != registry.Session.WrapperInstance ||
            lease.SessionEpoch != registry.Session.SessionEpoch ||
            lease.ConnectionGeneration != registry.Session.ConnectionGeneration ||
            !Observe(() => root.IsAdmitted(registry.Session, lease)))
            throw new ArgumentException("Private W journal admission failed.", nameof(lease));
        _registry = registry;
        _lease = lease;
        _root = root;
        _peer = peer;
        _store = store;
        if (_store.ReadFrames().Length != 0 || _store.ReadAnchor() is not null)
            throw new ArgumentException("A fresh writer requires an empty store.", nameof(store));
    }

    internal bool IsPoisoned { get { lock (_gate) return _poisoned; } }
    internal bool NativeDurabilityProven => false;

    internal bool TryRecordIntent(FixtureB1OriginalInvocation invocation)
    {
        lock (_gate)
            return _registry.WithOriginalAdmission(invocation,
                requirePeer: true, requireUniqueCompletion: false,
                () => TryAppendLocked(new(FixtureB1JournalAction.Intent,
                    FixtureB1EndpointRole.Wrapper, invocation.SourceKind,
                    invocation.Id, Guid.Empty, invocation.Source.Id, [])));
    }

    internal bool TryRecordSuccess(FixtureB1EmergencyObligation obligation)
    {
        lock (_gate)
        {
            if (!_registry.IsObligation(obligation) || _poisoned ||
                !_registry.IsLive ||
                !Observe(() => _root.IsAdmitted(_registry.Session, _lease)))
            {
                _poisoned = true;
                return false;
            }
            if (_committedSuccesses.ContainsKey(obligation.Id))
                return true;
            return TryAppendLocked(new(FixtureB1JournalAction.SuccessObligation,
                FixtureB1EndpointRole.Wrapper, obligation.Invocation.SourceKind,
                obligation.Invocation.Id, obligation.Id,
                obligation.Invocation.Source.Id, obligation.CallbackPayload));
        }
    }

    internal bool TryRecordObservation(
        FixtureB1JournalAction action,
        FixtureB1EndpointRole origin,
        FixtureB1EmergencyObligation obligation)
    {
        if (action is not (FixtureB1JournalAction.SuccessValidated or
            FixtureB1JournalAction.AckAccepted)) return false;
        lock (_gate)
        {
            if (!_registry.IsObligation(obligation)) return false;
            return _registry.WithOriginalAdmission(obligation.Invocation,
                requirePeer: action == FixtureB1JournalAction.AckAccepted,
                requireUniqueCompletion: action == FixtureB1JournalAction.AckAccepted,
                () =>
                {
                    if (action == FixtureB1JournalAction.AckAccepted &&
                        !Observe(() => _peer.IsExactAckAuthenticated(
                            _registry.Session, obligation.Invocation.Id,
                            obligation.Id, obligation.Invocation.Source.Id,
                            obligation.Invocation.SourceKind, origin)))
                        return false;
                    return TryAppendLocked(new(action, origin,
                        obligation.Invocation.SourceKind,
                        obligation.Invocation.Id, obligation.Id,
                        obligation.Invocation.Source.Id, []));
                });
        }
    }

    private bool TryAppendLocked(FixtureB1JournalRecord record)
    {
        if (_poisoned || !_registry.IsLive ||
            !Observe(() => _root.IsAdmitted(_registry.Session, _lease)) ||
            record.Payload.Length > MaxPayload || !ValidNextRecord(record))
        {
            _poisoned = true;
            return false;
        }
        var frame = Encode(record, _registry.Session, _lease.JournalId,
            _ordinal + 1, _head);
        var digest = frame[^DigestSize..];
        try
        {
            if (!_store.TryAppendAndFlush(frame) ||
                !_store.TryAdvanceAndFlushAnchor(
                    new(_ordinal + 1, digest)))
            {
                _poisoned = true;
                return false;
            }
        }
        catch
        {
            _poisoned = true;
            return false;
        }
        _head = digest;
        _ordinal++;
        _registry.RememberCommittedJournalHead(new(_ordinal, digest));
        switch (record.Action)
        {
            case FixtureB1JournalAction.Intent:
                _committedIntents.Add(record.InvocationId,
                    (record.SourceLeaseTokenId, record.SourceKind));
                break;
            case FixtureB1JournalAction.SuccessObligation:
                _committedSuccesses.Add(record.CompletionId,
                    (record.InvocationId, record.SourceLeaseTokenId,
                        record.SourceKind));
                break;
            case FixtureB1JournalAction.SuccessValidated:
                _validated.Add(record.CompletionId);
                break;
            case FixtureB1JournalAction.AckAccepted:
                _accepted.Add(record.CompletionId);
                break;
        }
        return true;
    }

    private bool ValidNextRecord(FixtureB1JournalRecord record) => record.Action switch
    {
        FixtureB1JournalAction.Intent =>
            record.Origin == FixtureB1EndpointRole.Wrapper &&
            CanDuplicateKind(record.SourceKind) &&
            record.InvocationId != Guid.Empty && record.CompletionId == Guid.Empty &&
            record.SourceLeaseTokenId != Guid.Empty && record.Payload.Length == 0 &&
            !_committedIntents.ContainsKey(record.InvocationId),
        FixtureB1JournalAction.SuccessObligation =>
            record.Origin == FixtureB1EndpointRole.Wrapper &&
            CanDuplicateKind(record.SourceKind) &&
            record.CompletionId != Guid.Empty &&
            _committedIntents.TryGetValue(record.InvocationId, out var source) &&
            source.Source == record.SourceLeaseTokenId &&
            source.Kind == record.SourceKind &&
            !_committedSuccesses.ContainsKey(record.CompletionId),
        FixtureB1JournalAction.SuccessValidated =>
            record.Origin == FixtureB1EndpointRole.Wrapper &&
            CanDuplicateKind(record.SourceKind) && record.Payload.Length == 0 &&
            _committedSuccesses.TryGetValue(record.CompletionId, out var success) &&
            success.Invocation == record.InvocationId &&
            success.Source == record.SourceLeaseTokenId &&
            success.Kind == record.SourceKind &&
            !_validated.Contains(record.CompletionId),
        FixtureB1JournalAction.AckAccepted =>
            record.Origin == ExpectedAckOrigin(record.SourceKind) &&
            record.Payload.Length == 0 &&
            _committedSuccesses.TryGetValue(record.CompletionId, out var success) &&
            success.Invocation == record.InvocationId &&
            success.Source == record.SourceLeaseTokenId &&
            success.Kind == record.SourceKind &&
            _validated.Contains(record.CompletionId) &&
            !_accepted.Contains(record.CompletionId),
        _ => false,
    };

    private static bool CanDuplicateKind(FixtureB1AuthorityObjectKind kind) =>
        kind is FixtureB1AuthorityObjectKind.RunnerJob or
            FixtureB1AuthorityObjectKind.RunnerProcess;

    private static FixtureB1EndpointRole ExpectedAckOrigin(
        FixtureB1AuthorityObjectKind kind) => kind ==
        FixtureB1AuthorityObjectKind.RunnerJob
            ? FixtureB1EndpointRole.Launcher
            : FixtureB1EndpointRole.Wrapper;

    internal static FixtureB1JournalInspection Inspect(
        FixtureB1AuthorityRegistry? survivingRegistry,
        FixtureB1AuthoritySession session,
        FixtureB1JournalStorageLease lease,
        IFixtureB1JournalRootAuthority root,
        IFixtureB1JournalPeerAuthority peer,
        IFixtureB1FramedJournalStore store)
    {
        var verified = new List<FixtureB1JournalRecord>();
        var obligations = new List<FixtureB1JournalRecord>();
        if (lease.WrapperInstance != session.WrapperInstance ||
            lease.SessionEpoch != session.SessionEpoch ||
            lease.ConnectionGeneration != session.ConnectionGeneration ||
            !Observe(() => root.IsAdmitted(session, lease)))
            return new(FixtureB1JournalInspectionState.Corrupt, verified, obligations);
        byte[] bytes;
        FixtureB1JournalAnchor? anchor;
        try { bytes = store.ReadFrames(); anchor = store.ReadAnchor(); }
        catch { return new(FixtureB1JournalInspectionState.Corrupt, verified, obligations); }
        if (bytes.Length == 0 && anchor is null)
        {
            survivingRegistry?.RefreshRetainedAuthority();
            var state = survivingRegistry?.IsLive == true &&
                survivingRegistry.Session == session
                ? survivingRegistry.CommittedJournalHead.Ordinal == 0
                    ? survivingRegistry.AdmissionOpen &&
                        survivingRegistry.OriginalInvocations.Count == 0 &&
                        survivingRegistry.EmergencyObligations.Count == 0
                        ? FixtureB1JournalInspectionState.Empty
                        : FixtureB1JournalInspectionState.CleanupOnly
                    : FixtureB1JournalInspectionState.Corrupt
                : FixtureB1JournalInspectionState.ForensicOnly;
            return new(state, verified, obligations);
        }
        var prior = new byte[DigestSize];
        var offset = 0;
        long ordinal = 0;
        var intents = new Dictionary<Guid,
            (Guid Source, FixtureB1AuthorityObjectKind Kind)>();
        var successes = new Dictionary<Guid,
            (Guid Invocation, Guid Source, FixtureB1AuthorityObjectKind Kind)>();
        var validated = new HashSet<Guid>();
        var accepted = new HashSet<Guid>();
        var semanticError = false;
        while (offset < bytes.Length)
        {
            if (!TryDecode(bytes, ref offset, session, lease.JournalId,
                    ordinal + 1, prior, out var record, out var digest))
                return new(FixtureB1JournalInspectionState.Corrupt, verified, obligations);
            ordinal++;
            prior = digest;
            verified.Add(record!);
            if (record!.Action == FixtureB1JournalAction.SuccessObligation)
                obligations.Add(record);
            switch (record.Action)
            {
                case FixtureB1JournalAction.Intent:
                    semanticError |= record.Origin != FixtureB1EndpointRole.Wrapper ||
                        !CanDuplicateKind(record.SourceKind) ||
                        record.InvocationId == Guid.Empty ||
                        record.CompletionId != Guid.Empty ||
                        record.SourceLeaseTokenId == Guid.Empty ||
                        record.Payload.Length != 0 ||
                        !intents.TryAdd(record.InvocationId,
                            (record.SourceLeaseTokenId, record.SourceKind));
                    break;
                case FixtureB1JournalAction.SuccessObligation:
                    semanticError |= record.Origin != FixtureB1EndpointRole.Wrapper ||
                        !CanDuplicateKind(record.SourceKind) ||
                        record.CompletionId == Guid.Empty ||
                        !intents.TryGetValue(record.InvocationId, out var source) ||
                        source.Source != record.SourceLeaseTokenId ||
                        source.Kind != record.SourceKind ||
                        !successes.TryAdd(record.CompletionId,
                            (record.InvocationId, record.SourceLeaseTokenId,
                                record.SourceKind));
                    break;
                case FixtureB1JournalAction.SuccessValidated:
                    semanticError |= record.Origin != FixtureB1EndpointRole.Wrapper ||
                        !successes.TryGetValue(record.CompletionId,
                            out var validatedSuccess) ||
                        validatedSuccess.Invocation != record.InvocationId ||
                        validatedSuccess.Source != record.SourceLeaseTokenId ||
                        validatedSuccess.Kind != record.SourceKind ||
                        record.Payload.Length != 0 ||
                        !validated.Add(record.CompletionId);
                    break;
                case FixtureB1JournalAction.AckAccepted:
                    semanticError |= record.Origin !=
                            ExpectedAckOrigin(record.SourceKind) ||
                        !Observe(() => peer.IsExactAckAuthenticated(session,
                            record.InvocationId, record.CompletionId,
                            record.SourceLeaseTokenId, record.SourceKind,
                            record.Origin)) ||
                        !successes.TryGetValue(record.CompletionId,
                            out var ackSuccess) ||
                        ackSuccess.Invocation != record.InvocationId ||
                        ackSuccess.Source != record.SourceLeaseTokenId ||
                        ackSuccess.Kind != record.SourceKind ||
                        !validated.Contains(record.CompletionId) ||
                        record.Payload.Length != 0 ||
                        !accepted.Add(record.CompletionId);
                    break;
                default:
                    semanticError = true;
                    break;
            }
        }
        if (anchor is null || anchor.Ordinal != ordinal ||
            anchor.Digest.Length != DigestSize ||
            !CryptographicOperations.FixedTimeEquals(anchor.Digest, prior) ||
            semanticError)
            return new(FixtureB1JournalInspectionState.Corrupt, verified, obligations);
        if (survivingRegistry is null || !survivingRegistry.IsLive ||
            survivingRegistry.Session != session)
            return new(FixtureB1JournalInspectionState.ForensicOnly, verified, obligations);
        survivingRegistry.RefreshRetainedAuthority();
        var retainedHead = survivingRegistry.CommittedJournalHead;
        if (retainedHead.Ordinal > ordinal ||
            (retainedHead.Ordinal == ordinal &&
                !CryptographicOperations.FixedTimeEquals(
                    retainedHead.Digest, prior)))
            return new(FixtureB1JournalInspectionState.Corrupt, verified, obligations);
        if (retainedHead.Ordinal < ordinal)
            return new(FixtureB1JournalInspectionState.CleanupOnly, verified, obligations);
        var unsettled = !survivingRegistry.AdmissionOpen ||
            survivingRegistry.DuplicateOriginalCompletion ||
            survivingRegistry.OriginalInvocations.Any(invocation =>
                !intents.ContainsKey(invocation.Id)) ||
            survivingRegistry.EmergencyObligations.Any(obligation =>
                !successes.ContainsKey(obligation.Id)) ||
            successes.Values.GroupBy(success => success.Invocation)
                .Any(group => group.Count() > 1) ||
            intents.Keys.Any(invocation =>
                !successes.Values.Any(success => success.Invocation == invocation)) ||
            successes.Keys.Any(completion =>
                !validated.Contains(completion) || !accepted.Contains(completion));
        return new(unsettled
            ? FixtureB1JournalInspectionState.CleanupOnly
            : FixtureB1JournalInspectionState.ManagedConsistent,
            verified, obligations);
    }

    private static byte[] Encode(
        FixtureB1JournalRecord record,
        FixtureB1AuthoritySession session,
        Guid journalId,
        long ordinal,
        byte[] previous)
    {
        using var bodyStream = new MemoryStream();
        using (var writer = new BinaryWriter(bodyStream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(ordinal);
            writer.Write((byte)record.Action);
            writer.Write((byte)record.Origin);
            writer.Write((byte)record.SourceKind);
            writer.Write(session.RunNonce.ToByteArray());
            writer.Write(session.LaunchId.ToByteArray());
            writer.Write(session.WrapperInstance.ToByteArray());
            writer.Write(session.SessionEpoch.ToByteArray());
            writer.Write(session.ConnectionGeneration);
            writer.Write(journalId.ToByteArray());
            writer.Write(record.InvocationId.ToByteArray());
            writer.Write(record.CompletionId.ToByteArray());
            writer.Write(record.SourceLeaseTokenId.ToByteArray());
            writer.Write(record.Payload.Length);
            writer.Write(record.Payload);
            writer.Write(previous);
        }
        var body = bodyStream.ToArray();
        var digest = SHA256.HashData(body);
        using var frameStream = new MemoryStream();
        using var frameWriter = new BinaryWriter(frameStream);
        frameWriter.Write(body.Length);
        frameWriter.Write(body);
        frameWriter.Write(digest);
        return frameStream.ToArray();
    }

    private static bool TryDecode(
        byte[] bytes,
        ref int offset,
        FixtureB1AuthoritySession session,
        Guid journalId,
        long expectedOrdinal,
        byte[] previous,
        out FixtureB1JournalRecord? record,
        out byte[] digest)
    {
        record = null;
        digest = [];
        if (bytes.Length - offset < sizeof(int)) return false;
        var bodyLength = BitConverter.ToInt32(bytes, offset);
        offset += sizeof(int);
        if (bodyLength <= 0 || bodyLength > MaxBody ||
            bytes.Length - offset < bodyLength + DigestSize) return false;
        var body = bytes.AsSpan(offset, bodyLength).ToArray();
        offset += bodyLength;
        digest = bytes.AsSpan(offset, DigestSize).ToArray();
        offset += DigestSize;
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), digest))
            return false;
        try
        {
            using var stream = new MemoryStream(body);
            using var reader = new BinaryReader(stream);
            if (reader.ReadInt32() != Magic || reader.ReadByte() != Version ||
                reader.ReadInt64() != expectedOrdinal) return false;
            var action = (FixtureB1JournalAction)reader.ReadByte();
            var origin = (FixtureB1EndpointRole)reader.ReadByte();
            var sourceKind = (FixtureB1AuthorityObjectKind)reader.ReadByte();
            if (!Enum.IsDefined(action) || !Enum.IsDefined(origin) ||
                !Enum.IsDefined(sourceKind) ||
                new Guid(reader.ReadBytes(16)) != session.RunNonce ||
                new Guid(reader.ReadBytes(16)) != session.LaunchId ||
                new Guid(reader.ReadBytes(16)) != session.WrapperInstance ||
                new Guid(reader.ReadBytes(16)) != session.SessionEpoch ||
                reader.ReadInt64() != session.ConnectionGeneration ||
                new Guid(reader.ReadBytes(16)) != journalId) return false;
            var invocation = new Guid(reader.ReadBytes(16));
            var completion = new Guid(reader.ReadBytes(16));
            var source = new Guid(reader.ReadBytes(16));
            var payloadLength = reader.ReadInt32();
            if (payloadLength < 0 || payloadLength > MaxPayload ||
                stream.Length - stream.Position != payloadLength + DigestSize)
                return false;
            var payload = reader.ReadBytes(payloadLength);
            if (!CryptographicOperations.FixedTimeEquals(
                    reader.ReadBytes(DigestSize), previous)) return false;
            record = new(action, origin, sourceKind,
                invocation, completion, source, payload);
            return true;
        }
        catch (Exception exception) when (exception is EndOfStreamException or ArgumentException)
        {
            return false;
        }
    }

    private static bool Observe(Func<bool> observation)
    {
        try { return observation(); }
        catch { return false; }
    }
}
