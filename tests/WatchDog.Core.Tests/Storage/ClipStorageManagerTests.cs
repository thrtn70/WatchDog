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

    [Fact]
    public async Task IndexingHighlightEnrichesExistingEntryAndPreservesUserChanges()
    {
        var manager = CreateManager();
        var path = CreateTestClip("highlight.mp4");
        await manager.IndexClipAsync(path, "Game");
        manager.ToggleFavorite(path);
        manager.AddTags(path, ["keep"]);
        var sessionId = Guid.NewGuid();

        await manager.IndexClipAsync(path, "Game", Core.Highlights.HighlightType.Kill, sessionId, 3);
        await manager.IndexClipAsync(path, "Game");

        var clip = Assert.Single(CreateManager().GetAllClips());
        Assert.Equal(Core.Highlights.HighlightType.Kill, clip.HighlightType);
        Assert.Equal(sessionId, clip.SessionId);
        Assert.Equal(3, clip.MatchNumber);
        Assert.True(clip.IsFavorite);
        Assert.Contains("keep", clip.Tags);
    }

    [Fact]
    public async Task CompletedMediaAttribution_IsPersistedAndCannotBeReassigned()
    {
        var manager = CreateManager();
        var path = CreateTestClip("owned.mp4");
        var receipt = new CompletedMediaReceipt(
            path,
            "Game",
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            Guid.NewGuid(),
            7,
            2);

        await manager.IndexCompletedMediaAsync(receipt);
        await manager.IndexCompletedMediaAsync(receipt, Core.Highlights.HighlightType.Kill);

        var persisted = Assert.Single(CreateManager().GetAllClips());
        Assert.Equal(receipt.SessionId, persisted.SessionId);
        Assert.Equal(receipt.CaptureRequestId, persisted.CaptureRequestId);
        Assert.Equal(receipt.CaptureGeneration, persisted.CaptureGeneration);
        Assert.Equal(receipt.MatchNumber, persisted.MatchNumber);
        Assert.Equal(Core.Highlights.HighlightType.Kill, persisted.HighlightType);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.IndexCompletedMediaAsync(receipt with { CaptureRequestId = Guid.NewGuid() }));
    }

    [Fact]
    public async Task CorruptPrimaryIndexRecoversMetadataFromBackup()
    {
        var manager = CreateManager();
        var path = CreateTestClip("recover.mp4");
        await manager.IndexClipAsync(path, "Game");
        manager.ToggleFavorite(path);
        manager.AddTags(path, ["latest"]);
        File.WriteAllText(Path.Combine(_tempDir, "clips-index.json"), "not json");

        var recovered = Assert.Single(CreateManager().GetAllClips());
        Assert.True(recovered.IsFavorite);
        Assert.True(File.Exists(path));
        Assert.NotEmpty(Directory.GetFiles(_tempDir, "clips-index.json.corrupt-*"));
        Assert.Single(CreateManager().GetAllClips());
    }

    [Fact]
    public async Task CorruptBackupIsPreservedWithoutBlockingValidPrimary()
    {
        var manager = CreateManager();
        var path = CreateTestClip("valid-primary.mp4");
        await manager.IndexClipAsync(path, "Game");
        manager.ToggleFavorite(path);
        var backup = Path.Combine(_tempDir, "clips-index.json.bak");
        File.WriteAllText(backup, "corrupt backup bytes");

        var restarted = CreateManager();

        Assert.True(Assert.Single(restarted.GetAllClips()).IsFavorite);
        var evidence = Assert.Single(Directory.GetFiles(_tempDir, "clips-index.json.bak.corrupt-*"));
        Assert.Equal("corrupt backup bytes", File.ReadAllText(evidence));
    }

    [Fact]
    public async Task BothCorruptIndexesStartLibraryUnavailableAndPreserveOriginalBytes()
    {
        var manager = CreateManager();
        await manager.IndexClipAsync(CreateTestClip("both-corrupt.mp4"), "Game");
        manager.ToggleFavorite(manager.GetAllClips()[0].FilePath);
        var primary = Path.Combine(_tempDir, "clips-index.json");
        var backup = primary + ".bak";
        File.WriteAllText(primary, "corrupt primary bytes");
        File.WriteAllText(backup, "corrupt backup bytes");

        Assert.False(CreateManager().LibraryStatus.IsAvailable);

        Assert.Equal("corrupt primary bytes", File.ReadAllText(primary));
        Assert.Equal("corrupt backup bytes", File.ReadAllText(backup));
        Assert.NotEmpty(Directory.GetFiles(_tempDir, "clips-index.json.corrupt-*"));
        Assert.NotEmpty(Directory.GetFiles(_tempDir, "clips-index.json.bak.corrupt-*"));
    }

    [Fact]
    public async Task FailedPrimaryRepairKeepsCorruptPrimaryAndValidBackupForRetry()
    {
        var manager = CreateManager();
        await manager.IndexClipAsync(CreateTestClip("repair-retry.mp4"), "Game");
        manager.ToggleFavorite(manager.GetAllClips()[0].FilePath);
        var primary = Path.Combine(_tempDir, "clips-index.json");
        var backup = primary + ".bak";
        File.WriteAllText(primary, "corrupt primary bytes");
        var backupBytes = File.ReadAllBytes(backup);

        using (File.Open(primary, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var unavailable = CreateManager().LibraryStatus;
            Assert.False(unavailable.IsAvailable);
            Assert.False(unavailable.CanBuildNewIndex);
        }

        Assert.Equal("corrupt primary bytes", File.ReadAllText(primary));
        Assert.Equal(backupBytes, File.ReadAllBytes(backup));
        Assert.Single(CreateManager().GetAllClips());
    }

    [Fact]
    public void SemanticallyInvalidWholeIndexIsNotAcceptedAsAnEmptyLibrary()
    {
        var primary = Path.Combine(_tempDir, "clips-index.json");
        File.WriteAllText(primary, "[{\"filePath\":\"\",\"fileName\":\"bad.mp4\"}]");

        var manager = CreateManager();

        Assert.False(manager.LibraryStatus.IsAvailable);
        Assert.Empty(manager.GetAllClips());

        Assert.Equal("[{\"filePath\":\"\",\"fileName\":\"bad.mp4\"}]", File.ReadAllText(primary));
    }

    [Fact]
    public async Task ConcurrentIndexingPersistsAllEntriesAfterRestart()
    {
        var manager = CreateManager();
        var paths = Enumerable.Range(0, 30).Select(i => CreateTestClip($"parallel-{i}.mp4")).ToArray();
        await Task.WhenAll(paths.Select(path => Task.Run(() => manager.IndexClipAsync(path, "Game"))));
        Assert.Equal(paths.Length, CreateManager().GetAllClips().Count);
    }

    [Fact]
    public async Task ConcurrentEquivalentPathsProduceOnePersistedEntry()
    {
        var manager = CreateManager();
        var path = CreateTestClip("normalized.mp4");
        var equivalentPath = Path.Combine(_tempDir, ".", "nested", "..", "normalized.mp4");

        await Task.WhenAll(
            Task.Run(() => manager.IndexClipAsync(path, "Game")),
            Task.Run(() => manager.IndexClipAsync(equivalentPath, "Game")));

        var clip = Assert.Single(CreateManager().GetAllClips());
        Assert.Equal(Path.GetFullPath(path), clip.FilePath);
    }

    [Fact]
    public async Task FailedIndexWriteRollsBackAndRetryPersists()
    {
        var manager = CreateManager();
        var path = CreateTestClip("retry.mp4");
        var index = Path.Combine(_tempDir, "clips-index.json");
        Directory.CreateDirectory(index);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => manager.IndexClipAsync(path, "Game"));
        Assert.Empty(manager.GetAllClips());
        Assert.True(File.Exists(path));
        Directory.Delete(index);
        await manager.IndexClipAsync(path, "Game");
        Assert.Single(CreateManager().GetAllClips());
    }

    [Fact]
    public async Task FailedFavoriteAndDeletePreserveMetadataAndMedia()
    {
        var manager = CreateManager();
        var path = CreateTestClip("keep.mp4");
        await manager.IndexClipAsync(path, "Game");
        var index = Path.Combine(_tempDir, "clips-index.json");
        using (var held = File.Open(index, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<IOException>(() => manager.ToggleFavorite(path));
            Assert.False(Assert.Single(manager.GetAllClips()).IsFavorite);
            Assert.Throws<IOException>(() => manager.DeleteClip(path));
            Assert.Single(manager.GetAllClips());
            Assert.True(File.Exists(path));
        }
        manager.ToggleFavorite(path);
        Assert.True(Assert.Single(CreateManager().GetAllClips()).IsFavorite);
    }

    [Fact]
    public async Task NewDestinationDiscoversLegacyMetadataWithoutChangingLegacyFiles()
    {
        var oldManager = CreateManager();
        var path = CreateTestClip("old.mp4");
        await oldManager.IndexClipAsync(path, "Game");
        oldManager.ToggleFavorite(path);
        var oldIndex = File.ReadAllText(Path.Combine(_tempDir, "clips-index.json"));
        var newConfig = _config with { BasePath = Path.Combine(_tempDir, "new"), LegacyReadOnlyPaths = [_tempDir] };
        var manager = CreateManager(newConfig);
        Assert.True(Assert.Single(manager.GetAllClips()).IsFavorite);
        manager.AddTags(path, ["legacy"]);
        Assert.Equal(oldIndex, File.ReadAllText(Path.Combine(_tempDir, "clips-index.json")));
        Assert.Contains("legacy", Assert.Single(CreateManager(newConfig).GetAllClips()).Tags);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task CleanupDoesNotCountOrDeleteNestedLegacyArchive()
    {
        var legacyRoot = Path.Combine(_tempDir, "archive");
        Directory.CreateDirectory(legacyRoot);
        var archivedPath = Path.Combine(legacyRoot, "large.mp4");
        using (var file = File.Create(archivedPath)) file.SetLength(2L * 1024 * 1024 * 1024);
        File.SetCreationTimeUtc(archivedPath, DateTime.UtcNow.AddDays(-10));
        var legacyManager = CreateManager(_config with { BasePath = legacyRoot });
        await legacyManager.IndexClipAsync(archivedPath, "Game");
        var activePath = CreateTestClip("active.mp4");
        File.SetCreationTimeUtc(activePath, DateTime.UtcNow.AddDays(-2));
        var manager = CreateManager(_config with { LegacyReadOnlyPaths = [legacyRoot] });
        await manager.IndexClipAsync(activePath, "Game");
        await manager.RunCleanupAsync();
        Assert.Equal(2, manager.GetAllClips().Count);
        Assert.True(File.Exists(activePath));
        Assert.True(File.Exists(archivedPath));
        Assert.Throws<InvalidOperationException>(() => manager.DeleteClip(archivedPath));
    }

    [Fact]
    public async Task TemporarilyUnavailableMediaKeepsMetadataAcrossUnrelatedWrites()
    {
        var manager = CreateManager();
        var path = CreateTestClip("unavailable.mp4");
        await manager.IndexClipAsync(path, "Game");
        manager.ToggleFavorite(path);
        File.Move(path, path + ".offline");
        var restarted = CreateManager();
        await restarted.IndexClipAsync(CreateTestClip("new.mp4"), "Game");
        await restarted.RunCleanupAsync();
        File.Move(path + ".offline", path);
        Assert.True(CreateManager().GetAllClips().Single(c => c.FilePath == path).IsFavorite);
    }

    [Fact]
    public async Task CleanupResultReportsOnlyCommittedReclamation()
    {
        var config = _config with { AutoDeleteDays = 0, MaxStorageBytesOverride = 0 };
        var path = CreateTestClip("reclaimed.mp4");
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        var manager = CreateManager(config);
        await manager.IndexClipAsync(path, "Game");

        var result = await ((IStorageMaintenance)manager).RunCleanupAsync();

        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1024, result.DeletedBytes);
        Assert.Empty(result.Failures);
        Assert.Equal(0, result.RemainingIndexedBytes);
        Assert.True(result.QuotaMet);
        Assert.False(File.Exists(path));
        Assert.Empty(manager.GetAllClips());
    }

    [Fact]
    public async Task CleanupDeletionFailurePreservesMetadataAndReportsQuotaFailure()
    {
        var config = _config with { AutoDeleteDays = 0, MaxStorageBytesOverride = 0 };
        var path = CreateTestClip("locked.mp4");
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        var manager = CreateManager(config);
        await manager.IndexClipAsync(path, "Game");

        StorageCleanupResult result;
        using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None))
            result = await ((IStorageMaintenance)manager).RunCleanupAsync();

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(0, result.DeletedBytes);
        Assert.Single(result.Failures);
        Assert.Equal(1024, result.RemainingIndexedBytes);
        Assert.False(result.QuotaMet);
        Assert.True(File.Exists(path));
        Assert.Single(manager.GetAllClips());
    }

    [Fact]
    public async Task CleanupHonorsCanonicalProtectionLease()
    {
        var config = _config with { AutoDeleteDays = 0, MaxStorageBytesOverride = 0 };
        var path = CreateTestClip("protected.mp4");
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        var registry = new ProtectedMediaRegistry();
        var manager = CreateManager(config, registry);
        await manager.IndexClipAsync(path, "Game");
        var equivalentPath = Path.Combine(_tempDir, ".", "protected.mp4");

        using (registry.Protect(equivalentPath))
        {
            var result = await ((IStorageMaintenance)manager).RunCleanupAsync();
            Assert.Equal(0, result.DeletedCount);
            Assert.Equal(1024, result.RemainingIndexedBytes);
            Assert.False(result.QuotaMet);
        }

        var afterRelease = await ((IStorageMaintenance)manager).RunCleanupAsync();
        Assert.Equal(1, afterRelease.DeletedCount);
        Assert.True(afterRelease.QuotaMet);
    }

    [Fact]
    public async Task ExplicitDeleteRejectsProtectedClipWithoutChangingMediaOrMetadata()
    {
        var path = CreateTestClip("protected-delete.mp4");
        var registry = new ProtectedMediaRegistry();
        var manager = CreateManager(protectedMedia: registry);
        await manager.IndexClipAsync(path, "Game");

        using (registry.Protect(path))
        {
            Assert.Throws<InvalidOperationException>(() => manager.DeleteClip(path));
            Assert.True(File.Exists(path));
            Assert.Single(manager.GetAllClips());
        }
    }

    [Fact]
    public async Task CleanupCommitFailureStopsCascadeAndRestartReconcilesDeletedIntent()
    {
        var config = _config with { AutoDeleteDays = 0, MaxStorageBytesOverride = 0 };
        var first = CreateTestClip("first-old.mp4");
        var second = CreateTestClip("second-old.mp4");
        File.SetCreationTimeUtc(first, DateTime.UtcNow.AddDays(-3));
        File.SetCreationTimeUtc(second, DateTime.UtcNow.AddDays(-2));
        var manager = CreateManager(config);
        await manager.IndexClipAsync(first, "Game");
        await manager.IndexClipAsync(second, "Game");
        var index = Path.Combine(_tempDir, "clips-index.json");

        StorageCleanupResult result;
        StorageCleanupResult repeated;
        using (File.Open(index, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await ((IStorageMaintenance)manager).RunCleanupAsync();
            var filesAfterFirstFailure = new[] { first, second }.Count(File.Exists);
            repeated = await ((IStorageMaintenance)manager).RunCleanupAsync();
            Assert.Throws<IOException>(() => manager.DeleteClip(
                new[] { first, second }.Single(File.Exists)));
            Assert.Equal(filesAfterFirstFailure, new[] { first, second }.Count(File.Exists));
        }

        Assert.Single(result.Failures);
        Assert.True(result.Failures[0].AbortCleanup);
        Assert.Single(repeated.Failures);
        Assert.True(repeated.Failures[0].AbortCleanup);
        Assert.Equal(1, new[] { first, second }.Count(File.Exists));
        Assert.Equal(2, manager.GetAllClips().Count);
        Assert.True(File.Exists(Path.Combine(_tempDir, "storage-deletion-pending.json")));

        var recovered = CreateManager(config);
        Assert.Single(recovered.GetAllClips());
        Assert.Single(new[] { first, second }, File.Exists);
        Assert.False(File.Exists(Path.Combine(_tempDir, "storage-deletion-pending.json")));
    }

    [Fact]
    public async Task SessionOwnedBytesContributeToQuotaWithoutBecomingDeletionCandidates()
    {
        var config = _config with { AutoDeleteDays = 0, MaxStorageBytesOverride = 1500 };
        var clip = CreateTestClip("quota-clip.mp4");
        File.SetCreationTimeUtc(clip, DateTime.UtcNow.AddDays(-2));
        var manager = CreateManager(config);
        await manager.IndexClipAsync(clip, "Game");

        var result = await ((IStorageMaintenance)manager).RunCleanupAsync(1000);

        Assert.True(result.QuotaMet);
        Assert.Equal(1000, result.ProtectedExternalBytes);
        Assert.Equal(1000, result.TotalTrackedBytes);
        Assert.False(File.Exists(clip));
    }

    [Fact]
    public async Task SessionAndClipCatalogOverlapCountsThePhysicalFileOnlyOnce()
    {
        var config = _config with { AutoDeleteDays = 0, MaxStorageBytesOverride = 1500 };
        var clip = CreateTestClip("overlapping-session-clip.mp4");
        var manager = CreateManager(config);
        await manager.IndexClipAsync(clip, "Game");
        IReadOnlyDictionary<string, long> sessionFiles = new Dictionary<string, long>(
            StringComparer.OrdinalIgnoreCase)
        {
            [clip] = 1024,
        };

        var result = await ((IStorageMaintenance)manager).RunCleanupAsync(sessionFiles);

        Assert.True(result.QuotaMet);
        Assert.Equal(0, result.ProtectedExternalBytes);
        Assert.Equal(1024, result.TotalTrackedBytes);
        Assert.True(File.Exists(clip));
        Assert.Single(manager.GetAllClips());
    }

    [Fact]
    public async Task CleanupDoesNotDeleteRecentMediaToMeetQuota()
    {
        var config = _config with { AutoDeleteDays = 0, MaxStorageBytesOverride = 0 };
        var path = CreateTestClip("recent.mp4");
        var manager = CreateManager(config);
        await manager.IndexClipAsync(path, "Game");

        var result = await ((IStorageMaintenance)manager).RunCleanupAsync();

        Assert.Equal(0, result.DeletedCount);
        Assert.Equal(1024, result.RemainingIndexedBytes);
        Assert.False(result.QuotaMet);
        Assert.True(File.Exists(path));
        Assert.Single(manager.GetAllClips());
    }

    [Fact]
    public async Task CleanupHonorsCancellationWithoutDeletingMedia()
    {
        var config = _config with { AutoDeleteDays = 0, MaxStorageBytesOverride = 0 };
        var path = CreateTestClip("cancel-cleanup.mp4");
        File.SetCreationTimeUtc(path, DateTime.UtcNow.AddDays(-2));
        var manager = CreateManager(config);
        await manager.IndexClipAsync(path, "Game");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ((IStorageMaintenance)manager).RunCleanupAsync(cts.Token));

        Assert.True(File.Exists(path));
        Assert.Single(manager.GetAllClips());
    }

    private ClipStorageManager CreateManager(
        StorageConfig? config = null,
        IProtectedMediaRegistry? protectedMedia = null)
    {
        var mockEditor = new StubClipEditor();
        return new ClipStorageManager(
            config ?? _config,
            mockEditor,
            NullLogger<ClipStorageManager>.Instance,
            protectedMedia ?? new ProtectedMediaRegistry());
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
