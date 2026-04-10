namespace WatchDog.Core.GameDetection;

public sealed record GameInfo
{
    public required string ExecutableName { get; init; }
    public required string DisplayName { get; init; }
    public int ProcessId { get; init; }
    public string? WindowTitle { get; init; }
    public GameGenre Genre { get; init; } = GameGenre.Unknown;
}

/// <summary>
/// Broad genre classification used to determine default recording mode.
/// FPS/MOBA/Fighting → highlight-compatible. Strategy/Sim/Racing → session or buffer.
/// </summary>
public enum GameGenre
{
    Unknown,
    FPS,
    BattleRoyale,
    MOBA,
    RPG,
    Survival,
    Strategy,
    Racing,
    Horror,
    MMO,
    Fighting,
    Sports,
    Sandbox,
    Platformer,
    Sim,
}

