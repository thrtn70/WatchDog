using WatchDog.Core.Capture;
using WatchDog.Core.Devices;

namespace WatchDog.Core.Tests.Capture;

public sealed class CaptureLifecycleCoordinatorLivenessTests
{
    [Fact]
    public async Task AcceptedFailureStopsWithNoncancelableCleanupAndRetainsManualReservation()
    {
        var stopEntered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Backend
        {
            StopHandler = async (_, token) =>
            {
                stopEntered.SetResult(token);
                await releaseStop.Task;
                return CaptureBackendCommandResult.Success();
            },
        };
        var claims = new ClaimSource();
        await using var coordinator = new CaptureLifecycleCoordinator(backend, claims);
        Assert.True((await coordinator.StartAsync(Request(CaptureInitiation.Manual))).Succeeded);
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);
        using var observerCancellation = new CancellationTokenSource();

        var transition = coordinator.HandleLivenessFailureAsync(
            Candidate(context),
            observerCancellation.Token);
        var cleanupToken = await stopEntered.Task;
        observerCancellation.Cancel();

        Assert.False(cleanupToken.CanBeCanceled);
        Assert.False(transition.IsCompleted);
        releaseStop.SetResult();
        Assert.True((await transition).Succeeded);
        Assert.Equal(CaptureFailureKind.Liveness, coordinator.Snapshot.LastFailure?.Kind);
        Assert.Equal(CaptureLifecycleStatus.Faulted, coordinator.Snapshot.Status);

