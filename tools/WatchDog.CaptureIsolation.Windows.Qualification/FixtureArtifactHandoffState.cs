using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal sealed class FixtureHandoffDescriptor
{
    private readonly byte[] _challenge;
    private readonly byte[] _mac;

    internal FixtureHandoffDescriptor(
        FixtureArtifactHandoffPlan plan, FixtureArtifactHandoffLedger ledger,
        FixtureHandoffBinding binding, IReadOnlyList<FixtureHandoffObservedHandle> handles,
        byte[] challenge, byte[] mac)
    {
        Plan = plan;
        Ledger = ledger;
        Binding = binding;
        Handles = new ReadOnlyCollection<FixtureHandoffObservedHandle>(handles.ToArray());
        _challenge = (byte[])challenge.Clone();
        _mac = (byte[])mac.Clone();
    }

    internal FixtureArtifactHandoffPlan Plan { get; }
    internal FixtureArtifactHandoffLedger Ledger { get; }
    internal FixtureHandoffBinding Binding { get; }
    internal IReadOnlyList<FixtureHandoffObservedHandle> Handles { get; }
    internal byte[] Challenge => (byte[])_challenge.Clone();
    internal byte[] Mac => (byte[])_mac.Clone();
}

internal sealed record FixtureHandoffChildProofObservation(
    FixtureHandoffProcessIdentity Peer, byte[] Proof);

// Future IPC adapter must supply OS-derived endpoint identity and a bounded proof frame.
// B0 fake sources let tests distinguish child-origin evidence from a parent-held MAC.
internal interface IFixtureHandoffChildProofSource
{
    FixtureHandoffChildProofObservation Receive(FixtureHandoffDescriptor expectedDescriptor);
}

internal sealed record FixtureHandoffParentGrantObservation(
    FixtureHandoffProcessIdentity Peer, byte[] Grant);

// A child must observe the grant on a channel whose peer is OS-bound to its exact parent.
internal interface IFixtureHandoffParentGrantSource
{
    FixtureHandoffParentGrantObservation Receive(FixtureHandoffDescriptor expectedDescriptor);
}

internal sealed record FixtureHandoffChildGrantAckObservation(
    FixtureHandoffProcessIdentity Peer, byte[] Ack);

internal interface IFixtureHandoffChildGrantAckSource
{
    FixtureHandoffChildGrantAckObservation Receive(FixtureHandoffDescriptor expectedDescriptor);
}

