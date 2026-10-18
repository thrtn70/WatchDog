using WatchDog.Core.Capture;

namespace WatchDog.Core.Tests.Capture;

public sealed class CaptureLivenessTrackerTests
{
    private static readonly CaptureLivenessTimingPolicy DefaultPolicy = new(
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(20));

    [Fact]
    public void IdentityRejectsInvalidStreamMeasurementCombinations()
    {
        var invalid = new[]
        {
            (CaptureLivenessStreamKind.SourceVideo,
                CaptureLivenessMeasurementKind.EncodedOutputVideoFrames),
            (CaptureLivenessStreamKind.SourceAudio,
                CaptureLivenessMeasurementKind.EncodedOutputAudioSamples),
            (CaptureLivenessStreamKind.EncodedOutputVideo,
                CaptureLivenessMeasurementKind.SourceDeliveredVideoFrames),
            (CaptureLivenessStreamKind.EncodedOutputAudio,
                CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples),
            (CaptureLivenessStreamKind.AggregateOutput,
                CaptureLivenessMeasurementKind.EncodedOutputVideoFrames),
        };

        foreach (var (streamKind, measurementKind) in invalid)
        {
            Assert.Throws<ArgumentException>(() => Identity(
                stream: new CaptureLivenessStream(streamKind, 0),
                measurementKind: measurementKind));
        }
    }

    [Theory]
    [InlineData(CaptureLivenessStreamKind.SourceVideo,
        CaptureLivenessMeasurementKind.SourceDeliveredVideoFrames)]
    [InlineData(CaptureLivenessStreamKind.SourceAudio,
        CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples)]
    [InlineData(CaptureLivenessStreamKind.EncodedOutputVideo,
        CaptureLivenessMeasurementKind.EncodedOutputVideoFrames)]
    [InlineData(CaptureLivenessStreamKind.EncodedOutputAudio,
        CaptureLivenessMeasurementKind.EncodedOutputAudioSamples)]
    [InlineData(CaptureLivenessStreamKind.AggregateOutput,
        CaptureLivenessMeasurementKind.OutputBytes)]
    public void IdentityAcceptsOnlyTheExplicitStreamMeasurementPairings(
        CaptureLivenessStreamKind streamKind,
        CaptureLivenessMeasurementKind measurementKind)
    {
        var identity = Identity(
            stream: new CaptureLivenessStream(streamKind, 0),
            measurementKind: measurementKind);

        Assert.Equal(streamKind, identity.Stream.Kind);
        Assert.Equal(measurementKind, identity.MeasurementKind);
    }

