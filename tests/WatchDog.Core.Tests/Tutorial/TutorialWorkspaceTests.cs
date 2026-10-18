using WatchDog.Core.Capture;
using WatchDog.Core.Settings;
using WatchDog.Core.Tutorial;

namespace WatchDog.Core.Tests.Tutorial;

public sealed class TutorialWorkspaceTests
{
    [Fact]
    public async Task PlanUsesExistingOwnerAndUnsupportedControlsCannotPretendToPause()
    {
        var backend = new FakeBackend();
        await using var owner = new CaptureLifecycleCoordinator(backend);
        var plan = Plan();
        Assert.True((await owner.StartAsync(plan.Request)).Succeeded);
        var before = owner.Snapshot;
        var projection = TutorialWorkspaceProjection.Create(plan, before);
        Assert.True(projection.OwnsObservedContext);
        Assert.True(projection.CanRequestStop);
        Assert.Contains("Recording", projection.StatusText);
        Assert.False(projection.Pause.Available);
        Assert.False(projection.Resume.Available);
        Assert.False(projection.Split.Available);
        Assert.False(projection.Preview.Available);
        Assert.False(projection.Countdown.Available);
        Assert.False(projection.Start.Available);
        foreach (var result in new[] { await owner.PauseAsync(), await owner.ResumeAsync(), await owner.SplitAsync() })
        {
            Assert.Equal(CaptureCommandDisposition.Unsupported, result.Disposition);
            Assert.Same(before, result.After);
        }
        Assert.Equal(0, backend.StopCount);
    }

