using TikrClipr.Core.Highlights;

namespace TikrClipr.Core.ClipEditor;

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
}
