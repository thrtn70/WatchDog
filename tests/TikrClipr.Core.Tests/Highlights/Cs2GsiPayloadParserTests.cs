using TikrClipr.Core.Highlights.Cs2;

namespace TikrClipr.Core.Tests.Highlights;

public sealed class Cs2GsiPayloadParserTests
{
    [Fact]
    public void Parse_ValidPayload_ReturnsState()
    {
        var json = """
        {
            "provider": { "steamid": "123" },
            "player": {
                "steamid": "123",
                "team": "CT",
                "state": { "health": 100, "round_kills": 2 },
                "match_stats": { "kills": 5, "deaths": 2, "assists": 1 }
            },
            "round": { "phase": "live" },
            "map": {
                "phase": "live",
                "team_ct": { "score": 3 },
                "team_t": { "score": 2 }
            }
        }
        """;

        var state = Cs2GsiPayloadParser.Parse(json);

        Assert.NotNull(state);
        Assert.Equal(5, state.Kills);
        Assert.Equal(2, state.Deaths);
        Assert.Equal(100, state.Health);
        Assert.Equal(2, state.RoundKills);
        Assert.Equal("live", state.RoundPhase);
        Assert.Equal("CT", state.PlayerTeam);
        Assert.Equal(3, state.TeamScore);   // CT score (player is CT)
        Assert.Equal(2, state.EnemyScore);  // T score
    }

    [Fact]
    public void Parse_SpectatorPayload_ReturnsNull()
    {
        var json = """
        {
            "provider": { "steamid": "123" },
            "player": {
                "steamid": "456",
                "team": "T",
                "state": { "health": 80 },
                "match_stats": { "kills": 10 }
            }
        }
        """;

        var state = Cs2GsiPayloadParser.Parse(json);

        Assert.Null(state); // Different steamids = spectating
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsNull()
    {
        var state = Cs2GsiPayloadParser.Parse("not json");
        Assert.Null(state);
    }

    [Fact]
    public void Parse_TerroristTeam_SwapsScores()
    {
        var json = """
        {
            "provider": { "steamid": "123" },
            "player": {
                "steamid": "123",
                "team": "T",
                "state": { "health": 100 },
                "match_stats": { "kills": 3 }
            },
            "map": {
                "phase": "live",
                "team_ct": { "score": 7 },
                "team_t": { "score": 5 }
            }
        }
        """;

        var state = Cs2GsiPayloadParser.Parse(json);

        Assert.NotNull(state);
        Assert.Equal(5, state.TeamScore);   // T score (player is T)
        Assert.Equal(7, state.EnemyScore);  // CT score
    }
}
