using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureBrokerPhase
{
    Reserved,
    TargetRegistered,
    Delivering,
    Delivered,
    Sealed,
    Resumed,
    GrantIssued,
    Committed,
    ActivationIssued,
    Active,
    Failed,
}

internal enum FixtureBrokerChildPhase
{
    DescriptorAccepted,
    GrantAccepted,
    Active,
    Failed,
}

internal enum FixtureBrokerFailureKind
{
    None,
    Prerequisite,
    Infrastructure,
    Assertion,
    Deadline,
    CleanupAmbiguous,
}

internal sealed record FixtureBrokerDeliveryReceipt(
    string Label, nint TargetHandle, FixtureHandoffHandleKind Kind,
    FixtureHandoffRights Rights, FixtureArtifactIdentity? Identity);

internal readonly record struct FixtureBrokerCreatorProcessCoordinate(nint Value);
internal readonly record struct FixtureBrokerIssuerProcessCoordinate(nint Value);

internal sealed record FixtureBrokerTargetRegistration(
    FixtureHandoffProcessIdentity Target,
    FixtureBrokerCreatorProcessCoordinate CreatorSource,
    FixtureBrokerIssuerProcessCoordinate IssuerCustody);

internal sealed record FixtureBrokerFrameObservation(
    long ConnectionReceipt, byte[] Frame);

internal sealed record FixtureBrokerOwnerDeathEvidence(
    bool NoMoreSpawn,
    bool ExactMembership,
    bool OutputsReconciled,
    bool RunnerHoldsInnerJobHandle,
    bool PendingInnerJobTransfer);

/// <summary>
/// Injected native facts for the pure broker model. An adapter must derive every answer from
/// retained authority and OS-observed peers; PIDs and raw handle values are not sufficient.
/// </summary>
internal interface IFixtureBrokerAuthority
{
    bool TryReserveLaunch(Guid launchId);
    bool IsProcessAlive(FixtureHandoffProcessIdentity process);
    bool AreExpectedCreatorAndIssuer(
        FixtureHandoffEdge edge,
        FixtureHandoffProcessIdentity creator,
        FixtureHandoffProcessIdentity issuer);
    bool HasAuthenticatedCreatorSession(
        FixtureHandoffProcessIdentity creator, FixtureHandoffProcessIdentity issuer);
    bool IsCreatorRegistrationSourceValid(
        FixtureHandoffProcessIdentity creator,
        FixtureBrokerCreatorProcessCoordinate source,
        FixtureHandoffProcessIdentity target);
    bool IsIssuerTargetCustodyValid(
        FixtureHandoffProcessIdentity issuer,
        FixtureBrokerIssuerProcessCoordinate custody,
        FixtureHandoffProcessIdentity target);
    bool IsExpectedBinary(FixtureHandoffProcessIdentity target, QualificationRole role);
    bool IsExpectedContainment(
        FixtureHandoffProcessIdentity target, FixtureHandoffEdge edge);
    FixtureBrokerDeliveryReceipt ObserveDeliveredHandle(
        FixtureHandoffProcessIdentity target, string label, nint targetHandle);
    FixtureArtifactCloseStatus ObserveTransferClose(
        FixtureHandoffProcessIdentity target, nint targetHandle);
    FixtureArtifactCloseStatus ObserveIssuerCustodyClose(
        FixtureHandoffProcessIdentity issuer,
        FixtureBrokerIssuerProcessCoordinate custody);
    bool IsExactTargetReaped(FixtureHandoffProcessIdentity target);
    bool AreObserversDrained(Guid launchId);
    bool AreArtifactOperationsDrained(Guid launchId);
    bool IsOwnerDeathTopologySafe(
        FixtureBrokerSnapshot snapshot, FixtureBrokerOwnerDeathEvidence evidence);
    FixtureHandoffProcessIdentity ObserveConnectedPeer(long connectionReceipt);
    bool IsSnapshotStillAdmitted(FixtureBrokerSnapshot snapshot);
}

internal sealed class FixtureBrokerContractException(string code) : Exception
{
    internal string Code { get; } = code;
}

/// <summary>A sealed, child-local transfer description. All arrays are defensive copies.</summary>
internal sealed class FixtureBrokerDescriptor
{
    private readonly byte[] _snapshotBytes;
    private readonly byte[] _snapshotMac;
    private readonly byte[] _challenge;
    private readonly byte[] _mac;

    internal FixtureBrokerDescriptor(
        FixtureArtifactHandoffPlan plan,
        FixtureBrokerParties parties,
        FixtureBrokerDeliveryMode delivery,
        IReadOnlyList<FixtureBrokerDeliveryReceipt> receipts,
        byte[] snapshotBytes,
        byte[] snapshotMac,
        long deadlineTimestamp,
        long timestampFrequency,
        byte[] challenge,
        byte[] mac)
    {
        Plan = plan;
        Parties = parties;
        Delivery = delivery;
        Receipts = new ReadOnlyCollection<FixtureBrokerDeliveryReceipt>(receipts.ToArray());
        _snapshotBytes = (byte[])snapshotBytes.Clone();
        _snapshotMac = (byte[])snapshotMac.Clone();
        DeadlineTimestamp = deadlineTimestamp;
        TimestampFrequency = timestampFrequency;
        _challenge = (byte[])challenge.Clone();
        _mac = (byte[])mac.Clone();
    }

    internal FixtureArtifactHandoffPlan Plan { get; }
    internal FixtureBrokerParties Parties { get; }
    internal FixtureBrokerDeliveryMode Delivery { get; }
    internal IReadOnlyList<FixtureBrokerDeliveryReceipt> Receipts { get; }
    internal byte[] SnapshotBytes => (byte[])_snapshotBytes.Clone();
    internal byte[] SnapshotMac => (byte[])_snapshotMac.Clone();
    internal long DeadlineTimestamp { get; }
    internal long TimestampFrequency { get; }
    internal byte[] Challenge => (byte[])_challenge.Clone();
    internal byte[] Mac => (byte[])_mac.Clone();
}

