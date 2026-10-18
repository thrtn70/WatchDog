using WatchDog.Core.Capture;
using WatchDog.Core.Devices;
using WatchDog.Core.Settings;
using WatchDog.Core.Storage;

namespace WatchDog.Core.Tests.Capture;

public sealed class CaptureLifecycleCoordinatorTests
{
    [Fact]
    public void CaptureFailureCompositionIsBoundedAndGivesExactUncertaintyPrecedence()
    {
        var primary = new CaptureFailure(
            CaptureFailureKind.SourceLost,
            "capture.source-lost",
            "The source was lost.",
            IsRetryable: true);
        var firstCleanup = new CaptureFailure(
            CaptureFailureKind.Backend,
            "capture.cleanup.first",
            "First cleanup detail.");
        var uncertainty = new CaptureFailure(
            CaptureFailureKind.Backend,
            CaptureFailure.WebcamViewReleaseUncertainCode,
            "Webcam cleanup could not be confirmed.",
            IsRetryable: true);
        var incoming = new CaptureFailure(
                CaptureFailureKind.Cancelled,
                "capture.cleanup.cancelled",
                "Later cleanup was cancelled.")
            .WithCleanupFailure(uncertainty);

        var ordinary = primary.WithCleanupFailure(firstCleanup);
        var composed = ordinary.WithCleanupFailure(incoming);
        var repeated = composed.WithCleanupFailure(
            new CaptureFailure(
                CaptureFailureKind.Backend,
                CaptureFailure.WebcamViewReleaseUncertainCode,
                "A later uncertainty detail."));

        Assert.Same(primary, primary.WithCleanupFailure(null));
        Assert.Equal(primary.Kind, composed.Kind);
        Assert.Equal(primary.Code, composed.Code);
        Assert.Equal(primary.Message, composed.Message);
        Assert.False(composed.IsRetryable);
        Assert.True(composed.HasUncertainWebcamViewRelease);
        Assert.Same(uncertainty, composed.CleanupFailure);
        Assert.Null(composed.CleanupFailure?.CleanupFailure);
        Assert.Same(composed.CleanupFailure, repeated.CleanupFailure);
        Assert.Same(composed, composed.WithCleanupFailure(firstCleanup));
        Assert.Same(composed, composed.WithCleanupFailure(null));
        Assert.Same(composed, composed.WithCleanupFailure(new CaptureFailure(
            CaptureFailureKind.Cancelled, "capture.cleanup.cancelled", "Cancelled observation.")));
        Assert.Same(ordinary, ordinary.WithCleanupFailure(new CaptureFailure(
            CaptureFailureKind.Backend, "capture.cleanup.later", "Later detail.")));
    }

