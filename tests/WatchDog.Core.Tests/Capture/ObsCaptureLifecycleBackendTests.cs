using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.App.Services;
using WatchDog.Core.Capture;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Devices;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Highlights;
using WatchDog.Core.Recording;
using WatchDog.Core.Sessions;
using WatchDog.Core.Settings;
using WatchDog.Core.Storage;
using WatchDog.Core.Tests.Helpers;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Tests.Capture;

public sealed class ObsCaptureLifecycleBackendTests
{
    [Fact]
    public async Task BoundProducerWithoutReceiptsRejectsBeforeAcquiringOutputs()
    {
        var engine = new Engine();
        var recorder = new Recorder();
        using var fixture = new Fixture(engine, recorder, bindStartup: true);
        var request = WatchDog.Core.Tutorial.TutorialRecordingPolicy.Create(Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, WebcamContext().Request.Target, fixture.Baseline).Plan!.Request;
        Assert.Equal("capture.segment-receipt.unsupported", fixture.Backend.ValidateStart(request)?.Code);
        var result = await fixture.Backend.StartAsync(new(Guid.NewGuid(), 1, request, request.Target, request.Target));
        Assert.False(result.Succeeded);
        Assert.Equal(0, engine.Initializations);
        Assert.Null(recorder.StartContext);
        Assert.Empty(await fixture.Repository.GetRecentAsync(10));
    }