    [Fact]
    public void ContractsRejectInvalidIdentityTimingStreamAndObservationValues()
    {
        Assert.Throws<ArgumentException>(() => Identity(sessionId: Guid.Empty));
        Assert.Throws<ArgumentException>(() => Identity(requestId: Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => Identity(generation: 0));
        Assert.Throws<ArgumentException>(() => Identity(outputEpoch: Guid.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureLivenessStream(CaptureLivenessStreamKind.SourceVideo, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureLivenessStream(CaptureLivenessStreamKind.AggregateOutput, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureLivenessTimingPolicy(TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureLivenessTimingPolicy(TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureLivenessTimingPolicy(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureLivenessTimingPolicy(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new CaptureLivenessTimingPolicy(
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromSeconds(1),
                TimeSpan.FromMilliseconds(uint.MaxValue)));

        var withoutExecutionDeadline = new CaptureLivenessTimingPolicy(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
        Assert.Null(withoutExecutionDeadline.ProviderExecutionTimeout);

        var identity = Identity();
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CaptureLivenessObservation.Available(identity, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CaptureLivenessObservation.Available(identity, 1, 0, -1));
        Assert.Throws<ArgumentException>(() => new CaptureLivenessObservation(
            identity,
            1,
            0,
            CaptureLivenessTelemetryAvailability.Missing,
            1));
    }

    [Fact]
    public void FirstCounterEstablishesBaselineAndStrictIncreaseBecomesLive()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);

        var baseline = tracker.Observe(Available(time, identity, 1, 100));

        Assert.True(baseline.WasAccepted);
        Assert.Equal(CaptureLivenessStatus.AwaitingProgress, tracker.Snapshot.Status);
        Assert.Equal(100, tracker.Snapshot.AcceptedCounter);
        Assert.Null(tracker.Snapshot.LastProgressTimestamp);

        time.Advance(TimeSpan.FromSeconds(1));
        tracker.Observe(Available(time, identity, 2, 101));

        Assert.Equal(CaptureLivenessStatus.Live, tracker.Snapshot.Status);
        Assert.Equal(101, tracker.Snapshot.AcceptedCounter);
        Assert.Equal(time.GetTimestamp(), tracker.Snapshot.LastProgressTimestamp);
    }

    [Fact]
    public void FreshEqualCounterTimesOutAtExactStartupBoundary()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);
        tracker.Observe(Available(time, identity, 1, 10));

        time.Advance(DefaultPolicy.StartupTimeout - TimeSpan.FromTicks(1));
        tracker.Observe(Available(time, identity, 2, 10));
        Assert.Equal(CaptureLivenessStatus.AwaitingProgress, tracker.Snapshot.Status);

        time.Advance(TimeSpan.FromTicks(1));
        tracker.Observe(Available(time, identity, 3, 10));

        Assert.Equal(CaptureLivenessStatus.StartupTimedOut, tracker.Snapshot.Status);
        Assert.Equal(CaptureLivenessReason.StartupTimeout, tracker.Snapshot.Reason);
    }

    [Fact]
    public void FreshEqualCounterStallsAtExactPostProgressBoundaryAndCanRecover()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);
        tracker.Observe(Available(time, identity, 1, 10));
        tracker.Observe(Available(time, identity, 2, 11));

        time.Advance(DefaultPolicy.StallTimeout - TimeSpan.FromTicks(1));
        tracker.Observe(Available(time, identity, 3, 11));
        Assert.Equal(CaptureLivenessStatus.Live, tracker.Snapshot.Status);

        time.Advance(TimeSpan.FromTicks(1));
        tracker.Observe(Available(time, identity, 4, 11));
        Assert.Equal(CaptureLivenessStatus.Stalled, tracker.Snapshot.Status);
        Assert.Equal(CaptureLivenessReason.StallTimeout, tracker.Snapshot.Reason);

        tracker.Observe(Available(time, identity, 5, 12));
        Assert.Equal(CaptureLivenessStatus.Live, tracker.Snapshot.Status);
        Assert.Equal(CaptureLivenessReason.None, tracker.Snapshot.Reason);
    }

    [Fact]
    public void TelemetrySilenceBecomesUnobservableAtExactBoundary()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);
        tracker.Observe(Available(time, identity, 1, 10));

        time.Advance(DefaultPolicy.TelemetryTimeout - TimeSpan.FromTicks(1));
        tracker.Evaluate(identity);
        Assert.Equal(CaptureLivenessStatus.StartupTimedOut, tracker.Snapshot.Status);

        time.Advance(TimeSpan.FromTicks(1));
        tracker.Evaluate(identity);

        Assert.Equal(CaptureLivenessStatus.Unobservable, tracker.Snapshot.Status);
        Assert.Equal(CaptureLivenessReason.TelemetryTimeout, tracker.Snapshot.Reason);
    }

    [Fact]
    public void MissingAndFailedTelemetryAreDistinctAndDoNotChangeCounterBaseline()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);
        tracker.Observe(Available(time, identity, 1, 10));

        time.Advance(TimeSpan.FromSeconds(1));
        var missingAt = time.GetTimestamp();
        tracker.Observe(Missing(time, identity, 2));
        Assert.Equal(CaptureLivenessStatus.Unobservable, tracker.Snapshot.Status);
        Assert.Equal(CaptureLivenessReason.TelemetryMissing, tracker.Snapshot.Reason);
        Assert.Equal(10, tracker.Snapshot.AcceptedCounter);
        Assert.Equal(missingAt, tracker.Snapshot.LastTelemetryTimestamp);

        time.Advance(TimeSpan.FromSeconds(1));
        tracker.Observe(Failed(time, identity, 3));
        Assert.Equal(CaptureLivenessReason.TelemetryFailed, tracker.Snapshot.Reason);
        Assert.Equal(10, tracker.Snapshot.AcceptedCounter);

