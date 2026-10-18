using WatchDog.Core.Capture;
using WatchDog.Core.Devices;

namespace WatchDog.Core.Tests.Capture;

public sealed class CaptureLivenessRegistryTests
{
    private static readonly CaptureLivenessTimingPolicy Policy = new(
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(10));

    [Fact]
    public void EmptyRegistryIsInert()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());

        Assert.Empty(registry.GetPollTargets());
    }

    [Fact]
    public void RegistrationRejectsUnqualifiedCapabilitiesAndMismatchedAudioAdmission()
    {
        var provider = new QueueProvider(Available(0));
        Assert.Throws<ArgumentException>(() => Registration(
            provider,
            identity: Identity(
                CaptureLivenessStreamKind.SourceVideo,
                CaptureLivenessMeasurementKind.SourceDeliveredVideoFrames)));
        Assert.Throws<ArgumentException>(() => Registration(
            provider,
            identity: Identity(
                CaptureLivenessStreamKind.EncodedOutputAudio,
                CaptureLivenessMeasurementKind.EncodedOutputAudioSamples)));
        Assert.Throws<ArgumentException>(() => Registration(
            provider,
            identity: Identity(
                CaptureLivenessStreamKind.AggregateOutput,
                CaptureLivenessMeasurementKind.OutputBytes)));
        Assert.Throws<ArgumentException>(() => Registration(
            provider,
            CaptureLivenessPipelineOwner.WebcamIntermediate,
            Identity(
                CaptureLivenessStreamKind.EncodedOutputAudio,
                CaptureLivenessMeasurementKind.EncodedOutputAudioSamples)));
        Assert.Throws<ArgumentNullException>(() => Registration(
            provider,
            CaptureLivenessPipelineOwner.AdmittedDesktopAudio,
            Identity(
                CaptureLivenessStreamKind.SourceAudio,
                CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples)));
        Assert.Throws<ArgumentException>(() => Registration(
            provider,
            CaptureLivenessPipelineOwner.AdmittedDesktopAudio,
            Identity(
                CaptureLivenessStreamKind.SourceAudio,
                CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples),
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic")));

        var admitted = Registration(
            provider,
            CaptureLivenessPipelineOwner.AdmittedDesktopAudio,
            Identity(
                CaptureLivenessStreamKind.SourceAudio,
                CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples),
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.DesktopAudio, "speaker"));
        Assert.Equal("speaker", admitted.AdmittedAudioDevice?.RequestedDeviceId);
    }

    [Fact]
    public async Task ReplacementMakesInFlightReadAndOldCandidateStale()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        var blocking = new BlockingProvider();
        var original = Registration(blocking);
        var originalResult = registry.Register(original);
        var originalTarget = Assert.IsType<CaptureLivenessPollTarget>(originalResult.Current);
        var delayedReplacement = Registration(
            new QueueProvider(Available(2)),
            identity: Identity(outputEpoch: Guid.NewGuid()));
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Register(delayedReplacement).Disposition);

        var read = registry.ObserveAsync(originalTarget).AsTask();
        await blocking.Entered.Task;
        var replacement = Registration(
            new QueueProvider(Available(1)),
            identity: Identity(outputEpoch: Guid.NewGuid()));
        var replacementResult = registry.Replace(originalTarget, replacement);
        Assert.Equal(CaptureLivenessRegistrationDisposition.Replaced, replacementResult.Disposition);
        var current = Assert.IsType<CaptureLivenessPollTarget>(replacementResult.Current);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Replace(originalTarget, delayedReplacement).Disposition);

        blocking.Release.SetResult();
        Assert.Null(await read);
        Assert.Equal(replacement.RegistrationId, current.RegistrationId);
        Assert.Equal(current, Assert.Single(registry.GetPollTargets()));
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Ignored,
            registry.Retire(originalTarget).Disposition);
    }

    [Fact]
    public async Task ReplacementReservationSuppressesLaneAndInvalidatesDelayedRead()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var provider = new BlockingProvider();
        var original = Registration(provider);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(original).Current);
        var delayedRead = registry.ObserveAsync(target).AsTask();
        await provider.Entered.Task;
        var replacement = Registration(
            new QueueProvider(Available(1)),
            identity: Identity(outputEpoch: Guid.NewGuid()));

        var reservation = Assert.IsType<CaptureLivenessReplacementReservation>(
            registry.BeginReplace(target, replacement));

        Assert.Empty(registry.GetPollTargets());
        Assert.Null(registry.BeginReplace(target, Registration(
            new QueueProvider(Available(2)),
            identity: Identity(outputEpoch: Guid.NewGuid()))));
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Replace(target, replacement).Disposition);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Ignored,
            registry.Retire(target).Disposition);

        provider.Release.SetResult();
        Assert.Null(await delayedRead);
        var committed = registry.CommitReplace(reservation);
        Assert.Equal(CaptureLivenessRegistrationDisposition.Replaced, committed.Disposition);
        Assert.Equal(replacement.RegistrationId, committed.Current?.RegistrationId);
    }

    [Fact]
    public async Task ReplacementReservationInvalidatesClaimAlreadyReadingFreshTelemetry()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(
            (context, _) => ValueTask.FromResult(context.Missing()),
            async (context, cancellationToken) =>
            {
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return context.Missing();
            });
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(provider)).Current);
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(target));
        var claim = registry.TryClaimAsync(candidate, CancellationToken.None).AsTask();
        await entered.Task;
        var replacement = Registration(
            new QueueProvider(Available(1)),
            identity: Identity(outputEpoch: Guid.NewGuid()));

        var reservation = Assert.IsType<CaptureLivenessReplacementReservation>(
            registry.BeginReplace(target, replacement));
        release.SetResult();

        Assert.Null(await claim);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Replaced,
            registry.CommitReplace(reservation).Disposition);
    }

    [Fact]
    public async Task CaptureFenceClaimedByAnotherLaneRejectsPendingCommit()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var output = Registration(new QueueProvider(Available(1)));
        var outputTarget = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(output).Current);
        var audio = Registration(
            new QueueProvider(Missing, Missing),
            CaptureLivenessPipelineOwner.AdmittedMicrophone,
            Identity(
                CaptureLivenessStreamKind.SourceAudio,
                CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples,
                Guid.NewGuid()),
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-a"));
        var audioTarget = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(audio).Current);
        var replacement = Registration(
            new QueueProvider(Available(2)),
            identity: Identity(outputEpoch: Guid.NewGuid()));
        var reservation = Assert.IsType<CaptureLivenessReplacementReservation>(
            registry.BeginReplace(outputTarget, replacement));
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(audioTarget));

        Assert.NotNull(await registry.TryClaimAsync(candidate, CancellationToken.None));
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.CommitReplace(reservation).Disposition);
        Assert.Empty(registry.GetPollTargets());
    }

    [Fact]
    public async Task CommitPublishesFrozenReplacementAndStartsItsDeadlineAtCommit()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(new QueueProvider(Available(1)))).Current);
        time.Advance(TimeSpan.FromHours(1));
        var replacement = Registration(
            new QueueProvider(Available(10), Available(10), Available(10)),
            identity: Identity(outputEpoch: Guid.NewGuid()));

        var reservation = Assert.IsType<CaptureLivenessReplacementReservation>(
            registry.BeginReplace(target, replacement));
        time.Advance(TimeSpan.FromHours(1));

        Assert.Empty(registry.GetPollTargets());
        var committed = registry.CommitReplace(reservation);
        var replacementTarget = Assert.IsType<CaptureLivenessPollTarget>(committed.Current);
        Assert.Equal(replacement, reservation.Replacement);
        Assert.Equal(replacement.RegistrationId, replacementTarget.RegistrationId);
        Assert.Null(await registry.ObserveAsync(replacementTarget));

        time.Advance(Policy.StartupTimeout - TimeSpan.FromTicks(1));
        Assert.Null(await registry.ObserveAsync(replacementTarget));
        time.Advance(TimeSpan.FromTicks(1));
        var timedOut = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(replacementTarget));
        Assert.Equal(CaptureLivenessStatus.StartupTimedOut, timedOut.Evidence.Status);
    }

    [Fact]
    public void WrongAndStaleReservationTokensCannotMutateLane()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        using var otherRegistry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(new QueueProvider(Available(1)))).Current);
        var otherTarget = Assert.IsType<CaptureLivenessPollTarget>(otherRegistry.Register(
            Registration(new QueueProvider(Available(1)))).Current);
        var replacement = Registration(
            new QueueProvider(Available(2)),
            identity: Identity(outputEpoch: Guid.NewGuid()));
        var otherReplacement = Registration(
            new QueueProvider(Available(2)),
            identity: Identity(outputEpoch: Guid.NewGuid()));
        var reservation = Assert.IsType<CaptureLivenessReplacementReservation>(
            registry.BeginReplace(target, replacement));
        var wrong = Assert.IsType<CaptureLivenessReplacementReservation>(
            otherRegistry.BeginReplace(otherTarget, otherReplacement));

        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Ignored,
            registry.CommitReplace(wrong).Disposition);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Ignored,
            registry.AbortReplace(wrong).Disposition);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Replaced,
            registry.CommitReplace(reservation).Disposition);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Ignored,
            registry.CommitReplace(reservation).Disposition);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Ignored,
            registry.AbortReplace(reservation).Disposition);
        Assert.Equal(replacement.RegistrationId, Assert.Single(registry.GetPollTargets()).RegistrationId);
    }

    [Fact]
    public void AbortingReservationTerminallyRetiresBothEpochs()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var original = Registration(new QueueProvider(Available(1)));
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(original).Current);
        var replacement = Registration(
            new QueueProvider(Available(2)),
            identity: Identity(outputEpoch: Guid.NewGuid()));
        var reservation = Assert.IsType<CaptureLivenessReplacementReservation>(
            registry.BeginReplace(target, replacement));

        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Retired,
            registry.AbortReplace(reservation).Disposition);
        Assert.Empty(registry.GetPollTargets());
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Ignored,
            registry.Retire(target).Disposition);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Ignored,
            registry.CommitReplace(reservation).Disposition);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Register(replacement).Disposition);
        Assert.Null(registry.BeginReplace(target, replacement));
    }

    [Fact]
    public void CaptureCompletionInvalidatesPendingReservation()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var original = Registration(new QueueProvider(Available(1)));
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(original).Current);
        var replacement = Registration(
            new QueueProvider(Available(2)),
            identity: Identity(outputEpoch: Guid.NewGuid()));
        var reservation = Assert.IsType<CaptureLivenessReplacementReservation>(
            registry.BeginReplace(target, replacement));

        registry.CompleteCapture(CaptureLivenessContext.From(original.Identity));

        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Ignored,
            registry.CommitReplace(reservation).Disposition);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Ignored,
            registry.AbortReplace(reservation).Disposition);
        Assert.Empty(registry.GetPollTargets());
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Register(replacement).Disposition);
    }

    [Fact]
    public async Task CandidateFromReplacedEpochCannotBeClaimed()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var original = Registration(new QueueProvider(Missing));
        var originalTarget = Assert.IsType<CaptureLivenessPollTarget>(
            registry.Register(original).Current);
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(originalTarget));
        var replacement = Registration(
            new QueueProvider(Available(1)),
            identity: Identity(outputEpoch: Guid.NewGuid()));
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Replaced,
            registry.Replace(originalTarget, replacement).Disposition);

        Assert.Null(await registry.TryClaimAsync(candidate, CancellationToken.None));
    }

    [Fact]
    public async Task FreshClaimRejectsRecoveryWhileWaiting()
    {
        var time = new ManualTimeProvider();
        var provider = new QueueProvider(
            Available(10),
            Available(10),
            Available(11));
        using var registry = new CaptureLivenessRegistry(time);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(provider)).Current);

        Assert.Null(await registry.ObserveAsync(target));
        time.Advance(Policy.StartupTimeout);
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(target));

        Assert.Null(await registry.TryClaimAsync(candidate, CancellationToken.None));
        Assert.Single(registry.GetPollTargets());
    }

    [Fact]
    public async Task DelayedIncreasingReadPastTelemetryDeadlineRemainsUnobservable()
    {
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(
            (context, _) => ValueTask.FromResult(context.Available(10)),
            async (context, cancellationToken) =>
            {
                var measured = context.Available(11);
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return measured;
            });
        using var registry = new CaptureLivenessRegistry(time);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(provider)).Current);
        Assert.Null(await registry.ObserveAsync(target));

        var delayed = registry.ObserveAsync(target).AsTask();
        await entered.Task;
        time.Advance(Policy.TelemetryTimeout);
        release.SetResult();
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(await delayed);

        Assert.Equal(CaptureLivenessStatus.Unobservable, candidate.Evidence.Status);
        Assert.Equal(CaptureLivenessReason.TelemetryTimeout, candidate.Evidence.Reason);
    }

    [Fact]
    public async Task FutureAndBackwardMeasurementsDoNotCorruptAcceptedBaseline()
    {
        var time = new ManualTimeProvider();
        time.SetTimestamp(TimeSpan.FromSeconds(10).Ticks);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(
            (context, _) => ValueTask.FromResult(context.Available(10)),
            async (context, cancellationToken) =>
            {
                var future = context.Available(50);
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return future;
            },
            (context, _) => ValueTask.FromResult(context.Available(60)),
            (context, _) => ValueTask.FromResult(context.Available(11)));
        using var registry = new CaptureLivenessRegistry(time);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(provider)).Current);
        Assert.Null(await registry.ObserveAsync(target));

        time.SetTimestamp(TimeSpan.FromSeconds(12).Ticks);
        var futureRead = registry.ObserveAsync(target).AsTask();
        await entered.Task;
        time.SetTimestamp(TimeSpan.FromSeconds(11).Ticks);
        release.SetResult();
        Assert.Null(await futureRead);

        time.SetTimestamp(TimeSpan.FromSeconds(9).Ticks);
        Assert.Null(await registry.ObserveAsync(target));
        time.SetTimestamp(TimeSpan.FromSeconds(13).Ticks);
        Assert.Null(await registry.ObserveAsync(target));
    }

    [Fact]
    public async Task DelayedClaimCannotInventFreshRecovery()
    {
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(
            (context, _) => ValueTask.FromResult(context.Missing()),
            async (context, cancellationToken) =>
            {
                var measured = context.Available(1);
                entered.SetResult();
                await release.Task.WaitAsync(cancellationToken);
                return measured;
            });
        using var registry = new CaptureLivenessRegistry(time);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(provider)).Current);
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(target));

        var claim = registry.TryClaimAsync(candidate, CancellationToken.None).AsTask();
        await entered.Task;
        time.Advance(Policy.TelemetryTimeout);
        release.SetResult();
        var accepted = Assert.IsType<CaptureLivenessFailureClaim>(await claim);
        Assert.Equal(CaptureLivenessReason.TelemetryTimeout, accepted.Candidate.Evidence.Reason);
    }

    [Fact]
    public async Task ClaimedFailureRemainsTerminalAfterRetireAndCompletion()
    {
        var time = new ManualTimeProvider();
        var provider = new QueueProvider(
            Missing,
            Missing);
        using var registry = new CaptureLivenessRegistry(time);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(provider)).Current);
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(target));

        Assert.NotNull(await registry.TryClaimAsync(candidate, CancellationToken.None));
        var replacement = Registration(
            new QueueProvider(Available(1)),
            identity: Identity(outputEpoch: Guid.NewGuid()));
        Assert.Null(registry.BeginReplace(target, replacement));
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Replace(target, replacement).Disposition);

        Assert.True(registry.Retire(target).Succeeded);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Register(replacement).Disposition);
        var distinct = Registration(
            new QueueProvider(Available(1)),
            identity: Identity(sessionId: Guid.NewGuid(), outputEpoch: Guid.NewGuid()));
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Registered,
            registry.Register(distinct).Disposition);
        registry.CompleteCapture(CaptureLivenessContext.From(target.Identity));
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Register(replacement).Disposition);
    }

    [Fact]
    public void RetiredLaneRejectsPlainRegistrationWithUnseenEpoch()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var original = Registration(new QueueProvider(Available(1)));
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(original).Current);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Retired,
            registry.Retire(target).Disposition);
        var unseenEpoch = Registration(
            new QueueProvider(Available(2)),
            identity: Identity(outputEpoch: Guid.NewGuid()));

        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Register(unseenEpoch).Disposition);
        Assert.Empty(registry.GetPollTargets());
    }

    [Fact]
    public void CompletedCaptureIsTerminalWhileDistinctContextsRemainAdmissible()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var original = Registration(new QueueProvider(Available(1)));
        Assert.True(registry.Register(original).Succeeded);

        registry.CompleteCapture(CaptureLivenessContext.From(original.Identity));

        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Register(original).Disposition);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Registered,
            registry.Register(Registration(
                new QueueProvider(Available(1)),
                identity: Identity(sessionId: Guid.NewGuid()))).Disposition);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Registered,
            registry.Register(Registration(
                new QueueProvider(Available(1)),
                identity: Identity(generation: 2))).Disposition);
    }

    [Fact]
    public async Task InFlightReadBeforeCompletionCannotReattachOrRecreateLane()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var provider = new BlockingProvider();
        var original = Registration(provider);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(original).Current);
        var read = registry.ObserveAsync(target).AsTask();
        await provider.Entered.Task;

        registry.CompleteCapture(CaptureLivenessContext.From(original.Identity));
        provider.Release.SetResult();

        Assert.Null(await read);
        Assert.Empty(registry.GetPollTargets());
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Register(original).Disposition);
    }

    [Fact]
    public void AudioEpochReplacementCannotChangeAdmittedDeviceIdentity()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var audioIdentity = Identity(
            CaptureLivenessStreamKind.SourceAudio,
            CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples);
        var original = Registration(
            new QueueProvider(Available(1)),
            CaptureLivenessPipelineOwner.AdmittedMicrophone,
            audioIdentity,
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-a"));
        Assert.True(registry.Register(original).Succeeded);
        var originalTarget = Assert.Single(registry.GetPollTargets());
        var replacement = Registration(
            new QueueProvider(Available(1)),
            CaptureLivenessPipelineOwner.AdmittedMicrophone,
            Identity(
                CaptureLivenessStreamKind.SourceAudio,
                CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples,
                Guid.NewGuid()),
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-b"));

        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Replace(originalTarget, replacement).Disposition);
    }

    [Fact]
    public async Task AudioClaimFencesOutputReplacementForSameCapture()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        var audioDevice = AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-a");
        var audio = Registration(
            new QueueProvider(Missing, Missing),
            CaptureLivenessPipelineOwner.AdmittedMicrophone,
            Identity(
                CaptureLivenessStreamKind.SourceAudio,
                CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples),
            audioDevice);
        var audioTarget = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(audio).Current);
        var output = Registration(
            new QueueProvider(Available(1)),
            CaptureLivenessPipelineOwner.SessionSegment,
            Identity(outputEpoch: Guid.NewGuid()));
        var outputTarget = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(output).Current);
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(audioTarget));
        Assert.NotNull(await registry.TryClaimAsync(candidate, CancellationToken.None));

        var outputReplacement = Registration(
            new QueueProvider(Available(2)),
            CaptureLivenessPipelineOwner.SessionSegment,
            Identity(outputEpoch: Guid.NewGuid()));
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Replace(outputTarget, outputReplacement).Disposition);
    }

    [Fact]
    public async Task DefaultProviderReadingIsNormalizedToFailedTelemetry()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(new QueueProvider(_ => default))).Current);

        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(target));

        Assert.Equal(CaptureLivenessStatus.Unobservable, candidate.Evidence.Status);
        Assert.Equal(CaptureLivenessReason.TelemetryFailed, candidate.Evidence.Reason);
        var completed = Assert.Single(
            registry.SnapshotReadDiagnostics().Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed);
        Assert.Equal(CaptureLivenessReadCompletionOutcome.ReturnedInvalid, completed.CompletionOutcome);
        Assert.Equal(
            CaptureLivenessReadPostDisposition.ObservationApplied,
            completed.PostReadDisposition);
    }

    [Fact]
    public async Task ProviderExceptionIsRecordedAsFaultedWithoutChangingAppliedFailureSemantics()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(new ScriptedProvider(
                (_, _) => throw new InvalidOperationException("private provider detail")))).Current);

        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(target));

        Assert.Equal(CaptureLivenessReason.TelemetryFailed, candidate.Evidence.Reason);
        var completed = Assert.Single(
            registry.DrainReadDiagnostics().Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed);
        Assert.Equal(CaptureLivenessReadCompletionOutcome.Faulted, completed.CompletionOutcome);
        Assert.Equal(
            CaptureLivenessReadPostDisposition.ObservationApplied,
            completed.PostReadDisposition);
    }

    [Fact]
    public async Task ReadDiagnosticsCorrelateObservationAndClaimWithoutTargetOrDeviceMetadata()
    {
        var time = new ManualTimeProvider();
        time.SetTimestamp(42);
        using var registry = new CaptureLivenessRegistry(time);
        var registration = Registration(new QueueProvider(Missing, Missing));
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(registration).Current);

        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(target));
        Assert.NotNull(await registry.TryClaimAsync(candidate, CancellationToken.None));

        var snapshot = registry.SnapshotReadDiagnostics();
        Assert.Equal(0, snapshot.DroppedCount);
        Assert.Equal(4, snapshot.Events.Count);
        Assert.Equal(
            [
                CaptureLivenessReadEventKind.Started,
                CaptureLivenessReadEventKind.Completed,
                CaptureLivenessReadEventKind.Started,
                CaptureLivenessReadEventKind.Completed,
            ],
            snapshot.Events.Select(diagnostic => diagnostic.Kind));
        Assert.Equal(
            [
                CaptureLivenessReadPhase.Observation,
                CaptureLivenessReadPhase.Observation,
                CaptureLivenessReadPhase.ClaimRevalidation,
                CaptureLivenessReadPhase.ClaimRevalidation,
            ],
            snapshot.Events.Select(diagnostic => diagnostic.Phase));
        Assert.Equal(2, snapshot.Events.Select(diagnostic => diagnostic.ReadId).Distinct().Count());
        Assert.All(snapshot.Events, diagnostic =>
        {
            Assert.Equal(registration.RegistrationId, diagnostic.RegistrationId);
            Assert.Equal(registration.Owner, diagnostic.Owner);
            Assert.Equal(registration.Identity.SessionId, diagnostic.SessionId);
            Assert.Equal(registration.Identity.RequestId, diagnostic.RequestId);
            Assert.Equal(registration.Identity.Generation, diagnostic.Generation);
            Assert.Equal(registration.Identity.OutputEpoch, diagnostic.OutputEpoch);
            Assert.Equal(registration.Identity.Stream, diagnostic.Stream);
            Assert.Equal(registration.Identity.MeasurementKind, diagnostic.MeasurementKind);
            Assert.Null(diagnostic.ConfiguredExecutionTimeout);
            Assert.Equal(42, diagnostic.RegistryTimestamp);
        });
        var completedDiagnostics = snapshot.Events
            .Where(diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed)
            .ToArray();
        Assert.All(completedDiagnostics, diagnostic =>
        {
            Assert.Equal(
                CaptureLivenessReadCompletionOutcome.ReturnedMissing,
                diagnostic.CompletionOutcome);
            Assert.False(diagnostic.CallerCancellationRequested);
        });
        Assert.Equal(
            CaptureLivenessReadPostDisposition.ObservationApplied,
            completedDiagnostics[0].PostReadDisposition);
        Assert.Equal(
            CaptureLivenessReadPostDisposition.ClaimAccepted,
            completedDiagnostics[1].PostReadDisposition);

        var drained = registry.DrainReadDiagnostics();
        Assert.Equal(snapshot.DroppedCount, drained.DroppedCount);
        Assert.Equal(
            snapshot.Events.Select(diagnostic => diagnostic.Sequence),
            drained.Events.Select(diagnostic => diagnostic.Sequence));
        Assert.Empty(registry.SnapshotReadDiagnostics().Events);
        Assert.Equal(0, registry.SnapshotReadDiagnostics().DroppedCount);
    }

    [Fact]
    public async Task BoundedReadDiagnosticsRetainDeterministicTailAndDroppedCount()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider(), readDiagnosticCapacity: 3);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(new QueueProvider(Available(1), Available(2)))).Current);

        Assert.Null(await registry.ObserveAsync(target));
        Assert.Null(await registry.ObserveAsync(target));

        var snapshot = registry.SnapshotReadDiagnostics();
        Assert.Equal(1, snapshot.DroppedCount);
        Assert.Equal(3, snapshot.Events.Count);
        Assert.Equal(
            [
                CaptureLivenessReadEventKind.Completed,
                CaptureLivenessReadEventKind.Started,
                CaptureLivenessReadEventKind.Completed,
            ],
            snapshot.Events.Select(diagnostic => diagnostic.Kind));
        Assert.Equal(
            snapshot.Events.OrderBy(diagnostic => diagnostic.Sequence),
            snapshot.Events);
        Assert.Equal(snapshot.Events[1].ReadId, snapshot.Events[2].ReadId);
        Assert.NotEqual(snapshot.Events[0].ReadId, snapshot.Events[1].ReadId);

        var drained = registry.DrainReadDiagnostics();
        Assert.Equal(
            snapshot.Events.Select(diagnostic => diagnostic.Sequence),
            drained.Events.Select(diagnostic => diagnostic.Sequence));
        Assert.Equal(1, drained.DroppedCount);
        var empty = registry.DrainReadDiagnostics();
        Assert.Empty(empty.Events);
        Assert.Equal(0, empty.DroppedCount);
    }

    [Fact]
    public async Task DrainDuringBlockedReadStartsCompletionInNextBatchWithContinuousSequence()
    {
        using var registry = new CaptureLivenessRegistry(
            new ManualTimeProvider(),
            readDiagnosticCapacity: 2);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(
            (context, _) => ValueTask.FromResult(context.Available(1)),
            async (context, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                return context.Available(2);
            });
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(provider)).Current);
        Assert.Null(await registry.ObserveAsync(target));

        var blockedRead = registry.ObserveAsync(target).AsTask();
        await entered.Task;
        var firstBatch = registry.DrainReadDiagnostics();

        Assert.Equal(1, firstBatch.DroppedCount);
        Assert.Equal(
            [CaptureLivenessReadEventKind.Completed, CaptureLivenessReadEventKind.Started],
            firstBatch.Events.Select(diagnostic => diagnostic.Kind));
        var blockedStarted = firstBatch.Events[^1];
        Assert.False(blockedRead.IsCompleted);

        release.TrySetResult();
        Assert.Null(await blockedRead);
        var secondBatch = registry.DrainReadDiagnostics();

        Assert.Equal(0, secondBatch.DroppedCount);
        var blockedCompleted = Assert.Single(secondBatch.Events);
        Assert.Equal(CaptureLivenessReadEventKind.Completed, blockedCompleted.Kind);
        Assert.Equal(blockedStarted.ReadId, blockedCompleted.ReadId);
        Assert.Equal(blockedStarted.Sequence + 1, blockedCompleted.Sequence);
        Assert.Empty(registry.SnapshotReadDiagnostics().Events);
        Assert.Equal(0, registry.SnapshotReadDiagnostics().DroppedCount);
    }

    [Fact]
    public async Task InFlightReadCompletesDiagnosticsAfterRegistryDisposalAndRemainsDrainable()
    {
        var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var provider = new BlockingProvider();
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(provider)).Current);

        var observe = registry.ObserveAsync(target).AsTask();
        await provider.Entered.Task;
        registry.Dispose();
        provider.Release.TrySetResult();

        Assert.Null(await observe);
        var diagnostics = registry.DrainReadDiagnostics();
        Assert.Equal(
            [CaptureLivenessReadEventKind.Started, CaptureLivenessReadEventKind.Completed],
            diagnostics.Events.Select(diagnostic => diagnostic.Kind));
        var completed = diagnostics.Events[^1];
        Assert.Equal(CaptureLivenessReadCompletionOutcome.ReturnedMissing, completed.CompletionOutcome);
        Assert.Equal(CaptureLivenessReadPostDisposition.RejectedStale, completed.PostReadDisposition);
        Assert.Empty(registry.SnapshotReadDiagnostics().Events);
    }

    [Fact]
    public async Task TimelyProviderReadWithExecutionDeadlineRemainsAvailable()
    {
        using var registry = new CaptureLivenessRegistry(TimeProvider.System);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(
                new QueueProvider(Available(1)),
                identity: Identity(timingPolicy: ExecutionPolicy(TimeSpan.FromSeconds(1))))).Current);

        Assert.Null(await registry.ObserveAsync(target));
    }

    [Fact]
    public async Task NullExecutionDeadlineDoesNotCreateProviderCancellationToken()
    {
        using var registry = new CaptureLivenessRegistry(TimeProvider.System);
        var providerTokenCanBeCanceled = true;
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(new ScriptedProvider((context, cancellationToken) =>
            {
                providerTokenCanBeCanceled = cancellationToken.CanBeCanceled;
                return ValueTask.FromResult(context.Available(1));
            }))).Current);

        Assert.Null(await registry.ObserveAsync(target, CancellationToken.None));
        Assert.False(providerTokenCanBeCanceled);
        var diagnostics = registry.DrainReadDiagnostics();
        Assert.DoesNotContain(
            diagnostics.Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.DeadlineReached);
        var completed = Assert.Single(
            diagnostics.Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed);
        Assert.Null(completed.ConfiguredExecutionTimeout);
        Assert.Equal(CaptureLivenessReadCompletionOutcome.ReturnedAvailable, completed.CompletionOutcome);
    }

    [Fact]
    public async Task CallerCancellationWithNullExecutionDeadlineDrainsAndDoesNotMutateTracker()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        using var callerCancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerSawCallerToken = false;
        var provider = new ScriptedProvider(
            async (context, cancellationToken) =>
            {
                providerSawCallerToken = cancellationToken == callerCancellation.Token;
                entered.TrySetResult();
                await release.Task;
                return context.Available(10);
            },
            (context, _) => ValueTask.FromResult(context.Available(5)));
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(provider)).Current);

        var observe = registry.ObserveAsync(target, callerCancellation.Token).AsTask();
        await entered.Task;
        callerCancellation.Cancel();
        Assert.False(observe.IsCompleted);
        Assert.DoesNotContain(
            registry.SnapshotReadDiagnostics().Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed);

        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observe);

        Assert.True(providerSawCallerToken);
        var diagnostics = registry.DrainReadDiagnostics();
        Assert.DoesNotContain(
            diagnostics.Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.DeadlineReached);
        var completed = Assert.Single(
            diagnostics.Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed);
        Assert.Null(completed.ConfiguredExecutionTimeout);
        Assert.Equal(CaptureLivenessReadCompletionOutcome.ReturnedAvailable, completed.CompletionOutcome);
        Assert.True(completed.CallerCancellationRequested);
        Assert.Equal(CaptureLivenessReadPostDisposition.CallerCanceled, completed.PostReadDisposition);
        Assert.Null(await registry.ObserveAsync(target));
    }

    [Fact]
    public async Task CallerCancellationBeforeDeadlineDrainsObserveAndDoesNotMutateTracker()
    {
        using var registry = new CaptureLivenessRegistry(TimeProvider.System);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(
            async (context, cancellationToken) =>
            {
                using var registration = cancellationToken.Register(
                    () => cancellationObserved.TrySetResult());
                entered.TrySetResult();
                await release.Task;
                return context.Available(10);
            },
            (context, _) => ValueTask.FromResult(context.Available(5)));
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(
                provider,
                identity: Identity(timingPolicy: ExecutionPolicy(TimeSpan.FromSeconds(5))))).Current);
        using var callerCancellation = new CancellationTokenSource();

        var observe = registry.ObserveAsync(target, callerCancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        callerCancellation.Cancel();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(observe.IsCompleted);
        Assert.DoesNotContain(
            registry.SnapshotReadDiagnostics().Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed);

        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => observe.WaitAsync(TimeSpan.FromSeconds(3)));
        var completed = Assert.Single(
            registry.DrainReadDiagnostics().Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed);
        Assert.True(completed.CallerCancellationRequested);
        Assert.Equal(CaptureLivenessReadPostDisposition.CallerCanceled, completed.PostReadDisposition);
        Assert.Equal(CaptureLivenessReadCompletionOutcome.ReturnedAvailable, completed.CompletionOutcome);
        Assert.Null(await registry.ObserveAsync(target));
    }

    [Fact]
    public async Task CallerCancellationBeforeDeadlineDrainsClaimAndLeavesCandidateClaimable()
    {
        using var registry = new CaptureLivenessRegistry(TimeProvider.System);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ScriptedProvider(
            (context, _) => ValueTask.FromResult(context.Missing()),
            async (context, cancellationToken) =>
            {
                using var registration = cancellationToken.Register(
                    () => cancellationObserved.TrySetResult());
                entered.TrySetResult();
                await release.Task;
                return context.Missing();
            },
            (context, _) => ValueTask.FromResult(context.Missing()));
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(
                provider,
                identity: Identity(timingPolicy: ExecutionPolicy(TimeSpan.FromSeconds(5))))).Current);
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(target));
        using var callerCancellation = new CancellationTokenSource();

        var claim = registry.TryClaimAsync(candidate, callerCancellation.Token).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        callerCancellation.Cancel();
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(claim.IsCompleted);

        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => claim.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.NotNull(await registry.TryClaimAsync(candidate, CancellationToken.None));
        Assert.Empty(registry.GetPollTargets());
    }

    [Fact]
    public async Task ProviderExecutionDeadlineCancelsDrainsAndRejectsLateResult()
    {
        using var registry = new CaptureLivenessRegistry(TimeProvider.System);
        var provider = new DeadlineProvider(ignoreCancellation: true);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(
                provider,
                identity: Identity(timingPolicy: ExecutionPolicy(TimeSpan.FromMilliseconds(50))))).Current);

        var observe = registry.ObserveAsync(target).AsTask();
        await provider.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await provider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(observe.IsCompleted);
        var pendingDiagnostics = registry.SnapshotReadDiagnostics();
        Assert.Equal(CaptureLivenessReadEventKind.Started, pendingDiagnostics.Events[0].Kind);
        Assert.DoesNotContain(
            pendingDiagnostics.Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed);

        provider.Release.TrySetResult();
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await observe.WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Equal(CaptureLivenessStatus.Unobservable, candidate.Evidence.Status);
        Assert.Equal(CaptureLivenessReason.ProviderExecutionTimeout, candidate.Evidence.Reason);
        Assert.Null(candidate.Evidence.AcceptedCounter);
        var diagnostics = registry.DrainReadDiagnostics();
        Assert.Equal(
            [
                CaptureLivenessReadEventKind.Started,
                CaptureLivenessReadEventKind.DeadlineReached,
                CaptureLivenessReadEventKind.Completed,
            ],
            diagnostics.Events.Select(diagnostic => diagnostic.Kind));
        var completed = Assert.Single(
            diagnostics.Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed);
        Assert.Equal(CaptureLivenessReadCompletionOutcome.ReturnedAvailable, completed.CompletionOutcome);
        Assert.False(completed.CallerCancellationRequested);
        Assert.Equal(
            CaptureLivenessReadPostDisposition.ObservationApplied,
            completed.PostReadDisposition);
    }

    [Fact]
    public async Task SynchronousReadCompletingOneTickBeforeDeadlineRemainsTimely()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        var timeout = TimeSpan.FromSeconds(1);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(
                new QueueProvider(context =>
                {
                    time.Advance(timeout - TimeSpan.FromTicks(1));
                    return context.Available(1);
                }),
                identity: Identity(timingPolicy: ExecutionPolicy(timeout)))).Current);

        Assert.Null(await registry.ObserveAsync(target));

        var diagnostics = registry.DrainReadDiagnostics();
        Assert.Equal(
            [CaptureLivenessReadEventKind.Started, CaptureLivenessReadEventKind.Completed],
            diagnostics.Events.Select(diagnostic => diagnostic.Kind));
        Assert.DoesNotContain(
            diagnostics.Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.DeadlineReached);
        var completed = diagnostics.Events[^1];
        Assert.Equal(CaptureLivenessReadCompletionOutcome.ReturnedAvailable, completed.CompletionOutcome);
        Assert.Equal(
            CaptureLivenessReadPostDisposition.ObservationApplied,
            completed.PostReadDisposition);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task SynchronousReadCompletingAtOrBeyondExecutionDeadlineRecordsExactlyOneDeadline(
        long ticksBeyondDeadline)
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        var timeout = TimeSpan.FromSeconds(1);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(
                new QueueProvider(context =>
                {
                    time.Advance(timeout + TimeSpan.FromTicks(ticksBeyondDeadline));
                    return context.Available(1);
                }),
                identity: Identity(timingPolicy: ExecutionPolicy(timeout)))).Current);

        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(target));

        Assert.Equal(CaptureLivenessReason.ProviderExecutionTimeout, candidate.Evidence.Reason);
        var diagnostics = registry.DrainReadDiagnostics();
        Assert.Equal(
            [
                CaptureLivenessReadEventKind.Started,
                CaptureLivenessReadEventKind.DeadlineReached,
                CaptureLivenessReadEventKind.Completed,
            ],
            diagnostics.Events.Select(diagnostic => diagnostic.Kind));
        Assert.Single(
            diagnostics.Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.DeadlineReached);
        Assert.All(
            diagnostics.Events,
            diagnostic => Assert.Equal(timeout, diagnostic.ConfiguredExecutionTimeout));
    }

    [Fact]
    public async Task CallerCancellationAfterSynchronousDeadlineWinsOverTimeoutEvidence()
    {
        var time = new ManualTimeProvider();
        var timeout = TimeSpan.FromSeconds(1);
        using var callerCancellation = new CancellationTokenSource();
        using var registry = new CaptureLivenessRegistry(time);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(
                new QueueProvider(context =>
                {
                    time.Advance(timeout);
                    callerCancellation.Cancel();
                    return context.Available(1);
                }),
                identity: Identity(timingPolicy: ExecutionPolicy(timeout)))).Current);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => registry.ObserveAsync(target, callerCancellation.Token).AsTask());

        var diagnostics = registry.DrainReadDiagnostics();
        Assert.Equal(
            [
                CaptureLivenessReadEventKind.Started,
                CaptureLivenessReadEventKind.DeadlineReached,
                CaptureLivenessReadEventKind.Completed,
            ],
            diagnostics.Events.Select(diagnostic => diagnostic.Kind));
        var completed = diagnostics.Events[^1];
        Assert.Equal(CaptureLivenessReadCompletionOutcome.ReturnedAvailable, completed.CompletionOutcome);
        Assert.True(completed.CallerCancellationRequested);
        Assert.Equal(CaptureLivenessReadPostDisposition.CallerCanceled, completed.PostReadDisposition);
        Assert.Single(registry.GetPollTargets());
    }

    [Fact]
    public async Task RetirementDuringSynchronousTimedOutReadCompletesAsStale()
    {
        var time = new ManualTimeProvider();
        var timeout = TimeSpan.FromSeconds(1);
        using var registry = new CaptureLivenessRegistry(time);
        CaptureLivenessPollTarget? target = null;
        target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(
                new QueueProvider(context =>
                {
                    time.Advance(timeout + TimeSpan.FromTicks(1));
                    Assert.Equal(
                        CaptureLivenessRegistrationDisposition.Retired,
                        registry.Retire(target!).Disposition);
                    return context.Available(1);
                }),
                identity: Identity(timingPolicy: ExecutionPolicy(timeout)))).Current);

        Assert.Null(await registry.ObserveAsync(target));

        var diagnostics = registry.DrainReadDiagnostics();
        Assert.Equal(
            [
                CaptureLivenessReadEventKind.Started,
                CaptureLivenessReadEventKind.DeadlineReached,
                CaptureLivenessReadEventKind.Completed,
            ],
            diagnostics.Events.Select(diagnostic => diagnostic.Kind));
        var completed = diagnostics.Events[^1];
        Assert.Equal(CaptureLivenessReadCompletionOutcome.ReturnedAvailable, completed.CompletionOutcome);
        Assert.Equal(CaptureLivenessReadPostDisposition.RejectedStale, completed.PostReadDisposition);
    }

    [Fact]
    public async Task TimelyClaimRecoveryRejectsProviderExecutionTimeoutCandidate()
    {
        using var registry = new CaptureLivenessRegistry(TimeProvider.System);
        var provider = new ScriptedProvider(
            async (context, cancellationToken) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return context.Available(1);
            },
            (context, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(context.Available(1));
            });
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(
                provider,
                identity: Identity(timingPolicy: ExecutionPolicy(TimeSpan.FromMilliseconds(50))))).Current);
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(target).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));

        var claim = await registry.TryClaimAsync(candidate, CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Null(claim);
        Assert.Single(registry.GetPollTargets());
        var completedDiagnostics = registry.DrainReadDiagnostics().Events
            .Where(diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed)
            .ToArray();
        Assert.Equal(2, completedDiagnostics.Length);
        Assert.Equal(
            CaptureLivenessReadPostDisposition.ObservationApplied,
            completedDiagnostics[0].PostReadDisposition);
        Assert.Equal(
            CaptureLivenessReadCompletionOutcome.ReturnedAvailable,
            completedDiagnostics[1].CompletionOutcome);
        Assert.Equal(
            CaptureLivenessReadPostDisposition.ClaimRejectedAfterRecovery,
            completedDiagnostics[1].PostReadDisposition);
    }

    [Fact]
    public async Task RepeatedProviderExecutionTimeoutCanBeClaimedOnce()
    {
        using var registry = new CaptureLivenessRegistry(TimeProvider.System);
        var provider = new ScriptedProvider(
            TimeoutStep,
            TimeoutStep);
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(
            Registration(
                provider,
                identity: Identity(timingPolicy: ExecutionPolicy(TimeSpan.FromMilliseconds(50))))).Current);
        var candidate = Assert.IsType<CaptureLivenessFailureCandidate>(
            await registry.ObserveAsync(target).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));

        var claim = Assert.IsType<CaptureLivenessFailureClaim>(
            await registry.TryClaimAsync(candidate, CancellationToken.None)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Equal(
            CaptureLivenessReason.ProviderExecutionTimeout,
            claim.Candidate.Evidence.Reason);
        Assert.Empty(registry.GetPollTargets());
        var diagnostics = registry.DrainReadDiagnostics();
        Assert.Equal(6, diagnostics.Events.Count);
        Assert.Equal(
            2,
            diagnostics.Events.Count(
                diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.DeadlineReached));
        Assert.Equal(
            [CaptureLivenessReadPhase.Observation, CaptureLivenessReadPhase.ClaimRevalidation],
            diagnostics.Events
                .Where(diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed)
                .Select(diagnostic => diagnostic.Phase));
        var completedDiagnostics = diagnostics.Events
            .Where(diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed)
            .ToArray();
        Assert.All(
            completedDiagnostics,
            diagnostic => Assert.Equal(
                CaptureLivenessReadCompletionOutcome.Canceled,
                diagnostic.CompletionOutcome));
        Assert.Equal(
            CaptureLivenessReadPostDisposition.ObservationApplied,
            completedDiagnostics[0].PostReadDisposition);
        Assert.Equal(
            CaptureLivenessReadPostDisposition.ClaimAccepted,
            completedDiagnostics[1].PostReadDisposition);

        static async ValueTask<CaptureLivenessTelemetryReading> TimeoutStep(
            CaptureLivenessReadContext context,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return context.Available(1);
        }
    }

    [Fact]
    public async Task ReplacementDuringTimedOutReadRejectsLateTimeoutEvidence()
    {
        using var registry = new CaptureLivenessRegistry(TimeProvider.System);
        var provider = new DeadlineProvider(ignoreCancellation: true);
        var original = Registration(
            provider,
            identity: Identity(timingPolicy: ExecutionPolicy(TimeSpan.FromMilliseconds(50))));
        var target = Assert.IsType<CaptureLivenessPollTarget>(registry.Register(original).Current);
        var observe = registry.ObserveAsync(target).AsTask();
        await provider.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var replacement = Registration(
            new QueueProvider(Available(1)),
            identity: Identity(
                outputEpoch: Guid.NewGuid(),
                timingPolicy: original.Identity.TimingPolicy));

        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Replaced,
            registry.Replace(target, replacement).Disposition);
        provider.Release.TrySetResult();

        Assert.Null(await observe.WaitAsync(TimeSpan.FromSeconds(3)));
        var diagnostics = registry.DrainReadDiagnostics();
        Assert.Single(
            diagnostics.Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.DeadlineReached);
        var completed = Assert.Single(
            diagnostics.Events,
            diagnostic => diagnostic.Kind == CaptureLivenessReadEventKind.Completed);
        Assert.Equal(CaptureLivenessReadCompletionOutcome.ReturnedAvailable, completed.CompletionOutcome);
        Assert.Equal(CaptureLivenessReadPostDisposition.RejectedStale, completed.PostReadDisposition);
    }

    private static CaptureLivenessRegistration Registration(
        ICaptureLivenessTelemetryProvider provider,
        CaptureLivenessPipelineOwner owner = CaptureLivenessPipelineOwner.SessionSegment,
        CaptureLivenessIdentity? identity = null,
        AdmittedCaptureDevice? admittedAudioDevice = null) =>
        new(
            Guid.NewGuid(),
            owner,
            identity ?? Identity(),
            TimeSpan.FromMilliseconds(100),
            provider,
            admittedAudioDevice);

    private static CaptureLivenessIdentity Identity(
        CaptureLivenessStreamKind streamKind = CaptureLivenessStreamKind.EncodedOutputVideo,
        CaptureLivenessMeasurementKind measurementKind =
            CaptureLivenessMeasurementKind.EncodedOutputVideoFrames,
        Guid? outputEpoch = null,
        Guid? sessionId = null,
        long generation = 1,
        CaptureLivenessTimingPolicy? timingPolicy = null) =>
        new(
            sessionId ?? new Guid("10000000-0000-0000-0000-000000000001"),
            new Guid("20000000-0000-0000-0000-000000000002"),
            generation,
            new WindowCaptureTarget(10, new IntPtr(0x1000), "window"),
            outputEpoch ?? new Guid("30000000-0000-0000-0000-000000000003"),
            new CaptureLivenessStream(streamKind, 0),
            measurementKind,
            timingPolicy ?? Policy);

    private static CaptureLivenessTimingPolicy ExecutionPolicy(TimeSpan timeout) =>
        new(
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(10),
            timeout);

    private static Func<CaptureLivenessReadContext, CaptureLivenessTelemetryReading> Available(
        long counter) => context => context.Available(counter);

    private static CaptureLivenessTelemetryReading Missing(CaptureLivenessReadContext context) =>
        context.Missing();

    private sealed class QueueProvider(
        params Func<CaptureLivenessReadContext, CaptureLivenessTelemetryReading>[] readings)
        : ICaptureLivenessTelemetryProvider
    {
        private readonly Queue<Func<CaptureLivenessReadContext, CaptureLivenessTelemetryReading>> _readings =
            new(readings);

        public ValueTask<CaptureLivenessTelemetryReading> ReadAsync(
            CaptureLivenessReadContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_readings.Dequeue()(context));
        }
    }

    private sealed class BlockingProvider : ICaptureLivenessTelemetryProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<CaptureLivenessReadContext, CaptureLivenessTelemetryReading> Reading { get; init; } =
            Missing;
        public bool MeasureBeforeRelease { get; init; }

        public async ValueTask<CaptureLivenessTelemetryReading> ReadAsync(
            CaptureLivenessReadContext context,
            CancellationToken cancellationToken)
        {
            Entered.SetResult();
            if (MeasureBeforeRelease)
            {
                var measured = Reading(context);
                await Release.Task.WaitAsync(cancellationToken);
                return measured;
            }

            await Release.Task.WaitAsync(cancellationToken);
            return Reading(context);
        }
    }

    private sealed class DeadlineProvider(bool ignoreCancellation)
        : ICaptureLivenessTelemetryProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<CaptureLivenessTelemetryReading> ReadAsync(
            CaptureLivenessReadContext context,
            CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                () => CancellationObserved.TrySetResult());
            Entered.TrySetResult();
            if (ignoreCancellation)
                await Release.Task;
            else
                await Release.Task.WaitAsync(cancellationToken);
            return context.Available(1);
        }
    }

    private sealed class ScriptedProvider(
        params Func<CaptureLivenessReadContext, CancellationToken,
            ValueTask<CaptureLivenessTelemetryReading>>[] steps)
        : ICaptureLivenessTelemetryProvider
    {
        private readonly Queue<Func<CaptureLivenessReadContext, CancellationToken,
            ValueTask<CaptureLivenessTelemetryReading>>> _steps = new(steps);

        public ValueTask<CaptureLivenessTelemetryReading> ReadAsync(
            CaptureLivenessReadContext context,
            CancellationToken cancellationToken) =>
            _steps.Dequeue()(context, cancellationToken);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
        public void SetTimestamp(long timestamp) => _timestamp = timestamp;
    }
}
