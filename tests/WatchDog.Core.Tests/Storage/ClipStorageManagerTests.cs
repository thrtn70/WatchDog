using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Storage;

namespace WatchDog.Core.Tests.Storage;

public sealed class ClipStorageManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly StorageConfig _config;

    public ClipStorageManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WatchDog_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _config = new StorageConfig { BasePath = _tempDir, MaxStorageGb = 1, AutoDeleteDays = 7 };
    }

    [Fact]
    public void GetAllClips_ReturnsEmpty_WhenNoClipsIndexed()
    {
        var manager = CreateManager();
        Assert.Empty(manager.GetAllClips());
    }

    [Fact]
    public async Task IndexClipAsync_AddsClipToIndex()
    {
        var clipPath = CreateTestClip("test.mp4");
        var manager = CreateManager();

        var metadata = await manager.IndexClipAsync(clipPath, "Test Game");

        Assert.Equal("test.mp4", metadata.FileName);
        Assert.Equal("Test Game", metadata.GameName);
        Assert.Single(manager.GetAllClips());
    }

    [Fact]
    public async Task GetClipsByGame_FiltersCorrectly()
    {
        var manager = CreateManager();
        await manager.IndexClipAsync(CreateTestClip("clip1.mp4"), "Valorant");
        await manager.IndexClipAsync(CreateTestClip("clip2.mp4"), "CS2");
        await manager.IndexClipAsync(CreateTestClip("clip3.mp4"), "Valorant");

        var valorantClips = manager.GetClipsByGame("Valorant");
        var cs2Clips = manager.GetClipsByGame("CS2");

        Assert.Equal(2, valorantClips.Count);
        Assert.Single(cs2Clips);
    }

    [Fact]
    public async Task DeleteClip_RemovesFromIndexAndDisk()
    {
        var clipPath = CreateTestClip("delete_me.mp4");
        var manager = CreateManager();
        await manager.IndexClipAsync(clipPath, "Test");

        Assert.Single(manager.GetAllClips());
        Assert.True(File.Exists(clipPath));

        manager.DeleteClip(clipPath);

        Assert.Empty(manager.GetAllClips());
        Assert.False(File.Exists(clipPath));
    }

    [Fact]
    public async Task ToggleFavorite_FlipsFlag()
    {
        var clipPath = CreateTestClip("fav.mp4");
        var manager = CreateManager();
        await manager.IndexClipAsync(clipPath, "Test");

        Assert.False(manager.GetAllClips()[0].IsFavorite);

        manager.ToggleFavorite(clipPath);
        Assert.True(manager.GetAllClips()[0].IsFavorite);

        manager.ToggleFavorite(clipPath);
        Assert.False(manager.GetAllClips()[0].IsFavorite);
    }

    [Fact]
    public async Task RunCleanupAsync_DeletesOldClips()
    {
        var config = _config with { AutoDeleteDays = 0 }; // Delete everything older than 0 days
        var manager = CreateManager(config);

        // Create a clip and backdate it
        var clipPath = CreateTestClip("old.mp4");
        File.SetCreationTimeUtc(clipPath, DateTime.UtcNow.AddDays(-2));
        await manager.IndexClipAsync(clipPath, "Test");

        await manager.RunCleanupAsync();

        // Clip should be deleted (older than 0 days and older than 24 hours)
        Assert.Empty(manager.GetAllClips());
    }

    [Fact]
    public async Task RunCleanupAsync_PreservesFavorites()
    {
        var config = _config with { AutoDeleteDays = 0 };
        var manager = CreateManager(config);

        var clipPath = CreateTestClip("fav_old.mp4");
        File.SetCreationTimeUtc(clipPath, DateTime.UtcNow.AddDays(-2));
        await manager.IndexClipAsync(clipPath, "Test");
        manager.ToggleFavorite(clipPath);

        await manager.RunCleanupAsync();

        Assert.Single(manager.GetAllClips());
    }

    private ClipStorageManager CreateManager(StorageConfig? config = null)
    {
        var mockEditor = new StubClipEditor();
        return new ClipStorageManager(
            config ?? _config,
            mockEditor,
            NullLogger<ClipStorageManager>.Instance);
    }

    private string CreateTestClip(string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, new byte[1024]); // 1KB dummy file
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// Stub editor that returns zero duration and skips thumbnail generation.
    /// </summary>
    private sealed class StubClipEditor : IClipEditor
    {
        public Task<string> TrimAsync(string inputPath, TimeSpan start, TimeSpan end, CancellationToken ct)
            => Task.FromResult(inputPath);

        public Task<string> GenerateThumbnailAsync(string inputPath, TimeSpan timestamp, CancellationToken ct)
            => Task.FromException<string>(new NotSupportedException("No ffmpeg in tests"));

        public Task<TimeSpan> GetDurationAsync(string inputPath, CancellationToken ct)
            => Task.FromResult(TimeSpan.FromSeconds(30));

        public Task<IReadOnlyList<string>> GenerateThumbnailStripAsync(
            string inputPath, int frameCount, int thumbnailWidth, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