        tracker.Observe(Available(time, identity, 4, 11));
        Assert.Equal(CaptureLivenessStatus.Live, tracker.Snapshot.Status);
    }

    [Fact]
    public void ProviderExecutionTimeoutIsDistinctRetainsBaselineAndCanRecover()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);
        tracker.Observe(Available(time, identity, 1, 10));

        time.Advance(TimeSpan.FromSeconds(1));
        tracker.Observe(ProviderExecutionTimedOut(time, identity, 2));

        Assert.Equal(CaptureLivenessStatus.Unobservable, tracker.Snapshot.Status);
        Assert.Equal(CaptureLivenessReason.ProviderExecutionTimeout, tracker.Snapshot.Reason);
        Assert.Equal(10, tracker.Snapshot.AcceptedCounter);

        time.Advance(DefaultPolicy.TelemetryTimeout);
        tracker.Evaluate(identity);
        Assert.Equal(CaptureLivenessReason.ProviderExecutionTimeout, tracker.Snapshot.Reason);

        tracker.Observe(Available(time, identity, 3, 11));
        Assert.Equal(CaptureLivenessStatus.Live, tracker.Snapshot.Status);
        Assert.Equal(CaptureLivenessReason.None, tracker.Snapshot.Reason);
        Assert.Equal(11, tracker.Snapshot.AcceptedCounter);
    }

    [Fact]
    public void DecreasingCounterIsInvalidWithoutPoisoningAcceptedBaseline()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);
        tracker.Observe(Available(time, identity, 1, 100));

        tracker.Observe(Available(time, identity, 2, 99));

        Assert.Equal(CaptureLivenessStatus.InvalidTelemetry, tracker.Snapshot.Status);
        Assert.Equal(CaptureLivenessReason.CounterDecreased, tracker.Snapshot.Reason);
        Assert.Equal(100, tracker.Snapshot.AcceptedCounter);

        tracker.Observe(Available(time, identity, 3, 101));
        Assert.Equal(CaptureLivenessStatus.Live, tracker.Snapshot.Status);
        Assert.Equal(101, tracker.Snapshot.AcceptedCounter);
    }

    [Fact]
    public void ForeignIdentityFieldsCannotMutateState()
    {
        var time = new ManualTimeProvider();
        var current = Identity();
        var tracker = new CaptureLivenessTracker(current, time);
        tracker.Observe(Available(time, current, 1, 10));
        var before = tracker.Snapshot;
        var otherPolicy = new CaptureLivenessTimingPolicy(
            TimeSpan.FromSeconds(11),
            DefaultPolicy.StallTimeout,
            DefaultPolicy.TelemetryTimeout);
        var foreign = new[]
        {
            Identity(sessionId: Guid.NewGuid()),
            Identity(requestId: Guid.NewGuid()),
            Identity(generation: current.Generation + 1),
            Identity(actualTarget: new WindowCaptureTarget(20, new IntPtr(0x2000), "other")),
            Identity(outputEpoch: Guid.NewGuid()),
            Identity(stream: new CaptureLivenessStream(CaptureLivenessStreamKind.SourceVideo, 1)),
            Identity(timingPolicy: otherPolicy),
            Identity(
                stream: new CaptureLivenessStream(CaptureLivenessStreamKind.EncodedOutputVideo, 0),
                measurementKind: CaptureLivenessMeasurementKind.EncodedOutputVideoFrames),
        };

        foreach (var identity in foreign)
        {
            var update = tracker.Observe(Available(time, identity, 2, 11));
            Assert.Equal(CaptureLivenessUpdateDisposition.Ignored, update.Disposition);
            Assert.Same(before, tracker.Snapshot);
        }
    }

    [Fact]
    public void DuplicateAndBackwardSequencesCannotPoisonBaselineOrTelemetryTime()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);
        tracker.Observe(Available(time, identity, 5, 100));
        var before = tracker.Snapshot;

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(tracker.Observe(
            Available(time, identity, 5, 500)).WasAccepted);
        Assert.False(tracker.Observe(
            Failed(time, identity, 4)).WasAccepted);

        Assert.Same(before, tracker.Snapshot);
        tracker.Observe(Available(time, identity, 6, 101));
        Assert.Equal(CaptureLivenessStatus.Live, tracker.Snapshot.Status);
    }

    [Fact]
    public void BackwardProviderTimestampIgnoresObservationWithoutConsumingSequence()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);
        tracker.Observe(Available(time, identity, 1, 10));
        time.Advance(TimeSpan.FromSeconds(5));
        tracker.Evaluate(identity);

        time.SetTimestamp(TimeSpan.FromSeconds(4).Ticks);
        var ignored = tracker.Observe(Available(time, identity, 2, 11));
        Assert.False(ignored.WasAccepted);
        Assert.Equal(1, tracker.Snapshot.LastAcceptedSequence);
        Assert.Equal(10, tracker.Snapshot.AcceptedCounter);

        time.SetTimestamp(TimeSpan.FromSeconds(6).Ticks);
        var accepted = tracker.Observe(Available(time, identity, 2, 11));
        Assert.True(accepted.WasAccepted);
        Assert.Equal(CaptureLivenessStatus.Live, tracker.Snapshot.Status);
    }

    [Fact]
    public void DelayedProgressRemainsUnobservableWhenItsMeasurementIsStale()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);
        tracker.Observe(Available(time, identity, 1, 10));

        time.Advance(TimeSpan.FromSeconds(1));
        var delayedMeasurementTimestamp = time.GetTimestamp();
        time.Advance(DefaultPolicy.TelemetryTimeout);
        tracker.Evaluate(identity);
        Assert.Equal(CaptureLivenessStatus.Unobservable, tracker.Snapshot.Status);

        var delayed = tracker.Observe(Available(
            time,
            identity,
            2,
            11,
            delayedMeasurementTimestamp));

        Assert.True(delayed.WasAccepted);
        Assert.Equal(11, tracker.Snapshot.AcceptedCounter);
        Assert.Equal(delayedMeasurementTimestamp, tracker.Snapshot.LastProgressTimestamp);
        Assert.Equal(CaptureLivenessStatus.Unobservable, tracker.Snapshot.Status);
        Assert.Equal(CaptureLivenessReason.TelemetryTimeout, tracker.Snapshot.Reason);
    }

    [Fact]
    public void FutureOrBackwardMeasurementTimeIsIgnoredWithoutConsumingSequence()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);
        time.Advance(TimeSpan.FromSeconds(2));
        tracker.Observe(Available(time, identity, 1, 10));

        var future = tracker.Observe(Available(
            time,
            identity,
            2,
            11,
            time.GetTimestamp() + 1));
        var backward = tracker.Observe(Available(
            time,
            identity,
            2,
            11,
            time.GetTimestamp() - TimeSpan.FromSeconds(1).Ticks));

        Assert.False(future.WasAccepted);
        Assert.False(backward.WasAccepted);
        Assert.Equal(1, tracker.Snapshot.LastAcceptedSequence);
        Assert.True(tracker.Observe(Available(time, identity, 2, 11)).WasAccepted);
        Assert.Equal(CaptureLivenessStatus.Live, tracker.Snapshot.Status);
    }

    [Fact]
    public void UtcJumpsDoNotAffectTimeoutDecisions()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var tracker = new CaptureLivenessTracker(identity, time);
        tracker.Observe(Available(time, identity, 1, 10));

        time.SetUtcNow(DateTimeOffset.MaxValue);
        tracker.Evaluate(identity);
        Assert.Equal(CaptureLivenessStatus.AwaitingProgress, tracker.Snapshot.Status);

        time.SetUtcNow(DateTimeOffset.MinValue);
        time.Advance(DefaultPolicy.StartupTimeout);
        tracker.Observe(Available(time, identity, 2, 10));
        Assert.Equal(CaptureLivenessStatus.StartupTimedOut, tracker.Snapshot.Status);
    }

    [Fact]
    public void ReplacementResetsBaselineTombstonesOldEpochAndRequiresFrozenFields()
    {
        var time = new ManualTimeProvider();
        var original = Identity();
        var replacement = Identity(outputEpoch: Guid.NewGuid());
        var tracker = new CaptureLivenessTracker(original, time);
        tracker.Observe(Available(time, original, 10, 100));

        var replaced = tracker.ReplaceOutput(original, replacement);

        Assert.True(replaced.WasAccepted);
        Assert.Equal(CaptureLivenessStatus.AwaitingProgress, tracker.Snapshot.Status);
        Assert.Null(tracker.Snapshot.AcceptedCounter);
        Assert.Null(tracker.Snapshot.LastAcceptedSequence);
        Assert.False(tracker.Observe(
            Available(time, original, 11, 101)).WasAccepted);
        Assert.True(tracker.Observe(
            Available(time, replacement, 1, 1)).WasAccepted);

        Assert.False(tracker.ReplaceOutput(replacement, original).WasAccepted);
        Assert.False(tracker.ReplaceOutput(replacement, Identity(
            outputEpoch: Guid.NewGuid(),
            timingPolicy: new CaptureLivenessTimingPolicy(
                TimeSpan.FromSeconds(11),
                DefaultPolicy.StallTimeout,
                DefaultPolicy.TelemetryTimeout))).WasAccepted);
        Assert.Equal(replacement, tracker.Snapshot.Identity);
    }

    [Fact]
    public void DelayedReplacementCannotDisplaceAReplacementThatWonTheCurrentEpoch()
    {
        var time = new ManualTimeProvider();
        var original = Identity();
        var delayed = Identity(outputEpoch: Guid.NewGuid());
        var current = Identity(outputEpoch: Guid.NewGuid());
        var tracker = new CaptureLivenessTracker(original, time);

        Assert.True(tracker.ReplaceOutput(original, current).WasAccepted);
        Assert.False(tracker.ReplaceOutput(original, delayed).WasAccepted);
        Assert.Equal(current, tracker.Snapshot.Identity);
        Assert.True(tracker.Observe(Available(time, current, 1, 1)).WasAccepted);
    }

    [Fact]
    public void StopIsTerminalAndCannotBeResurrected()
    {
        var time = new ManualTimeProvider();
        var identity = Identity();
        var replacement = Identity(outputEpoch: Guid.NewGuid());
        var tracker = new CaptureLivenessTracker(identity, time);
        tracker.Observe(Available(time, identity, 1, 10));

        Assert.True(tracker.Stop(identity).WasAccepted);
        var stopped = tracker.Snapshot;
        Assert.Equal(CaptureLivenessStatus.Stopped, stopped.Status);

        time.Advance(TimeSpan.FromHours(1));
        Assert.False(tracker.Observe(
            Available(time, identity, 2, 11)).WasAccepted);
        Assert.False(tracker.Evaluate(identity).WasAccepted);
        Assert.False(tracker.ReplaceOutput(identity, replacement).WasAccepted);
        Assert.False(tracker.Stop(identity).WasAccepted);
        Assert.Same(stopped, tracker.Snapshot);
    }

    private static CaptureLivenessIdentity Identity(
        Guid? sessionId = null,
        Guid? requestId = null,
        long generation = 1,
        CaptureTarget? actualTarget = null,
        Guid? outputEpoch = null,
        CaptureLivenessStream? stream = null,
        CaptureLivenessMeasurementKind measurementKind =
            CaptureLivenessMeasurementKind.SourceDeliveredVideoFrames,
        CaptureLivenessTimingPolicy? timingPolicy = null) =>
        new(
            sessionId ?? new Guid("10000000-0000-0000-0000-000000000001"),
            requestId ?? new Guid("20000000-0000-0000-0000-000000000002"),
            generation,
            actualTarget ?? new WindowCaptureTarget(10, new IntPtr(0x1000), "window"),
            outputEpoch ?? new Guid("30000000-0000-0000-0000-000000000003"),
            stream ?? new CaptureLivenessStream(CaptureLivenessStreamKind.SourceVideo, 0),
            measurementKind,
            timingPolicy ?? DefaultPolicy);

    private static CaptureLivenessObservation Available(
        ManualTimeProvider time,
        CaptureLivenessIdentity identity,
        long sequence,
        long counter,
        long? observedTimestamp = null) =>
        CaptureLivenessObservation.Available(
            identity,
            sequence,
            observedTimestamp ?? time.GetTimestamp(),
            counter);

    private static CaptureLivenessObservation Missing(
        ManualTimeProvider time,
        CaptureLivenessIdentity identity,
        long sequence) =>
        CaptureLivenessObservation.Missing(identity, sequence, time.GetTimestamp());

    private static CaptureLivenessObservation Failed(
        ManualTimeProvider time,
        CaptureLivenessIdentity identity,
        long sequence) =>
        CaptureLivenessObservation.Failed(identity, sequence, time.GetTimestamp());

    private static CaptureLivenessObservation ProviderExecutionTimedOut(
        ManualTimeProvider time,
        CaptureLivenessIdentity identity,
        long sequence) =>
        CaptureLivenessObservation.ProviderExecutionTimedOut(
            identity,
            sequence,
            time.GetTimestamp());

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
        public void SetTimestamp(long timestamp) => _timestamp = timestamp;
        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
