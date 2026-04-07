using TikrClipr.Core.ClipEditor;

namespace TikrClipr.Core.Storage;

public interface IClipStorage
{
    IReadOnlyList<ClipMetadata> GetAllClips();
    IReadOnlyList<ClipMetadata> GetClipsByGame(string gameName);
    Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, CancellationToken ct = default);
    Task<int> ScanAndIndexAsync(CancellationToken ct = default);
    void DeleteClip(string filePath);
    void ToggleFavorite(string filePath);
    Task RunCleanupAsync(CancellationToken ct = default);
}
