namespace TikrClipr.Core.Highlights.Cs2;

internal sealed record Cs2GameState
{
    public int Kills { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public int Health { get; init; }
    public int RoundKills { get; init; }
    public string? RoundPhase { get; init; }   // "live", "freezetime", "over"
    public string? MapPhase { get; init; }     // "warmup", "live", "intermission", "gameover"
    public int TeamScore { get; init; }
    public int EnemyScore { get; init; }
    public string? PlayerTeam { get; init; }   // "CT" or "T"
}