/// <summary>
/// Pure validator for a frozen, labeled handle set. The two probes used by the parent and
/// child must be independent; neither this interface nor a MAC proves native inheritance.
/// </summary>
internal static class FixtureHandoffEvidence
{
    internal static IReadOnlyList<FixtureHandoffObservedHandle> Validate(
        FixtureArtifactHandoffPlan plan, FixtureArtifactHandoffLedger ledger,
        IFixtureHandoffProbe probe, FixtureHandoffProcessIdentity expectedParent,
        IReadOnlyList<FixtureHandoffClaim> claims)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(claims);
        if (ledger.Scenario != plan.Scenario || !ledger.IsStillAdmitted ||
            claims.Count != plan.Slots.Count ||
            !expectedParent.IsValid)
            throw Invalid("plan-invalid");
        var raw = new HashSet<nint>();
        var observed = new List<FixtureHandoffObservedHandle>(claims.Count);
        nint directoryHandle = 0;
        for (var index = 0; index < claims.Count; index++)
        {
            var claim = claims[index];
            var slot = plan.Slots[index];
            if (claim is null || claim.Label != slot.Label ||
                claim.RawValue is 0 or -1 || !raw.Add(claim.RawValue))
                throw Invalid("plan-invalid");
            var handle = probe.ObserveHandle(claim.RawValue);
            if (handle.RawValue != claim.RawValue || handle.Kind != slot.Kind ||
                handle.Rights != slot.Rights)
                throw Invalid("identity-mismatch");
            if (slot.Kind == FixtureHandoffHandleKind.Process)
            {
                if (handle.Identity is not null || !probe.IsProcessAlive(claim.RawValue) ||
                    probe.ObserveProcess(claim.RawValue) != expectedParent)
                    throw Invalid("parent-mismatch");
            }
            else if (slot.Kind == FixtureHandoffHandleKind.Directory)
            {
                if (slot.Label != "artifact-directory" || handle.Identity != ledger.Directory)
                    throw Invalid("identity-mismatch");
                directoryHandle = claim.RawValue;
            }
            else if (slot.Kind == FixtureHandoffHandleKind.Section)
            {
                if (handle.Identity is not null)
                    throw Invalid("identity-mismatch");
            }
            else
            {
                var name = slot.Label.StartsWith("write:", StringComparison.Ordinal)
                    ? slot.Label[6..] : slot.Label.StartsWith("read:", StringComparison.Ordinal)
                        ? slot.Label[5..] : null;
                if (name is null || !ledger.Entries.TryGetValue(name, out var entry) ||
                    handle.Identity != entry.Identity || directoryHandle is 0 or -1 ||
                    !probe.IsRegisteredChild(directoryHandle, name, entry.Identity) ||
                    (slot.Label.StartsWith("write:", StringComparison.Ordinal) &&
                        (entry.Writer != plan.Receiver || entry.WriterRights != slot.Rights)) ||
                    (slot.Label.StartsWith("read:", StringComparison.Ordinal) &&
                        slot.Rights != FixtureHandoffRights.FileRead))
                    throw Invalid("identity-mismatch");
            }
            observed.Add(handle);
        }
        return new ReadOnlyCollection<FixtureHandoffObservedHandle>(observed);
    }

    internal static byte[] Transcript(FixtureHandoffDescriptor descriptor)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write("WatchDog.fixture-artifact-descriptor.v1");
        writer.Write((int)descriptor.Binding.Edge);
        writer.Write(descriptor.Binding.Scenario is null ? -1 :
            (int)descriptor.Binding.Scenario.Value);
        writer.Write(descriptor.Binding.RunNonce.ToByteArray());
        writer.Write(descriptor.Binding.LaunchId.ToByteArray());
        writer.Write(descriptor.Binding.Parent.ProcessId);
        writer.Write(descriptor.Binding.Parent.CreationFileTime);
        writer.Write(descriptor.Binding.Child.ProcessId);
        writer.Write(descriptor.Binding.Child.CreationFileTime);
        WriteIdentity(writer, descriptor.Ledger.Directory);
        writer.Write(descriptor.Ledger.Entries.Count);
        foreach (var entry in descriptor.Ledger.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.Write(entry.Key);
            writer.Write((int)entry.Value.Writer);
            writer.Write((uint)entry.Value.WriterRights);
            WriteIdentity(writer, entry.Value.Identity);
        }
        writer.Write(descriptor.Handles.Count);
        for (var index = 0; index < descriptor.Handles.Count; index++)
        {
            var slot = descriptor.Plan.Slots[index];
            var handle = descriptor.Handles[index];
            writer.Write(slot.Label);
            writer.Write((int)slot.Kind);
            writer.Write((uint)slot.Rights);
            writer.Write(handle.RawValue.ToInt64());
            if (handle.Identity is { } identity)
                WriteIdentity(writer, identity);
        }
        writer.Write(descriptor.Challenge);
        writer.Flush();
        return stream.ToArray();
    }

    internal static byte[] Authenticate(
        ReadOnlySpan<byte> key, string domain, ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second)
    {
        if (key.Length != 32 || first.Length != 32 || second.Length != 32)
            throw Invalid("descriptor-invalid");
        var domainBytes = Encoding.ASCII.GetBytes(domain);
        var message = new byte[domainBytes.Length + first.Length + second.Length];
        domainBytes.CopyTo(message, 0);
        first.CopyTo(message.AsSpan(domainBytes.Length));
        second.CopyTo(message.AsSpan(domainBytes.Length + first.Length));
        try { return HMACSHA256.HashData(key, message); }
        finally { CryptographicOperations.ZeroMemory(message); }
    }

    internal static FixtureHandoffContractException Invalid(string suffix) =>
        new($"fixture.handoff.{suffix}");

    private static void WriteIdentity(BinaryWriter writer, FixtureArtifactIdentity identity)
    {
        writer.Write(identity.Volume);
        writer.Write(identity.FileIdLow);
        writer.Write(identity.FileIdHigh);
        writer.Write((int)identity.Kind);
        writer.Write(identity.IsReparse);
    }
}