    [Fact]
    public void DirectUncertaintyRemainsRecognizedWithAnOrdinaryCompanion()
    {
        var uncertainty = new CaptureFailure(
            CaptureFailureKind.Backend,
            CaptureFailure.WebcamViewReleaseUncertainCode,
            "Webcam cleanup could not be confirmed.",
            IsRetryable: true);
        var ordinary = new CaptureFailure(
            CaptureFailureKind.Backend,
            "capture.cleanup.ordinary",
            "Ordinary cleanup detail.");

        var composed = uncertainty.WithCleanupFailure(ordinary);

        Assert.True(composed.HasUncertainWebcamViewRelease);
        Assert.False(composed.IsRetryable);
        Assert.Same(ordinary, composed.CleanupFailure);

        var primary = new CaptureFailure(CaptureFailureKind.SourceLost, "source.lost", "Source lost.", true);
        var attached = primary.WithCleanupFailure(composed);
        Assert.Equal(primary.Kind, attached.Kind);
        Assert.Equal(primary.Code, attached.Code);
        Assert.Equal(primary.Message, attached.Message);
        Assert.False(attached.IsRetryable);
        Assert.True(CaptureFailure.IsUncertainWebcamViewReleaseNode(attached.CleanupFailure));
        Assert.Null(attached.CleanupFailure?.CleanupFailure);
        Assert.Same(ordinary, composed.CleanupFailure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplacementPreflightFailurePreservesActiveOwnerAndReleasesManualReservation(bool throws)
    {
        var reject = false;
        var backend = new FakeBackend
        {
            ValidateHandler = _ => !reject ? null : throws
                ? throw new InvalidOperationException("preflight failure")
                : new(CaptureFailureKind.Unsupported, "fixture.policy.unsupported", "Cannot apply policy"),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)))).Succeeded);
        var active = coordinator.Snapshot;
        reject = true;
        var rejected = await coordinator.SwitchAsync(Request(CaptureInitiation.Manual, Window(20, 0x2000)));
        Assert.False(rejected.Succeeded);
        Assert.Same(active, coordinator.Snapshot);
        Assert.Empty(backend.StopCalls);
        Assert.Single(backend.StartCalls);
        reject = false;
        Assert.True((await coordinator.SwitchAsync(Request(CaptureInitiation.Automatic, Game(30)))).Succeeded);
        Assert.Single(backend.StopCalls);
    }

    [Fact]
    public async Task AutomaticPreflightCannotBypassManualPriority()
    {
        var validations = 0;
        var backend = new FakeBackend { ValidateHandler = _ => { validations++; return null; } };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(10, 0x1000)))).Succeeded);
        Assert.False((await coordinator.SwitchAsync(Request(CaptureInitiation.Automatic, Game(20)))).Succeeded);
        Assert.Equal(1, validations);
        Assert.Empty(backend.StopCalls);
    }
    [Fact]
    public async Task ManualStart_ReservesPriorityAheadOfQueuedAutomaticStart()
    {
        var firstStartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            StartHandler = async (context, _) =>
            {
                if (context.Generation == 1)
                {
                    firstStartEntered.SetResult();
                    await releaseFirstStart.Task;
                }

                return CaptureBackendStartResult.Success(context.RequestedTarget);
            },
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);

        var occupyingStart = coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        await firstStartEntered.Task;

        var queuedAutomatic = coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(20)));
        var manualTarget = Window(30, 0x3000);
        var manualStart = coordinator.StartAsync(Request(CaptureInitiation.Manual, manualTarget));
        releaseFirstStart.SetResult();

        Assert.Equal(CaptureCommandDisposition.Completed, (await occupyingStart).Disposition);
        Assert.Equal(CaptureCommandDisposition.Rejected, (await queuedAutomatic).Disposition);
        Assert.Equal(
            CaptureCommandDisposition.Rejected,
            (await coordinator.StartAsync(Request(CaptureInitiation.ConfiguredStartup, Game(21)))).Disposition);
        Assert.Equal(CaptureCommandDisposition.Completed, (await manualStart).Disposition);
        Assert.Equal(2, backend.StartCalls.Count);
        Assert.Equal(manualTarget, coordinator.Snapshot.Context?.ActualTarget);
    }

    [Fact]
    public async Task GameTargetWithoutProcessStartTime_IsRejectedBeforeBackendAdmission()
    {
        var backend = new FakeBackend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);

        var result = await coordinator.StartAsync(Request(
            CaptureInitiation.Automatic,
            new GameCaptureTarget("game", 42, "game.exe")));

        Assert.Equal(CaptureCommandDisposition.Rejected, result.Disposition);
        Assert.Equal("capture.target.game-invalid", result.Failure?.Code);
        Assert.Empty(backend.StartCalls);
    }

    [Fact]
    public async Task CancelledQueuedManualStart_ReleasesReservationWhenNoManualOwnerExists()
    {
        var firstStartEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            StartHandler = async (context, _) =>
            {
                if (context.Generation == 1)
                {
                    firstStartEntered.SetResult();
                    await releaseFirstStart.Task;
                }

                return CaptureBackendStartResult.Success(context.RequestedTarget);
            },
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);

        var occupyingStart = coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        await firstStartEntered.Task;
        using var cancellation = new CancellationTokenSource();
        var queuedManual = coordinator.StartAsync(
            Request(CaptureInitiation.Manual, Window(20, 0x2000)),
            cancellation.Token);
        cancellation.Cancel();

        var cancelled = await queuedManual;
        releaseFirstStart.SetResult();
        await occupyingStart;
        var laterAutomatic = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(30)));

        Assert.Equal(CaptureCommandDisposition.Failed, cancelled.Disposition);
        Assert.Equal(CaptureFailureKind.Cancelled, cancelled.Failure?.Kind);
        Assert.Equal(CaptureCommandDisposition.Completed, laterAutomatic.Disposition);
        Assert.Equal(2, backend.StartCalls.Count);
    }

    [Fact]
    public async Task CancelledQueuedManualStart_RestoresActiveManualReservation()
    {
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            SaveHandler = async (_, _) =>
            {
                saveEntered.SetResult();
                await releaseSave.Task;
                return CaptureBackendSaveResult.Success("clip.mp4");
            },
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(10, 0x1000)));

        var occupyingSave = coordinator.SaveAsync();
        await saveEntered.Task;
        using var cancellation = new CancellationTokenSource();
        var queuedManual = coordinator.StartAsync(
            Request(CaptureInitiation.Manual, Window(20, 0x2000)),
            cancellation.Token);
        cancellation.Cancel();

        var cancelled = await queuedManual;
        releaseSave.SetResult();
        await occupyingSave;
        var automatic = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(30)));

        Assert.Equal(CaptureCommandDisposition.Failed, cancelled.Disposition);
        Assert.Equal(CaptureFailureKind.Cancelled, cancelled.Failure?.Kind);
        Assert.Equal(CaptureCommandDisposition.Rejected, automatic.Disposition);
        Assert.Equal(CaptureFailureKind.ManualPriority, automatic.Failure?.Kind);
        Assert.Single(backend.StartCalls);
    }

    [Fact]
    public async Task CancellingNewestQueuedManual_DoesNotEraseActiveOwnerOrPromoteSupersededRequest()
    {
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            SaveHandler = async (_, _) =>
            {
                saveEntered.SetResult();
                await releaseSave.Task;
                return CaptureBackendSaveResult.Success("clip.mp4");
            },
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(10, 0x1000)));
        var occupyingSave = coordinator.SaveAsync();
        await saveEntered.Task;

        var olderQueuedManual = coordinator.StartAsync(
            Request(CaptureInitiation.Manual, Window(20, 0x2000)));
        using var cancellation = new CancellationTokenSource();
        var newestQueuedManual = coordinator.StartAsync(
            Request(CaptureInitiation.Manual, Window(30, 0x3000)),
            cancellation.Token);
        cancellation.Cancel();

        var cancelled = await newestQueuedManual;
        releaseSave.SetResult();
        await occupyingSave;
        var superseded = await olderQueuedManual;
        var automatic = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(40)));

        Assert.Equal(CaptureFailureKind.Cancelled, cancelled.Failure?.Kind);
        Assert.Equal(CaptureCommandDisposition.Rejected, superseded.Disposition);
        Assert.Equal(CaptureFailureKind.Superseded, superseded.Failure?.Kind);
        Assert.Equal(CaptureCommandDisposition.Rejected, automatic.Disposition);
        Assert.Equal(CaptureFailureKind.ManualPriority, automatic.Failure?.Kind);
        Assert.Single(backend.StartCalls);
    }

    [Fact]
    public async Task CancellingQueuedManual_DoesNotEraseManualOwnerDuringBackendStart()
    {
        var startEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStart = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            StartHandler = async (context, _) =>
            {
                startEntered.SetResult();
                await releaseStart.Task;
                return CaptureBackendStartResult.Success(context.RequestedTarget);
            },
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        var inFlightManual = coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(10, 0x1000)));
        await startEntered.Task;

        using var cancellation = new CancellationTokenSource();
        var queuedManual = coordinator.StartAsync(
            Request(CaptureInitiation.Manual, Window(20, 0x2000)),
            cancellation.Token);
        cancellation.Cancel();

        var cancelled = await queuedManual;
        releaseStart.SetResult();
        await inFlightManual;
        var automatic = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(30)));

        Assert.Equal(CaptureFailureKind.Cancelled, cancelled.Failure?.Kind);
        Assert.Equal(CaptureCommandDisposition.Rejected, automatic.Disposition);
        Assert.Equal(CaptureFailureKind.ManualPriority, automatic.Failure?.Kind);
        Assert.Single(backend.StartCalls);
    }

    [Fact]
    public async Task FailedStart_DoesNotReportAnActiveSession()
    {
        var failure = new CaptureFailure(CaptureFailureKind.Backend, "start.failed", "Encoder unavailable.");
        var backend = new FakeBackend
        {
            StartHandler = (_, _) => Task.FromResult(CaptureBackendStartResult.Failed(failure)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);

        var result = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));

        Assert.Equal(CaptureCommandDisposition.Failed, result.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Faulted, result.After.Status);
        Assert.Equal(CaptureReadiness.Available, result.After.Readiness);
        Assert.Equal(CaptureHealth.Degraded, result.After.Health);
        Assert.Null(result.After.Context);
        Assert.Equal(failure, result.Failure);
    }

    [Fact]
    public async Task FailedStartWithCleanupRequired_RejectsRestartUntilExplicitStopSucceeds()
    {
        var failure = new CaptureFailure(CaptureFailureKind.Backend, "start.cleanup-failed", "Startup cleanup failed.");
        var ownedWindow = Window(10, 0x1010);
        var backend = new FakeBackend
        {
            StartHandler = (_, _) => Task.FromResult(CaptureBackendStartResult.Failed(failure, ownedWindow)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);

        var failedStart = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var retainedContext = Assert.IsType<CaptureSessionContext>(failedStart.After.Context);
        var rejectedRestart = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(20)));
        var cleanup = await coordinator.StopAsync(CaptureInitiation.Automatic);

        Assert.Equal(CaptureCommandDisposition.Failed, failedStart.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Faulted, failedStart.After.Status);
        Assert.Equal(failure, failedStart.After.LastFailure);
        Assert.Equal(ownedWindow, retainedContext.ActualTarget);
        Assert.Equal(CaptureCommandDisposition.Rejected, rejectedRestart.Disposition);
        Assert.Same(retainedContext, rejectedRestart.After.Context);
        var provisionalContext = Assert.Single(backend.StartCalls);
        Assert.Equal(retainedContext.SessionId, provisionalContext.SessionId);
        Assert.Equal(retainedContext.Generation, provisionalContext.Generation);
        Assert.Equal(retainedContext.RequestedTarget, provisionalContext.ActualTarget);
        Assert.Equal(CaptureCommandDisposition.Completed, cleanup.Disposition);
        Assert.Same(retainedContext, Assert.Single(backend.StopCalls));
        Assert.Equal(CaptureLifecycleStatus.Idle, coordinator.Snapshot.Status);
        Assert.Null(coordinator.Snapshot.Context);
        Assert.Null(coordinator.Snapshot.LastFailure);
    }

    [Fact]
    public async Task FailedStartWithCleanupRequired_FailedExplicitStopRetainsFaultAndContext()
    {
        var startFailure = new CaptureFailure(
            CaptureFailureKind.Backend,
            "start.cleanup-failed",
            "Startup cleanup failed.");
        var stopFailure = new CaptureFailure(
            CaptureFailureKind.Backend,
            "stop.cleanup-failed",
            "Explicit cleanup failed.");
        var backend = new FakeBackend
        {
            StartHandler = (_, _) => Task.FromResult(
                CaptureBackendStartResult.Failed(startFailure, Window(10, 0x1010))),
            StopHandler = (_, _) => Task.FromResult(CaptureBackendCommandResult.Failed(stopFailure)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var retainedContext = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var cleanup = await coordinator.StopAsync(CaptureInitiation.Automatic);

        Assert.Equal(CaptureCommandDisposition.Failed, cleanup.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Faulted, cleanup.After.Status);
        Assert.Same(retainedContext, cleanup.After.Context);
        Assert.Equal(stopFailure, cleanup.After.LastFailure);
        Assert.Same(retainedContext, Assert.Single(backend.StopCalls));
    }

    [Fact]
    public async Task StopFailureAfterCleanup_ReleasesContextButReportsFailure()
    {
        var stopFailure = new CaptureFailure(
            CaptureFailureKind.Backend,
            "stop.media-invalid",
            "Finalized media was missing.");
        var backend = new FakeBackend
        {
            StopHandler = (_, _) => Task.FromResult(
                CaptureBackendCommandResult.Failed(stopFailure, cleanupRequired: false)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(10, 0x1010)));

        var stop = await coordinator.StopAsync(CaptureInitiation.Manual);
        var restart = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(20)));

        Assert.Equal(CaptureCommandDisposition.Failed, stop.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Faulted, stop.After.Status);
        Assert.Equal(CaptureHealth.Degraded, stop.After.Health);
        Assert.Null(stop.After.Context);
        Assert.Equal(stopFailure, stop.After.LastFailure);
        Assert.True(restart.Succeeded);
    }

    [Fact]
    public async Task StaleSourceLoss_IsIgnoredByExactIdentityAndGeneration()
    {
        var backend = new FakeBackend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        var target = Window(10, 0x1000);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual, target));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var result = await coordinator.HandleSourceLostAsync(new CaptureSourceLoss(
            context.SessionId,
            context.Generation - 1,
            target,
            DateTimeOffset.UnixEpoch));

        Assert.Equal(CaptureCommandDisposition.Ignored, result.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Active, coordinator.Snapshot.Status);
        Assert.Empty(backend.StopCalls);

        var wrongIdentity = await coordinator.HandleSourceLostAsync(new CaptureSourceLoss(
            context.SessionId,
            context.Generation,
            Window(10, 0x1001),
            DateTimeOffset.UnixEpoch));

        Assert.Equal(CaptureCommandDisposition.Ignored, wrongIdentity.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Active, coordinator.Snapshot.Status);
        Assert.Empty(backend.StopCalls);
    }

    [Fact]
    public async Task ExactCurrentSourceLoss_StopsButRetainsManualReservation()
    {
        var backend = new FakeBackend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        var target = new RegionCaptureTarget(
            "display-1",
            new PhysicalCaptureBounds(100, 200, 800, 600),
            new PhysicalCaptureBounds(0, 0, 1920, 1080));
        await coordinator.StartAsync(Request(CaptureInitiation.Manual, target));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var loss = await coordinator.HandleSourceLostAsync(new CaptureSourceLoss(
            context.SessionId,
            context.Generation,
            target,
            DateTimeOffset.UnixEpoch));
        var rejectedAutomatic = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(20)));

        Assert.Equal(CaptureCommandDisposition.Completed, loss.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Idle, coordinator.Snapshot.Status);
        Assert.Single(backend.StopCalls);
        Assert.Equal(CaptureCommandDisposition.Rejected, rejectedAutomatic.Disposition);

        Assert.Equal(CaptureCommandDisposition.Completed,
            (await coordinator.StopAsync(CaptureInitiation.Manual)).Disposition);
        Assert.Equal(CaptureCommandDisposition.Completed,
            (await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(20)))).Disposition);
    }

    [Fact]
    public async Task ExactStoragePressure_StopsManualCaptureAndBlocksAdmissionUntilRecovered()
    {
        var backend = new FakeBackend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(10, 0x1000)));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var pressure = await coordinator.HandleStoragePressureAsync(new CaptureStoragePressure(
            context.SessionId,
            context.Generation,
            DateTimeOffset.UnixEpoch,
            "The recording volume is below its safety reserve."));
        var blocked = await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(20, 0x2000)));
        var wasBlocked = coordinator.IsStorageAdmissionBlocked;
        var recovered = await coordinator.ClearStoragePressureAsync();
        var restarted = await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(20, 0x2000)));

        Assert.Equal(CaptureCommandDisposition.Completed, pressure.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Idle, pressure.After.Status);
        Assert.Equal(CaptureReadiness.Closed, pressure.After.Readiness);
        Assert.Equal(CaptureFailureKind.Storage, pressure.After.LastFailure?.Kind);
        Assert.True(wasBlocked);
        Assert.False(coordinator.IsStorageAdmissionBlocked);
        Assert.Equal(CaptureCommandDisposition.Rejected, blocked.Disposition);
        Assert.Equal(CaptureFailureKind.Storage, blocked.Failure?.Kind);
        Assert.Equal(CaptureCommandDisposition.Completed, recovered.Disposition);
        Assert.Equal(CaptureCommandDisposition.Completed, restarted.Disposition);
        Assert.Single(backend.StopCalls);
    }

    [Fact]
    public async Task StaleStoragePressure_BlocksAdmissionWithoutStoppingNewerContext()
    {
        var backend = new FakeBackend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var stale = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);
        await coordinator.SwitchAsync(Request(CaptureInitiation.Automatic, Game(20)));
        var current = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var pressure = await coordinator.HandleStoragePressureAsync(new CaptureStoragePressure(
            stale.SessionId,
            stale.Generation,
            DateTimeOffset.UnixEpoch,
            "Low disk space."));

        Assert.Equal(CaptureCommandDisposition.Ignored, pressure.Disposition);
        Assert.Same(current, coordinator.Snapshot.Context);
        Assert.Equal(CaptureLifecycleStatus.Active, coordinator.Snapshot.Status);
        Assert.True(coordinator.IsStorageAdmissionBlocked);
        Assert.Single(backend.StopCalls); // the deliberate switch only
        Assert.Equal(
            CaptureFailureKind.Storage,
            (await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(30, 0x3000)))).Failure?.Kind);
    }

    [Fact]
    public async Task StoragePressureStopFailureRetainsExactContextForRetry()
    {
        var failure = new CaptureFailure(
            CaptureFailureKind.Backend,
            "storage.stop-failed",
            "Recorder cleanup failed.");
        var backend = new FakeBackend
        {
            StopHandler = (_, _) => Task.FromResult(CaptureBackendCommandResult.Failed(failure)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var pressure = await coordinator.HandleStoragePressureAsync(new CaptureStoragePressure(
            context.SessionId,
            context.Generation,
            DateTimeOffset.UnixEpoch,
            "Low disk space."));

        Assert.Equal(CaptureCommandDisposition.Failed, pressure.Disposition);
        Assert.Same(context, pressure.After.Context);
        Assert.Equal(CaptureLifecycleStatus.Faulted, pressure.After.Status);
        Assert.True(coordinator.IsStorageAdmissionBlocked);
        Assert.Equal(failure, pressure.Failure);
    }

    [Fact]
    public async Task SourceLossStopFailure_BlocksSaveAndRestartUntilExplicitCleanupSucceeds()
    {
        var stopFailure = new CaptureFailure(
            CaptureFailureKind.Backend,
            "source-loss.stop-failed",
            "Source-loss cleanup failed.");
        var stopAttempts = 0;
        var backend = new FakeBackend
        {
            StopHandler = (_, _) => Task.FromResult(++stopAttempts == 1
                ? CaptureBackendCommandResult.Failed(stopFailure)
                : CaptureBackendCommandResult.Success()),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        var target = Game(10);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, target));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var sourceLoss = await coordinator.HandleSourceLostAsync(new CaptureSourceLoss(
            context.SessionId,
            context.Generation,
            target,
            DateTimeOffset.UnixEpoch));
        var save = await coordinator.SaveAsync();
        var restart = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(20)));
        var cleanup = await coordinator.StopAsync(CaptureInitiation.Automatic);

        Assert.Equal(CaptureCommandDisposition.Failed, sourceLoss.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Faulted, sourceLoss.After.Status);
        Assert.Same(context, sourceLoss.After.Context);
        Assert.Equal(stopFailure, sourceLoss.After.LastFailure);
        Assert.Equal(CaptureCommandDisposition.Rejected, save.Disposition);
        Assert.Equal(CaptureCommandDisposition.Rejected, restart.Disposition);
        Assert.Single(backend.StartCalls);
        Assert.Equal(CaptureCommandDisposition.Completed, cleanup.Disposition);
        Assert.Equal(2, backend.StopCalls.Count);
        Assert.Equal(CaptureLifecycleStatus.Idle, cleanup.After.Status);
        Assert.Null(cleanup.After.Context);
    }

    [Fact]
    public async Task SwitchStopFailure_BlocksReplacementUntilExplicitCleanupSucceeds()
    {
        var stopFailure = new CaptureFailure(
            CaptureFailureKind.Backend,
            "switch.stop-failed",
            "Replacement cleanup failed.");
        var stopAttempts = 0;
        var backend = new FakeBackend
        {
            StopHandler = (_, _) => Task.FromResult(++stopAttempts == 1
                ? CaptureBackendCommandResult.Failed(stopFailure)
                : CaptureBackendCommandResult.Success()),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var failedSwitch = await coordinator.SwitchAsync(Request(CaptureInitiation.Automatic, Game(20)));
        var rejectedSwitch = await coordinator.SwitchAsync(Request(CaptureInitiation.Automatic, Game(30)));

        Assert.Equal(CaptureCommandDisposition.Failed, failedSwitch.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Faulted, failedSwitch.After.Status);
        Assert.Same(context, failedSwitch.After.Context);
        Assert.Equal(stopFailure, failedSwitch.After.LastFailure);
        Assert.Equal(CaptureCommandDisposition.Rejected, rejectedSwitch.Disposition);
        Assert.Single(backend.StartCalls);

        var cleanup = await coordinator.StopAsync(CaptureInitiation.Automatic);
        var replacement = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(40)));

        Assert.Equal(CaptureCommandDisposition.Completed, cleanup.Disposition);
        Assert.Equal(CaptureCommandDisposition.Completed, replacement.Disposition);
        Assert.Equal(2, backend.StartCalls.Count);
        Assert.Equal(2, backend.StopCalls.Count);
    }

    [Fact]
    public async Task SaveResult_CarriesTheImmutableSessionContext()
    {
        var requested = Game(42);
        var actual = Window(42, 0x4242);
        var backend = new FakeBackend
        {
            StartHandler = (_, _) => Task.FromResult(CaptureBackendStartResult.Success(actual)),
            SaveHandler = (context, _) => Task.FromResult(CaptureBackendSaveResult.Success(
                "clip.mp4",
                new CompletedMediaReceipt(
                    "clip.mp4",
                    "Game",
                    DateTimeOffset.UnixEpoch,
                    context.SessionId,
                    context.Request.RequestId,
                    context.Generation,
                    null))),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, requested));
        var expectedContext = coordinator.Snapshot.Context;

        var result = await coordinator.SaveAsync();

        Assert.Equal(CaptureCommandDisposition.Completed, result.Disposition);
        Assert.Equal("clip.mp4", result.OutputPath);
        Assert.Same(expectedContext, result.Context);
        Assert.Equal(requested, result.Context?.RequestedTarget);
        Assert.Equal(actual, result.Context?.ActualTarget);
        Assert.Equal(expectedContext!.SessionId, result.Receipt?.SessionId);
        Assert.Equal(expectedContext.Request.RequestId, result.Receipt?.CaptureRequestId);
        Assert.Equal(expectedContext.Generation, result.Receipt?.CaptureGeneration);
        Assert.Equal(expectedContext, Assert.Single(backend.SaveCalls));
    }

    [Fact]
    public async Task BackendSaveFailure_NeverReportsSaved()
    {
        var failure = new CaptureFailure(CaptureFailureKind.Backend, "save.failed", "Muxer failed.");
        var backend = new FakeBackend
        {
            SaveHandler = (_, _) => Task.FromResult(CaptureBackendSaveResult.Failed(failure)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));

        var result = await coordinator.SaveAsync();

        Assert.Equal(CaptureCommandDisposition.Failed, result.Disposition);
        Assert.Null(result.OutputPath);
        Assert.Equal(failure, result.Failure);
        Assert.Equal(CaptureLifecycleStatus.Active, coordinator.Snapshot.Status);
        Assert.Equal(CaptureHealth.Degraded, coordinator.Snapshot.Health);
    }

    [Fact]
    public async Task BackendDisplayFallback_IsStoppedAndReportedAsFailure()
    {
        var fallback = new DisplayCaptureTarget("display-1", "Primary", 0);
        var backend = new FakeBackend
        {
            StartHandler = (_, _) => Task.FromResult(CaptureBackendStartResult.Success(fallback)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);

        var result = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));

        Assert.Equal(CaptureCommandDisposition.Failed, result.Disposition);
        Assert.Equal("capture.start.unapproved-target", result.Failure?.Code);
        Assert.Equal(CaptureLifecycleStatus.Faulted, coordinator.Snapshot.Status);
        Assert.Null(coordinator.Snapshot.Context);
        Assert.Single(backend.StopCalls);
        Assert.Equal(fallback, backend.StopCalls[0].ActualTarget);
    }

    [Theory]
    [InlineData(true, "window-title")]
    [InlineData(true, "window-executable")]
    [InlineData(true, "window-class")]
    [InlineData(true, "display-name")]
    [InlineData(true, "display-index")]
    [InlineData(false, "window-title")]
    [InlineData(false, "window-executable")]
    [InlineData(false, "window-class")]
    [InlineData(false, "display-name")]
    [InlineData(false, "display-index")]
    public async Task BoundRequestsRequireFullReturnedTargetEqualityWhileLegacyRetainsIdentityPermissions(
        bool bound,
        string changedField)
    {
        var (requested, actual) = FullRecordDrift(changedField);
        var backend = new FakeBackend
        {
            StartHandler = (_, _) => Task.FromResult(CaptureBackendStartResult.Success(actual)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        var request = bound
            ? BoundRequest(requested)
            : Request(CaptureInitiation.Manual, requested);

        var result = await coordinator.StartAsync(request);

        if (bound)
        {
            Assert.Equal(CaptureCommandDisposition.Failed, result.Disposition);
            Assert.Equal("capture.start.unapproved-target", result.Failure?.Code);
            Assert.Equal(actual, Assert.Single(backend.StopCalls).ActualTarget);
            Assert.Null(result.After.Context);
        }
        else
        {
            Assert.Equal(CaptureCommandDisposition.Completed, result.Disposition);
            Assert.Equal(CaptureLifecycleStatus.Active, result.After.Status);
            Assert.Equal(requested, result.After.Context?.RequestedTarget);
            Assert.Equal(actual, result.After.Context?.ActualTarget);
            Assert.Empty(backend.StopCalls);
        }
    }

    [Fact]
    public async Task BoundTargetDriftRetainsRequestedAndActualTargetsWhenUncancelledCleanupFails()
    {
        var requested = new DisplayCaptureTarget("display-1", "Confirmed display", 0);
        var actual = requested with { DisplayName = "Changed display", MonitorIndex = 1 };
        var cleanupFailure = new CaptureFailure(
            CaptureFailureKind.Backend,
            "cleanup.failed",
            "Cleanup remains owned.");
        var cleanupToken = new CancellationToken(canceled: true);
        var backend = new FakeBackend
        {
            StartHandler = (_, _) => Task.FromResult(CaptureBackendStartResult.Success(actual)),
            StopHandler = (_, token) =>
            {
                cleanupToken = token;
                return Task.FromResult(CaptureBackendCommandResult.Failed(cleanupFailure));
            },
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        var request = BoundRequest(requested);
        using var callerCancellation = new CancellationTokenSource();

        var result = await coordinator.StartAsync(request, callerCancellation.Token);

        Assert.Equal(CaptureCommandDisposition.Failed, result.Disposition);
        Assert.Equal("capture.start.unapproved-target", result.Failure?.Code);
        Assert.Equal(
            "The backend started an unapproved physical target; implicit display fallback is not allowed.",
            result.Failure?.Message);
        Assert.Same(cleanupFailure, result.Failure?.CleanupFailure);
        Assert.False(cleanupToken.CanBeCanceled);
        var cleanupContext = Assert.Single(backend.StopCalls);
        Assert.Same(request, cleanupContext.Request);
        Assert.Equal(requested, cleanupContext.RequestedTarget);
        Assert.Equal(actual, cleanupContext.ActualTarget);
        var retained = Assert.IsType<CaptureSessionContext>(result.After.Context);
        Assert.Same(request, retained.Request);
        Assert.Equal(requested, retained.RequestedTarget);
        Assert.Equal(actual, retained.ActualTarget);
        Assert.Equal(CaptureLifecycleStatus.Faulted, result.After.Status);
        Assert.Equal(CaptureHealth.Failed, result.After.Health);
        var automatic = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(99)));
        Assert.Equal(CaptureCommandDisposition.Rejected, automatic.Disposition);
        Assert.Equal(CaptureFailureKind.ManualPriority, automatic.Failure?.Kind);
    }

    [Fact]
    public async Task BackendCannotShiftOrExpandRequestedRegion()
    {
        var requested = new RegionCaptureTarget(
            "display-1",
            new PhysicalCaptureBounds(100, 100, 640, 360),
            new PhysicalCaptureBounds(0, 0, 1920, 1080));
        var widened = requested with
        {
            PhysicalBounds = new PhysicalCaptureBounds(99, 100, 641, 360),
        };
        var backend = new FakeBackend
        {
            StartHandler = (_, _) => Task.FromResult(CaptureBackendStartResult.Success(widened)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);

        var result = await coordinator.StartAsync(Request(CaptureInitiation.Manual, requested));

        Assert.Equal(CaptureCommandDisposition.Failed, result.Disposition);
        Assert.Equal("capture.start.unapproved-target", result.Failure?.Code);
        Assert.Equal(widened, Assert.Single(backend.StopCalls).ActualTarget);
        Assert.Null(result.After.Context);
    }

    [Fact]
    public async Task Shutdown_ClosesAdmissionBeforeWaitingForBackendStop()
    {
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            StopHandler = async (_, _) =>
            {
                stopEntered.SetResult();
                await releaseStop.Task;
                return CaptureBackendCommandResult.Success();
            },
        };
        var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));

        var shutdown = coordinator.ShutdownAsync();
        await stopEntered.Task;

        Assert.Equal(CaptureCommandDisposition.Rejected,
            (await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(20)))).Disposition);
        Assert.Equal(CaptureCommandDisposition.Rejected,
            (await coordinator.SwitchAsync(Request(CaptureInitiation.Manual, Window(30, 0x3000)))).Disposition);
        Assert.Equal(CaptureCommandDisposition.Rejected, (await coordinator.SaveAsync()).Disposition);

        releaseStop.SetResult();
        Assert.Equal(CaptureCommandDisposition.Completed, (await shutdown).Disposition);
        Assert.Equal(CaptureLifecycleStatus.Shutdown, coordinator.Snapshot.Status);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task FailedShutdown_IsTerminalAndLaterStopCannotEraseFailureOrContext()
    {
        var startFailure = new CaptureFailure(
            CaptureFailureKind.Backend,
            "start.cleanup-failed",
            "Startup cleanup failed.");
        var shutdownFailure = new CaptureFailure(
            CaptureFailureKind.Backend,
            "shutdown.stop-failed",
            "Shutdown cleanup failed.");
        var backend = new FakeBackend
        {
            StartHandler = (_, _) => Task.FromResult(
                CaptureBackendStartResult.Failed(startFailure, Window(10, 0x1010))),
            StopHandler = (_, _) => Task.FromResult(CaptureBackendCommandResult.Failed(shutdownFailure)),
        };
        var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var retainedContext = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var shutdown = await coordinator.ShutdownAsync();
        var stopAfterShutdown = await coordinator.StopAsync(CaptureInitiation.Automatic);

        Assert.Equal(CaptureCommandDisposition.Failed, shutdown.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Faulted, shutdown.After.Status);
        Assert.Equal(CaptureReadiness.Closed, shutdown.After.Readiness);
        Assert.Same(retainedContext, shutdown.After.Context);
        Assert.Equal(shutdownFailure, shutdown.After.LastFailure);
        Assert.Equal(CaptureCommandDisposition.Rejected, stopAfterShutdown.Disposition);
        Assert.Equal(CaptureFailureKind.AdmissionClosed, stopAfterShutdown.Failure?.Kind);
        Assert.Same(shutdown.After, stopAfterShutdown.After);
        Assert.Single(backend.StopCalls);
        Assert.Same(shutdown.After, coordinator.Snapshot);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task FailedShutdown_CanRetryCleanupWithoutReopeningAdmission()
    {
        var stopFailure = new CaptureFailure(
            CaptureFailureKind.Backend,
            "shutdown.stop-failed",
            "Shutdown cleanup failed.");
        var stopAttempts = 0;
        var backend = new FakeBackend
        {
            StopHandler = (_, _) => Task.FromResult(++stopAttempts == 1
                ? CaptureBackendCommandResult.Failed(stopFailure)
                : CaptureBackendCommandResult.Success()),
        };
        var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));

        var firstShutdown = await coordinator.ShutdownAsync();
        var retry = await coordinator.ShutdownAsync();
        var rejectedStart = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(20)));

        Assert.Equal(CaptureCommandDisposition.Failed, firstShutdown.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Faulted, firstShutdown.After.Status);
        Assert.Equal(CaptureCommandDisposition.Completed, retry.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Shutdown, retry.After.Status);
        Assert.Equal(2, backend.StopCalls.Count);
        Assert.True(coordinator.IsAdmissionClosed);
        Assert.Equal(CaptureCommandDisposition.Rejected, rejectedStart.Disposition);
        Assert.Equal(CaptureFailureKind.AdmissionClosed, rejectedStart.Failure?.Kind);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ShutdownWarningAfterCleanup_CompletesAndRemainsTerminal()
    {
        var warning = new CaptureFailure(
            CaptureFailureKind.Backend,
            "shutdown.media-invalid",
            "Finalized media was missing.");
        var backend = new FakeBackend
        {
            StopHandler = (_, _) => Task.FromResult(
                CaptureBackendCommandResult.Failed(warning, cleanupRequired: false)),
        };
        var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));

        var first = await coordinator.ShutdownAsync();
        var second = await coordinator.ShutdownAsync();

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(CaptureLifecycleStatus.Shutdown, first.After.Status);
        Assert.Equal(CaptureHealth.Degraded, first.After.Health);
        Assert.Null(first.After.Context);
        Assert.Equal(warning, first.After.LastFailure);
        Assert.Single(backend.StopCalls);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task PauseResumeAndSplit_AreTruthfullyUnsupported()
    {
        var backend = new FakeBackend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));

        var pause = await coordinator.PauseAsync();
        var resume = await coordinator.ResumeAsync();
        var split = await coordinator.SplitAsync();

        Assert.All(new[] { pause, resume, split }, result =>
        {
            Assert.Equal(CaptureCommandDisposition.Unsupported, result.Disposition);
            Assert.Equal(CaptureFailureKind.Unsupported, result.Failure?.Kind);
            Assert.Equal(CaptureLifecycleStatus.Active, result.After.Status);
        });
    }

    [Fact]
    public async Task SaveWithExpectedContext_IsRejectedAfterSourceSwitch()
    {
        var backend = new FakeBackend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var originalContext = coordinator.Snapshot.Context!;
        await coordinator.SwitchAsync(Request(CaptureInitiation.Automatic, Game(20)));

        var result = await coordinator.SaveAsync(originalContext);

        Assert.Equal(CaptureCommandDisposition.Rejected, result.Disposition);
        Assert.Equal(CaptureFailureKind.Superseded, result.Failure?.Kind);
        Assert.Equal("capture.save.context-superseded", result.Failure?.Code);
        Assert.Empty(backend.SaveCalls);
    }

    [Fact]
    public async Task OwnedBackendFailure_DegradesActiveLifecycleWithoutStoppingReplay()
    {
        var backend = new FakeBackend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var context = coordinator.Snapshot.Context!;
        var failure = new CaptureFailure(
            CaptureFailureKind.Backend,
            "capture.session-recording.failed",
            "Session output failed.",
            true);

        var result = await coordinator.ReportDegradedAsync(context, failure);

        Assert.Equal(CaptureCommandDisposition.Completed, result.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Active, result.After.Status);
        Assert.Equal(CaptureHealth.Degraded, result.After.Health);
        Assert.Equal(failure, result.After.LastFailure);
        Assert.Empty(backend.StopCalls);
    }

    [Fact]
    public async Task StaleBackendFailure_IsIgnoredAfterSourceSwitch()
    {
        var backend = new FakeBackend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var staleContext = coordinator.Snapshot.Context!;
        await coordinator.SwitchAsync(Request(CaptureInitiation.Automatic, Game(20)));

        var result = await coordinator.ReportDegradedAsync(
            staleContext,
            new CaptureFailure(CaptureFailureKind.Backend, "stale", "Stale failure."));

        Assert.Equal(CaptureCommandDisposition.Ignored, result.Disposition);
        Assert.Equal(CaptureHealth.Healthy, result.After.Health);
        Assert.Null(result.After.LastFailure);
    }

    [Fact]
    public async Task UnsupportedSave_PreservesHealthyActiveState()
    {
        var unsupported = new CaptureFailure(
            CaptureFailureKind.Unsupported,
            "capture.save.replay-disabled",
            "Replay saving is unavailable.");
        var backend = new FakeBackend
        {
            SaveHandler = (_, _) => Task.FromResult(CaptureBackendSaveResult.Failed(unsupported)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));

        var result = await coordinator.SaveAsync();

        Assert.Equal(CaptureCommandDisposition.Unsupported, result.Disposition);
        Assert.Equal(CaptureLifecycleStatus.Active, coordinator.Snapshot.Status);
        Assert.Equal(CaptureHealth.Healthy, coordinator.Snapshot.Health);
        Assert.Null(coordinator.Snapshot.LastFailure);
    }

    [Fact]
    public async Task SuccessfulSave_PreservesAnUnrelatedActiveDegradation()
    {
        var backend = new FakeBackend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);
        var warning = new CaptureFailure(
            CaptureFailureKind.Backend,
            "capture.session-recording.failed",
            "Session output failed.");
        await coordinator.ReportDegradedAsync(context, warning);

        var save = await coordinator.SaveAsync(context);

        Assert.True(save.Saved);
        Assert.Equal(CaptureLifecycleStatus.Active, coordinator.Snapshot.Status);
        Assert.Equal(CaptureHealth.Degraded, coordinator.Snapshot.Health);
        Assert.Equal(warning, coordinator.Snapshot.LastFailure);
    }

    [Fact]
    public async Task SnapshotSubscriberFailure_DoesNotBreakLifecycleOrOtherSubscribers()
    {
        var backend = new FakeBackend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        var observed = new List<CaptureLifecycleSnapshot>();
        coordinator.SnapshotChanged += _ => throw new InvalidOperationException("observer failed");
        coordinator.SnapshotChanged += observed.Add;

        var start = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var stop = await coordinator.StopAsync(CaptureInitiation.Automatic);

        Assert.True(start.Succeeded);
        Assert.True(stop.Succeeded);
        Assert.Contains(observed, snapshot => snapshot.Status == CaptureLifecycleStatus.Active);
        Assert.Equal(CaptureLifecycleStatus.Idle, observed[^1].Status);
    }

    [Fact]
    public async Task DelayedSave_CompletesBeforeQueuedStop()
    {
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var backend = new FakeBackend
        {
            SaveHandler = async (_, _) =>
            {
                order.Add("save-entered");
                saveEntered.SetResult();
                await releaseSave.Task;
                order.Add("save-completed");
                return CaptureBackendSaveResult.Success("clip.mp4");
            },
            StopHandler = (_, _) =>
            {
                order.Add("stop");
                return Task.FromResult(CaptureBackendCommandResult.Success());
            },
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));

        var save = coordinator.SaveAsync();
        await saveEntered.Task;
        var stop = coordinator.StopAsync(CaptureInitiation.Automatic);

        Assert.False(stop.IsCompleted);
        Assert.Empty(backend.StopCalls);
        releaseSave.SetResult();

        Assert.True((await save).Saved);
        Assert.True((await stop).Succeeded);
        Assert.Equal(["save-entered", "save-completed", "stop"], order);
    }

    [Fact]
    public async Task DelayedSave_CompletesBeforeShutdownWhileAdmissionClosesImmediately()
    {
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();
        var backend = new FakeBackend
        {
            SaveHandler = async (_, _) =>
            {
                order.Add("save-entered");
                saveEntered.SetResult();
                await releaseSave.Task;
                order.Add("save-completed");
                return CaptureBackendSaveResult.Success("clip.mp4");
            },
            StopHandler = (_, _) =>
            {
                order.Add("shutdown-stop");
                return Task.FromResult(CaptureBackendCommandResult.Success());
            },
        };
        var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));

        var save = coordinator.SaveAsync();
        await saveEntered.Task;
        var shutdown = coordinator.ShutdownAsync();

        Assert.True(coordinator.IsAdmissionClosed);
        Assert.False(shutdown.IsCompleted);
        Assert.Empty(backend.StopCalls);
        releaseSave.SetResult();

        Assert.True((await save).Saved);
        Assert.True((await shutdown).Succeeded);
        Assert.Equal(["save-entered", "save-completed", "shutdown-stop"], order);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task UnsupportedSplit_DoesNotInterruptAnAdmittedSave()
    {
        var saveEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new FakeBackend
        {
            SaveHandler = async (_, _) =>
            {
                saveEntered.SetResult();
                await releaseSave.Task;
                return CaptureBackendSaveResult.Success("clip.mp4");
            },
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));

        var save = coordinator.SaveAsync();
        await saveEntered.Task;
        var split = await coordinator.SplitAsync();

        Assert.Equal(CaptureCommandDisposition.Unsupported, split.Disposition);
        Assert.False(save.IsCompleted);
        Assert.Equal(CaptureLifecycleStatus.Saving, coordinator.Snapshot.Status);

        releaseSave.SetResult();
        Assert.True((await save).Saved);
        Assert.Equal(CaptureLifecycleStatus.Active, coordinator.Snapshot.Status);
    }

    [Fact]
    public async Task CorrelatedDeviceLoss_StopsOnceRetainsCauseAndManualReservation()
    {
        var manifest = new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-id"),
        ]);
        var backend = new FakeBackend
        {
            StartHandler = (context, _) => Task.FromResult(
                CaptureBackendStartResult.Success(context.RequestedTarget, manifest)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(10, 0x1000)));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);
        var observation = DeviceLoss(context, CaptureDeviceClosedReason.ExactSelectionUnavailable);

        var first = await coordinator.HandleDeviceLostAsync(observation);
        var duplicate = await coordinator.HandleDeviceLostAsync(observation);
        var automatic = await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(20)));

        Assert.True(first.Succeeded);
        Assert.Equal(CaptureCommandDisposition.Ignored, duplicate.Disposition);
        Assert.Single(backend.StopCalls);
        Assert.Equal(CaptureLifecycleStatus.Faulted, coordinator.Snapshot.Status);
        Assert.Equal(CaptureHealth.Degraded, coordinator.Snapshot.Health);
        Assert.Null(coordinator.Snapshot.Context);
        Assert.Equal(
            "capture.device.microphone.active.selection-unavailable",
            coordinator.Snapshot.LastFailure?.Code);
        Assert.Equal(CaptureCommandDisposition.Rejected, automatic.Disposition);
        Assert.Equal(CaptureFailureKind.ManualPriority, automatic.Failure?.Kind);
    }

    [Fact]
    public async Task DeviceLossRejectsEveryStaleCorrelationFieldBeforeStopping()
    {
        var manifest = new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Webcam, "camera-id"),
        ]);
        var backend = new FakeBackend
        {
            StartHandler = (context, _) => Task.FromResult(
                CaptureBackendStartResult.Success(context.RequestedTarget, manifest)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(10, 0x1000)));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        CaptureDeviceLossObservation Observation(
            Guid sessionId,
            Guid requestId,
            long generation,
            CaptureDeviceRole role,
            string deviceId) => new(
                sessionId,
                requestId,
                generation,
                role,
                CaptureDeviceSelectionMode.Exact,
                deviceId,
                DateTimeOffset.UnixEpoch,
                CaptureDeviceClosedReason.ExactSelectionUnavailable);
        var stale = new[]
        {
            Observation(Guid.NewGuid(), context.Request.RequestId, context.Generation, CaptureDeviceRole.Webcam, "camera-id"),
            Observation(context.SessionId, Guid.NewGuid(), context.Generation, CaptureDeviceRole.Webcam, "camera-id"),
            Observation(context.SessionId, context.Request.RequestId, context.Generation + 1, CaptureDeviceRole.Webcam, "camera-id"),
            Observation(context.SessionId, context.Request.RequestId, context.Generation, CaptureDeviceRole.Microphone, "camera-id"),
            Observation(context.SessionId, context.Request.RequestId, context.Generation, CaptureDeviceRole.Webcam, "CAMERA-ID"),
        };

        foreach (var observation in stale)
        {
            Assert.Equal(
                CaptureCommandDisposition.Ignored,
                (await coordinator.HandleDeviceLostAsync(observation)).Disposition);
        }
        Assert.Empty(backend.StopCalls);
    }

    [Theory]
    [InlineData(true, CaptureHealth.Failed, true)]
    [InlineData(false, CaptureHealth.Degraded, false)]
    public async Task DeviceLossCleanupFailureRetainsCausalCodeAndTruthfulOwnership(
        bool cleanupRequired,
        CaptureHealth expectedHealth,
        bool expectsContext)
    {
        var manifest = new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.SystemDefault(CaptureDeviceRole.DesktopAudio),
        ]);
        var backend = new FakeBackend
        {
            StartHandler = (context, _) => Task.FromResult(
                CaptureBackendStartResult.Success(context.RequestedTarget, manifest)),
            StopHandler = (_, _) => Task.FromResult(CaptureBackendCommandResult.Failed(
                new CaptureFailure(CaptureFailureKind.Backend, "cleanup.detail", "cleanup detail"),
                cleanupRequired)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic, Game(10)));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);
        var observation = new CaptureDeviceLossObservation(
            context.SessionId,
            context.Request.RequestId,
            context.Generation,
            CaptureDeviceRole.DesktopAudio,
            CaptureDeviceSelectionMode.SystemDefault,
            "default",
            DateTimeOffset.UnixEpoch,
            CaptureDeviceClosedReason.DefaultChanged,
            Core.Audio.WindowsAudioEndpointRole.Multimedia,
            "speaker-a",
            "speaker-b");

        var result = await coordinator.HandleDeviceLostAsync(observation);

        Assert.Equal(CaptureCommandDisposition.Failed, result.Disposition);
        Assert.Equal(expectedHealth, coordinator.Snapshot.Health);
        Assert.Equal(expectsContext, coordinator.Snapshot.Context is not null);
        Assert.Equal(
            "capture.device.desktop-audio.active.default-changed",
            coordinator.Snapshot.LastFailure?.Code);
        Assert.DoesNotContain("cleanup detail", coordinator.Snapshot.LastFailure?.Message);
        Assert.Equal("cleanup.detail", coordinator.Snapshot.LastFailure?.CleanupFailure?.Code);
        Assert.Equal("cleanup detail", coordinator.Snapshot.LastFailure?.CleanupFailure?.Message);
        Assert.DoesNotContain("speaker-a", coordinator.Snapshot.LastFailure?.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("speaker-b", coordinator.Snapshot.LastFailure?.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CaptureDeviceClosedReason.CatalogUnavailable, "catalog-unavailable")]
    [InlineData(CaptureDeviceClosedReason.CatalogInvalid, "catalog-invalid")]
    public async Task DeviceCatalogObservationFailureRemainsBackendEvidence(
        CaptureDeviceClosedReason reason,
        string codeSuffix)
    {
        var manifest = new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-id"),
        ]);
        var backend = new FakeBackend
        {
            StartHandler = (context, _) => Task.FromResult(
                CaptureBackendStartResult.Success(context.RequestedTarget, manifest)),
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(10, 0x1000)));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var result = await coordinator.HandleDeviceLostAsync(DeviceLoss(context, reason));

        Assert.True(result.Succeeded);
        Assert.Equal(CaptureFailureKind.Backend, coordinator.Snapshot.LastFailure?.Kind);
        Assert.Equal(
            $"capture.device.microphone.active.{codeSuffix}",
            coordinator.Snapshot.LastFailure?.Code);
        Assert.DoesNotContain("busy", coordinator.Snapshot.LastFailure?.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("permission", coordinator.Snapshot.LastFailure?.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AcceptedDeviceLossCleanupFinishesAfterObserverCancellation()
    {
        var stopEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manifest = new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-id"),
        ]);
        var backend = new FakeBackend
        {
            StartHandler = (context, _) => Task.FromResult(
                CaptureBackendStartResult.Success(context.RequestedTarget, manifest)),
            StopHandler = async (_, cleanupToken) =>
            {
                stopEntered.SetResult(cleanupToken);
                await releaseStop.Task;
                return CaptureBackendCommandResult.Success();
            },
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual, Window(10, 0x1000)));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);
        using var observationCancellation = new CancellationTokenSource();

        var transition = coordinator.HandleDeviceLostAsync(
            DeviceLoss(context, CaptureDeviceClosedReason.ExactSelectionUnavailable),
            observationCancellation.Token);
        var cleanupToken = await stopEntered.Task;
        observationCancellation.Cancel();

        Assert.False(cleanupToken.CanBeCanceled);
        Assert.False(transition.IsCompleted);
        releaseStop.SetResult();
        Assert.True((await transition).Succeeded);
        Assert.Equal(CaptureLifecycleStatus.Faulted, coordinator.Snapshot.Status);
    }

    private static CaptureDeviceLossObservation DeviceLoss(
        CaptureSessionContext context,
        CaptureDeviceClosedReason reason) =>
        new(
            context.SessionId,
            context.Request.RequestId,
            context.Generation,
            CaptureDeviceRole.Microphone,
            CaptureDeviceSelectionMode.Exact,
            "mic-id",
            DateTimeOffset.UnixEpoch,
            reason);

    private static CaptureRequest Request(CaptureInitiation initiation, CaptureTarget target) =>
        new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, initiation, target);

    private static CaptureRequest BoundRequest(CaptureTarget target)
    {
        var baseline = CaptureStartupBaselineResult.Create(
            new AppSettings(),
            Guid.Parse("ed86e887-7136-444d-bbc4-c8aa66af42ec"));
        var template = CaptureStartupBaselineTests.Request(baseline);
        return new CaptureRequest(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            CaptureInitiation.Manual,
            target,
            startPolicy: template.StartPolicy,
            startupExpectation: template.StartupExpectation);
    }

    private static (CaptureTarget Requested, CaptureTarget Actual) FullRecordDrift(string changedField)
    {
        var window = new WindowCaptureTarget(42, new IntPtr(0x4200), "Confirmed title", "tutorial.exe", "TutorialClass");
        var display = new DisplayCaptureTarget("display-1", "Confirmed display", 0);
        return changedField switch
        {
            "window-title" => (window, window with { WindowTitle = "Changed title" }),
            "window-executable" => (window, window with { ExecutableName = "changed.exe" }),
            "window-class" => (window, window with { WindowClass = "ChangedClass" }),
            "display-name" => (display, display with { DisplayName = "Changed display" }),
            "display-index" => (display, display with { MonitorIndex = 1 }),
            _ => throw new ArgumentOutOfRangeException(nameof(changedField)),
        };
    }

    private static GameCaptureTarget Game(int processId) =>
        new($"game-{processId}", processId, "game.exe", DateTimeOffset.UnixEpoch);

    private static WindowCaptureTarget Window(int processId, long handle) =>
        new(processId, new IntPtr(handle), $"window-{processId}");

    private sealed class FakeBackend : ICaptureLifecycleBackend
    {
        public Func<CaptureRequest, CaptureFailure?> ValidateHandler { get; init; } = _ => null;
        public CaptureFailure? ValidateStart(CaptureRequest request) => ValidateHandler(request);
        public Func<CaptureSessionContext, CancellationToken, Task<CaptureBackendStartResult>> StartHandler { get; init; } =
            (context, _) => Task.FromResult(CaptureBackendStartResult.Success(context.RequestedTarget));

        public Func<CaptureSessionContext, CancellationToken, Task<CaptureBackendCommandResult>> StopHandler { get; init; } =
            (_, _) => Task.FromResult(CaptureBackendCommandResult.Success());

        public Func<CaptureSessionContext, CancellationToken, Task<CaptureBackendSaveResult>> SaveHandler { get; init; } =
            (_, _) => Task.FromResult(CaptureBackendSaveResult.Success("clip.mp4"));

        public List<CaptureSessionContext> StartCalls { get; } = [];
        public List<CaptureSessionContext> StopCalls { get; } = [];
        public List<CaptureSessionContext> SaveCalls { get; } = [];

        public async Task<CaptureBackendStartResult> StartAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken)
        {
            StartCalls.Add(context);
            return await StartHandler(context, cancellationToken);
        }

        public async Task<CaptureBackendCommandResult> StopAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken)
        {
            StopCalls.Add(context);
            return await StopHandler(context, cancellationToken);
        }

        public async Task<CaptureBackendSaveResult> SaveAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken)
        {
            SaveCalls.Add(context);
            return await SaveHandler(context, cancellationToken);
        }
    }
}
