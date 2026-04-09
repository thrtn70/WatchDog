namespace WatchDog.Core.Sessions;

public sealed record GameSession
{
    public required Guid Id { get; init; }
    public required string GameName { get; init; }
    public required string GameExecutableName { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public SessionStatus Status { get; init; } = SessionStatus.InProgress;
    public IReadOnlyList<SessionMatch> Matches { get; init; } = [];
    public IReadOnlyList<string> RecordingPaths { get; init; } = [];
    public bool IsAutoRecorded { get; init; }
}