    [Fact]
    public async Task ProducerReceiptPersistsExactIdentityAndEstimateAndIgnoresDualLegacyEvent()
    {
        var recorder = new ReceiptRecorder();
        using var fixture = new Fixture(new Engine(), recorder);
        var context = Context();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);
        var receipt = recorder.Receipt();
        recorder.EmitReceipt(receipt);
        recorder.EmitSegment(receipt.FilePath);
        recorder.EmitReceipt(receipt);
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
        var media = Assert.Single((await fixture.Repository.GetByIdAsync(context.SessionId))!.MediaSegments);
        Assert.Equal(receipt.MediaId, media.MediaId);
        Assert.Equal(receipt.StartOffset, media.StartOffset);
        Assert.Equal(receipt.EndOffset - receipt.StartOffset, media.Duration);
        Assert.Equal(SessionTimingProvenance.RecorderStopwatchEstimate, media.TimingProvenance);
        Assert.Equal(receipt.OutputEpoch, media.ProducerMetadata!.OutputEpoch);
        Assert.Equal(receipt.ProducerTimingEpoch, media.ProducerMetadata.ProducerTimingEpoch);
    }

    [Fact]
    public async Task ReceiptDuplicateRetriesPendingPersistenceAndConflictCannotReplaceIt()
    {
        var recorder = new ReceiptRecorder();
        using var fixture = new Fixture(new Engine(), recorder);
        var context = Context();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);
        var receipt = recorder.Receipt();
        fixture.BlockIndex();
        recorder.EmitReceipt(receipt);
        Assert.False((await fixture.Backend.StopAsync(context)).Succeeded);
        fixture.UnblockIndex();
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = fixture.Bus.Subscribe<SessionRecordingSegmentSavedEvent>(_ => persisted.TrySetResult());
        recorder.EmitReceipt(receipt with { EndOffset = receipt.EndOffset + TimeSpan.FromSeconds(99) });
        recorder.EmitReceipt(receipt);
        await persisted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
        var media = Assert.Single((await fixture.Repository.GetByIdAsync(context.SessionId))!.MediaSegments);
        Assert.Equal(receipt.MediaId, media.MediaId);
        Assert.Equal(receipt.EndOffset - receipt.StartOffset, media.Duration);
    }

    [Fact]
    public async Task DelayedForeignReceiptCannotAcquireReplacementIdentityOrDegradeIt()
    {
        var recorder = new ReceiptRecorder();
        using var fixture = new Fixture(new Engine(), recorder);
        var old = Context();
        Assert.True((await fixture.Backend.StartAsync(old)).Succeeded);
        var stale = recorder.Receipt();
        Assert.True((await fixture.Backend.StopAsync(old)).Succeeded);
        var current = Context();
        Assert.True((await fixture.Backend.StartAsync(current)).Succeeded);
        var degradations = new List<CaptureHealthDegradedEvent>();
        using var subscription = fixture.Bus.Subscribe<CaptureHealthDegradedEvent>(degradations.Add);
        recorder.EmitReceipt(stale);
        var fresh = recorder.Receipt();
        recorder.EmitReceipt(fresh);
        Assert.True((await fixture.Backend.StopAsync(current)).Succeeded);
        Assert.Null(await fixture.Repository.GetByIdAsync(old.SessionId)); // Empty stopped sessions are removed.
        Assert.Equal(fresh.MediaId, Assert.Single((await fixture.Repository.GetByIdAsync(current.SessionId))!.MediaSegments).MediaId);
        Assert.Empty(degradations);
    }

    [Fact]
    public async Task BoundReceiptsKeepProducerOffsetsWhenRotationCallbacksArriveOutOfOrder()
    {
        var recorder = new ReceiptRecorder();
        using var fixture = new Fixture(new Engine(), recorder, bindStartup: true);
        var request = WatchDog.Core.Tutorial.TutorialRecordingPolicy.Create(Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, WebcamContext().Request.Target, fixture.Baseline).Plan!.Request;
        var context = new CaptureSessionContext(Guid.NewGuid(), 1, request, request.Target, request.Target);
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);
        var first = recorder.Receipt();
        var second = recorder.Receipt() with
        {
            ProducerTimingEpoch = first.ProducerTimingEpoch,
            Sequence = 1,
            StartOffset = first.EndOffset,
            EndOffset = TimeSpan.FromSeconds(15)
        };
        recorder.EmitReceipt(second);
        recorder.EmitReceipt(first);
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
        var segments = (await fixture.Repository.GetByIdAsync(context.SessionId))!.MediaSegments.OrderBy(s => s.Sequence).ToArray();
        Assert.Equal(new[] { first.MediaId, second.MediaId }, segments.Select(s => s.MediaId));
        Assert.Equal(new[] { first.StartOffset, second.StartOffset }, segments.Select(s => s.StartOffset));
        Assert.Equal(new[] { first.EndOffset - first.StartOffset, second.EndOffset - second.StartOffset },
            segments.Select(s => s.Duration!.Value));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopReconcilesFinalizedReceiptBeforeDelayedPublicationAndRetainsPostReleaseWarning(bool warning)
    {
        var recorder = new ReceiptRecorder { FailStopAfterCleanup = warning };
        using var fixture = new Fixture(new Engine(), recorder);
        var context = Context();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);
        var first = recorder.Receipt();
        var second = recorder.Receipt() with
        {
            ProducerTimingEpoch = first.ProducerTimingEpoch,
            Sequence = 1,
            StartOffset = first.EndOffset,
            EndOffset = TimeSpan.FromSeconds(15)
        };
        recorder.RetainReceipt(first);
        recorder.RetainReceipt(second);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = PublishLaterAsync();
        try
        {
            recorder.EmitReceipt(second);
            var stop = await fixture.Backend.StopAsync(context);
            Assert.Equal(!warning, stop.Succeeded);
            Assert.False(stop.CleanupRequired);
            var saved = (await fixture.Repository.GetByIdAsync(context.SessionId))!;
            Assert.Equal(2, saved.MediaSegments.Count);
            Assert.Contains(saved.MediaSegments, segment => segment.MediaId == first.MediaId);
            recorder.FailStopAfterCleanup = false;
            var replacement = Context();
            Assert.True((await fixture.Backend.StartAsync(replacement)).Succeeded);
            release.TrySetResult();
            await delayed;
            Assert.True((await fixture.Backend.StopAsync(replacement)).Succeeded);
            Assert.Equal(2, (await fixture.Repository.GetByIdAsync(context.SessionId))!.MediaSegments.Count);
            Assert.Null(await fixture.Repository.GetByIdAsync(replacement.SessionId));
        }
        finally
        {
            release.TrySetResult();
            await delayed;
        }

        async Task PublishLaterAsync()
        {
            await release.Task;
            recorder.EmitReceipt(first);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void BoundExactTargetKindsPassPreflightWithoutPhysicalEffects(int kind)
    {
        var engine = new Engine();
        using var fixture = new Fixture(engine, bindStartup: true);
        CaptureTarget target = kind switch
        {
            0 => WebcamContext().Request.Target,
            1 => new DisplayCaptureTarget("display-1", "Display 1", 0),
            _ => new RegionCaptureTarget("display-1", new(0, 0, 100, 100), new(0, 0, 1920, 1080)),
        };
        var planned = WatchDog.Core.Tutorial.TutorialRecordingPolicy.Create(Guid.NewGuid(),
            DateTimeOffset.UnixEpoch, target, fixture.Baseline).Plan!;
        Assert.Null(fixture.Backend.ValidateStart(planned.Request));
        Assert.Equal(0, engine.Initializations);
        Assert.Equal(0, engine.StableDisplayResolutions);
        Assert.Equal(0, engine.IndexDisplayResolutions);
        Assert.Null(engine.AppliedPolicy);
    }

    [Fact]
    public async Task DirectBackendStartRejectsForeignHostBeforeAnyEffectsAndUnavailableAuthorityPreservesLegacy()
    {
        var engine = new Engine();
        using var fixture = new Fixture(engine, bindStartup: true);
        var request = CaptureStartupBaselineTests.Request(fixture.Baseline,
            CaptureStartupBaselineTests.Change(fixture.Baseline.Baseline!.Expectation, "host"));
        var result = await fixture.Backend.StartAsync(new(Guid.NewGuid(), 1, request, request.Target, request.Target));
        Assert.Equal("capture.startup-binding.host-changed", result.Failure?.Code);
        Assert.Equal(0, engine.Initializations);
        Assert.Null(engine.AppliedPolicy);
        Assert.Empty(await fixture.Repository.GetRecentAsync(10));
        using var unavailable = new Fixture(new Engine()); // Deliberately inconsistent injected roots.
        Assert.Null(unavailable.Baseline.Baseline);
        Assert.Equal("capture.startup-binding.unavailable",
            unavailable.Backend.ValidateStart(CaptureStartupBaselineTests.Request(fixture.Baseline))?.Code);
        Assert.Null(unavailable.Backend.ValidateStart(Context().Request));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BoundAndLegacyOutputsSelectTheirOwnRootAndKeepExactRequest(bool bound)
    {
        var engine = new Engine();
        var recorder = new ReceiptRecorder();
        var camera = new WebcamRecorder();
        using var fixture = new Fixture(engine, recorder, RecordingMode.ReplayBufferOnly,
            webcamRecorder: camera, captureDefaults: new() { AudioBitrate = -1 }, bindStartup: true,
            distinctLegacyRoot: true);
        var target = WebcamContext().Request.Target;
        var selectedCamera = WebcamContext().Request.Webcam;
        var requestId = Guid.NewGuid();
        var requestedAt = DateTimeOffset.UnixEpoch.AddMinutes(7);
        var request = WatchDog.Core.Tutorial.TutorialRecordingPolicy.Create(requestId, requestedAt,
            target, fixture.Baseline, selectedCamera, explicitWebcamConsent: true).Plan!.Request;
        Assert.Equal(requestId, request.RequestId);
        Assert.Equal(requestedAt, request.RequestedAt);
        Assert.Same(target, request.Target);
        Assert.Same(selectedCamera, request.Webcam);
        if (!bound) request = new CaptureRequest(request.RequestId, request.RequestedAt, request.Initiation,
            request.Target, request.Webcam, request.StartPolicy);
        var context = new CaptureSessionContext(Guid.NewGuid(), 1, request, request.Target, request.Target);
        var result = await fixture.Backend.StartAsync(context);
        Assert.True(result.Succeeded);
        Assert.Equal(64, engine.AppliedPolicy!.Capture.AudioBitrate);
        Assert.False(engine.LastReplayEnabled);
        Assert.Same(request, recorder.StartContext!.Request);
        var canonicalRoot = fixture.Baseline.Baseline!.Expectation.CanonicalSaveRoot;
        Assert.NotEqual(canonicalRoot, fixture.LegacySaveRoot);
        var root = bound ? canonicalRoot : fixture.LegacySaveRoot;
        var directory = Assert.IsType<string>(recorder.OutputDirectory);
        Assert.Equal(Path.Combine(root, "Tutorial", "Sessions"), directory);
        Assert.Equal(Path.Combine(directory, "Camera"), Path.GetDirectoryName(camera.CurrentOutputPath));
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BoundDisplayResolutionRejectsFullRecordDriftWhileLegacyKeepsStableIdentityRules(bool bound)
    {
        var engine = new Engine();
        using var fixture = new Fixture(engine, bindStartup: true);
        var confirmed = new DisplayCaptureTarget("display-1", "Confirmed display", 7);
        var planned = WatchDog.Core.Tutorial.TutorialRecordingPolicy.Create(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            confirmed,
            fixture.Baseline).Plan!.Request;
        var request = bound
            ? planned
            : new CaptureRequest(
                planned.RequestId,
                planned.RequestedAt,
                planned.Initiation,
                planned.Target,
                startPolicy: planned.StartPolicy);
        var context = new CaptureSessionContext(Guid.NewGuid(), 1, request, request.Target, request.Target);

        var result = await fixture.Backend.StartAsync(context);

        Assert.Equal(1, engine.Initializations);
        Assert.Equal(1, engine.StableDisplayResolutions);
        if (bound)
        {
            Assert.False(result.Succeeded);
            Assert.Equal(CaptureFailureKind.SourceLost, result.Failure?.Kind);
            Assert.Equal("capture.startup-binding.target-changed", result.Failure?.Code);
            Assert.Equal(1, engine.Stops);
            Assert.Equal(0, engine.DesktopStarts);
            Assert.Empty(await fixture.Repository.GetRecentAsync(10));
        }
        else
        {
            var resolved = new DisplayCaptureTarget("display-1", "Display 1", 0);
            Assert.True(result.Succeeded);
            Assert.Equal(resolved, result.ActualTarget);
            Assert.Equal(resolved, engine.LastDisplayTarget);
            Assert.Equal(0, engine.Stops);
            Assert.Equal(1, engine.DesktopStarts);
            Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
        }
    }

    [Theory]
    [InlineData("canvas")]
    [InlineData("audio-bitrate")]
    [InlineData("format")]
    [InlineData("buffer")]
    public void MatchingBindingDoesNotBypassChosenPolicyConstraints(string field)
    {
        using var fixture = new Fixture(new Engine(), bindStartup: true);
        var request = CaptureStartupBaselineTests.Request(fixture.Baseline);
        var policy = request.StartPolicy!;
        policy = field switch
        {
            "canvas" => policy with { Capture = policy.Capture with { Fps = policy.Capture.Fps + 1 } },
            "audio-bitrate" => policy with { Capture = policy.Capture with { AudioBitrate = 128 } },
            "format" => policy with { Recording = policy.Recording with { FileFormat = "mkv" } },
            _ => policy with { Buffer = policy.Buffer with { FilenameFormat = "other" } },
        };
        var changed = new CaptureRequest(request.RequestId, request.RequestedAt, request.Initiation,
            request.Target, startPolicy: policy, startupExpectation: request.StartupExpectation);
        Assert.Equal(field == "canvas" ? "capture.policy.canvas-change-unsupported" : "capture.policy.restart-required",
            fixture.Backend.ValidateStart(changed)?.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedBindingMismatchPreservesNewOwnerAndDoesNotClearItsManualProtection(bool superseded)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var engine = new Engine { StopEntered = entered, StopRelease = release };
        using var fixture = new Fixture(engine, bindStartup: true);
        await using var lifecycle = new CaptureLifecycleCoordinator(fixture.Backend);
        Assert.True((await lifecycle.StartAsync(Context().Request)).Succeeded);
        var valid = CaptureStartupBaselineTests.Request(fixture.Baseline);
        var switching = lifecycle.SwitchAsync(valid);
        Task<CaptureTransitionResult>? queued = null;
        Task<CaptureTransitionResult>? newest = null;
        var latest = CaptureStartupBaselineTests.Request(fixture.Baseline);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            queued = lifecycle.SwitchAsync(CaptureStartupBaselineTests.Request(fixture.Baseline,
                CaptureStartupBaselineTests.Change(fixture.Baseline.Baseline!.Expectation, "mic-id")));
            if (superseded) newest = lifecycle.SwitchAsync(latest);
        }
        finally { release.TrySetResult(); }
        var active = await switching.WaitAsync(TimeSpan.FromSeconds(5));
        var rejected = await queued!.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(active.Succeeded);
        Assert.Equal(superseded ? "capture.manual.superseded" : "capture.startup-binding.mismatch", rejected.Failure?.Code);
        Assert.Same(active.After, rejected.After);
        if (newest is not null) Assert.True((await newest.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        Assert.Same(superseded ? latest : valid, lifecycle.Snapshot.Context!.Request);
        Assert.Equal(superseded ? 2 : 1, engine.Stops);
        var competing = await lifecycle.StartAsync(new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            CaptureInitiation.Automatic, new GameCaptureTarget("next", 43, "next.exe", DateTimeOffset.UnixEpoch)));
        Assert.Equal(CaptureFailureKind.ManualPriority, competing.Failure?.Kind);
    }

    [Fact]
    public async Task MismatchPreservesRetainedFailedCleanupContext()
    {
        var engine = new Engine { FailStop = true };
        using var fixture = new Fixture(engine, bindStartup: true);
        await using var lifecycle = new CaptureLifecycleCoordinator(fixture.Backend);
        var request = CaptureStartupBaselineTests.Request(fixture.Baseline);
        Assert.True((await lifecycle.StartAsync(request)).Succeeded);
        await lifecycle.StopOwnedManualAsync(lifecycle.Snapshot.Context!);
        var retained = lifecycle.Snapshot;
        var stops = engine.Stops;
        var rejected = await lifecycle.SwitchAsync(CaptureStartupBaselineTests.Request(fixture.Baseline,
            CaptureStartupBaselineTests.Change(fixture.Baseline.Baseline!.Expectation, "host")));
        Assert.Same(retained, rejected.After);
        Assert.Equal(stops, engine.Stops);
        engine.FailStop = false;
        Assert.True((await lifecycle.StopOwnedManualAsync(retained.Context!)).Succeeded);
    }

    [Theory]
    [MemberData(nameof(CaptureStartupBaselineTests.ExpectationFields), MemberType = typeof(CaptureStartupBaselineTests))]
    public async Task BoundStartupMismatchCannotReplaceEitherCurrentOwner(string field)
    {
        foreach (var manual in new[] { false, true })
        {
            var engine = new Engine();
            using var fixture = new Fixture(engine, bindStartup: true);
            await using var lifecycle = new CaptureLifecycleCoordinator(fixture.Backend);
            var owner = manual ? Context().Request : new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
                CaptureInitiation.Automatic, new GameCaptureTarget("game", 42, "game.exe", DateTimeOffset.UnixEpoch));
            Assert.True((await lifecycle.StartAsync(owner)).Succeeded);
            var before = lifecycle.Snapshot;
            var starts = engine.WindowStarts;
            var initialized = engine.Initializations;
            var policy = engine.AppliedPolicy;
            var expectation = CaptureStartupBaselineTests.Change(fixture.Baseline.Baseline!.Expectation, field);
            var request = CaptureStartupBaselineTests.Request(fixture.Baseline, expectation);
            var rejected = await lifecycle.SwitchAsync(request);
            Assert.Equal(CaptureCommandDisposition.Rejected, rejected.Disposition);
            var expectedFailure = CaptureStartupBaselineTests.ExpectedFailure(field);
            Assert.Equal(expectedFailure.Kind, rejected.Failure!.Kind);
            Assert.Equal(expectedFailure.Code, rejected.Failure.Code);
            Assert.Same(before, rejected.After);
            Assert.Same(before, lifecycle.Snapshot);
            Assert.Equal(0, engine.Stops);
            Assert.Equal(starts, engine.WindowStarts);
            Assert.Equal(initialized, engine.Initializations);
            Assert.Same(policy, engine.AppliedPolicy);
            var competing = await lifecycle.SwitchAsync(new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
                CaptureInitiation.Automatic, new GameCaptureTarget("next", 43, "next.exe", DateTimeOffset.UnixEpoch)));
            if (manual) Assert.Equal(CaptureFailureKind.ManualPriority, competing.Failure?.Kind);
            else Assert.True(competing.Succeeded);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidStartupDefaultCannotReplaceSupportedProfileCapture(bool invalidRateControl)
    {
        var engine = new Engine();
        using var fixture = new Fixture(engine,
            recordingMode: invalidRateControl ? RecordingMode.ReplayBufferOnly : (RecordingMode)999,
            captureDefaults: new CaptureConfig { RateControl = invalidRateControl ? (RateControlType)999 : RateControlType.CBR });
        await using var lifecycle = new CaptureLifecycleCoordinator(fixture.Backend);
        var target = new GameCaptureTarget("test", 42, "test.exe", DateTimeOffset.UnixEpoch, "Test");
        var policy = new CaptureStartPolicy(new() { Encoder = EncoderType.X264, RateControl = RateControlType.CBR }, new(), new());
        var automatic = new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            CaptureInitiation.Automatic, target, startPolicy: policy);
        Assert.True((await lifecycle.StartAsync(automatic)).Succeeded);
        var active = lifecycle.Snapshot;
        var replacement = new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            CaptureInitiation.Manual, new WindowCaptureTarget(77, new IntPtr(0x7777), "Manual", "manual.exe"));
        Assert.Equal("capture.policy.undefined-mode", (await lifecycle.SwitchAsync(replacement)).Failure?.Code);
        Assert.Same(active, lifecycle.Snapshot);
        Assert.Equal(0, engine.Stops);
        Assert.Equal(1, engine.WindowStarts);
    }

    [Fact]
    public async Task SanitizedStartupAudioDefaultMatchesAutomaticPolicyAdmission()
    {
        var defaults = new CaptureConfig { AudioBitrate = -1 };
        var engine = new Engine();
        using var fixture = new Fixture(engine, captureDefaults: defaults);
        var settings = new AppSettings { Capture = defaults };
        var decision = GameCapturePolicy.Resolve(settings, "test.exe", defaults);
        Assert.Equal(CapturePolicyDisposition.Ready, decision.Disposition);
        var target = new GameCaptureTarget("test", 42, "test.exe", DateTimeOffset.UnixEpoch, "Test");
        var request = new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            CaptureInitiation.Automatic, target, startPolicy: decision.Policy);
        Assert.Null(fixture.Backend.ValidateStart(request));
        Assert.True((await fixture.Backend.StartAsync(new(Guid.NewGuid(), 1, request, target, target))).Succeeded);
        Assert.Equal(64, engine.AppliedPolicy!.Capture.AudioBitrate);
        Assert.Equal(-1, defaults.AudioBitrate);
    }
    [Theory]
    [InlineData(RecordingMode.SessionRecording)]
    [InlineData(RecordingMode.ReplayBufferOnly)]
    public async Task FrozenPolicyOverridesStartupModeAndFlowsToOutputs(RecordingMode mode)
    {
        var engine = new Engine();
        var recorder = new Recorder();
        using var fixture = new Fixture(engine, recorder, mode == RecordingMode.SessionRecording
            ? RecordingMode.ReplayBufferOnly : RecordingMode.SessionRecording);
        var policy = new CaptureStartPolicy(
            new CaptureConfig { Encoder = EncoderType.X264, RateControl = RateControlType.CBR, Bitrate = 7_000 },
            new WatchDog.Core.Buffer.BufferConfig { MaxSeconds = 30 },
            new SessionRecordingConfig { Mode = mode });
        var target = new GameCaptureTarget("test", 42, "test.exe", DateTimeOffset.UnixEpoch, "Test");
        var request = new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            CaptureInitiation.Automatic, target, startPolicy: policy);
        var context = new CaptureSessionContext(Guid.NewGuid(), 1, request, target, target);
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);
        Assert.Equal(policy.Capture, engine.AppliedPolicy!.Capture);
        Assert.Equal(policy.Buffer, engine.AppliedPolicy.Buffer);
        Assert.Equal(policy.Recording.IsReplayBufferEnabled, engine.LastReplayEnabled);
        Assert.Equal(policy.Recording.IsSessionRecordingEnabled, recorder.IsRecording);
        if (recorder.IsRecording)
            Assert.Same(policy, recorder.StartContext!.Request.StartPolicy);
        var saved = await fixture.Backend.SaveAsync(context);
        if (!policy.Recording.IsReplayBufferEnabled)
            Assert.Equal("capture.save.replay-disabled", saved.Failure?.Code);
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);

        // A later request without a game policy restores startup defaults.
        Assert.True((await fixture.Backend.StartAsync(Context())).Succeeded);
        Assert.Equal(new CaptureConfig(), engine.AppliedPolicy.Capture);
        Assert.Equal(120, engine.AppliedPolicy.Buffer.MaxSeconds);
    }

    [Fact]
    public async Task ConfigurationRejectionHasNoStartupOrAttributionEffects()
    {
        var engine = new Engine
        {
            ApplyFailure = new(CaptureFailureKind.Unsupported, "fixture.apply-rejected", "Unavailable configuration seam"),
        };
        using var fixture = new Fixture(engine);
        Assert.Equal("fixture.apply-rejected", (await fixture.Backend.StartAsync(Context())).Failure?.Code);
        Assert.Equal(0, engine.Initializations);
        Assert.Empty(await fixture.Repository.GetRecentAsync(10));
        Assert.Null(engine.AppliedPolicy);
    }
    [Theory]
    [InlineData(EncoderType.AmfH264)]
    [InlineData(EncoderType.AmfHevc)]
    [InlineData((EncoderType)999)]
    public async Task UnsupportedEncoderIsRejectedBeforeAnyStartupEffect(EncoderType encoder)
    {
        var engine = new Engine();
        using var fixture = new Fixture(engine, encoder: encoder);
        var result = await fixture.Backend.StartAsync(Context());
        Assert.Equal("capture.encoder.unsupported", result.Failure?.Code);
        Assert.Equal(0, engine.Initializations);
        Assert.Equal(0, engine.WindowStarts);
        Assert.Empty(await fixture.Repository.GetRecentAsync(10));
    }
    [Fact]
    public async Task SuccessfulStartReturnsFrozenEngineAudioManifestAndPostStartExactWebcam()
    {
        var audioManifest = new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.DesktopAudio, "speaker-a"),
            AdmittedCaptureDevice.SystemDefault(CaptureDeviceRole.Microphone),
        ]);
        var engine = new Engine { AdmittedAudioDevices = audioManifest };
        var recorder = new Recorder();
        using var fixture = new Fixture(
            engine,
            recorder: recorder,
            recordingMode: RecordingMode.SessionRecording,
            webcamRecorder: new WebcamRecorder());
        var context = WebcamContext();

        var result = await fixture.Backend.StartAsync(context);

        Assert.True(result.Succeeded);
        Assert.Equal(context, recorder.StartContext);
        Assert.Equal(3, result.AdmittedDevices.Entries.Count);
        Assert.Contains(result.AdmittedDevices.Entries, entry =>
            entry.Role == CaptureDeviceRole.DesktopAudio && entry.RequestedDeviceId == "speaker-a");
        Assert.Contains(result.AdmittedDevices.Entries, entry =>
            entry.Role == CaptureDeviceRole.Microphone &&
            entry.SelectionMode == CaptureDeviceSelectionMode.SystemDefault);
        Assert.Contains(result.AdmittedDevices.Entries, entry =>
            entry.Role == CaptureDeviceRole.Webcam && entry.RequestedDeviceId == "exact-camera-id");
    }

    [Fact]
    public async Task SaveReplay_IngestsExactContextBeforePublishingUiSuccess()
    {
        var engine = new Engine { ReplayPath = "saved.mp4" };
        using var fixture = new Fixture(engine);
        var context = Context();
        await fixture.Backend.StartAsync(context);
        var ingestionCountWhenPublished = -1;
        ClipSavedEvent? published = null;
        fixture.Bus.Subscribe<ClipSavedEvent>(message =>
        {
            published = message;
            ingestionCountWhenPublished = fixture.Ingestion.Accepted.Count;
        });

        var result = await fixture.Backend.SaveAsync(context, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, ingestionCountWhenPublished);
        var accepted = Assert.Single(fixture.Ingestion.Accepted);
        Assert.Equal(context.SessionId, accepted.SessionId);
        Assert.Equal(context.Request.RequestId, accepted.CaptureRequestId);
        Assert.Equal(context.Generation, accepted.CaptureGeneration);
        Assert.Equal(accepted, result.Receipt);
        Assert.Equal(context.SessionId, published!.CaptureContext!.SessionId);
        Assert.Equal(context.Request, published.CaptureContext.Request);
        Assert.Equal(context.Generation, published.CaptureContext.Generation);
    }

    [Fact]
    public async Task SaveReplay_WhenIngestionIsPending_DoesNotPublishUiSuccess()
    {
        var engine = new Engine { ReplayPath = "saved.mp4" };
        var ingestion = new RecordingIngestion
        {
            Result = new CompletedMediaIngestionResult(
                CompletedMediaIngestionDisposition.PendingRetry,
                FailureMessage: "index unavailable"),
        };
        using var fixture = new Fixture(engine, ingestion: ingestion);
        var context = Context();
        await fixture.Backend.StartAsync(context);
        ClipSavedEvent? published = null;
        fixture.Bus.Subscribe<ClipSavedEvent>(message => published = message);

        var result = await fixture.Backend.SaveAsync(context, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("capture.save.ingestion-pending", result.Failure!.Code);
        Assert.Equal("saved.mp4", result.OutputPath);
        Assert.Equal(Assert.Single(ingestion.Accepted), result.Receipt);
        Assert.Null(published);
    }

    [Fact]
    public async Task FailedSessionIndexCreationDoesNotStartPhysicalCapture()
    {
        var engine = new Engine();
        using var fixture = new Fixture(engine);
        fixture.BlockEmptyIndex();
        var result = await fixture.Backend.StartAsync(Context());
        Assert.False(result.Succeeded);
        Assert.False(result.CleanupRequired);
        Assert.Equal(0, engine.WindowStarts);
    }

    [Fact]
    public async Task FailedInitializationAndCleanupRetainOwnershipUntilRetry()
    {
        var engine = new Engine { FailInitialize = true, FailStop = true };
        using var fixture = new Fixture(engine);
        var context = Context();
        var result = await fixture.Backend.StartAsync(context);
        Assert.False(result.Succeeded);
        Assert.True(result.CleanupRequired);
        Assert.Equal(1, engine.Stops);
        Assert.False((await fixture.Backend.StartAsync(Context())).Succeeded);
        engine.FailStop = false;
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
        Assert.Equal(2, engine.Stops);
    }

    [Fact]
    public async Task FailedRecorderCleanupDoesNotTearDownSharedEngine()
    {
        var engine = new Engine();
        var recorder = new Recorder { FailStart = true, FailStop = true };
        using var fixture = new Fixture(engine, recorder);
        var context = Context();
        var failedStart = await fixture.Backend.StartAsync(context);
        Assert.True(failedStart.CleanupRequired);
        var ownedTarget = Assert.IsType<WindowCaptureTarget>(failedStart.ActualTarget);
        Assert.Equal(new IntPtr(1234), ownedTarget.WindowHandle);
        Assert.Equal(0, engine.Stops);
        recorder.FailStop = false;
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
        Assert.Equal(1, engine.Stops);
    }

    [Fact]
    public async Task GameRequestStartsOnlyTheResolvedExactWindow()
    {
        var engine = new Engine();
        var recorder = new Recorder();
        using var fixture = new Fixture(
            engine,
            recorder,
            recordingMode: RecordingMode.SessionRecording);
        var context = Context();
        var result = await fixture.Backend.StartAsync(context);
        var actual = Assert.IsType<WindowCaptureTarget>(result.ActualTarget);
        Assert.Equal(((GameCaptureTarget)context.RequestedTarget).ProcessId, actual.ProcessId);
        Assert.Equal(new IntPtr(1234), actual.WindowHandle);
        Assert.Equal(1, engine.WindowStarts);
        var recorderContext = Assert.IsType<CaptureSessionContext>(recorder.StartContext);
        Assert.Equal(context.SessionId, recorderContext.SessionId);
        Assert.Equal(context.Request.RequestId, recorderContext.Request.RequestId);
        Assert.Equal(context.Generation, recorderContext.Generation);
        Assert.Equal(actual, recorderContext.ActualTarget);
        Assert.True((await fixture.Backend.StopAsync(context with { ActualTarget = actual })).Succeeded);
    }

    [Fact]
    public async Task MissingExactGameWindowFailsClosedBeforeSessionStarts()
    {
        var engine = new Engine { ResolveGameWindow = false };
        using var fixture = new Fixture(engine);
        var result = await fixture.Backend.StartAsync(Context());
        Assert.False(result.Succeeded);
        Assert.Equal("capture.game.exact-window-unavailable", result.Failure?.Code);
        Assert.Equal(0, engine.WindowStarts);
    }

    [Fact]
    public async Task DisplayRequestBindsTheResolvedStableIdentity()
    {
        var engine = new Engine();
        using var fixture = new Fixture(engine);
        var context = Context(new DisplayCaptureTarget("display-1", "Display 1", 0));

        var result = await fixture.Backend.StartAsync(context);

        Assert.True(result.Succeeded);
        Assert.Equal("display-1", Assert.IsType<DisplayCaptureTarget>(result.ActualTarget).StableDisplayId);
        Assert.Equal("display-1", engine.LastDisplayTarget?.StableDisplayId);
        Assert.Equal(0, engine.IndexDisplayResolutions);
        Assert.Equal(1, engine.StableDisplayResolutions);
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
    }

    [Fact]
    public async Task RegionRequestStartsOnlyTheExactOwnedRegion()
    {
        var engine = new Engine();
        using var fixture = new Fixture(engine);
        var target = new RegionCaptureTarget(
            "display-1",
            new PhysicalCaptureBounds(100, 120, 640, 360),
            new PhysicalCaptureBounds(0, 0, 1920, 1080));
        var context = Context(target);

        var result = await fixture.Backend.StartAsync(context);

        Assert.True(result.Succeeded);
        Assert.Equal(target, Assert.IsType<RegionCaptureTarget>(result.ActualTarget));
        Assert.Equal(target, engine.LastRegionTarget);
        Assert.Equal(1, engine.RegionStarts);
        Assert.Equal(0, engine.DesktopStarts);
        Assert.Equal(0, engine.WindowStarts);
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
    }

    [Fact]
    public async Task UnavailableRegionFailsClosedWithoutAnotherCaptureMode()
    {
        var engine = new Engine();
        using var fixture = new Fixture(engine);
        var target = new RegionCaptureTarget(
            "missing-display",
            new PhysicalCaptureBounds(100, 120, 640, 360),
            new PhysicalCaptureBounds(0, 0, 1920, 1080));

        var result = await fixture.Backend.StartAsync(Context(target));

        Assert.False(result.Succeeded);
        Assert.Equal("capture.region.unavailable", result.Failure?.Code);
        Assert.Equal(0, engine.RegionStarts);
        Assert.Equal(0, engine.DesktopStarts);
        Assert.Equal(0, engine.WindowStarts);
    }

    [Fact]
    public async Task ReorderedDisplayIdentityFailsClosed()
    {
        var engine = new Engine { ResolvedDisplayId = "different-display" };
        using var fixture = new Fixture(engine);
        var context = Context(new DisplayCaptureTarget("display-1", "Display 1", 0));

        var result = await fixture.Backend.StartAsync(context);

        Assert.False(result.Succeeded);
        Assert.Equal("capture.display.identity-mismatch", result.Failure?.Code);
        Assert.Equal(0, engine.DesktopStarts);
    }

    [Fact]
    public async Task SessionOnlyModeStartsExactSourceWithoutReplayBuffer()
    {
        var engine = new Engine();
        var recorder = new Recorder();
        using var fixture = new Fixture(engine, recorder, RecordingMode.SessionRecording);
        var context = Context();

        var result = await fixture.Backend.StartAsync(context);

        Assert.True(result.Succeeded);
        Assert.True(recorder.IsRecording);
        Assert.False(engine.LastReplayEnabled);
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
    }

    [Theory]
    [InlineData("flv")]
    [InlineData("mov")]
    [InlineData("unknown")]
    public async Task SeparateTracksWithUnsupportedContainerRejectBeforeNativeAcquisition(string format)
    {
        var engine = new Engine();
        using var fixture = new Fixture(
            engine,
            new Recorder(),
            RecordingMode.SessionRecording,
            separateAudioTracks: true,
            recordingFileFormat: format);

        var result = await fixture.Backend.StartAsync(Context());

        Assert.False(result.Succeeded);
        Assert.Equal("capture.audio.separate-tracks-container-unsupported", result.Failure?.Code);
        Assert.Equal(0, engine.WindowStarts);
        Assert.Equal(CaptureState.Idle, engine.State);
    }

    [Fact]
    public async Task CompletedSegmentPersistsBeforeCaptureStops()
    {
        var recorder = new Recorder();
        using var fixture = new Fixture(new Engine(), recorder);
        var context = Context();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);
        var persisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subscription = fixture.Bus.Subscribe<SessionRecordingSegmentSavedEvent>(_ => persisted.TrySetResult());
        recorder.EmitSegment("completed-segment.mkv");
        await persisted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("completed-segment.mkv", (await fixture.Repository.GetByIdAsync(context.SessionId))!.RecordingPaths);
        Assert.True(recorder.IsRecording);
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
    }

    [Fact]
    public async Task MetadataFailureStillStopsEngineAndRetriesPendingSegment()
    {
        var engine = new Engine();
        var recorder = new Recorder();
        using var fixture = new Fixture(engine, recorder);
        var context = Context();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);
        fixture.BlockIndex();
        recorder.EmitSegment("pending-segment.mkv");
        Assert.False((await fixture.Backend.StopAsync(context)).Succeeded);
        Assert.Equal(CaptureState.Idle, engine.State);
        Assert.False(recorder.IsRecording);
        fixture.UnblockIndex();
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
        Assert.Equal(new[] { "pending-segment.mkv" }, (await fixture.Repository.GetByIdAsync(context.SessionId))!.RecordingPaths);
    }

    [Fact]
    public async Task RecorderFinalizationFailureStillStopsEngineAndReleasesPhysicalOwnership()
    {
        var engine = new Engine();
        var recorder = new Recorder { FailStopAfterCleanup = true };
        using var fixture = new Fixture(engine, recorder);
        var context = Context();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);

        var stop = await fixture.Backend.StopAsync(context);

        Assert.False(stop.Succeeded);
        Assert.False(stop.CleanupRequired);
        Assert.False(recorder.IsRecording);
        Assert.Equal(CaptureState.Idle, engine.State);
        Assert.Equal(1, engine.Stops);
        recorder.FailStopAfterCleanup = false;
        Assert.True((await fixture.Backend.StartAsync(Context())).Succeeded);
    }

    [Fact]
    public async Task RecorderFinalizationFailure_RemainsObservableInLifecycleSnapshotAfterCleanup()
    {
        var engine = new Engine();
        var recorder = new Recorder { FailStopAfterCleanup = true };
        using var fixture = new Fixture(engine, recorder);
        await using var coordinator = new CaptureLifecycleCoordinator(fixture.Backend);
        var observed = new List<CaptureLifecycleSnapshot>();
        coordinator.SnapshotChanged += observed.Add;
        Assert.True((await coordinator.StartAsync(Context().Request)).Succeeded);

        var stop = await coordinator.StopAsync(CaptureInitiation.Manual);

        Assert.False(stop.Succeeded);
        Assert.Equal(CaptureLifecycleStatus.Faulted, stop.After.Status);
        Assert.Equal(CaptureHealth.Degraded, stop.After.Health);
        Assert.Null(stop.After.Context);
        Assert.Equal("capture.backend.stop-failed", stop.After.LastFailure?.Code);
        Assert.Contains("finalized media is missing", stop.After.LastFailure?.Message);
        Assert.Contains(observed, snapshot =>
            snapshot.Status == CaptureLifecycleStatus.Faulted &&
            snapshot.LastFailure?.Code == "capture.backend.stop-failed");
        Assert.Equal(CaptureState.Idle, engine.State);
        Assert.False(recorder.HasActiveResources);
    }

    [Fact]
    public async Task StartCleanupWarningDoesNotRetainReleasedBackendOwnership()
    {
        var recorder = new Recorder
        {
            FailStart = true,
            FailStopAfterCleanup = true,
        };
        using var fixture = new Fixture(new Engine(), recorder);
        await using var coordinator = new CaptureLifecycleCoordinator(fixture.Backend);

        var failedStart = await coordinator.StartAsync(Context().Request);

        Assert.False(failedStart.Succeeded);
        Assert.Null(failedStart.After.Context);
        recorder.FailStart = false;
        recorder.FailStopAfterCleanup = false;
        Assert.True((await coordinator.StartAsync(Context().Request)).Succeeded);
        Assert.True((await coordinator.ShutdownAsync()).Succeeded);
    }

    [Fact]
    public async Task UnexpectedRecorderStopPublishesOwnedHealthDegradation()
    {
        var recorder = new Recorder();
        using var fixture = new Fixture(new Engine(), recorder);
        var context = Context();
        var health = new List<CaptureHealthDegradedEvent>();
        using var subscription = fixture.Bus.Subscribe<CaptureHealthDegradedEvent>(health.Add);
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);

        recorder.EmitStopped();

        var degraded = Assert.Single(health);
        Assert.Equal(context.SessionId, degraded.Context.SessionId);
        Assert.Equal("capture.session-recording.stopped", degraded.Failure.Code);
    }

    [Fact]
    public async Task ExpectedRecorderStopDoesNotPublishHealthDegradation()
    {
        var recorder = new Recorder { EmitStoppedOnStop = true };
        using var fixture = new Fixture(new Engine(), recorder);
        var context = Context();
        var health = new List<CaptureHealthDegradedEvent>();
        using var subscription = fixture.Bus.Subscribe<CaptureHealthDegradedEvent>(health.Add);
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);

        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);

        Assert.Empty(health);
    }

    [Fact]
    public async Task WebcamRequestRequiresSessionRecordingBeforeNativeAcquisition()
    {
        var engine = new Engine();
        var webcam = new WebcamRecorder();
        using var fixture = new Fixture(engine, webcamRecorder: webcam);

        var result = await fixture.Backend.StartAsync(WebcamContext());

        Assert.False(result.Succeeded);
        Assert.Equal("capture.webcam.session-recording-required", result.Failure?.Code);
        Assert.Equal(0, engine.WindowStarts);
        Assert.Equal(0, webcam.Starts);
    }

    [Fact]
    public async Task WebcamIntentIsPersistedBeforeDeviceActivationAndFinalizedAsIndependentMedia()
    {
        var webcam = new WebcamRecorder();
        using var fixture = new Fixture(
            new Engine(),
            new Recorder(),
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);
        var context = WebcamContext();
        webcam.BeforeStart = () =>
        {
            var session = fixture.Repository.GetByIdAsync(context.SessionId).GetAwaiter().GetResult();
            var planned = Assert.Single(session!.MediaSegments);
            Assert.Equal(SessionMediaRole.Camera, planned.Role);
            Assert.Equal(SessionMediaState.Planned, planned.State);
        };

        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);
        Assert.True(await fixture.Backend.StopAsync(context) is { Succeeded: true });

        var saved = await fixture.Repository.GetByIdAsync(context.SessionId);
        var camera = Assert.Single(saved!.MediaSegments, item => item.Role == SessionMediaRole.Camera);
        Assert.Equal(SessionMediaState.Finalized, camera.State);
        Assert.NotNull(camera.Duration);
        Assert.DoesNotContain(saved.RecordingPaths, path =>
            string.Equals(path, camera.FilePath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WebcamCleanupFailureStillAttemptsScreenRecorderAndBlocksEngineTeardown()
    {
        var engine = new Engine();
        var screen = new Recorder();
        var webcam = new WebcamRecorder { FailStop = true };
        using var fixture = new Fixture(
            engine,
            screen,
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);
        var context = WebcamContext();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);

        var stop = await fixture.Backend.StopAsync(context);

        Assert.False(stop.Succeeded);
        Assert.True(stop.CleanupRequired);
        Assert.Equal("capture.backend.stop-failed", stop.Failure?.Code);
        Assert.False(stop.Failure?.HasUncertainWebcamViewRelease);
        Assert.False(screen.IsRecording);
        Assert.Equal(0, engine.Stops);
        webcam.FailStop = false;
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
        Assert.Equal(1, engine.Stops);
        var saved = await fixture.Repository.GetByIdAsync(context.SessionId);
        Assert.Equal(
            SessionMediaState.Finalized,
            Assert.Single(saved!.MediaSegments, item => item.Role == SessionMediaRole.Camera).State);
    }

    [Fact]
    public async Task UncertainViewReleaseWithoutNewExceptionMapsStableNonRetryableFailure()
    {
        var engine = new Engine();
        var webcam = new WebcamRecorder();
        using var fixture = new Fixture(
            engine,
            new Recorder(),
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);
        var context = WebcamContext();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);
        webcam.RetainUncertainViewRelease = true;

        var stop = await fixture.Backend.StopAsync(context);

        Assert.False(stop.Succeeded);
        Assert.True(stop.CleanupRequired);
        Assert.Equal(CaptureFailureKind.Backend, stop.Failure?.Kind);
        Assert.Equal(CaptureFailure.WebcamViewReleaseUncertainCode, stop.Failure?.Code);
        Assert.Equal("Webcam cleanup could not be confirmed.", stop.Failure?.Message);
        Assert.False(stop.Failure?.IsRetryable);
        Assert.True(stop.Failure?.HasUncertainWebcamViewRelease);
        Assert.Equal(0, engine.Stops);
        Assert.Equal(1, webcam.UncertainViewReleaseReads);
    }

    [Fact]
    public async Task UncertainViewReleaseTakesPrecedenceOverStopCancellation()
    {
        var webcam = new WebcamRecorder();
        using var fixture = new Fixture(
            new Engine(),
            new Recorder(),
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);
        var context = WebcamContext();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);
        webcam.RetainUncertainViewRelease = true;
        webcam.HonorStopCancellation = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var stop = await fixture.Backend.StopAsync(context, cancellation.Token);

        Assert.False(stop.Succeeded);
        Assert.True(stop.CleanupRequired);
        Assert.Equal(CaptureFailureKind.Backend, stop.Failure?.Kind);
        Assert.Equal(CaptureFailure.WebcamViewReleaseUncertainCode, stop.Failure?.Code);
        Assert.Equal("Webcam cleanup could not be confirmed.", stop.Failure?.Message);
        Assert.False(stop.Failure?.IsRetryable);
        Assert.Equal(1, webcam.UncertainViewReleaseReads);
    }

    [Fact]
    public async Task UncertainViewReleaseTakesPrecedenceOverOtherStopErrors()
    {
        var webcam = new WebcamRecorder
        {
            FailStop = true,
            RetainUncertainViewRelease = true,
        };
        using var fixture = new Fixture(
            new Engine(),
            new Recorder(),
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);
        var context = WebcamContext();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);

        var stop = await fixture.Backend.StopAsync(context);

        Assert.False(stop.Succeeded);
        Assert.True(stop.CleanupRequired);
        Assert.Equal(CaptureFailure.WebcamViewReleaseUncertainCode, stop.Failure?.Code);
        Assert.Equal("Webcam cleanup could not be confirmed.", stop.Failure?.Message);
        Assert.False(stop.Failure?.IsRetryable);
        Assert.Equal(1, webcam.UncertainViewReleaseReads);
    }

    [Fact]
    public async Task ForeignStopRejectsWithoutObservingWebcamUncertainty()
    {
        var webcam = new WebcamRecorder();
        using var fixture = new Fixture(
            new Engine(),
            new Recorder(),
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);
        var context = WebcamContext();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);
        Assert.Equal(0, webcam.UncertainViewReleaseReads);

        var stop = await fixture.Backend.StopAsync(context with { SessionId = Guid.NewGuid() });

        Assert.False(stop.Succeeded);
        Assert.Equal(CaptureFailureKind.Superseded, stop.Failure?.Kind);
        Assert.Equal(0, webcam.UncertainViewReleaseReads);
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
    }

    [Fact]
    public async Task PendingWebcamFinalizationAllowsEngineTeardownButKeepsCleanupRetryable()
    {
        var engine = new Engine();
        var screen = new Recorder();
        var webcam = new WebcamRecorder { FailPendingFinalization = true };
        using var fixture = new Fixture(
            engine,
            screen,
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);
        var context = WebcamContext();
        Assert.True((await fixture.Backend.StartAsync(context)).Succeeded);

        var stop = await fixture.Backend.StopAsync(context);

        Assert.False(stop.Succeeded);
        Assert.True(stop.CleanupRequired);
        Assert.False(screen.IsRecording);
        Assert.Equal(1, engine.Stops);
        Assert.True(webcam.HasActiveResources);
        Assert.False(webcam.HasNativeResources);

        var retryDelay = TimeSpan.FromSeconds(1);
        await Task.Delay(retryDelay);
        webcam.FailPendingFinalization = false;
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
        var saved = await fixture.Repository.GetByIdAsync(context.SessionId);
        var camera = Assert.Single(saved!.MediaSegments, item => item.Role == SessionMediaRole.Camera);
        Assert.Equal(SessionMediaState.Finalized, camera.State);
        Assert.True(camera.Duration < retryDelay / 2);
    }

    [Theory]
    [InlineData(CaptureDeviceRole.DesktopAudio, CaptureDeviceStartupStage.Binding, CaptureDeviceStartupReason.Unknown, "capture.device.desktop-audio.binding.unknown")]
    [InlineData(CaptureDeviceRole.Microphone, CaptureDeviceStartupStage.Start, CaptureDeviceStartupReason.Unknown, "capture.device.microphone.start.unknown")]
    [InlineData(CaptureDeviceRole.Webcam, CaptureDeviceStartupStage.Readiness, CaptureDeviceStartupReason.Unknown, "capture.device.webcam.readiness.unknown")]
    public async Task StructuredDeviceFailuresMapToStableRetryableBackendCodes(
        CaptureDeviceRole role,
        CaptureDeviceStartupStage stage,
        CaptureDeviceStartupReason reason,
        string expectedCode)
    {
        var engine = new Engine
        {
            StartFailure = new CaptureDeviceStartupException(role, stage, reason, "device detail"),
        };
        using var fixture = new Fixture(engine);

        var result = await fixture.Backend.StartAsync(Context());

        Assert.False(result.Succeeded);
        Assert.Equal(CaptureFailureKind.Backend, result.Failure?.Kind);
        Assert.Equal(expectedCode, result.Failure?.Code);
        Assert.True(result.Failure?.IsRetryable);
        Assert.DoesNotContain("device detail", result.Failure?.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(CaptureDeviceStartupStage.Catalog, CaptureDeviceStartupReason.InvalidCatalog, "capture.device.webcam.catalog.invalid")]
    [InlineData(CaptureDeviceStartupStage.Binding, CaptureDeviceStartupReason.Unknown, "capture.device.webcam.binding.unknown")]
    [InlineData(CaptureDeviceStartupStage.Readiness, CaptureDeviceStartupReason.Unknown, "capture.device.webcam.readiness.unknown")]
    [InlineData(CaptureDeviceStartupStage.Start, CaptureDeviceStartupReason.Unknown, "capture.device.webcam.start.unknown")]
    public async Task CleanedWebcamStartupRejectionCompensatesPlanAndAllowsRecovery(
        CaptureDeviceStartupStage stage,
        CaptureDeviceStartupReason reason,
        string expectedCode)
    {
        var engine = new Engine();
        var screen = new Recorder();
        var webcam = new WebcamRecorder
        {
            StartFailure = new CaptureDeviceStartupException(
                CaptureDeviceRole.Webcam,
                stage,
                reason,
                "native start detail"),
        };
        using var fixture = new Fixture(
            engine,
            screen,
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);

        var rejected = WebcamContext();
        var failed = await fixture.Backend.StartAsync(rejected);

        Assert.False(failed.Succeeded);
        Assert.False(failed.CleanupRequired);
        Assert.Equal(expectedCode, failed.Failure?.Code);
        Assert.False(screen.HasActiveResources);
        Assert.False(webcam.HasActiveResources);
        Assert.False(webcam.HasNativeResources);
        Assert.Equal(CaptureState.Idle, engine.State);
        Assert.Equal(1, engine.Stops);
        Assert.Null(await fixture.Repository.GetByIdAsync(rejected.SessionId));

        webcam.StartFailure = null;
        var recovery = WebcamContext();
        Assert.True((await fixture.Backend.StartAsync(recovery)).Succeeded);
        Assert.True((await fixture.Backend.StopAsync(recovery)).Succeeded);
        var recovered = await fixture.Repository.GetByIdAsync(recovery.SessionId);
        Assert.DoesNotContain(recovered!.MediaSegments, media => media.State == SessionMediaState.Planned);
        Assert.Equal(
            SessionMediaState.Finalized,
            Assert.Single(recovered.MediaSegments, media => media.Role == SessionMediaRole.Camera).State);
    }

    [Fact]
    public async Task FailedPlannedCameraCompensationRetainsOwnershipUntilRetry()
    {
        var webcam = new WebcamRecorder
        {
            StartFailure = new CaptureDeviceStartupException(
                CaptureDeviceRole.Webcam,
                CaptureDeviceStartupStage.Readiness,
                CaptureDeviceStartupReason.Unknown,
                "readiness detail"),
        };
        using var fixture = new Fixture(
            new Engine(),
            new Recorder(),
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);
        webcam.BeforeStart = fixture.BlockIndex;
        var context = WebcamContext();

        var failed = await fixture.Backend.StartAsync(context);

        Assert.True(failed.CleanupRequired);
        var retained = await fixture.Repository.GetByIdAsync(context.SessionId);
        Assert.Equal(
            SessionMediaState.Planned,
            Assert.Single(retained!.MediaSegments, media => media.Role == SessionMediaRole.Camera).State);

        fixture.UnblockIndex();
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
        Assert.Null(await fixture.Repository.GetByIdAsync(context.SessionId));
    }

    [Fact]
    public async Task ReleasedCleanupWarningIsNotReportedAsIncompleteOwnership()
    {
        var webcam = new WebcamRecorder
        {
            FailStopAfterRelease = true,
            StartFailure = new CaptureDeviceStartupException(
                CaptureDeviceRole.Webcam,
                CaptureDeviceStartupStage.Start,
                CaptureDeviceStartupReason.Unknown,
                "start detail"),
        };
        using var fixture = new Fixture(
            new Engine(),
            new Recorder(),
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);

        var result = await fixture.Backend.StartAsync(WebcamContext());

        Assert.False(result.CleanupRequired);
        Assert.Equal("capture.device.webcam.start.unknown", result.Failure?.Code);
        Assert.Equal("OBS could not start the selected webcam.", result.Failure?.Message);
        Assert.Equal("capture.backend.stop-failed", result.Failure?.CleanupFailure?.Code);
        Assert.Contains("webcam cleanup warning after release", result.Failure?.CleanupFailure?.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task StructuredDeviceFailureSurvivesCleanupAggregation()
    {
        var structured = new CaptureDeviceStartupException(
            CaptureDeviceRole.Webcam,
            CaptureDeviceStartupStage.Binding,
            CaptureDeviceStartupReason.IdentityMismatch,
            "exact identity mismatch");
        var webcam = new WebcamRecorder
        {
            FailStop = true,
            StartFailure = new AggregateException(
                "start failed and cleanup was incomplete",
                structured,
                new IOException("cleanup detail")),
        };
        using var fixture = new Fixture(
            new Engine(),
            new Recorder(),
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);

        var context = WebcamContext();
        var result = await fixture.Backend.StartAsync(context);

        Assert.Equal("capture.device.webcam.binding.identity-mismatch", result.Failure?.Code);
        Assert.True(result.CleanupRequired);
        Assert.DoesNotContain("cleanup detail", result.Failure?.Message, StringComparison.Ordinal);
        Assert.Equal("OBS did not retain the exact selected webcam identity.", result.Failure?.Message);
        Assert.Equal("capture.backend.stop-failed", result.Failure?.CleanupFailure?.Code);
        Assert.Contains("webcam recorder: webcam stop failed", result.Failure?.CleanupFailure?.Message,
            StringComparison.Ordinal);

        webcam.FailStop = false;
        Assert.True((await fixture.Backend.StopAsync(context)).Succeeded);
    }

    [Fact]
    public async Task WebcamStartupFailurePreservesPrimaryAndAttachesUncertainCleanup()
    {
        var webcam = new WebcamRecorder
        {
            RetainUncertainViewRelease = true,
            StartFailure = new CaptureDeviceStartupException(
                CaptureDeviceRole.Webcam,
                CaptureDeviceStartupStage.Binding,
                CaptureDeviceStartupReason.IdentityMismatch,
                "private startup detail"),
        };
        using var fixture = new Fixture(
            new Engine(),
            new Recorder(),
            RecordingMode.SessionRecording,
            webcamRecorder: webcam);

        var result = await fixture.Backend.StartAsync(WebcamContext());

        Assert.False(result.Succeeded);
        Assert.True(result.CleanupRequired);
        Assert.Equal(CaptureFailureKind.Backend, result.Failure?.Kind);
        Assert.Equal("capture.device.webcam.binding.identity-mismatch", result.Failure?.Code);
        Assert.Equal("OBS did not retain the exact selected webcam identity.", result.Failure?.Message);
        Assert.False(result.Failure?.IsRetryable);
        Assert.Equal(CaptureFailure.WebcamViewReleaseUncertainCode, result.Failure?.CleanupFailure?.Code);
        Assert.Equal("Webcam cleanup could not be confirmed.", result.Failure?.CleanupFailure?.Message);
        Assert.Null(result.Failure?.CleanupFailure?.CleanupFailure);
        Assert.Equal(1, webcam.UncertainViewReleaseReads);
    }

    [Fact]
    public async Task UnrelatedStorageUnauthorizedAccessRemainsGenericBackendFailure()
    {
        var recorder = new Recorder
        {
            StartFailure = new UnauthorizedAccessException("session folder denied"),
        };
        using var fixture = new Fixture(new Engine(), recorder, RecordingMode.SessionRecording);

        var result = await fixture.Backend.StartAsync(Context());

        Assert.Equal("capture.backend.start-failed", result.Failure?.Code);
        Assert.Contains("session folder denied", result.Failure?.Message, StringComparison.Ordinal);
    }

    private static CaptureSessionContext Context()
    {
        var target = new GameCaptureTarget("test", 42, "test.exe", DateTimeOffset.UnixEpoch, "Test");
        return Context(target);
    }

    private static CaptureSessionContext Context(CaptureTarget target)
    {
        var request = new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, CaptureInitiation.Manual, target);
        return new CaptureSessionContext(Guid.NewGuid(), 1, request, target, target);
    }

    private static CaptureSessionContext WebcamContext()
    {
        var target = new WindowCaptureTarget(42, new IntPtr(1234), "Tutorial", "tutorial.exe", "Fixture");
        var webcam = new WebcamCaptureSelection(
            "exact-camera-id", "Physical camera", 1280, 720, 30, "MJPEG", 1_500);
        var request = new CaptureRequest(
            Guid.NewGuid(), DateTimeOffset.UtcNow, CaptureInitiation.Manual, target, webcam);
        return new CaptureSessionContext(Guid.NewGuid(), 1, request, target, target);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), "WD_backend_" + Guid.NewGuid().ToString("N"));
        private readonly JsonSessionRepository _repository;
        public JsonSessionRepository Repository => _repository;
        public void BlockEmptyIndex() => Directory.CreateDirectory(Path.Combine(_path, "sessions-index.json"));
        public void BlockIndex()
        {
            var index = Path.Combine(_path, "sessions-index.json");
            File.Move(index, index + ".blocked");
            Directory.CreateDirectory(index);
        }
        public void UnblockIndex()
        {
            var index = Path.Combine(_path, "sessions-index.json");
            Directory.Delete(index);
            File.Move(index + ".blocked", index);
        }
        public InMemoryEventBus Bus { get; } = new();
        public RecordingIngestion Ingestion { get; }
        public ObsCaptureLifecycleBackend Backend { get; }
        public CaptureStartupBaselineResult Baseline { get; }
        public string LegacySaveRoot { get; }
        public Fixture(
            Engine engine,
            Recorder? recorder = null,
            RecordingMode? recordingMode = null,
            bool separateAudioTracks = false,
            string? recordingFileFormat = null,
            WebcamRecorder? webcamRecorder = null,
            RecordingIngestion? ingestion = null,
            EncoderType encoder = EncoderType.NvencH264,
            CaptureConfig? captureDefaults = null,
            bool bindStartup = false,
            bool distinctLegacyRoot = false)
        {
            _repository = new JsonSessionRepository(_path, NullLogger<JsonSessionRepository>.Instance);
            var manager = new SessionManager(_repository, new NullClipStorage(), Bus, NullLogger<SessionManager>.Instance);
            var settings = new AppSettings();
            settings = settings with
            {
                Capture = captureDefaults ?? settings.Capture with { Encoder = encoder },
                Storage = settings.Storage with { SavePath = _path },
                Buffer = bindStartup ? settings.Buffer with { OutputDirectory = _path } : settings.Buffer,
                Recording = settings.Recording with
                {
                    Mode = recordingMode ?? (recorder is not null ? RecordingMode.Both : RecordingMode.ReplayBufferOnly),
                    FileFormat = recordingFileFormat ?? settings.Recording.FileFormat,
                },
                Audio = settings.Audio with { SeparateAudioTracks = separateAudioTracks },
            };
            Ingestion = ingestion ?? new RecordingIngestion();
            Baseline = CaptureStartupBaselineResult.Create(settings);
            // Deliberately disagree only in this fixture to prove which authority the output helper consumes.
            // Production composition supplies one shared settings snapshot to both consumers.
            if (distinctLegacyRoot) settings = settings with { Storage = settings.Storage with { SavePath = Path.Combine(_path, "legacy") } };
            LegacySaveRoot = settings.Storage.SavePath;
            Backend = new ObsCaptureLifecycleBackend(
                engine,
                recorder ?? (bindStartup ? new ReceiptRecorder() : new Recorder()),
                webcamRecorder ?? new WebcamRecorder(),
                manager,
                Bus,
                Ingestion,
                settings,
                NullLogger<ObsCaptureLifecycleBackend>.Instance,
                Baseline);
        }
        public void Dispose()
        {
            Backend.Dispose();
            _repository.Dispose();
            TestFs.DeleteDirectoryWithRetry(_path);
        }
    }

