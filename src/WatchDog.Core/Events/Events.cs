using WatchDog.Core.Capture;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Highlights;

namespace WatchDog.Core.Events;

public sealed record GameDetectedEvent(GameInfo Game);

public sealed record GameExitedEvent(GameInfo Game);

public sealed record ClipSavedEvent(
    string FilePath, GameInfo? Game, DateTimeOffset Timestamp,
    Guid? SessionId = null, int? MatchNumber = null);

public sealed record BufferStateChangedEvent(CaptureState NewState);

public sealed record CaptureErrorEvent(string Message, Exception? Exception = null);

public sealed record SessionRecordingStartedEvent(string OutputPath, GameInfo? Game);

public sealed record SessionRecordingSegmentSavedEvent(string FilePath, GameInfo? Game, TimeSpan Elapsed);

public sealed record SessionRecordingStoppedEvent(GameInfo? Game, TimeSpan TotalDuration);

public sealed record HighlightDetectedEvent(
    HighlightType Type,
    GameInfo Game,
    DateTimeOffset Timestamp,
    string? Description = null);

// Session lifecycle events
public sealed record SessionStartedEvent(Guid SessionId, GameInfo Game);
public sealed record SessionEndedEvent(Guid SessionId, GameInfo Game, TimeSpan Duration);
public sealed record MatchStartedEvent(Guid SessionId, int MatchNumber, GameInfo Game);
public sealed record MatchEndedEvent(Guid SessionId, int MatchNumber, Sessions.MatchResult Result, string? Score);

public sealed record DiscordUploadStartedEvent(string FilePath, string? GameName);

public sealed record DiscordUploadCompletedEvent(string FilePath, bool Success, string? ErrorMessage = null);
