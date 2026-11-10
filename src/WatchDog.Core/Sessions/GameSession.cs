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
    public IReadOnlyList<SessionMediaSegment> MediaSegments { get; init; } = [];
    public bool IsAutoRecorded { get; init; }

    /// <summary>
    /// True for sessions started by v1.4.2 or later. Media of a session without it, one written
    /// by v1.4.1 or older, does not count toward the storage limit: cleanup never deletes session
    /// media, so counting an upgraded library's recordings could block capture for good.
    /// </summary>
    public bool CountsTowardStorageLimit { get; init; }
}
