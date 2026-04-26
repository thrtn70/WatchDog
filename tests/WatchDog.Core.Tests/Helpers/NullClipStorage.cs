using WatchDog.Core.ClipEditor;
using WatchDog.Core.Highlights;
using WatchDog.Core.Storage;

namespace WatchDog.Core.Tests.Helpers;

/// <summary>
/// Minimal IClipStorage stub for tests that need to satisfy DI without exercising clip storage.
/// All queries return empty; all mutations are no-ops; Index methods throw.
/// </summary>
internal sealed class NullClipStorage : IClipStorage
{
    public IReadOnlyList<ClipMetadata> GetAllClips() => [];
    public IReadOnlyList<ClipMetadata> GetClipsByGame(string gameName) => [];
    public IReadOnlyList<ClipMetadata> GetClipsBySession(Guid sessionId) => [];

    public Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, HighlightType? highlightType, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, HighlightType? highlightType, Guid? sessionId, int? matchNumber, CancellationToken ct = default) =>
        throw new NotImplementedException();

    public Task<int> ScanAndIndexAsync(CancellationToken ct = default) => Task.FromResult(0);
    public void DeleteClip(string filePath) { }
    public void ToggleFavorite(string filePath) { }
    public Task RunCleanupAsync(CancellationToken ct = default) => Task.CompletedTask;

    public void AddTags(string filePath, IEnumerable<string> tags) { }
    public void RemoveTags(string filePath, IEnumerable<string> tags) { }
    public IReadOnlySet<string> GetAllTags() => new HashSet<string>();
    public IReadOnlyList<ClipMetadata> GetClipsByTag(string tag) => [];
}
