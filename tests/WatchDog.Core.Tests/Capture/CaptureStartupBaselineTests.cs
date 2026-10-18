using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Recording;
using WatchDog.Core.Settings;

namespace WatchDog.Core.Tests.Capture;

public sealed class CaptureStartupBaselineTests
{
    [Fact]
    public void LiveMixerGainAndMuteChangesDoNotAlterOrGetResetByStartupBinding()
    {
        var settings = new AppSettings();
        var result = CaptureStartupBaselineResult.Create(settings);
        var calls = 0;
        var mixer = new ObsAudioMixer((_, _, _, _) => calls++,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<ObsAudioMixer>.Instance);
        mixer.ApplyConfig(settings.Audio);
        mixer.SetDesktopVolume(0.2f);
        mixer.SetMicVolume(0.3f);
        mixer.ToggleDesktopMute();
        mixer.ToggleMicMute();
        var before = calls;
        Assert.Null(result.Validate(Request(result)));
        Assert.Equal(before, calls);
        Assert.Equal(1f, result.Baseline!.Expectation.StartupAudio.DesktopVolume);
        Assert.Equal(1f, result.Baseline.Expectation.StartupAudio.MicVolume);
        Assert.Equal(0.2f, mixer.DesktopVolume);
        Assert.Equal(0.3f, mixer.MicVolume);
        Assert.True(mixer.IsDesktopMuted);
        Assert.True(mixer.IsMicMuted);
    }

    [Fact]
    public void BindingTypesExposeOnlyOneGetOnlyPropertyPerField()
    {
        foreach (var type in new[] { typeof(CaptureStartupExpectation), typeof(CaptureStartupCanvas),
            typeof(CaptureStartupConstraints), typeof(TutorialWebcamFormatDefaults) })
        {
            Assert.All(type.GetProperties(), property =>
            {
                Assert.Null(property.SetMethod);
                Assert.True(char.IsUpper(property.Name[0]));
            });
        }
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("buffer")]
    [InlineData("recording")]
    [InlineData("policy")]
    [InlineData("mode")]
    [InlineData("automatic")]
    [InlineData("target")]
    public void MalformedBoundPolicyOrRequestRejectsWithoutThrowing(string field)
    {
        var result = CaptureStartupBaselineResult.Create(new AppSettings());
        var original = Request(result);
        var policy = field switch
        {
            "capture" => original.StartPolicy! with { Capture = null! },
            "buffer" => original.StartPolicy! with { Buffer = null! },
            "recording" => original.StartPolicy! with { Recording = null! },
            "policy" => null,
            "mode" => original.StartPolicy! with { Recording = new() },
            _ => original.StartPolicy,
        };
        var request = new CaptureRequest(original.RequestId, original.RequestedAt,
            field == "automatic" ? CaptureInitiation.Automatic : CaptureInitiation.Manual,
            field == "target" ? new GameCaptureTarget("game", 42, "game.exe") : original.Target,
            startPolicy: policy, startupExpectation: original.StartupExpectation);
        Assert.Equal("capture.startup-binding.invalid", result.Validate(request)?.Code);
    }