        var automatic = await coordinator.StartAsync(Request(CaptureInitiation.Automatic));
        Assert.Equal(CaptureCommandDisposition.Rejected, automatic.Disposition);
        Assert.Equal(CaptureFailureKind.ManualPriority, automatic.Failure?.Kind);
    }

    [Fact]
    public async Task ProviderExecutionTimeoutProjectsDedicatedLivenessFailureCode()
    {
        var backend = new Backend();
        var claims = new ClaimSource();
        await using var coordinator = new CaptureLifecycleCoordinator(backend, claims);
        Assert.True((await coordinator.StartAsync(Request(CaptureInitiation.Automatic))).Succeeded);
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var transition = await coordinator.HandleLivenessFailureAsync(Candidate(
            context,
            CaptureLivenessStatus.Unobservable,
            CaptureLivenessReason.ProviderExecutionTimeout));

        Assert.True(transition.Succeeded);
        Assert.Equal(
            "capture.liveness.session-segment.provider-execution-timeout",
            coordinator.Snapshot.LastFailure?.Code);
        Assert.Equal(CaptureFailureKind.Liveness, coordinator.Snapshot.LastFailure?.Kind);
    }

    [Fact]
    public async Task StaleContextAndWrongAudioAdmissionAreIgnoredBeforeClaim()
    {
        var admitted = AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-a");
        var backend = new Backend
        {
            Manifest = new CaptureDeviceManifest([admitted]),
        };
        var claims = new ClaimSource();
        await using var coordinator = new CaptureLifecycleCoordinator(backend, claims);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var staleIdentity = new CaptureLivenessIdentity(
            context.SessionId,
            context.Request.RequestId,
            context.Generation + 1,
            context.ActualTarget,
            Guid.NewGuid(),
            new CaptureLivenessStream(CaptureLivenessStreamKind.EncodedOutputVideo, 0),
            CaptureLivenessMeasurementKind.EncodedOutputVideoFrames,
            Policy());
        Assert.Equal(
            CaptureCommandDisposition.Ignored,
            (await coordinator.HandleLivenessFailureAsync(Candidate(staleIdentity))).Disposition);

        var wrongAudio = Candidate(
            Identity(
                context,
                CaptureLivenessStreamKind.SourceAudio,
                CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples),
            CaptureLivenessPipelineOwner.AdmittedMicrophone,
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-b"));
        Assert.Equal(
            CaptureCommandDisposition.Ignored,
            (await coordinator.HandleLivenessFailureAsync(wrongAudio)).Disposition);
        Assert.Equal(0, claims.Calls);
        Assert.Empty(backend.StopCalls);
    }

    [Fact]
    public async Task CleanupRequiredRetainsContextAndCausalLivenessFailure()
    {
        var stops = new Queue<CaptureBackendCommandResult>(
        [
            CaptureBackendCommandResult.Failed(
                new CaptureFailure(CaptureFailureKind.Backend, "cleanup", "cleanup detail"),
                cleanupRequired: true),
            CaptureBackendCommandResult.Success(),
        ]);
        var backend = new Backend
        {
            StopHandler = (_, _) => Task.FromResult(stops.Dequeue()),
        };
        var claims = new ClaimSource();
        await using var coordinator = new CaptureLifecycleCoordinator(backend, claims);
        await coordinator.StartAsync(Request(CaptureInitiation.Automatic));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var result = await coordinator.HandleLivenessFailureAsync(Candidate(context));

        Assert.Equal(CaptureCommandDisposition.Failed, result.Disposition);
        Assert.Same(context, coordinator.Snapshot.Context);
        var failure = Assert.IsType<CaptureFailure>(coordinator.Snapshot.LastFailure);
        Assert.Equal(CaptureFailureKind.Liveness, failure.Kind);
        Assert.Equal("capture.liveness.session-segment.startup-timeout", failure.Code);
        Assert.Equal(
            "The session-segment progress counter became startup-timeout. Capture stopped because counter " +
            "progress could not be verified. This does not establish source or device binding, " +
            "visual content or change, audible audio, or audio/video synchronization.",
            failure.Message);
        Assert.True(failure.IsRetryable);
        Assert.Equal("cleanup", failure.CleanupFailure?.Code);
        Assert.Equal("cleanup detail", failure.CleanupFailure?.Message);
        Assert.Equal(CaptureHealth.Failed, coordinator.Snapshot.Health);
        Assert.Empty(claims.Completed);

        Assert.True((await coordinator.StopAsync(CaptureInitiation.Automatic)).Succeeded);
        Assert.Equal(CaptureLivenessContext.From(context), Assert.Single(claims.Completed));
    }

    [Fact]
    public async Task UncertainWebcamCleanupRetainsExactLivenessCauseContextAndManualPriority()
    {
        var uncertainty = new CaptureFailure(
            CaptureFailureKind.Backend,
            CaptureFailure.WebcamViewReleaseUncertainCode,
            "Webcam cleanup could not be confirmed.");
        var stops = new Queue<CaptureBackendCommandResult>(
        [
            CaptureBackendCommandResult.Failed(uncertainty, cleanupRequired: true),
            CaptureBackendCommandResult.Success(),
        ]);
        var backend = new Backend
        {
            StopHandler = (_, _) => Task.FromResult(stops.Dequeue()),
        };
        var claims = new ClaimSource();
        await using var coordinator = new CaptureLifecycleCoordinator(backend, claims);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);

        var result = await coordinator.HandleLivenessFailureAsync(Candidate(context));

        Assert.Equal(CaptureCommandDisposition.Failed, result.Disposition);
        Assert.Same(context, result.After.Context);
        var failure = Assert.IsType<CaptureFailure>(result.Failure);
        Assert.Same(failure, result.After.LastFailure);
        Assert.Equal(CaptureFailureKind.Liveness, failure.Kind);
        Assert.Equal("capture.liveness.session-segment.startup-timeout", failure.Code);
        Assert.Equal(
            "The session-segment progress counter became startup-timeout. Capture stopped because counter " +
            "progress could not be verified. This does not establish source or device binding, " +
            "visual content or change, audible audio, or audio/video synchronization.",
            failure.Message);
        Assert.False(failure.IsRetryable);
        Assert.True(failure.HasUncertainWebcamViewRelease);
        Assert.Same(uncertainty, failure.CleanupFailure);
        Assert.Null(failure.CleanupFailure?.CleanupFailure);
        Assert.Empty(claims.Completed);

        var automatic = await coordinator.StartAsync(Request(CaptureInitiation.Automatic));

        Assert.Equal(CaptureCommandDisposition.Rejected, automatic.Disposition);
        Assert.Equal(CaptureFailureKind.ManualPriority, automatic.Failure?.Kind);
        Assert.Same(context, coordinator.Snapshot.Context);
    }

    [Fact]
    public async Task ChangedAudioAdmissionReturnedByClaimIsIgnored()
    {
        var admitted = AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-a");
        var backend = new Backend { Manifest = new CaptureDeviceManifest([admitted]) };
        var claims = new ClaimSource
        {
            Transform = candidate => candidate with
            {
                AdmittedAudioDevice = AdmittedCaptureDevice.Exact(
                    CaptureDeviceRole.Microphone,
                    "mic-b"),
            },
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend, claims);
        await coordinator.StartAsync(Request(CaptureInitiation.Manual));
        var context = Assert.IsType<CaptureSessionContext>(coordinator.Snapshot.Context);
        var candidate = Candidate(
            Identity(
                context,
                CaptureLivenessStreamKind.SourceAudio,
                CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples),
            CaptureLivenessPipelineOwner.AdmittedMicrophone,
            admitted);

        var result = await coordinator.HandleLivenessFailureAsync(candidate);

        Assert.Equal(CaptureCommandDisposition.Ignored, result.Disposition);
        Assert.Equal(1, claims.Calls);
        Assert.Empty(backend.StopCalls);
    }

    private static CaptureLivenessFailureCandidate Candidate(
        CaptureSessionContext context,
        CaptureLivenessStatus status = CaptureLivenessStatus.StartupTimedOut,
        CaptureLivenessReason reason = CaptureLivenessReason.StartupTimeout) =>
        Candidate(Identity(context), status: status, reason: reason);

    private static CaptureLivenessFailureCandidate Candidate(
        CaptureLivenessIdentity identity,
        CaptureLivenessPipelineOwner owner = CaptureLivenessPipelineOwner.SessionSegment,
        AdmittedCaptureDevice? admittedAudioDevice = null,
        CaptureLivenessStatus status = CaptureLivenessStatus.StartupTimedOut,
        CaptureLivenessReason reason = CaptureLivenessReason.StartupTimeout)
    {
        var evidence = new CaptureLivenessSnapshot(
            identity,
            status,
            reason,
            0,
            0,
            null,
            1,
            10);
        return new(
            Guid.NewGuid(),
            owner,
            identity,
            admittedAudioDevice,
            1,
            evidence);
    }

    private static CaptureLivenessIdentity Identity(
        CaptureSessionContext context,
        CaptureLivenessStreamKind streamKind = CaptureLivenessStreamKind.EncodedOutputVideo,
        CaptureLivenessMeasurementKind measurementKind =
            CaptureLivenessMeasurementKind.EncodedOutputVideoFrames) =>
        new(
            context.SessionId,
            context.Request.RequestId,
            context.Generation,
            context.ActualTarget,
            Guid.NewGuid(),
            new CaptureLivenessStream(streamKind, 0),
            measurementKind,
            Policy());

    private static CaptureLivenessTimingPolicy Policy() => new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2));

    private static CaptureRequest Request(CaptureInitiation initiation) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            initiation,
            new WindowCaptureTarget(10, new IntPtr(0x1000), "window"));

    private sealed class ClaimSource : ICaptureLivenessFailureClaimSource
    {
        public int Calls { get; private set; }
        public List<CaptureLivenessContext> Completed { get; } = [];
        public Func<CaptureLivenessFailureCandidate, CaptureLivenessFailureCandidate> Transform { get; init; } =
            candidate => candidate;

        public ValueTask<CaptureLivenessFailureClaim?> TryClaimAsync(
            CaptureLivenessFailureCandidate candidate,
            CancellationToken cancellationToken)
        {
            Calls++;
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<CaptureLivenessFailureClaim?>(new(Transform(candidate)));
        }

        public void CompleteCapture(CaptureLivenessContext context) => Completed.Add(context);
    }

    private sealed class Backend : ICaptureLifecycleBackend
    {
        public CaptureDeviceManifest Manifest { get; init; } = CaptureDeviceManifest.Empty;
        public Func<CaptureSessionContext, CancellationToken, Task<CaptureBackendCommandResult>> StopHandler { get; init; } =
            (_, _) => Task.FromResult(CaptureBackendCommandResult.Success());
        public List<CaptureSessionContext> StopCalls { get; } = [];

        public Task<CaptureBackendStartResult> StartAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(CaptureBackendStartResult.Success(context.RequestedTarget, Manifest));

        public Task<CaptureBackendCommandResult> StopAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken)
        {
            StopCalls.Add(context);
            return StopHandler(context, cancellationToken);
        }

        public Task<CaptureBackendSaveResult> SaveAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(CaptureBackendSaveResult.Success("clip.mp4"));
    }
}
