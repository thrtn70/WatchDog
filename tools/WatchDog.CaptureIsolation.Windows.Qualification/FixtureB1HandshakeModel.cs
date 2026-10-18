using System.Security.Cryptography;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal sealed record FixtureB1DescriptorObservation(
    string ControlEndpoint,
    long ConnectionReceipt,
    FixtureBrokerDescriptor Descriptor);

internal interface IFixtureB1StagedChildLocalObserver : IFixtureBrokerChildLocalObserver
{
    // Coordinates are routing claims until child-local object, peer, and MAC checks finish.
    void StageUntrustedDescriptor(FixtureBrokerDescriptor descriptor);
}

internal interface IFixtureB1HandshakeTransport
{
    // Implementations must make cancellation terminal before returning control; the model
    // does not detach a token-ignoring descriptor receive.
    ValueTask<FixtureB1DescriptorObservation?> ReceiveDescriptorAsync(
        string controlEndpoint,
        CancellationToken cancellationToken);

    ValueTask SendProofAsync(
        long connectionReceipt,
        ReadOnlyMemory<byte> proof,
        CancellationToken cancellationToken);

    ValueTask<FixtureBrokerFrameObservation?> ReceiveGrantAsync(
        long connectionReceipt,
        CancellationToken cancellationToken);

    ValueTask SendGrantAckAsync(
        long connectionReceipt,
        ReadOnlyMemory<byte> grantAck,
        CancellationToken cancellationToken);

    ValueTask<FixtureBrokerFrameObservation?> ReceiveActivationAsync(
        long connectionReceipt,
        CancellationToken cancellationToken);

    ValueTask SendActivationAckAsync(
        long connectionReceipt,
        ReadOnlyMemory<byte> activationAck,
        CancellationToken cancellationToken);
}

/// <summary>
/// A scoped observation that the runner accepted activation and sent its ACK. Launcher
/// acceptance and cleanup require separate evidence. This is not write, launch, content,
/// native-cleanup, qualification-pass, or release evidence.
/// </summary>
internal sealed record FixtureB1ActivationObservation(
    Guid RunNonce,
    Guid LaunchId,
    FixtureHandoffProcessIdentity Issuer,
    FixtureHandoffProcessIdentity Target,
    long ConnectionReceipt);

internal sealed class FixtureB1HandshakeException(string code, Exception? innerException = null)
    : Exception(code, innerException)
{
    internal string Code { get; } = code;
}

/// <summary>
/// Pure, unregistered runner-side B1 handshake orchestration. It performs no process launch,
/// IPC creation, native call, content operation, or qualification decision.
/// </summary>
internal sealed class FixtureB1HandshakeModel
{
    private const int SecretBytes = 32;
    private const string AuthSectionLabel = "artifact-auth-section";
    private const string ParentProofLabel = "artifact-parent-proof";
    private const FixtureHandoffRights ParentProofRights =
        FixtureHandoffRights.ProcessQueryLimited | FixtureHandoffRights.Synchronize;
    private readonly IFixtureB1StagedChildLocalObserver _observer;
    private readonly IFixtureBrokerAuthority _authority;
    private readonly IFixtureB1HandshakeTransport _transport;
    private readonly TimeProvider _clock;
    private int _used;

    internal FixtureB1HandshakeModel(
        IFixtureB1StagedChildLocalObserver observer,
        IFixtureBrokerAuthority authority,
        IFixtureB1HandshakeTransport transport,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(clock);
        _observer = observer;
        _authority = authority;
        _transport = transport;
        _clock = clock;
    }

