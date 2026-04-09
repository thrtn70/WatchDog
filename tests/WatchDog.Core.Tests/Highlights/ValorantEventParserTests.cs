using WatchDog.Core.Highlights.Valorant;

namespace WatchDog.Core.Tests.Highlights;

public sealed class ValorantEventParserTests
{
    [Fact]
    public void Parse_ValidJson_ReturnsGameState()
    {
        var json = """
        {
            "kills": 5,
            "deaths": 2,
            "assists": 3,
            "health": 100,
            "roundKills": 2,
            "roundPhase": "combat",
            "matchPhase": "in_progress",
            "teamScore": 7,
            "enemyScore": 5,
            "agentName": "Jett",
            "spikePlanted": false
        }
        """;

        var state = ValorantEventParser.Parse(json);

        Assert.NotNull(state);
        Assert.Equal(5, state.Kills);
        Assert.Equal(2, state.Deaths);
        Assert.Equal(3, state.Assists);
        Assert.Equal(100, state.Health);
        Assert.Equal(2, state.RoundKills);
        Assert.Equal("combat", state.RoundPhase);
        Assert.Equal("in_progress", state.MatchPhase);
        Assert.Equal(7, state.TeamScore);
        Assert.Equal(5, state.EnemyScore);
        Assert.Equal("Jett", state.AgentName);
        Assert.False(state.SpikeState);
    }

    [Fact]
    public void Parse_EmptyJson_ReturnsNull()
    {
        Assert.Null(ValorantEventParser.Parse(""));
        Assert.Null(ValorantEventParser.Parse(null!));
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsNull()
    {
        Assert.Null(ValorantEventParser.Parse("not json at all"));
    }

    [Fact]
    public void Parse_PartialJson_FillsDefaults()
    {
        var json = """{ "kills": 3 }""";

        var state = ValorantEventParser.Parse(json);

        Assert.NotNull(state);
        Assert.Equal(3, state.Kills);
        Assert.Equal(0, state.Deaths);
        Assert.Equal(100, state.Health); // default for health
        Assert.Null(state.RoundPhase);
    }

    [Fact]
    public void ParseSessionPayload_NestedStructure_ExtractsState()
    {
        var json = """
        {
            "player": {
                "kills": 8,
                "deaths": 1,
                "assists": 4,
                "health": 50,
                "roundKills": 3,
                "agentName": "Reyna"
            },
            "match": {
                "phase": "in_progress",
                "teamScore": 10,
                "enemyScore": 7,
                "round": {
                    "phase": "combat",
                    "spikePlanted": true
                }
            }
        }
        """;

        var state = ValorantEventParser.ParseSessionPayload(json);

        Assert.NotNull(state);
        Assert.Equal(8, state.Kills);
        Assert.Equal(1, state.Deaths);
        Assert.Equal(50, state.Health);
        Assert.Equal("combat", state.RoundPhase);
        Assert.Equal("in_progress", state.MatchPhase);
        Assert.Equal(10, state.TeamScore);
        Assert.True(state.SpikeState);
        Assert.Equal("Reyna", state.AgentName);
    }
}
