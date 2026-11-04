using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Storage;

namespace WatchDog.Core.Tests.Storage;

/// <summary>
/// v1.2.3 and v1.3.0 never ran storage cleanup, so a library they wrote was never subject to
/// auto-delete or the quota. Only clips this version records may be deleted automatically;
/// everything an older version indexed is kept and does not count toward the quota.
/// </summary>
public sealed class ClipAutoDeleteEligibilityTests : IDisposable
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly string _root;
    private readonly StorageConfig _config;

    public ClipAutoDeleteEligibilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"WatchDog_Eligibility_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _config = new StorageConfig { BasePath = _root, AutoDeleteDays = 1, MaxStorageBytesOverride = 0 };
    }

    [Fact]
    public async Task OnlyClipsIndexedFromACaptureReceiptAreEligible()
    {
        var manager = CreateManager();
        var recorded = CreateClip("recorded.mp4", ageDays: 0);
        var scanned = CreateClip("scanned.mp4", ageDays: 0);

        await manager.IndexCompletedMediaAsync(Receipt(recorded, ageDays: 0));
        await manager.IndexClipAsync(scanned, "Game");

        var reloaded = CreateManager().GetAllClips();
        Assert.True(reloaded.Single(clip => clip.FilePath == recorded).AutoDeleteEligible);
        Assert.False(reloaded.Single(clip => clip.FilePath == scanned).AutoDeleteEligible);
    }

    [Fact]
    public async Task ClipsWithoutACaptureReceiptAreNeverDeletedAndDoNotCountTowardTheQuota()
    {
        var manager = CreateManager();
        var path = CreateClip("unknown-origin.mp4", ageDays: 200);
        await manager.IndexClipAsync(path, "Game");

        var result = await ((IStorageMaintenance)manager).RunCleanupAsync();

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.RemainingIndexedBytes);
        Assert.True(result.QuotaMet);
        Assert.True(File.Exists(path));
        Assert.Single(manager.GetAllClips());
    }

    [Fact]
    public async Task OlderVersionEntriesAreKeptWhileRecordedClipsFollowRetention()
    {
        var legacy = CreateClip("legacy.mp4", ageDays: 200);
        WriteIndex(new JsonArray(OlderVersionEntry(legacy, ageDays: 200)));
        var manager = CreateManager();
        var recorded = CreateClip("recorded-old.mp4", ageDays: 3);
        await manager.IndexCompletedMediaAsync(Receipt(recorded, ageDays: 3));

        var result = await ((IStorageMaintenance)manager).RunCleanupAsync();

        Assert.Equal(1, result.DeletedCount);
        Assert.True(result.QuotaMet);
        Assert.False(File.Exists(recorded));
        Assert.True(File.Exists(legacy));
        var kept = Assert.Single(CreateManager().GetAllClips());
        Assert.Equal(legacy, kept.FilePath);
        Assert.False(kept.AutoDeleteEligible);
    }

    [Fact]
    public async Task MergedDuplicatesStayProtectedUnlessEveryCopyWasRecorded()
    {
        var path = CreateClip("duplicate.mp4", ageDays: 200);
        var recordedCopy = OlderVersionEntry(path, ageDays: 200);
        recordedCopy["autoDeleteEligible"] = true;
        var olderCopy = OlderVersionEntry(path.Replace('\\', '/'), ageDays: 200);
        WriteIndex(new JsonArray(recordedCopy, olderCopy));
        var manager = CreateManager();

        var result = await ((IStorageMaintenance)manager).RunCleanupAsync();

        Assert.Equal(0, result.DeletedCount);
        Assert.True(File.Exists(path));
        Assert.False(Assert.Single(manager.GetAllClips()).AutoDeleteEligible);
    }

    [Fact]
    public async Task ProtectedClipThatIsAlsoASessionRecordingIsNotCountedEither()
    {
        // v1.3.0 indexed session recordings as clips, so they are also in session RecordingPaths.
        var manager = CreateManager();
        var path = CreateClip("Sessions-segment.mp4", ageDays: 200);
        await manager.IndexClipAsync(path, "Game");
        IReadOnlyDictionary<string, long> sessionFiles = new Dictionary<string, long>(
            StringComparer.OrdinalIgnoreCase)
        {
            [path] = 1024,
        };

        var result = await ((IStorageMaintenance)manager).RunCleanupAsync(sessionFiles);

        Assert.Equal(0, result.ProtectedExternalBytes);
        Assert.Equal(0, result.DeletedCount);
        Assert.True(result.QuotaMet);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task ALaterReceiptDoesNotMakeAProtectedClipDeletable()
    {
        var manager = CreateManager();
        var path = CreateClip("indexed-first.mp4", ageDays: 200);
        await manager.IndexClipAsync(path, "Game");

        await manager.IndexCompletedMediaAsync(Receipt(path, ageDays: 200));
        var result = await ((IStorageMaintenance)manager).RunCleanupAsync();

        Assert.False(Assert.Single(CreateManager().GetAllClips()).AutoDeleteEligible);
        Assert.Equal(0, result.DeletedCount);
        Assert.True(File.Exists(path));
    }

    private ClipStorageManager CreateManager() => new(
        _config,
        new StubClipEditor(),
        NullLogger<ClipStorageManager>.Instance,
        new ProtectedMediaRegistry());

    private string CreateClip(string name, int ageDays)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, new byte[1024]);
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-ageDays));
        return path;
    }

    private static CompletedMediaReceipt Receipt(string path, int ageDays) => new(
        path,
        "Game",
        DateTimeOffset.UtcNow.AddDays(-ageDays),
        SessionId: null,
        CaptureRequestId: Guid.NewGuid(),
        CaptureGeneration: 1,
        MatchNumber: null);

    // The shape v1.2.x, v1.3.0, v1.4.0 and v1.4.1 wrote: no autoDeleteEligible field.
    private static JsonObject OlderVersionEntry(string path, int ageDays) => new()
    {
        ["filePath"] = path,
        ["fileName"] = Path.GetFileName(path),
        ["gameName"] = "Desktop",
        ["createdAt"] = DateTimeOffset.UtcNow.AddDays(-ageDays),
        ["duration"] = "00:00:30",
        ["fileSizeBytes"] = 1024,
        ["thumbnailPath"] = null,
        ["isFavorite"] = false,
        ["highlightType"] = null,
        ["sessionId"] = null,
        ["matchNumber"] = null,
        ["tags"] = new JsonArray(),
        ["highlightSource"] = null,
    };

    private void WriteIndex(JsonArray index) =>
        File.WriteAllText(Path.Combine(_root, "clips-index.json"), index.ToJsonString(IndentedJson));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

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