/// <summary>Parent-side launch/cleanup ownership; never used as a child write grant.</summary>
internal sealed class FixtureArtifactHandoffParent
{
    private readonly FixtureArtifactHandoffPlan _plan;
    private readonly FixtureArtifactHandoffLedger _ledger;
    private readonly IFixtureHandoffProbe _probe;
    private readonly nint _parentHandle;
    private readonly FixtureHandoffProcessIdentity _parent;
    private readonly CancellationToken _lifetime;
    private readonly IReadOnlyList<FixtureHandoffClaim> _claims;
    private readonly IReadOnlyList<FixtureHandoffObservedHandle> _observed;
    private readonly HashSet<nint> _pendingTransferHandles;
    private nint _ownedChildHandle;
    private FixtureHandoffProcessIdentity? _child;
    private bool _childReaped;
    private bool _resumed;
    private bool _grantIssued;
    private bool _authenticated;
    private bool _observersDrained;
    private bool _artifactOperationsDrained;
    private bool _failed;
    private bool _uncertain;
    private byte[]? _expectedProof;
    private byte[]? _grant;
    private byte[]? _expectedGrantAck;
    private FixtureHandoffDescriptor? _descriptor;

    internal FixtureArtifactHandoffParent(
        FixtureArtifactHandoffPlan plan, FixtureArtifactHandoffLedger ledger,
        IFixtureHandoffProbe parentProbe, nint retainedParentHandle,
        FixtureHandoffProcessIdentity expectedParent,
        IReadOnlyList<FixtureHandoffClaim> transferClaims, CancellationToken lifetime)
    {
        _plan = plan ?? throw new ArgumentNullException(nameof(plan));
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _probe = parentProbe ?? throw new ArgumentNullException(nameof(parentProbe));
        _parent = expectedParent ?? throw new ArgumentNullException(nameof(expectedParent));
        _parentHandle = retainedParentHandle;
        _lifetime = lifetime;
        _lifetime.ThrowIfCancellationRequested();
        if (retainedParentHandle is 0 or -1 || !_parent.IsValid ||
            !_probe.IsProcessAlive(retainedParentHandle) ||
            _probe.ObserveProcess(retainedParentHandle) != _parent)
            throw FixtureHandoffEvidence.Invalid("parent-mismatch");
        _claims = transferClaims?.ToArray() ?? throw new ArgumentNullException(nameof(transferClaims));
        _observed = FixtureHandoffEvidence.Validate(
            _plan, _ledger, _probe, _parent, _claims);
        _pendingTransferHandles = _claims.Select(claim => claim.RawValue).ToHashSet();
    }

