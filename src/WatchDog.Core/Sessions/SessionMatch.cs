namespace WatchDog.Core.Sessions;

public sealed record SessionMatch
{
    public required int MatchNumber { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? Map { get; init; }
    public string? Score { get; init; }
    public string? GameMode { get; init; }
    public MatchResult? Result { get; init; }
    public IReadOnlyList<string> ClipIds { get; init; } = [];
}