/// <summary>
/// Pure, unregistered reservation and activation policy. It does not create processes,
/// duplicate handles, open paths, or establish native qualification evidence.
/// </summary>
internal sealed class FixtureBrokerState
{
    private const int SecretBytes = 32;
    private readonly FixtureArtifactHandoffPlan _plan;
    private readonly FixtureHandoffProcessIdentity _creator;
    private readonly FixtureHandoffProcessIdentity _issuer;
    private readonly FixtureBrokerDeliveryMode _delivery;
    private readonly IFixtureBrokerAuthority _authority;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _lifetime;
    private readonly long _deadlineTimestamp;
    private readonly List<FixtureBrokerDeliveryReceipt> _receipts = [];
    private readonly HashSet<nint> _targetHandles = [];
    private readonly HashSet<nint> _pendingTransferHandles = [];
    private readonly object _transitionGate = new();
    private FixtureBrokerSnapshot? _snapshot;
    private FixtureHandoffProcessIdentity? _ownedTarget;
    private FixtureBrokerIssuerProcessCoordinate? _issuerCustody;
    private bool _issuerCustodyPending;
    private byte[]? _snapshotBytes;
    private byte[]? _snapshotMac;
    private byte[]? _secret;
    private byte[]? _expectedProof;
    private byte[]? _expectedGrantAck;
    private byte[]? _expectedActivationAck;
    private FixtureBrokerDescriptor? _descriptor;
    private FixtureBrokerOwnerDeathEvidence? _ownerDeathEvidence;
    private FixtureBrokerFailureKind _failure;
    private bool _exitRequested;
    private QualificationExitCode? _terminalExitCode;
    private bool _mutated;
    private bool _uncertain;
    private bool _targetReaped;
    private bool _observersDrained;
    private bool _artifactOperationsDrained;
    private bool _cleanupStarted;

    private FixtureBrokerState(
        FixtureArtifactHandoffPlan plan,
        FixtureHandoffProcessIdentity creator,
        FixtureHandoffProcessIdentity issuer,
        FixtureBrokerDeliveryMode delivery,
        IFixtureBrokerAuthority authority,
        TimeProvider clock,
        long deadlineTimestamp,
        CancellationToken lifetime)
    {
        _plan = plan;
        _creator = creator;
        _issuer = issuer;
        _delivery = delivery;
        _authority = authority;
        _clock = clock;
        _deadlineTimestamp = deadlineTimestamp;
        _lifetime = lifetime;
    }

    internal FixtureBrokerPhase Phase { get; private set; } = FixtureBrokerPhase.Reserved;
    internal FixtureBrokerFailureKind Failure => _failure;
    internal bool TargetCustodyAdopted
    {
        get
        {
            lock (_transitionGate)
                return _ownedTarget is not null;
        }
    }

    internal bool OwnsPendingTransfer(nint targetHandle)
    {
        lock (_transitionGate)
            return _pendingTransferHandles.Contains(targetHandle);
    }
    internal bool OwnerDeathArmed { get; private set; }
    internal long DeadlineTimestamp => _deadlineTimestamp;

    internal static FixtureBrokerState Reserve(
        FixtureArtifactHandoffPlan plan,
        FixtureHandoffProcessIdentity creator,
        FixtureHandoffProcessIdentity issuer,
        FixtureBrokerDeliveryMode delivery,
        IFixtureBrokerAuthority authority,
        TimeProvider clock,
        TimeSpan budget,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(creator);
        ArgumentNullException.ThrowIfNull(issuer);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(clock);
        lifetime.ThrowIfCancellationRequested();
        var creatorIsIssuer = plan.Edge is FixtureHandoffEdge.LauncherToRunner or
            FixtureHandoffEdge.RunnerToOwner or
            FixtureHandoffEdge.RunnerToBootstrapHelper;
        if (!creator.IsValid || !issuer.IsValid || budget <= TimeSpan.Zero ||
            budget > QualificationConstants.ReadinessTimeout ||
            delivery != (plan.Edge == FixtureHandoffEdge.LauncherToRunner
                ? FixtureBrokerDeliveryMode.LocalLauncher
                : FixtureBrokerDeliveryMode.DirectIssuer) ||
            (creator == issuer) != creatorIsIssuer ||
            !authority.AreExpectedCreatorAndIssuer(plan.Edge, creator, issuer) ||
            !authority.IsProcessAlive(creator) || !authority.IsProcessAlive(issuer) ||
            (creator != issuer && delivery == FixtureBrokerDeliveryMode.DirectIssuer &&
                !authority.HasAuthenticatedCreatorSession(creator, issuer)))
            throw Invalid("reservation-rejected");
        long deadline;
        try
        {
            var budgetTicks = checked((long)Math.Ceiling(
                (decimal)budget.Ticks * clock.TimestampFrequency / TimeSpan.TicksPerSecond));
            deadline = checked(clock.GetTimestamp() + budgetTicks);
        }
        catch (OverflowException)
        {
            throw Invalid("reservation-rejected");
        }
        if (!authority.TryReserveLaunch(plan.LaunchId))
            throw Invalid("reservation-rejected");
        return new(plan, creator, issuer, delivery, authority, clock, deadline, lifetime);
    }

    internal void RegisterTarget(
        FixtureBrokerTargetRegistration registration,
        FixtureBrokerSnapshot snapshot,
        ReadOnlySpan<byte> snapshotBytes,
        ReadOnlySpan<byte> snapshotMac,
        ReadOnlySpan<byte> secret)
    {
        lock (_transitionGate)
            RegisterTargetCore(registration, snapshot, snapshotBytes, snapshotMac, secret);
    }