    [Fact]
    public void FreezesStartupInputsWithoutRetainingCallerCollectionsOrSavedCameraConsent()
    {
        var settings = new AppSettings
        {
            Capture = new() { AudioBitrate = -1 },
            Audio = new() { SeparateAudioTracks = true, MicEnabled = false },
            Webcam = new() { EnabledForExplicitManualRecording = true, DeviceId = "private-camera" },
        };
        var result = CaptureStartupBaselineResult.Create(settings);
        var baseline = Assert.IsType<CaptureStartupBaseline>(result.Baseline);
        var expected = baseline.Expectation;
        Assert.Null(result.Failure);
        Assert.NotEqual(Guid.Empty, expected.HostBaselineId);
        Assert.Equal(64, expected.StartupOnlyConstraints.EffectiveAudioBitrate);
        Assert.Equal(-1, settings.Capture.AudioBitrate);
        Assert.Equal(settings.Capture.OutputWidth, expected.StartupCanvas.OutputWidth);
        Assert.NotSame(settings.Audio, expected.StartupAudio);
        settings.CustomGames.Add(new() { DisplayName = "Changed", ExecutableName = "changed.exe" });
        var replacement = settings with { Audio = settings.Audio with { MicEnabled = true } };
        Assert.False(expected.StartupAudio.MicEnabled);
        Assert.True(replacement.Audio.MicEnabled);
        Assert.Equal(2, AudioTrackPlan.Create(expected.StartupAudio.SeparateAudioTracks).EncoderCount);
        Assert.DoesNotContain("private-camera", baseline.WebcamFormatDefaults!.ToString());
        Assert.NotEqual(expected.HostBaselineId, CaptureStartupBaselineResult.Create(settings).Baseline!.Expectation.HostBaselineId);
        Assert.DoesNotContain(expected.CanonicalSaveRoot, expected.ToString());
        Assert.DoesNotContain(expected.CanonicalSaveRoot, expected.StartupOnlyConstraints.ToString());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("capture")]
    [InlineData("buffer")]
    [InlineData("recording")]
    [InlineData("storage")]
    [InlineData("audio")]
    [InlineData("selector")]
    [InlineData("whitespace-selector")]
    [InlineData("gain")]
    [InlineData("relative")]
    [InlineData("inconsistent")]
    [InlineData("invalid-path")]
    [InlineData("host")]
    [InlineData("filename")]
    [InlineData("format")]
    public void InvalidStartupInputsFailClosedWithoutThrowingOrLeakingValues(string field)
    {
        var settings = new AppSettings();
        AppSettings? invalid = field switch
        {
            "missing" => null,
            "capture" => settings with { Capture = null! },
            "buffer" => settings with { Buffer = null! },
            "recording" => settings with { Recording = null! },
            "storage" => settings with { Storage = null! },
            "audio" => settings with { Audio = null! },
            "selector" => settings with { Audio = settings.Audio with { MicDeviceId = null! } },
            "whitespace-selector" => settings with { Audio = settings.Audio with { DesktopDeviceId = " \t" } },
            "gain" => settings with { Audio = settings.Audio with { DesktopVolume = float.NaN } },
            "relative" => settings with { Storage = settings.Storage with { SavePath = "private-relative" } },
            "inconsistent" => settings with { Buffer = settings.Buffer with { OutputDirectory = @"C:\private-other" } },
            "invalid-path" => settings with { Storage = settings.Storage with { SavePath = "C:\\private\0folder" } },
            "filename" => settings with { Buffer = settings.Buffer with { FilenameFormat = null! } },
            "format" => settings with { Recording = settings.Recording with { FileFormat = null! } },
            _ => settings,
        };
        var result = CaptureStartupBaselineResult.Create(invalid, field == "host" ? Guid.Empty : null);
        Assert.Null(result.Baseline);
        Assert.Equal("capture.startup-binding.unavailable", result.Failure!.Code);
        Assert.False(result.Failure.IsRetryable);
        Assert.DoesNotContain("private", result.Failure.Message);
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Tutorial\sub\..\")]
    public void CanonicalizesLexicallyAndPreservesOriginalConstraintSpelling(string root)
    {
        var settings = new AppSettings
        {
            Storage = new() { SavePath = root },
            Buffer = new() { OutputDirectory = root },
        };
        var baseline = CaptureStartupBaselineResult.Create(settings).Baseline!;
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), baseline.Expectation.CanonicalSaveRoot);
        Assert.Equal(root, baseline.Expectation.StartupOnlyConstraints.BufferOutputDirectory);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidUnusedWebcamDefaultsDoNotInvalidateCameraOffBaseline(bool missing)
    {
        var settings = new AppSettings { Webcam = missing ? null! : new() { FramesPerSecond = double.NaN } };
        var result = CaptureStartupBaselineResult.Create(settings);
        Assert.NotNull(result.Baseline);
        Assert.Null(result.Baseline.WebcamFormatDefaults);
    }

