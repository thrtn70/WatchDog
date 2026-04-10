using WatchDog.Core.ClipEditor;
using WatchDog.Core.Highlights;

namespace WatchDog.Core.Storage;

public interface IClipStorage
{
    IReadOnlyList<ClipMetadata> GetAllClips();
    IReadOnlyList<ClipMetadata> GetClipsByGame(string gameName);
    IReadOnlyList<ClipMetadata> GetClipsBySession(Guid sessionId);
    Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, CancellationToken ct = default);
    Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, HighlightType? highlightType, CancellationToken ct = default);
    Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, HighlightType? highlightType, Guid? sessionId, int? matchNumber, CancellationToken ct = default);
    Task<int> ScanAndIndexAsync(CancellationToken ct = default);
    void DeleteClip(string filePath);
    void ToggleFavorite(string filePath);
    Task RunCleanupAsync(CancellationToken ct = default);

    /// <summary>Add tags to a clip. Tags are deduplicated and case-insensitive.</summary>
    void AddTags(string filePath, IEnumerable<string> tags);

    /// <summary>Remove tags from a clip.</summary>
    void RemoveTags(string filePath, IEnumerable<string> tags);

    /// <summary>Get all unique tags used across all clips (for autocomplete).</summary>
    IReadOnlySet<string> GetAllTags();

    /// <summary>Get clips matching a specific tag.</summary>
    IReadOnlyList<ClipMetadata> GetClipsByTag(string tag);
}