#pragma warning disable CS0067
    private sealed class Engine : ICaptureEngine
    {
        public TaskCompletionSource? StopEntered, StopRelease;
        public bool FailInitialize, FailStop;
        public int Initializations;
        public bool ResolveGameWindow = true;
        public string ResolvedDisplayId = "display-1";
        public bool LastReplayEnabled;
        public string? ReplayPath;
        public int Stops, WindowStarts, DesktopStarts, RegionStarts;
        public int IndexDisplayResolutions, StableDisplayResolutions;
        public DisplayCaptureTarget? LastDisplayTarget;
        public RegionCaptureTarget? LastRegionTarget;
        public Exception? StartFailure;
        public CaptureState State { get; private set; }
        public CaptureConfig Config { get; } = new();
        public CaptureStartPolicy? AppliedPolicy { get; private set; }
        public CaptureFailure? ApplyFailure { get; set; }
        public CaptureFailure? ApplyNextStartConfiguration(CaptureConfig capture, WatchDog.Core.Buffer.BufferConfig buffer)
        {
            if (ApplyFailure is not null) return ApplyFailure;
            AppliedPolicy = new(capture, buffer, new());
            return null;
        }
        public bool IsDesktopCapture => false;
        public bool IsReplayBufferActive => LastReplayEnabled && State == CaptureState.Buffering;
        public GameInfo? CurrentGame { get; private set; }
        public CaptureDeviceManifest AdmittedAudioDevices { get; init; } = CaptureDeviceManifest.Empty;
        public event Action<CaptureState>? StateChanged;
        public event Action<string>? ClipSaved;
        public event Action<string>? Error;
        public void EnsureInitialized() { Initializations++; if (FailInitialize) throw new IOException("partial initialization"); }
        public DisplayCaptureTarget? ResolveDisplayTarget(int monitorIndex)
        {
            IndexDisplayResolutions++;
            return new(ResolvedDisplayId, "Display 1", monitorIndex);
        }
        public DisplayCaptureTarget? ResolveDisplayTarget(string stableDisplayId)
        {
            StableDisplayResolutions++;
            return StringComparer.Ordinal.Equals(ResolvedDisplayId, stableDisplayId)
                ? new DisplayCaptureTarget(stableDisplayId, "Display 1", 0)
                : null;
        }
        public DisplayCaptureTarget? ResolveDisplayTargetByDeviceName(string deviceName) =>
            new(ResolvedDisplayId, "Display 1", 0);
        public bool IsDisplayTargetAvailable(DisplayCaptureTarget target) =>
            StringComparer.Ordinal.Equals(ResolvedDisplayId, target.StableDisplayId);
        public RegionCaptureTarget? ResolveRegionTarget(string owningDisplayId, PhysicalCaptureBounds physicalBounds) =>
            new(owningDisplayId, physicalBounds, new PhysicalCaptureBounds(0, 0, 1920, 1080));
        public bool IsRegionTargetAvailable(RegionCaptureTarget target) =>
            StringComparer.Ordinal.Equals(ResolvedDisplayId, target.OwningDisplayId) &&
            RegionCaptureGeometry.TryGetDisplayLocalBounds(target, out _);
        public bool IsWindowTargetAvailable(WindowCaptureTarget target) => true;
        public WindowCaptureTarget? ResolveGameWindowTarget(GameCaptureTarget game) => ResolveGameWindow
            ? new WindowCaptureTarget(game.ProcessId, new IntPtr(1234), "Game", game.ExecutableName, "GameClass")
            : null;
        public Task StartDesktopCaptureAsync(
            DisplayCaptureTarget target,
            bool startReplayBuffer,
            CancellationToken ct = default)
        { DesktopStarts++; LastDisplayTarget = target; LastReplayEnabled = startReplayBuffer; if (StartFailure is not null) throw StartFailure; State = CaptureState.Buffering; return Task.CompletedTask; }
        public Task StartRegionCaptureAsync(
            RegionCaptureTarget target,
            bool startReplayBuffer,
            CancellationToken ct = default)
        { RegionStarts++; LastRegionTarget = target; LastReplayEnabled = startReplayBuffer; if (StartFailure is not null) throw StartFailure; State = CaptureState.Buffering; return Task.CompletedTask; }
        public Task StartWindowCaptureAsync(
            CaptureSource source,
            bool startReplayBuffer,
            CancellationToken ct = default)
        { WindowStarts++; LastReplayEnabled = startReplayBuffer; CurrentGame = source.ToGameInfo(); if (StartFailure is not null) throw StartFailure; State = CaptureState.Buffering; return Task.CompletedTask; }
        public async Task StopAsync(CancellationToken ct = default)
        {
            Stops++;
            StopEntered?.TrySetResult();
            if (StopRelease is not null) await StopRelease.Task.WaitAsync(ct);
            if (FailStop) throw new IOException("cleanup failed");
            State = CaptureState.Idle;
        }
        public Task<string?> SaveReplayAsync(CancellationToken ct = default) => Task.FromResult(ReplayPath);
        public void Dispose() { }
    }

    private sealed class RecordingIngestion : ICompletedMediaIngestion
    {
        public CompletedMediaIngestionResult Result { get; init; } = new(
            CompletedMediaIngestionDisposition.Indexed,
            new ClipMetadata { FilePath = "saved.mp4", FileName = "saved.mp4" });
        public List<CompletedMediaReceipt> Accepted { get; } = [];
        public CompletedMediaIngestionStatus Status { get; } = new(0, 0, null);
        public event Action<CompletedMediaIngestionStatus>? StatusChanged;

        public Task<CompletedMediaIngestionResult> AcceptAsync(
            CompletedMediaReceipt media,
            CancellationToken cancellationToken = default)
        {
            Accepted.Add(media);
            return Task.FromResult(Result);
        }

        public Task<CompletedMediaIngestionResult> EnrichAsync(
            CompletedMediaReceipt media,
            HighlightType highlightType,
            CancellationToken cancellationToken = default) => AcceptAsync(media, cancellationToken);

        public Task<CompletedMediaDrainResult> DrainAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new CompletedMediaDrainResult(true, true, 0, 0, null));
    }
    private class Recorder : ISessionRecorder
    {
        public bool FailStart, FailStop, FailStopAfterCleanup;
        public bool EmitStoppedOnStop;
        public Exception? StartFailure;
        public bool IsRecording { get; private set; }
        public bool HasActiveResources { get; private set; }
        public CaptureSessionContext? StartContext { get; private set; }
        public string? OutputDirectory { get; private set; }
        public TimeSpan Elapsed => TimeSpan.Zero;
        public string? CurrentOutputPath => "session.mkv";
        public void EmitSegment(string path) => SegmentSaved?.Invoke(path);
        public event Action<string>? SegmentSaved;
        public event Action? Stopped;
        public event Action<string>? Error;
        public void EmitStopped()
        { IsRecording = false; Stopped?.Invoke(); }
        public Task StartAsync(string outputDirectory, GameInfo? game, CancellationToken ct = default)
        { OutputDirectory = outputDirectory; IsRecording = true; HasActiveResources = true; if (StartFailure is not null) throw StartFailure; if (FailStart) throw new IOException("partial recorder start"); return Task.CompletedTask; }
        public Task StartAsync(
            string outputDirectory,
            GameInfo? game,
            CaptureSessionContext context,
            CancellationToken ct = default)
        {
            StartContext = context;
            return StartAsync(outputDirectory, game, ct);
        }
        public Task StopAsync(CancellationToken ct = default)
        {
            if (FailStop) throw new IOException("recorder stop failed");
            IsRecording = false;
            HasActiveResources = false;
            if (EmitStoppedOnStop) Stopped?.Invoke();
            if (FailStopAfterCleanup) throw new IOException("finalized media is missing");
            return Task.CompletedTask;
        }
        public void Dispose() { }
    }

    private sealed class ReceiptRecorder : Recorder, ISessionSegmentReceiptSource
    {
        private readonly List<SessionSegmentReceipt> _finalized = [];
        public event Action<SessionSegmentReceipt>? SegmentFinalized;
        public void EmitReceipt(SessionSegmentReceipt receipt) => SegmentFinalized?.Invoke(receipt);
        public void RetainReceipt(SessionSegmentReceipt receipt) => _finalized.Add(receipt);
        public IReadOnlyList<SessionSegmentReceipt> GetFinalizedSegments(CaptureSessionContext expected) =>
            _finalized.Where(receipt => SessionSegmentReceiptLedger.SameCapture(expected, receipt.Context)).ToArray();
        public SessionSegmentReceipt Receipt() => new(1, StartContext!, Guid.NewGuid(), Guid.NewGuid(),
            Guid.NewGuid(), 0, Path.Combine(OutputDirectory!, Guid.NewGuid().ToString("N") + ".mkv"),
            TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(8), SessionTimingProvenance.RecorderStopwatchEstimate);
    }

    private sealed class WebcamRecorder : IWebcamRecorder
    {
        private string? _outputPath;
        private bool _retainUncertainViewRelease;
        public bool FailStop;
        public bool FailPendingFinalization;
        public bool FailStopAfterRelease;
        public bool HonorStopCancellation;
        public Exception? StartFailure;
        public int Starts { get; private set; }
        public Action? BeforeStart { get; set; }
        public bool IsRecording { get; private set; }
        public bool HasActiveResources { get; private set; }
        public bool HasNativeResources { get; private set; }
        public int UncertainViewReleaseReads { get; private set; }
        public bool RetainUncertainViewRelease
        {
            get => _retainUncertainViewRelease;
            set => _retainUncertainViewRelease = value;
        }
        public bool HasUncertainViewRelease
        {
            get
            {
                UncertainViewReleaseReads++;
                return _retainUncertainViewRelease;
            }
        }
        public string? CurrentOutputPath => _outputPath;

        public Task StartAsync(
            WebcamCaptureSelection selection,
            string outputPath,
            CancellationToken cancellationToken = default)
        {
            Starts++;
            BeforeStart?.Invoke();
            _outputPath = outputPath;
            IsRecording = true;
            HasActiveResources = true;
            HasNativeResources = true;
            if (StartFailure is not null)
                throw StartFailure;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            IsRecording = false;
            if (HonorStopCancellation)
                cancellationToken.ThrowIfCancellationRequested();
            if (FailStop) throw new IOException("webcam stop failed");
            if (RetainUncertainViewRelease)
                return Task.CompletedTask;
            HasNativeResources = false;
            if (FailPendingFinalization)
                throw new IOException("webcam finalization failed");
            HasActiveResources = false;
            if (FailStopAfterRelease)
                throw new IOException("webcam cleanup warning after release");
            if (_outputPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_outputPath)!);
                File.WriteAllBytes(_outputPath, [1]);
            }
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
#pragma warning restore CS0067
}