    private void RegisterTargetCore(
        FixtureBrokerTargetRegistration registration,
        FixtureBrokerSnapshot snapshot,
        ReadOnlySpan<byte> snapshotBytes,
        ReadOnlySpan<byte> snapshotMac,
        ReadOnlySpan<byte> secret)
    {
        if (_exitRequested)
            throw Invalid("reservation-stopped");
        if (Phase == FixtureBrokerPhase.Failed)
            throw Invalid("reservation-stopped");
        if (Phase != FixtureBrokerPhase.Reserved)
            throw Reject("target-rejected", FixtureBrokerFailureKind.Assertion);
        if (registration is null)
        {
            SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
            throw Invalid("target-ownership-uncertain");
        }
        var createdTarget = registration.Target;
        if (createdTarget is null || !createdTarget.IsValid ||
            createdTarget == _creator || createdTarget == _issuer)
        {
            SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
            throw Invalid("target-ownership-uncertain");
        }
        // Creation custody is owned before snapshot parsing or any injected observation.
        _ownedTarget = createdTarget;
        _mutated = true;
        if (registration.CreatorSource.Value is 0 or -1 ||
            registration.IssuerCustody.Value is 0 or -1)
        {
            SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
            throw Invalid("target-ownership-uncertain");
        }
        _issuerCustody = registration.IssuerCustody;
        _issuerCustodyPending = true;
        CheckActive(requireTarget: true);
        if (snapshot is null ||
            secret.Length != SecretBytes || snapshot.Parties is null ||
            snapshot.Edge != _plan.Edge || snapshot.Scenario != _plan.Scenario ||
            snapshot.RunNonce != _plan.RunNonce || snapshot.LaunchId != _plan.LaunchId ||
            snapshot.Delivery != _delivery || snapshot.Parties.Creator != _creator ||
            snapshot.Parties.Issuer != _issuer || snapshot.Parties.Target != createdTarget)
            throw Reject("target-rejected", FixtureBrokerFailureKind.Assertion);
        try
        {
            _snapshot = Clone(snapshot);
            var canonical = FixtureBrokerSnapshotCodec.Seal(snapshot, secret);
            if (!CryptographicOperations.FixedTimeEquals(canonical.Bytes, snapshotBytes) ||
                !CryptographicOperations.FixedTimeEquals(canonical.Mac, snapshotMac) ||
                !_authority.IsProcessAlive(snapshot.Parties.Target) ||
                !_authority.IsCreatorRegistrationSourceValid(
                    _creator, registration.CreatorSource, snapshot.Parties.Target) ||
                !_authority.IsIssuerTargetCustodyValid(
                    _issuer, registration.IssuerCustody, snapshot.Parties.Target) ||
                !_authority.IsExpectedBinary(snapshot.Parties.Target, _plan.Receiver) ||
                !_authority.IsExpectedContainment(snapshot.Parties.Target, _plan.Edge) ||
                !_authority.IsSnapshotStillAdmitted(snapshot))
                throw Reject("target-rejected", FixtureBrokerFailureKind.Assertion);
            _snapshotBytes = snapshotBytes.ToArray();
            _snapshotMac = snapshotMac.ToArray();
            _secret = secret.ToArray();
            CheckActive(requireTarget: true);
            Phase = FixtureBrokerPhase.TargetRegistered;
        }
        catch (FixtureBrokerContractException)
        {
            throw;
        }
        catch (Exception)
        {
            throw Reject("target-rejected", FixtureBrokerFailureKind.Infrastructure);
        }
    }

    internal void RecordDelivery(FixtureBrokerDeliveryReceipt receipt)
    {
        lock (_transitionGate)
            RecordDeliveryCore(receipt);
    }

    private void RecordDeliveryCore(FixtureBrokerDeliveryReceipt receipt)
    {
        if (_exitRequested)
            throw Invalid("reservation-stopped");
        if (receipt is null || receipt.TargetHandle is 0 or -1 ||
            !_targetHandles.Add(receipt.TargetHandle))
        {
            SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
            throw Invalid("delivery-custody-uncertain");
        }
        // Once duplication reports a target-local coordinate, cleanup owns it before any
        // fallible identity/rights observation or later policy check.
        _pendingTransferHandles.Add(receipt.TargetHandle);
        CheckActive(requireTarget: true);
        if (Phase is not (FixtureBrokerPhase.TargetRegistered or
                FixtureBrokerPhase.Delivering) || _snapshot is null ||
            _receipts.Count >= _plan.Slots.Count)
            throw Reject("delivery-rejected", FixtureBrokerFailureKind.Assertion);
        var slot = _plan.Slots[_receipts.Count];
        FixtureBrokerDeliveryReceipt observed;
        try
        {
            observed = _authority.ObserveDeliveredHandle(
                _snapshot.Parties.Target, slot.Label, receipt.TargetHandle);
        }
        catch
        {
            throw Reject("delivery-observation-failed",
                FixtureBrokerFailureKind.Infrastructure);
        }
        CheckActive(requireTarget: true);
        if (receipt.Label != slot.Label || observed != receipt ||
            receipt.Kind != slot.Kind || receipt.Rights != slot.Rights ||
            receipt.Identity != SlotIdentity(_snapshot, slot))
            throw Reject("delivery-rejected", FixtureBrokerFailureKind.Assertion);
        _receipts.Add(receipt);
        Phase = _receipts.Count == _plan.Slots.Count
            ? FixtureBrokerPhase.Delivered
            : FixtureBrokerPhase.Delivering;
    }

    internal FixtureBrokerDescriptor SealDelivery()
    {
        lock (_transitionGate)
            return SealDeliveryCore();
    }

    private FixtureBrokerDescriptor SealDeliveryCore()
    {
        CheckActive(requireTarget: true);
        if (Phase != FixtureBrokerPhase.Delivered || _snapshot is null ||
            _snapshotBytes is null || _snapshotMac is null || _secret is null)
            throw Reject("delivery-incomplete", FixtureBrokerFailureKind.Assertion);
        var challenge = RandomNumberGenerator.GetBytes(SecretBytes);
        try
        {
            var unsigned = new FixtureBrokerDescriptor(_plan, _snapshot.Parties, _delivery,
                _receipts, _snapshotBytes, _snapshotMac, _deadlineTimestamp,
                _clock.TimestampFrequency, challenge, new byte[SecretBytes]);
            var transcript = FixtureBrokerCrypto.DescriptorTranscript(unsigned);
            byte[] mac;
            try
            {
                mac = FixtureBrokerCrypto.Authenticate(_secret,
                "WatchDog.fixture.broker.descriptor.v1", transcript);
            }
            finally { CryptographicOperations.ZeroMemory(transcript); }
            try
            {
                _descriptor = new(_plan, _snapshot.Parties, _delivery, _receipts,
                    _snapshotBytes, _snapshotMac, _deadlineTimestamp,
                    _clock.TimestampFrequency, challenge, mac);
                _expectedProof = FixtureBrokerCrypto.Authenticate(_secret,
                    "WatchDog.fixture.broker.child-proof.v1", mac, challenge);
                Phase = FixtureBrokerPhase.Sealed;
                return _descriptor;
            }
            finally { CryptographicOperations.ZeroMemory(mac); }
        }
        finally { CryptographicOperations.ZeroMemory(challenge); }
    }

    internal void MarkResumed()
    {
        lock (_transitionGate)
            MarkResumedCore();
    }

    private void MarkResumedCore()
    {
        CheckActive(requireTarget: true);
        if (Phase != FixtureBrokerPhase.Sealed)
            throw Reject("resume-rejected", FixtureBrokerFailureKind.Assertion);
        Phase = FixtureBrokerPhase.Resumed;
    }

    internal byte[] AcceptChildProof(FixtureBrokerFrameObservation observation)
    {
        lock (_transitionGate)
            return AcceptChildProofCore(observation);
    }

