using WatchDog.App.Logging;

namespace WatchDog.Integration.Tests;

public sealed class SafeLogDestinationCustodyPolicyTests
{
    private static readonly string[] Identities = ["private-root", "watchdog", "logs"];
    private const string Owner = "owner-token";

    [Fact]
    public void CompleteOwnerOnlyDirectoryChainSatisfiesSyntheticPolicy()
    {
        var intent = SafeIntent();
        var snapshot = SafeSnapshot();

        var first = SafeLogDestinationCustodyPolicy.Evaluate(intent, snapshot);
        Assert.True(first.SyntheticPolicySatisfied);
        Assert.Equal(SafeLogCustodyRejection.None, first.Rejection);
        Assert.Null(first.ComponentIndex);
        Assert.Equal(first, SafeLogDestinationCustodyPolicy.Evaluate(intent, snapshot));
    }

    [Fact]
    public void NullMissingOrMalformedIntentFailsClosed()
    {
        var snapshot = SafeSnapshot();
        AssertReject(null, snapshot, SafeLogCustodyRejection.MissingEvidence);
        AssertReject(SafeIntent(), null, SafeLogCustodyRejection.MissingEvidence);
        AssertReject(new SafeLogDestinationIntent("logs", Owner, null), snapshot,
            SafeLogCustodyRejection.MissingEvidence);
        AssertReject(new SafeLogDestinationIntent("logs", Owner, ["private-root", "", "logs"]), snapshot,
            SafeLogCustodyRejection.MissingEvidence);
        AssertReject(new SafeLogDestinationIntent("wrong-root", Owner, Identities), snapshot,
            SafeLogCustodyRejection.InvalidIntent);
        AssertReject(new SafeLogDestinationIntent("logs", Owner, ["private-root", "logs", "logs"]), snapshot,
            SafeLogCustodyRejection.InvalidIntent);
    }

