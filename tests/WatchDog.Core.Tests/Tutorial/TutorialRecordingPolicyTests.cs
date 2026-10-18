using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Recording;
using WatchDog.Core.Settings;
using WatchDog.Core.Tutorial;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Tests.Tutorial;

public sealed class TutorialRecordingPolicyTests
{
    [Fact]
    public void OriginalStartupCanvasStillRejectsAChangedSanitizedPlanCanvas()
    {
        var settings = new AppSettings { Capture = new() { OutputWidth = 1 } };
        var baseline = CaptureStartupBaselineResult.Create(settings);
        Assert.Equal(1u, baseline.Baseline!.Expectation.StartupCanvas.OutputWidth);
        Assert.Equal(640u, baseline.Baseline.DefaultStartPolicy.Capture.OutputWidth);
        Assert.Equal("capture.policy.canvas-change-unsupported", TutorialRecordingPolicy.Create(
            Guid.NewGuid(), DateTimeOffset.UnixEpoch, Target(0), baseline).Failure?.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FreezesExactManualBoundaryAndSessionModeWithoutMutatingDefaults(int kind)
    {
        var settings = new AppSettings { Audio = new AudioMixConfig { SeparateAudioTracks = true } };
        var target = Target(kind);
        var result = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, target, CaptureStartupBaselineResult.Create(settings));
        var plan = Assert.IsType<TutorialRecordingPlan>(result.Plan);
        Assert.Null(result.Failure);
        Assert.NotNull(plan.Request.StartupExpectation);
        Assert.Same(target, plan.Request.Target);
        Assert.Equal(CaptureInitiation.Manual, plan.Request.Initiation);
        Assert.Equal(RecordingMode.SessionRecording, plan.Request.StartPolicy!.Recording.Mode);
        Assert.Equal(RecordingMode.ReplayBufferOnly, settings.Recording.Mode);
        Assert.True(plan.CanPlanStart);
        Assert.False(plan.NativeRecordingProven);
        Assert.Equal(2, plan.AudioTracks.EncoderCount);
        Assert.Equal(plan.Request.StartupExpectation.StartupAudio, plan.StartupAudio);
        Assert.Equal(plan.Request.StartupExpectation.CanonicalSaveRoot, plan.CanonicalSaveRoot);
        var edited = settings with { Audio = settings.Audio with { MicEnabled = false } };
        Assert.True(plan.StartupAudio.MicEnabled);
        Assert.False(edited.Audio.MicEnabled);
    }

    [Fact]
    public void SavedWebcamPreferenceAloneDoesNotAuthorizeCamera()
    {
        var settings = new AppSettings { Webcam = new WebcamConfig { EnabledForExplicitManualRecording = true, DeviceId = "saved-camera" } };
        var plan = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, Target(0), CaptureStartupBaselineResult.Create(settings)).Plan!;
        Assert.Null(plan.Request.Webcam);
        var camera = new WebcamCaptureSelection("exact-camera", "Camera", 640, 480, 30, "MJPEG", 1500);
        var denied = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, Target(0), CaptureStartupBaselineResult.Create(settings), camera);
        Assert.Null(denied.Plan);
        Assert.Equal("tutorial.webcam.consent-required", denied.Failure!.Code);
        var consented = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, Target(0), CaptureStartupBaselineResult.Create(settings), camera, true).Plan!;
        Assert.Same(camera, consented.Request.Webcam);
    }

    [Theory]
    [MemberData(nameof(InvalidTargets))]
    public void InvalidOrBroaderTargetsAreRejectedWithoutFallback(CaptureTarget? target)
    {
        var result = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, target, CaptureStartupBaselineResult.Create(new AppSettings()));
        Assert.Null(result.Plan);
        Assert.Equal(CaptureFailureKind.InvalidRequest, result.Failure!.Kind);
    }

    public static TheoryData<CaptureTarget?> InvalidTargets => new()
    {
        null,
        new GameCaptureTarget("game", 1, "game.exe", DateTimeOffset.UnixEpoch),
        new WindowCaptureTarget(0, new IntPtr(1)),
        new WindowCaptureTarget(1, IntPtr.Zero),
        new WindowCaptureTarget(1, new IntPtr(1)),
        new WindowCaptureTarget(1, new IntPtr(1), " ", "tutorial.exe"),
        new WindowCaptureTarget(1, new IntPtr(1), "Tutorial", " "),
        new DisplayCaptureTarget("", MonitorIndex: 0),
        new RegionCaptureTarget("display", new(-1, 0, 101, 100), new(0, 0, 100, 100)),
        new RegionCaptureTarget("display", new(int.MaxValue, 0, 100, 100), new(int.MaxValue, 0, 100, 100)),
    };

    [Fact]
    public void ConsentWithoutExactCameraSelectionIsRejected()
    {
        var result = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, Target(0), CaptureStartupBaselineResult.Create(new AppSettings()), explicitWebcamConsent: true);
        Assert.Null(result.Plan);
        Assert.Equal("tutorial.webcam.selection-required", result.Failure!.Code);
    }

    [Fact]
    public void EmptyIdUnsupportedEncoderAndContainerAreActionable()
    {
        Assert.Equal("tutorial.request.id-empty", TutorialRecordingPolicy.Create(Guid.Empty, DateTimeOffset.UnixEpoch, Target(0), CaptureStartupBaselineResult.Create(new AppSettings())).Failure!.Code);
        var unsupported = new AppSettings { Capture = new CaptureConfig { Encoder = EncoderType.AmfH264 } };
        Assert.Equal(CaptureFailureKind.Unsupported, TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, Target(0), CaptureStartupBaselineResult.Create(unsupported)).Failure!.Kind);
        var container = new AppSettings { Audio = new AudioMixConfig { SeparateAudioTracks = true }, Recording = new SessionRecordingConfig { FileFormat = "flv" } };
        Assert.Equal("tutorial.audio.container-unsupported", TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, Target(0), CaptureStartupBaselineResult.Create(container)).Failure!.Code);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(-0.1f)]
    [InlineData(1.1f)]
    public void InvalidAudioMetadataIsRejected(float volume)
    {
        var settings = new AppSettings { Audio = new AudioMixConfig { MicVolume = volume } };
        Assert.Equal("capture.startup-binding.unavailable", TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, Target(0), CaptureStartupBaselineResult.Create(settings)).Failure!.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("unknown")]
    public void UndefinedSessionContainerIsRejectedEvenWithCombinedAudio(string? format)
    {
        var settings = new AppSettings { Recording = new SessionRecordingConfig { FileFormat = format! } };
        Assert.Equal(format is null ? "capture.startup-binding.unavailable" : "tutorial.recording.container-unsupported", TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, Target(0), CaptureStartupBaselineResult.Create(settings)).Failure!.Code);
    }

    [Fact]
    public void CombinedFlvIsSourceSupportedWithoutAdvertisingPause()
    {
        var settings = new AppSettings { Recording = new SessionRecordingConfig { FileFormat = "flv" } };
        Assert.NotNull(TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UnixEpoch, Target(0), CaptureStartupBaselineResult.Create(settings)).Plan);
    }

    internal static CaptureTarget Target(int kind) => kind switch
    {
        0 => new WindowCaptureTarget(12, new IntPtr(123), "Tutorial", "tutorial.exe", "ExactClass"),
        1 => new DisplayCaptureTarget("display", "Display"),
        _ => new RegionCaptureTarget("display", new(-100, 20, 80, 60), new(-1920, 0, 1920, 1080)),
    };
}
