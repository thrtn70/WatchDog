using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
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
    public async Task BothCorruptIndexesFailVisiblyAndPreserveOriginalBytes()
    {
        var manager = CreateManager();
        await manager.IndexClipAsync(CreateTestClip("both-corrupt.mp4"), "Game");
        manager.ToggleFavorite(manager.GetAllClips()[0].FilePath);
        var primary = Path.Combine(_tempDir, "clips-index.json");
        var backup = primary + ".bak";
        File.WriteAllText(primary, "corrupt primary bytes");
        File.WriteAllText(backup, "corrupt backup bytes");

        Assert.Throws<InvalidDataException>(() => CreateManager());

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
            Assert.Throws<InvalidDataException>(() => CreateManager());

        Assert.Equal("corrupt primary bytes", File.ReadAllText(primary));
        Assert.Equal(backupBytes, File.ReadAllBytes(backup));
        Assert.Single(CreateManager().GetAllClips());
    }

    [Fact]
    public void SemanticallyInvalidWholeIndexIsNotAcceptedAsAnEmptyLibrary()
    {
        var primary = Path.Combine(_tempDir, "clips-index.json");
        File.WriteAllText(primary, "[{\"filePath\":\"\",\"fileName\":\"bad.mp4\"}]");

        Assert.Throws<InvalidDataException>(() => CreateManager());

        Assert.Equal("[{\"filePath\":\"\",\"fileName\":\"bad.mp4\"}]", File.ReadAllText(primary));
    }

    [Fact]
    public void LegacySeparatorDuplicatesLoadAsOneClipPerMediaFile()
    {
        WriteIndex(ActiveIndexPath, CreateLegacyDuplicateIndex(_tempDir));

        var clips = CreateManager().GetAllClips();

        Assert.Equal(
            LegacyFileNames.Select(name => Path.GetFullPath(Path.Combine(_tempDir, "Desktop", name))).Order(),
            clips.Select(clip => clip.FilePath).Order());
        Assert.All(clips, clip => Assert.Equal("Desktop", clip.GameName));
        Assert.All(clips, clip => Assert.False(clip.IsFavorite));
        Assert.All(clips, clip => Assert.Empty(clip.Tags));
    }

    [Fact]
    public void LegacyDuplicateMergeKeepsUserEditsAndRicherAttribution()
    {
        var sessionId = Guid.NewGuid();
        var name = LegacyFileNames[0];
        var obsPath = $"{_tempDir.Replace('\\', '/')}/Desktop/{name}";
        var scanPath = Path.Combine(_tempDir, "Desktop", name);
        var saveEvent = LegacyEntry(obsPath, name, LegacyCreatedAt.AddSeconds(5));
        saveEvent["gameName"] = null;
        saveEvent["tags"] = new JsonArray("clutch");
        var folderScan = LegacyEntry(scanPath, name, LegacyCreatedAt);
        folderScan["isFavorite"] = true;
        folderScan["tags"] = new JsonArray("Clutch", "funny");
        folderScan["highlightType"] = "Kill";
        var secondSaveEvent = LegacyEntry(obsPath, name, LegacyCreatedAt.AddSeconds(10));
        secondSaveEvent["sessionId"] = sessionId.ToString();
        secondSaveEvent["matchNumber"] = 3;
        WriteIndex(ActiveIndexPath, new JsonArray(saveEvent, folderScan, secondSaveEvent));

        var clip = Assert.Single(CreateManager().GetAllClips());

        Assert.Equal(Path.GetFullPath(scanPath), clip.FilePath);
        Assert.True(clip.IsFavorite);
        Assert.Equal(new[] { "clutch", "funny" }, clip.Tags);
        Assert.Equal(LegacyCreatedAt, clip.CreatedAt);
        Assert.Equal("Desktop", clip.GameName);
        Assert.Equal(Core.Highlights.HighlightType.Kill, clip.HighlightType);
        Assert.Equal(sessionId, clip.SessionId);
        Assert.Equal(3, clip.MatchNumber);
        var restarted = Assert.Single(CreateManager().GetAllClips());
        Assert.True(restarted.IsFavorite);
        Assert.Equal(new[] { "clutch", "funny" }, restarted.Tags);
    }

    [Fact]
    public void LegacyDuplicateMergePreservesOriginalBytesBeforeRewritingIndex()
    {
        WriteIndex(ActiveIndexPath, CreateLegacyDuplicateIndex(_tempDir));
        var original = File.ReadAllBytes(ActiveIndexPath);

        Assert.Equal(5, CreateManager().GetAllClips().Count);

        var evidence = Assert.Single(Directory.GetFiles(_tempDir, "clips-index.json.duplicates-*"));
        Assert.Equal(original, File.ReadAllBytes(evidence));
        Assert.Empty(Directory.GetFiles(_tempDir, "clips-index.json.corrupt-*"));
        var rewritten = JsonNode.Parse(File.ReadAllText(ActiveIndexPath))!.AsArray();
        Assert.Equal(5, rewritten.Count);
        Assert.Equal(5, rewritten
            .Select(entry => Path.GetFullPath((string)entry!["filePath"]!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count());

        Assert.Equal(5, CreateManager().GetAllClips().Count);
        Assert.Single(Directory.GetFiles(_tempDir, "clips-index.json.duplicates-*"));
    }

    [Fact]
    public void LegacyDuplicateRewriteFailureStillLoadsAndRetainsOriginalIndex()
    {
        WriteIndex(ActiveIndexPath, CreateLegacyDuplicateIndex(_tempDir));
        var original = File.ReadAllBytes(ActiveIndexPath);

        using (File.Open(ActiveIndexPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(5, CreateManager().GetAllClips().Count);

        Assert.Equal(original, File.ReadAllBytes(ActiveIndexPath));
        Assert.Equal(5, CreateManager().GetAllClips().Count);
        Assert.NotEqual(original, File.ReadAllBytes(ActiveIndexPath));
    }

    [Fact]
    public void LegacyDuplicateMergeLogsHowManyEntriesWereMerged()
    {
        WriteIndex(ActiveIndexPath, CreateLegacyDuplicateIndex(_tempDir));
        var logger = new RecordingLogger();

        _ = CreateManager(logger: logger);

        Assert.Contains(logger.Entries, entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("4 duplicate"));
    }

    [Theory]
    [InlineData("relative-path", "invalid media path at position 3")]
    [InlineData("blank-path", "invalid media path at position 3")]
    [InlineData("blank-file-name", "invalid file name at position 3")]
    [InlineData("default-created-at", "invalid metadata at position 3")]
    [InlineData("negative-duration", "invalid metadata at position 3")]
    [InlineData("negative-size", "invalid metadata at position 3")]
    [InlineData("null-tags", "invalid tags at position 3")]
    [InlineData("null-tag", "invalid tags at position 3")]
    [InlineData("negative-capture-generation", "invalid capture generation at position 3")]
    [InlineData("null-entry", "null entry at position 3")]
    public void InvalidEntryAmongLegacyDuplicatesStillRefusesToLoad(string defect, string expectedReason)
    {
        // Position 3 duplicates position 2, so for every defect that keeps the path,
        // merging before validating would hide it.
        var index = CreateLegacyDuplicateIndex(_tempDir);
        ApplyDefect(index, 3, defect);
        WriteIndex(ActiveIndexPath, index);
        var original = File.ReadAllBytes(ActiveIndexPath);

        var error = Assert.Throws<InvalidDataException>(() => CreateManager());

        Assert.Contains(expectedReason, error.InnerException?.Message);
        Assert.Equal(original, File.ReadAllBytes(ActiveIndexPath));
        Assert.NotEmpty(Directory.GetFiles(_tempDir, "clips-index.json.corrupt-*"));
        Assert.Empty(Directory.GetFiles(_tempDir, "clips-index.json.duplicates-*"));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("truncated")]
    public void CorruptIndexStillRefusesToLoad(string content)
    {
        if (content == "truncated")
        {
            var whole = CreateLegacyDuplicateIndex(_tempDir).ToJsonString(IndentedJson);
            content = whole[..(whole.Length / 2)];
        }
        File.WriteAllText(ActiveIndexPath, content);

        Assert.Throws<InvalidDataException>(() => CreateManager());

        Assert.Equal(content, File.ReadAllText(ActiveIndexPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "clips-index.json.duplicates-*"));
    }

    [Fact]
    public void LegacyDuplicatesInBackupRecoverWhenPrimaryIsCorrupt()
    {
        File.WriteAllText(ActiveIndexPath, "corrupt primary bytes");
        WriteIndex(ActiveIndexPath + ".bak", CreateLegacyDuplicateIndex(_tempDir));
        var backup = File.ReadAllBytes(ActiveIndexPath + ".bak");

        Assert.Equal(5, CreateManager().GetAllClips().Count);

        Assert.Equal(backup, File.ReadAllBytes(ActiveIndexPath + ".bak"));
        Assert.Equal(5, JsonNode.Parse(File.ReadAllText(ActiveIndexPath))!.AsArray().Count);
        var evidence = Assert.Single(Directory.GetFiles(_tempDir, "clips-index.json.corrupt-*"));
        Assert.Equal("corrupt primary bytes", File.ReadAllText(evidence));
        Assert.Equal(5, CreateManager().GetAllClips().Count);
    }

    [Fact]
    public void LegacyDuplicatePrimaryAlsoRepairsCorruptBackup()
    {
        WriteIndex(ActiveIndexPath, CreateLegacyDuplicateIndex(_tempDir));
        var original = File.ReadAllBytes(ActiveIndexPath);
        File.WriteAllText(ActiveIndexPath + ".bak", "corrupt backup bytes");

        Assert.Equal(5, CreateManager().GetAllClips().Count);

        var corruptEvidence = Assert.Single(Directory.GetFiles(_tempDir, "clips-index.json.bak.corrupt-*"));
        Assert.Equal("corrupt backup bytes", File.ReadAllText(corruptEvidence));
        Assert.Equal(original, File.ReadAllBytes(ActiveIndexPath + ".bak"));
        var duplicateEvidence = Assert.Single(Directory.GetFiles(_tempDir, "clips-index.json.duplicates-*"));
        Assert.Equal(original, File.ReadAllBytes(duplicateEvidence));
        Assert.Equal(5, JsonNode.Parse(File.ReadAllText(ActiveIndexPath))!.AsArray().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LegacyDuplicatesInReadOnlyLegacyIndexLoadWithoutChangingIt(bool duplicatesInBackup)
    {
        var legacyRoot = Path.Combine(_tempDir, "legacy");
        var legacyIndex = Path.Combine(legacyRoot, "clips-index.json");
        WriteIndex(duplicatesInBackup ? legacyIndex + ".bak" : legacyIndex, CreateLegacyDuplicateIndex(legacyRoot));
        if (duplicatesInBackup)
            File.WriteAllText(legacyIndex, "corrupt legacy bytes");
        var legacyBytes = Directory.GetFiles(legacyRoot).ToDictionary(path => path, File.ReadAllBytes);
        var config = _config with
        {
            BasePath = Path.Combine(_tempDir, "new"),
            LegacyReadOnlyPaths = [legacyRoot],
        };

        Assert.Equal(5, CreateManager(config).GetAllClips().Count);

        Assert.Equal(legacyBytes.Keys.Order(), Directory.GetFiles(legacyRoot).Order());
        Assert.All(legacyBytes, file => Assert.Equal(file.Value, File.ReadAllBytes(file.Key)));
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
        IProtectedMediaRegistry? protectedMedia = null,
        ILogger<ClipStorageManager>? logger = null)
    {
        var mockEditor = new StubClipEditor();
        return new ClipStorageManager(
            config ?? _config,
            mockEditor,
            logger ?? NullLogger<ClipStorageManager>.Instance,
            protectedMedia ?? new ProtectedMediaRegistry());
    }

    private string ActiveIndexPath => Path.Combine(_tempDir, "clips-index.json");

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private static readonly DateTimeOffset LegacyCreatedAt = new(2026, 4, 9, 19, 18, 30, TimeSpan.FromHours(-7));

    private static readonly string[] LegacyFileNames =
    [
        "2026-04-09 19-18-30.mp4",
        "2026-04-09 19-42-11.mp4",
        "2026-04-10 21-03-05.mp4",
        "2026-04-10 21-27-48.mp4",
        "2026-04-11 00-55-19.mp4",
    ];

    // v1.2.x and v1.3.0 indexed a replay once from OBS's save event (forward slashes,
    // no game for a Desktop capture) and again from the folder scan (backslashes, game
    // taken from the folder name): 9 entries for 5 files, in the order a real index had.
    private static JsonArray CreateLegacyDuplicateIndex(string root)
    {
        JsonObject SaveEvent(int file)
        {
            var name = LegacyFileNames[file];
            var entry = LegacyEntry(
                $"{root.Replace('\\', '/')}/Desktop/{name}",
                name,
                LegacyCreatedAt.AddMinutes(file * 20));
            entry["gameName"] = null;
            return entry;
        }

        JsonObject FolderScan(int file)
        {
            var name = LegacyFileNames[file];
            return LegacyEntry(Path.Combine(root, "Desktop", name), name, LegacyCreatedAt.AddMinutes(file * 20));
        }

        return
        [
            FolderScan(0), FolderScan(1),
            SaveEvent(2), SaveEvent(2), SaveEvent(3),
            FolderScan(2), FolderScan(3),
            SaveEvent(4), FolderScan(4),
        ];
    }

    private static JsonObject LegacyEntry(string filePath, string fileName, DateTimeOffset createdAt) => new()
    {
        ["filePath"] = filePath,
        ["fileName"] = fileName,
        ["gameName"] = "Desktop",
        ["createdAt"] = createdAt,
        ["duration"] = "00:02:00",
        ["fileSizeBytes"] = 52_428_800,
        ["thumbnailPath"] = null,
        ["isFavorite"] = false,
        ["highlightType"] = null,
        ["sessionId"] = null,
        ["matchNumber"] = null,
        ["tags"] = new JsonArray(),
        ["highlightSource"] = null,
    };

    private static void ApplyDefect(JsonArray index, int position, string defect)
    {
        var entry = index[position]!.AsObject();
        switch (defect)
        {
            case "relative-path": entry["filePath"] = "Desktop/clip.mp4"; break;
            case "blank-path": entry["filePath"] = " "; break;
            case "blank-file-name": entry["fileName"] = " "; break;
            case "default-created-at": entry["createdAt"] = "0001-01-01T00:00:00+00:00"; break;
            case "negative-duration": entry["duration"] = "-00:00:01"; break;
            case "negative-size": entry["fileSizeBytes"] = -1; break;
            case "null-tags": entry["tags"] = null; break;
            case "null-tag": entry["tags"] = new JsonArray("ok", null); break;
            case "negative-capture-generation": entry["captureGeneration"] = -1; break;
            case "null-entry": index[position] = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(defect), defect, null);
        }
    }

    private static void WriteIndex(string path, JsonArray index)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, index.ToJsonString(IndentedJson));
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

    private sealed class RecordingLogger : ILogger<ClipStorageManager>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_entries)
                    return [.. _entries];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