    [Fact]
    public void ReplacementMissingExtraReorderedOrMisparentedComponentFailsClosed()
    {
        AssertReject(SafeIntent(), SnapshotWith(2, Component(2, identity: "replacement")),
            SafeLogCustodyRejection.RootMismatch, 2);
        AssertReject(SafeIntent(), new SafeLogDestinationSnapshot(SafeComponents()[..2]),
            SafeLogCustodyRejection.ChainMismatch);
        AssertReject(SafeIntent(), new SafeLogDestinationSnapshot([.. SafeComponents(), Component(2)]),
            SafeLogCustodyRejection.ChainMismatch);
        var reordered = SafeComponents();
        (reordered[0], reordered[1]) = (reordered[1], reordered[0]);
        AssertReject(SafeIntent(), new SafeLogDestinationSnapshot(reordered),
            SafeLogCustodyRejection.ChainMismatch, 0);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1, parent: "alternate-parent")),
            SafeLogCustodyRejection.ChainMismatch, 1);
        AssertReject(SafeIntent(), SnapshotWith(0, Component(0, parent: "hidden-ancestor")),
            SafeLogCustodyRejection.ChainMismatch, 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ReparseOrWrongOwnerAtAnyInspectedComponentFailsClosed(int index)
    {
        AssertReject(SafeIntent(), SnapshotWith(index, Component(index,
            reparse: SafeLogCustodyReparseState.Present)),
            SafeLogCustodyRejection.Redirected, index);
        AssertReject(SafeIntent(), SnapshotWith(index, Component(index, owner: "other-owner")),
            SafeLogCustodyRejection.OwnerMismatch, index);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnyOtherPrincipalReadOrWriteAuthorityFailsClosed(bool read, bool write)
    {
        var grants = new[]
        {
            new SafeLogEffectiveGrant(Owner, true, true),
            new SafeLogEffectiveGrant("other-principal", read, write),
        };
        AssertReject(SafeIntent(), SnapshotWith(2, Component(2, grants: grants)),
            SafeLogCustodyRejection.OtherPrincipalAccess, 2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OtherPrincipalWriteAuthorityAtAnyComponentFailsClosed(int index)
    {
        AssertReject(SafeIntent(), SnapshotWith(index, Component(index, grants:
            [new SafeLogEffectiveGrant(Owner, true, true),
             new SafeLogEffectiveGrant("other-principal", false, true)])),
            SafeLogCustodyRejection.OtherPrincipalAccess, index);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void IntendedOwnerNeedsReadAndWriteOrControl(bool read, bool write)
    {
        AssertReject(SafeIntent(), SnapshotWith(2, Component(2, grants:
            [new SafeLogEffectiveGrant(Owner, read, write)])),
            SafeLogCustodyRejection.OwnerAccessMissing, 2);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ProspectiveChildFileBroadAccessRejectsOwnerOnlyDirectories(bool read, bool write)
    {
        var childGrants = new[]
        {
            new SafeLogEffectiveGrant(Owner, true, true),
            new SafeLogEffectiveGrant("other-principal", read, write),
        };
        AssertReject(SafeIntent(), new SafeLogDestinationSnapshot(SafeComponents(),
            SafeLogCustodyAccessAssessment.Complete, childGrants, Owner),
            SafeLogCustodyRejection.OtherPrincipalAccess, 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("other-owner")]
    public void ProspectiveChildFileOwnerMustBeKnownAndMatchIntendedOwner(string? childOwner)
    {
        var expectedRejection = string.IsNullOrWhiteSpace(childOwner)
            ? SafeLogCustodyRejection.MissingEvidence : SafeLogCustodyRejection.OwnerMismatch;
        AssertReject(SafeIntent(), new SafeLogDestinationSnapshot(SafeComponents(),
            SafeLogCustodyAccessAssessment.Complete, SafeGrants(), childOwner),
            expectedRejection, 2);
    }

    [Fact]
    public void UnknownOrIncompleteProspectiveChildFileAccessFailsClosed()
    {
        AssertReject(SafeIntent(), new SafeLogDestinationSnapshot(SafeComponents()),
            SafeLogCustodyRejection.MissingEvidence, 2);
        AssertReject(SafeIntent(), new SafeLogDestinationSnapshot(SafeComponents(),
            SafeLogCustodyAccessAssessment.Incomplete, SafeGrants(), Owner),
            SafeLogCustodyRejection.IncompleteAccessEvidence, 2);
        AssertReject(SafeIntent(), new SafeLogDestinationSnapshot(SafeComponents(),
            SafeLogCustodyAccessAssessment.Complete, null, Owner),
            SafeLogCustodyRejection.IncompleteAccessEvidence, 2);
        AssertReject(SafeIntent(), new SafeLogDestinationSnapshot(SafeComponents(),
            SafeLogCustodyAccessAssessment.Complete,
            [new SafeLogEffectiveGrant(Owner, true, false)], Owner),
            SafeLogCustodyRejection.OwnerAccessMissing, 2);
    }

    [Fact]
    public void UnknownIncompleteOrInvalidObservationsFailClosed()
    {
        AssertReject(SafeIntent(), new SafeLogDestinationSnapshot(null),
            SafeLogCustodyRejection.MissingEvidence);
        AssertReject(SafeIntent(), SnapshotWith(1, null),
            SafeLogCustodyRejection.MissingEvidence, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1, identity: "")),
            SafeLogCustodyRejection.MissingEvidence, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1, owner: null)),
            SafeLogCustodyRejection.MissingEvidence, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1,
            kind: SafeLogCustodyComponentKind.Unknown)),
            SafeLogCustodyRejection.MissingEvidence, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1,
            kind: SafeLogCustodyComponentKind.Other)),
            SafeLogCustodyRejection.WrongComponentKind, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1,
            kind: (SafeLogCustodyComponentKind)999)),
            SafeLogCustodyRejection.WrongComponentKind, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1,
            reparse: SafeLogCustodyReparseState.Unknown)),
            SafeLogCustodyRejection.MissingEvidence, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1,
            reparse: (SafeLogCustodyReparseState)999)),
            SafeLogCustodyRejection.Redirected, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1,
            assessment: SafeLogCustodyAccessAssessment.Incomplete)),
            SafeLogCustodyRejection.IncompleteAccessEvidence, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1,
            assessment: (SafeLogCustodyAccessAssessment)999)),
            SafeLogCustodyRejection.IncompleteAccessEvidence, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1, omitGrants: true)),
            SafeLogCustodyRejection.IncompleteAccessEvidence, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1, grants:
            [new SafeLogEffectiveGrant("", true, true)])),
            SafeLogCustodyRejection.IncompleteAccessEvidence, 1);
        AssertReject(SafeIntent(), SnapshotWith(1, Component(1, grants: [])),
            SafeLogCustodyRejection.OwnerAccessMissing, 1);
    }

    [Fact]
    public void CallerMutationCannotChangeFrozenIntentSnapshotOrGrantFacts()
    {
        var expected = Identities.ToList();
        var grants = SafeGrants().ToList();
        var childGrants = SafeGrants().ToList();
        var components = SafeComponents();
        components[2] = Component(2, grants: grants);
        var intent = new SafeLogDestinationIntent("logs", Owner, expected);
        var snapshot = new SafeLogDestinationSnapshot(components,
            SafeLogCustodyAccessAssessment.Complete, childGrants, Owner);

        expected[2] = "replacement";
        components[2] = Component(2, reparse: SafeLogCustodyReparseState.Present);
        grants.Add(new SafeLogEffectiveGrant("other-principal", true, true));
        childGrants.Add(new SafeLogEffectiveGrant("other-principal", true, true));

        Assert.True(SafeLogDestinationCustodyPolicy.Evaluate(intent, snapshot).SyntheticPolicySatisfied);
    }

    private static SafeLogDestinationIntent SafeIntent() => new("logs", Owner, Identities);

    private static SafeLogDestinationSnapshot SafeSnapshot() => new(SafeComponents(),
        SafeLogCustodyAccessAssessment.Complete, SafeGrants(), Owner);

    private static SafeLogCustodyComponent?[] SafeComponents() =>
        [Component(0), Component(1), Component(2)];

    private static SafeLogDestinationSnapshot SnapshotWith(int index, SafeLogCustodyComponent? replacement)
    {
        var components = SafeComponents();
        components[index] = replacement;
        return new SafeLogDestinationSnapshot(components,
            SafeLogCustodyAccessAssessment.Complete, SafeGrants(), Owner);
    }

    private static SafeLogCustodyComponent Component(int index, string? identity = "default",
        string? parent = "default", SafeLogCustodyComponentKind kind = SafeLogCustodyComponentKind.Directory,
        SafeLogCustodyReparseState reparse = SafeLogCustodyReparseState.Absent,
        string? owner = Owner, SafeLogCustodyAccessAssessment assessment = SafeLogCustodyAccessAssessment.Complete,
        IEnumerable<SafeLogEffectiveGrant>? grants = null, bool omitGrants = false)
    {
        var actualIdentity = identity == "default" ? Identities[index] : identity;
        var actualParent = parent == "default" ? (index == 0 ? null : Identities[index - 1]) : parent;
        return new SafeLogCustodyComponent(actualIdentity, actualParent, kind, reparse,
            owner, assessment, omitGrants ? null : grants ?? SafeGrants());
    }

    private static SafeLogEffectiveGrant[] SafeGrants() =>
        [new(Owner, true, true), new("other-principal", false, false)];

    private static void AssertReject(SafeLogDestinationIntent? intent, SafeLogDestinationSnapshot? snapshot,
        SafeLogCustodyRejection reason, int? index = null)
    {
        var decision = SafeLogDestinationCustodyPolicy.Evaluate(intent, snapshot);
        Assert.False(decision.SyntheticPolicySatisfied);
        Assert.Equal(reason, decision.Rejection);
        Assert.Equal(index, decision.ComponentIndex);
    }
}
