using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureArtifactHandoffTests
{
    private static readonly FixtureHandoffProcessIdentity Parent = new(40, 1000);
    private static readonly FixtureHandoffProcessIdentity Child = new(41, 2000);
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

    [Fact]
    public void InventoriesGrantOnlyReceiverWritesAndNeededCoordinationReads()
    {
        var owner = Plan(FixtureHandoffEdge.RunnerToOwner, QualificationScenario.OwnerDeath);
        Assert.Contains(owner.Slots, slot => slot.Label == "write:owner-armed.json");
        Assert.Contains(owner.Slots, slot => slot.Label == "read:helper-ready.json" &&
            slot.Rights == FixtureHandoffRights.FileRead);
        Assert.DoesNotContain(owner.Slots, slot =>
            slot.Label.StartsWith("write:helper-", StringComparison.Ordinal) ||
            slot.Label.StartsWith("write:descendant-", StringComparison.Ordinal));
        Assert.Equal(FixtureHandoffRights.DirectoryObserve, owner.Slots[2].Rights);

        var helper = Plan(FixtureHandoffEdge.OwnerToHelper, QualificationScenario.OwnerDeath);
        Assert.Contains(helper.Slots, slot => slot.Label == "read:descendant-startup.json");
        Assert.DoesNotContain(helper.Slots, slot =>
            slot.Label.StartsWith("write:descendant-", StringComparison.Ordinal));
        var descendant = Plan(FixtureHandoffEdge.HelperToDescendant,
            QualificationScenario.OwnerDeath);
        Assert.Contains(descendant.Slots, slot => slot.Label == "write:descendant-startup.json");

        var bootstrap = Plan(FixtureHandoffEdge.RunnerToBootstrapHelper,
            QualificationScenario.RejectedBootstrapMaterial);
        Assert.Equal("bootstrap-section", bootstrap.Slots[3].Label);
        Assert.Equal("bootstrap-parent-proof", bootstrap.Slots[4].Label);
        Assert.Equal(FixtureHandoffRights.ProcessQueryLimited |
            FixtureHandoffRights.Synchronize, bootstrap.Slots[4].Rights);
        Assert.Contains(bootstrap.Slots, slot => slot.Label == "write:bootstrap-ready.json");
        Assert.DoesNotContain(bootstrap.Slots, slot => slot.Label.Contains("sentinel"));

        var runner = Plan(FixtureHandoffEdge.LauncherToRunner, null);
        Assert.Equal(FixtureHandoffRights.DirectoryObserve |
            FixtureHandoffRights.DirectoryCreateFile |
            FixtureHandoffRights.DirectoryCreateDirectory, runner.Slots[2].Rights);
        Assert.Contains(runner.Slots, slot => slot.Label == "write:report.md");
        Assert.DoesNotContain(runner.Slots, slot => slot.Label.Contains(".tmp"));
    }

    [Fact]
    public void PlanAndLedgerRejectUnsupportedScenariosMissingOutputsAndForeignIdentity()
    {
        Assert.Throws<FixtureHandoffContractException>(() =>
            FixtureArtifactHandoffPlan.Create(FixtureHandoffEdge.OwnerToHelper,
                QualificationScenario.AuthenticatedBootstrap, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<FixtureHandoffContractException>(() =>
            FixtureArtifactHandoffPlan.Create(FixtureHandoffEdge.LauncherToRunner,
                QualificationScenario.OwnerDeath, Guid.NewGuid(), Guid.NewGuid()));
        Assert.Throws<FixtureHandoffContractException>(() =>
            FixtureArtifactHandoffPlan.Create(FixtureHandoffEdge.HelperToDescendant,
                QualificationScenario.AtomicPublicationProbe, Guid.NewGuid(), Guid.NewGuid()));

        var entries = Entries(QualificationScenario.OwnerDeath);
        var authority = NewAuthority();
        var outputObserver = new FakeOutputObserver(entries);
        Assert.Equal("fixture.handoff.ledger-invalid", Assert.Throws<FixtureHandoffContractException>(
            () => FixtureArtifactHandoffLedger.Create(QualificationScenario.OwnerDeath,
                authority, authority.Root, outputObserver, entries.Skip(1))).Code);
        var foreign = entries.ToArray();
        foreign[0] = foreign[0] with
        {
            Identity = foreign[0].Identity with { Volume = 8 },
        };
        Assert.Equal("fixture.handoff.ledger-invalid", Assert.Throws<FixtureHandoffContractException>(
            () => FixtureArtifactHandoffLedger.Create(QualificationScenario.OwnerDeath,
                authority, authority.Root, outputObserver, foreign)).Code);
        var alias = entries.ToArray();
        alias[1] = alias[1] with { Identity = alias[0].Identity };
        Assert.Throws<FixtureHandoffContractException>(() =>
            FixtureArtifactHandoffLedger.Create(QualificationScenario.OwnerDeath,
                authority, authority.Root, outputObserver, alias));
    }

    [Fact]
    public void IndependentChildObservationAndTwoDirectionProofGateWriting()
    {
        var fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.Parent.MarkResumed(CancellationToken.None);
        var child = FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
            fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None);
        Assert.False(child.MayWrite);
        Assert.Equal("fixture.handoff.child-proof-rejected", Assert.Throws<FixtureHandoffContractException>(
            () => fixture.Parent.AcceptChildProof(
                new FakeProofSource(Child, descriptor.Mac), CancellationToken.None)).Code);

        fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.Parent.MarkResumed(CancellationToken.None);
        child = FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
            fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None);
        var grant = fixture.Parent.AcceptChildProof(
            new FakeProofSource(Child, child.Proof), CancellationToken.None);
        Assert.False(child.MayWrite);
        child.AcceptParentGrant(new FakeParentGrantSource(Parent, grant), CancellationToken.None);
        Assert.True(child.MayWrite);
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            fixture.Parent.ExitAfterCleanup(QualificationExitCode.Pass));
        fixture.Parent.ConfirmChildGrantAccepted(
            new FakeGrantAckSource(Child, child.GrantAck), CancellationToken.None);
        foreach (var claim in fixture.Claims)
            fixture.Parent.ConfirmTransferClosed(claim.RawValue, FixtureArtifactCloseStatus.Closed);
        fixture.Parent.ConfirmExactChildReaped(200, processExited: true, jobEmpty: true);
        fixture.ParentProbe.DeadProcesses.Add(200);
        fixture.ChildProbe.DeadProcesses.Add(200);
        fixture.Parent.ConfirmObserversDrained();
        fixture.Parent.ConfirmArtifactOperationsDrained();
        Assert.Equal(QualificationExitCode.Pass,
            fixture.Parent.ExitAfterCleanup(QualificationExitCode.Pass));

        var changed = NewFixture();
        changed.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        descriptor = changed.Parent.Seal(CancellationToken.None);
        changed.Parent.MarkResumed(CancellationToken.None);
        child = FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
            changed.ChildProbe, 200, changed.Claims, CancellationToken.None);
        grant = changed.Parent.AcceptChildProof(
            new FakeProofSource(Child, child.Proof), CancellationToken.None);
        child.AcceptParentGrant(new FakeParentGrantSource(Parent, grant), CancellationToken.None);
        changed.Parent.ConfirmChildGrantAccepted(
            new FakeGrantAckSource(Child, child.GrantAck), CancellationToken.None);
        foreach (var claim in changed.Claims)
            changed.Parent.ConfirmTransferClosed(claim.RawValue, FixtureArtifactCloseStatus.Closed);
        changed.Parent.ConfirmExactChildReaped(200, true, true);
        changed.Parent.ConfirmObserversDrained();
        changed.Parent.ConfirmArtifactOperationsDrained();
        changed.OutputObserver.Current["owner-result.json"] =
            changed.OutputObserver.Current["owner-result.json"] with { FileIdLow = 999 };
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            changed.Parent.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void ChildRejectsUnrelatedParentProofDespiteCorrectRetainedParentHandle()
    {
        var fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var descriptor = fixture.Parent.Seal(CancellationToken.None);
        var proofHandle = fixture.Claims[1].RawValue;
        fixture.ChildProbe.Processes[proofHandle] = new(99, 9999);
        Assert.Equal("fixture.handoff.parent-mismatch", Assert.Throws<FixtureHandoffContractException>(
            () => FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
                fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None)).Code);
    }

    [Fact]
    public void ChildRejectsSameVolumeForeignFileAndBrokenRegisteredParentage()
    {
        var fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var descriptor = fixture.Parent.Seal(CancellationToken.None);
        var outputHandle = fixture.Claims[^1].RawValue;
        fixture.ChildProbe.Handles[outputHandle] = fixture.ChildProbe.Handles[outputHandle] with
        {
            Identity = new FixtureArtifactIdentity(7, 999, 0, FixtureArtifactKind.File, false),
        };
        Assert.Equal("fixture.handoff.identity-mismatch", Assert.Throws<FixtureHandoffContractException>(
            () => FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
                fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None)).Code);

        fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.ChildProbe.RegisteredNames.Clear();
        Assert.Equal("fixture.handoff.identity-mismatch", Assert.Throws<FixtureHandoffContractException>(
            () => FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
                fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None)).Code);
    }

    [Fact]
    public void WrongRightsMissingExtraDuplicateOrWrongOrderCannotBecomeProof()
    {
        var fixture = NewFixture();
        var plan = fixture.Plan;
        var claims = fixture.Claims;
        Assert.Equal("fixture.handoff.plan-invalid", Assert.Throws<FixtureHandoffContractException>(
            () => NewParent(plan, fixture.Ledger, fixture.ParentProbe, claims.Skip(1).ToArray(),
                CancellationToken.None)).Code);
        Assert.Equal("fixture.handoff.plan-invalid", Assert.Throws<FixtureHandoffContractException>(
            () => NewParent(plan, fixture.Ledger, fixture.ParentProbe,
                claims.Append(new("sentinel", 999)).ToArray(), CancellationToken.None)).Code);
        var wrongOrder = claims.ToArray();
        (wrongOrder[0], wrongOrder[1]) = (wrongOrder[1], wrongOrder[0]);
        Assert.Throws<FixtureHandoffContractException>(() =>
            NewParent(plan, fixture.Ledger, fixture.ParentProbe, wrongOrder,
                CancellationToken.None));
        var duplicate = claims.ToArray();
        duplicate[1] = duplicate[1] with { RawValue = duplicate[0].RawValue };
        Assert.Throws<FixtureHandoffContractException>(() =>
            NewParent(plan, fixture.Ledger, fixture.ParentProbe, duplicate,
                CancellationToken.None));
        var proofHandle = claims[1].RawValue;
        fixture.ParentProbe.Handles[proofHandle] = fixture.ParentProbe.Handles[proofHandle] with
        {
            Rights = FixtureHandoffRights.ProcessQueryLimited |
                FixtureHandoffRights.Synchronize | FixtureHandoffRights.FileWrite,
        };
        Assert.Equal("fixture.handoff.identity-mismatch", Assert.Throws<FixtureHandoffContractException>(
            () => NewParent(plan, fixture.Ledger, fixture.ParentProbe, claims,
                CancellationToken.None)).Code);
    }

    [Fact]
    public void ForgedBindingKeyAndReplayedGrantFailClosed()
    {
        var fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var descriptor = fixture.Parent.Seal(CancellationToken.None);
        var forgedBinding = descriptor.Binding with { RunNonce = Guid.NewGuid() };
        Assert.Equal("fixture.handoff.descriptor-rejected", Assert.Throws<FixtureHandoffContractException>(
            () => FixtureArtifactHandoffChild.Accept(descriptor, forgedBinding,
                fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None)).Code);
        var wrongKey = (byte[])Key.Clone();
        wrongKey[0] ^= 1;
        fixture.ChildProbe.Keys[fixture.Claims[0].RawValue] = wrongKey;
        Assert.Equal("fixture.handoff.descriptor-rejected", Assert.Throws<FixtureHandoffContractException>(
            () => FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
                fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None)).Code);
        fixture.ChildProbe.Keys[fixture.Claims[0].RawValue] = (byte[])Key.Clone();

        var second = NewFixture();
        second.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var secondDescriptor = second.Parent.Seal(CancellationToken.None);
        var firstChild = FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
            fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None);
        var secondChild = FixtureArtifactHandoffChild.Accept(secondDescriptor,
            secondDescriptor.Binding, second.ChildProbe, 200, second.Claims,
            CancellationToken.None);
        fixture.Parent.MarkResumed(CancellationToken.None);
        var firstGrant = fixture.Parent.AcceptChildProof(
            new FakeProofSource(Child, firstChild.Proof), CancellationToken.None);
        Assert.Equal("fixture.handoff.parent-grant-rejected", Assert.Throws<FixtureHandoffContractException>(
            () => secondChild.AcceptParentGrant(
                new FakeParentGrantSource(Parent, firstGrant), CancellationToken.None)).Code);
        Assert.False(secondChild.MayWrite);
    }

    [Fact]
    public void CancellationOrProbeFailureAfterCreatedChildKeepsExactCleanupObligation()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = NewFixture(cancellation.Token);
        cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None));
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            fixture.Parent.ExitAfterCleanup(QualificationExitCode.Pass));
        foreach (var claim in fixture.Claims)
            fixture.Parent.ConfirmTransferClosed(claim.RawValue, FixtureArtifactCloseStatus.Closed);
        fixture.Parent.ConfirmExactChildReaped(200, processExited: true, jobEmpty: true);
        fixture.Parent.ConfirmObserversDrained();
        fixture.Parent.ConfirmArtifactOperationsDrained();
        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            fixture.Parent.ExitAfterCleanup(QualificationExitCode.Pass));

        fixture = NewFixture();
        fixture.ParentProbe.ThrowOnProcess = 200;
        Assert.Throws<InvalidOperationException>(() =>
            fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None));
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            fixture.Parent.ExitAfterCleanup(QualificationExitCode.Pass));
        fixture.Parent.ConfirmExactChildReaped(200, processExited: true, jobEmpty: true);
    }

    [Fact]
    public void AmbiguousTransferCloseAndPostGrantCancellationBlockPassAndWrite()
    {
        using var cancellation = new CancellationTokenSource();
        var fixture = NewFixture(cancellation.Token);
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.Parent.MarkResumed(CancellationToken.None);
        var child = FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
            fixture.ChildProbe, 200, fixture.Claims, cancellation.Token);
        child.AcceptParentGrant(new FakeParentGrantSource(Parent,
            fixture.Parent.AcceptChildProof(
                new FakeProofSource(Child, child.Proof), CancellationToken.None)),
            CancellationToken.None);
        Assert.True(child.MayWrite);
        cancellation.Cancel();
        Assert.False(child.MayWrite);
        fixture.Parent.ConfirmTransferClosed(fixture.Claims[0].RawValue,
            FixtureArtifactCloseStatus.Ambiguous);
        foreach (var claim in fixture.Claims.Skip(1))
            fixture.Parent.ConfirmTransferClosed(claim.RawValue, FixtureArtifactCloseStatus.Closed);
        fixture.Parent.ConfirmExactChildReaped(200, processExited: true, jobEmpty: true);
        fixture.Parent.ConfirmObserversDrained();
        fixture.Parent.ConfirmArtifactOperationsDrained();
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            fixture.Parent.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void WrongProofPeerAndBootstrapParentHandleAreRejected()
    {
        var fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.Parent.MarkResumed(CancellationToken.None);
        var child = FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
            fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None);
        Assert.Equal("fixture.handoff.child-proof-rejected", Assert.Throws<FixtureHandoffContractException>(
            () => fixture.Parent.AcceptChildProof(
                new FakeProofSource(new(99, 9999), child.Proof), CancellationToken.None)).Code);

        var bootstrap = NewFixture(FixtureHandoffEdge.RunnerToBootstrapHelper,
            QualificationScenario.RejectedUnintendedPeer);
        var proofHandle = bootstrap.Claims[4].RawValue;
        bootstrap.ChildProbe.Processes[proofHandle] = new(99, 9999);
        bootstrap.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        descriptor = bootstrap.Parent.Seal(CancellationToken.None);
        Assert.Equal("fixture.handoff.parent-mismatch", Assert.Throws<FixtureHandoffContractException>(
            () => FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
                bootstrap.ChildProbe, 200, bootstrap.Claims, CancellationToken.None)).Code);
    }

    [Fact]
    public void ReplacedParentOrChildAndUnconfirmedReadRevokeHandshake()
    {
        var fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        fixture.ParentProbe.Processes[100] = new(40, 1001);
        Assert.Equal("fixture.handoff.identity-mismatch", Assert.Throws<FixtureHandoffContractException>(
            () => fixture.Parent.Seal(CancellationToken.None)).Code);
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            fixture.Parent.ExitAfterCleanup(QualificationExitCode.Pass));

        fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.ChildProbe.Processes[200] = new(41, 2001);
        Assert.Equal("fixture.handoff.descriptor-rejected", Assert.Throws<FixtureHandoffContractException>(
            () => FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
                fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None)).Code);

        fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.ChildProbe.RegisteredNames.Clear();
        Assert.Equal("fixture.handoff.identity-mismatch", Assert.Throws<FixtureHandoffContractException>(
            () => FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
                fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None)).Code);
    }

    [Fact]
    public void PerLaunchChallengeAndRetryableCloseDoNotGrantPrematurePass()
    {
        var first = NewFixture();
        first.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var firstDescriptor = first.Parent.Seal(CancellationToken.None);
        var second = NewFixture();
        second.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var secondDescriptor = second.Parent.Seal(CancellationToken.None);
        Assert.NotEqual(firstDescriptor.Challenge, secondDescriptor.Challenge);

        first.Parent.MarkResumed(CancellationToken.None);
        var child = FixtureArtifactHandoffChild.Accept(firstDescriptor,
            firstDescriptor.Binding, first.ChildProbe, 200, first.Claims,
            CancellationToken.None);
        child.AcceptParentGrant(new FakeParentGrantSource(Parent,
            first.Parent.AcceptChildProof(
                new FakeProofSource(Child, child.Proof), CancellationToken.None)),
            CancellationToken.None);
        first.Parent.ConfirmTransferClosed(first.Claims[0].RawValue,
            FixtureArtifactCloseStatus.StillOpen);
        foreach (var claim in first.Claims.Skip(1))
            first.Parent.ConfirmTransferClosed(claim.RawValue, FixtureArtifactCloseStatus.Closed);
        first.Parent.ConfirmExactChildReaped(200, true, true);
        first.Parent.ConfirmObserversDrained();
        first.Parent.ConfirmArtifactOperationsDrained();
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            first.Parent.ExitAfterCleanup(QualificationExitCode.Pass));
        first.Parent.ConfirmTransferClosed(first.Claims[0].RawValue,
            FixtureArtifactCloseStatus.Closed);
        Assert.True(first.Parent.CleanupConfirmed);
    }

    [Fact]
    public void ChangedRetainedAuthorityAndPendingArtifactOperationPreventPass()
    {
        var fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        fixture.Native.ObservedIdentity = Directory() with { FileIdLow = 2 };
        Assert.Throws<FixtureArtifactContractException>(() =>
            fixture.Parent.Seal(CancellationToken.None));
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            fixture.Parent.ExitAfterCleanup(QualificationExitCode.Pass));

        fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.Parent.MarkResumed(CancellationToken.None);
        var child = FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
            fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None);
        var grant = fixture.Parent.AcceptChildProof(
            new FakeProofSource(Child, child.Proof), CancellationToken.None);
        child.AcceptParentGrant(new FakeParentGrantSource(Parent, grant), CancellationToken.None);
        fixture.Parent.ConfirmChildGrantAccepted(
            new FakeGrantAckSource(Child, child.GrantAck), CancellationToken.None);
        foreach (var claim in fixture.Claims)
            fixture.Parent.ConfirmTransferClosed(claim.RawValue, FixtureArtifactCloseStatus.Closed);
        fixture.Parent.ConfirmExactChildReaped(200, true, true);
        fixture.Parent.ConfirmObserversDrained();
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            fixture.Parent.ExitAfterCleanup(QualificationExitCode.Pass));
        fixture.Parent.ConfirmArtifactOperationsDrained();
        Assert.Equal(QualificationExitCode.Pass,
            fixture.Parent.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void CancellationDuringProofReceiveCannotIssueGrantOrPass()
    {
        var fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.Parent.MarkResumed(CancellationToken.None);
        var child = FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
            fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        Assert.ThrowsAny<OperationCanceledException>(() => fixture.Parent.AcceptChildProof(
            new FakeProofSource(Child, child.Proof, cancellation.Cancel), cancellation.Token));
        Assert.Throws<FixtureHandoffContractException>(() =>
            fixture.Parent.ConfirmChildGrantAccepted(
                new FakeGrantAckSource(Child, new byte[32]), CancellationToken.None));
        foreach (var claim in fixture.Claims)
            fixture.Parent.ConfirmTransferClosed(claim.RawValue, FixtureArtifactCloseStatus.Closed);
        fixture.Parent.ConfirmExactChildReaped(200, true, true);
        fixture.Parent.ConfirmObserversDrained();
        fixture.Parent.ConfirmArtifactOperationsDrained();
        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            fixture.Parent.ExitAfterCleanup(QualificationExitCode.Pass));
    }

    [Fact]
    public void DuplicateChildProofCannotRotateAlreadyIssuedParentGrant()
    {
        var fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.Parent.MarkResumed(CancellationToken.None);
        var child = FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
            fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None);
        var proofSource = new FakeProofSource(Child, child.Proof);
        var firstGrant = fixture.Parent.AcceptChildProof(proofSource, CancellationToken.None);
        Assert.Equal("fixture.handoff.child-proof-rejected", Assert.Throws<FixtureHandoffContractException>(
            () => fixture.Parent.AcceptChildProof(proofSource, CancellationToken.None)).Code);
        child.AcceptParentGrant(new FakeParentGrantSource(Parent, firstGrant),
            CancellationToken.None);
        Assert.Throws<FixtureHandoffContractException>(() =>
            fixture.Parent.ConfirmChildGrantAccepted(
                new FakeGrantAckSource(Child, child.GrantAck), CancellationToken.None));
    }

    [Fact]
    public void FailedOrCanceledChildCannotExposeGrantAck()
    {
        var fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        var descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.Parent.MarkResumed(CancellationToken.None);
        var child = FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
            fixture.ChildProbe, 200, fixture.Claims, CancellationToken.None);
        var grant = fixture.Parent.AcceptChildProof(
            new FakeProofSource(Child, child.Proof), CancellationToken.None);
        child.AcceptParentGrant(new FakeParentGrantSource(Parent, grant), CancellationToken.None);
        fixture.ChildProbe.DeadProcesses.Add(200);
        Assert.False(child.MayWrite);
        Assert.Equal("fixture.handoff.grant-not-accepted", Assert.Throws<FixtureHandoffContractException>(
            () => child.GrantAck).Code);

        using var cancellation = new CancellationTokenSource();
        fixture = NewFixture();
        fixture.Parent.BindSuspendedChild(200, Child, CancellationToken.None);
        descriptor = fixture.Parent.Seal(CancellationToken.None);
        fixture.Parent.MarkResumed(CancellationToken.None);
        child = FixtureArtifactHandoffChild.Accept(descriptor, descriptor.Binding,
            fixture.ChildProbe, 200, fixture.Claims, cancellation.Token);
        grant = fixture.Parent.AcceptChildProof(
            new FakeProofSource(Child, child.Proof), CancellationToken.None);
        child.AcceptParentGrant(new FakeParentGrantSource(Parent, grant), CancellationToken.None);
        cancellation.Cancel();
        Assert.Equal("fixture.handoff.grant-not-accepted", Assert.Throws<FixtureHandoffContractException>(
            () => child.GrantAck).Code);
    }

    private static FixtureArtifactHandoffPlan Plan(
        FixtureHandoffEdge edge, QualificationScenario? scenario) =>
        FixtureArtifactHandoffPlan.Create(edge, scenario, Guid.NewGuid(), Guid.NewGuid());

    private static FixtureArtifactIdentity Directory() =>
        new(7, 1, 0, FixtureArtifactKind.Directory, false);

    private static FixtureArtifactOwnership NewAuthority() =>
        NewAuthority(new FakeArtifactNative());

    private static FixtureArtifactOwnership NewAuthority(FakeArtifactNative native) =>
        new(native, new FakeArtifactHandle(1), Directory(), ["scenario"]);

    private static IReadOnlyList<FixtureHandoffLedgerEntry> Entries(
        QualificationScenario? scenario)
    {
        var entries = new List<(string Name, QualificationRole Writer, FixtureHandoffRights Rights)>();
        static void Add(List<(string, QualificationRole, FixtureHandoffRights)> target,
            string name, QualificationRole writer, FixtureHandoffRights rights) =>
            target.Add((name, writer, rights));
        if (scenario is null)
        {
            Add(entries, "manifest.json", QualificationRole.Runner, FixtureHandoffRights.FileWrite);
            Add(entries, "result.json", QualificationRole.Runner, FixtureHandoffRights.FileWrite);
            Add(entries, "report.md", QualificationRole.Runner, FixtureHandoffRights.FileWrite);
            Add(entries, "runner-run.jsonl", QualificationRole.Runner, FixtureHandoffRights.FileAppend);
        }
        else
        {
            var name = scenario.Value.ToString();
            Add(entries, $"runner-{name}.jsonl", QualificationRole.Runner,
                FixtureHandoffRights.FileAppend);
            if (FixtureArtifactHandoffLedger.IsDescendantScenario(scenario))
            {
                Add(entries, $"owner-{name}.jsonl", QualificationRole.Owner,
                    FixtureHandoffRights.FileAppend);
                Add(entries, "membership-enumeration.json", QualificationRole.Owner,
                    FixtureHandoffRights.FileWrite);
                Add(entries, "membership-snapshot.json", QualificationRole.Owner,
                    FixtureHandoffRights.FileWrite);
                Add(entries, "owner-result.json", QualificationRole.Owner,
                    FixtureHandoffRights.FileWrite);
                if (scenario == QualificationScenario.OwnerDeath)
                    Add(entries, "owner-armed.json", QualificationRole.Owner,
                        FixtureHandoffRights.FileWrite);
                Add(entries, $"helper-{name}.jsonl", QualificationRole.Helper,
                    FixtureHandoffRights.FileAppend);
                Add(entries, "helper-startup.json", QualificationRole.Helper,
                    FixtureHandoffRights.FileWrite);
                Add(entries, "helper-ready.json", QualificationRole.Helper,
                    FixtureHandoffRights.FileWrite);
                Add(entries, $"descendant-{name}.jsonl", QualificationRole.Descendant,
                    FixtureHandoffRights.FileAppend);
                Add(entries, "descendant-startup.json", QualificationRole.Descendant,
                    FixtureHandoffRights.FileWrite);
            }
            else
            {
                Add(entries, $"helper-{name}.jsonl", QualificationRole.Helper,
                    FixtureHandoffRights.FileAppend);
                Add(entries, "bootstrap-ready.json", QualificationRole.Helper,
                    FixtureHandoffRights.FileWrite);
            }
        }
        return entries.Select((entry, index) => new FixtureHandoffLedgerEntry(
            entry.Name, new FixtureArtifactIdentity(7, (ulong)(index + 10), 0,
                FixtureArtifactKind.File, false), entry.Writer, entry.Rights)).ToArray();
    }

    private static FixtureArtifactHandoffParent NewParent(
        FixtureArtifactHandoffPlan plan, FixtureArtifactHandoffLedger ledger,
        FakeProbe probe, IReadOnlyList<FixtureHandoffClaim> claims, CancellationToken lifetime) =>
        new(plan, ledger, probe, 100, Parent, claims, lifetime);

    private static TestFixture NewFixture(
        CancellationToken lifetime = default) =>
        NewFixture(FixtureHandoffEdge.RunnerToOwner, QualificationScenario.OwnerDeath, lifetime);

    private static TestFixture NewFixture(
        FixtureHandoffEdge edge, QualificationScenario? scenario,
        CancellationToken lifetime = default)
    {
        var plan = Plan(edge, scenario);
        var native = new FakeArtifactNative();
        var authority = NewAuthority(native);
        var entries = Entries(plan.Scenario);
        var outputObserver = new FakeOutputObserver(entries);
        var ledger = FixtureArtifactHandoffLedger.Create(plan.Scenario,
            authority, authority.Root, outputObserver, entries);
        var parentProbe = new FakeProbe();
        parentProbe.Processes[100] = Parent;
        parentProbe.Processes[200] = Child;
        var claims = plan.Slots.Select((slot, index) =>
        {
            var raw = (nint)(300 + index);
            FixtureArtifactIdentity? identity = slot.Kind switch
            {
                FixtureHandoffHandleKind.Directory => ledger.Directory,
                FixtureHandoffHandleKind.File => ledger.Entries[slot.Label[(slot.Label.IndexOf(':') + 1)..]].Identity,
                _ => null,
            };
            parentProbe.Handles[raw] = new(raw, slot.Kind, slot.Rights, identity);
            if (slot.Kind == FixtureHandoffHandleKind.Process)
                parentProbe.Processes[raw] = Parent;
            if (slot.Kind == FixtureHandoffHandleKind.Section)
                parentProbe.Keys[raw] = (byte[])Key.Clone();
            if (slot.Kind == FixtureHandoffHandleKind.File)
                parentProbe.RegisteredNames.Add(slot.Label[(slot.Label.IndexOf(':') + 1)..]);
            return new FixtureHandoffClaim(slot.Label, raw);
        }).ToArray();
        var childProbe = parentProbe.Clone();
        var parent = NewParent(plan, ledger, parentProbe, claims, lifetime);
        return new(plan, ledger, native, outputObserver, parent,
            parentProbe, childProbe, claims);
    }

    private sealed record TestFixture(
        FixtureArtifactHandoffPlan Plan, FixtureArtifactHandoffLedger Ledger,
        FakeArtifactNative Native, FakeOutputObserver OutputObserver,
        FixtureArtifactHandoffParent Parent,
        FakeProbe ParentProbe, FakeProbe ChildProbe,
        FixtureHandoffClaim[] Claims);

    private sealed class FakeProbe : IFixtureHandoffProbe
    {
        internal Dictionary<nint, FixtureHandoffObservedHandle> Handles { get; } = [];
        internal Dictionary<nint, FixtureHandoffProcessIdentity> Processes { get; } = [];
        internal HashSet<nint> DeadProcesses { get; } = [];
        internal HashSet<string> RegisteredNames { get; } = new(StringComparer.Ordinal);
        internal Dictionary<nint, byte[]> Keys { get; } = [];
        internal nint ThrowOnProcess { get; set; }

        public FixtureHandoffProcessIdentity ObserveProcess(nint retainedProcessHandle)
        {
            if (retainedProcessHandle == ThrowOnProcess)
                throw new InvalidOperationException("injected process observation failure");
            return Processes[retainedProcessHandle];
        }

        public bool IsProcessAlive(nint retainedProcessHandle) =>
            !DeadProcesses.Contains(retainedProcessHandle);

        public FixtureHandoffObservedHandle ObserveHandle(nint handle) => Handles[handle];

        public byte[] ReadSectionKey(nint sectionHandle) =>
            (byte[])Keys[sectionHandle].Clone();

        public bool IsRegisteredChild(nint retainedDirectoryHandle, string literalName,
            FixtureArtifactIdentity expected) =>
            retainedDirectoryHandle == 302 && RegisteredNames.Contains(literalName);

        internal FakeProbe Clone()
        {
            var copy = new FakeProbe();
            foreach (var pair in Handles)
                copy.Handles.Add(pair.Key, pair.Value);
            foreach (var pair in Processes)
                copy.Processes.Add(pair.Key, pair.Value);
            foreach (var name in RegisteredNames)
                copy.RegisteredNames.Add(name);
            foreach (var pair in Keys)
                copy.Keys.Add(pair.Key, (byte[])pair.Value.Clone());
            return copy;
        }
    }

    private sealed record FakeArtifactHandle(nint RawValue) : IFixtureArtifactHandle;

    private sealed class FakeOutputObserver(
        IEnumerable<FixtureHandoffLedgerEntry> entries) : IFixtureHandoffRegisteredOutputObserver
    {
        internal Dictionary<string, FixtureArtifactIdentity> Current { get; } =
            entries.ToDictionary(entry => entry.Name, entry => entry.Identity,
                StringComparer.Ordinal);

        public bool IsStillOwnedChild(
            FixtureArtifactIdentity directory, string literalName,
            FixtureArtifactIdentity expectedOutput) =>
            directory == Directory() && Current.TryGetValue(literalName, out var current) &&
            current == expectedOutput;
    }

    private sealed class FakeArtifactNative : IFixtureArtifactNative
    {
        internal FixtureArtifactIdentity ObservedIdentity { get; set; } = Directory();
        public FixtureArtifactCreateResult CreateNew(
            IFixtureArtifactHandle parent, string name, FixtureArtifactKind kind) =>
            throw new NotSupportedException();
        public FixtureArtifactIdentity Observe(IFixtureArtifactHandle handle) => ObservedIdentity;
        public bool IsEmptyDirectory(IFixtureArtifactHandle handle,
            FixtureArtifactIdentity expected) => throw new NotSupportedException();
        public bool MarkDelete(IFixtureArtifactHandle handle,
            FixtureArtifactIdentity expected) => throw new NotSupportedException();
        public FixtureArtifactCloseStatus Close(IFixtureArtifactHandle handle) =>
            throw new NotSupportedException();
        public FixtureArtifactDeletionStatus ConfirmTerminalDeletion(
            IFixtureArtifactHandle parent, FixtureArtifactIdentity expectedParent,
            string name, FixtureArtifactIdentity expectedChild) =>
            throw new NotSupportedException();
    }

    private sealed class FakeParentGrantSource(
        FixtureHandoffProcessIdentity peer, byte[] grant) : IFixtureHandoffParentGrantSource
    {
        public FixtureHandoffParentGrantObservation Receive(
            FixtureHandoffDescriptor expectedDescriptor) => new(peer, (byte[])grant.Clone());
    }

    private sealed class FakeGrantAckSource(
        FixtureHandoffProcessIdentity peer, byte[] ack) : IFixtureHandoffChildGrantAckSource
    {
        public FixtureHandoffChildGrantAckObservation Receive(
            FixtureHandoffDescriptor expectedDescriptor) => new(peer, (byte[])ack.Clone());
    }

    private sealed class FakeProofSource(
        FixtureHandoffProcessIdentity peer, byte[] proof, Action? onReceive = null) :
        IFixtureHandoffChildProofSource
    {
        public FixtureHandoffChildProofObservation Receive(
            FixtureHandoffDescriptor expectedDescriptor)
        {
            onReceive?.Invoke();
            return new(peer, (byte[])proof.Clone());
        }
    }
}
