using TikrClipr.Core.ClipEditor;
using TikrClipr.Core.Storage;

namespace TikrClipr.Core.Tests.Storage;

public sealed class StorageAnalyticsTests
{
    [Fact]
    public void Analyze_EmptyStorage_ReturnsZeros()
    {
        var storage = new FakeClipStorage([]);
        var config = new StorageConfig { MaxStorageGb = 50 };

        var report = StorageAnalytics.Analyze(storage, config);

        Assert.Equal(0, report.TotalClips);
        Assert.Equal(0, report.TotalBytes);
        Assert.Empty(report.ByGame);
    }

    [Fact]
    public void Analyze_GroupsByGame()
    {
        var clips = new[]
        {
            MakeClip("CS2", 100_000_000),
            MakeClip("CS2", 200_000_000),
            MakeClip("Valorant", 50_000_000),
        };
        var storage = new FakeClipStorage(clips);
        var config = new StorageConfig { MaxStorageGb = 50 };

        var report = StorageAnalytics.Analyze(storage, config);

        Assert.Equal(3, report.TotalClips);
        Assert.Equal(2, report.ByGame.Count);
        Assert.Equal("CS2", report.ByGame[0].GameName); // Largest first
        Assert.Equal(2, report.ByGame[0].ClipCount);
        Assert.Equal(300_000_000, report.ByGame[0].TotalBytes);
    }

    [Fact]
    public void Analyze_CalculatesUsagePercent()
    {
        var clips = new[] { MakeClip("Game", 1_073_741_824) }; // 1 GB
        var storage = new FakeClipStorage(clips);
        var config = new StorageConfig { MaxStorageGb = 10 };

        var report = StorageAnalytics.Analyze(storage, config);

        Assert.Equal(10.0, report.UsagePercent, 1);
        Assert.Equal(9.0, report.RemainingGb, 1);
    }

    private static ClipMetadata MakeClip(string game, long bytes) => new()
    {
        FilePath = $"C:\\clips\\{game}\\{Guid.NewGuid()}.mp4",
        FileName = "clip.mp4",
        GameName = game,
        Duration = TimeSpan.FromSeconds(30),
        FileSizeBytes = bytes,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class FakeClipStorage(IReadOnlyList<ClipMetadata> clips) : IClipStorage
    {
        public IReadOnlyList<ClipMetadata> GetAllClips() => clips;
        public IReadOnlyList<ClipMetadata> GetClipsByGame(string gameName) =>
            clips.Where(c => c.GameName == gameName).ToList();
        public Task<ClipMetadata> IndexClipAsync(string fp, string? gn, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<int> ScanAndIndexAsync(CancellationToken ct) => Task.FromResult(0);
        public void DeleteClip(string fp) { }
        public void ToggleFavorite(string fp) { }
        public Task RunCleanupAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
