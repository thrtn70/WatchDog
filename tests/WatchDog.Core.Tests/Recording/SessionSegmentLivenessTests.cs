using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Recording;

namespace WatchDog.Core.Tests.Recording;

public sealed class SessionSegmentLivenessTests
{
    private static readonly SessionSegmentLivenessPolicy Policy = new(
        TimeSpan.FromMilliseconds(100),
        new CaptureLivenessTimingPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(1)));

    [Fact]
    public void MissingPolicyLeavesProductionAdapterInert()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var owner = new SessionSegmentLivenessOwner(
            registry,
            policy: null,
            static (epoch, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(context.Available(1));
            });

        owner.RegisterInitial(Context(), Guid.NewGuid());

        Assert.False(owner.Enabled);
        Assert.Null(owner.Current);
        Assert.Empty(registry.GetPollTargets());
    }

    [Fact]
    public async Task EnabledPolicyRejectsUncorrelatedStartBeforeNativeAllocation()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        using var recorder = new ObsSessionRecorder(
            new SessionRecordingConfig(),
            videoBitrate: 1_500,
            audioBitrate: 96,
            audioTrackPlan: AudioTrackPlan.Create(separateAudioTracks: false),
            logger: NullLogger<ObsSessionRecorder>.Instance,
            livenessRegistry: registry,
            livenessPolicy: Policy);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recorder.StartAsync("unused", game: null));
        Assert.False(recorder.HasActiveResources);
        Assert.Empty(registry.GetPollTargets());
    }

    [Fact]
    public async Task InitialAdmissionPublishesExactSessionSegmentEpoch()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var epoch = Guid.NewGuid();
        Guid observedEpoch = Guid.Empty;
        var owner = new SessionSegmentLivenessOwner(
            registry,
            Policy,
            (readEpoch, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                observedEpoch = readEpoch;
                return ValueTask.FromResult(context.Available(7));
            });

        owner.RegisterInitial(Context(), epoch);
        var target = Assert.Single(registry.GetPollTargets());
        Assert.Equal(CaptureLivenessPipelineOwner.SessionSegment, target.Owner);
        Assert.Equal(epoch, target.Identity.OutputEpoch);
        Assert.Equal(CaptureLivenessStreamKind.EncodedOutputVideo, target.Identity.Stream.Kind);
        Assert.Equal(CaptureLivenessMeasurementKind.EncodedOutputVideoFrames,
            target.Identity.MeasurementKind);

        Assert.Null(await registry.ObserveAsync(target));
        Assert.Equal(epoch, observedEpoch);
    }

    [Fact]
    public async Task ReplacementReservationSuppressesOldReadUntilSuccessorCommits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var originalEpoch = Guid.NewGuid();
        var owner = new SessionSegmentLivenessOwner(
            registry,
            Policy,
            async (epoch, context, cancellationToken) =>
            {
                var measured = context.Available(epoch == originalEpoch ? 3 : 0);
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return measured;
            });
        owner.RegisterInitial(Context(), originalEpoch);
        var original = Assert.Single(registry.GetPollTargets());
        var delayedRead = registry.ObserveAsync(original).AsTask();
        await entered.Task;

        var successorEpoch = Guid.NewGuid();
        var reservation = Assert.IsType<CaptureLivenessReplacementReservation>(
            owner.BeginReplacement(successorEpoch));
        Assert.Empty(registry.GetPollTargets());
        release.SetResult();
        Assert.Null(await delayedRead);

        owner.CommitReplacement(reservation);
        var successor = Assert.Single(registry.GetPollTargets());
        Assert.Equal(successorEpoch, successor.Identity.OutputEpoch);
        Assert.NotEqual(original.RegistrationId, successor.RegistrationId);
    }

    [Fact]
    public void AbortedReplacementLeavesSessionLaneTerminal()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var owner = Owner(registry);
        owner.RegisterInitial(Context(), Guid.NewGuid());
        var reservation = Assert.IsType<CaptureLivenessReplacementReservation>(
            owner.BeginReplacement(Guid.NewGuid()));

        owner.AbortReplacement(reservation);

        Assert.Null(owner.Current);
        Assert.Empty(registry.GetPollTargets());
    }

    [Fact]
    public void RetiredEpochNeverInvokesNativeGetter()
    {
        var reads = 0;
        var context = new CaptureLivenessReadContext(new ManualTimeProvider());

        var reading = ObsSessionRecorder.ReadOutputFrameCounter(
            isReadable: false,
            () =>
            {
                reads++;
                return 42;
            },
            context);

        Assert.Equal(0, reads);
        Assert.Equal(CaptureLivenessTelemetryAvailability.Missing, reading.Availability);
        Assert.Null(reading.Counter);
    }

    [Fact]
    public void ActiveEpochReportsOnlyNonnegativeTotalFrames()
    {
        var context = new CaptureLivenessReadContext(new ManualTimeProvider());

        var available = ObsSessionRecorder.ReadOutputFrameCounter(true, () => 42, context);
        var negative = ObsSessionRecorder.ReadOutputFrameCounter(true, () => -1, context);
        var failed = ObsSessionRecorder.ReadOutputFrameCounter(
            true,
            static () => throw new InvalidOperationException("native getter failed"),
            context);

        Assert.Equal(CaptureLivenessTelemetryAvailability.Available, available.Availability);
        Assert.Equal(42, available.Counter);
        Assert.Equal(CaptureLivenessTelemetryAvailability.Failed, negative.Availability);
        Assert.Equal(CaptureLivenessTelemetryAvailability.Failed, failed.Availability);
    }

    private static SessionSegmentLivenessOwner Owner(CaptureLivenessRegistry registry) =>
        new(
            registry,
            Policy,
            static (epoch, context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(context.Available(0));
            });

    private static CaptureSessionContext Context() =>
        new(
            new Guid("10000000-0000-0000-0000-000000000001"),
            1,
            new CaptureRequest(
                new Guid("20000000-0000-0000-0000-000000000002"),
                DateTimeOffset.UnixEpoch,
                CaptureInitiation.Automatic,
                new WindowCaptureTarget(10, new IntPtr(0x1000), "window")),
            new WindowCaptureTarget(10, new IntPtr(0x1000), "window"),
            new WindowCaptureTarget(10, new IntPtr(0x1000), "window"));

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }
}
