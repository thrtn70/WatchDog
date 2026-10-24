using WatchDog.CaptureIsolation.Windows.Qualification;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1HandshakeModelTests
{
    private static readonly Guid RunNonce =
        Guid.Parse("aaaaaaaa-2222-3333-4444-555555555555");
    private static readonly Guid LaunchId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureHandoffProcessIdentity Launcher = new(100, 1_001);
    private static readonly FixtureHandoffProcessIdentity Runner = new(200, 2_002);
    private const string Endpoint = "WDB1-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ValidExchangeAuthenticatesBeforeProofAndReturnsOnlyActivationObservation()
    {
        var fixture = NewFixture();
        var result = await fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None);

        Assert.Equal(RunNonce, result.RunNonce);
        Assert.Equal(LaunchId, result.LaunchId);
        Assert.Equal(Launcher, result.Issuer);
        Assert.Equal(Runner, result.Target);
        Assert.Equal(FakeTransport.DescriptorReceipt, result.ConnectionReceipt);
        Assert.Equal(FixtureBrokerPhase.Active, fixture.Broker.Phase);
        Assert.Equal(1, fixture.Transport.ProofSends);
        Assert.Equal(1, fixture.Transport.GrantAckSends);
        Assert.Equal(1, fixture.Transport.ActivationAckSends);
        Assert.True(fixture.Authority.DescriptorPeerObservations >= 4);
        Assert.NotNull(fixture.Observer.LastReturnedSecret);
        Assert.All(fixture.Observer.LastReturnedSecret!, value => Assert.Equal(0, value));
        Assert.All(fixture.Transport.LastProofView.ToArray(), value => Assert.Equal(0, value));
        Assert.All(fixture.Transport.LastGrantAckView.ToArray(), value => Assert.Equal(0, value));
        Assert.All(fixture.Transport.LastActivationAckView.ToArray(),
            value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task DescriptorArrivesBeforeAnyChildLocalPartyOrSlotObservation()
    {
        var fixture = NewFixture();
        fixture.Observer.RefusePreDescriptorAccess = true;

        _ = await fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None);

        Assert.True(fixture.Observer.DescriptorReceived);
        Assert.Equal(0, fixture.Observer.PreDescriptorAccesses);
        Assert.Equal(1, fixture.Transport.ProofSends);
    }

    [Fact]
    public async Task LateDescriptorAfterReadinessDeadlineDoesNotStageOrSendProof()
    {
        var fixture = NewFixture();
        fixture.Transport.BlockDescriptor = true;
        fixture.Transport.IgnoreDescriptorCancellation = true;
        var pending = fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None);
        await fixture.Transport.DescriptorReceiveEntered.Task.WaitAsync(TestTimeouts.Signal);

        fixture.Clock.Advance(QualificationConstants.ReadinessTimeout);
        fixture.Transport.ReleaseDescriptor();
        var failure = await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => pending.WaitAsync(TestTimeouts.Signal));

        Assert.Equal("fixture.b1.handshake.deadline-expired", failure.Code);
        Assert.False(fixture.Observer.DescriptorReceived);
        Assert.Null(fixture.Observer.LastReturnedSecret);
        Assert.Equal(0, fixture.Transport.ProofSends);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CooperativePostDescriptorWaitUsesSameReadinessDeadline(
        bool blockGrant)
    {
        var fixture = NewFixture();
        fixture.Transport.BlockGrant = blockGrant;
        fixture.Transport.BlockActivation = !blockGrant;
        var pending = fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None);
        await (blockGrant
                ? fixture.Transport.GrantReceiveEntered.Task
                : fixture.Transport.ActivationReceiveEntered.Task)
            .WaitAsync(TestTimeouts.Signal);

        fixture.Clock.Advance(QualificationConstants.ReadinessTimeout);
        var failure = await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => pending.WaitAsync(TestTimeouts.Signal));

        Assert.Equal("fixture.b1.handshake.deadline-expired", failure.Code);
        Assert.Equal(1, fixture.Transport.ProofSends);
        Assert.Equal(blockGrant ? 0 : 1, fixture.Transport.GrantAckSends);
        Assert.Equal(0, fixture.Transport.ActivationAckSends);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthenticatedDescriptorWithWrongArgvIdentifierSendsNoProofOrAck(
        bool changeNonce)
    {
        var fixture = NewFixture();
        var arguments = Arguments(
            changeNonce ? Guid.Parse("bbbbbbbb-2222-3333-4444-555555555555") : RunNonce,
            changeNonce ? LaunchId : Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));

        await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => fixture.Model.ExecuteAsync(arguments, CancellationToken.None));

        Assert.Equal(0, fixture.Transport.ProofSends);
        Assert.Equal(0, fixture.Transport.GrantAckSends);
        Assert.Equal(0, fixture.Transport.ActivationAckSends);
    }

    [Fact]
    public async Task PeerChangeAfterDescriptorAuthenticationIsRejectedBeforeProof()
    {
        var fixture = NewFixture();
        fixture.Authority.DescriptorPeerAfterFirstObservation = Runner;

        await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None));

        Assert.Equal(2, fixture.Authority.DescriptorPeerObservations);
        Assert.Equal(0, fixture.Transport.ProofSends);
        Assert.Equal(0, fixture.Transport.GrantAckSends);
    }

    [Fact]
    public async Task RecycledParentProofIdentityIsRejectedBeforeSecretOrProof()
    {
        var fixture = NewFixture();
        fixture.Observer.ParentProofProcess = new(
            Launcher.ProcessId, Launcher.CreationFileTime + 1);

        var failure = await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None));

        Assert.Equal("fixture.b1.handshake.parent-proof-rejected", failure.Code);
        Assert.Null(fixture.Observer.LastReturnedSecret);
        Assert.Equal(0, fixture.Transport.ProofSends);
        Assert.Equal(0, fixture.Transport.GrantAckSends);
    }

    [Fact]
    public async Task DescriptorConnectionPeerMismatchIsRejectedBeforeSecretOrProof()
    {
        var fixture = NewFixture();
        fixture.Authority.DescriptorPeer = Runner;

        var failure = await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None));

        Assert.Equal("fixture.b1.handshake.parent-proof-rejected", failure.Code);
        Assert.Null(fixture.Observer.LastReturnedSecret);
        Assert.Equal(0, fixture.Transport.ProofSends);
        Assert.Equal(0, fixture.Transport.GrantAckSends);
    }

    [Fact]
    public async Task WrongObservedSectionKeyIsRejectedBeforeProof()
    {
        var fixture = NewFixture();
        fixture.Observer.SectionKey[0] ^= 1;

        await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None));

        Assert.Equal(0, fixture.Transport.ProofSends);
        Assert.Equal(0, fixture.Transport.GrantAckSends);
        Assert.NotNull(fixture.Observer.LastReturnedSecret);
        Assert.All(fixture.Observer.LastReturnedSecret!, value => Assert.Equal(0, value));
    }

    [Fact]
    public async Task WrongChildLocalSlotIsRejectedBeforeProof()
    {
        var fixture = NewFixture();
        const string label = "artifact-parent-proof";
        fixture.Observer.Slots[label] = fixture.Observer.Slots[label] with
        {
            RawValue = (nint)9_999,
        };

        await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None));

        Assert.Equal(0, fixture.Transport.ProofSends);
        Assert.Equal(0, fixture.Transport.GrantAckSends);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DroppedOrWrongActivationNeverSendsActivationAck(bool dropActivation)
    {
        var fixture = NewFixture();
        fixture.Transport.DropActivation = dropActivation;
        fixture.Transport.CorruptActivation = !dropActivation;

        await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None));

        Assert.Equal(1, fixture.Transport.ProofSends);
        Assert.Equal(1, fixture.Transport.GrantAckSends);
        Assert.Equal(0, fixture.Transport.ActivationAckSends);
    }

    [Fact]
    public async Task CancellationConsumesModelAndDuplicateUseIsRejected()
    {
        var fixture = NewFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.BlockDescriptor = true;
        var pending = fixture.Model.ExecuteAsync(Arguments(), cancellation.Token);
        await fixture.Transport.DescriptorReceiveEntered.Task.WaitAsync(TestTimeouts.Signal);

        var duplicate = await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None));
        Assert.Equal("fixture.b1.handshake.one-use-rejected", duplicate.Code);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(0, fixture.Transport.ProofSends);
        Assert.Equal(0, fixture.Transport.GrantAckSends);
        Assert.Equal(0, fixture.Transport.ActivationAckSends);
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]
    [InlineData(1, 1, 0, 0)]
    [InlineData(2, 1, 1, 0)]
    public async Task CancellationReturnedByNoncooperativeReceiveSendsNoLaterFrame(
        int cancellationPoint,
        int expectedProofs,
        int expectedGrantAcks,
        int expectedActivationAcks)
    {
        var fixture = NewFixture();
        using var cancellation = new CancellationTokenSource();
        switch (cancellationPoint)
        {
            case 0:
                fixture.Transport.BeforeDescriptorReturn = cancellation.Cancel;
                break;
            case 1:
                fixture.Transport.BeforeGrantReturn = cancellation.Cancel;
                break;
            case 2:
                fixture.Transport.BeforeActivationReturn = cancellation.Cancel;
                break;
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Model.ExecuteAsync(Arguments(), cancellation.Token));

        Assert.Equal(expectedProofs, fixture.Transport.ProofSends);
        Assert.Equal(expectedGrantAcks, fixture.Transport.GrantAckSends);
        Assert.Equal(expectedActivationAcks, fixture.Transport.ActivationAckSends);
    }

    [Fact]
    public async Task ActivationAckCompletedAfterDeadlineReturnsNoObservation()
    {
        var fixture = NewFixture();
        fixture.Transport.BeforeActivationAckReturn = () =>
            fixture.Clock.SetTimestamp(long.MaxValue);

        var failure = await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None));

        Assert.Equal("fixture.b1.handshake.deadline-expired", failure.Code);
        Assert.Equal(1, fixture.Transport.ActivationAckSends);
    }

    [Fact]
    public async Task FinalPeerObservationAfterDeadlineReturnsNoActivationObservation()
    {
        var fixture = NewFixture();
        fixture.Transport.BeforeActivationAckReturn = () =>
            fixture.Authority.BeforeNextDescriptorPeerReturn = () =>
                fixture.Clock.Advance(QualificationConstants.ReadinessTimeout);

        var failure = await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None));

        Assert.Equal("fixture.b1.handshake.deadline-expired", failure.Code);
        Assert.Equal(1, fixture.Transport.ActivationAckSends);
    }

    [Theory]
    [InlineData(true, 1, 0)]
    [InlineData(false, 1, 1)]
    public async Task EarlierSendCompletedAfterDeadlineEmitsNoLaterAck(
        bool expireDuringProof, int expectedProofs, int expectedGrantAcks)
    {
        var fixture = NewFixture();
        Action expire = () => fixture.Clock.SetTimestamp(long.MaxValue);
        if (expireDuringProof)
            fixture.Transport.BeforeProofReturn = expire;
        else
            fixture.Transport.BeforeGrantAckReturn = expire;

        var failure = await Assert.ThrowsAsync<FixtureB1HandshakeException>(
            () => fixture.Model.ExecuteAsync(Arguments(), CancellationToken.None));

        Assert.Equal("fixture.b1.handshake.deadline-expired", failure.Code);
        Assert.Equal(expectedProofs, fixture.Transport.ProofSends);
        Assert.Equal(expectedGrantAcks, fixture.Transport.GrantAckSends);
        Assert.Equal(0, fixture.Transport.ActivationAckSends);
    }

    [Fact]
    public async Task CancellationDuringActivationAckReturnsNoObservation()
    {
        var fixture = NewFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Transport.BeforeActivationAckReturn = cancellation.Cancel;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Model.ExecuteAsync(Arguments(), cancellation.Token));

        Assert.Equal(1, fixture.Transport.ActivationAckSends);
    }

    private static TestFixture NewFixture()
    {
        var secret = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();
        var plan = FixtureArtifactHandoffPlan.Create(
            FixtureHandoffEdge.LauncherToRunner, null, RunNonce, LaunchId);
        var parties = new FixtureBrokerParties(Launcher, Launcher, Runner);
        var directory = new FixtureArtifactIdentity(
            7, 1, 0, FixtureArtifactKind.Directory, false);
        var outputs = FixtureArtifactHandoffLedger.ExpectedEntries(null)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select((pair, index) => new FixtureBrokerOutput(
                pair.Key,
                pair.Value.Writer,
                pair.Value.WriterRights,
                new FixtureArtifactIdentity(
                    7,
                    (ulong)(index + 10),
                    0,
                    FixtureArtifactKind.File,
                    false),
                directory))
            .ToArray();
        var snapshot = new FixtureBrokerSnapshot(
            plan.Edge,
            plan.Scenario,
            plan.RunNonce,
            plan.LaunchId,
            parties,
            FixtureBrokerDeliveryMode.LocalLauncher,
            directory,
            outputs);
        var receipts = plan.Slots.Select((slot, index) =>
            new FixtureBrokerDeliveryReceipt(
                slot.Label,
                (nint)(1_000 + index),
                slot.Kind,
                slot.Rights,
                SlotIdentity(snapshot, slot))).ToArray();
        var observer = new FakeObserver(snapshot, receipts, secret);
        var authority = new FakeAuthority(plan, parties, snapshot, receipts);
        var clock = new FakeTimeProvider();
        var broker = FixtureBrokerState.Reserve(
            plan,
            parties.Creator,
            parties.Issuer,
            snapshot.Delivery,
            authority,
            clock,
            QualificationConstants.ReadinessTimeout,
            CancellationToken.None);
        var seal = FixtureBrokerSnapshotCodec.Seal(snapshot, secret);
        broker.RegisterTarget(
            new FixtureBrokerTargetRegistration(Runner, new((nint)501), new((nint)601)),
            snapshot,
            seal.Bytes,
            seal.Mac,
            secret);
        foreach (var receipt in receipts)
            broker.RecordDelivery(receipt);
        var descriptor = broker.SealDelivery();
        broker.MarkResumed();
        var transport = new FakeTransport(broker, descriptor, authority);
        var model = new FixtureB1HandshakeModel(observer, authority, transport, clock);
        return new(model, broker, observer, authority, transport, clock);
    }

    private static string[] Arguments(
        Guid? runNonce = null,
        Guid? launchId = null) =>
    [
        "--opt-in", QualificationConstants.OptInToken,
        "--mode", "artifact-runner",
        "--nonce", (runNonce ?? RunNonce).ToString("D"),
        "--launch-id", (launchId ?? LaunchId).ToString("D"),
        "--control-endpoint", Endpoint,
    ];

    private static FixtureArtifactIdentity? SlotIdentity(
        FixtureBrokerSnapshot snapshot,
        FixtureHandoffSlot slot)
    {
        if (slot.Kind == FixtureHandoffHandleKind.Directory)
            return snapshot.Directory;
        if (slot.Kind != FixtureHandoffHandleKind.File)
            return null;
        var separator = slot.Label.IndexOf(':');
        return snapshot.Outputs.Single(output =>
            output.Name == slot.Label[(separator + 1)..]).Identity;
    }

    private sealed record TestFixture(
        FixtureB1HandshakeModel Model,
        FixtureBrokerState Broker,
        FakeObserver Observer,
        FakeAuthority Authority,
        FakeTransport Transport,
        FakeTimeProvider Clock);

    private sealed class FakeTransport(
        FixtureBrokerState broker,
        FixtureBrokerDescriptor descriptor,
        FakeAuthority authority) : IFixtureB1HandshakeTransport
    {
        internal const long DescriptorReceipt = 12;
        private byte[]? _grant;
        private byte[]? _activation;
        private readonly TaskCompletionSource _descriptorRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _grantRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _activationRelease =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal int ProofSends { get; private set; }
        internal int GrantAckSends { get; private set; }
        internal int ActivationAckSends { get; private set; }
        internal bool BlockDescriptor { get; set; }
        internal bool IgnoreDescriptorCancellation { get; set; }
        internal bool BlockGrant { get; set; }
        internal bool BlockActivation { get; set; }
        internal bool DropActivation { get; set; }
        internal bool CorruptActivation { get; set; }
        internal Action? BeforeDescriptorReturn { get; set; }
        internal Action? BeforeGrantReturn { get; set; }
        internal Action? BeforeActivationReturn { get; set; }
        internal Action? BeforeProofReturn { get; set; }
        internal Action? BeforeGrantAckReturn { get; set; }
        internal Action? BeforeActivationAckReturn { get; set; }
        internal ReadOnlyMemory<byte> LastProofView { get; private set; }
        internal ReadOnlyMemory<byte> LastGrantAckView { get; private set; }
        internal ReadOnlyMemory<byte> LastActivationAckView { get; private set; }
        internal TaskCompletionSource DescriptorReceiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource GrantReceiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ActivationReceiveEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseDescriptor() => _descriptorRelease.TrySetResult();

        public async ValueTask<FixtureB1DescriptorObservation?> ReceiveDescriptorAsync(
            string controlEndpoint,
            CancellationToken cancellationToken)
        {
            DescriptorReceiveEntered.TrySetResult();
            if (BlockDescriptor)
            {
                if (IgnoreDescriptorCancellation)
                    await _descriptorRelease.Task;
                else
                    await _descriptorRelease.Task.WaitAsync(cancellationToken);
            }
            BeforeDescriptorReturn?.Invoke();
            return new(controlEndpoint, DescriptorReceipt, descriptor);
        }

        public ValueTask SendProofAsync(
            long connectionReceipt,
            ReadOnlyMemory<byte> proof,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(DescriptorReceipt, connectionReceipt);
            ProofSends++;
            LastProofView = proof;
            _grant = broker.AcceptChildProof(new(
                authority.ReceiptFor(Runner), proof.ToArray()));
            BeforeProofReturn?.Invoke();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<FixtureBrokerFrameObservation?> ReceiveGrantAsync(
            long connectionReceipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(DescriptorReceipt, connectionReceipt);
            GrantReceiveEntered.TrySetResult();
            if (BlockGrant)
                await _grantRelease.Task.WaitAsync(cancellationToken);
            BeforeGrantReturn?.Invoke();
            return new(DescriptorReceipt, (byte[])_grant!.Clone());
        }

        public ValueTask SendGrantAckAsync(
            long connectionReceipt,
            ReadOnlyMemory<byte> grantAck,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(DescriptorReceipt, connectionReceipt);
            GrantAckSends++;
            LastGrantAckView = grantAck;
            broker.AcceptGrantAck(new(authority.ReceiptFor(Runner), grantAck.ToArray()));
            _activation = broker.IssueActivation();
            BeforeGrantAckReturn?.Invoke();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<FixtureBrokerFrameObservation?> ReceiveActivationAsync(
            long connectionReceipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(DescriptorReceipt, connectionReceipt);
            ActivationReceiveEntered.TrySetResult();
            if (BlockActivation)
                await _activationRelease.Task.WaitAsync(cancellationToken);
            BeforeActivationReturn?.Invoke();
            if (DropActivation)
                return null;
            var frame = (byte[])_activation!.Clone();
            if (CorruptActivation)
                frame[^1] ^= 1;
            return new(DescriptorReceipt, frame);
        }

        public ValueTask SendActivationAckAsync(
            long connectionReceipt,
            ReadOnlyMemory<byte> activationAck,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(DescriptorReceipt, connectionReceipt);
            ActivationAckSends++;
            LastActivationAckView = activationAck;
            broker.ConfirmChildActive(new(
                authority.ReceiptFor(Runner), activationAck.ToArray()));
            BeforeActivationAckReturn?.Invoke();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeObserver : IFixtureB1StagedChildLocalObserver
    {
        private readonly FixtureBrokerSnapshot _snapshot;
        private IReadOnlyDictionary<string, nint>? _stagedCoordinates;

        internal FakeObserver(
            FixtureBrokerSnapshot snapshot,
            IEnumerable<FixtureBrokerDeliveryReceipt> receipts,
            byte[] secret)
        {
            _snapshot = snapshot;
            SectionKey = (byte[])secret.Clone();
            Outputs = snapshot.Outputs.ToDictionary(
                output => output.Name,
                output => output.Identity,
                StringComparer.Ordinal);
            Slots = receipts.ToDictionary(
                receipt => receipt.Label,
                receipt => new FixtureHandoffObservedHandle(
                    receipt.TargetHandle,
                    receipt.Kind,
                    receipt.Rights,
                    receipt.Identity),
                StringComparer.Ordinal);
        }

        internal byte[] SectionKey { get; }
        internal byte[]? LastReturnedSecret { get; private set; }
        internal Dictionary<string, FixtureArtifactIdentity> Outputs { get; }
        internal Dictionary<string, FixtureHandoffObservedHandle> Slots { get; }
        internal bool DescriptorReceived { get; private set; }
        internal bool RefusePreDescriptorAccess { get; set; }
        internal int PreDescriptorAccesses { get; private set; }
        internal FixtureHandoffProcessIdentity ParentProofProcess { get; set; } = Launcher;

        public void StageUntrustedDescriptor(FixtureBrokerDescriptor descriptor)
        {
            if (DescriptorReceived || descriptor.Receipts.Count != 7)
                throw new InvalidOperationException("descriptor staging rejected");
            _stagedCoordinates = descriptor.Receipts.ToDictionary(
                receipt => receipt.Label, receipt => receipt.TargetHandle,
                StringComparer.Ordinal);
            DescriptorReceived = true;
        }

        public FixtureHandoffProcessIdentity ObserveTargetProcess()
        {
            RequireDescriptor();
            return _snapshot.Parties.Target;
        }

        public FixtureHandoffProcessIdentity ObserveCreatorProcess()
        {
            RequireDescriptor();
            return _snapshot.Parties.Creator;
        }

        public FixtureHandoffProcessIdentity ObserveIssuerProcess()
        {
            RequireDescriptor();
            return _snapshot.Parties.Issuer;
        }

        public FixtureArtifactIdentity ObserveRetainedDirectory()
        {
            RequireDescriptor();
            return _snapshot.Directory;
        }

        public FixtureArtifactIdentity ObserveOutput(string literalName)
        {
            RequireDescriptor();
            return Outputs[literalName];
        }

        public int DeliveredSlotCount
        {
            get
            {
                RequireDescriptor();
                return Slots.Count;
            }
        }

        public FixtureHandoffObservedHandle ObserveDeliveredSlot(string label)
        {
            RequireDescriptor();
            var observed = Slots[label];
            if (observed.RawValue != _stagedCoordinates![label])
                throw new InvalidOperationException("staged coordinate mismatch");
            return observed;
        }

        public FixtureHandoffProcessIdentity ObserveProcessAtTargetCoordinate(nint coordinate)
        {
            RequireDescriptor();
            if (coordinate != _stagedCoordinates!["artifact-parent-proof"])
                throw new InvalidOperationException("parent proof coordinate mismatch");
            return ParentProofProcess;
        }

        public byte[] ReadSectionKeyAtTargetCoordinate(nint coordinate)
        {
            RequireDescriptor();
            if (coordinate != _stagedCoordinates!["artifact-auth-section"])
                throw new InvalidOperationException("auth section coordinate mismatch");
            LastReturnedSecret = (byte[])SectionKey.Clone();
            return LastReturnedSecret;
        }

        public bool IsExactChild(
            FixtureArtifactIdentity parent,
            string literalName,
            FixtureArtifactIdentity output) =>
            DescriptorReady() && parent == _snapshot.Directory &&
            Outputs[literalName] == output;

        private bool DescriptorReady()
        {
            RequireDescriptor();
            return true;
        }

        private void RequireDescriptor()
        {
            if (DescriptorReceived)
                return;
            if (RefusePreDescriptorAccess)
                PreDescriptorAccesses++;
            throw new InvalidOperationException("child coordinate observed before descriptor");
        }
    }

    private sealed class FakeAuthority : IFixtureBrokerAuthority
    {
        private readonly FixtureArtifactHandoffPlan _plan;
        private readonly FixtureBrokerParties _parties;
        private readonly FixtureBrokerSnapshot _snapshot;
        private readonly Dictionary<(string Label, nint Handle), FixtureBrokerDeliveryReceipt>
            _receipts;
        private readonly HashSet<Guid> _reservations = [];
        private readonly Dictionary<long, FixtureHandoffProcessIdentity> _connections = [];

        internal FakeAuthority(
            FixtureArtifactHandoffPlan plan,
            FixtureBrokerParties parties,
            FixtureBrokerSnapshot snapshot,
            IEnumerable<FixtureBrokerDeliveryReceipt> receipts)
        {
            _plan = plan;
            _parties = parties;
            _snapshot = snapshot;
            _receipts = receipts.ToDictionary(receipt => (receipt.Label, receipt.TargetHandle));
            _connections[FakeTransport.DescriptorReceipt] = parties.Issuer;
            _connections[13] = parties.Target;
        }

        internal int DescriptorPeerObservations { get; private set; }
        internal FixtureHandoffProcessIdentity DescriptorPeer { get; set; } = Launcher;
        internal FixtureHandoffProcessIdentity? DescriptorPeerAfterFirstObservation { get; set; }
        internal Action? BeforeNextDescriptorPeerReturn { get; set; }
        internal long ReceiptFor(FixtureHandoffProcessIdentity process) =>
            _connections.First(pair => pair.Value == process).Key;

        public bool TryReserveLaunch(Guid launchId) => _reservations.Add(launchId);
        public bool IsProcessAlive(FixtureHandoffProcessIdentity process) =>
            process == _parties.Creator || process == _parties.Issuer ||
            process == _parties.Target;
        public bool AreExpectedCreatorAndIssuer(
            FixtureHandoffEdge edge,
            FixtureHandoffProcessIdentity creator,
            FixtureHandoffProcessIdentity issuer) =>
            edge == _plan.Edge && creator == _parties.Creator && issuer == _parties.Issuer;
        public bool HasAuthenticatedCreatorSession(
            FixtureHandoffProcessIdentity creator,
            FixtureHandoffProcessIdentity issuer) => true;
        public bool IsCreatorRegistrationSourceValid(
            FixtureHandoffProcessIdentity creator,
            FixtureBrokerCreatorProcessCoordinate source,
            FixtureHandoffProcessIdentity target) =>
            creator == _parties.Creator && source.Value == (nint)501 &&
            target == _parties.Target;
        public bool IsIssuerTargetCustodyValid(
            FixtureHandoffProcessIdentity issuer,
            FixtureBrokerIssuerProcessCoordinate custody,
            FixtureHandoffProcessIdentity target) =>
            issuer == _parties.Issuer && custody.Value == (nint)601 &&
            target == _parties.Target;
        public bool IsExpectedBinary(
            FixtureHandoffProcessIdentity target,
            QualificationRole role) =>
            target == _parties.Target && role == QualificationRole.Runner;
        public bool IsExpectedContainment(
            FixtureHandoffProcessIdentity target,
            FixtureHandoffEdge edge) =>
            target == _parties.Target && edge == _plan.Edge;
        public FixtureBrokerDeliveryReceipt ObserveDeliveredHandle(
            FixtureHandoffProcessIdentity target,
            string label,
            nint targetHandle) => _receipts[(label, targetHandle)];
        public FixtureArtifactCloseStatus ObserveTransferClose(
            FixtureHandoffProcessIdentity target,
            nint targetHandle) => FixtureArtifactCloseStatus.Closed;
        public FixtureArtifactCloseStatus ObserveIssuerCustodyClose(
            FixtureHandoffProcessIdentity issuer,
            FixtureBrokerIssuerProcessCoordinate custody) => FixtureArtifactCloseStatus.Closed;
        public bool IsExactTargetReaped(FixtureHandoffProcessIdentity target) => true;
        public bool AreObserversDrained(Guid launchId) => true;
        public bool AreArtifactOperationsDrained(Guid launchId) => true;
        public bool IsOwnerDeathTopologySafe(
            FixtureBrokerSnapshot snapshot,
            FixtureBrokerOwnerDeathEvidence evidence) => true;

        public FixtureHandoffProcessIdentity ObserveConnectedPeer(long connectionReceipt)
        {
            if (connectionReceipt == FakeTransport.DescriptorReceipt)
            {
                DescriptorPeerObservations++;
                var beforeReturn = BeforeNextDescriptorPeerReturn;
                BeforeNextDescriptorPeerReturn = null;
                beforeReturn?.Invoke();
                if (DescriptorPeerObservations > 1 &&
                    DescriptorPeerAfterFirstObservation is not null)
                    return DescriptorPeerAfterFirstObservation;
                return DescriptorPeer;
            }
            return _connections[connectionReceipt];
        }

        public bool IsSnapshotStillAdmitted(FixtureBrokerSnapshot snapshot) =>
            snapshot.Edge == _snapshot.Edge && snapshot.Scenario == _snapshot.Scenario &&
            snapshot.RunNonce == _snapshot.RunNonce && snapshot.LaunchId == _snapshot.LaunchId &&
            snapshot.Parties == _snapshot.Parties;
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            lock (_gate)
                return _timestamp;
        }

        public override ITimer CreateTimer(
            TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            lock (_gate)
            {
                _timers.Add(timer);
                timer.ChangeUnderLock(dueTime, _timestamp);
            }
            return timer;
        }

        internal void SetTimestamp(long timestamp)
        {
            lock (_gate)
                _timestamp = timestamp;
        }

        internal void Advance(TimeSpan by)
        {
            List<ManualTimer> due;
            lock (_gate)
            {
                _timestamp = checked(_timestamp + by.Ticks);
                due = _timers.Where(timer => timer.MarkDueUnderLock(_timestamp)).ToList();
            }
            foreach (var timer in due)
                timer.Fire();
        }

        private sealed class ManualTimer(
            FakeTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long _dueAt = long.MaxValue;
            private bool _disposed;

            internal void ChangeUnderLock(TimeSpan dueTime, long now) =>
                _dueAt = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : checked(now + dueTime.Ticks);

            internal bool MarkDueUnderLock(long now)
            {
                if (_disposed || now < _dueAt)
                    return false;
                _dueAt = long.MaxValue;
                return true;
            }

            internal void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (_disposed)
                        return false;
                    ChangeUnderLock(dueTime, owner._timestamp);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner._gate)
                    _disposed = true;
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
