using TikrClipr.Core.Highlights.RainbowSixSiege;

namespace TikrClipr.Core.Tests.Highlights;

public sealed class R6LogLineParserTests
{
    [Fact]
    public void ParseLine_Kill_ReturnsKill()
    {
        var result = R6LogLineParser.ParseLine("[KILL] Player eliminated Enemy");

        Assert.NotNull(result);
        Assert.Equal(R6LogEventType.Kill, result.Type);
    }

    [Fact]
    public void ParseLine_Death_ReturnsDeath()
    {
        var result = R6LogLineParser.ParseLine("[DEATH] Player was eliminated");

        Assert.NotNull(result);
        Assert.Equal(R6LogEventType.Death, result.Type);
    }

    [Fact]
    public void ParseLine_Assist_ReturnsAssist()
    {
        var result = R6LogLineParser.ParseLine("[ASSIST] Player assisted on kill");

        Assert.NotNull(result);
        Assert.Equal(R6LogEventType.Assist, result.Type);
    }

    [Fact]
    public void ParseLine_BombPlant_ReturnsBombPlant()
    {
        var result = R6LogLineParser.ParseLine("bomb has been planted");

        Assert.NotNull(result);
        Assert.Equal(R6LogEventType.BombPlant, result.Type);
    }

    [Fact]
    public void ParseLine_BombDefuse_ReturnsBombDefuse()
    {
        var result = R6LogLineParser.ParseLine("bomb has been defused");

        Assert.NotNull(result);
        Assert.Equal(R6LogEventType.BombDefuse, result.Type);
    }

    [Fact]
    public void ParseLine_RoundStart_ReturnsRoundStart()
    {
        var result = R6LogLineParser.ParseLine("[ROUND_START]");

        Assert.NotNull(result);
        Assert.Equal(R6LogEventType.RoundStart, result.Type);
    }

    [Fact]
    public void ParseLine_RoundEnd_ReturnsRoundEnd()
    {
        var result = R6LogLineParser.ParseLine("[ROUND_END]");

        Assert.NotNull(result);
        Assert.Equal(R6LogEventType.RoundEnd, result.Type);
    }

    [Fact]
    public void ParseLine_MatchEnd_ReturnsMatchEnd()
    {
        var result = R6LogLineParser.ParseLine("[MATCH_END]");

        Assert.NotNull(result);
        Assert.Equal(R6LogEventType.MatchEnd, result.Type);
    }

    [Fact]
    public void ParseLine_Score_ReturnsScoreUpdate()
    {
        var result = R6LogLineParser.ParseLine("[SCORE] Attackers: 3 - Defenders: 2");

        Assert.NotNull(result);
        Assert.Equal(R6LogEventType.ScoreUpdate, result.Type);
        Assert.Equal("3-2", result.Details);
    }

    [Fact]
    public void ParseLine_UnrecognizedLine_ReturnsNull()
    {
        Assert.Null(R6LogLineParser.ParseLine(""));
        Assert.Null(R6LogLineParser.ParseLine("  "));
        Assert.Null(R6LogLineParser.ParseLine("random log output"));
    }

    [Fact]
    public void ParseLine_CaseInsensitive_Kill()
    {
        var result = R6LogLineParser.ParseLine("[kill] player eliminated enemy");
        Assert.NotNull(result);
        Assert.Equal(R6LogEventType.Kill, result.Type);
    }
}
