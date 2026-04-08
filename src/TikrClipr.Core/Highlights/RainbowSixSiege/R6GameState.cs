namespace TikrClipr.Core.Highlights.RainbowSixSiege;

internal sealed record R6GameState
{
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public int RoundKills { get; init; }
    public string? RoundPhase { get; init; }   // "action", "prep", "end"
    public string? MatchPhase { get; init; }   // "in_progress", "completed"
    public int TeamScore { get; init; }
    public int EnemyScore { get; init; }
    public string? OperatorName { get; init; }
}
