using System.Security.Cryptography;
using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1AuthorityJournalContractTests
{
    [Fact]
    public void AuthorityRequiresOriginalTokenExactLeaseAndLiveRetainedObject()
    {
        var setup = NewSetup();
        var observation = NewObservation();
        var token = setup.Registry.Register(observation);
        Assert.NotNull(token);
        Assert.Null(setup.Registry.Register(observation));
        Assert.True(setup.Registry.Owns(token!, setup.Session, observation.Kind,
            observation.Owner, observation.ExactObjectReceipt,
            observation.LeaseGeneration));
        Assert.False(setup.Registry.Owns(new FixtureB1AuthorityToken(token!.Id),
            setup.Session, observation.Kind, observation.Owner,
            observation.ExactObjectReceipt, observation.LeaseGeneration));
        Assert.False(setup.Registry.Owns(token!, setup.Session with
        {
            ConnectionGeneration = 2,
        }, observation.Kind, observation.Owner,
            observation.ExactObjectReceipt, observation.LeaseGeneration));
        Assert.False(setup.Registry.Owns(token!, setup.Session,
            FixtureB1AuthorityObjectKind.RunnerProcess, observation.Owner,
            observation.ExactObjectReceipt, observation.LeaseGeneration));
        setup.ObjectAuthority.Accept = false;
        Assert.Null(setup.Registry.BeginOriginal(token!, setup.Session));
        setup.ObjectAuthority.Accept = true;
        setup.Registry.LoseRetainedObject(token!);
        Assert.True(setup.Registry.IsLive);
        Assert.False(setup.Registry.AdmissionOpen);
        Assert.False(setup.Registry.Owns(token!, setup.Session, observation.Kind,
            observation.Owner, observation.ExactObjectReceipt,
            observation.LeaseGeneration));
    }

    [Fact]
    public void IdenticalMalformedCallbacksFromDistinctOriginalsKeepDistinctEmergencyCustody()
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        Assert.NotNull(token);
        var first = setup.Registry.BeginOriginal(token!, setup.Session);
        var second = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.NotNull(first);
        Assert.NotNull(second);
        var malformed = new byte[] { 0, 0, 0 };
        var one = setup.Registry.CaptureNativeSuccess(first!, malformed);
        var two = setup.Registry.CaptureNativeSuccess(second!, malformed);
        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.NotEqual(one!.Id, two!.Id);
        Assert.Equal(2, setup.Registry.EmergencyObligations.Count);
        malformed[0] = 9;
        Assert.Equal((byte)0, one.CallbackPayload[0]);
        setup.Registry.WrapperDied();
        Assert.False(setup.Registry.IsLive);
        Assert.Null(setup.Registry.BeginOriginal(token!, setup.Session));
    }

    [Fact]
    public void ImportedJobAndRunnerRequireOriginalExactObjectProvenance()
    {
        var setup = NewSetup();
        var job = NewObservation();
        var importedJob = job with
        {
            Owner = FixtureB1EndpointRole.Launcher,
            LeaseGeneration = Guid.NewGuid(),
            ObservationReceipt = Guid.NewGuid(),
        };
        Assert.Null(setup.Registry.Register(importedJob));
        Assert.NotNull(setup.Registry.Register(job));
        Assert.NotNull(setup.Registry.Register(importedJob));
        Assert.Null(setup.Registry.Register(importedJob with
        {
            ExactObjectReceipt = Guid.NewGuid(),
            LeaseGeneration = Guid.NewGuid(),
        }));

        var runner = NewObservation() with
        {
            Kind = FixtureB1AuthorityObjectKind.RunnerProcess,
            Owner = FixtureB1EndpointRole.Launcher,
            Creator = FixtureB1EndpointRole.Launcher,
        };
        Assert.NotNull(setup.Registry.Register(runner));
        Assert.NotNull(setup.Registry.Register(runner with
        {
            Owner = FixtureB1EndpointRole.Wrapper,
            LeaseGeneration = Guid.NewGuid(),
            ObservationReceipt = Guid.NewGuid(),
        }));
        Assert.Null(setup.Registry.Register(runner with
        {
            Owner = FixtureB1EndpointRole.Wrapper,
            ExactObjectReceipt = Guid.NewGuid(),
            LeaseGeneration = Guid.NewGuid(),
        }));
    }

    [Fact]
    public void SecondOriginalCompletionWithIdenticalPayloadCannotReachAck()
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.True(setup.Journal.TryRecordIntent(invocation!));
        var first = setup.Registry.CaptureNativeSuccess(invocation!, [0, 0]);
        Assert.True(setup.Journal.TryRecordSuccess(first!));
        Assert.True(setup.Journal.TryRecordSuccess(first!));
        Assert.Equal(2, setup.Store.FrameCount);
        var second = setup.Registry.CaptureNativeSuccess(invocation!, [0, 0]);
        Assert.NotEqual(first!.Id, second!.Id);
        Assert.True(setup.Registry.DuplicateOriginalCompletion);
        Assert.True(setup.Journal.TryRecordSuccess(second!));
        Assert.False(setup.Journal.TryRecordObservation(
            FixtureB1JournalAction.AckAccepted,
            FixtureB1EndpointRole.Launcher, first!));
        Assert.Equal(FixtureB1JournalInspectionState.CleanupOnly,
            Inspect(setup).State);
        Assert.Equal(2, Inspect(setup).KnownSuccessObligations.Count);
    }

    [Fact]
    public void CanonicalFramesRequireIntentSuccessValidationAndAckOrder()
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        var obligation = setup.Registry.CaptureNativeSuccess(invocation!, [1, 2, 3]);
        Assert.NotNull(obligation);
        Assert.False(setup.Journal.TryRecordObservation(
            FixtureB1JournalAction.AckAccepted,
            FixtureB1EndpointRole.Launcher, obligation!));
        Assert.True(setup.Journal.IsPoisoned);

        setup = NewSetup();
        token = setup.Registry.Register(NewObservation());
        invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.True(setup.Journal.TryRecordIntent(invocation!));
        obligation = setup.Registry.CaptureNativeSuccess(invocation!, [1, 2, 3]);
        Assert.True(setup.Journal.TryRecordSuccess(obligation!));
        Assert.True(setup.Journal.TryRecordObservation(
            FixtureB1JournalAction.SuccessValidated,
            FixtureB1EndpointRole.Wrapper, obligation!));
        Assert.True(setup.Journal.TryRecordObservation(
            FixtureB1JournalAction.AckAccepted,
            FixtureB1EndpointRole.Launcher, obligation!));
        var result = Inspect(setup);
        Assert.Equal(FixtureB1JournalInspectionState.ManagedConsistent, result.State);
        Assert.Equal(4, result.VerifiedRecords.Count);
        Assert.Single(result.KnownSuccessObligations);
        Assert.False(result.NativeAuthorityProven);
        Assert.False(result.NativeDurabilityProven);
    }

    [Theory]
    [InlineData(AppendFault.Refuse)]
    [InlineData(AppendFault.TornHeader)]
    [InlineData(AppendFault.TornPayload)]
    [InlineData(AppendFault.TornDigest)]
    [InlineData(AppendFault.WrittenButFalse)]
    public void NativeSuccessAppendFailureRetainsEmergencyCustodyAndStopsAdmission(
        AppendFault fault)
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.True(setup.Journal.TryRecordIntent(invocation!));
        var obligation = setup.Registry.CaptureNativeSuccess(invocation!, [0, 0]);
        setup.Store.NextAppendFault = fault;

        Assert.False(setup.Journal.TryRecordSuccess(obligation!));
        Assert.True(setup.Journal.IsPoisoned);
        Assert.Contains(obligation!, setup.Registry.EmergencyObligations);
        Assert.False(setup.Journal.TryRecordSuccess(obligation!));
        Assert.NotEqual(FixtureB1JournalInspectionState.ManagedConsistent,
            Inspect(setup).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnchorFailureAfterFlushedSuccessIsAmbiguous(bool committedButReplyLost)
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.True(setup.Journal.TryRecordIntent(invocation!));
        var obligation = setup.Registry.CaptureNativeSuccess(invocation!, [5]);
        setup.Store.NextAnchorFault = committedButReplyLost
            ? AnchorFault.CommittedButReplyLost : AnchorFault.Refuse;

        Assert.False(setup.Journal.TryRecordSuccess(obligation!));
        Assert.True(setup.Journal.IsPoisoned);
        Assert.Contains(obligation!, setup.Registry.EmergencyObligations);
        var inspection = Inspect(setup);
        Assert.NotEqual(FixtureB1JournalInspectionState.ManagedConsistent,
            inspection.State);
        Assert.Single(inspection.KnownSuccessObligations);
    }

    [Fact]
    public void TornReorderedDuplicatedAndTruncatedStreamsRejectRecovery()
    {
        var setup = CompleteJournal();
        setup.Store.TruncateLastByte();
        Assert.Equal(FixtureB1JournalInspectionState.Corrupt,
            Inspect(setup).State);

        setup = CompleteJournal();
        setup.Store.ReorderFirstTwoFrames();
        Assert.Equal(FixtureB1JournalInspectionState.Corrupt,
            Inspect(setup).State);

        setup = CompleteJournal();
        setup.Store.DuplicateFirstFrame();
        Assert.Equal(FixtureB1JournalInspectionState.Corrupt,
            Inspect(setup).State);

        setup = CompleteJournal();
        setup.Store.DropLastFrame();
        Assert.Equal(FixtureB1JournalInspectionState.Corrupt,
            Inspect(setup).State);

        setup = CompleteJournal();
        setup.Store.DropEverything();
        Assert.Equal(FixtureB1JournalInspectionState.Corrupt,
            Inspect(setup).State);
    }

    [Fact]
    public void WrongRootAdmissionAndRetainedRootDriftStopJournalUse()
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        setup.Root.PrivateAcl = false;

        Assert.False(setup.Journal.TryRecordIntent(invocation!));
        Assert.True(setup.Journal.IsPoisoned);
        Assert.Equal(FixtureB1JournalInspectionState.Corrupt,
            Inspect(setup).State);

        setup.Root.PrivateAcl = true;
        Assert.False(setup.Journal.TryRecordIntent(invocation!));
        Assert.True(setup.Journal.IsPoisoned);
    }

    [Fact]
    public void RootDriftOnSuccessReplayDoesNotReturnFalseDurability()
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.True(setup.Journal.TryRecordIntent(invocation!));
        var obligation = setup.Registry.CaptureNativeSuccess(invocation!, [4]);
        Assert.True(setup.Journal.TryRecordSuccess(obligation!));

        setup.Root.PrivateAcl = false;
        Assert.False(setup.Journal.TryRecordSuccess(obligation!));
        setup.Root.PrivateAcl = true;
        Assert.False(setup.Journal.TryRecordSuccess(obligation!));
        Assert.True(setup.Journal.IsPoisoned);
    }

    [Fact]
    public void WrongObservationOriginAndUnauthenticatedAckCannotCommit()
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.True(setup.Journal.TryRecordIntent(invocation!));
        var obligation = setup.Registry.CaptureNativeSuccess(invocation!, [8]);
        Assert.True(setup.Journal.TryRecordSuccess(obligation!));
        Assert.False(setup.Journal.TryRecordObservation(
            FixtureB1JournalAction.SuccessValidated,
            FixtureB1EndpointRole.Launcher, obligation!));
        Assert.True(setup.Journal.IsPoisoned);

        setup = NewSetup();
        token = setup.Registry.Register(NewObservation());
        invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.True(setup.Journal.TryRecordIntent(invocation!));
        obligation = setup.Registry.CaptureNativeSuccess(invocation!, [8]);
        Assert.True(setup.Journal.TryRecordSuccess(obligation!));
        Assert.True(setup.Journal.TryRecordObservation(
            FixtureB1JournalAction.SuccessValidated,
            FixtureB1EndpointRole.Wrapper, obligation!));
        setup.Peer.Accept = false;
        Assert.False(setup.Journal.TryRecordObservation(
            FixtureB1JournalAction.AckAccepted,
            FixtureB1EndpointRole.Launcher, obligation!));
        Assert.Equal(FixtureB1JournalInspectionState.CleanupOnly,
            Inspect(setup).State);
    }

    [Fact]
    public void SourceObserverDriftAndPostDeathReplayFailAdmission()
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        setup.ObjectAuthority.Accept = false;
        Assert.False(setup.Journal.TryRecordIntent(invocation!));
        Assert.False(setup.Registry.AdmissionOpen);
        Assert.Equal(FixtureB1JournalInspectionState.CleanupOnly,
            Inspect(setup).State);

        setup = NewSetup();
        token = setup.Registry.Register(NewObservation());
        invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.True(setup.Journal.TryRecordIntent(invocation!));
        var obligation = setup.Registry.CaptureNativeSuccess(invocation!, [1]);
        Assert.True(setup.Journal.TryRecordSuccess(obligation!));
        setup.Registry.WrapperDied();
        Assert.False(setup.Journal.TryRecordSuccess(obligation!));
        Assert.Equal(FixtureB1JournalInspectionState.ForensicOnly,
            Inspect(setup).State);
    }

    [Fact]
    public void ConnectionGenerationDriftRejectsFrameInspection()
    {
        var setup = CompleteJournal();
        var drifted = setup.Session with { ConnectionGeneration = 2 };
        var driftedLease = setup.StorageLease with { ConnectionGeneration = 2 };
        var inspected = FixtureB1FramedJournalContract.Inspect(setup.Registry,
            drifted, driftedLease, setup.Root, setup.Peer, setup.Store);
        Assert.Equal(FixtureB1JournalInspectionState.Corrupt, inspected.State);
    }

    [Fact]
    public void LiveRecoveryReobservesExactObjectAndUnjournaledCustody()
    {
        var setup = CompleteJournal();
        setup.ObjectAuthority.Accept = false;
        Assert.Equal(FixtureB1JournalInspectionState.CleanupOnly,
            Inspect(setup).State);
        Assert.False(setup.Registry.AdmissionOpen);

        setup = CompleteJournal();
        var token = setup.Registry.Register(NewObservation());
        var second = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.NotNull(second);
        var emergency = setup.Registry.CaptureNativeSuccess(second!, [0, 0]);
        Assert.NotNull(emergency);
        Assert.Equal(FixtureB1JournalInspectionState.CleanupOnly,
            Inspect(setup).State);
        Assert.Contains(emergency!, setup.Registry.EmergencyObligations);

        setup = NewSetup();
        token = setup.Registry.Register(NewObservation());
        second = setup.Registry.BeginOriginal(token!, setup.Session);
        emergency = setup.Registry.CaptureNativeSuccess(second!, [0, 0]);
        Assert.NotNull(emergency);
        Assert.Equal(FixtureB1JournalInspectionState.CleanupOnly,
            Inspect(setup).State);
        Assert.Contains(emergency!, setup.Registry.EmergencyObligations);
    }

    [Fact]
    public void HashValidWrongRoleStillRejectsAndRetainsKnownSuccess()
    {
        var setup = CompleteJournal();
        setup.Store.ChangeLastOriginAndRehash(FixtureB1EndpointRole.Wrapper);
        // A fresh registry has no retained head, so a corrupt result must come from
        // semantic role validation rather than a stale in-memory digest mismatch.
        var freshRegistry = new FixtureB1AuthorityRegistry(
            setup.Session, setup.ObjectAuthority);
        var inspection = FixtureB1FramedJournalContract.Inspect(
            freshRegistry, setup.Session, setup.StorageLease,
            setup.Root, setup.Peer, setup.Store);
        Assert.Equal(FixtureB1JournalInspectionState.Corrupt, inspection.State);
        Assert.Single(inspection.KnownSuccessObligations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TornAppendAtEachProtocolBoundaryClosesProgression(int failingStep)
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        FixtureB1EmergencyObligation? obligation = null;
        for (var step = 0; step <= failingStep; step++)
        {
            if (step == failingStep)
                setup.Store.NextAppendFault = AppendFault.TornDigest;
            var committed = ApplyStep(setup, invocation!, ref obligation, step);
            Assert.Equal(step != failingStep, committed);
        }
        Assert.True(setup.Journal.IsPoisoned);
        Assert.NotEqual(FixtureB1JournalInspectionState.ManagedConsistent,
            Inspect(setup).State);
        if (failingStep >= 1)
            Assert.Contains(obligation!, setup.Registry.EmergencyObligations);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void OwnerDeathAfterEachAppendBoundaryCannotGrantManagedProgression(
        int completedStep, bool wrapperDeath)
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        FixtureB1EmergencyObligation? obligation = null;
        for (var step = 0; step <= completedStep; step++)
            Assert.True(ApplyStep(setup, invocation!, ref obligation, step));
        if (wrapperDeath)
            setup.Registry.WrapperDied();
        else
            setup.Registry.LauncherDied();
        Assert.Equal(wrapperDeath
                ? FixtureB1JournalInspectionState.ForensicOnly
                : FixtureB1JournalInspectionState.CleanupOnly,
            Inspect(setup).State);
    }

    private static bool ApplyStep(
        Setup setup,
        FixtureB1OriginalInvocation invocation,
        ref FixtureB1EmergencyObligation? obligation,
        int step)
    {
        switch (step)
        {
            case 0:
                return setup.Journal.TryRecordIntent(invocation);
            case 1:
                obligation = setup.Registry.CaptureNativeSuccess(invocation, [3]);
                return setup.Journal.TryRecordSuccess(obligation!);
            case 2:
                return setup.Journal.TryRecordObservation(
                    FixtureB1JournalAction.SuccessValidated,
                    FixtureB1EndpointRole.Wrapper, obligation!);
            case 3:
                return setup.Journal.TryRecordObservation(
                    FixtureB1JournalAction.AckAccepted,
                    FixtureB1EndpointRole.Launcher, obligation!);
            default:
                throw new ArgumentOutOfRangeException(nameof(step));
        }
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void JournalCreationRejectsMissingRootOrAclProof(
        bool localFixedRoot, bool privateAcl, bool noReparse,
        bool exclusiveWriter)
    {
        var setup = NewSetup();
        setup.Root.LocalFixedRoot = localFixedRoot;
        setup.Root.PrivateAcl = privateAcl;
        setup.Root.NoReparse = noReparse;
        setup.Root.ExclusiveWriter = exclusiveWriter;
        Assert.Throws<ArgumentException>(() =>
            new FixtureB1FramedJournalContract(setup.Registry,
                setup.StorageLease, setup.Root, setup.Peer, new FakeStore()));
    }

    [Fact]
    public void WDeathIsForensicOnlyAndLDeathRevokesNewIntentOrAck()
    {
        var setup = CompleteJournal();
        setup.Registry.WrapperDied();
        Assert.Equal(FixtureB1JournalInspectionState.ForensicOnly,
            Inspect(setup).State);

        setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.True(setup.Journal.TryRecordIntent(invocation!));
        var obligation = setup.Registry.CaptureNativeSuccess(invocation!, [7]);
        setup.Registry.LauncherDied();
        Assert.True(setup.Journal.TryRecordSuccess(obligation!));
        Assert.True(setup.Journal.TryRecordObservation(
            FixtureB1JournalAction.SuccessValidated,
            FixtureB1EndpointRole.Wrapper, obligation!));
        Assert.False(setup.Journal.TryRecordObservation(
            FixtureB1JournalAction.AckAccepted,
            FixtureB1EndpointRole.Launcher, obligation!));
        Assert.Equal(FixtureB1JournalInspectionState.CleanupOnly,
            Inspect(setup).State);
        Assert.Null(setup.Registry.BeginOriginal(token!, setup.Session));
    }

    private static Setup CompleteJournal()
    {
        var setup = NewSetup();
        var token = setup.Registry.Register(NewObservation());
        var invocation = setup.Registry.BeginOriginal(token!, setup.Session);
        Assert.True(setup.Journal.TryRecordIntent(invocation!));
        var obligation = setup.Registry.CaptureNativeSuccess(invocation!, [1]);
        Assert.True(setup.Journal.TryRecordSuccess(obligation!));
        Assert.True(setup.Journal.TryRecordObservation(
            FixtureB1JournalAction.SuccessValidated,
            FixtureB1EndpointRole.Wrapper, obligation!));
        Assert.True(setup.Journal.TryRecordObservation(
            FixtureB1JournalAction.AckAccepted,
            FixtureB1EndpointRole.Launcher, obligation!));
        return setup;
    }

    private static FixtureB1JournalInspection Inspect(Setup setup) =>
        FixtureB1FramedJournalContract.Inspect(setup.Registry, setup.Session,
            setup.StorageLease, setup.Root, setup.Peer, setup.Store);

    private static FixtureB1ExactObjectObservation NewObservation() => new(
        FixtureB1AuthorityObjectKind.RunnerJob,
        FixtureB1EndpointRole.Wrapper,
        FixtureB1EndpointRole.Wrapper,
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Retained: true, NonInheritable: true);

    private static Setup NewSetup()
    {
        var session = new FixtureB1AuthoritySession(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var authority = new FakeObjectAuthority();
        var registry = new FixtureB1AuthorityRegistry(session, authority);
        var root = new FakeRootAuthority();
        var lease = new FixtureB1JournalStorageLease(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), session.WrapperInstance, session.SessionEpoch,
            session.ConnectionGeneration);
        var store = new FakeStore();
        var peer = new FakePeerAuthority();
        var journal = new FixtureB1FramedJournalContract(
            registry, lease, root, peer, store);
        return new(session, registry, authority, lease, root, peer, store, journal);
    }

    private sealed record Setup(
        FixtureB1AuthoritySession Session,
        FixtureB1AuthorityRegistry Registry,
        FakeObjectAuthority ObjectAuthority,
        FixtureB1JournalStorageLease StorageLease,
        FakeRootAuthority Root,
        FakePeerAuthority Peer,
        FakeStore Store,
        FixtureB1FramedJournalContract Journal);

    private sealed class FakeObjectAuthority : IFixtureB1ExactObjectAuthority
    {
        internal bool Accept { get; set; } = true;
        public bool IsExactRetainedObject(
            FixtureB1AuthoritySession session,
            FixtureB1ExactObjectObservation observation) => Accept;
    }

    private sealed class FakeRootAuthority : IFixtureB1JournalRootAuthority
    {
        internal bool LocalFixedRoot { get; set; } = true;
        internal bool PrivateAcl { get; set; } = true;
        internal bool NoReparse { get; set; } = true;
        internal bool ExclusiveWriter { get; set; } = true;
        public bool IsAdmitted(
            FixtureB1AuthoritySession session,
            FixtureB1JournalStorageLease lease) =>
            LocalFixedRoot && PrivateAcl && NoReparse && ExclusiveWriter &&
            lease.WrapperInstance == session.WrapperInstance &&
            lease.SessionEpoch == session.SessionEpoch &&
            lease.ConnectionGeneration == session.ConnectionGeneration;
    }

    private sealed class FakePeerAuthority : IFixtureB1JournalPeerAuthority
    {
        internal bool Accept { get; set; } = true;
        public bool IsExactAckAuthenticated(
            FixtureB1AuthoritySession session,
            Guid invocationId,
            Guid completionId,
            Guid sourceTokenId,
            FixtureB1AuthorityObjectKind sourceKind,
            FixtureB1EndpointRole origin) => Accept;
    }

    public enum AppendFault
    {
        None,
        Refuse,
        TornHeader,
        TornPayload,
        TornDigest,
        WrittenButFalse,
    }

    private enum AnchorFault
    {
        None,
        Refuse,
        CommittedButReplyLost,
    }

    private sealed class FakeStore : IFixtureB1FramedJournalStore
    {
        private readonly List<byte[]> _frames = [];
        private FixtureB1JournalAnchor? _anchor;
        internal int FrameCount => _frames.Count;
        internal AppendFault NextAppendFault { get; set; }
        internal AnchorFault NextAnchorFault { get; set; }

        public bool TryAppendAndFlush(byte[] frame)
        {
            var fault = NextAppendFault;
            NextAppendFault = AppendFault.None;
            switch (fault)
            {
                case AppendFault.Refuse: return false;
                case AppendFault.TornHeader: _frames.Add(frame[..2]); return false;
                case AppendFault.TornPayload:
                    _frames.Add(frame[..(frame.Length / 2)]); return false;
                case AppendFault.TornDigest:
                    _frames.Add(frame[..^16]); return false;
                case AppendFault.WrittenButFalse:
                    _frames.Add((byte[])frame.Clone()); return false;
                default:
                    _frames.Add((byte[])frame.Clone()); return true;
            }
        }

        public bool TryAdvanceAndFlushAnchor(FixtureB1JournalAnchor anchor)
        {
            var fault = NextAnchorFault;
            NextAnchorFault = AnchorFault.None;
            if (fault == AnchorFault.Refuse) return false;
            _anchor = new(anchor.Ordinal, (byte[])anchor.Digest.Clone());
            return fault != AnchorFault.CommittedButReplyLost;
        }

        public byte[] ReadFrames() => [.. _frames.SelectMany(frame => frame)];
        public FixtureB1JournalAnchor? ReadAnchor() => _anchor;

        internal void TruncateLastByte()
        {
            var last = _frames[^1];
            _frames[^1] = last[..^1];
        }

        internal void ReorderFirstTwoFrames() =>
            (_frames[0], _frames[1]) = (_frames[1], _frames[0]);

        internal void DuplicateFirstFrame() => _frames.Insert(1, _frames[0]);
        internal void DropLastFrame() => _frames.RemoveAt(_frames.Count - 1);
        internal void DropEverything()
        {
            _frames.Clear();
            _anchor = null;
        }

        internal void ChangeLastOriginAndRehash(FixtureB1EndpointRole origin)
        {
            var frame = (byte[])_frames[^1].Clone();
            var bodyLength = BitConverter.ToInt32(frame, 0);
            frame[18] = (byte)origin;
            var digest = SHA256.HashData(frame.AsSpan(4, bodyLength));
            digest.CopyTo(frame, 4 + bodyLength);
            _frames[^1] = frame;
            _anchor = new(_anchor!.Ordinal, digest);
        }
    }
}