    internal async Task<FixtureB1ActivationObservation> ExecuteAsync(
        IReadOnlyList<string>? arguments,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _used, 1, 0) != 0)
            throw Invalid("one-use-rejected");

        cancellationToken.ThrowIfCancellationRequested();
        if (!FixtureB1Command.TryParseRunnerSyntax(arguments, out var parsed) || parsed is null)
            throw Invalid("arguments-rejected");

        FixtureArtifactHandoffPlan plan;
        try
        {
            plan = FixtureArtifactHandoffPlan.Create(
                FixtureHandoffEdge.LauncherToRunner,
                null,
                parsed.RunNonce,
                parsed.LaunchId);
        }
        catch (Exception exception)
        {
            throw Invalid("arguments-rejected", exception);
        }

        using var readinessBudget = new CancellationTokenSource(
            QualificationConstants.ReadinessTimeout, _clock);
        using var readiness = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, readinessBudget.Token);
        using var sessionLifetime =
            CancellationTokenSource.CreateLinkedTokenSource(readiness.Token);
        FixtureBrokerChildState? child = null;
        try
        {
            FixtureB1DescriptorObservation? descriptorObservation;
            try
            {
                descriptorObservation = await _transport.ReceiveDescriptorAsync(
                    parsed.ControlEndpoint, readiness.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                readinessBudget.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                throw Invalid("descriptor-timeout");
            }
            readiness.Token.ThrowIfCancellationRequested();
            if (descriptorObservation is null ||
                descriptorObservation.ConnectionReceipt <= 0 ||
                descriptorObservation.Descriptor is null ||
                !string.Equals(
                    descriptorObservation.ControlEndpoint,
                    parsed.ControlEndpoint,
                    StringComparison.Ordinal))
                throw Invalid("descriptor-rejected");

            // Only after a bounded descriptor arrival may the runner touch inherited target
            // coordinates. Descriptor fields and argv IDs remain untrusted at this point.
            _observer.StageUntrustedDescriptor(descriptorObservation.Descriptor);
            readiness.Token.ThrowIfCancellationRequested();
            var parties = ObserveParties();
            RequireParentProofPeer(
                descriptorObservation.ConnectionReceipt, parties);
            readiness.Token.ThrowIfCancellationRequested();

            var secret = ReadObservedSectionSecret();
            try
            {
                child = FixtureBrokerChildState.Accept(
                    descriptorObservation.Descriptor,
                    plan,
                    parties,
                    secret,
                    _observer,
                    _authority,
                    _clock,
                    sessionLifetime.Token);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(secret);
            }

            // Re-observe the exact descriptor connection after descriptor and both IDs have
            // authenticated, immediately before any proof bytes may leave this process.
            RequirePeer(descriptorObservation.ConnectionReceipt, parties.Issuer);
            readiness.Token.ThrowIfCancellationRequested();

            var proof = child.Proof;
            try
            {
                readiness.Token.ThrowIfCancellationRequested();
                await _transport.SendProofAsync(
                    descriptorObservation.ConnectionReceipt,
                    proof,
                    readiness.Token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(proof);
            }
            readiness.Token.ThrowIfCancellationRequested();
            RequireBeforeDeadline(descriptorObservation.Descriptor);

            var grant = await _transport.ReceiveGrantAsync(
                descriptorObservation.ConnectionReceipt,
                readiness.Token).ConfigureAwait(false);
            readiness.Token.ThrowIfCancellationRequested();
            RequireSameConnection(grant, descriptorObservation.ConnectionReceipt, "grant-rejected");
            child.AcceptGrant(grant!);
            if (child.Phase != FixtureBrokerChildPhase.GrantAccepted ||
                child.MayWrite || child.MayLaunch)
                throw Invalid("grant-fence-rejected");

            var grantAck = child.GrantAck;
            try
            {
                readiness.Token.ThrowIfCancellationRequested();
                await _transport.SendGrantAckAsync(
                    descriptorObservation.ConnectionReceipt,
                    grantAck,
                    readiness.Token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(grantAck);
            }
            readiness.Token.ThrowIfCancellationRequested();
            RequireBeforeDeadline(descriptorObservation.Descriptor);

            var activation = await _transport.ReceiveActivationAsync(
                descriptorObservation.ConnectionReceipt,
                readiness.Token).ConfigureAwait(false);
            readiness.Token.ThrowIfCancellationRequested();
            RequireSameConnection(
                activation,
                descriptorObservation.ConnectionReceipt,
                "activation-rejected");
            child.AcceptActivation(activation!);

            var activationAck = child.ActivationAck;
            try
            {
                readiness.Token.ThrowIfCancellationRequested();
                await _transport.SendActivationAckAsync(
                    descriptorObservation.ConnectionReceipt,
                    activationAck,
                    readiness.Token).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(activationAck);
            }

            readiness.Token.ThrowIfCancellationRequested();
            RequireBeforeDeadline(descriptorObservation.Descriptor);
            RequirePeer(descriptorObservation.ConnectionReceipt, parties.Issuer);
            readiness.Token.ThrowIfCancellationRequested();
            RequireBeforeDeadline(descriptorObservation.Descriptor);

            return new(
                parsed.RunNonce,
                parsed.LaunchId,
                parties.Issuer,
                parties.Target,
                descriptorObservation.ConnectionReceipt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (readinessBudget.IsCancellationRequested)
        {
            throw Invalid("deadline-expired");
        }
        catch (FixtureB1HandshakeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Invalid("exchange-rejected", exception);
        }
        finally
        {
            await sessionLifetime.CancelAsync().ConfigureAwait(false);
            if (child is not null)
            {
                try
                {
                    _ = child.MayWrite;
                }
                catch
                {
                    // Session retirement is fail closed; no child authority is returned.
                }
            }
        }
    }

    private FixtureBrokerParties ObserveParties()
    {
        FixtureHandoffProcessIdentity creator;
        FixtureHandoffProcessIdentity issuer;
        FixtureHandoffProcessIdentity target;
        try
        {
            creator = _observer.ObserveCreatorProcess();
            issuer = _observer.ObserveIssuerProcess();
            target = _observer.ObserveTargetProcess();
        }
        catch (Exception exception)
        {
            throw Invalid("parties-rejected", exception);
        }

        if (creator is null || issuer is null || target is null ||
            !creator.IsValid || !issuer.IsValid || !target.IsValid ||
            creator != issuer || target == creator || target == issuer)
            throw Invalid("parties-rejected");
        return new(creator, issuer, target);
    }

    private byte[] ReadObservedSectionSecret()
    {
        try
        {
            var observed = _observer.ObserveDeliveredSlot(AuthSectionLabel);
            if (observed is null || observed.RawValue is 0 or -1 ||
                observed.Kind != FixtureHandoffHandleKind.Section ||
                observed.Rights != FixtureHandoffRights.SectionRead ||
                observed.Identity is not null)
                throw Invalid("section-rejected");
            var secret = _observer.ReadSectionKeyAtTargetCoordinate(observed.RawValue);
            if (secret is null || secret.Length != SecretBytes)
            {
                if (secret is not null)
                    CryptographicOperations.ZeroMemory(secret);
                throw Invalid("section-rejected");
            }
            return secret;
        }
        catch (FixtureB1HandshakeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Invalid("section-rejected", exception);
        }
    }

    private void RequireParentProofPeer(
        long connectionReceipt,
        FixtureBrokerParties parties)
    {
        try
        {
            var observed = _observer.ObserveDeliveredSlot(ParentProofLabel);
            if (observed is null || observed.RawValue is 0 or -1 ||
                observed.Kind != FixtureHandoffHandleKind.Process ||
                observed.Rights != ParentProofRights || observed.Identity is not null)
                throw Invalid("parent-proof-rejected");

            var retainedParent =
                _observer.ObserveProcessAtTargetCoordinate(observed.RawValue);
            var serverPeer = _authority.ObserveConnectedPeer(connectionReceipt);
            if (retainedParent is null || !retainedParent.IsValid ||
                retainedParent != parties.Creator ||
                retainedParent != parties.Issuer ||
                serverPeer != retainedParent)
                throw Invalid("parent-proof-rejected");
        }
        catch (FixtureB1HandshakeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Invalid("parent-proof-rejected", exception);
        }
    }

    private void RequirePeer(
        long connectionReceipt,
        FixtureHandoffProcessIdentity expected)
    {
        try
        {
            if (_authority.ObserveConnectedPeer(connectionReceipt) != expected)
                throw Invalid("peer-rejected");
        }
        catch (FixtureB1HandshakeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw Invalid("peer-rejected", exception);
        }
    }

    private void RequireBeforeDeadline(FixtureBrokerDescriptor descriptor)
    {
        if (_clock.GetTimestamp() >= descriptor.DeadlineTimestamp)
            throw Invalid("deadline-expired");
    }

    private static void RequireSameConnection(
        FixtureBrokerFrameObservation? observation,
        long expectedConnectionReceipt,
        string failureCode)
    {
        if (observation is null || observation.Frame is null ||
            observation.ConnectionReceipt != expectedConnectionReceipt)
            throw Invalid(failureCode);
    }

    private static FixtureB1HandshakeException Invalid(
        string suffix,
        Exception? innerException = null) =>
        new($"fixture.b1.handshake.{suffix}", innerException);
}
