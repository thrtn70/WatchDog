using WatchDog.Core.Buffer;
using WatchDog.Core.Recording;

namespace WatchDog.Core.Capture;

/// <summary>Immutable, validated configuration for one next start; never live reconfiguration.</summary>
public sealed record CaptureStartPolicy(
    CaptureConfig Capture,
    BufferConfig Buffer,
    SessionRecordingConfig Recording)
{
    public CaptureFailure? Validate(CaptureConfig startupCanvas, bool allowLegacyHighlights = false)
    {
        if (EncoderCapabilities.Validate(Capture.Encoder) is { } encoderFailure)
            return encoderFailure;
        if (!Enum.IsDefined(Capture.RateControl) || !Enum.IsDefined(Recording.Mode))
            return Unsupported("capture.policy.undefined-mode", "Select a defined recording and rate-control mode in Settings.");
        if (Recording.IsHighlightModeEnabled && !allowLegacyHighlights)
            return Unsupported("capture.policy.highlight-unsupported", "Select Replay Buffer or Session Recording; next-start highlight configuration is unsupported.");
        if (Capture.OutputWidth != startupCanvas.OutputWidth ||
            Capture.OutputHeight != startupCanvas.OutputHeight || Capture.Fps != startupCanvas.Fps)
        {
            return Unsupported("capture.policy.canvas-change-unsupported",
                "Resolution/FPS changes require changing global settings and an application restart. Per-game canvas changes are unsupported.");
        }
        if (this != Sanitized())
            return new(CaptureFailureKind.InvalidRequest, "capture.policy.invalid-values", "Next-start configuration contains values outside supported bounds.");
        return null;
    }

    public CaptureStartPolicy Sanitized() => this with
    {
        Capture = Capture with
        {
            OutputWidth = Math.Clamp(Capture.OutputWidth, 640u, 7680u),
            OutputHeight = Math.Clamp(Capture.OutputHeight, 360u, 4320u),
            Fps = Math.Clamp(Capture.Fps, 1u, 120u),
            Quality = Math.Clamp(Capture.Quality, 0, 51),
            Bitrate = Math.Clamp(Capture.Bitrate, 1_000, 100_000),
            MaxBitrate = Math.Clamp(Math.Max(Capture.MaxBitrate, Capture.Bitrate), 1_000, 100_000),
            AudioBitrate = Math.Clamp(Capture.AudioBitrate, 64, 320),
        },
        Buffer = Buffer with
        {
            MaxSeconds = Math.Clamp(Buffer.MaxSeconds, 10, 1_200),
            MaxSizeMb = Math.Clamp(Buffer.MaxSizeMb, 16, 4_096),
        },
        Recording = Recording with
        {
            SegmentDurationMinutes = Math.Clamp(Recording.SegmentDurationMinutes, 0, 1_440),
            MaxDurationMinutes = Math.Clamp(Recording.MaxDurationMinutes, 0, 1_440),
        },
    };

    internal static CaptureFailure Unsupported(string code, string message) =>
        new(CaptureFailureKind.Unsupported, code, message);
}