    // A successful CreateProcess hands this exact process handle to ownership before any
    // fallible cancellation, metadata query, or membership check.
    internal void BindSuspendedChild(
        nint createdChildHandle, FixtureHandoffProcessIdentity expectedChild,
        CancellationToken cancellationToken)
    {
        if (_ownedChildHandle != 0 || createdChildHandle is 0 or -1)
        {
            _uncertain = true;
            throw FixtureHandoffEvidence.Invalid("child-ownership-uncertain");
        }
        _ownedChildHandle = createdChildHandle;
        try
        {
            CheckActive(cancellationToken);
            if (expectedChild is null || !expectedChild.IsValid || expectedChild == _parent ||
                !_probe.IsProcessAlive(createdChildHandle) ||
                _probe.ObserveProcess(createdChildHandle) != expectedChild)
                throw FixtureHandoffEvidence.Invalid("child-mismatch");
            _child = expectedChild;
        }
        catch
        {
            _failed = true;
            throw;
        }
    }

    internal FixtureHandoffDescriptor Seal(CancellationToken cancellationToken)
    {
        CheckActive(cancellationToken);
        if (_child is null || _descriptor is not null || _resumed)
            throw Reject("descriptor-invalid");
        RevalidateParentAndChild();
        byte[] perLaunchKey;
        try { perLaunchKey = _probe.ReadSectionKey(_claims[0].RawValue); }
        catch
        {
            _failed = true;
            throw;
        }
        try
        {
            if (perLaunchKey.Length != 32)
                throw Reject("descriptor-invalid");
            var challenge = RandomNumberGenerator.GetBytes(32);
            try
            {
                var binding = new FixtureHandoffBinding(_plan.Edge, _plan.Scenario,
                    _plan.RunNonce, _plan.LaunchId, _parent, _child);
                var unsigned = new FixtureHandoffDescriptor(
                    _plan, _ledger, binding, _observed, challenge, new byte[32]);
                var transcript = FixtureHandoffEvidence.Transcript(unsigned);
                byte[] mac;
                try { mac = HMACSHA256.HashData(perLaunchKey, transcript); }
                finally { CryptographicOperations.ZeroMemory(transcript); }
                try
                {
                    _descriptor = new(_plan, _ledger, binding, _observed, challenge, mac);
                    _expectedProof = FixtureHandoffEvidence.Authenticate(
                        perLaunchKey, "WatchDog.fixture.child-proof.v1", mac, challenge);
                    return _descriptor;
                }
                finally { CryptographicOperations.ZeroMemory(mac); }
            }
            finally { CryptographicOperations.ZeroMemory(challenge); }
        }
        catch
        {
            _failed = true;
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(perLaunchKey); }
    }

    internal void MarkResumed(CancellationToken cancellationToken)
    {
        CheckActive(cancellationToken);
        if (_descriptor is null || _resumed)
            throw Reject("resume-invalid");
        _resumed = true;
    }

    internal byte[] AcceptChildProof(
        IFixtureHandoffChildProofSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        CheckActive(cancellationToken);
        if (!_resumed || _grantIssued || _authenticated || _expectedProof is null ||
            _descriptor is null)
            throw Reject("child-proof-rejected");
        RevalidateParentAndChild();
        FixtureHandoffChildProofObservation observation;
        try { observation = source.Receive(_descriptor); }
        catch
        {
            _failed = true;
            throw;
        }
        CheckActive(cancellationToken);
        RevalidateParentAndChild();
        if (observation is null || observation.Peer != _child ||
            observation.Proof is null || observation.Proof.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(_expectedProof, observation.Proof))
            throw Reject("child-proof-rejected");
        byte[] key;
        try { key = _probe.ReadSectionKey(_claims[0].RawValue); }
        catch
        {
            _failed = true;
            throw;
        }
        if (key.Length != 32)
        {
            CryptographicOperations.ZeroMemory(key);
            throw Reject("child-proof-rejected");
        }
        byte[]? nonce = null;
        try
        {
            nonce = RandomNumberGenerator.GetBytes(32);
            var grantMac = FixtureHandoffEvidence.Authenticate(key,
                "WatchDog.fixture.parent-grant.v1", _expectedProof, nonce);
            try
            {
                _grant = new byte[64];
                nonce.CopyTo(_grant, 0);
                grantMac.CopyTo(_grant, 32);
                _expectedGrantAck = FixtureHandoffEvidence.Authenticate(key,
                    "WatchDog.fixture.grant-ack.v1", grantMac, nonce);
            }
            finally { CryptographicOperations.ZeroMemory(grantMac); }
        }
        catch
        {
            _failed = true;
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            if (nonce is not null)
                CryptographicOperations.ZeroMemory(nonce);
        }
        _grantIssued = true;
        return (byte[])_grant.Clone();
    }

