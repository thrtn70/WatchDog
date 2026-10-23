using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Capture;
using WatchDog.Core.Recording;
using WatchDog.Core.Settings;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Tests.Settings;

public sealed class SettingsPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"WatchDogSettings-{Guid.NewGuid():N}");
    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Theory]
    [InlineData(EncoderType.AmfH264)]
    [InlineData(EncoderType.AmfHevc)]
    [InlineData((EncoderType)999)]
    public void UnsupportedEncoderSurvivesDiskReloadAndCapabilityRejectionWithoutRewrite(EncoderType encoder)
    {
        var service = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath);
        var settings = service.Load();
        service.Save(settings with { Capture = settings.Capture with { Encoder = encoder } });
        var savedBytes = File.ReadAllBytes(SettingsPath);
        var reloaded = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath).Load();
        Assert.Equal(encoder, reloaded.Capture.Encoder);
        Assert.NotNull(EncoderCapabilities.Validate(reloaded.Capture.Encoder));
        Assert.Contains(EncoderCapabilities.OptionsForSavedSelection(reloaded.Capture.Encoder),
            option => option.Encoder == encoder && !option.IsImplemented);
        Assert.Equal(savedBytes, File.ReadAllBytes(SettingsPath));
    }

    [Fact]
    public void StoragePathWinsAndPreviousLocationsRemainDiscoverableAfterReload()
    {
        var service = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath);
        var original = service.Load();
        var legacy = Path.Combine(_directory, "old clips");
        var selected = Path.Combine(_directory, "My clips 日本語");
        service.Save(original with
        {
            Storage = original.Storage with { SavePath = selected },
            Buffer = original.Buffer with { OutputDirectory = legacy },
        });
        var reloaded = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath).Load();
        Assert.Equal(selected, reloaded.Storage.SavePath);
        Assert.Equal(selected, reloaded.Buffer.OutputDirectory);
        Assert.Contains(legacy, reloaded.Storage.LegacySavePaths);
        Assert.Contains(original.Storage.SavePath, reloaded.Storage.LegacySavePaths);
        Assert.False(Directory.Exists(selected));
    }

    [Fact]
    public void FailedWriteDoesNotPublishOrChangeCachedSettings()
    {
        var service = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath);
        var original = service.Load();
        var notifications = 0;
        service.SettingsChanged += _ => notifications++;
        Directory.CreateDirectory(SettingsPath); // A directory cannot be replaced by the settings file.
        Assert.Throws<UnauthorizedAccessException>(() => service.Save(original with { StartMinimized = !original.StartMinimized }));
        Assert.Same(original, service.Load());
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void LegacySettingsWithoutLivenessRequestRemainSafelyOff()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """
            {
              "startMinimized": false
            }
            """);

        var settings = new JsonSettingsService(
            NullLogger<JsonSettingsService>.Instance,
            SettingsPath).Load();

        Assert.False(settings.StartMinimized);
        Assert.Null(settings.SessionSegmentLiveness);
    }

    [Fact]
    public void WrongTopLevelLivenessTokenFailsClosedWithoutDiscardingOtherSettings()
    {
        var settings = LoadRawSettings("""
            {
              "startMinimized": false,
              "startWithWindows": true,
              "desktopCaptureEnabled": true,
              "sessionSegmentLiveness": "invalid"
            }
            """);

        AssertUnrelatedSettingsPreserved(settings);
        Assert.Null(settings.SessionSegmentLiveness);
    }

    [Fact]
    public void WrongNestedLivenessPolicyTokenFailsClosedWithoutDiscardingOtherSettings()
    {
        var settings = LoadRawSettings("""
            {
              "startMinimized": false,
              "startWithWindows": true,
              "desktopCaptureEnabled": true,
              "sessionSegmentLiveness": {
                "enabled": true,
                "policy": "invalid"
              }
            }
            """);

        AssertUnrelatedSettingsPreserved(settings);
        Assert.Null(settings.SessionSegmentLiveness);
    }

    [Fact]
    public void WrongNestedLivenessPolicyScalarFailsClosedWithoutDiscardingOtherSettings()
    {
        var settings = LoadRawSettings("""
            {
              "startMinimized": false,
              "startWithWindows": true,
              "desktopCaptureEnabled": true,
              "sessionSegmentLiveness": {
                "enabled": true,
                "policy": {
                  "pollIntervalMilliseconds": "250"
                }
              }
            }
            """);

        AssertUnrelatedSettingsPreserved(settings);
        Assert.Null(settings.SessionSegmentLiveness);
    }

    [Fact]
    public void WrongLivenessEnabledScalarFailsClosedWithoutDiscardingOtherSettings()
    {
        var settings = LoadRawSettings("""
            {
              "startMinimized": false,
              "startWithWindows": true,
              "desktopCaptureEnabled": true,
              "sessionSegmentLiveness": {
                "enabled": "true",
                "policy": null
              }
            }
            """);

        AssertUnrelatedSettingsPreserved(settings);
        Assert.Null(settings.SessionSegmentLiveness);
    }

    [Fact]
    public void ExplicitNullLivenessRequestRemainsSafelyOffWithoutDiscardingOtherSettings()
    {
        var settings = LoadRawSettings("""
            {
              "startMinimized": false,
              "startWithWindows": true,
              "desktopCaptureEnabled": true,
              "sessionSegmentLiveness": null
            }
            """);

        AssertUnrelatedSettingsPreserved(settings);
        Assert.Null(settings.SessionSegmentLiveness);
    }

    [Fact]
    public void NewInstallLeavesDesktopCaptureOff()
    {
        var settings = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath).Load();

        Assert.False(settings.DesktopCaptureEnabled);
    }

    [Fact]
    public void ExistingSettingsThatEnabledDesktopCaptureKeepIt()
    {
        // Settings written by v1.2.x-v1.4.0 store the field explicitly, so upgrading users keep their choice.
        var settings = LoadRawSettings("""
            {
              "desktopCaptureEnabled": true
            }
            """);

        Assert.True(settings.DesktopCaptureEnabled);
    }

    [Fact]
    public void MalformedUnrelatedFieldStillFallsBackToWholeSettingsDefaults()
    {
        var settings = LoadRawSettings("""
            {
              "startMinimized": "false",
              "desktopCaptureEnabled": true,
              "sessionSegmentLiveness": {
                "enabled": true,
                "policy": {
                  "pollIntervalMilliseconds": 250,
                  "startupTimeoutMilliseconds": 5000,
                  "stallTimeoutMilliseconds": 5000,
                  "telemetryTimeoutMilliseconds": 2000,
                  "providerExecutionTimeoutMilliseconds": 2000
                }
              }
            }
            """);

        var defaults = new AppSettings();
        Assert.Equal(defaults.StartMinimized, settings.StartMinimized);
        Assert.Equal(defaults.DesktopCaptureEnabled, settings.DesktopCaptureEnabled);
        Assert.Null(settings.SessionSegmentLiveness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LivenessRequestRoundTripsWithoutBecomingEffective(bool enabled)
    {
        var service = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath);
        var original = service.Load();
        service.Save(original with
        {
            SessionSegmentLiveness = LivenessRequest(enabled),
        });

        var reloaded = new JsonSettingsService(
            NullLogger<JsonSettingsService>.Instance,
            SettingsPath).Load();

        var request = Assert.IsType<SessionSegmentLivenessSettings>(
            reloaded.SessionSegmentLiveness);
        Assert.Equal(enabled, request.Enabled);
        Assert.Equal(250, request.Policy!.PollIntervalMilliseconds);
        Assert.Equal(2_000, request.Policy.ProviderExecutionTimeoutMilliseconds);
    }

    [Fact]
    public void SavedRequestRequiresFreshStartupDecision()
    {
        var service = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath);
        var defaults = service.Load();
        service.Save(defaults with
        {
            Recording = defaults.Recording with { Mode = RecordingMode.SessionRecording },
        });
        var startupSettings = service.Load();
        var startupDecision = SessionSegmentLivenessStartupGate.Evaluate(
            startupSettings.SessionSegmentLiveness,
            startupSettings.Recording.IsSessionRecordingEnabled,
            ObsSessionRecorder.SegmentFrameCounterCapabilities);

        service.Save(startupSettings with
        {
            SessionSegmentLiveness = LivenessRequest(enabled: true),
        });

        Assert.Equal(SessionSegmentLivenessStartupState.Disabled, startupDecision.State);
        Assert.False(startupDecision.RequestedEnabled);
        var nextStartupSettings = new JsonSettingsService(
            NullLogger<JsonSettingsService>.Instance,
            SettingsPath).Load();
        var nextStartupDecision = SessionSegmentLivenessStartupGate.Evaluate(
            nextStartupSettings.SessionSegmentLiveness,
            nextStartupSettings.Recording.IsSessionRecordingEnabled,
            ObsSessionRecorder.SegmentFrameCounterCapabilities);
        Assert.True(nextStartupDecision.RequestedEnabled);
        Assert.Equal(
            SessionSegmentLivenessStartupState.ProviderNotAdmitted,
            nextStartupDecision.State);
        Assert.Null(nextStartupDecision.EffectivePolicy);
    }

    [Fact]
    public void FailedWriteDoesNotChangeCachedLivenessRequest()
    {
        var service = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath);
        var original = service.Load();
        service.Save(original with
        {
            SessionSegmentLiveness = LivenessRequest(enabled: true),
        });
        var saved = service.Load();
        File.Delete(SettingsPath);
        Directory.CreateDirectory(SettingsPath);

        Assert.Throws<UnauthorizedAccessException>(() => service.Save(saved with
        {
            SessionSegmentLiveness = LivenessRequest(enabled: false),
        }));
        Assert.Same(saved, service.Load());
        Assert.True(service.Load().SessionSegmentLiveness!.Enabled);
    }

    [Fact]
    public void StableDisplayIdentitySurvivesReloadIndependentlyOfMonitorIndex()
    {
        var service = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath);
        var original = service.Load();
        service.Save(original with
        {
            Capture = original.Capture with
            {
                MonitorIndex = 7,
                MonitorStableId = @"\\?\DISPLAY#FIXTURE#stable",
            },
        });

        var reloaded = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath).Load();

        Assert.Equal(7, reloaded.Capture.MonitorIndex);
        Assert.Equal(@"\\?\DISPLAY#FIXTURE#stable", reloaded.Capture.MonitorStableId);
    }

    [Fact]
    public void ExplicitAudioEndpointIdsSurviveReloadWithoutDefaultCoercion()
    {
        var service = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath);
        var original = service.Load();
        service.Save(original with
        {
            Audio = original.Audio with
            {
                DesktopDeviceId = "{0.0.0.00000000}.missing-output",
                MicDeviceId = "{0.0.1.00000000}.missing-input",
            },
        });

        var reloaded = new JsonSettingsService(
            NullLogger<JsonSettingsService>.Instance,
            SettingsPath).Load();

        Assert.Equal("{0.0.0.00000000}.missing-output", reloaded.Audio.DesktopDeviceId);
        Assert.Equal("{0.0.1.00000000}.missing-input", reloaded.Audio.MicDeviceId);
    }

    [Fact]
    public void UnavailableExactWebcamSelectionSurvivesReloadWithoutFallback()
    {
        const string exactDeviceId =
            @"EMEET SmartCam C60E 4K:\\?\usb#22vid_328f&pid_00a5&mi_00#22fixture#22{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global";
        var service = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath);
        var original = service.Load();
        service.Save(original with
        {
            Webcam = new WebcamConfig
            {
                EnabledForExplicitManualRecording = true,
                DeviceId = exactDeviceId,
                DisplayName = "EMEET SmartCam C60E 4K",
                Width = 1280,
                Height = 720,
                FramesPerSecond = 30,
                VideoFormat = "MJPEG",
                VideoBitrateKbps = 1_750,
            },
        });

        var reloaded = new JsonSettingsService(
            NullLogger<JsonSettingsService>.Instance,
            SettingsPath).Load();

        Assert.True(reloaded.Webcam.EnabledForExplicitManualRecording);
        Assert.Equal(exactDeviceId, reloaded.Webcam.DeviceId);
        Assert.Equal("EMEET SmartCam C60E 4K", reloaded.Webcam.DisplayName);
        Assert.Equal(1280, reloaded.Webcam.Width);
        Assert.Equal(720, reloaded.Webcam.Height);
        Assert.Equal(30, reloaded.Webcam.FramesPerSecond);
        Assert.Equal("MJPEG", reloaded.Webcam.VideoFormat);
        Assert.Equal(1_750, reloaded.Webcam.VideoBitrateKbps);
    }

    [Fact]
    public void RelativeDestinationIsRejectedWithoutChangingSettings()
    {
        var service = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance, SettingsPath);
        var original = service.Load();
        Assert.Throws<ArgumentException>(() => service.Save(original with
        {
            Storage = original.Storage with { SavePath = "relative-clips" },
        }));
        Assert.Same(original, service.Load());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static SessionSegmentLivenessSettings LivenessRequest(bool enabled) =>
        new()
        {
            Enabled = enabled,
            Policy = new SessionSegmentLivenessPolicySettings
            {
                PollIntervalMilliseconds = 250,
                StartupTimeoutMilliseconds = 5_000,
                StallTimeoutMilliseconds = 5_000,
                TelemetryTimeoutMilliseconds = 2_000,
                ProviderExecutionTimeoutMilliseconds = 2_000,
            },
        };

    private AppSettings LoadRawSettings(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, json);
        return new JsonSettingsService(
            NullLogger<JsonSettingsService>.Instance,
            SettingsPath).Load();
    }

    private static void AssertUnrelatedSettingsPreserved(AppSettings settings)
    {
        Assert.False(settings.StartMinimized);
        Assert.True(settings.StartWithWindows);
        Assert.True(settings.DesktopCaptureEnabled);
    }
}
