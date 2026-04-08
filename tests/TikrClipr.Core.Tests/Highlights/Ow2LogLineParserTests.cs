using TikrClipr.Core.Highlights.Overwatch2;

namespace TikrClipr.Core.Tests.Highlights;

public sealed class Ow2LogLineParserTests
{
    [Fact]
    public void ParseLine_Elimination_ReturnsElimination()
    {
        var result = Ow2LogLineParser.ParseLine("[ELIMINATION] Player killed Enemy with Pulse Rifle");

        Assert.NotNull(result);
        Assert.Equal(Ow2LogEventType.Elimination, result.Type);
        Assert.Contains("Player killed Enemy", result.Details);
    }

    [Fact]
    public void ParseLine_Death_ReturnsDeath()
    {
        var result = Ow2LogLineParser.ParseLine("[DEATH] Player died");

        Assert.NotNull(result);
        Assert.Equal(Ow2LogEventType.Death, result.Type);
    }

    [Fact]
    public void ParseLine_Assist_ReturnsAssist()
    {
        var result = Ow2LogLineParser.ParseLine("[ASSIST] Player assisted on Enemy elimination");

        Assert.NotNull(result);
        Assert.Equal(Ow2LogEventType.Assist, result.Type);
    }

    [Fact]
    public void ParseLine_Ultimate_ReturnsUltimate()
    {
        var result = Ow2LogLineParser.ParseLine("[ULTIMATE] Player activated ultimate ability");

        Assert.NotNull(result);
        Assert.Equal(Ow2LogEventType.UltimateActivated, result.Type);
    }

    [Fact]
    public void ParseLine_MatchStart_ReturnsMatchStart()
    {
        var result = Ow2LogLineParser.ParseLine("[MATCH_START]");

        Assert.NotNull(result);
        Assert.Equal(Ow2LogEventType.MatchStart, result.Type);
    }

    [Fact]
    public void ParseLine_MatchEnd_ReturnsMatchEnd()
    {
        var result = Ow2LogLineParser.ParseLine("[MATCH_END]");

        Assert.NotNull(result);
        Assert.Equal(Ow2LogEventType.MatchEnd, result.Type);
    }

    [Fact]
    public void ParseLine_RoundEnd_ReturnsRoundEnd()
    {
        var result = Ow2LogLineParser.ParseLine("[ROUND_END]");

        Assert.NotNull(result);
        Assert.Equal(Ow2LogEventType.RoundEnd, result.Type);
    }

    [Fact]
    public void ParseLine_Score_ReturnsScoreUpdate()
    {
        var result = Ow2LogLineParser.ParseLine("[SCORE] Team1: 2 - Team2: 1");

        Assert.NotNull(result);
        Assert.Equal(Ow2LogEventType.ScoreUpdate, result.Type);
        Assert.Equal("2-1", result.Details);
    }

    [Fact]
    public void ParseLine_DoubleKill_ReturnsMultikill()
    {
        var result = Ow2LogLineParser.ParseLine("DOUBLE KILL");

        Assert.NotNull(result);
        Assert.Equal(Ow2LogEventType.Multikill, result.Type);
        Assert.Equal("DOUBLE", result.Details);
    }

    [Fact]
    public void ParseLine_SextupleKill_ReturnsMultikill()
    {
        var result = Ow2LogLineParser.ParseLine("SEXTUPLE KILL");

        Assert.NotNull(result);
        Assert.Equal(Ow2LogEventType.Multikill, result.Type);
    }

    [Fact]
    public void ParseLine_UnrecognizedLine_ReturnsNull()
    {
        Assert.Null(Ow2LogLineParser.ParseLine(""));
        Assert.Null(Ow2LogLineParser.ParseLine("  "));
        Assert.Null(Ow2LogLineParser.ParseLine("random log output here"));
    }

    [Fact]
    public void ParseLine_CaseInsensitive()
    {
        var result = Ow2LogLineParser.ParseLine("[elimination] player got a kill");
        Assert.NotNull(result);
        Assert.Equal(Ow2LogEventType.Elimination, result.Type);
    }
}