    [Fact]
    public async Task ManualPriorityStaleLossAndExactLossPreserveBoundariesAndReservation()
    {
        var backend = new FakeBackend();
        await using var owner = new CaptureLifecycleCoordinator(backend);
        var plan = Plan();
        await owner.StartAsync(plan.Request);
        var context = owner.Snapshot.Context!;
        var automatic = new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch, CaptureInitiation.Automatic,
            new GameCaptureTarget("game", 22, "game.exe", DateTimeOffset.UnixEpoch));
        Assert.Equal(CaptureFailureKind.ManualPriority, (await owner.SwitchAsync(automatic)).Failure!.Kind);
        var stale = new CaptureSourceLoss(context.SessionId, context.Generation - 1, context.ActualTarget, DateTimeOffset.UnixEpoch);
        Assert.Equal(CaptureCommandDisposition.Ignored, (await owner.HandleSourceLostAsync(stale)).Disposition);
        Assert.Equal(0, backend.StopCount);
        Assert.True((await owner.HandleSourceLostAsync(stale with { Generation = context.Generation })).Succeeded);
        var projection = TutorialWorkspaceProjection.Create(plan, owner.Snapshot);
        Assert.False(projection.OwnsObservedContext);
        Assert.False(projection.CanRequestStop);
        Assert.Contains("ownership", projection.StatusText);
        Assert.False(TutorialWorkspaceProjection.Create(Plan(), owner.Snapshot).CanRequestStop);
        Assert.Equal(CaptureFailureKind.ManualPriority, (await owner.StartAsync(automatic)).Failure!.Kind);
        Assert.True((await owner.StopAsync(CaptureInitiation.Manual)).Succeeded);
        Assert.Single(backend.Starts);
        Assert.Same(plan.Request.Target, backend.Starts[0].ActualTarget);
    }

    [Fact]
    public async Task FailedCleanupRetainsStopControlAndTruthfulFailure()
    {
        var backend = new FakeBackend { FailStop = true };
        await using var owner = new CaptureLifecycleCoordinator(backend);
        var plan = Plan();
        await owner.StartAsync(plan.Request);
        await owner.StopAsync(CaptureInitiation.Manual);
        var projection = TutorialWorkspaceProjection.Create(plan, owner.Snapshot);
        Assert.True(projection.OwnsObservedContext);
        Assert.True(projection.CanRequestStop);
        Assert.Contains("cleanup failed", projection.StatusText);
        backend.FailStop = false;
    }

    [Fact]
    public void UnrelatedAutomaticCaptureAndWidenedTargetNeverShowTutorialRecording()
    {
        var plan = Plan();
        var context = new CaptureSessionContext(Guid.NewGuid(), 1, plan.Request, plan.Request.Target, new DisplayCaptureTarget("display"));
        var active = new CaptureLifecycleSnapshot(CaptureLifecycleStatus.Active, CaptureReadiness.Available, CaptureHealth.Healthy, context, null);
        Assert.False(TutorialWorkspaceProjection.Create(plan, active).OwnsObservedContext);
        Assert.False(TutorialWorkspaceProjection.Create(plan, active).CanRequestStop);
        Assert.Contains("Another", TutorialWorkspaceProjection.Create(plan, active).StatusText);
        Assert.Contains("Window", TutorialWorkspaceProjection.Create(plan, active).BoundaryText);
        Assert.False(TutorialWorkspaceProjection.Create(plan, active with { Readiness = CaptureReadiness.Closed }).CanRequestStop);
    }

    [Fact]
    public void BusyAndShutdownNeverOfferNewStartOrStop()
    {
        var plan = Plan();
        var context = new CaptureSessionContext(Guid.NewGuid(), 1, plan.Request, plan.Request.Target, plan.Request.Target);
        foreach (var status in new[] { CaptureLifecycleStatus.Starting, CaptureLifecycleStatus.Saving, CaptureLifecycleStatus.Stopping, CaptureLifecycleStatus.Shutdown })
        {
            var snapshot = new CaptureLifecycleSnapshot(status, CaptureReadiness.Busy, CaptureHealth.Healthy, context, null);
            var projection = TutorialWorkspaceProjection.Create(plan, snapshot);
            Assert.False(projection.CanRequestStop);
            Assert.False(projection.Start.Available);
        }
    }

    [Fact]
    public void UnknownStartingContextNeverAttributesAdmissionToSelectedTutorial()
    {
        var projection = TutorialWorkspaceProjection.Create(Plan(), CaptureLifecycleSnapshot.Initial with
        {
            Status = CaptureLifecycleStatus.Starting,
            Readiness = CaptureReadiness.Busy,
        });
        Assert.False(projection.OwnsObservedContext);
        Assert.False(projection.CanRequestStop);
        Assert.Contains("ownership is unknown", projection.StatusText);
        Assert.DoesNotContain("selected recording", projection.StatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedStartShowsFailureAndStopOnlyForExactRetainedOwnership(bool retained)
    {
        var backend = new FakeBackend { FailStart = true, RetainFailedStart = retained };
        await using var owner = new CaptureLifecycleCoordinator(backend);
        var plan = Plan();
        Assert.False((await owner.StartAsync(plan.Request)).Succeeded);
        var projection = TutorialWorkspaceProjection.Create(plan, owner.Snapshot);
        Assert.Contains("start failed", projection.StatusText);
        Assert.DoesNotContain("Recording the selected", projection.StatusText);
        Assert.Equal(retained, projection.OwnsObservedContext);
        Assert.Equal(retained, projection.CanRequestStop);
        Assert.False(TutorialWorkspaceProjection.Create(Plan(), owner.Snapshot).CanRequestStop);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ChangedIdentityMetadataCannotClaimSelectedContext(int field)
    {
        var plan = Plan();
        var window = (WindowCaptureTarget)plan.Request.Target;
        CaptureTarget changed = field switch
        {
            0 => window with { ExecutableName = "other.exe" },
            1 => window with { WindowTitle = "Another window" },
            2 => window with { WindowClass = "AnotherClass" },
            _ => window with { WindowHandle = new IntPtr(999) },
        };
        var context = new CaptureSessionContext(Guid.NewGuid(), 1, plan.Request, window, changed);
        var snapshot = new CaptureLifecycleSnapshot(CaptureLifecycleStatus.Active, CaptureReadiness.Available, CaptureHealth.Healthy, context, null);
        Assert.False(TutorialWorkspaceProjection.Create(plan, snapshot).OwnsObservedContext);
        Assert.False(TutorialWorkspaceProjection.Create(plan, snapshot).CanRequestStop);
    }

    internal static TutorialRecordingPlan Plan() => TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, TutorialRecordingPolicyTests.Target(0), CaptureStartupBaselineResult.Create(new AppSettings())).Plan!;

    private sealed class FakeBackend : ICaptureLifecycleBackend
    {
        public List<CaptureSessionContext> Starts { get; } = [];
        public int StopCount { get; private set; }
        public bool FailStop { get; set; }
        public bool FailStart { get; init; }
        public bool RetainFailedStart { get; init; }
        public Task<CaptureBackendStartResult> StartAsync(CaptureSessionContext context, CancellationToken cancellationToken)
        {
            Starts.Add(context);
            if (FailStart)
            {
                var failure = new CaptureFailure(CaptureFailureKind.Backend, "fake.start", "start failed");
                return Task.FromResult(RetainFailedStart
                    ? CaptureBackendStartResult.Failed(failure, context.RequestedTarget)
                    : CaptureBackendStartResult.Failed(failure));
            }
            return Task.FromResult(CaptureBackendStartResult.Success(context.RequestedTarget));
        }
        public Task<CaptureBackendCommandResult> StopAsync(CaptureSessionContext context, CancellationToken cancellationToken)
        {
            StopCount++;
            return Task.FromResult(FailStop ? CaptureBackendCommandResult.Failed(new(CaptureFailureKind.Backend, "fake.stop", "cleanup failed")) : CaptureBackendCommandResult.Success());
        }
        public Task<CaptureBackendSaveResult> SaveAsync(CaptureSessionContext context, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