    private byte[] AcceptChildProofCore(FixtureBrokerFrameObservation observation)
    {
        CheckActive(requireTarget: true);
        if (Phase != FixtureBrokerPhase.Resumed || _snapshot is null ||
            _descriptor is null || _secret is null || _expectedProof is null ||
            !ValidFrame(observation, _snapshot.Parties.Target, _expectedProof))
            throw Reject("child-proof-rejected", FixtureBrokerFailureKind.Assertion);
        CheckActive(requireTarget: true);
        var nonce = RandomNumberGenerator.GetBytes(SecretBytes);
        try
        {
            var grantMac = FixtureBrokerCrypto.Authenticate(_secret,
                "WatchDog.fixture.broker.parent-grant.v1", _expectedProof, nonce);
            try
            {
                var grant = new byte[SecretBytes * 2];
                nonce.CopyTo(grant, 0);
                grantMac.CopyTo(grant, SecretBytes);
                _expectedGrantAck = FixtureBrokerCrypto.Authenticate(_secret,
                    "WatchDog.fixture.broker.grant-ack.v1", grantMac, nonce);
                Phase = FixtureBrokerPhase.GrantIssued;
                return grant;
            }
            finally { CryptographicOperations.ZeroMemory(grantMac); }
        }
        finally { CryptographicOperations.ZeroMemory(nonce); }
    }

    internal void AcceptGrantAck(FixtureBrokerFrameObservation observation)
    {
        lock (_transitionGate)
            AcceptGrantAckCore(observation);
    }

    private void AcceptGrantAckCore(FixtureBrokerFrameObservation observation)
    {
        CheckActive(requireTarget: true);
        if (Phase != FixtureBrokerPhase.GrantIssued || _snapshot is null ||
            _expectedGrantAck is null ||
            !ValidFrame(observation, _snapshot.Parties.Target, _expectedGrantAck))
            throw Reject("grant-ack-rejected", FixtureBrokerFailureKind.Assertion);
        CheckActive(requireTarget: true);
        Phase = FixtureBrokerPhase.Committed;
    }

    internal byte[] IssueActivation()
    {
        lock (_transitionGate)
            return IssueActivationCore();
    }

    private byte[] IssueActivationCore()
    {
        CheckActive(requireTarget: true);
        if (Phase != FixtureBrokerPhase.Committed || _descriptor is null ||
            _secret is null || _expectedGrantAck is null)
            throw Reject("activation-rejected", FixtureBrokerFailureKind.Assertion);
        var nonce = RandomNumberGenerator.GetBytes(SecretBytes);
        try
        {
            var descriptorMac = _descriptor.Mac;
            byte[] activationMac;
            try
            {
                activationMac = FixtureBrokerCrypto.Authenticate(_secret,
                    "WatchDog.fixture.broker.activate.v1", descriptorMac,
                    _expectedGrantAck, nonce);
            }
            finally { CryptographicOperations.ZeroMemory(descriptorMac); }
            try
            {
                var activation = new byte[SecretBytes * 2];
                nonce.CopyTo(activation, 0);
                activationMac.CopyTo(activation, SecretBytes);
                _expectedActivationAck = FixtureBrokerCrypto.Authenticate(_secret,
                    "WatchDog.fixture.broker.active-ack.v1", activationMac, nonce);
                Phase = FixtureBrokerPhase.ActivationIssued;
                return activation;
            }
            finally { CryptographicOperations.ZeroMemory(activationMac); }
        }
        finally { CryptographicOperations.ZeroMemory(nonce); }
    }

    internal void ConfirmChildActive(FixtureBrokerFrameObservation observation)
    {
        lock (_transitionGate)
            ConfirmChildActiveCore(observation);
    }

    private void ConfirmChildActiveCore(FixtureBrokerFrameObservation observation)
    {
        CheckActive(requireTarget: true);
        if (Phase != FixtureBrokerPhase.ActivationIssued || _snapshot is null ||
            _expectedActivationAck is null ||
            !ValidFrame(observation, _snapshot.Parties.Target, _expectedActivationAck))
            throw Reject("activation-ack-rejected", FixtureBrokerFailureKind.Assertion);
        CheckActive(requireTarget: true);
        Phase = FixtureBrokerPhase.Active;
    }

    internal void ArmOwnerDeath(FixtureBrokerOwnerDeathEvidence evidence)
    {
        lock (_transitionGate)
            ArmOwnerDeathCore(evidence);
    }

    private void ArmOwnerDeathCore(FixtureBrokerOwnerDeathEvidence evidence)
    {
        CheckActive(requireTarget: true);
        if (Phase != FixtureBrokerPhase.Active || _snapshot is null || evidence is null ||
            _plan.Edge != FixtureHandoffEdge.HelperToDescendant ||
            _plan.Scenario != QualificationScenario.OwnerDeath ||
            !evidence.NoMoreSpawn || !evidence.ExactMembership ||
            !evidence.OutputsReconciled || evidence.RunnerHoldsInnerJobHandle ||
            evidence.PendingInnerJobTransfer)
            throw Reject("owner-death-rejected", FixtureBrokerFailureKind.Assertion);
        try
        {
            if (!_authority.IsSnapshotStillAdmitted(_snapshot) ||
                !_authority.IsOwnerDeathTopologySafe(_snapshot, evidence))
                throw Reject("owner-death-rejected", FixtureBrokerFailureKind.Assertion);
        }
        catch (FixtureBrokerContractException)
        {
            throw;
        }
        catch
        {
            throw Reject("owner-death-observation-failed",
                FixtureBrokerFailureKind.Infrastructure);
        }
        _ownerDeathEvidence = evidence;
        OwnerDeathArmed = true;
    }

    internal void Fail(FixtureBrokerFailureKind kind)
    {
        lock (_transitionGate)
        {
            if (kind is FixtureBrokerFailureKind.None)
                throw new ArgumentOutOfRangeException(nameof(kind));
            SetFailure(kind == FixtureBrokerFailureKind.Prerequisite && _mutated
                ? FixtureBrokerFailureKind.Assertion : kind);
        }
    }

    internal void ConfirmTransferClosed(nint targetHandle)
    {
        lock (_transitionGate)
        {
            _cleanupStarted = true;
            if (_ownedTarget is null || !_pendingTransferHandles.Contains(targetHandle))
                throw Invalid("close-rejected");
            FixtureArtifactCloseStatus status;
            try { status = _authority.ObserveTransferClose(_ownedTarget, targetHandle); }
            catch
            {
                SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
                throw Invalid("close-observation-failed");
            }
            if (status == FixtureArtifactCloseStatus.Closed)
                _pendingTransferHandles.Remove(targetHandle);
            else if (status == FixtureArtifactCloseStatus.Ambiguous)
                SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
        }
    }

