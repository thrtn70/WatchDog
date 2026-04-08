using TikrClipr.Core.Highlights;

namespace TikrClipr.Core.Tests.Highlights;

public sealed class HighlightConfigTests
{
    [Fact]
    public void Defaults_Enabled_With15sDelay_5sCooldown()
    {
        var config = new HighlightConfig();

        Assert.True(config.Enabled);
        Assert.Equal(15, config.PostEventDelaySeconds);
        Assert.Equal(5, config.CooldownSeconds);
    }

    [Fact]
    public void DefaultEnabledTypes_IncludesKillDeathWinAce()
    {
        var config = new HighlightConfig();

        Assert.Contains(HighlightType.Kill, config.EnabledTypes);
        Assert.Contains(HighlightType.Death, config.EnabledTypes);
        Assert.Contains(HighlightType.RoundWin, config.EnabledTypes);
        Assert.Contains(HighlightType.MatchWin, config.EnabledTypes);
        Assert.Contains(HighlightType.Ace, config.EnabledTypes);
    }

    [Fact]
    public void DefaultEnabledTypes_ExcludesLossAndBombEvents()
    {
        var config = new HighlightConfig();

        Assert.DoesNotContain(HighlightType.RoundLoss, config.EnabledTypes);
        Assert.DoesNotContain(HighlightType.MatchLoss, config.EnabledTypes);
        Assert.DoesNotContain(HighlightType.BombPlant, config.EnabledTypes);
        Assert.DoesNotContain(HighlightType.BombDefuse, config.EnabledTypes);
    }

    [Fact]
    public void Config_IsImmutable()
    {
        var original = new HighlightConfig();
        var modified = original with { PostEventDelaySeconds = 10, CooldownSeconds = 3 };

        Assert.Equal(15, original.PostEventDelaySeconds);
        Assert.Equal(10, modified.PostEventDelaySeconds);
    }
}
