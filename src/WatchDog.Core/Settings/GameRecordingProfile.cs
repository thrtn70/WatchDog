namespace WatchDog.Core.Settings;

/// <summary>
/// Per-game recording configuration. Overrides the default capture settings
/// when this game is detected. Stored in AppSettings.GameProfiles.
/// </summary>
public sealed record GameRecordingProfile
{
    public required string GameExecutableName { get; init; }

    /// <summary>Recording mode for this game.</summary>
    public RecordingMode Mode { get; init; } = RecordingMode.ReplayBuffer;

    /// <summary>Resolution override. Null = use default from CaptureConfig.</summary>
    public int? OutputWidth { get; init; }
    public int? OutputHeight { get; init; }

    /// <summary>Bitrate override in kbps. Null = use default from CaptureConfig.</summary>
    public int? BitrateKbps { get; init; }

    /// <summary>Buffer duration override in seconds. Null = use default from BufferConfig.</summary>
    public int? BufferDurationSeconds { get; init; }

    /// <summary>
    /// AI highlight detection sensitivity (0.0–1.0).
    /// Lower = more clips (more false positives). Higher = fewer clips (more precise).
    /// Null = use default (0.6).
    /// </summary>
    public float? HighlightSensitivity { get; init; }

    /// <summary>
    /// Validates and clamps numeric fields to safe ranges after deserialization.
    /// Returns a new record with clamped values (immutable — does not mutate this instance).
    /// </summary>
    public GameRecordingProfile Sanitized() => this with
    {
        OutputWidth = OutputWidth is not null ? Math.Clamp(OutputWidth.Value, 640, 7680) : null,
        OutputHeight = OutputHeight is not null ? Math.Clamp(OutputHeight.Value, 360, 4320) : null,
        BitrateKbps = BitrateKbps is not null ? Math.Clamp(BitrateKbps.Value, 1000, 100_000) : null,
        BufferDurationSeconds = BufferDurationSeconds is not null ? Math.Clamp(BufferDurationSeconds.Value, 10, 1200) : null,
        HighlightSensitivity = HighlightSensitivity is not null ? Math.Clamp(HighlightSensitivity.Value, 0.1f, 1.0f) : null,
    };

    /// <summary>Whether to auto-start recording when this game launches.</summary>
    public bool AutoRecord { get; init; } = true;
}

public enum RecordingMode
{
    /// <summary>Replay buffer — press hotkey to save last N seconds.</summary>
    ReplayBuffer,

    /// <summary>Session recording — record the entire gameplay session.</summary>
    SessionRecording,

    /// <summary>Highlight mode — auto-clip on detected events (native or AI).</summary>
    Highlight,
}