    internal void ConfirmIssuerCustodyClosed()
    {
        lock (_transitionGate)
        {
            _cleanupStarted = true;
            if (_issuerCustody is null || !_issuerCustodyPending)
                throw Invalid("issuer-custody-close-rejected");
            FixtureArtifactCloseStatus status;
            try
            {
                status = _authority.ObserveIssuerCustodyClose(
                    _issuer, _issuerCustody.Value);
            }
            catch
            {
                SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
                throw Invalid("issuer-custody-close-observation-failed");
            }
            if (status == FixtureArtifactCloseStatus.Closed)
                _issuerCustodyPending = false;
            else if (status == FixtureArtifactCloseStatus.Ambiguous)
                SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
        }
    }

    internal void ConfirmExactTargetReaped(FixtureHandoffProcessIdentity target)
    {
        lock (_transitionGate)
        {
            _cleanupStarted = true;
            try
            {
                if (_ownedTarget is null || target != _ownedTarget ||
                    !_authority.IsExactTargetReaped(target))
                    throw Invalid("target-reap-uncertain");
            }
            catch
            {
                SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
                throw;
            }
            _targetReaped = true;
        }
    }

    internal void ConfirmObserversDrained()
    {
        lock (_transitionGate)
        {
            _cleanupStarted = true;
            try
            {
                if (!_authority.AreObserversDrained(_plan.LaunchId))
                    throw Invalid("observers-not-drained");
            }
            catch
            {
                SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
                throw;
            }
            _observersDrained = true;
        }
    }

    internal void ConfirmArtifactOperationsDrained()
    {
        lock (_transitionGate)
        {
            _cleanupStarted = true;
            try
            {
                if (!_authority.AreArtifactOperationsDrained(_plan.LaunchId))
                    throw Invalid("artifact-operations-not-drained");
            }
            catch
            {
                SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
                throw;
            }
            _artifactOperationsDrained = true;
        }
    }

    internal bool CleanupConfirmed
    {
        get
        {
            lock (_transitionGate)
                return CleanupConfirmedCore();
        }
    }

    internal QualificationExitCode ExitAfterCleanup(QualificationExitCode requested)
    {
        lock (_transitionGate)
        {
            if (_exitRequested)
                return _terminalExitCode ?? QualificationExitCode.CleanupIncomplete;
            try
            {
                var result = ExitAfterCleanupCore(requested);
                _terminalExitCode = result;
                return result;
            }
            finally
            {
                _exitRequested = true;
                ClearSensitiveBuffers();
            }
        }
    }

    private QualificationExitCode ExitAfterCleanupCore(QualificationExitCode requested)
    {
        if (_mutated && !CleanupConfirmedCore())
            return QualificationExitCode.CleanupIncomplete;
        if (_mutated)
        {
            try
            {
                if (_ownedTarget is null || !_authority.IsExactTargetReaped(_ownedTarget) ||
                    _issuerCustody is null ||
                    _authority.ObserveIssuerCustodyClose(_issuer, _issuerCustody.Value) !=
                        FixtureArtifactCloseStatus.Closed ||
                    !_authority.AreObserversDrained(_plan.LaunchId) ||
                    !_authority.AreArtifactOperationsDrained(_plan.LaunchId) ||
                    _targetHandles.Any(handle =>
                        _authority.ObserveTransferClose(_ownedTarget, handle) !=
                            FixtureArtifactCloseStatus.Closed))
                    SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
            }
            catch { SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous); }
        }
        if (!CleanupConfirmedCore())
            return QualificationExitCode.CleanupIncomplete;
        if (requested == QualificationExitCode.Pass && _snapshot is not null)
        {
            try
            {
                if (!_authority.IsSnapshotStillAdmitted(_snapshot))
                    SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
            }
            catch
            {
                SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
            }
            if (OwnerDeathArmed)
            {
                try
                {
                    if (_ownerDeathEvidence is null ||
                        !_authority.IsOwnerDeathTopologySafe(
                            _snapshot, _ownerDeathEvidence))
                        SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous);
                }
                catch { SetFailure(FixtureBrokerFailureKind.CleanupAmbiguous); }
            }
        }
        if (!CleanupConfirmedCore())
            return QualificationExitCode.CleanupIncomplete;
        if (_failure != FixtureBrokerFailureKind.None)
            return ExitForFailure(_failure);
        if (requested == QualificationExitCode.Pass &&
            (Phase != FixtureBrokerPhase.Active ||
                (_plan.Edge == FixtureHandoffEdge.HelperToDescendant &&
                    _plan.Scenario == QualificationScenario.OwnerDeath &&
                    !OwnerDeathArmed)))
            return QualificationExitCode.AssertionFailedCleanupConfirmed;
        if (_mutated && requested == QualificationExitCode.UsageOrPrerequisite)
            return QualificationExitCode.AssertionFailedCleanupConfirmed;
        return requested;
    }

    private bool CleanupConfirmedCore() => !_uncertain &&
        (!_mutated || (_targetReaped && _observersDrained &&
            _artifactOperationsDrained && !_issuerCustodyPending &&
            _pendingTransferHandles.Count == 0));

    private bool ValidFrame(
        FixtureBrokerFrameObservation observation,
        FixtureHandoffProcessIdentity expectedPeer,
        byte[] expectedFrame)
    {
        try
        {
            return observation is not null && observation.ConnectionReceipt > 0 &&
                observation.Frame is not null &&
                observation.Frame.Length == expectedFrame.Length &&
                _authority.ObserveConnectedPeer(observation.ConnectionReceipt) == expectedPeer &&
                CryptographicOperations.FixedTimeEquals(expectedFrame, observation.Frame);
        }
        catch
        {
            throw Reject("peer-observation-failed",
                FixtureBrokerFailureKind.Infrastructure);
        }
    }

    private void CheckActive(bool requireTarget)
    {
        if (_exitRequested)
            throw Invalid("reservation-stopped");
        if (Phase == FixtureBrokerPhase.Failed)
            throw Invalid("reservation-stopped");
        if (_cleanupStarted)
            throw Reject("cleanup-started", FixtureBrokerFailureKind.Assertion);
        if (_lifetime.IsCancellationRequested)
        {
            SetFailure(FixtureBrokerFailureKind.Assertion);
            throw new OperationCanceledException(_lifetime);
        }
        if (Phase != FixtureBrokerPhase.Active &&
            (_clock.TimestampFrequency <= 0 || _clock.GetTimestamp() >= _deadlineTimestamp))
            throw Reject("deadline-expired", FixtureBrokerFailureKind.Deadline);
        try
        {
            if (!_authority.IsProcessAlive(_creator) || !_authority.IsProcessAlive(_issuer) ||
                (requireTarget && (_ownedTarget is null ||
                    !_authority.IsProcessAlive(_ownedTarget))) ||
                (_creator != _issuer && _delivery == FixtureBrokerDeliveryMode.DirectIssuer &&
                    !_authority.HasAuthenticatedCreatorSession(_creator, _issuer)))
                throw Reject("authority-lost", FixtureBrokerFailureKind.Infrastructure);
        }
        catch (FixtureBrokerContractException)
        {
            throw;
        }
        catch
        {
            throw Reject("authority-lost", FixtureBrokerFailureKind.Infrastructure);
        }
    }

    private FixtureBrokerContractException Reject(
        string suffix, FixtureBrokerFailureKind kind)
    {
        SetFailure(kind);
        return Invalid(suffix);
    }

    private void SetFailure(FixtureBrokerFailureKind kind)
    {
        if (FailureRank(kind) > FailureRank(_failure))
            _failure = kind;
        if (kind == FixtureBrokerFailureKind.CleanupAmbiguous)
            _uncertain = true;
        Phase = FixtureBrokerPhase.Failed;
        ClearSensitiveBuffers();
    }

    private void ClearSensitiveBuffers()
    {
        if (_secret is not null)
            CryptographicOperations.ZeroMemory(_secret);
        if (_expectedProof is not null)
            CryptographicOperations.ZeroMemory(_expectedProof);
        if (_expectedGrantAck is not null)
            CryptographicOperations.ZeroMemory(_expectedGrantAck);
        if (_expectedActivationAck is not null)
            CryptographicOperations.ZeroMemory(_expectedActivationAck);
    }

    private static int FailureRank(FixtureBrokerFailureKind kind) => kind switch
    {
        FixtureBrokerFailureKind.CleanupAmbiguous => 5,
        FixtureBrokerFailureKind.Deadline => 4,
        FixtureBrokerFailureKind.Assertion => 3,
        FixtureBrokerFailureKind.Infrastructure => 2,
        FixtureBrokerFailureKind.Prerequisite => 1,
        _ => 0,
    };

    private static QualificationExitCode ExitForFailure(FixtureBrokerFailureKind kind) =>
        kind switch
        {
            FixtureBrokerFailureKind.CleanupAmbiguous =>
                QualificationExitCode.CleanupIncomplete,
            FixtureBrokerFailureKind.Deadline =>
                QualificationExitCode.TimeoutCleanupConfirmed,
            FixtureBrokerFailureKind.Assertion =>
                QualificationExitCode.AssertionFailedCleanupConfirmed,
            FixtureBrokerFailureKind.Infrastructure =>
                QualificationExitCode.InfrastructureExceptionCleanupConfirmed,
            FixtureBrokerFailureKind.Prerequisite =>
                QualificationExitCode.UsageOrPrerequisite,
            _ => QualificationExitCode.AssertionFailedCleanupConfirmed,
        };

    private static FixtureArtifactIdentity? SlotIdentity(
        FixtureBrokerSnapshot snapshot, FixtureHandoffSlot slot)
    {
        if (slot.Kind == FixtureHandoffHandleKind.Directory)
            return snapshot.Directory;
        if (slot.Kind != FixtureHandoffHandleKind.File)
            return null;
        var separator = slot.Label.IndexOf(':');
        if (separator < 0)
            throw Invalid("delivery-rejected");
        var name = slot.Label[(separator + 1)..];
        return snapshot.Outputs.SingleOrDefault(output => output.Name == name)?.Identity ??
            throw Invalid("delivery-rejected");
    }

    private static FixtureBrokerSnapshot Clone(FixtureBrokerSnapshot snapshot) => new(
        snapshot.Edge, snapshot.Scenario, snapshot.RunNonce, snapshot.LaunchId,
        new(snapshot.Parties.Creator, snapshot.Parties.Issuer, snapshot.Parties.Target),
        snapshot.Delivery, snapshot.Directory,
        new ReadOnlyCollection<FixtureBrokerOutput>(snapshot.Outputs.ToArray()));

    private static FixtureBrokerContractException Invalid(string suffix) =>
        new($"fixture.broker.{suffix}");
}

