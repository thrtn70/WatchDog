using WatchDog.Core.Highlights;

namespace WatchDog.Core.ClipEditor;

public sealed record ClipMetadata
{
    public required string FilePath { get; init; }
    public required string FileName { get; init; }
    public string? GameName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public TimeSpan Duration { get; init; }
    public long FileSizeBytes { get; init; }
    public string? ThumbnailPath { get; init; }
    public bool IsFavorite { get; init; }
    public HighlightType? HighlightType { get; init; }
    public Guid? SessionId { get; init; }
    public Guid? CaptureRequestId { get; init; }
    public long? CaptureGeneration { get; init; }
    public int? MatchNumber { get; init; }

    /// <summary>User-assigned tags for clip organization (e.g., "montage", "funny", "clutch").</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Source of highlight detection: "native", "ai-audio", or "manual".</summary>
    public string? HighlightSource { get; init; }

    /// <summary>
    /// True only for clips this version indexed from a capture receipt. Storage cleanup never
    /// deletes, or counts toward the quota, a clip without it: entries written by v1.4.1 and
    /// older, whose cleanup never ran on existing libraries, and files of unknown origin.
    /// </summary>
    public bool AutoDeleteEligible { get; init; }
}