    internal void ConfirmChildGrantAccepted(
        IFixtureHandoffChildGrantAckSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        CheckActive(cancellationToken);
        if (!_grantIssued || _authenticated || _expectedGrantAck is null ||
            _descriptor is null)
            throw Reject("grant-ack-rejected");
        RevalidateParentAndChild();
        FixtureHandoffChildGrantAckObservation observation;
        try { observation = source.Receive(_descriptor); }
        catch
        {
            _failed = true;
            throw;
        }
        CheckActive(cancellationToken);
        RevalidateParentAndChild();
        if (observation is null || observation.Peer != _child ||
            observation.Ack is null || observation.Ack.Length != 32 ||
            !CryptographicOperations.FixedTimeEquals(_expectedGrantAck, observation.Ack))
            throw Reject("grant-ack-rejected");
        _authenticated = true;
    }

    internal void ConfirmTransferClosed(nint rawHandle, FixtureArtifactCloseStatus status)
    {
        if (!_pendingTransferHandles.Contains(rawHandle))
            throw Reject("close-invalid");
        if (status == FixtureArtifactCloseStatus.Closed)
            _pendingTransferHandles.Remove(rawHandle);
        else if (status == FixtureArtifactCloseStatus.Ambiguous)
            _uncertain = true;
    }

    internal void ConfirmExactChildReaped(nint createdChildHandle, bool processExited, bool jobEmpty)
    {
        if (_ownedChildHandle == 0 || createdChildHandle != _ownedChildHandle ||
            !processExited || !jobEmpty)
        {
            _uncertain = true;
            throw FixtureHandoffEvidence.Invalid("child-reap-uncertain");
        }
        _childReaped = true;
    }

    internal void ConfirmObserversDrained() => _observersDrained = true;
    internal void ConfirmArtifactOperationsDrained() => _artifactOperationsDrained = true;
    internal bool CleanupConfirmed => !_uncertain && _observersDrained &&
        _artifactOperationsDrained &&
        _pendingTransferHandles.Count == 0 && (_ownedChildHandle == 0 || _childReaped);
    internal QualificationExitCode ExitAfterCleanup(QualificationExitCode requested)
    {
        if (requested == QualificationExitCode.Pass && !_uncertain)
        {
            try
            {
                if (!_ledger.IsStillAdmitted)
                    _uncertain = true;
            }
            catch { _uncertain = true; }
        }
        return FixtureArtifactOwnership.ExitCodeAfterCleanup(
            requested == QualificationExitCode.Pass &&
                (!_authenticated || _failed || _lifetime.IsCancellationRequested)
                ? QualificationExitCode.AssertionFailedCleanupConfirmed : requested,
            CleanupConfirmed);
    }

    private void RevalidateParentAndChild()
    {
        try
        {
            if (!_probe.IsProcessAlive(_parentHandle) ||
                _probe.ObserveProcess(_parentHandle) != _parent || _child is null ||
                !_probe.IsProcessAlive(_ownedChildHandle) ||
                _probe.ObserveProcess(_ownedChildHandle) != _child ||
                !FixtureHandoffEvidence.Validate(_plan, _ledger, _probe, _parent, _claims)
                    .SequenceEqual(_observed))
                throw FixtureHandoffEvidence.Invalid("identity-mismatch");
        }
        catch
        {
            _failed = true;
            _uncertain = true;
            throw;
        }
    }