/// <summary>Child-side fence. Grant acceptance produces an ACK but never write authority.</summary>
internal sealed class FixtureBrokerChildState
{
    private const int SecretBytes = 32;
    private readonly FixtureBrokerDescriptor _descriptor;
    private readonly FixtureBrokerSnapshot _snapshot;
    private readonly IFixtureBrokerChildLocalObserver _observer;
    private readonly IFixtureBrokerAuthority _authority;
    private readonly TimeProvider _clock;
    private readonly CancellationToken _lifetime;
    private readonly byte[] _secret;
    private readonly byte[] _proof;
    private readonly object _transitionGate = new();
    private FixtureBrokerChildPhase _phase =
        FixtureBrokerChildPhase.DescriptorAccepted;
    private byte[]? _grantAck;
    private byte[]? _activationAck;

    private FixtureBrokerChildState(
        FixtureBrokerDescriptor descriptor,
        FixtureBrokerSnapshot snapshot,
        IFixtureBrokerChildLocalObserver observer,
        IFixtureBrokerAuthority authority,
        TimeProvider clock,
        CancellationToken lifetime,
        ReadOnlySpan<byte> secret,
        byte[] proof)
    {
        _descriptor = descriptor;
        _snapshot = snapshot;
        _observer = observer;
        _authority = authority;
        _clock = clock;
        _lifetime = lifetime;
        _secret = secret.ToArray();
        _proof = (byte[])proof.Clone();
    }

    internal FixtureBrokerChildPhase Phase
    {
        get
        {
            lock (_transitionGate)
                return _phase;
        }
    }

