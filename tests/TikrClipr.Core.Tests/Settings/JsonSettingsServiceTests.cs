using Microsoft.Extensions.Logging.Abstractions;
using TikrClipr.Core.Capture;
using TikrClipr.Core.Settings;

namespace TikrClipr.Core.Tests.Settings;

public sealed class JsonSettingsServiceTests
{
    [Fact]
    public void Load_ReturnsDefaults_WhenNoFileExists()
    {
        var service = new JsonSettingsService(NullLogger<JsonSettingsService>.Instance);
        var settings = service.Load();

        Assert.NotNull(settings);
        Assert.NotNull(settings.Capture);
        Assert.NotNull(settings.Buffer);
        Assert.NotNull(settings.Hotkey);
        Assert.NotNull(settings.Storage);
    }

    [Fact]
    public void DefaultCaptureConfig_HasExpectedValues()
    {
        var config = new CaptureConfig();

        Assert.Equal(1920u, config.OutputWidth);
        Assert.Equal(1080u, config.OutputHeight);
        Assert.Equal(60u, config.Fps);
        Assert.Equal(EncoderType.NvencH264, config.Encoder);
        Assert.Equal(RateControlType.CQP, config.RateControl);
        Assert.Equal(20, config.Quality);
    }

    [Fact]
    public void DefaultBufferConfig_HasExpectedValues()
    {
        var config = new Buffer.BufferConfig();

        Assert.Equal(120, config.MaxSeconds);
        Assert.Equal(512, config.MaxSizeMb);
        Assert.Contains("TikrClipr", config.OutputDirectory);
    }

    [Fact]
    public void DefaultHotkeySettings_UsesF9AndF10()
    {
        var hotkey = new HotkeySettings();

        Assert.Equal(0x78, hotkey.SaveClipKey);           // VK_F9
        Assert.Equal(0u, hotkey.Modifiers);
        Assert.Equal(0x79, hotkey.ToggleRecordingKey);    // VK_F10
        Assert.Equal(0u, hotkey.ToggleRecordingModifiers);
    }

    [Fact]
    public void DefaultStorageSettings_Has30DayRetention()
    {
        var storage = new StorageSettings();

        Assert.Equal(30, storage.AutoDeleteDays);
        Assert.Equal(50, storage.MaxStorageGb);
        Assert.Contains("TikrClipr", storage.SavePath);
    }

    [Fact]
    public void AppSettings_IsImmutable()
    {
        var settings = new AppSettings();
        var modified = settings with { StartWithWindows = true };

        Assert.False(settings.StartWithWindows);
        Assert.True(modified.StartWithWindows);
    }
}