    private void CheckActive(CancellationToken cancellationToken)
    {
        if (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            _failed = true;
            throw new OperationCanceledException(cancellationToken.IsCancellationRequested
                ? cancellationToken : _lifetime);
        }
        if (_failed || _uncertain)
            throw FixtureHandoffEvidence.Invalid("handoff-stopped");
    }

    private FixtureHandoffContractException Reject(string suffix)
    {
        _failed = true;
        return FixtureHandoffEvidence.Invalid(suffix);
    }
}

/// <summary>
/// Independent child-side observer. A descriptor MAC is not an ACK, and a parent proof is
/// not a child write grant. Child writes are fenced until the separate parent grant arrives.
/// </summary>
internal sealed class FixtureArtifactHandoffChild
{
    private readonly FixtureHandoffDescriptor _descriptor;
    private readonly IFixtureHandoffProbe _probe;
    private readonly nint _selfHandle;
    private readonly IReadOnlyList<FixtureHandoffClaim> _claims;
    private readonly IReadOnlyList<FixtureHandoffObservedHandle> _observed;
    private readonly CancellationToken _lifetime;
    private readonly byte[] _sessionKey;
    private byte[]? _grantAck;
    private bool _granted;
    private bool _failed;

    private FixtureArtifactHandoffChild(
        FixtureHandoffDescriptor descriptor, IFixtureHandoffProbe childProbe,
        nint selfHandle, IReadOnlyList<FixtureHandoffClaim> claims,
        IReadOnlyList<FixtureHandoffObservedHandle> observed,
        CancellationToken lifetime, byte[] proof, byte[] sessionKey)
    {
        _descriptor = descriptor;
        _probe = childProbe;
        _selfHandle = selfHandle;
        _claims = claims;
        _observed = observed;
        _lifetime = lifetime;
        _proof = (byte[])proof.Clone();
        _sessionKey = (byte[])sessionKey.Clone();
    }

    private readonly byte[] _proof;
    internal byte[] Proof => (byte[])_proof.Clone();
    internal byte[] GrantAck => MayWrite && _grantAck is not null
        ? (byte[])_grantAck.Clone() :
        throw FixtureHandoffEvidence.Invalid("grant-not-accepted");

