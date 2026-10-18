using WatchDog.Core.Capture;
using WatchDog.Core.Recording;
using WatchDog.Core.Settings;

namespace WatchDog.Core.Tests.Settings;

public sealed class GameCapturePolicyTests
{
    private static AppSettings Defaults => new()
    {
        Capture = new CaptureConfig { Encoder = EncoderType.X264, RateControl = RateControlType.CBR },
    };

    [Fact]
    public void NoProfileAndNullOverridesInheritDefaultsWithoutMutation()
    {
        var settings = Defaults;
        var none = GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture);
        var profile = new GameRecordingProfile { GameExecutableName = "game.exe" };
        var inherited = GameCapturePolicy.Resolve(settings with { GameProfiles = [profile] }, "GAME.exe", settings.Capture);
        Assert.Equal(CapturePolicyDisposition.Ready, none.Disposition);
        Assert.Equal(none.Policy, inherited.Policy);
        Assert.Equal(settings.Capture, none.Policy!.Capture);
        Assert.Null(profile.BitrateKbps);
        Assert.Equal(20_000, settings.Capture.Bitrate);
    }

    [Theory]
    [InlineData(GameRecordingMode.ReplayBuffer, RecordingMode.ReplayBufferOnly)]
    [InlineData(GameRecordingMode.SessionRecording, RecordingMode.SessionRecording)]
    public void SupportedModeAndBitrateAreFrozen(GameRecordingMode mode, RecordingMode expected)
    {
        var settings = Defaults;
        var profile = new GameRecordingProfile { GameExecutableName = "game.exe", Mode = mode, BitrateKbps = 7_000 };
        var profiles = new List<GameRecordingProfile> { profile };
        var result = GameCapturePolicy.Resolve(settings with { GameProfiles = profiles }, "game.exe", settings.Capture);
        Assert.Equal(CapturePolicyDisposition.Ready, result.Disposition);
        Assert.Equal(expected, result.Policy!.Recording.Mode);
        Assert.Equal(7_000, result.Policy.Capture.Bitrate);
        profiles[0] = profile with { BitrateKbps = 99_000 };
        Assert.Equal(7_000, result.Policy.Capture.Bitrate);
        Assert.Equal(20_000, settings.Capture.Bitrate);
        Assert.Equal(7_000, profile.BitrateKbps);
    }

    [Fact]
    public void NumericOverridesAreSanitizedAndCanvasChangesAreActionable()
    {
        var settings = Defaults;
        var profile = new GameRecordingProfile
        {
            GameExecutableName = "game.exe",
            BitrateKbps = -1,
            BufferDurationSeconds = int.MaxValue,
        };
        var result = GameCapturePolicy.Resolve(settings with { GameProfiles = [profile] }, "game.exe", settings.Capture);
        Assert.Equal(1_000, result.Policy!.Capture.Bitrate);
        Assert.Equal(1_200, result.Policy.Buffer.MaxSeconds);
        Assert.Equal(-1, profile.BitrateKbps);

        var resolution = profile with { OutputWidth = int.MaxValue, OutputHeight = -1 };
        result = GameCapturePolicy.Resolve(settings with { GameProfiles = [resolution] }, "game.exe", settings.Capture);
        Assert.Equal(CapturePolicyDisposition.Unsupported, result.Disposition);
        Assert.Equal("capture.policy.canvas-change-unsupported", result.Failure?.Code);
        Assert.Contains("restart", result.Failure!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AutoRecordFalseDisablesBeforeUnsupportedFieldsAreConsidered()
    {
        var settings = Defaults with { GameProfiles = [new() { GameExecutableName = "game.exe", AutoRecord = false, Mode = GameRecordingMode.Highlight }] };
        var result = GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture);
        Assert.Equal(CapturePolicyDisposition.Disabled, result.Disposition);
        Assert.Null(result.Policy);
    }

    [Theory]
    [InlineData(GameRecordingMode.Highlight)]
    [InlineData((GameRecordingMode)999)]
    public void UnsupportedModesRejectWithNoPolicy(GameRecordingMode mode)
    {
        var settings = Defaults with { GameProfiles = [new() { GameExecutableName = "game.exe", Mode = mode }] };
        var result = GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture);
        Assert.Equal(CapturePolicyDisposition.Unsupported, result.Disposition);
        Assert.Null(result.Policy);
        Assert.Contains("Select", result.Failure!.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0.6f)]
    [InlineData(float.NaN)]
    public void SensitivityOverrideIsNeverFalselyApplied(float sensitivity)
    {
        var settings = Defaults with { GameProfiles = [new() { GameExecutableName = "game.exe", HighlightSensitivity = sensitivity }] };
        Assert.Equal("capture.profile.sensitivity-unsupported", GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture).Failure?.Code);
    }

    [Fact]
    public void ExactExecutableMatchDoesNotApplyOtherGamesOrPathsAndDuplicatesReject()
    {
        var settings = Defaults with { GameProfiles = [new() { GameExecutableName = "other-game.exe", AutoRecord = false }] };
        Assert.Equal(CapturePolicyDisposition.Ready, GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture).Disposition);
        settings = settings with { GameProfiles = [new() { GameExecutableName = "C:\\games\\game.exe", AutoRecord = false }] };
        Assert.Equal(CapturePolicyDisposition.Ready, GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture).Disposition);
        settings = settings with { GameProfiles = [new() { GameExecutableName = "game.exe" }, new() { GameExecutableName = "GAME.exe" }] };
        Assert.Equal("capture.profile.ambiguous", GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture).Failure?.Code);
    }

    [Fact]
    public void OverridesMustBeConsumedByTheSelectedOutput()
    {
        var settings = Defaults;
        var profile = new GameRecordingProfile { GameExecutableName = "game.exe", BitrateKbps = 7_000 };
        settings = settings with { Capture = settings.Capture with { RateControl = RateControlType.CQP }, GameProfiles = [profile] };
        Assert.Equal("capture.profile.replay-bitrate-unsupported", GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture).Failure?.Code);
        settings = settings with { GameProfiles = [profile with { Mode = GameRecordingMode.SessionRecording }] };
        Assert.Equal(CapturePolicyDisposition.Ready, GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture).Disposition);
        settings = settings with { GameProfiles = [profile with { Mode = GameRecordingMode.SessionRecording, BufferDurationSeconds = 30 }] };
        Assert.Equal("capture.profile.buffer-output-unsupported", GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture).Failure?.Code);
    }

    [Fact]
    public void InvalidNumericDefaultsAreSanitizedWithoutChangingTheSavedSettings()
    {
        var settings = Defaults with
        {
            Capture = Defaults.Capture with { Bitrate = -1, Quality = 999, AudioBitrate = -1 },
            Buffer = new() { MaxSeconds = -1, MaxSizeMb = int.MaxValue },
            Recording = new() { SegmentDurationMinutes = -1, MaxDurationMinutes = int.MaxValue },
        };
        var result = GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture);
        Assert.Equal(CapturePolicyDisposition.Ready, result.Disposition);
        Assert.Equal(1_000, result.Policy!.Capture.Bitrate);
        Assert.Equal(51, result.Policy.Capture.Quality);
        Assert.Equal(64, result.Policy.Capture.AudioBitrate);
        Assert.Equal(10, result.Policy.Buffer.MaxSeconds);
        Assert.Equal(4_096, result.Policy.Buffer.MaxSizeMb);
        Assert.Equal(0, result.Policy.Recording.SegmentDurationMinutes);
        Assert.Equal(1_440, result.Policy.Recording.MaxDurationMinutes);
        Assert.Equal(-1, settings.Capture.Bitrate);
        Assert.Equal(-1, settings.Buffer.MaxSeconds);
    }

    [Fact]
    public void VbrBitrateOverrideRaisesEffectiveMaximumWithoutChangingDefaults()
    {
        var settings = Defaults with
        {
            Capture = Defaults.Capture with { RateControl = RateControlType.VBR },
            GameProfiles = [new() { GameExecutableName = "game.exe", BitrateKbps = 80_000 }],
        };
        var policy = GameCapturePolicy.Resolve(settings, "game.exe", settings.Capture).Policy!;
        Assert.Equal(80_000, policy.Capture.Bitrate);
        Assert.Equal(80_000, policy.Capture.MaxBitrate);
        Assert.Equal(40_000, settings.Capture.MaxBitrate);
    }
}
