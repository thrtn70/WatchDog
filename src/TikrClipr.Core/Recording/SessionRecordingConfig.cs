namespace TikrClipr.Core.Recording;

public enum RecordingMode
{
    ReplayBufferOnly,
    SessionRecording,
    Both,
    Highlights,
}

public sealed record SessionRecordingConfig
{
    public RecordingMode Mode { get; init; } = RecordingMode.ReplayBufferOnly;
    public int MaxDurationMinutes { get; init; } = 0;        // 0 = unlimited
    public int SegmentDurationMinutes { get; init; } = 30;   // Split into segments
    public string FileFormat { get; init; } = "mp4";

    public bool IsSessionRecordingEnabled =>
        Mode is RecordingMode.SessionRecording or RecordingMode.Both;

    public bool IsReplayBufferEnabled =>
        Mode is RecordingMode.ReplayBufferOnly or RecordingMode.Both or RecordingMode.Highlights;

    public bool IsHighlightModeEnabled =>
        Mode is RecordingMode.Highlights;
}