    internal static FixtureArtifactHandoffChild Accept(
        FixtureHandoffDescriptor descriptor, FixtureHandoffBinding expectedBinding,
        IFixtureHandoffProbe childProbe, nint retainedSelfHandle,
        IReadOnlyList<FixtureHandoffClaim> inheritedClaims, CancellationToken lifetime)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(expectedBinding);
        ArgumentNullException.ThrowIfNull(childProbe);
        ArgumentNullException.ThrowIfNull(inheritedClaims);
        lifetime.ThrowIfCancellationRequested();
        if (descriptor.Binding != expectedBinding ||
            descriptor.Plan.Edge != expectedBinding.Edge ||
            descriptor.Plan.Scenario != expectedBinding.Scenario ||
            descriptor.Plan.RunNonce != expectedBinding.RunNonce ||
            descriptor.Plan.LaunchId != expectedBinding.LaunchId ||
            retainedSelfHandle is 0 or -1 || !childProbe.IsProcessAlive(retainedSelfHandle) ||
            childProbe.ObserveProcess(retainedSelfHandle) != expectedBinding.Child)
            throw FixtureHandoffEvidence.Invalid("descriptor-rejected");
        var observed = FixtureHandoffEvidence.Validate(descriptor.Plan, descriptor.Ledger,
            childProbe, expectedBinding.Parent, inheritedClaims);
        if (!observed.SequenceEqual(descriptor.Handles))
            throw FixtureHandoffEvidence.Invalid("inheritance-failed");
        var perLaunchKey = childProbe.ReadSectionKey(inheritedClaims[0].RawValue);
        try
        {
            if (perLaunchKey.Length != 32)
                throw FixtureHandoffEvidence.Invalid("descriptor-rejected");
            var transcript = FixtureHandoffEvidence.Transcript(descriptor);
            byte[] expectedMac;
            try { expectedMac = HMACSHA256.HashData(perLaunchKey, transcript); }
            finally { CryptographicOperations.ZeroMemory(transcript); }
            var mac = descriptor.Mac;
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(expectedMac, mac))
                    throw FixtureHandoffEvidence.Invalid("descriptor-rejected");
                var challenge = descriptor.Challenge;
                try
                {
                    var proof = FixtureHandoffEvidence.Authenticate(perLaunchKey,
                        "WatchDog.fixture.child-proof.v1", mac, challenge);
                    return new(descriptor, childProbe, retainedSelfHandle,
                        inheritedClaims.ToArray(), observed, lifetime, proof, perLaunchKey);
                }
                finally { CryptographicOperations.ZeroMemory(challenge); }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(expectedMac);
                CryptographicOperations.ZeroMemory(mac);
            }
        }
        finally { CryptographicOperations.ZeroMemory(perLaunchKey); }
    }

    internal void AcceptParentGrant(
        IFixtureHandoffParentGrantSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            _failed = true;
            CryptographicOperations.ZeroMemory(_sessionKey);
            throw new OperationCanceledException(cancellationToken.IsCancellationRequested
                ? cancellationToken : _lifetime);
        }
        FixtureHandoffParentGrantObservation observation;
        try { observation = source.Receive(_descriptor); }
        catch
        {
            _failed = true;
            CryptographicOperations.ZeroMemory(_sessionKey);
            throw;
        }
        if (_lifetime.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            _failed = true;
            CryptographicOperations.ZeroMemory(_sessionKey);
            throw new OperationCanceledException(cancellationToken.IsCancellationRequested
                ? cancellationToken : _lifetime);
        }
        if (_granted || _failed || _lifetime.IsCancellationRequested ||
            observation is null || observation.Peer != _descriptor.Binding.Parent ||
            observation.Grant is null || observation.Grant.Length != 64)
        {
            _failed = true;
            CryptographicOperations.ZeroMemory(_sessionKey);
            throw FixtureHandoffEvidence.Invalid("parent-grant-rejected");
        }
        var nonce = observation.Grant.AsSpan(0, 32);
        var grantMac = observation.Grant.AsSpan(32, 32);
        byte[]? expectedMac = null;
        try
        {
            expectedMac = FixtureHandoffEvidence.Authenticate(_sessionKey,
                "WatchDog.fixture.parent-grant.v1", _proof, nonce);
            if (!CryptographicOperations.FixedTimeEquals(grantMac, expectedMac))
            {
                _failed = true;
                throw FixtureHandoffEvidence.Invalid("parent-grant-rejected");
            }
            _grantAck = FixtureHandoffEvidence.Authenticate(_sessionKey,
                "WatchDog.fixture.grant-ack.v1", grantMac, nonce);
        }
        catch
        {
            _failed = true;
            throw;
        }
        finally
        {
            if (expectedMac is not null)
                CryptographicOperations.ZeroMemory(expectedMac);
            CryptographicOperations.ZeroMemory(_sessionKey);
        }
        _granted = true;
    }

    internal bool MayWrite
    {
        get
        {
            if (!_granted || _failed || _lifetime.IsCancellationRequested)
                return false;
            try
            {
                if (!_probe.IsProcessAlive(_selfHandle) ||
                    _probe.ObserveProcess(_selfHandle) != _descriptor.Binding.Child ||
                    !FixtureHandoffEvidence.Validate(_descriptor.Plan, _descriptor.Ledger,
                        _probe, _descriptor.Binding.Parent, _claims).SequenceEqual(_observed))
                {
                    _failed = true;
                    return false;
                }
                return true;
            }
            catch
            {
                _failed = true;
                return false;
            }
        }
    }
}