    [Theory]
    [MemberData(nameof(ExpectationFields))]
    public void EveryExpectationFieldParticipatesInValueEqualityAndAdmission(string field)
    {
        var result = CaptureStartupBaselineResult.Create(new AppSettings());
        var original = result.Baseline!.Expectation;
        var changed = Change(original, field);
        Assert.NotEqual(original, changed);
        var request = Request(result, changed);
        var failure = Assert.IsType<CaptureFailure>(result.Validate(request));
        Assert.False(failure.IsRetryable);
        var expectedFailure = ExpectedFailure(field);
        Assert.Equal(expectedFailure.Kind, failure.Kind);
        Assert.Equal(expectedFailure.Code, failure.Code);
        Assert.DoesNotContain(original.CanonicalSaveRoot, failure.Message);
        Assert.Null(result.Validate(Request(result, original)));
    }

    public static TheoryData<string> ExpectationFields => new()
    {
        "schema", "host", "empty-host", "width", "height", "fps", "desktop-on", "desktop-id",
        "desktop-gain", "mic-on", "mic-id", "mic-gain", "tracks", "layout", "bitrate",
        "buffer-root", "filename", "format", "save-root", "null-canvas", "null-audio", "null-constraints",
    };

    internal static (CaptureFailureKind Kind, string Code) ExpectedFailure(string field) => field switch
    {
        "host" => (CaptureFailureKind.Superseded, "capture.startup-binding.host-changed"),
        "schema" or "empty-host" or "layout" or "buffer-root" or "save-root" or
            "null-canvas" or "null-audio" or "null-constraints" =>
            (CaptureFailureKind.InvalidRequest, "capture.startup-binding.invalid"),
        _ => (CaptureFailureKind.Unsupported, "capture.startup-binding.mismatch"),
    };

    internal static CaptureRequest Request(CaptureStartupBaselineResult result, CaptureStartupExpectation? expectation = null) =>
        new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, CaptureInitiation.Manual,
            new WindowCaptureTarget(42, new IntPtr(1234), "Tutorial", "tutorial.exe", "Fixture"),
            startPolicy: result.Baseline!.DefaultStartPolicy with
            {
                Recording = result.Baseline.DefaultStartPolicy.Recording with { Mode = RecordingMode.SessionRecording },
            }, startupExpectation: expectation ?? result.Baseline.Expectation);

    internal static CaptureStartupExpectation Change(CaptureStartupExpectation e, string field)
    {
        var audio = field switch
        {
            "desktop-on" => e.StartupAudio with { DesktopAudioEnabled = !e.StartupAudio.DesktopAudioEnabled },
            "desktop-id" => e.StartupAudio with { DesktopDeviceId = "private-speaker" },
            "desktop-gain" => e.StartupAudio with { DesktopVolume = 0.25f },
            "mic-on" => e.StartupAudio with { MicEnabled = !e.StartupAudio.MicEnabled },
            "mic-id" => e.StartupAudio with { MicDeviceId = "private-mic" },
            "mic-gain" => e.StartupAudio with { MicVolume = 0.25f },
            "tracks" => e.StartupAudio with { SeparateAudioTracks = !e.StartupAudio.SeparateAudioTracks },
            "null-audio" => null!,
            _ => e.StartupAudio,
        };
        var canvas = e.StartupCanvas;
        var constraints = e.StartupOnlyConstraints;
        return new(field == "schema" ? 2 : e.SchemaVersion,
            field == "host" ? Guid.NewGuid() : field == "empty-host" ? Guid.Empty : e.HostBaselineId,
            field == "null-canvas" ? null! : new(
                canvas.OutputWidth + (field == "width" ? 1u : 0),
                canvas.OutputHeight + (field == "height" ? 1u : 0),
                canvas.Fps + (field == "fps" ? 1u : 0)), audio,
            field == "layout" ? 2 : e.TrackLayoutVersion,
            field == "null-constraints" ? null! : new(
                field == "bitrate" ? 128 : constraints.EffectiveAudioBitrate,
                field == "buffer-root" ? @"C:\private-other" : constraints.BufferOutputDirectory,
                field == "filename" ? "private-format" : constraints.BufferFilenameFormat,
                field == "format" ? "mkv" : constraints.RecordingFileFormat),
            field == "save-root" ? @"C:\private-other" : e.CanonicalSaveRoot);
    }
}
