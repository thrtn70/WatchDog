using WatchDog.Core.Settings;

namespace WatchDog.Core.Tests.Settings;

public sealed class DiscordSettingsTests
{
    [Fact]
    public void Default_HasEmptyWebhookUrl()
    {
        var settings = new DiscordSettings();
        Assert.Equal(string.Empty, settings.WebhookUrl);
    }

    [Fact]
    public void Default_HasWatchDogUsername()
    {
        var settings = new DiscordSettings();
        Assert.Equal("WatchDog", settings.Username);
    }

    [Fact]
    public void Default_IncludeEmbedIsTrue()
    {
        var settings = new DiscordSettings();
        Assert.True(settings.IncludeEmbed);
    }

    [Fact]
    public void AppSettings_IncludesDiscordDefaults()
    {
        var app = new AppSettings();
        Assert.NotNull(app.Discord);
        Assert.Equal(string.Empty, app.Discord.WebhookUrl);
    }

    [Fact]
    public void WithExpression_CreatesNewInstance()
    {
        var original = new DiscordSettings();
        var updated = original with { WebhookUrl = "https://discord.com/api/webhooks/123/abc" };

        Assert.NotEqual(original.WebhookUrl, updated.WebhookUrl);
        Assert.Equal("WatchDog", updated.Username); // unchanged
    }
}
