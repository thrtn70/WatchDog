using TikrClipr.Core.Capture;
using TikrClipr.Core.GameDetection;

namespace TikrClipr.Core.Events;

public sealed record GameDetectedEvent(GameInfo Game);

public sealed record GameExitedEvent(GameInfo Game);

public sealed record ClipSavedEvent(string FilePath, GameInfo? Game, DateTimeOffset Timestamp);

public sealed record BufferStateChangedEvent(CaptureState NewState);

public sealed record CaptureErrorEvent(string Message, Exception? Exception = null);
