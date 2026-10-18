using WatchDog.Core.Capture;
using WatchDog.Core.Runtime;

namespace WatchDog.Core.Tests.Runtime;

public sealed class ApplicationShutdownDrainTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainAsync_EarlierWarningSurvivesLaterHostFailure(bool failDuringDispose)
    {
        var error = new IOException("private host detail");
        var diagnostic = new CaptureFailure(CaptureFailureKind.Backend, "capture.output.warning", "private path");
        var drain = new ApplicationShutdownDrain(
            () => Task.FromResult(ShutdownStepResult.Success("private recording warning", diagnostic)),
            () => Task.CompletedTask,
            () => failDuringDispose ? Task.CompletedTask : Task.FromException(error),
            () => failDuringDispose ? ValueTask.FromException(error) : ValueTask.CompletedTask);

        var result = await drain.DrainAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(failDuringDispose ? ShutdownDrainFailureStage.HostDispose : ShutdownDrainFailureStage.HostStop, result.FailedStage);
        Assert.Same(error, result.Exception);
        Assert.Equal("private recording warning", result.WarningMessage);
        Assert.Same(diagnostic, result.CaptureDiagnostic);
    }

    [Fact]
    public async Task DrainAsync_TypedCaptureFailurePreservesDiagnosticAndSuppressesHost()
    {
        var diagnostic = new CaptureFailure(
            CaptureFailureKind.Backend, "capture.webcam.view-release-uncertain", "private device", false);
        var events = new List<string>();
        var drain = new ApplicationShutdownDrain(
            () => Task.FromResult(ShutdownStepResult.Failed("private cleanup message", diagnostic)),
            () => Task.CompletedTask,
            () => { events.Add("stop"); return Task.CompletedTask; },
            () => events.Add("dispose"));

        var result = await drain.DrainAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDrainFailureStage.Capture, result.FailedStage);
        Assert.Equal("private cleanup message", result.FailureMessage);
        Assert.Same(diagnostic, result.CaptureDiagnostic);
        Assert.Null(result.Exception);
        Assert.Empty(events);
    }

    [Fact]
    public async Task DrainAsync_TypedReleasedWarningAllowsHostTeardown()
    {
        var diagnostic = new CaptureFailure(CaptureFailureKind.Backend, "capture.output.warning", "private output");
        var events = new List<string>();
        var drain = new ApplicationShutdownDrain(
            () => Task.FromResult(ShutdownStepResult.Success(captureDiagnostic: diagnostic)),
            () => Task.CompletedTask,
            () => { events.Add("stop"); return Task.CompletedTask; },
            () => events.Add("dispose"));

        var result = await drain.DrainAsync();

        Assert.True(result.Succeeded);
        Assert.Same(diagnostic, result.CaptureDiagnostic);
        Assert.Equal(["stop", "dispose"], events);
    }

    [Fact]
    public async Task DrainAsync_LaterProducerFailurePreservesEarlierWarningAndExactException()
    {
        var diagnostic = new CaptureFailure(CaptureFailureKind.SourceLost, "capture.warning", "private device");
        var error = new IOException("private export path");
        var calls = new List<string>();
        var drain = new ApplicationShutdownDrain(
            () => Task.FromResult(ShutdownStepResult.Failed(error, diagnostic, "private earlier warning")),
            () => Task.CompletedTask,
            () => { calls.Add("stop"); return Task.CompletedTask; },
            () => calls.Add("dispose"));

        var result = await drain.DrainAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDrainFailureStage.Capture, result.FailedStage);
        Assert.Same(error, result.Exception);
        Assert.Equal(error.Message, result.FailureMessage);
        Assert.Equal("private earlier warning", result.WarningMessage);
        Assert.Same(diagnostic, result.CaptureDiagnostic);
        Assert.Empty(calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainAsync_UnclassifiedCaptureExceptionStaysUnclassified(bool synchronous)
    {
        var error = new IOException("capture.webcam.view-release-uncertain private forged message");
        var calls = new List<string>();
        var drain = new ApplicationShutdownDrain(
            () => synchronous ? throw error : Task.FromException<ShutdownStepResult>(error),
            () => Task.CompletedTask,
            () => { calls.Add("stop"); return Task.CompletedTask; },
            () => calls.Add("dispose"));

        var result = await drain.DrainAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDrainFailureStage.Capture, result.FailedStage);
        Assert.Same(error, result.Exception);
        Assert.Null(result.CaptureDiagnostic);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task DrainAsync_CanceledCaptureTaskDoesNotReleaseHost()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var calls = new List<string>();
        var drain = new ApplicationShutdownDrain(
            () => Task.FromCanceled<ShutdownStepResult>(cancellation.Token),
            () => Task.CompletedTask,
            () => { calls.Add("stop"); return Task.CompletedTask; },
            () => calls.Add("dispose"));

        var result = await drain.DrainAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDrainFailureStage.Capture, result.FailedStage);
        Assert.IsAssignableFrom<OperationCanceledException>(result.Exception);
        Assert.Null(result.CaptureDiagnostic);
        Assert.Empty(calls);
    }

    [Fact]
    public async Task DrainAsync_StartupFaultStillAwaitsCaptureBeforeHostTeardown()
    {
        var capture = new TaskCompletionSource<ShutdownStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var drain = new ApplicationShutdownDrain(
            () => { events.Add("capture"); return capture.Task; },
            () => { events.Add("startup"); return Task.FromException(new IOException("startup failure")); },
            () => { events.Add("stop"); return Task.CompletedTask; },
            () => events.Add("dispose"));

        var draining = drain.DrainAsync();
        Assert.Equal(["capture", "startup"], events);
        Assert.False(draining.IsCompleted);
        capture.SetResult(ShutdownStepResult.Success());

        Assert.True((await draining).Succeeded);
        Assert.Equal(["capture", "startup", "stop", "dispose"], events);
    }

    [Fact]
    public async Task DrainAsync_AwaitedDisposeFailureRetainsExactErrorAndWarning()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var diagnostic = new CaptureFailure(CaptureFailureKind.Backend, "output.warning", "private output");
        var error = new IOException("private dispose detail");
        var drain = new ApplicationShutdownDrain(
            () => Task.FromResult(ShutdownStepResult.Success("earlier warning", diagnostic)),
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            async () => { entered.SetResult(); await release.Task; throw error; });

        var draining = drain.DrainAsync();
        await entered.Task;
        Assert.False(draining.IsCompleted);
        release.SetResult();
        var result = await draining;

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDrainFailureStage.HostDispose, result.FailedStage);
        Assert.Same(error, result.Exception);
        Assert.Same(diagnostic, result.CaptureDiagnostic);
        Assert.Equal("earlier warning", result.WarningMessage);
    }

    [Fact]
    public async Task DrainAsync_BeginsCaptureImmediately_ThenStopsAndDisposesHost()
    {
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new TaskCompletionSource<ShutdownStepResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new List<string>();
        var drain = new ApplicationShutdownDrain(
            () =>
            {
                events.Add("capture-began");
                return capture.Task;
            },
            () => startup.Task,
            () =>
            {
                events.Add("host-stopped");
                return Task.CompletedTask;
            },
            () => events.Add("host-disposed"));

        var draining = drain.DrainAsync();
        Assert.Equal(["capture-began"], events);

        startup.SetResult();
        capture.SetResult(ShutdownStepResult.Success());
        var result = await draining;

        Assert.True(result.Succeeded);
        Assert.Equal(["capture-began", "host-stopped", "host-disposed"], events);
    }

    [Fact]
    public async Task DrainAsync_CaptureFailure_DoesNotStopOrDisposeHost()
    {
        var hostStopCalled = false;
        var hostDisposeCalled = false;
        var drain = new ApplicationShutdownDrain(
            () => Task.FromResult(ShutdownStepResult.Failed("recording is still active")),
            () => Task.CompletedTask,
            () =>
            {
                hostStopCalled = true;
                return Task.CompletedTask;
            },
            () => hostDisposeCalled = true);

        var result = await drain.DrainAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDrainFailureStage.Capture, result.FailedStage);
        Assert.Contains("recording is still active", result.FailureMessage);
        Assert.False(hostStopCalled);
        Assert.False(hostDisposeCalled);
    }

    [Fact]
    public async Task DrainAsync_HostStopFailure_DoesNotDisposeHost()
    {
        var hostDisposeCalled = false;
        var drain = new ApplicationShutdownDrain(
            () => Task.FromResult(ShutdownStepResult.Success()),
            () => Task.CompletedTask,
            () => throw new IOException("host stop failed"),
            () => hostDisposeCalled = true);

        var result = await drain.DrainAsync();

        Assert.False(result.Succeeded);
        Assert.Equal(ShutdownDrainFailureStage.HostStop, result.FailedStage);
        Assert.Contains("host stop failed", result.FailureMessage);
        Assert.False(hostDisposeCalled);
    }

    [Fact]
    public async Task DrainAsync_CaptureWarningStillDrainsHostAndRemainsVisible()
    {
        var hostStopCalled = false;
        var hostDisposeCalled = false;
        var drain = new ApplicationShutdownDrain(
            () => Task.FromResult(ShutdownStepResult.Success("finalized media was missing")),
            () => Task.CompletedTask,
            () =>
            {
                hostStopCalled = true;
                return Task.CompletedTask;
            },
            () => hostDisposeCalled = true);

        var result = await drain.DrainAsync();

        Assert.True(result.Succeeded);
        Assert.Equal("finalized media was missing", result.WarningMessage);
        Assert.True(hostStopCalled);
        Assert.True(hostDisposeCalled);
    }

    [Fact]
    public async Task DrainAsync_AwaitsAsynchronousHostDisposal()
    {
        var disposeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var drain = new ApplicationShutdownDrain(
            () => Task.FromResult(ShutdownStepResult.Success()),
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            async () =>
            {
                disposeEntered.SetResult();
                await releaseDispose.Task;
            });

        var draining = drain.DrainAsync();
        await disposeEntered.Task;

        Assert.False(draining.IsCompleted);
        releaseDispose.SetResult();
        Assert.True((await draining).Succeeded);
    }
}
