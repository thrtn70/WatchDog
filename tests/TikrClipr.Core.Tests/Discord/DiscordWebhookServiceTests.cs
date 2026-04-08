using Microsoft.Extensions.Logging.Abstractions;
using TikrClipr.Core.ClipEditor;
using TikrClipr.Core.Discord;
using TikrClipr.Core.Events;
using TikrClipr.Core.Settings;

namespace TikrClipr.Core.Tests.Discord;

public sealed class DiscordWebhookServiceTests
{
    private static ClipMetadata CreateTestMetadata() => new()
    {
        FilePath = @"C:\Videos\test_clip.mp4",
        FileName = "test_clip.mp4",
        GameName = "Counter-Strike 2",
        CreatedAt = DateTimeOffset.UtcNow,
        Duration = TimeSpan.FromSeconds(30),
        FileSizeBytes = 5 * 1024 * 1024,
    };

    private static DiscordWebhookService CreateService(AppSettings? settings = null)
    {
        var settingsService = new StubSettingsService(settings ?? new AppSettings());
        var eventBus = new InMemoryEventBus();
        var httpClient = new HttpClient();
        var logger = NullLogger<DiscordWebhookService>.Instance;
        return new DiscordWebhookService(httpClient, settingsService, eventBus, logger);
    }

    [Fact]
    public async Task UploadClipAsync_ReturnsError_WhenWebhookUrlIsEmpty()
    {
        var service = CreateService();
        var result = await service.UploadClipAsync("test.mp4", CreateTestMetadata());

        Assert.False(result.Success);
        Assert.Contains("No Discord webhook URL", result.ErrorMessage);
    }

    [Fact]
    public async Task UploadClipAsync_ReturnsError_WhenFileNotFound()
    {
        var settings = new AppSettings
        {
            Discord = new DiscordSettings { WebhookUrl = "https://discord.com/api/webhooks/123/abc" }
        };
        var service = CreateService(settings);
        var result = await service.UploadClipAsync(@"C:\nonexistent\clip.mp4", CreateTestMetadata());

        Assert.False(result.Success);
        Assert.Contains("not found", result.ErrorMessage);
    }

    [Fact]
    public async Task ValidateWebhookUrlAsync_ReturnsFalse_ForEmptyUrl()
    {
        var service = CreateService();
        var valid = await service.ValidateWebhookUrlAsync("");

        Assert.False(valid);
    }

    [Fact]
    public async Task ValidateWebhookUrlAsync_ReturnsFalse_ForNonDiscordUrl()
    {
        var service = CreateService();
        var valid = await service.ValidateWebhookUrlAsync("https://example.com/webhook");

        Assert.False(valid);
    }

    [Fact]
    public async Task ValidateWebhookUrlAsync_ReturnsFalse_ForInvalidUri()
    {
        var service = CreateService();
        var valid = await service.ValidateWebhookUrlAsync("not a url");

        Assert.False(valid);
    }

    /// <summary>
    /// Minimal stub for ISettingsService used in tests.
    /// </summary>
    private sealed class StubSettingsService : ISettingsService
    {
        private AppSettings _settings;

        public StubSettingsService(AppSettings settings) => _settings = settings;

        public AppSettings Load() => _settings;
        public void Save(AppSettings settings) => _settings = settings;
        public event Action<AppSettings>? SettingsChanged;

        // Suppress unused warning — event is required by interface
        private void SuppressWarning() => SettingsChanged?.Invoke(_settings);
    }
}