    internal static FixtureBrokerChildState Accept(
        FixtureBrokerDescriptor descriptor,
        FixtureArtifactHandoffPlan expectedPlan,
        FixtureBrokerParties expectedParties,
        ReadOnlySpan<byte> secret,
        IFixtureBrokerChildLocalObserver observer,
        IFixtureBrokerAuthority authority,
        TimeProvider clock,
        CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(expectedPlan);
        ArgumentNullException.ThrowIfNull(expectedParties);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(clock);
        lifetime.ThrowIfCancellationRequested();
        if (secret.Length != SecretBytes || descriptor.TimestampFrequency <= 0 ||
            descriptor.TimestampFrequency != clock.TimestampFrequency ||
            clock.GetTimestamp() >= descriptor.DeadlineTimestamp ||
            descriptor.Plan.Edge != expectedPlan.Edge ||
            descriptor.Plan.Scenario != expectedPlan.Scenario ||
            descriptor.Plan.RunNonce != expectedPlan.RunNonce ||
            descriptor.Plan.LaunchId != expectedPlan.LaunchId ||
            descriptor.Parties != expectedParties)
            throw Invalid("descriptor-rejected");
        try
        {
            var snapshotBytes = descriptor.SnapshotBytes;
            var snapshotMac = descriptor.SnapshotMac;
            var snapshot = FixtureBrokerSnapshotCodec.Open(snapshotBytes, snapshotMac,
                secret, expectedPlan, expectedParties, observer);
            ValidateReceipts(expectedPlan, snapshot, descriptor.Receipts, observer);
            if (!PartiesAlive(snapshot.Parties, authority))
                throw Invalid("descriptor-rejected");
            lifetime.ThrowIfCancellationRequested();
            if (clock.GetTimestamp() >= descriptor.DeadlineTimestamp)
                throw Invalid("descriptor-rejected");
            var transcript = FixtureBrokerCrypto.DescriptorTranscript(descriptor);
            byte[] expectedMac;
            try
            {
                expectedMac = FixtureBrokerCrypto.Authenticate(secret,
                    "WatchDog.fixture.broker.descriptor.v1", transcript);
            }
            finally { CryptographicOperations.ZeroMemory(transcript); }
            var descriptorMac = descriptor.Mac;
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedMac, descriptorMac))
                    throw Invalid("descriptor-rejected");
                var challenge = descriptor.Challenge;
                try
                {
                    var proof = FixtureBrokerCrypto.Authenticate(secret,
                        "WatchDog.fixture.broker.child-proof.v1", descriptorMac, challenge);
                    try
                    {
                        return new(descriptor, snapshot, observer, authority, clock,
                        lifetime, secret, proof);
                    }
                    finally { CryptographicOperations.ZeroMemory(proof); }
                }
                finally { CryptographicOperations.ZeroMemory(challenge); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedMac);
                CryptographicOperations.ZeroMemory(descriptorMac);
            }
        }
        catch (FixtureBrokerContractException)
        {
            throw;
        }
        catch
        {
            throw Invalid("descriptor-rejected");
        }
    }

    internal byte[] Proof
    {
        get
        {
            lock (_transitionGate)
            {
                CheckActive(FixtureBrokerChildPhase.DescriptorAccepted);
                return (byte[])_proof.Clone();
            }
        }
    }

    internal byte[] GrantAck
    {
        get
        {
            lock (_transitionGate)
            {
                CheckActive(FixtureBrokerChildPhase.GrantAccepted);
                return _grantAck is null ? throw Invalid("grant-not-accepted") :
                    (byte[])_grantAck.Clone();
            }
        }
    }

    internal byte[] ActivationAck
    {
        get
        {
            lock (_transitionGate)
            {
                CheckActive(FixtureBrokerChildPhase.Active);
                return _activationAck is null ? throw Invalid("activation-not-accepted") :
                    (byte[])_activationAck.Clone();
            }
        }
    }

    internal void AcceptGrant(FixtureBrokerFrameObservation observation)
    {
        lock (_transitionGate)
            AcceptGrantCore(observation);
    }

    private void AcceptGrantCore(FixtureBrokerFrameObservation observation)
    {
        CheckActive(FixtureBrokerChildPhase.DescriptorAccepted);
        if (!ValidPeer(observation, _snapshot.Parties.Issuer) ||
            observation.Frame.Length != SecretBytes * 2)
            throw Stop("grant-rejected");
        CheckActive(FixtureBrokerChildPhase.DescriptorAccepted);
        var nonce = observation.Frame.AsSpan(0, SecretBytes);
        var grantMac = observation.Frame.AsSpan(SecretBytes, SecretBytes);
        var expected = FixtureBrokerCrypto.Authenticate(_secret,
            "WatchDog.fixture.broker.parent-grant.v1", _proof, nonce.ToArray());
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expected, grantMac))
                throw Stop("grant-rejected");
            _grantAck = FixtureBrokerCrypto.Authenticate(_secret,
                "WatchDog.fixture.broker.grant-ack.v1", grantMac.ToArray(),
                nonce.ToArray());
            _phase = FixtureBrokerChildPhase.GrantAccepted;
        }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    internal void AcceptActivation(FixtureBrokerFrameObservation observation)
    {
        lock (_transitionGate)
            AcceptActivationCore(observation);
    }

    private void AcceptActivationCore(FixtureBrokerFrameObservation observation)
    {
        CheckActive(FixtureBrokerChildPhase.GrantAccepted);
        if (_grantAck is null || !ValidPeer(observation, _snapshot.Parties.Issuer) ||
            observation.Frame.Length != SecretBytes * 2)
            throw Stop("activation-rejected");
        CheckActive(FixtureBrokerChildPhase.GrantAccepted);
        var nonce = observation.Frame.AsSpan(0, SecretBytes);
        var activationMac = observation.Frame.AsSpan(SecretBytes, SecretBytes);
        var descriptorMac = _descriptor.Mac;
        byte[] expected;
        try
        {
            expected = FixtureBrokerCrypto.Authenticate(_secret,
                "WatchDog.fixture.broker.activate.v1", descriptorMac, _grantAck,
                nonce.ToArray());
        }
        finally { CryptographicOperations.ZeroMemory(descriptorMac); }
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expected, activationMac))
                throw Stop("activation-rejected");
            _activationAck = FixtureBrokerCrypto.Authenticate(_secret,
                "WatchDog.fixture.broker.active-ack.v1", activationMac.ToArray(),
                nonce.ToArray());
            _phase = FixtureBrokerChildPhase.Active;
        }
        finally { CryptographicOperations.ZeroMemory(expected); }
    }

    internal bool MayWrite
    {
        get
        {
            lock (_transitionGate)
                return RemainsActive();
        }
    }

    internal bool MayLaunch
    {
        get
        {
            lock (_transitionGate)
                return RemainsActive();
        }
    }

    private bool RemainsActive()
    {
        if (_phase != FixtureBrokerChildPhase.Active)
        {
            if (ExpiredOrAuthorityLost())
                StopWithoutThrow();
            return false;
        }
        if (ExpiredOrAuthorityLost())
        {
            StopWithoutThrow();
            return false;
        }
        return true;
    }

    private void CheckActive(FixtureBrokerChildPhase required)
    {
        if (_phase == FixtureBrokerChildPhase.Failed)
            throw Invalid("child-stopped");
        if (ExpiredOrAuthorityLost())
            throw Stop("child-stopped");
        if (_phase != required)
            throw Stop("phase-rejected");
    }

    private bool ExpiredOrAuthorityLost()
    {
        try
        {
            return _lifetime.IsCancellationRequested ||
                _clock.TimestampFrequency != _descriptor.TimestampFrequency ||
                (_phase != FixtureBrokerChildPhase.Active &&
                    _clock.GetTimestamp() >= _descriptor.DeadlineTimestamp) ||
                !PartiesAlive(_snapshot.Parties, _authority) ||
                !ChildLocalAuthorityValid();
        }
        catch { return true; }
    }

    private bool ValidPeer(
        FixtureBrokerFrameObservation observation,
        FixtureHandoffProcessIdentity expected)
    {
        try
        {
            return observation is not null && observation.ConnectionReceipt > 0 &&
                observation.Frame is not null &&
                _authority.ObserveConnectedPeer(observation.ConnectionReceipt) == expected;
        }
        catch
        {
            throw Stop("peer-observation-failed");
        }
    }

    private FixtureBrokerContractException Stop(string suffix)
    {
        StopWithoutThrow();
        return Invalid(suffix);
    }

    private void StopWithoutThrow()
    {
        _phase = FixtureBrokerChildPhase.Failed;
        CryptographicOperations.ZeroMemory(_secret);
        CryptographicOperations.ZeroMemory(_proof);
        if (_grantAck is not null)
            CryptographicOperations.ZeroMemory(_grantAck);
        if (_activationAck is not null)
            CryptographicOperations.ZeroMemory(_activationAck);
    }

    private static bool PartiesAlive(
        FixtureBrokerParties parties, IFixtureBrokerAuthority authority) =>
        authority.IsProcessAlive(parties.Creator) &&
        authority.IsProcessAlive(parties.Issuer) &&
        authority.IsProcessAlive(parties.Target);

    private static void ValidateReceipts(
        FixtureArtifactHandoffPlan plan,
        FixtureBrokerSnapshot snapshot,
        IReadOnlyList<FixtureBrokerDeliveryReceipt> receipts,
        IFixtureBrokerChildLocalObserver observer)
    {
        if (receipts.Count != plan.Slots.Count)
            throw Invalid("descriptor-rejected");
        var handles = new HashSet<nint>();
        for (var index = 0; index < receipts.Count; index++)
        {
            var receipt = receipts[index];
            var slot = plan.Slots[index];
            if (receipt is null || receipt.Label != slot.Label ||
                receipt.TargetHandle is 0 or -1 || !handles.Add(receipt.TargetHandle) ||
                receipt.Kind != slot.Kind || receipt.Rights != slot.Rights ||
                receipt.Identity != SlotIdentity(snapshot, slot) ||
                observer.ObserveDeliveredSlot(slot.Label).RawValue != receipt.TargetHandle)
                throw Invalid("descriptor-rejected");
        }
    }

    private bool ChildLocalAuthorityValid()
    {
        try
        {
            var snapshot = FixtureBrokerSnapshotCodec.Open(
                _descriptor.SnapshotBytes, _descriptor.SnapshotMac, _secret,
                _descriptor.Plan, _descriptor.Parties, _observer);
            ValidateReceipts(_descriptor.Plan, snapshot, _descriptor.Receipts, _observer);
            return true;
        }
        catch { return false; }
    }

    private static FixtureArtifactIdentity? SlotIdentity(
        FixtureBrokerSnapshot snapshot, FixtureHandoffSlot slot)
    {
        if (slot.Kind == FixtureHandoffHandleKind.Directory)
            return snapshot.Directory;
        if (slot.Kind != FixtureHandoffHandleKind.File)
            return null;
        var separator = slot.Label.IndexOf(':');
        if (separator < 0)
            throw Invalid("descriptor-rejected");
        var name = slot.Label[(separator + 1)..];
        return snapshot.Outputs.SingleOrDefault(output => output.Name == name)?.Identity ??
            throw Invalid("descriptor-rejected");
    }

    private static FixtureBrokerContractException Invalid(string suffix) =>
        new($"fixture.broker.{suffix}");
}

