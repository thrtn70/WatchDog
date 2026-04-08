using Microsoft.Extensions.Logging.Abstractions;
using TikrClipr.Core.Highlights;
using TikrClipr.Core.Highlights.Valorant;

namespace TikrClipr.Core.Tests.Highlights;

public sealed class ValorantHighlightDetectorTests
{
    private readonly ValorantHighlightDetector _detector = new(NullLogger<ValorantHighlightDetector>.Instance);

    [Fact]
    public void DetectHighlights_KillIncrease_EmitsKill()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new ValorantGameState { Kills = 2, Health = 100 };
        var curr = new ValorantGameState { Kills = 3, Health = 100 };

        _detector.DetectHighlights(prev, curr);

        Assert.Contains(events, e => e.Type == HighlightType.Kill);
    }

    [Fact]
    public void DetectHighlights_HealthDropsToZero_EmitsDeath()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new ValorantGameState { Health = 100 };
        var curr = new ValorantGameState { Health = 0 };

        _detector.DetectHighlights(prev, curr);

        Assert.Single(events);
        Assert.Equal(HighlightType.Death, events[0].Type);
    }

    [Fact]
    public void DetectHighlights_FiveRoundKills_EmitsAce()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new ValorantGameState { RoundKills = 4, Kills = 9, Health = 100 };
        var curr = new ValorantGameState { RoundKills = 5, Kills = 10, Health = 100 };

        _detector.DetectHighlights(prev, curr);

        Assert.Contains(events, e => e.Type == HighlightType.Kill);
        Assert.Contains(events, e => e.Type == HighlightType.Ace);
        Assert.DoesNotContain(events, e => e.Type == HighlightType.Multikill);
    }

    [Fact]
    public void DetectHighlights_ThreeRoundKills_EmitsMultikill()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new ValorantGameState { RoundKills = 2, Kills = 5, Health = 100 };
        var curr = new ValorantGameState { RoundKills = 3, Kills = 6, Health = 100 };

        _detector.DetectHighlights(prev, curr);

        Assert.Contains(events, e => e.Type == HighlightType.Multikill);
    }

    [Fact]
    public void DetectHighlights_RoundWon_EmitsRoundWin()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new ValorantGameState { RoundPhase = "combat", TeamScore = 5, EnemyScore = 3 };
        var curr = new ValorantGameState { RoundPhase = "end", TeamScore = 6, EnemyScore = 3 };

        _detector.DetectHighlights(prev, curr);

        Assert.Single(events);
        Assert.Equal(HighlightType.RoundWin, events[0].Type);
    }

    [Fact]
    public void DetectHighlights_RoundLost_EmitsRoundLoss()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new ValorantGameState { RoundPhase = "combat", TeamScore = 5, EnemyScore = 5 };
        var curr = new ValorantGameState { RoundPhase = "end", TeamScore = 5, EnemyScore = 6 };

        _detector.DetectHighlights(prev, curr);

        Assert.Single(events);
        Assert.Equal(HighlightType.RoundLoss, events[0].Type);
    }

    [Fact]
    public void DetectHighlights_MatchWin_EmitsMatchWin()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new ValorantGameState { MatchPhase = "in_progress", TeamScore = 13, EnemyScore = 8 };
        var curr = new ValorantGameState { MatchPhase = "completed", TeamScore = 13, EnemyScore = 8 };

        _detector.DetectHighlights(prev, curr);

        Assert.Single(events);
        Assert.Equal(HighlightType.MatchWin, events[0].Type);
    }

    [Fact]
    public void DetectHighlights_SpikePlanted_EmitsBombPlant()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        var prev = new ValorantGameState { SpikeState = false };
        var curr = new ValorantGameState { SpikeState = true };

        _detector.DetectHighlights(prev, curr);

        Assert.Single(events);
        Assert.Equal(HighlightType.BombPlant, events[0].Type);
    }

    [Fact]
    public void DetectHighlights_NoPreviousState_NoEvents()
    {
        var events = new List<HighlightDetectedEventArgs>();
        _detector.HighlightDetected += e => events.Add(e);

        _detector.DetectHighlights(null, new ValorantGameState { Kills = 3 });

        Assert.Empty(events);
    }

    [Fact]
    public void SupportedExecutableNames_ContainsBothExes()
    {
        Assert.Contains("valorant-win64-shipping.exe", _detector.SupportedExecutableNames);
        Assert.Contains("valorant.exe", _detector.SupportedExecutableNames);
    }
}
