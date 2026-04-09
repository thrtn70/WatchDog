namespace WatchDog.Core.Highlights.Overwatch2;

internal sealed record Ow2GameState
{
    public int Eliminations { get; init; }
    public int Deaths { get; init; }
    public int Assists { get; init; }
    public int RoundEliminations { get; init; }
    public string? HeroName { get; init; }
    public bool IsUltimateActive { get; init; }
    public string? MatchPhase { get; init; }   // "in_progress", "completed"
    public string? RoundPhase { get; init; }   // "live", "over"
    public int TeamScore { get; init; }
    public int EnemyScore { get; init; }
}
