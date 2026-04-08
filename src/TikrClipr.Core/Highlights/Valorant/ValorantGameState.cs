namespace TikrClipr.Core.Highlights.Valorant;

internal sealed record ValorantGameState
{
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public int Health { get; init; }
    public int RoundKills { get; init; }
    public string? RoundPhase { get; init; }   // "shopping", "combat", "end"
    public string? MatchPhase { get; init; }   // "in_progress", "completed"
    public int TeamScore { get; init; }
    public int EnemyScore { get; init; }
    public string? AgentName { get; init; }
    public bool SpikeState { get; init; }      // true if spike planted this round
}
