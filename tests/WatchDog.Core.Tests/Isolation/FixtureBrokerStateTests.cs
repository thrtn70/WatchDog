using System.Collections.Concurrent;
using System.Reflection;
using WatchDog.CaptureIsolation.Windows.Qualification;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureBrokerStateTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, 32)
        .Select(value => (byte)value).ToArray();
    private static readonly FixtureHandoffProcessIdentity Runner = new(100, 1_001);
    private static readonly FixtureHandoffProcessIdentity Owner = new(200, 2_002);
    private static readonly FixtureHandoffProcessIdentity Helper = new(300, 3_003);
    private static readonly FixtureHandoffProcessIdentity Descendant = new(400, 4_004);

    [Fact]
    public void BrokerErasesItsOwnedLaunchKeyOnFailureAndTerminalExit()
    {
        var failedFixture = NewFixture();
        var failed = Register(failedFixture);
        var failedKey = OwnedKey(failed);
        Assert.Equal(Secret, failedKey);
        failed.Fail(FixtureBrokerFailureKind.Assertion);
        Assert.All(failedKey, value => Assert.Equal(0, value));

        var activeFixture = NewFixture();
        var active = Activate(activeFixture);
        var activeKey = OwnedKey(active.Broker);
        Assert.Equal(Secret, activeKey);
        Drain(activeFixture, active.Broker);
        Assert.Equal(QualificationExitCode.Pass,
            active.Broker.ExitAfterCleanup(QualificationExitCode.Pass));
        Assert.All(activeKey, value => Assert.Equal(0, value));
    }

    [Fact]
    public void TerminalExitClassificationCannotBeUpgradedByAnotherRequest()
    {
        var fixture = NewFixture();
        var active = Activate(fixture);
        Drain(fixture, active.Broker);

        var first = active.Broker.ExitAfterCleanup(
            QualificationExitCode.AssertionFailedCleanupConfirmed);
        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed, first);
        Assert.Equal(first, active.Broker.ExitAfterCleanup(QualificationExitCode.Pass));
        Assert.All(OwnedKey(active.Broker), value => Assert.Equal(0, value));

        fixture = NewFixture();
        active = Activate(fixture);
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            active.Broker.ExitAfterCleanup(QualificationExitCode.Pass));
        Drain(fixture, active.Broker);
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            active.Broker.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void TerminalExitRejectsLateTargetAndDeliveryBeforeAdoptingCustody()
    {
        var fixture = NewFixture();
        var broker = Reserve(fixture);
        Assert.Equal(QualificationExitCode.UsageOrPrerequisite,
            broker.ExitAfterCleanup(QualificationExitCode.UsageOrPrerequisite));
        Assert.Throws<FixtureBrokerContractException>(() =>
            broker.RegisterTarget(fixture.Registration, fixture.Snapshot,
                fixture.Seal.Bytes, fixture.Seal.Mac, Secret));
        Assert.True(broker.CleanupConfirmed);

        fixture = NewFixture();
        var active = Activate(fixture);
        Drain(fixture, active.Broker);
        Assert.Equal(QualificationExitCode.Pass,
            active.Broker.ExitAfterCleanup(QualificationExitCode.Pass));
        Assert.Throws<FixtureBrokerContractException>(() =>
            active.Broker.RecordDelivery(fixture.Receipts[0] with
            {
                TargetHandle = (nint)999_999,
            }));
        Assert.True(active.Broker.CleanupConfirmed);
    }

    private static byte[] OwnedKey(FixtureBrokerState broker) =>
        (byte[])typeof(FixtureBrokerState)
            .GetField("_secret", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(broker)!;

    [Fact]
    public void GrantAckPrecedesCommitAndOnlyIssuerOriginActivationOpensChildFence()
    {
        var fixture = NewFixture();
        var exchange = BeginExchange(fixture);

        var grant = exchange.Broker.AcceptChildProof(
            Frame(fixture, fixture.Parties.Target, exchange.Child.Proof));
        exchange.Child.AcceptGrant(Frame(fixture, fixture.Parties.Issuer, grant));

        Assert.Equal(FixtureBrokerChildPhase.GrantAccepted, exchange.Child.Phase);
        Assert.False(exchange.Child.MayWrite);
        Assert.False(exchange.Child.MayLaunch);
        exchange.Broker.AcceptGrantAck(
            Frame(fixture, fixture.Parties.Target, exchange.Child.GrantAck));
        Assert.Equal(FixtureBrokerPhase.Committed, exchange.Broker.Phase);

        var activation = exchange.Broker.IssueActivation();
        Assert.False(exchange.Child.MayWrite); // Lost activation remains fenced.
        exchange.Child.AcceptActivation(Frame(fixture, fixture.Parties.Issuer, activation));
        exchange.Broker.ConfirmChildActive(
            Frame(fixture, fixture.Parties.Target, exchange.Child.ActivationAck));
        Assert.True(exchange.Child.MayWrite);
        Assert.True(exchange.Child.MayLaunch);

        fixture.Clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(exchange.Child.MayWrite); // Deadline governs registration through activation.
    }

    [Fact]
    public void WrongCreationTimeAndCreatorIssuerSubstitutionAreRejectedAfterCustodyAdoption()
    {
        var fixture = NewFixture();
        var broker = Reserve(fixture);
        var wrongTarget = fixture.Parties.Target with
        {
            CreationFileTime = fixture.Parties.Target.CreationFileTime + 1,
        };
        fixture.Authority.Alive.Add(wrongTarget);
        fixture.Authority.ReapedTargets.Add(wrongTarget);
        var exception = Assert.Throws<FixtureBrokerContractException>(() =>
            broker.RegisterTarget(fixture.Registration with { Target = wrongTarget },
                fixture.Snapshot, fixture.Seal.Bytes,
                fixture.Seal.Mac, Secret));
        Assert.Equal("fixture.broker.target-rejected", exception.Code);
        Assert.False(broker.CleanupConfirmed);
        broker.ConfirmIssuerCustodyClosed();
        broker.ConfirmExactTargetReaped(wrongTarget);
        broker.ConfirmObserversDrained();
        broker.ConfirmArtifactOperationsDrained();
        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            broker.ExitAfterCleanup(QualificationExitCode.Pass));

        fixture = NewFixture();
        broker = Reserve(fixture);
        var substituted = fixture.Snapshot with
        {
            Parties = new(fixture.Parties.Issuer, fixture.Parties.Creator,
                fixture.Parties.Target),
        };
        var substitutedSeal = FixtureBrokerSnapshotCodec.Seal(substituted, Secret);
        exception = Assert.Throws<FixtureBrokerContractException>(() =>
            broker.RegisterTarget(fixture.Registration, substituted,
                substitutedSeal.Bytes, substitutedSeal.Mac, Secret));
        Assert.Equal("fixture.broker.target-rejected", exception.Code);
    }

    [Fact]
    public void DroppedAckTimesOutAndRejectedAckFailsAssertionWithoutGrantingWrite()
    {
        var dropped = NewFixture();
        var droppedExchange = BeginExchange(dropped);
        var grant = droppedExchange.Broker.AcceptChildProof(
            Frame(dropped, dropped.Parties.Target, droppedExchange.Child.Proof));
        droppedExchange.Child.AcceptGrant(
            Frame(dropped, dropped.Parties.Issuer, grant));
        var droppedAck = droppedExchange.Child.GrantAck;
        dropped.Clock.Advance(QualificationConstants.ReadinessTimeout);
        Assert.Throws<FixtureBrokerContractException>(() =>
            droppedExchange.Broker.AcceptGrantAck(
                Frame(dropped, dropped.Parties.Target, droppedAck)));
        Drain(dropped, droppedExchange.Broker);
        Assert.Equal(QualificationExitCode.TimeoutCleanupConfirmed,
            droppedExchange.Broker.ExitAfterCleanup(QualificationExitCode.Pass));

        var rejected = NewFixture();
        var rejectedExchange = BeginExchange(rejected);
        grant = rejectedExchange.Broker.AcceptChildProof(
            Frame(rejected, rejected.Parties.Target, rejectedExchange.Child.Proof));
        rejectedExchange.Child.AcceptGrant(
            Frame(rejected, rejected.Parties.Issuer, grant));
        Assert.Throws<FixtureBrokerContractException>(() =>
            rejectedExchange.Broker.AcceptGrantAck(
                Frame(rejected, rejected.Parties.Target, new byte[32])));
        Assert.False(rejectedExchange.Child.MayWrite);
        Drain(rejected, rejectedExchange.Broker);
        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            rejectedExchange.Broker.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void WrongPeerLostAndReplayedActivationRemainFailClosed()
    {
        var wrongPeer = NewFixture();
        var wrongPeerExchange = Commit(wrongPeer);
        var activation = wrongPeerExchange.Broker.IssueActivation();
        Assert.Throws<FixtureBrokerContractException>(() =>
            wrongPeerExchange.Child.AcceptActivation(
                Frame(wrongPeer, wrongPeer.Parties.Creator, activation)));
        Assert.False(wrongPeerExchange.Child.MayWrite);

        var lost = NewFixture();
        var lostExchange = Commit(lost);
        _ = lostExchange.Broker.IssueActivation();
        lost.Clock.Advance(QualificationConstants.ReadinessTimeout);
        Assert.False(lostExchange.Child.MayWrite);
        Assert.Equal(FixtureBrokerChildPhase.Failed, lostExchange.Child.Phase);

        var replayed = NewFixture();
        var replayedExchange = Commit(replayed);
        activation = replayedExchange.Broker.IssueActivation();
        replayedExchange.Child.AcceptActivation(
            Frame(replayed, replayed.Parties.Issuer, activation));
        Assert.True(replayedExchange.Child.MayWrite);
        Assert.Throws<FixtureBrokerContractException>(() =>
            replayedExchange.Child.AcceptActivation(
                Frame(replayed, replayed.Parties.Issuer, activation)));
        Assert.False(replayedExchange.Child.MayWrite);
    }

    [Fact]
    public void CreatorIssuerAndTargetDeathEachTerminalizeCurrentPhase()
    {
        var creatorDeath = NewFixture();
        var broker = Reserve(creatorDeath);
        creatorDeath.Authority.Alive.Remove(creatorDeath.Parties.Creator);
        Assert.Throws<FixtureBrokerContractException>(() => broker.RegisterTarget(
            creatorDeath.Registration, creatorDeath.Snapshot,
            creatorDeath.Seal.Bytes, creatorDeath.Seal.Mac, Secret));
        Drain(creatorDeath, broker, closeTransfers: false);
        Assert.Equal(QualificationExitCode.InfrastructureExceptionCleanupConfirmed,
            broker.ExitAfterCleanup(QualificationExitCode.Pass));

        var issuerDeath = NewFixture();
        broker = RegisterAndDeliver(issuerDeath);
        issuerDeath.Authority.Alive.Remove(issuerDeath.Parties.Issuer);
        Assert.Throws<FixtureBrokerContractException>(broker.SealDelivery);
        Drain(issuerDeath, broker);
        Assert.Equal(QualificationExitCode.InfrastructureExceptionCleanupConfirmed,
            broker.ExitAfterCleanup(QualificationExitCode.Pass));

        var targetDeath = NewFixture();
        var exchange = BeginExchange(targetDeath);
        var grant = exchange.Broker.AcceptChildProof(
            Frame(targetDeath, targetDeath.Parties.Target, exchange.Child.Proof));
        exchange.Child.AcceptGrant(
            Frame(targetDeath, targetDeath.Parties.Issuer, grant));
        var targetAck = exchange.Child.GrantAck;
        targetDeath.Authority.Alive.Remove(targetDeath.Parties.Target);
        Assert.Throws<FixtureBrokerContractException>(() =>
            exchange.Broker.AcceptGrantAck(
                Frame(targetDeath, targetDeath.Parties.Target, targetAck)));
        Drain(targetDeath, exchange.Broker);
        Assert.Equal(QualificationExitCode.InfrastructureExceptionCleanupConfirmed,
            exchange.Broker.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void PartialAndAmbiguousDeliveryCannotPassCleanup()
    {
        var fixture = NewFixture();
        var broker = Register(fixture);
        broker.RecordDelivery(fixture.Receipts[0]);
        Assert.Throws<FixtureBrokerContractException>(broker.SealDelivery);
        fixture.Authority.CloseStatuses[fixture.Receipts[0].TargetHandle] =
            FixtureArtifactCloseStatus.Ambiguous;
        broker.ConfirmTransferClosed(fixture.Receipts[0].TargetHandle);
        fixture.Authority.CloseStatuses[fixture.Receipts[0].TargetHandle] =
            FixtureArtifactCloseStatus.Closed;
        broker.ConfirmTransferClosed(fixture.Receipts[0].TargetHandle);
        Drain(fixture, broker, closeTransfers: false);
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            broker.ExitAfterCleanup(QualificationExitCode.Pass));

        fixture = NewFixture();
        broker = Register(fixture);
        broker.RecordDelivery(fixture.Receipts[0]);
        var reused = fixture.Receipts[1] with
        {
            TargetHandle = fixture.Receipts[0].TargetHandle,
        };
        Assert.Throws<FixtureBrokerContractException>(() => broker.RecordDelivery(reused));
        broker.ConfirmTransferClosed(fixture.Receipts[0].TargetHandle);
        Drain(fixture, broker, closeTransfers: false);
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            broker.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void ChildRejectsDescriptorToObservedCoordinateSubstitution()
    {
        var fixture = NewFixture();
        var broker = RegisterAndDeliver(fixture);
        var descriptor = broker.SealDelivery();
        broker.MarkResumed();
        var first = fixture.Plan.Slots[0].Label;
        fixture.Observer.Slots[first] = fixture.Observer.Slots[first] with
        {
            RawValue = (nint)9_999,
        };
        Assert.ThrowsAny<Exception>(() => FixtureBrokerChildState.Accept(
            descriptor, fixture.Plan, fixture.Parties, Secret, fixture.Observer,
            fixture.Authority, fixture.Clock, CancellationToken.None));
    }

    [Fact]
    public void OwnerDeathArmingRequiresActiveDescendantAndAllIndependentEvidence()
    {
        var premature = NewFixture(FixtureHandoffEdge.HelperToDescendant,
            QualificationScenario.OwnerDeath, Helper, Runner, Descendant);
        var prematureBroker = RegisterAndDeliver(premature);
        Assert.Throws<FixtureBrokerContractException>(() => prematureBroker.ArmOwnerDeath(
            new(true, true, true, false, false)));

        var fixture = NewFixture(FixtureHandoffEdge.HelperToDescendant,
            QualificationScenario.OwnerDeath, Helper, Runner, Descendant);
        var exchange = Activate(fixture);
        exchange.Broker.ArmOwnerDeath(new(
            NoMoreSpawn: true,
            ExactMembership: true,
            OutputsReconciled: true,
            RunnerHoldsInnerJobHandle: false,
            PendingInnerJobTransfer: false));
        Assert.True(exchange.Broker.OwnerDeathArmed);
        Drain(fixture, exchange.Broker);
        Assert.Equal(QualificationExitCode.Pass,
            exchange.Broker.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void LaunchReservationIsOneUseAndCancellationAfterCreateRetainsCleanupOwnership()
    {
        var fixture = NewFixture();
        _ = Reserve(fixture);
        Assert.Throws<FixtureBrokerContractException>(() => Reserve(fixture));

        fixture = NewFixture();
        using var cancellation = new CancellationTokenSource();
        var broker = Reserve(fixture, cancellation.Token);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => broker.RegisterTarget(
            fixture.Registration, fixture.Snapshot, fixture.Seal.Bytes,
            fixture.Seal.Mac, Secret));
        Assert.False(broker.CleanupConfirmed);
        Drain(fixture, broker, closeTransfers: false);
        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            broker.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void RegistrationRejectsLostCreatorSourceAndIssuerCoordinateSubstitution()
    {
        var lostSource = NewFixture();
        lostSource.Authority.CreatorSourceValid = false;
        var broker = Reserve(lostSource);
        Assert.Throws<FixtureBrokerContractException>(() => broker.RegisterTarget(
            lostSource.Registration, lostSource.Snapshot, lostSource.Seal.Bytes,
            lostSource.Seal.Mac, Secret));
        Assert.Equal(FixtureBrokerPhase.Failed, broker.Phase);

        var substituted = NewFixture();
        broker = Reserve(substituted);
        var wrongCoordinates = substituted.Registration with
        {
            CreatorSource = new((nint)601),
            IssuerCustody = new((nint)501),
        };
        Assert.Throws<FixtureBrokerContractException>(() => broker.RegisterTarget(
            wrongCoordinates, substituted.Snapshot, substituted.Seal.Bytes,
            substituted.Seal.Mac, Secret));
        Assert.Equal(FixtureBrokerPhase.Failed, broker.Phase);
    }

    [Fact]
    public void SuccessfulDuplicateIsCleanupOwnedBeforeCancellationOrDeadlineCheck()
    {
        var canceled = NewFixture();
        using var cancellation = new CancellationTokenSource();
        var broker = Reserve(canceled, cancellation.Token);
        broker.RegisterTarget(canceled.Registration, canceled.Snapshot,
            canceled.Seal.Bytes, canceled.Seal.Mac, Secret);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            broker.RecordDelivery(canceled.Receipts[0]));
        Assert.False(broker.CleanupConfirmed);
        Drain(canceled, broker);
        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            broker.ExitAfterCleanup(QualificationExitCode.Pass));

        var expired = NewFixture();
        broker = Register(expired);
        expired.Clock.Advance(QualificationConstants.ReadinessTimeout);
        Assert.Throws<FixtureBrokerContractException>(() =>
            broker.RecordDelivery(expired.Receipts[0]));
        Assert.False(broker.CleanupConfirmed);
        Drain(expired, broker);
        Assert.Equal(QualificationExitCode.TimeoutCleanupConfirmed,
            broker.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void MalformedSnapshotAfterAdoptionTerminalizesWithoutReplacingOwnedTarget()
    {
        var fixture = NewFixture();
        var broker = Reserve(fixture);
        var malformed = fixture.Snapshot with { Outputs = null! };
        Assert.Throws<FixtureBrokerContractException>(() => broker.RegisterTarget(
            fixture.Registration, malformed, fixture.Seal.Bytes, fixture.Seal.Mac, Secret));
        var replacement = fixture.Parties.Target with
        {
            ProcessId = fixture.Parties.Target.ProcessId + 1,
            CreationFileTime = fixture.Parties.Target.CreationFileTime + 1,
        };
        fixture.Authority.Alive.Add(replacement);
        fixture.Authority.ReapedTargets.Add(replacement);
        Assert.Throws<FixtureBrokerContractException>(() => broker.RegisterTarget(
            fixture.Registration with { Target = replacement }, fixture.Snapshot,
            fixture.Seal.Bytes, fixture.Seal.Mac, Secret));
        broker.ConfirmIssuerCustodyClosed();
        broker.ConfirmExactTargetReaped(fixture.Parties.Target);
        broker.ConfirmObserversDrained();
        broker.ConfirmArtifactOperationsDrained();
        Assert.Equal(QualificationExitCode.InfrastructureExceptionCleanupConfirmed,
            broker.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void NullCreatedTargetIsCleanupUncertainAndCannotBeRetried()
    {
        var fixture = NewFixture();
        var broker = Reserve(fixture);
        var invalid = fixture.Registration with { Target = null! };
        var exception = Assert.Throws<FixtureBrokerContractException>(() =>
            broker.RegisterTarget(invalid, fixture.Snapshot, fixture.Seal.Bytes,
                fixture.Seal.Mac, Secret));
        Assert.Equal("fixture.broker.target-ownership-uncertain", exception.Code);
        Assert.Equal(FixtureBrokerPhase.Failed, broker.Phase);
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            broker.ExitAfterCleanup(QualificationExitCode.Pass));
        Assert.Throws<FixtureBrokerContractException>(() => broker.RegisterTarget(
            fixture.Registration, fixture.Snapshot, fixture.Seal.Bytes,
            fixture.Seal.Mac, Secret));
    }

    [Fact]
    public void ConcurrentAckAndActivationConsumptionAreSerializedAndOneUse()
    {
        var ackFixture = NewFixture();
        var ackExchange = BeginExchange(ackFixture);
        var grant = ackExchange.Broker.AcceptChildProof(
            Frame(ackFixture, ackFixture.Parties.Target, ackExchange.Child.Proof));
        ackExchange.Child.AcceptGrant(
            Frame(ackFixture, ackFixture.Parties.Issuer, grant));
        var ack = ackExchange.Child.GrantAck;
        var ackSuccesses = 0;
        var ackFailures = new ConcurrentBag<Exception>();
        Parallel.Invoke(
            () => Try(() => ackExchange.Broker.AcceptGrantAck(
                Frame(ackFixture, ackFixture.Parties.Target, ack)),
                ref ackSuccesses, ackFailures),
            () => Try(() => ackExchange.Broker.AcceptGrantAck(
                Frame(ackFixture, ackFixture.Parties.Target, ack)),
                ref ackSuccesses, ackFailures));
        Assert.Equal(1, ackSuccesses);
        Assert.Single(ackFailures);
        Assert.Equal(FixtureBrokerPhase.Failed, ackExchange.Broker.Phase);

        var activationFixture = NewFixture();
        var activationExchange = Commit(activationFixture);
        var activationSuccesses = 0;
        var activationFailures = new ConcurrentBag<Exception>();
        Parallel.Invoke(
            () => Try(() => _ = activationExchange.Broker.IssueActivation(),
                ref activationSuccesses, activationFailures),
            () => Try(() => _ = activationExchange.Broker.IssueActivation(),
                ref activationSuccesses, activationFailures));
        Assert.Equal(1, activationSuccesses);
        Assert.Single(activationFailures);
        Assert.Equal(FixtureBrokerPhase.Failed, activationExchange.Broker.Phase);
    }

    [Fact]
    public async Task ConcurrentFailureCannotBeOverwrittenByCommitOrPermitActivation()
    {
        var fixture = NewFixture();
        var exchange = BeginExchange(fixture);
        var grant = exchange.Broker.AcceptChildProof(
            Frame(fixture, fixture.Parties.Target, exchange.Child.Proof));
        exchange.Child.AcceptGrant(Frame(fixture, fixture.Parties.Issuer, grant));
        var ack = exchange.Child.GrantAck;
        fixture.Authority.BlockPeerObservation = true;

        var commit = Task.Run(() => exchange.Broker.AcceptGrantAck(
            Frame(fixture, fixture.Parties.Target, ack)));
        Assert.True(fixture.Authority.PeerObservationEntered.Wait(TestTimeouts.Signal));
        var fail = Task.Run(() =>
            exchange.Broker.Fail(FixtureBrokerFailureKind.CleanupAmbiguous));
        fixture.Authority.PeerObservationRelease.Set();
        await Task.WhenAll(commit, fail);

        Assert.Equal(FixtureBrokerPhase.Failed, exchange.Broker.Phase);
        Assert.Equal(FixtureBrokerFailureKind.CleanupAmbiguous, exchange.Broker.Failure);
        Assert.Throws<FixtureBrokerContractException>(exchange.Broker.IssueActivation);
    }

    [Fact]
    public async Task ChildFailureDuringLocalRevalidationCannotBeOverwrittenByActivation()
    {
        var fixture = NewFixture();
        var exchange = Commit(fixture);
        var activation = exchange.Broker.IssueActivation();
        fixture.Observer.BlockDeliveredObservation = true;
        fixture.Observer.ThrowOnDeliveredObservation = true;

        var write = Task.Run(() => exchange.Child.MayWrite);
        Assert.True(fixture.Observer.DeliveredObservationEntered.Wait(TestTimeouts.Signal));
        var activate = Task.Run(() => Record.Exception(() =>
            exchange.Child.AcceptActivation(
                Frame(fixture, fixture.Parties.Issuer, activation))));
        fixture.Observer.DeliveredObservationRelease.Set();

        Assert.False(await write);
        Assert.IsType<FixtureBrokerContractException>(await activate);
        Assert.Equal(FixtureBrokerChildPhase.Failed, exchange.Child.Phase);
        Assert.False(exchange.Child.MayWrite);
    }

    [Fact]
    public void AuthorityObservationExceptionsTerminalizeBrokerAndChild()
    {
        var brokerFixture = NewFixture();
        var brokerExchange = BeginExchange(brokerFixture);
        brokerFixture.Authority.ThrowOnPeerObservation = true;
        Assert.Throws<FixtureBrokerContractException>(() =>
            brokerExchange.Broker.AcceptChildProof(
                Frame(brokerFixture, brokerFixture.Parties.Target,
                    brokerExchange.Child.Proof)));
        Assert.Equal(FixtureBrokerFailureKind.Infrastructure,
            brokerExchange.Broker.Failure);

        var childFixture = NewFixture();
        var childExchange = BeginExchange(childFixture);
        var grant = childExchange.Broker.AcceptChildProof(
            Frame(childFixture, childFixture.Parties.Target, childExchange.Child.Proof));
        childFixture.Authority.ThrowOnPeerObservation = true;
        Assert.Throws<FixtureBrokerContractException>(() =>
            childExchange.Child.AcceptGrant(
                Frame(childFixture, childFixture.Parties.Issuer, grant)));
        Assert.Equal(FixtureBrokerChildPhase.Failed, childExchange.Child.Phase);
    }

    [Fact]
    public void PassReobservesCleanupAndMutationCannotReturnPrerequisiteCode()
    {
        var drift = NewFixture();
        var active = Activate(drift);
        Drain(drift, active.Broker);
        drift.Authority.ObserversDrained = false;
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            active.Broker.ExitAfterCleanup(QualificationExitCode.Pass));

        var nonPassDrift = NewFixture();
        active = Activate(nonPassDrift);
        Drain(nonPassDrift, active.Broker);
        nonPassDrift.Authority.ObserversDrained = false;
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            active.Broker.ExitAfterCleanup(
                QualificationExitCode.AssertionFailedCleanupConfirmed));

        var prerequisite = NewFixture();
        var broker = Register(prerequisite);
        broker.Fail(FixtureBrokerFailureKind.Prerequisite);
        Drain(prerequisite, broker, closeTransfers: false);
        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            broker.ExitAfterCleanup(QualificationExitCode.UsageOrPrerequisite));

        var drainedEarly = NewFixture();
        broker = RegisterAndDeliver(drainedEarly);
        _ = broker.SealDelivery();
        broker.ConfirmObserversDrained();
        Assert.Throws<FixtureBrokerContractException>(broker.MarkResumed);
    }

    public static TheoryData<int, string> DeathCases
    {
        get
        {
            var data = new TheoryData<int, string>();
            foreach (var phase in new[]
            {
                FixtureBrokerPhase.Reserved,
                FixtureBrokerPhase.TargetRegistered,
                FixtureBrokerPhase.Delivering,
                FixtureBrokerPhase.Sealed,
                FixtureBrokerPhase.Resumed,
                FixtureBrokerPhase.GrantIssued,
                FixtureBrokerPhase.Committed,
                FixtureBrokerPhase.ActivationIssued,
            })
            {
                data.Add((int)phase, "creator");
                data.Add((int)phase, "issuer");
                if (phase != FixtureBrokerPhase.Reserved)
                    data.Add((int)phase, "target");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DeathCases))]
    public void PartyDeathTerminalizesEveryPreActivationPhase(
        int phaseValue, string party)
    {
        var phase = (FixtureBrokerPhase)phaseValue;
        var fixture = NewFixture();
        var (broker, trigger) = ReachPhase(fixture, phase);
        var identity = party switch
        {
            "creator" => fixture.Parties.Creator,
            "issuer" => fixture.Parties.Issuer,
            "target" => fixture.Parties.Target,
            _ => throw new ArgumentOutOfRangeException(nameof(party)),
        };
        fixture.Authority.Alive.Remove(identity);

        Assert.ThrowsAny<Exception>(trigger);
        Assert.Equal(FixtureBrokerPhase.Failed, broker.Phase);
        Assert.Equal(FixtureBrokerFailureKind.Infrastructure, broker.Failure);
    }

    private static (FixtureBrokerState Broker, Action Trigger) ReachPhase(
        TestFixture fixture, FixtureBrokerPhase phase)
    {
        if (phase == FixtureBrokerPhase.Reserved)
        {
            var reserved = Reserve(fixture);
            return (reserved, () => reserved.RegisterTarget(fixture.Registration,
                fixture.Snapshot, fixture.Seal.Bytes, fixture.Seal.Mac, Secret));
        }
        if (phase == FixtureBrokerPhase.TargetRegistered)
        {
            var registered = Register(fixture);
            return (registered, () => registered.RecordDelivery(fixture.Receipts[0]));
        }
        if (phase == FixtureBrokerPhase.Delivering)
        {
            var delivering = Register(fixture);
            delivering.RecordDelivery(fixture.Receipts[0]);
            return (delivering, () => delivering.RecordDelivery(fixture.Receipts[1]));
        }
        if (phase == FixtureBrokerPhase.Sealed)
        {
            var sealedBroker = RegisterAndDeliver(fixture);
            _ = sealedBroker.SealDelivery();
            return (sealedBroker, sealedBroker.MarkResumed);
        }
        var exchange = BeginExchange(fixture);
        if (phase == FixtureBrokerPhase.Resumed)
        {
            var proof = exchange.Child.Proof;
            return (exchange.Broker, () => exchange.Broker.AcceptChildProof(
                Frame(fixture, fixture.Parties.Target, proof)));
        }
        var grant = exchange.Broker.AcceptChildProof(
            Frame(fixture, fixture.Parties.Target, exchange.Child.Proof));
        exchange.Child.AcceptGrant(Frame(fixture, fixture.Parties.Issuer, grant));
        var ack = exchange.Child.GrantAck;
        if (phase == FixtureBrokerPhase.GrantIssued)
            return (exchange.Broker, () => exchange.Broker.AcceptGrantAck(
                Frame(fixture, fixture.Parties.Target, ack)));
        exchange.Broker.AcceptGrantAck(Frame(fixture, fixture.Parties.Target, ack));
        if (phase == FixtureBrokerPhase.Committed)
            return (exchange.Broker, () => _ = exchange.Broker.IssueActivation());
        var activation = exchange.Broker.IssueActivation();
        exchange.Child.AcceptActivation(Frame(fixture, fixture.Parties.Issuer, activation));
        var activationAck = exchange.Child.ActivationAck;
        return (exchange.Broker, () => exchange.Broker.ConfirmChildActive(
            Frame(fixture, fixture.Parties.Target, activationAck)));
    }

    private static void Try(
        Action action, ref int successes, ConcurrentBag<Exception> failures)
    {
        try
        {
            action();
            Interlocked.Increment(ref successes);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private static Exchange Activate(TestFixture fixture)
    {
        var exchange = Commit(fixture);
        var activation = exchange.Broker.IssueActivation();
        exchange.Child.AcceptActivation(Frame(fixture, fixture.Parties.Issuer, activation));
        exchange.Broker.ConfirmChildActive(
            Frame(fixture, fixture.Parties.Target, exchange.Child.ActivationAck));
        return exchange;
    }

    private static Exchange Commit(TestFixture fixture)
    {
        var exchange = BeginExchange(fixture);
        var grant = exchange.Broker.AcceptChildProof(
            Frame(fixture, fixture.Parties.Target, exchange.Child.Proof));
        exchange.Child.AcceptGrant(Frame(fixture, fixture.Parties.Issuer, grant));
        exchange.Broker.AcceptGrantAck(
            Frame(fixture, fixture.Parties.Target, exchange.Child.GrantAck));
        return exchange;
    }

    private static Exchange BeginExchange(TestFixture fixture)
    {
        var broker = RegisterAndDeliver(fixture);
        var descriptor = broker.SealDelivery();
        broker.MarkResumed();
        var child = FixtureBrokerChildState.Accept(descriptor, fixture.Plan,
            fixture.Parties, Secret, fixture.Observer, fixture.Authority,
            fixture.Clock, CancellationToken.None);
        return new(broker, child);
    }

    private static FixtureBrokerState RegisterAndDeliver(TestFixture fixture)
    {
        var broker = Register(fixture);
        foreach (var receipt in fixture.Receipts)
            broker.RecordDelivery(receipt);
        return broker;
    }

    private static FixtureBrokerState Register(TestFixture fixture)
    {
        var broker = Reserve(fixture);
        broker.RegisterTarget(fixture.Registration, fixture.Snapshot,
            fixture.Seal.Bytes, fixture.Seal.Mac, Secret);
        return broker;
    }

    private static FixtureBrokerState Reserve(
        TestFixture fixture, CancellationToken lifetime = default) =>
        FixtureBrokerState.Reserve(fixture.Plan, fixture.Parties.Creator,
            fixture.Parties.Issuer, fixture.Snapshot.Delivery, fixture.Authority,
            fixture.Clock, QualificationConstants.ReadinessTimeout, lifetime);

    private static FixtureBrokerFrameObservation Frame(
        TestFixture fixture,
        FixtureHandoffProcessIdentity peer,
        byte[] frame) => new(fixture.Authority.ReceiptFor(peer), frame);

    private static void Drain(
        TestFixture fixture, FixtureBrokerState broker, bool closeTransfers = true)
    {
        if (closeTransfers)
            foreach (var receipt in fixture.Receipts)
            {
                try
                {
                    broker.ConfirmTransferClosed(receipt.TargetHandle);
                }
                catch (FixtureBrokerContractException)
                {
                    // The receipt was never successfully duplicated.
                }
            }
        try { broker.ConfirmIssuerCustodyClosed(); }
        catch (FixtureBrokerContractException) { }
        broker.ConfirmExactTargetReaped(fixture.Parties.Target);
        broker.ConfirmObserversDrained();
        broker.ConfirmArtifactOperationsDrained();
    }

    private static TestFixture NewFixture(
        FixtureHandoffEdge edge = FixtureHandoffEdge.OwnerToHelper,
        QualificationScenario scenario = QualificationScenario.OwnerDeath,
        FixtureHandoffProcessIdentity? creator = null,
        FixtureHandoffProcessIdentity? issuer = null,
        FixtureHandoffProcessIdentity? target = null)
    {
        creator ??= Owner;
        issuer ??= Runner;
        target ??= Helper;
        var plan = FixtureArtifactHandoffPlan.Create(
            edge, scenario, Guid.NewGuid(), Guid.NewGuid());
        var parties = new FixtureBrokerParties(creator, issuer, target);
        var directory = new FixtureArtifactIdentity(
            7, 1, 0, FixtureArtifactKind.Directory, false);
        var outputs = FixtureArtifactHandoffLedger.ExpectedEntries(scenario)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select((pair, index) => new FixtureBrokerOutput(
                pair.Key, pair.Value.Writer, pair.Value.WriterRights,
                new(7, (ulong)(index + 10), 0, FixtureArtifactKind.File, false),
                directory))
            .ToArray();
        var delivery = edge == FixtureHandoffEdge.LauncherToRunner
            ? FixtureBrokerDeliveryMode.LocalLauncher
            : FixtureBrokerDeliveryMode.DirectIssuer;
        var snapshot = new FixtureBrokerSnapshot(edge, scenario,
            plan.RunNonce, plan.LaunchId, parties, delivery, directory, outputs);
        var registration = new FixtureBrokerTargetRegistration(target,
            new((nint)501), new((nint)601));
        var receipts = plan.Slots.Select((slot, index) =>
            new FixtureBrokerDeliveryReceipt(slot.Label, (nint)(1_000 + index),
                slot.Kind, slot.Rights, SlotIdentity(snapshot, slot))).ToArray();
        var observer = new FakeChildObserver(snapshot, receipts, Secret);
        var authority = new FakeAuthority(parties, edge, snapshot, receipts);
        var seal = FixtureBrokerSnapshotCodec.Seal(snapshot, Secret);
        return new(plan, parties, registration, snapshot, receipts, observer, authority,
            new FakeTimeProvider(), seal);
    }

    private static FixtureArtifactIdentity? SlotIdentity(
        FixtureBrokerSnapshot snapshot, FixtureHandoffSlot slot)
    {
        if (slot.Kind == FixtureHandoffHandleKind.Directory)
            return snapshot.Directory;
        if (slot.Kind != FixtureHandoffHandleKind.File)
            return null;
        var name = slot.Label[(slot.Label.IndexOf(':') + 1)..];
        return snapshot.Outputs.Single(output => output.Name == name).Identity;
    }

    private sealed record Exchange(
        FixtureBrokerState Broker, FixtureBrokerChildState Child);

    private sealed record TestFixture(
        FixtureArtifactHandoffPlan Plan,
        FixtureBrokerParties Parties,
        FixtureBrokerTargetRegistration Registration,
        FixtureBrokerSnapshot Snapshot,
        FixtureBrokerDeliveryReceipt[] Receipts,
        FakeChildObserver Observer,
        FakeAuthority Authority,
        FakeTimeProvider Clock,
        (byte[] Bytes, byte[] Mac) Seal);

    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        internal void Advance(TimeSpan value) => _timestamp += value.Ticks;
    }

    private sealed class FakeAuthority : IFixtureBrokerAuthority
    {
        private readonly FixtureBrokerParties _parties;
        private readonly FixtureHandoffEdge _edge;
        private readonly FixtureBrokerSnapshot _snapshot;
        private readonly Dictionary<(string Label, nint Handle),
            FixtureBrokerDeliveryReceipt> _receipts;
        private readonly HashSet<Guid> _reservations = [];
        private readonly Dictionary<long, FixtureHandoffProcessIdentity> _connections = [];

        internal FakeAuthority(
            FixtureBrokerParties parties,
            FixtureHandoffEdge edge,
            FixtureBrokerSnapshot snapshot,
            IEnumerable<FixtureBrokerDeliveryReceipt> receipts)
        {
            _parties = parties;
            _edge = edge;
            _snapshot = snapshot;
            _receipts = receipts.ToDictionary(
                receipt => (receipt.Label, receipt.TargetHandle));
            Alive.UnionWith([parties.Creator, parties.Issuer, parties.Target]);
            ReapedTargets.Add(parties.Target);
            foreach (var receipt in receipts)
                CloseStatuses[receipt.TargetHandle] = FixtureArtifactCloseStatus.Closed;
            _connections[11] = parties.Creator;
            _connections[12] = parties.Issuer;
            _connections[13] = parties.Target;
        }

        internal HashSet<FixtureHandoffProcessIdentity> Alive { get; } = [];
        internal HashSet<FixtureHandoffProcessIdentity> ReapedTargets { get; } = [];
        internal Dictionary<nint, FixtureArtifactCloseStatus> CloseStatuses { get; } = [];
        internal bool SnapshotAdmitted { get; set; } = true;
        internal bool ObserversDrained { get; set; } = true;
        internal bool ArtifactOperationsDrained { get; set; } = true;
        internal bool OwnerDeathTopologySafe { get; set; } = true;
        internal FixtureArtifactCloseStatus IssuerCustodyCloseStatus { get; set; } =
            FixtureArtifactCloseStatus.Closed;
        internal bool CreatorSourceValid { get; set; } = true;
        internal bool IssuerCustodyValid { get; set; } = true;
        internal bool ThrowOnPeerObservation { get; set; }
        internal bool BlockPeerObservation { get; set; }
        internal ManualResetEventSlim PeerObservationEntered { get; } = new(false);
        internal ManualResetEventSlim PeerObservationRelease { get; } = new(false);
        internal long ReceiptFor(FixtureHandoffProcessIdentity process) =>
            _connections.First(pair => pair.Value == process).Key;
        public bool TryReserveLaunch(Guid launchId) => _reservations.Add(launchId);
        public bool IsProcessAlive(FixtureHandoffProcessIdentity process) =>
            Alive.Contains(process);
        public bool AreExpectedCreatorAndIssuer(
            FixtureHandoffEdge edge,
            FixtureHandoffProcessIdentity creator,
            FixtureHandoffProcessIdentity issuer) =>
            edge == _edge && creator == _parties.Creator && issuer == _parties.Issuer;
        public bool HasAuthenticatedCreatorSession(
            FixtureHandoffProcessIdentity creator,
            FixtureHandoffProcessIdentity issuer) =>
            creator == _parties.Creator && issuer == _parties.Issuer;
        public bool IsCreatorRegistrationSourceValid(
            FixtureHandoffProcessIdentity creator,
            FixtureBrokerCreatorProcessCoordinate source,
            FixtureHandoffProcessIdentity target) =>
            CreatorSourceValid && creator == _parties.Creator &&
            source.Value == (nint)501 && target == _parties.Target;
        public bool IsIssuerTargetCustodyValid(
            FixtureHandoffProcessIdentity issuer,
            FixtureBrokerIssuerProcessCoordinate custody,
            FixtureHandoffProcessIdentity target) =>
            IssuerCustodyValid && issuer == _parties.Issuer &&
            custody.Value == (nint)601 && target == _parties.Target;
        public bool IsExpectedBinary(
            FixtureHandoffProcessIdentity target, QualificationRole role) =>
            target == _parties.Target;
        public bool IsExpectedContainment(
            FixtureHandoffProcessIdentity target, FixtureHandoffEdge edge) =>
            target == _parties.Target && edge == _edge;
        public FixtureBrokerDeliveryReceipt ObserveDeliveredHandle(
            FixtureHandoffProcessIdentity target, string label, nint targetHandle) =>
            target == _parties.Target
                ? _receipts[(label, targetHandle)]
                : throw new InvalidOperationException();
        public FixtureArtifactCloseStatus ObserveTransferClose(
            FixtureHandoffProcessIdentity target, nint targetHandle) =>
            CloseStatuses[targetHandle];
        public FixtureArtifactCloseStatus ObserveIssuerCustodyClose(
            FixtureHandoffProcessIdentity issuer,
            FixtureBrokerIssuerProcessCoordinate custody) =>
            issuer == _parties.Issuer && custody.Value == (nint)601
                ? IssuerCustodyCloseStatus
                : FixtureArtifactCloseStatus.Ambiguous;
        public bool IsExactTargetReaped(FixtureHandoffProcessIdentity target) =>
            ReapedTargets.Contains(target);
        public bool AreObserversDrained(Guid launchId) => ObserversDrained;
        public bool AreArtifactOperationsDrained(Guid launchId) =>
            ArtifactOperationsDrained;
        public bool IsOwnerDeathTopologySafe(
            FixtureBrokerSnapshot snapshot, FixtureBrokerOwnerDeathEvidence evidence) =>
            OwnerDeathTopologySafe && evidence.NoMoreSpawn && evidence.ExactMembership &&
            evidence.OutputsReconciled && !evidence.RunnerHoldsInnerJobHandle &&
            !evidence.PendingInnerJobTransfer;
        public FixtureHandoffProcessIdentity ObserveConnectedPeer(long connectionReceipt)
        {
            if (ThrowOnPeerObservation)
                throw new InvalidOperationException("injected peer observation failure");
            if (BlockPeerObservation)
            {
                PeerObservationEntered.Set();
                PeerObservationRelease.Wait(TestTimeouts.Signal);
            }
            return _connections[connectionReceipt];
        }
        public bool IsSnapshotStillAdmitted(FixtureBrokerSnapshot snapshot) =>
            SnapshotAdmitted && snapshot == _snapshot with { Outputs = snapshot.Outputs };
    }

    private sealed class FakeChildObserver : IFixtureBrokerChildLocalObserver
    {
        private readonly FixtureBrokerSnapshot _snapshot;
        private readonly byte[] _secret;

        internal FakeChildObserver(
            FixtureBrokerSnapshot snapshot,
            IEnumerable<FixtureBrokerDeliveryReceipt> receipts,
            byte[] secret)
        {
            _snapshot = snapshot;
            _secret = (byte[])secret.Clone();
            Outputs = snapshot.Outputs.ToDictionary(output => output.Name,
                output => output.Identity, StringComparer.Ordinal);
            Slots = receipts.ToDictionary(receipt => receipt.Label,
                receipt => new FixtureHandoffObservedHandle(receipt.TargetHandle,
                    receipt.Kind, receipt.Rights, receipt.Identity),
                StringComparer.Ordinal);
        }

        internal Dictionary<string, FixtureArtifactIdentity> Outputs { get; }
        internal Dictionary<string, FixtureHandoffObservedHandle> Slots { get; }
        internal bool BlockDeliveredObservation { get; set; }
        internal bool ThrowOnDeliveredObservation { get; set; }
        internal ManualResetEventSlim DeliveredObservationEntered { get; } = new(false);
        internal ManualResetEventSlim DeliveredObservationRelease { get; } = new(false);
        public FixtureHandoffProcessIdentity ObserveTargetProcess() =>
            _snapshot.Parties.Target;
        public FixtureHandoffProcessIdentity ObserveCreatorProcess() =>
            _snapshot.Parties.Creator;
        public FixtureHandoffProcessIdentity ObserveIssuerProcess() =>
            _snapshot.Parties.Issuer;
        public FixtureArtifactIdentity ObserveRetainedDirectory() => _snapshot.Directory;
        public FixtureArtifactIdentity ObserveOutput(string literalName) => Outputs[literalName];
        public int DeliveredSlotCount => Slots.Count;
        public FixtureHandoffObservedHandle ObserveDeliveredSlot(string label)
        {
            if (BlockDeliveredObservation)
            {
                DeliveredObservationEntered.Set();
                DeliveredObservationRelease.Wait(TestTimeouts.Signal);
            }
            if (ThrowOnDeliveredObservation)
                throw new InvalidOperationException("injected child-local observation failure");
            return Slots[label];
        }
        public FixtureHandoffProcessIdentity ObserveProcessAtTargetCoordinate(
            nint coordinate) => _snapshot.Parties.Creator;
        public byte[] ReadSectionKeyAtTargetCoordinate(nint coordinate) =>
            (byte[])_secret.Clone();
        public bool IsExactChild(
            FixtureArtifactIdentity parent,
            string literalName,
            FixtureArtifactIdentity output) =>
            parent == _snapshot.Directory && Outputs[literalName] == output;
    }
}
