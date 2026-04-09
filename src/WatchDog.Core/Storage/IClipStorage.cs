using WatchDog.Core.ClipEditor;
using WatchDog.Core.Highlights;

namespace WatchDog.Core.Storage;

public interface IClipStorage
{
    IReadOnlyList<ClipMetadata> GetAllClips();
    IReadOnlyList<ClipMetadata> GetClipsByGame(string gameName);
    Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, CancellationToken ct = default);
    Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, HighlightType? highlightType, CancellationToken ct = default);
    Task<int> ScanAndIndexAsync(CancellationToken ct = default);
    void DeleteClip(string filePath);
    void ToggleFavorite(string filePath);
    Task RunCleanupAsync(CancellationToken ct = default);
}
