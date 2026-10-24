using WatchDog.CaptureIsolation.Windows.Qualification;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1LauncherModelTests
{
    private static readonly Guid RunNonce =
        Guid.Parse("10000000-2222-3333-4444-555555555555");
    private static readonly Guid LaunchId =
        Guid.Parse("20000000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureHandoffProcessIdentity Launcher = new(401, 4_001);
    private static readonly FixtureHandoffProcessIdentity Runner = new(402, 4_002);

    [Fact]
    public async Task FullExchangeUsesFrozenOrderAndPassesOnlyAfterCleanupAndFinalClose()
    {
        var session = new FakeSession();
        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.True(result.ExitCode == QualificationExitCode.Pass,
            $"exit={result.ExitCode}, active={result.ActivationAcknowledged}, " +
            $"cleanup={result.CleanupConfirmed}, abort={result.AbortConfirmed}, " +
            $"drain={result.DrainConfirmed}, events={string.Join(',', session.Events)}");
        Assert.True(result.ActivationAcknowledged);
        Assert.True(result.CleanupConfirmed);
        Assert.True(result.DrainConfirmed);
        Assert.Equal(Launcher, result.Launcher);
        Assert.Equal(Runner, result.Target);
        Assert.Equal(7, session.Duplicates);
        Assert.Equal(1, session.ResumeCalls);
        Assert.Equal(1, session.ExitCleanupCalls);
        Assert.Equal(
            new[]
            {
                "prepare", "duplicate:0", "observe:0", "duplicate:1", "observe:1",
                "duplicate:2", "observe:2", "duplicate:3", "observe:3",
                "duplicate:4", "observe:4", "duplicate:5", "observe:5",
                "duplicate:6", "observe:6", "resume", "accept", "descriptor",
                "proof", "grant", "grant-ack", "activation", "active-ack",
                "drain", "reconcile", "close", "observe-artifacts-after-close",
            },
            session.Events);
        Assert.True(session.CloseSawReconciled);
        Assert.Equal(0, session.ContentOperations);
    }

    [Theory]
    [InlineData("peer")]
    [InlineData("ids")]
    [InlineData("slot")]
    public async Task WrongPeerIdsOrSlotCannotPassAndStillReconciles(string failure)
    {
        var session = new FakeSession();
        if (failure == "peer")
            session.WrongPeer = true;
        else if (failure == "ids")
            session.CommandAttributed = false;
        else
            session.CorruptSlotIndex = 3;

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.NotEqual(QualificationExitCode.Pass, result.ExitCode);
        Assert.False(result.ActivationAcknowledged);
        Assert.True(result.CleanupConfirmed);
        Assert.True(session.Reconciled);
        if (failure == "peer")
            Assert.DoesNotContain("descriptor", session.Events);
        if (failure == "ids")
            Assert.DoesNotContain("prepare", session.Events);
    }

    [Fact]
    public async Task ProofOnDifferentConnectionFromSameRunnerIsRejected()
    {
        var session = new FakeSession { WrongProofReceipt = true };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            result.ExitCode);
        Assert.False(result.ActivationAcknowledged);
        Assert.DoesNotContain("grant", session.Events);
        Assert.True(result.CleanupConfirmed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialDeliveryAndPreRegistrationMutationAreCleaned(bool failPreparation)
    {
        var session = new FakeSession
        {
            ThrowPrepareAfterMutation = failPreparation,
            ThrowDuplicateIndex = failPreparation ? null : 3,
        };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(failPreparation
                ? QualificationExitCode.InfrastructureExceptionCleanupConfirmed
                : QualificationExitCode.CleanupIncomplete,
            result.ExitCode);
        Assert.Equal(failPreparation, result.CleanupConfirmed);
        Assert.Equal(Runner, result.Target);
        Assert.True(session.Reconciled);
        Assert.True(session.CloseSawReconciled);
        Assert.Equal(failPreparation ? 0 : 3, session.LastCleanupJournalCount);
    }

    [Fact]
    public async Task FaultAfterDuplicateWithoutReceiptIsCleanupIncomplete()
    {
        var session = new FakeSession { ThrowAfterDuplicateIndex = 3 };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.False(result.CleanupConfirmed);
        Assert.Equal(4, session.Duplicates);
        Assert.Equal(4, session.AdoptedCoordinateCount);
        Assert.True(session.RetainsCustody);
        Assert.Equal(4, session.LastReconciledAdoptedCount);
        Assert.Equal(3, session.LastCleanupJournalCount);
        Assert.True(session.Reconciled);
        Assert.True(session.CloseSawReconciled);
    }

    [Fact]
    public async Task PreRegistrationMutationWithoutIndependentRetainedTargetIsCodeFive()
    {
        var session = new FakeSession
        {
            ThrowPrepareAfterMutation = true,
            HideRetainedTarget = true,
        };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.False(result.CleanupConfirmed);
    }

    [Fact]
    public async Task MutationWithNoTargetInEitherObservationIsCodeFive()
    {
        var session = new FakeSession
        {
            ThrowPrepareAfterMutation = true,
            HideRetainedTarget = true,
            HideTargetInCleanup = true,
        };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.False(result.CleanupConfirmed);
    }

    [Fact]
    public async Task RejectedRegistrationAfterCustodyStillConfirmsCleanup()
    {
        var session = new FakeSession();
        session.Authority.RejectExpectedBinary = true;

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            result.ExitCode);
        Assert.True(result.CleanupConfirmed);
        Assert.Equal(Runner, result.Target);
        Assert.True(session.Reconciled);
    }

    [Fact]
    public async Task LateDuplicateReceiptIsJournaledBeforeCleanup()
    {
        var session = new FakeSession { BlockDuplicateIndex = 2 };
        using var cancellation = new CancellationTokenSource();
        var pending = new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), cancellation.Token);
        await session.DuplicateEntered.Task.WaitAsync(TestTimeouts.Signal);

        cancellation.Cancel();
        var result = await pending.WaitAsync(TestTimeouts.Signal);

        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            result.ExitCode);
        Assert.True(result.CleanupConfirmed);
        Assert.Equal(3, session.LastCleanupJournalCount);
    }

    [Fact]
    public async Task LateDuplicateReceiptDisagreeingWithAdoptionCannotConfirmCleanup()
    {
        var session = new FakeSession
        {
            BlockDuplicateIndex = 2,
            CorruptLateDuplicateReceipt = true,
        };
        using var cancellation = new CancellationTokenSource();
        var pending = new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), cancellation.Token);
        await session.DuplicateEntered.Task.WaitAsync(TestTimeouts.Signal);

        cancellation.Cancel();
        var result = await pending.WaitAsync(TestTimeouts.Signal);

        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.False(result.CleanupConfirmed);
        Assert.True(session.CustodyAmbiguous);
        Assert.Equal(3, session.LastCleanupJournalCount);
        Assert.Equal(3, session.LastReconciledAdoptedCount);
    }

    [Fact]
    public async Task CancellationAbortsNoncooperativeReceiveThenAwaitsOriginalAndDrain()
    {
        var session = new FakeSession { BlockProof = true, BlockDrain = true };
        using var cancellation = new CancellationTokenSource();
        var pending = new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), cancellation.Token);
        await session.ProofEntered.Task.WaitAsync(TestTimeouts.Signal);

        cancellation.Cancel();
        await session.AbortEntered.Task.WaitAsync(TestTimeouts.Signal);
        await session.DrainEntered.Task.WaitAsync(TestTimeouts.Signal);
        Assert.False(pending.IsCompleted);

        session.DrainRelease.TrySetResult();
        var result = await pending.WaitAsync(TestTimeouts.Signal);

        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            result.ExitCode);
        Assert.True(result.AbortConfirmed);
        Assert.True(result.DrainConfirmed);
        Assert.DoesNotContain("grant", session.Events);
    }

    [Fact]
    public async Task CancellationDuringCleanupCannotReturnPass()
    {
        var session = new FakeSession { BlockDrain = true };
        using var cancellation = new CancellationTokenSource();
        var pending = new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), cancellation.Token);
        await session.DrainEntered.Task.WaitAsync(TestTimeouts.Signal);

        cancellation.Cancel();
        session.DrainRelease.TrySetResult();
        var result = await pending.WaitAsync(TestTimeouts.Signal);

        Assert.True(result.ActivationAcknowledged);
        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            result.ExitCode);
        Assert.True(result.CleanupConfirmed);
    }

    [Fact]
    public async Task DeadlineAbortsNoncooperativeReceiveAndWaitsForOriginalAndDrainBeforeCodeFour()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeSession
        {
            BlockProof = true,
            BlockDrain = true,
            TestClock = clock,
        };
        var pending = new FixtureB1LauncherModel(
            session, TimeSpan.FromSeconds(1))
            .ExecuteAsync(Command(), CancellationToken.None);
        await session.ProofEntered.Task.WaitAsync(TestTimeouts.Signal);
        clock.Advance(TimeSpan.FromSeconds(1));
        await session.AbortEntered.Task.WaitAsync(TestTimeouts.Signal);
        await session.DrainEntered.Task.WaitAsync(TestTimeouts.Signal);
        Assert.False(pending.IsCompleted);

        session.DrainRelease.TrySetResult();
        var result = await pending.WaitAsync(TestTimeouts.Signal);

        Assert.Equal(QualificationExitCode.TimeoutCleanupConfirmed, result.ExitCode);
        Assert.True(result.AbortConfirmed);
        Assert.True(result.DrainConfirmed);
        Assert.True(result.CleanupConfirmed);
    }

    [Fact]
    public async Task CleanupDeadlineDoesNotRestartAfterAbortDrain()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeSession
        {
            BlockProof = true,
            BlockDrain = true,
            TestClock = clock,
        };
        using var cancellation = new CancellationTokenSource();
        var pending = new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), cancellation.Token);
        await session.ProofEntered.Task.WaitAsync(TestTimeouts.Signal);

        cancellation.Cancel();
        await session.DrainEntered.Task.WaitAsync(TestTimeouts.Signal);
        clock.Advance(QualificationConstants.CleanupTimeout);
        try
        {
            var result = await pending.WaitAsync(TestTimeouts.Signal);

            Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
            Assert.False(result.CleanupConfirmed);
            Assert.False(result.DrainConfirmed);
            Assert.DoesNotContain("reconcile", session.Events);
            Assert.DoesNotContain("close", session.Events);
        }
        finally
        {
            session.DrainRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task FailedDrainLeavesLiveObserverAndAllLeasesWithSession()
    {
        var session = new FakeSession { DrainFails = true };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.False(result.CleanupConfirmed);
        Assert.True(session.ObserverStillActive);
        Assert.True(session.RetainsCustody);
        Assert.Null(result.Cleanup);
        Assert.Null(result.LocalClose);
        Assert.DoesNotContain("reconcile", session.Events);
        Assert.DoesNotContain("close", session.Events);
        Assert.False(session.ReaperReleasedAtClose);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task EitherMissingTerminalConditionRetainsReaper(
        bool exactTargetReaped, bool outerJobEmpty)
    {
        var session = new FakeSession
        {
            ExactTargetReaped = exactTargetReaped,
            OuterJobEmpty = outerJobEmpty,
        };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.False(session.ReaperReleasedAtClose);
        Assert.False(result.LocalClose!.ReapingLeaseClosed);
    }

    [Fact]
    public async Task RepeatedAdoptionRemainsLedgerOwnedAndCannotPass()
    {
        var session = new FakeSession { ReadoptSameCoordinateAt = 0 };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.True(session.CustodyAmbiguous);
        Assert.Equal(7, session.AdoptedCoordinateCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedNewCoordinateStaysWithProviderUntilItsOwnTerminalProof(
        bool foreignTarget)
    {
        var session = new FakeSession
        {
            RejectNewCoordinateAt = 0,
            RejectedCoordinateForeignTarget = foreignTarget,
        };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.True(session.CustodyAmbiguous);
        Assert.Equal(7, session.AdoptedCoordinateCount);
        Assert.Equal(!foreignTarget, session.RejectedCoordinateClosed);
        Assert.Equal(!foreignTarget, result.LocalClose!.RejectedAdoptionsClosed);
    }

    [Fact]
    public async Task BrokerRechecksArtifactBeforeLocalLeaseClosure()
    {
        var session = new FakeSession();
        session.Authority.RejectLateSnapshot = true;

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.True(session.Authority.SnapshotRechecked);
        Assert.True(session.CloseSawReconciled);
        Assert.True(session.ReaperReleasedAtClose);
    }

    [Fact]
    public async Task ArtifactDriftDuringLocalCloseCannotPass()
    {
        var session = new FakeSession { RejectArtifactAfterClose = true };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.True(result.ActivationAcknowledged);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.False(result.CleanupConfirmed);
        Assert.True(result.LocalClose!.Complete);
        Assert.Contains("observe-artifacts-after-close", session.Events);
    }

    [Fact]
    public async Task MalformedAdoptedReaperLeaseCannotConfirmOrRelease()
    {
        var session = new FakeSession { MalformedReaperLease = true };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.True(session.CustodyAmbiguous);
        Assert.False(session.ReaperReleasedAtClose);
    }

    [Fact]
    public async Task TokenIgnoringCloseAfterCleanupDeadlineCannotPass()
    {
        var clock = new ManualTimeProvider();
        var session = new FakeSession
        {
            TestClock = clock,
            AdvanceDuringClose = QualificationConstants.CleanupTimeout,
        };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.True(result.ActivationAcknowledged);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.False(result.CleanupConfirmed);
        Assert.True(session.CloseSawReconciled);
    }

    [Fact]
    public async Task MalformedPreparationStillErasesReturnedSecretAndCleansRetainedTarget()
    {
        var session = new FakeSession { ReturnMalformedPreparation = true };

        var result = await new FixtureB1LauncherModel(session)
            .ExecuteAsync(Command(), CancellationToken.None);

        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            result.ExitCode);
        Assert.NotNull(session.LastPreparationSecret);
        Assert.All(session.LastPreparationSecret!, value => Assert.Equal(0, value));
        Assert.True(result.CleanupConfirmed);
        Assert.Equal(Runner, result.Target);
    }

    [Theory]
    [InlineData("abort")]
    [InlineData("drain")]
    [InlineData("close")]
    [InlineData("job")]
    public async Task AbortCloseOrJobUncertaintyReturnsCodeFive(string failure)
    {
        var session = new FakeSession();
        using var cancellation = new CancellationTokenSource();
        Task<FixtureB1LauncherResult> pending;
        if (failure == "abort")
        {
            session.BlockProof = true;
            session.AbortThrowsAfterRelease = true;
            pending = new FixtureB1LauncherModel(session)
                .ExecuteAsync(Command(), cancellation.Token);
            await session.ProofEntered.Task.WaitAsync(TestTimeouts.Signal);
            cancellation.Cancel();
        }
        else
        {
            session.DrainFails = failure == "drain";
            session.CloseFails = failure == "close";
            session.OuterJobEmpty = failure != "job";
            pending = new FixtureB1LauncherModel(session)
                .ExecuteAsync(Command(), CancellationToken.None);
        }

        var result = await pending.WaitAsync(TestTimeouts.Signal);

        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.False(result.CleanupConfirmed);
        Assert.Equal(failure != "drain", session.Reconciled);
        Assert.Equal(failure != "drain", session.CloseSawReconciled);
        if (failure == "job")
            Assert.False(session.ReaperReleasedAtClose);
    }

    [Fact]
    public async Task ModelIsOneUseAndReturnsNoSessionOrBrokerAuthority()
    {
        var model = new FixtureB1LauncherModel(new FakeSession());
        _ = await model.ExecuteAsync(Command(), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => model.ExecuteAsync(Command(), CancellationToken.None));
        Assert.DoesNotContain(typeof(FixtureB1LauncherResult).GetProperties(), property =>
            typeof(IFixtureB1LauncherOwnedSession).IsAssignableFrom(property.PropertyType) ||
            typeof(IFixtureBrokerAuthority).IsAssignableFrom(property.PropertyType) ||
            property.PropertyType == typeof(FixtureBrokerState));
    }

    private static FixtureB1LauncherCommand Command() => new(
        RunNonce,
        @"C:\WatchDog\qualification.exe",
        new string('a', 40),
        new string('b', 64),
        new string('c', 64),
        new string('d', 64),
        new string('e', 64),
        new string('f', 64),
        new string('1', 64),
        new string('2', 64));

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

    private sealed class FakeSession : IFixtureB1LauncherOwnedSession
    {
        private const long Receipt = 77;
        private const long ChildReceipt = 78;
        private readonly byte[] _secret = Enumerable.Range(1, 32)
            .Select(value => (byte)value).ToArray();
        private readonly FixtureArtifactHandoffPlan _plan;
        private readonly FixtureBrokerSnapshot _snapshot;
        private readonly FixtureBrokerDeliveryReceipt[] _receipts;
        private readonly FakeObserver _observer;
        private FixtureBrokerChildState? _child;
        private FixtureB1CleanupContext? _cleanupContext;
        private FixtureB1AdapterCustodyModel.CustodyLedger? _custodyLedger;
        private FixtureB1AdapterTargetCoordinate? _rejectedCoordinate;
        private bool _prepared;

        internal FakeSession()
        {
            _plan = FixtureArtifactHandoffPlan.Create(
                FixtureHandoffEdge.LauncherToRunner, null, RunNonce, LaunchId);
            var parties = new FixtureBrokerParties(Launcher, Launcher, Runner);
            var directory = new FixtureArtifactIdentity(
                17, 1, 0, FixtureArtifactKind.Directory, false);
            var outputs = FixtureArtifactHandoffLedger.ExpectedEntries(null)
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select((pair, index) => new FixtureBrokerOutput(
                    pair.Key,
                    pair.Value.Writer,
                    pair.Value.WriterRights,
                    new FixtureArtifactIdentity(
                        17, (ulong)(index + 10), 0, FixtureArtifactKind.File, false),
                    directory))
                .ToArray();
            _snapshot = new(
                _plan.Edge, null, RunNonce, LaunchId, parties,
                FixtureBrokerDeliveryMode.LocalLauncher, directory, outputs);
            _receipts = _plan.Slots.Select((slot, index) =>
                new FixtureBrokerDeliveryReceipt(
                    slot.Label,
                    (nint)(3_000 + index),
                    slot.Kind,
                    slot.Rights,
                    SlotIdentity(_snapshot, slot))).ToArray();
            Authority = new FakeAuthority(_plan, parties, _snapshot, _receipts, Events);
            _observer = new FakeObserver(_snapshot, _receipts, _secret);
        }

        internal FakeAuthority Authority { get; }
        internal List<string> Events { get; } = [];
        internal bool CommandAttributed { get; set; } = true;
        internal bool WrongPeer { get; set; }
        internal bool WrongProofReceipt { get; set; }
        internal int? CorruptSlotIndex { get; set; }
        internal int? ThrowDuplicateIndex { get; set; }
        internal int? ThrowAfterDuplicateIndex { get; set; }
        internal bool ThrowPrepareAfterMutation { get; set; }
        internal bool HideRetainedTarget { get; set; }
        internal bool HideTargetInCleanup { get; set; }
        internal bool ReturnMalformedPreparation { get; set; }
        internal int? BlockDuplicateIndex { get; set; }
        internal bool BlockProof { get; set; }
        internal bool BlockDrain { get; set; }
        internal bool DrainFails { get; set; }
        internal bool ObserverStillActive { get; private set; }
        internal bool AbortThrowsAfterRelease { get; set; }
        internal bool CloseFails { get; set; }
        internal TimeSpan AdvanceDuringClose { get; set; }
        internal bool OuterJobEmpty { get; set; } = true;
        internal bool ExactTargetReaped { get; set; } = true;
        internal bool MalformedReaperLease { get; set; }
        internal bool CorruptLateDuplicateReceipt { get; set; }
        internal bool RejectArtifactAfterClose { get; set; }
        internal int? ReadoptSameCoordinateAt { get; set; }
        internal int? RejectNewCoordinateAt { get; set; }
        internal bool RejectedCoordinateForeignTarget { get; set; }
        internal bool RejectedCoordinateClosed { get; private set; }
        internal TimeProvider TestClock { get; set; } = TimeProvider.System;
        internal int Duplicates { get; private set; }
        internal int ResumeCalls { get; private set; }
        internal int ExitCleanupCalls { get; private set; }
        internal int LastCleanupJournalCount { get; private set; }
        internal int LastReconciledAdoptedCount { get; private set; }
        internal int ContentOperations { get; private set; }
        internal byte[]? LastPreparationSecret { get; private set; }
        internal bool Reconciled { get; private set; }
        internal bool CloseSawReconciled { get; private set; }
        internal bool ReaperReleasedAtClose { get; private set; }
        internal bool RetainsCustody => _custodyLedger is not null;
        internal bool CustodyAmbiguous => _custodyLedger?.Ambiguous == true;
        internal int AdoptedCoordinateCount => _custodyLedger?.Coordinates.Count ?? 0;
        internal TaskCompletionSource ProofEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DuplicateEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AbortEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DrainEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DrainRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<FixtureBrokerFrameObservation> ProofRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<FixtureBrokerDeliveryReceipt> DuplicateRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Guid LaunchId => FixtureB1LauncherModelTests.LaunchId;
        public string ControlEndpoint => "WDB1-20000000bbbbccccddddeeeeeeeeeeee";
        public FixtureHandoffProcessIdentity Launcher =>
            FixtureB1LauncherModelTests.Launcher;
        public IFixtureBrokerAuthority BrokerAuthority => Authority;
        public TimeProvider Clock => TestClock;
        public FixtureB1CleanupContext CleanupContext =>
            _cleanupContext ??= new(TestClock, QualificationConstants.CleanupTimeout);
        public bool MutationStarted => _prepared;

        public void RetainCustodyLedger(
            FixtureB1AdapterCustodyModel.CustodyLedger ledger)
        {
            Assert.Null(_custodyLedger);
            _custodyLedger = ledger;
        }

        public bool AreCommandClaimsAttributed(FixtureB1LauncherCommand command) =>
            CommandAttributed && command.RunNonce == RunNonce;
        public FixtureHandoffProcessIdentity ObserveCreator() => Launcher;
        public FixtureHandoffProcessIdentity ObserveIssuer() => Launcher;
        public FixtureHandoffProcessIdentity? ObserveRetainedTarget() =>
            _prepared && !HideRetainedTarget ? Runner : null;

        public ValueTask<FixtureB1LauncherPreparation> PrepareTargetAsync(
            FixtureArtifactHandoffPlan plan,
            FixtureB1LauncherCommand command,
            IFixtureB1AdapterAdoptionSink adoption,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("prepare");
            _prepared = true;
            Assert.True(RetainsCustody);
            Assert.Equal(FixtureB1AdoptionDisposition.Adopted,
                adoption.TryAdoptTarget(new(
                    LaunchId, Runner, 501, 502, 503,
                    MalformedReaperLease ? 0 : 504)));
            if (ThrowPrepareAfterMutation)
                throw new IOException("prepare fault after retained target creation");
            var sealedSnapshot = FixtureBrokerSnapshotCodec.Seal(_snapshot, _secret);
            LastPreparationSecret = (byte[])_secret.Clone();
            return ValueTask.FromResult(new FixtureB1LauncherPreparation(
                ReturnMalformedPreparation
                    ? null!
                    : new(Runner, new((nint)501), new((nint)601)),
                _snapshot,
                sealedSnapshot.Bytes,
                sealedSnapshot.Mac,
                LastPreparationSecret));
        }

        public ValueTask<FixtureBrokerDeliveryReceipt> DuplicateNextSlotAsync(
            FixtureHandoffSlot slot,
            int index,
            IFixtureB1AdapterAdoptionSink adoption,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add($"duplicate:{index}");
            if (ThrowDuplicateIndex == index)
                throw new IOException("duplicate fault");
            var receipt = _receipts[index];
            Assert.Equal(FixtureB1AdoptionDisposition.Adopted,
                adoption.TryAdoptCoordinate(new(
                    LaunchId, Runner, slot.Label, (long)receipt.TargetHandle)));
            if (ReadoptSameCoordinateAt == index)
                Assert.Equal(FixtureB1AdoptionDisposition.AlreadyOwned,
                    adoption.TryAdoptCoordinate(new(
                        LaunchId, Runner, slot.Label, (long)receipt.TargetHandle)));
            if (RejectNewCoordinateAt == index)
            {
                _rejectedCoordinate = new(
                    LaunchId,
                    RejectedCoordinateForeignTarget
                        ? new FixtureHandoffProcessIdentity(499, 4_999)
                        : Runner,
                    slot.Label,
                    (long)receipt.TargetHandle + 10_000);
                Assert.Equal(FixtureB1AdoptionDisposition.ProviderRetainsNewResource,
                    adoption.TryAdoptCoordinate(_rejectedCoordinate));
            }
            if (BlockDuplicateIndex == index)
            {
                DuplicateEntered.TrySetResult();
                return new ValueTask<FixtureBrokerDeliveryReceipt>(DuplicateRelease.Task);
            }
            Duplicates++;
            if (ThrowAfterDuplicateIndex == index)
                throw new IOException("duplicate fault after target transfer");
            if (CorruptSlotIndex == index)
                receipt = receipt with { Rights = FixtureHandoffRights.None };
            return ValueTask.FromResult(receipt);
        }

        public ValueTask ResumeTargetAsync(
            FixtureHandoffProcessIdentity exactTarget,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(Runner, exactTarget);
            Events.Add("resume");
            ResumeCalls++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<FixtureB1LauncherConnection> AcceptRunnerAsync(
            string controlEndpoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("accept");
            Authority.ConnectionPeer = WrongPeer ? Launcher : Runner;
            return ValueTask.FromResult(new FixtureB1LauncherConnection(
                Receipt, WrongPeer ? Launcher : Runner));
        }

        public ValueTask SendDescriptorAsync(
            long connectionReceipt,
            FixtureBrokerDescriptor descriptor,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("descriptor");
            _child = FixtureBrokerChildState.Accept(
                descriptor, _plan, _snapshot.Parties, _secret,
                _observer, Authority, Clock, CancellationToken.None);
            return ValueTask.CompletedTask;
        }

        public ValueTask<FixtureBrokerFrameObservation> ReceiveProofAsync(
            long connectionReceipt,
            CancellationToken cancellationToken)
        {
            Events.Add("proof");
            var observation = new FixtureBrokerFrameObservation(
                WrongProofReceipt ? Receipt + 1 : Receipt, _child!.Proof);
            if (!BlockProof)
                return ValueTask.FromResult(observation);
            ProofEntered.TrySetResult();
            return new ValueTask<FixtureBrokerFrameObservation>(ProofRelease.Task);
        }

        public ValueTask SendGrantAsync(
            long connectionReceipt,
            ReadOnlyMemory<byte> grant,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("grant");
            _child!.AcceptGrant(new(ChildReceipt, grant.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask<FixtureBrokerFrameObservation> ReceiveGrantAckAsync(
            long connectionReceipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("grant-ack");
            return ValueTask.FromResult(new FixtureBrokerFrameObservation(
                Receipt, _child!.GrantAck));
        }

        public ValueTask SendActivationAsync(
            long connectionReceipt,
            ReadOnlyMemory<byte> activation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("activation");
            _child!.AcceptActivation(new(ChildReceipt, activation.ToArray()));
            return ValueTask.CompletedTask;
        }

        public ValueTask<FixtureBrokerFrameObservation> ReceiveActivationAckAsync(
            long connectionReceipt,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("active-ack");
            return ValueTask.FromResult(new FixtureBrokerFrameObservation(
                Receipt, _child!.ActivationAck));
        }

        public ValueTask AbortAsync(CancellationToken cancellationToken)
        {
            Events.Add("abort");
            AbortEntered.TrySetResult();
            if (BlockProof)
                ProofRelease.TrySetException(new IOException("receive aborted"));
            if (BlockDuplicateIndex is { } index)
                DuplicateRelease.TrySetResult(CorruptLateDuplicateReceipt
                    ? _receipts[index] with
                    {
                        TargetHandle = _receipts[index].TargetHandle + 1,
                    }
                    : _receipts[index]);
            if (AbortThrowsAfterRelease)
                throw new IOException("abort failed after releasing receive");
            return ValueTask.CompletedTask;
        }

        public async ValueTask DrainAsync(CancellationToken cancellationToken)
        {
            Events.Add("drain");
            DrainEntered.TrySetResult();
            if (DrainFails)
            {
                ObserverStillActive = true;
                throw new IOException("drain failed");
            }
            if (BlockDrain)
                await DrainRelease.Task.WaitAsync(cancellationToken);
        }

        public ValueTask<FixtureB1LauncherCleanupObservation> ReconcileAsync(
            FixtureHandoffProcessIdentity? exactTarget,
            IReadOnlyList<FixtureBrokerDeliveryReceipt> journaledTransfers,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("reconcile");
            ExitCleanupCalls++;
            Reconciled = true;
            LastCleanupJournalCount = journaledTransfers.Count;
            LastReconciledAdoptedCount = _custodyLedger?.Coordinates.Count ?? 0;
            Authority.CleanupClosed = true;
            var closes = journaledTransfers.ToDictionary(
                receipt => receipt.TargetHandle,
                _ => FixtureArtifactCloseStatus.Closed);
            return ValueTask.FromResult(new FixtureB1LauncherCleanupObservation(
                _prepared && !HideTargetInCleanup ? Runner : null,
                _prepared && ExactTargetReaped,
                OuterJobEmpty,
                closes,
                _prepared,
                true,
                true,
                true));
        }

        public ValueTask<FixtureB1LauncherLocalCloseReport> CloseLocalAsync(
            bool mayReleaseReaper,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add("close");
            CloseSawReconciled = Reconciled;
            if (TestClock is ManualTimeProvider clock && AdvanceDuringClose > TimeSpan.Zero)
                clock.Advance(AdvanceDuringClose);
            ReaperReleasedAtClose = mayReleaseReaper && !CloseFails;
            Authority.LocalClosed = true;
            RejectedCoordinateClosed = _rejectedCoordinate is not null &&
                _rejectedCoordinate.ExactTarget == Runner && mayReleaseReaper && !CloseFails;
            return ValueTask.FromResult(new FixtureB1LauncherLocalCloseReport(
                ReaperReleasedAtClose || !_prepared, true, true, true, true, true,
                _rejectedCoordinate is null || RejectedCoordinateClosed));
        }

        public bool AreRetainedRootAndOutputsAdmitted()
        {
            Events.Add(Authority.LocalClosed
                ? "observe-artifacts-after-close"
                : "observe-artifacts-before-close");
            return !(RejectArtifactAfterClose && Authority.LocalClosed);
        }
    }

    private sealed class FakeObserver : IFixtureBrokerChildLocalObserver
    {
        private readonly FixtureBrokerSnapshot _snapshot;
        private readonly Dictionary<string, FixtureHandoffObservedHandle> _slots;
        private readonly Dictionary<string, FixtureArtifactIdentity> _outputs;
        private readonly byte[] _secret;

        internal FakeObserver(
            FixtureBrokerSnapshot snapshot,
            IEnumerable<FixtureBrokerDeliveryReceipt> receipts,
            byte[] secret)
        {
            _snapshot = snapshot;
            _secret = (byte[])secret.Clone();
            _slots = receipts.ToDictionary(
                receipt => receipt.Label,
                receipt => new FixtureHandoffObservedHandle(
                    receipt.TargetHandle, receipt.Kind, receipt.Rights, receipt.Identity));
            _outputs = snapshot.Outputs.ToDictionary(
                output => output.Name, output => output.Identity, StringComparer.Ordinal);
        }

        public FixtureHandoffProcessIdentity ObserveTargetProcess() => Runner;
        public FixtureHandoffProcessIdentity ObserveCreatorProcess() => Launcher;
        public FixtureHandoffProcessIdentity ObserveIssuerProcess() => Launcher;
        public FixtureArtifactIdentity ObserveRetainedDirectory() => _snapshot.Directory;
        public FixtureArtifactIdentity ObserveOutput(string literalName) => _outputs[literalName];
        public int DeliveredSlotCount => _slots.Count;
        public FixtureHandoffObservedHandle ObserveDeliveredSlot(string label) => _slots[label];
        public FixtureHandoffProcessIdentity ObserveProcessAtTargetCoordinate(nint coordinate) =>
            Launcher;
        public byte[] ReadSectionKeyAtTargetCoordinate(nint coordinate) =>
            (byte[])_secret.Clone();
        public bool IsExactChild(
            FixtureArtifactIdentity parent,
            string literalName,
            FixtureArtifactIdentity output) =>
            parent == _snapshot.Directory && _outputs[literalName] == output;
    }

    private sealed class FakeAuthority : IFixtureBrokerAuthority
    {
        private readonly FixtureArtifactHandoffPlan _plan;
        private readonly FixtureBrokerParties _parties;
        private readonly FixtureBrokerSnapshot _snapshot;
        private readonly Dictionary<(string, nint), FixtureBrokerDeliveryReceipt> _receipts;
        private readonly List<string> _events;
        private bool _reserved;

        internal FakeAuthority(
            FixtureArtifactHandoffPlan plan,
            FixtureBrokerParties parties,
            FixtureBrokerSnapshot snapshot,
            IEnumerable<FixtureBrokerDeliveryReceipt> receipts,
            List<string> events)
        {
            _plan = plan;
            _parties = parties;
            _snapshot = snapshot;
            _events = events;
            _receipts = receipts.ToDictionary(
                receipt => (receipt.Label, receipt.TargetHandle));
        }

        internal FixtureHandoffProcessIdentity ConnectionPeer { get; set; } = Runner;
        internal bool CleanupClosed { get; set; }
        internal bool RejectExpectedBinary { get; set; }
        internal bool RejectLateSnapshot { get; set; }
        internal bool SnapshotRechecked { get; private set; }
        internal bool LocalClosed { get; set; }

        public bool TryReserveLaunch(Guid launchId) => !_reserved && (_reserved = true);
        public bool IsProcessAlive(FixtureHandoffProcessIdentity process) =>
            process == Launcher || process == Runner;
        public bool AreExpectedCreatorAndIssuer(
            FixtureHandoffEdge edge,
            FixtureHandoffProcessIdentity creator,
            FixtureHandoffProcessIdentity issuer) =>
            edge == _plan.Edge && creator == Launcher && issuer == Launcher;
        public bool HasAuthenticatedCreatorSession(
            FixtureHandoffProcessIdentity creator,
            FixtureHandoffProcessIdentity issuer) => true;
        public bool IsCreatorRegistrationSourceValid(
            FixtureHandoffProcessIdentity creator,
            FixtureBrokerCreatorProcessCoordinate source,
            FixtureHandoffProcessIdentity target) =>
            creator == Launcher && source.Value == (nint)501 && target == Runner;
        public bool IsIssuerTargetCustodyValid(
            FixtureHandoffProcessIdentity issuer,
            FixtureBrokerIssuerProcessCoordinate custody,
            FixtureHandoffProcessIdentity target) =>
            issuer == Launcher && custody.Value == (nint)601 && target == Runner;
        public bool IsExpectedBinary(
            FixtureHandoffProcessIdentity target,
            QualificationRole role) => !RejectExpectedBinary &&
            target == Runner && role == QualificationRole.Runner;
        public bool IsExpectedContainment(
            FixtureHandoffProcessIdentity target,
            FixtureHandoffEdge edge) => target == Runner && edge == _plan.Edge;
        public FixtureBrokerDeliveryReceipt ObserveDeliveredHandle(
            FixtureHandoffProcessIdentity target,
            string label,
            nint targetHandle)
        {
            var index = Array.FindIndex(_plan.Slots.ToArray(), slot => slot.Label == label);
            _events.Add($"observe:{index}");
            return _receipts[(label, targetHandle)];
        }
        public FixtureArtifactCloseStatus ObserveTransferClose(
            FixtureHandoffProcessIdentity target,
            nint targetHandle) => CleanupClosed
                ? FixtureArtifactCloseStatus.Closed
                : FixtureArtifactCloseStatus.StillOpen;
        public FixtureArtifactCloseStatus ObserveIssuerCustodyClose(
            FixtureHandoffProcessIdentity issuer,
            FixtureBrokerIssuerProcessCoordinate custody) => CleanupClosed
                ? FixtureArtifactCloseStatus.Closed
                : FixtureArtifactCloseStatus.StillOpen;
        public bool IsExactTargetReaped(FixtureHandoffProcessIdentity target) => CleanupClosed;
        public bool AreObserversDrained(Guid launchId) => CleanupClosed;
        public bool AreArtifactOperationsDrained(Guid launchId) => CleanupClosed;
        public bool IsOwnerDeathTopologySafe(
            FixtureBrokerSnapshot snapshot,
            FixtureBrokerOwnerDeathEvidence evidence) => false;
        public FixtureHandoffProcessIdentity ObserveConnectedPeer(long connectionReceipt) =>
            connectionReceipt == 78 ? Launcher : ConnectionPeer;
        public bool IsSnapshotStillAdmitted(FixtureBrokerSnapshot snapshot)
        {
            Assert.False(LocalClosed);
            SnapshotRechecked = true;
            return !(RejectLateSnapshot && CleanupClosed) &&
                snapshot.Edge == _snapshot.Edge &&
                snapshot.RunNonce == _snapshot.RunNonce &&
                snapshot.LaunchId == _snapshot.LaunchId &&
                snapshot.Parties == _snapshot.Parties;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
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
            ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
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
