using WatchDog.CaptureIsolation.Windows.Qualification;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1AdapterCustodyModelTests
{
    private static readonly Guid RunNonce =
        Guid.Parse("31000000-2222-3333-4444-555555555555");
    private static readonly Guid LaunchId =
        Guid.Parse("32000000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureHandoffProcessIdentity Target = new(712, 7_120);

    [Fact]
    public async Task ValidCustodyConfirmsAllSevenCoordinatesAndKeepsReaperIndependent()
    {
        var fake = new FakeAdapter();
        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Confirmed, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.None, result.DiagnosticCode);
        Assert.True(result.MutationStarted);
        Assert.True(result.ExactTargetSignaled);
        Assert.True(result.OuterJobEmpty);
        Assert.Equal(7, result.AdoptedCoordinateCount);
        Assert.Equal(7, result.ConfirmedCoordinateCloseCount);
        Assert.Equal(7, result.AdoptedInspectionCount);
        Assert.True(result.FinalClose!.Complete);
        Assert.True(fake.ReaperObservedAfterIssuerClose);
        Assert.Equal(1, fake.IssuerCloseCalls);
        Assert.Equal(1, fake.FinalCloseCalls);
        Assert.All(fake.TransferCloseTerminalStates,
            state => Assert.Equal((true, true), state));
        Assert.DoesNotContain(typeof(FixtureB1AdapterCustodyResult).GetProperties(),
            property => property.PropertyType == typeof(string) ||
                property.PropertyType == typeof(QualificationExitCode));
    }

    [Fact]
    public void TargetAndIssuerLeaseAliasIsInvalidCustody()
    {
        var custody = new FixtureB1AdapterLaunchCustody(
            LaunchId, Target, 10, 20, 10, 30);

        Assert.False(FixtureB1AdapterCustodyModel.ValidCustodyShape(
            custody, LaunchId));
    }

    [Fact]
    public async Task SideEffectThenThrowWithoutCoordinateIsStickyUncertainty()
    {
        var fake = new FakeAdapter { ThrowDuplicateWithoutAdoptionAt = 2 };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.CustodyAmbiguous,
            result.DiagnosticCode);
        Assert.True(result.MutationStarted);
        Assert.Equal(2, result.AdoptedCoordinateCount);
        Assert.Equal(2, result.ConfirmedCoordinateCloseCount);
        Assert.True(fake.FinalCloseCalled);
    }

    [Fact]
    public async Task InspectionAdoptedBeforeValidationThrowIsStillClosed()
    {
        var fake = new FakeAdapter { ThrowInspectionValidationAt = 2 };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Rejected, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.ObservationRejected,
            result.DiagnosticCode);
        Assert.Equal(3, result.AdoptedCoordinateCount);
        Assert.Equal(3, result.AdoptedInspectionCount);
        Assert.Equal(3, fake.ClosedInspections.Count);
        Assert.True(result.FinalClose!.Complete);
    }

    [Fact]
    public async Task LateAdoptionAfterStopIsCapturedBeforeDrainAndNoTaskDetaches()
    {
        var fake = new FakeAdapter { BlockFirstDuplicate = true };
        using var cancellation = new CancellationTokenSource();
        var pending = Model(fake).ExecuteAsync(
            RunNonce, LaunchId, cancellation.Token);
        await fake.DuplicateEntered.Task.WaitAsync(TestTimeouts.Signal);

        cancellation.Cancel();
        await fake.AbortEntered.Task.WaitAsync(TestTimeouts.Signal);
        Assert.False(pending.IsCompleted);
        Assert.Equal(0, fake.DrainCalls);

        fake.DuplicateRelease.TrySetResult();
        var result = await pending.WaitAsync(TestTimeouts.Signal);

        Assert.Equal(FixtureB1AdapterTerminalState.Rejected, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.CallerCanceled,
            result.DiagnosticCode);
        Assert.Equal(1, result.AdoptedCoordinateCount);
        Assert.Equal(1, result.ConfirmedCoordinateCloseCount);
        Assert.True(fake.OriginalSettled);
        Assert.False(fake.DrainObservedBeforeOriginalSettled);
        Assert.True(fake.DrainCalls >= 1);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OneTerminalConditionNeverConfirmsTransferClose(
        bool targetSignaled,
        bool jobEmpty)
    {
        var fake = new FakeAdapter
        {
            TargetSignalOnTerminal = targetSignaled,
            JobEmptyOnTerminal = jobEmpty,
        };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.CustodyAmbiguous,
            result.DiagnosticCode);
        Assert.Equal(0, result.ConfirmedCoordinateCloseCount);
        Assert.Equal(0, fake.TransferCloseCalls);
        Assert.False(fake.ReaperReleasedAtFinalClose);
    }

    [Fact]
    public async Task IssuerSourceCloseCannotConsumeIndependentReapingLease()
    {
        var fake = new FakeAdapter { ConsumeReaperOnIssuerClose = true };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.CustodyAmbiguous,
            result.DiagnosticCode);
        Assert.False(fake.ReaperObservedAfterIssuerClose);
    }

    [Fact]
    public async Task DuplicateTargetCoordinateOwnershipIsUncertain()
    {
        var fake = new FakeAdapter { ReuseCoordinateAt = 1 };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.CustodyAmbiguous,
            result.DiagnosticCode);
    }

    [Fact]
    public async Task FinalCloseFailureIsUncertain()
    {
        var fake = new FakeAdapter { FinalCloseComplete = false };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.CustodyAmbiguous,
            result.DiagnosticCode);
    }

    [Fact]
    public async Task CleanupBudgetExpirationIsStickyEvenWhenProviderIgnoresCancellation()
    {
        var fake = new FakeAdapter
        {
            FinalCloseDelay = TimeSpan.FromMilliseconds(75),
            IgnoreFinalCloseCancellation = true,
        };
        var model = new FixtureB1AdapterCustodyModel(
            fake, fake, fake, TimeProvider.System,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(20));

        var result = await model.ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.CustodyAmbiguous,
            result.DiagnosticCode);
        Assert.True(fake.FinalCloseCalled);
    }

    [Fact]
    public async Task SharedCleanupContextExpirationIsStickyAcrossAdapterModels()
    {
        var clock = new ManualTimeProvider();
        using var cleanup = new FixtureB1CleanupContext(
            clock, QualificationConstants.CleanupTimeout);
        clock.Advance(QualificationConstants.CleanupTimeout);
        Assert.False(cleanup.IsExpired);
        var firstFake = new FakeAdapter
        {
            DuringFinalClose = () =>
                clock.Advance(QualificationConstants.CleanupTimeout),
        };
        var first = new FixtureB1AdapterCustodyModel(
            firstFake,
            firstFake,
            firstFake,
            clock,
            cleanup,
            TimeSpan.FromSeconds(2));

        var firstResult = await first.ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, firstResult.TerminalState);
        Assert.True(cleanup.IsExpired);
        Assert.True(cleanup.Token.IsCancellationRequested);

        var secondFake = new FakeAdapter();
        var second = new FixtureB1AdapterCustodyModel(
            secondFake,
            secondFake,
            secondFake,
            clock,
            cleanup,
            TimeSpan.FromSeconds(2));

        var secondResult = await second.ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, secondResult.TerminalState);
        Assert.True(cleanup.IsExpired);
        Assert.True(cleanup.Token.IsCancellationRequested);
    }

    [Fact]
    public async Task CleanupExpirationIsVisibleBeforeRegistrationCallbackCompletes()
    {
        var clock = new ManualTimeProvider();
        using var cleanup = new FixtureB1CleanupContext(clock, TimeSpan.FromSeconds(1));
        var token = cleanup.Token;
        var callbackEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(() =>
        {
            callbackEntered.TrySetResult();
            releaseCallback.Task.GetAwaiter().GetResult();
        });
        var advancing = Task.Run(() => clock.Advance(TimeSpan.FromSeconds(1)));
        try
        {
            await callbackEntered.Task.WaitAsync(TestTimeouts.Signal);
            Assert.True(token.IsCancellationRequested);
            Assert.True(cleanup.IsExpired);
        }
        finally
        {
            releaseCallback.TrySetResult();
            await advancing.WaitAsync(TestTimeouts.Signal);
        }
    }

    [Fact]
    public void LedgerWithoutInspectionsRegistersPrepareThenSevenCoordinatesInOrder()
    {
        var plan = FixtureArtifactHandoffPlan.Create(
            FixtureHandoffEdge.LauncherToRunner, null, RunNonce, LaunchId);
        var ledger = new FixtureB1AdapterCustodyModel.CustodyLedger(
            plan.Slots.Count, includeInspections: false);
        var custody = new FixtureB1AdapterLaunchCustody(
            LaunchId, Target, 10, 20, 40, 30);
        var prepare = ledger.Register(FixtureB1AdapterOperationKind.PrepareTarget, 0);
        Assert.Equal(0, prepare.OperationId);
        Assert.Equal(FixtureB1AdoptionDisposition.Adopted,
            prepare.TryAdoptTarget(custody));
        prepare.Complete();
        ledger.MarkMissingAdoption(prepare);

        for (var index = 0; index < plan.Slots.Count; index++)
        {
            var duplicate = ledger.Register(
                FixtureB1AdapterOperationKind.DuplicateCoordinate, index);
            Assert.Equal(index + 1, duplicate.OperationId);
            Assert.Equal(FixtureB1AdapterOperationKind.DuplicateCoordinate,
                duplicate.Kind);
            Assert.Equal(FixtureB1AdoptionDisposition.Adopted,
                duplicate.TryAdoptCoordinate(new FixtureB1AdapterTargetCoordinate(
                    LaunchId, Target, plan.Slots[index].Label, 100 + index)));
            duplicate.Complete();
            ledger.MarkMissingAdoption(duplicate);
        }

        var terminal = ledger.Register(FixtureB1AdapterOperationKind.AwaitTerminal, 0);
        Assert.Equal(plan.Slots.Count + 1, terminal.OperationId);
        terminal.Complete();
        ledger.MarkMissingAdoption(terminal);
        ledger.Stop();

        Assert.Equal(custody, ledger.Target);
        Assert.Equal(plan.Slots.Count, ledger.Coordinates.Count);
        Assert.Empty(ledger.Inspections);
        Assert.True(ledger.MutationStarted);
        Assert.False(ledger.Ambiguous);
        Assert.Throws<InvalidOperationException>(() => ledger.Register(
            FixtureB1AdapterOperationKind.AcquireInspection, 0));
    }

    [Fact]
    public async Task MutationGetterFailureAndSecondAdoptionFailClosed()
    {
        var fake = new FakeAdapter
        {
            ThrowMutationGetter = true,
            AdoptSecondCoordinateAt = 0,
        };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.CustodyAmbiguous,
            result.DiagnosticCode);
        Assert.True(result.MutationStarted);
        Assert.True(fake.RejectedAdoptionClosed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MalformedCoordinateNeverUsesRetainedTargetTerminalEvidence(
        bool foreignTarget)
    {
        var fake = new FakeAdapter
        {
            WrongTargetAt = foreignTarget ? 0 : null,
            WrongLaunchAt = foreignTarget ? null : 0,
        };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.Equal(0, result.ConfirmedCoordinateCloseCount);
        Assert.Equal(0, fake.TransferCloseCalls);
    }

    [Fact]
    public async Task LateAdoptionDuringFinalCloseDowngradesResult()
    {
        var fake = new FakeAdapter { LateAdoptionDuringFinalClose = true };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.CustodyAmbiguous,
            result.DiagnosticCode);
    }

    [Fact]
    public async Task ArtifactAdmissionLostDuringFinalCloseDowngradesResult()
    {
        var fake = new FakeAdapter { LoseAdmissionDuringFinalClose = true };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
    }

    [Fact]
    public async Task CancellationBetweenOperationsStillAbortsBeforeLocalClose()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeAdapter
        {
            DuringCustodyValidation = cancellation.Cancel,
            RequireDrainBeforeClose = true,
        };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, cancellation.Token);

        Assert.Equal(FixtureB1AdapterTerminalState.Rejected, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.CallerCanceled,
            result.DiagnosticCode);
        Assert.True(fake.AbortCalls > 0);
        Assert.True(fake.FinalCloseCalled);
    }

    [Fact]
    public async Task CancellationDuringFinalCloseCannotConfirm()
    {
        using var cancellation = new CancellationTokenSource();
        var fake = new FakeAdapter { DuringFinalClose = cancellation.Cancel };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, cancellation.Token);

        Assert.Equal(FixtureB1AdapterTerminalState.Rejected, result.TerminalState);
        Assert.Equal(FixtureB1AdapterDiagnosticCode.CallerCanceled,
            result.DiagnosticCode);
    }

    [Fact]
    public async Task MutationLatchesBeforeProviderFaultWithoutAdoption()
    {
        var fake = new FakeAdapter { ThrowBeforePrepareAdoption = true };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.True(result.MutationStarted);
        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
    }

    [Fact]
    public async Task NormalCleanupDrainsBeforeClosingDependentLeases()
    {
        var fake = new FakeAdapter { RequireDrainBeforeClose = true };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Confirmed, result.TerminalState);
    }

    [Fact]
    public async Task FailedDrainWithLiveObserverSkipsDependentCloses()
    {
        var fake = new FakeAdapter { FailFinalDrain = true };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.True(fake.ObserverStillActive);
        Assert.Null(result.FinalClose);
        Assert.Equal(0, fake.TransferCloseCalls);
        Assert.Equal(0, fake.IssuerCloseCalls);
        Assert.False(fake.ReaperReleasedAtFinalClose);
    }

    [Fact]
    public async Task SameResourceReadoptionRemainsLedgerOwned()
    {
        var fake = new FakeAdapter { ReadoptSameCoordinateAt = 0 };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.Equal(7, fake.TransferCloseCalls);
        Assert.True(fake.RejectedAdoptionClosed);
    }

    [Fact]
    public async Task LocalInspectionLeaseWithChangedMetadataClosesOnce()
    {
        var fake = new FakeAdapter { ReuseInspectionWithWrongMetadataAt = 1 };

        var result = await Model(fake).ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        Assert.Equal(1, result.AdoptedInspectionCount);
        Assert.Equal(1, fake.InspectionCloseCalls);
    }

    [Fact]
    public async Task RetainedOutputRevalidationAndOneUseAreFailClosed()
    {
        var fake = new FakeAdapter { RetainedOutputsAdmitted = false };
        var model = Model(fake);

        var result = await model.ExecuteAsync(
            RunNonce, LaunchId, CancellationToken.None);

        Assert.Equal(FixtureB1AdapterTerminalState.Uncertain, result.TerminalState);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            model.ExecuteAsync(RunNonce, LaunchId, CancellationToken.None));
    }

    private static FixtureB1AdapterCustodyModel Model(
        FakeAdapter fake,
        TimeSpan? readiness = null) => new(
            fake,
            fake,
            fake,
            TimeProvider.System,
            readiness ?? QualificationConstants.ReadinessTimeout);

    private sealed class FakeAdapter :
        IFixtureB1AdapterCustodyProvider,
        IFixtureB1AdapterObservationProvider,
        IFixtureB1AdapterAbortProvider
    {
        private readonly FixtureArtifactHandoffPlan _plan =
            FixtureArtifactHandoffPlan.Create(
                FixtureHandoffEdge.LauncherToRunner, null, RunNonce, LaunchId);
        private readonly FixtureB1AdapterLaunchCustody _custody = new(
            LaunchId, Target, 10, 20, 40, 30);
        private readonly HashSet<long> _closedCoordinates = [];
        private readonly HashSet<long> _closedInspections = [];
        private readonly List<FixtureB1AdapterTargetCoordinate> _rejectedCoordinates = [];
        private bool _mutationStarted;
        private bool _issuerClosed;
        private bool _reaperRetained = true;
        private IFixtureB1AdapterAdoptionSink? _firstCoordinateSink;

        internal int? ThrowDuplicateWithoutAdoptionAt { get; set; }
        internal int? ThrowInspectionValidationAt { get; set; }
        internal int? ReuseCoordinateAt { get; set; }
        internal int? AdoptSecondCoordinateAt { get; set; }
        internal int? ReadoptSameCoordinateAt { get; set; }
        internal int? ReuseInspectionWithWrongMetadataAt { get; set; }
        internal int? WrongTargetAt { get; set; }
        internal int? WrongLaunchAt { get; set; }
        internal bool LateAdoptionDuringFinalClose { get; set; }
        internal bool LoseAdmissionDuringFinalClose { get; set; }
        internal bool RejectedAdoptionClosed { get; private set; } = true;
        internal bool ReaperReleasedAtFinalClose { get; private set; }
        internal bool FailFinalDrain { get; set; }
        internal bool ObserverStillActive { get; private set; }
        internal bool ThrowBeforePrepareAdoption { get; set; }
        internal bool RequireDrainBeforeClose { get; set; }
        internal Action? AfterPrepare { get; set; }
        internal Action? DuringCustodyValidation { get; set; }
        internal Action? DuringFinalClose { get; set; }
        internal bool BlockFirstDuplicate { get; set; }
        internal bool TargetSignalOnTerminal { get; set; } = true;
        internal bool JobEmptyOnTerminal { get; set; } = true;
        internal bool ConsumeReaperOnIssuerClose { get; set; }
        internal bool FinalCloseComplete { get; set; } = true;
        internal bool ThrowMutationGetter { get; set; }
        internal bool RetainedOutputsAdmitted { get; set; } = true;
        internal bool IgnoreFinalCloseCancellation { get; set; }
        internal TimeSpan FinalCloseDelay { get; set; }
        internal bool TargetSignaled { get; private set; }
        internal bool JobEmpty { get; private set; }
        internal bool OriginalSettled { get; private set; }
        internal bool DrainObservedBeforeOriginalSettled { get; private set; }
        internal int DrainCalls { get; private set; }
        internal int AbortCalls { get; private set; }
        internal int TransferCloseCalls { get; private set; }
        internal int InspectionCloseCalls { get; private set; }
        internal int IssuerCloseCalls { get; private set; }
        internal int FinalCloseCalls { get; private set; }
        internal bool FinalCloseCalled => FinalCloseCalls != 0;
        internal bool ReaperObservedAfterIssuerClose { get; private set; }
        internal IReadOnlyCollection<long> ClosedInspections => _closedInspections;
        internal List<(bool Signaled, bool JobEmpty)> TransferCloseTerminalStates { get; } = [];
        internal TaskCompletionSource DuplicateEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AbortEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DuplicateRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool MutationStarted
        {
            get
            {
                if (ThrowMutationGetter)
                    throw new InvalidOperationException("sensitive provider failure");
                return _mutationStarted;
            }
        }

        public ValueTask PrepareTargetAsync(
            Guid runNonce,
            Guid launchId,
            IFixtureB1AdapterAdoptionSink adoption,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowBeforePrepareAdoption)
                throw new IOException("prepare mutated then lost custody");
            _mutationStarted = true;
            Assert.Equal(FixtureB1AdoptionDisposition.Adopted,
                adoption.TryAdoptTarget(_custody));
            AfterPrepare?.Invoke();
            return ValueTask.CompletedTask;
        }

        public async ValueTask DuplicateTargetCoordinateAsync(
            FixtureHandoffSlot slot,
            int slotIndex,
            IFixtureB1AdapterAdoptionSink adoption,
            CancellationToken cancellationToken)
        {
            _mutationStarted = true;
            if (ThrowDuplicateWithoutAdoptionAt == slotIndex)
                throw new IOException("duplicate side effect lost its coordinate");

            var value = ReuseCoordinateAt == slotIndex ? 100 : 100 + slotIndex;
            var coordinate = new FixtureB1AdapterTargetCoordinate(
                WrongLaunchAt == slotIndex ? Guid.NewGuid() : LaunchId,
                WrongTargetAt == slotIndex ? new FixtureHandoffProcessIdentity(713, 7_130) : Target,
                slot.Label, value);
            if (BlockFirstDuplicate && slotIndex == 0)
            {
                DuplicateEntered.TrySetResult();
                await DuplicateRelease.Task.ConfigureAwait(false);
                Assert.Equal(FixtureB1AdoptionDisposition.Adopted,
                    adoption.TryAdoptCoordinate(coordinate));
                OriginalSettled = true;
                return;
            }

            Assert.Equal(FixtureB1AdoptionDisposition.Adopted,
                adoption.TryAdoptCoordinate(coordinate));
            if (slotIndex == 0)
                _firstCoordinateSink = adoption;
            if (AdoptSecondCoordinateAt == slotIndex)
            {
                var rejected = coordinate with
                {
                    Coordinate = value + 10_000,
                };
                Assert.Equal(FixtureB1AdoptionDisposition.ProviderRetainsNewResource,
                    adoption.TryAdoptCoordinate(rejected));
                _rejectedCoordinates.Add(rejected);
            }
            if (ReadoptSameCoordinateAt == slotIndex)
                Assert.Equal(FixtureB1AdoptionDisposition.AlreadyOwned,
                    adoption.TryAdoptCoordinate(coordinate));
        }

        public ValueTask AcquireInspectionAsync(
            FixtureB1AdapterTargetCoordinate coordinate,
            IFixtureB1AdapterAdoptionSink adoption,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _mutationStarted = true;
            var index = Array.FindIndex(_plan.Slots.ToArray(),
                slot => slot.Label == coordinate.Label);
            var reused = ReuseInspectionWithWrongMetadataAt == index;
            var inspection = reused
                ? new FixtureB1AdapterInspectionLease(Guid.NewGuid(),
                    new FixtureHandoffProcessIdentity(713, 7_130), 200)
                : new FixtureB1AdapterInspectionLease(LaunchId, Target, 200 + index);
            Assert.Equal(reused
                    ? FixtureB1AdoptionDisposition.AlreadyOwned
                    : FixtureB1AdoptionDisposition.Adopted,
                adoption.TryAdoptInspection(inspection));
            return ValueTask.CompletedTask;
        }

        public ValueTask AwaitTerminalAsync(
            FixtureB1AdapterLaunchCustody custody,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TargetSignaled = TargetSignalOnTerminal;
            JobEmpty = JobEmptyOnTerminal;
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseInspectionAsync(
            FixtureB1AdapterInspectionLease inspection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RequireDrainBeforeClose)
                Assert.True(DrainCalls > 0);
            InspectionCloseCalls++;
            _closedInspections.Add(inspection.Lease);
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseTargetCoordinateAsync(
            FixtureB1AdapterTargetCoordinate coordinate,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RequireDrainBeforeClose)
                Assert.True(DrainCalls > 0);
            TransferCloseCalls++;
            TransferCloseTerminalStates.Add((TargetSignaled, JobEmpty));
            _closedCoordinates.Add(coordinate.Coordinate);
            return ValueTask.CompletedTask;
        }

        public ValueTask CloseIssuerSourceAsync(
            FixtureB1AdapterLaunchCustody custody,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (RequireDrainBeforeClose)
                Assert.True(DrainCalls > 0);
            IssuerCloseCalls++;
            _issuerClosed = true;
            if (ConsumeReaperOnIssuerClose)
                _reaperRetained = false;
            return ValueTask.CompletedTask;
        }

        public async ValueTask<FixtureB1AdapterFinalCloseReport> CloseLocalAsync(
            FixtureB1AdapterLaunchCustody? custody,
            IReadOnlyList<FixtureB1AdapterInspectionLease> inspections,
            bool mayReleaseReaper,
            CancellationToken cancellationToken)
        {
            FinalCloseCalls++;
            if (RequireDrainBeforeClose)
                Assert.True(DrainCalls > 0);
            DuringFinalClose?.Invoke();
            if (FinalCloseDelay > TimeSpan.Zero)
                await Task.Delay(FinalCloseDelay,
                    IgnoreFinalCloseCancellation ? CancellationToken.None : cancellationToken);
            if (LateAdoptionDuringFinalClose)
            {
                var rejected = new FixtureB1AdapterTargetCoordinate(
                    LaunchId, Target, "late", 99_999);
                Assert.Equal(FixtureB1AdoptionDisposition.ProviderRetainsNewResource,
                    _firstCoordinateSink!.TryAdoptCoordinate(rejected));
                _rejectedCoordinates.Add(rejected);
            }
            if (LoseAdmissionDuringFinalClose)
                RetainedOutputsAdmitted = false;
            ReaperReleasedAtFinalClose = mayReleaseReaper && _reaperRetained;
            if (mayReleaseReaper && TargetSignaled && JobEmpty)
                foreach (var rejected in _rejectedCoordinates)
                    _closedCoordinates.Add(rejected.Coordinate);
            RejectedAdoptionClosed = _rejectedCoordinates.All(coordinate =>
                _closedCoordinates.Contains(coordinate.Coordinate));
            var complete = FinalCloseComplete;
            return new(
                complete,
                complete,
                complete,
                complete && _issuerClosed,
                complete && ReaperReleasedAtFinalClose,
                complete && inspections.All(inspection =>
                    _closedInspections.Contains(inspection.Lease)),
                complete && DrainCalls > 0,
                complete,
                complete,
                complete,
                complete,
                complete,
                complete && RejectedAdoptionClosed,
                complete);
        }

        public bool IsLaunchCustodyValid(FixtureB1AdapterLaunchCustody custody)
        {
            DuringCustodyValidation?.Invoke();
            return custody == _custody;
        }

        public bool IsTargetCoordinateValid(
            FixtureHandoffSlot expectedSlot,
            FixtureB1AdapterLaunchCustody custody,
            FixtureB1AdapterTargetCoordinate coordinate) =>
            custody == _custody && coordinate.LaunchId == LaunchId &&
            coordinate.ExactTarget == Target && coordinate.Label == expectedSlot.Label;

        public bool IsInspectionValid(
            FixtureB1AdapterTargetCoordinate coordinate,
            FixtureB1AdapterInspectionLease inspection)
        {
            var index = checked((int)(inspection.Lease - 200));
            if (ThrowInspectionValidationAt == index)
                throw new InvalidOperationException("inspection validation failed");
            return inspection.LaunchId == coordinate.LaunchId &&
                inspection.ExactTarget == coordinate.ExactTarget;
        }

        public bool IsExactTargetSignaled(FixtureB1AdapterLaunchCustody custody) =>
            TargetSignaled;
        public bool IsOuterJobEmpty(FixtureB1AdapterLaunchCustody custody) => JobEmpty;
        public bool IsTargetCoordinateClosed(FixtureB1AdapterTargetCoordinate coordinate) =>
            _closedCoordinates.Contains(coordinate.Coordinate);
        public bool IsInspectionClosed(FixtureB1AdapterInspectionLease inspection) =>
            _closedInspections.Contains(inspection.Lease);
        public bool IsIssuerSourceClosed(FixtureB1AdapterLaunchCustody custody) =>
            _issuerClosed;
        public bool IsReapingLeaseRetained(FixtureB1AdapterLaunchCustody custody)
        {
            ReaperObservedAfterIssuerClose = _issuerClosed && _reaperRetained;
            return _reaperRetained;
        }
        public bool AreRetainedRootAndOutputsAdmitted(
            FixtureB1AdapterLaunchCustody custody) => RetainedOutputsAdmitted;

        public ValueTask AbortAsync(CancellationToken cancellationToken)
        {
            AbortCalls++;
            AbortEntered.TrySetResult();
            TargetSignaled = TargetSignalOnTerminal;
            JobEmpty = JobEmptyOnTerminal;
            return ValueTask.CompletedTask;
        }

        public ValueTask DrainAsync(CancellationToken cancellationToken)
        {
            DrainCalls++;
            DrainObservedBeforeOriginalSettled |= BlockFirstDuplicate && !OriginalSettled;
            if (FailFinalDrain && AbortCalls == 0)
            {
                ObserverStillActive = true;
                throw new IOException("observer still active");
            }
            return ValueTask.CompletedTask;
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
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
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
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state) : ITimer
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