internal static class FixtureBrokerCrypto
{
    internal static byte[] Authenticate(
        ReadOnlySpan<byte> secret, string domain, params byte[][] parts)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            var domainBytes = Encoding.UTF8.GetBytes(domain);
            writer.Write(domainBytes.Length);
            writer.Write(domainBytes);
            foreach (var part in parts)
            {
                writer.Write(part.Length);
                writer.Write(part);
            }
        }
        var message = stream.ToArray();
        try { return HMACSHA256.HashData(secret, message); }
        finally { CryptographicOperations.ZeroMemory(message); }
    }

    internal static byte[] DescriptorTranscript(FixtureBrokerDescriptor descriptor)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write((int)descriptor.Plan.Edge);
        writer.Write(descriptor.Plan.Scenario is null ? -1 :
            (int)descriptor.Plan.Scenario.Value);
        writer.Write(descriptor.Plan.RunNonce.ToByteArray());
        writer.Write(descriptor.Plan.LaunchId.ToByteArray());
        WriteProcess(writer, descriptor.Parties.Creator);
        WriteProcess(writer, descriptor.Parties.Issuer);
        WriteProcess(writer, descriptor.Parties.Target);
        writer.Write((byte)descriptor.Delivery);
        writer.Write(descriptor.DeadlineTimestamp);
        writer.Write(descriptor.TimestampFrequency);
        var snapshotBytes = descriptor.SnapshotBytes;
        var snapshotMac = descriptor.SnapshotMac;
        var challenge = descriptor.Challenge;
        writer.Write(snapshotBytes.Length);
        writer.Write(snapshotBytes);
        writer.Write(snapshotMac.Length);
        writer.Write(snapshotMac);
        writer.Write(descriptor.Receipts.Count);
        foreach (var receipt in descriptor.Receipts)
        {
            writer.Write(receipt.Label);
            writer.Write((long)receipt.TargetHandle);
            writer.Write((byte)receipt.Kind);
            writer.Write((uint)receipt.Rights);
            writer.Write(receipt.Identity is not null);
            if (receipt.Identity is not null)
                WriteIdentity(writer, receipt.Identity);
        }
        writer.Write(challenge.Length);
        writer.Write(challenge);
        return stream.ToArray();
    }

    private static void WriteProcess(
        BinaryWriter writer, FixtureHandoffProcessIdentity process)
    {
        writer.Write(process.ProcessId);
        writer.Write(process.CreationFileTime);
    }

    private static void WriteIdentity(BinaryWriter writer, FixtureArtifactIdentity identity)
    {
        writer.Write(identity.Volume);
        writer.Write(identity.FileIdLow);
        writer.Write(identity.FileIdHigh);
        writer.Write((byte)identity.Kind);
        writer.Write(identity.IsReparse);
    }
}
