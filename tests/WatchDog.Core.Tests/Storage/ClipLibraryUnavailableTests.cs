using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Storage;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Storage;

public sealed class ClipLibraryUnavailableTests : IDisposable
{
    private const string CorruptPrimary = "corrupt primary bytes";
    private const string CorruptBackup = "corrupt backup bytes";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"WatchDog_LibraryUnavailable_{Guid.NewGuid():N}");
    private readonly StorageConfig _config;

    public ClipLibraryUnavailableTests()
    {
        Directory.CreateDirectory(_root);
        _config = new StorageConfig
        {
            BasePath = _root,
            AutoDeleteDays = 0,
            MaxStorageBytesOverride = 0,
        };
    }

    private string PrimaryPath => Path.Combine(_root, "clips-index.json");
    private string BackupPath => PrimaryPath + ".bak";

    [Fact]
    public void HealthyLibraryIsAvailable()
    {
        Assert.True(CreateManager().LibraryStatus.IsAvailable);
    }

    [Fact]
    public void CorruptIndexAndBackupStartTheLibraryUnavailableAndNameTheEvidence()
    {
        WriteCorruptIndexes();

        var manager = CreateManager();

        var status = manager.LibraryStatus;
        Assert.False(status.IsAvailable);
        Assert.True(status.CanBuildNewIndex);
        Assert.False(string.IsNullOrWhiteSpace(status.Problem));
        Assert.Equal(2, status.EvidencePaths.Count);
        Assert.Contains(status.EvidencePaths, path =>
            Path.GetFileName(path).StartsWith("clips-index.json.corrupt-", StringComparison.Ordinal) &&
            File.ReadAllText(path) == CorruptPrimary);
        Assert.Contains(status.EvidencePaths, path =>
            Path.GetFileName(path).StartsWith("clips-index.json.bak.corrupt-", StringComparison.Ordinal) &&
            File.ReadAllText(path) == CorruptBackup);
        Assert.Empty(manager.GetAllClips());
        AssertCorruptIndexesUntouched();
    }

    [Fact]
    public async Task UnavailableLibraryRefusesEveryMutationAndNeverWritesTheIndex()
    {
        WriteCorruptIndexes();
        var media = CreateMedia("Game", "kept.mp4");
        var manager = CreateManager();

        await Assert.ThrowsAsync<ClipLibraryUnavailableException>(
            () => manager.IndexClipAsync(media, "Game"));
        await Assert.ThrowsAsync<ClipLibraryUnavailableException>(
            () => manager.IndexCompletedMediaAsync(Receipt(media)));
        await Assert.ThrowsAsync<ClipLibraryUnavailableException>(
            () => manager.ScanAndIndexAsync());
        await Assert.ThrowsAsync<ClipLibraryUnavailableException>(
            () => manager.IndexRecoveredMediaAsync(new HashSet<string>()));
        Assert.Throws<ClipLibraryUnavailableException>(() => manager.ToggleFavorite(media));
        Assert.Throws<ClipLibraryUnavailableException>(() => manager.AddTags(media, ["tag"]));
        Assert.Throws<ClipLibraryUnavailableException>(() => manager.RemoveTags(media, ["tag"]));
        Assert.Throws<ClipLibraryUnavailableException>(() => manager.DeleteClip(media));

        Assert.True(File.Exists(media));
        Assert.Empty(manager.GetAllClips());
        Assert.Empty(Directory.GetFiles(_root, "clips-index.*.tmp"));
        AssertCorruptIndexesUntouched();
    }

    [Fact]
    public async Task UnavailableLibrarySuspendsCleanupWithoutTouchingMediaOrPendingDeletion()
    {
        WriteCorruptIndexes();
        var media = CreateMedia("Game", "old.mp4");
        File.SetCreationTimeUtc(media, DateTime.UtcNow.AddDays(-10));
        var intent = Path.Combine(_root, "storage-deletion-pending.json");
        var intentJson = JsonSerializer.Serialize(new
        {
            filePath = Path.Combine(_root, "Game", "already-gone.mp4"),
            thumbnailPath = (string?)null,
        });
        File.WriteAllText(intent, intentJson);
        var manager = CreateManager();
        var maintenance = (IStorageMaintenance)manager;

        var byBytes = await maintenance.RunCleanupAsync(1234, CancellationToken.None);
        var byFiles = await maintenance.RunCleanupAsync(
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { [media] = 1024 },
            CancellationToken.None);
        await manager.RunCleanupAsync();

        Assert.True(byBytes.IsSuspended);
        Assert.Equal(0, byBytes.DeletedCount);
        Assert.Empty(byBytes.Failures);
        Assert.False(byBytes.QuotaMet);
        Assert.Equal(1234, byBytes.ProtectedExternalBytes);
        Assert.True(byFiles.IsSuspended);
        Assert.Equal(0, byFiles.DeletedCount);
        Assert.True(File.Exists(media));
        Assert.Equal(intentJson, File.ReadAllText(intent));
        AssertCorruptIndexesUntouched();
    }

    [Fact]
    public void UnavailableLibraryDoesNotReportUnknownMediaAsUnindexed()
    {
        WriteCorruptIndexes();
        CreateMedia("Game", "unknown.mp4");

        Assert.Empty(CreateManager().FindUnindexedMediaFiles());
    }

    [Fact]
    public void ReloadWithoutPermissionStaysUnavailableAndReusesTheEvidence()
    {
        WriteCorruptIndexes();
        var manager = CreateManager();
        var evidence = manager.LibraryStatus.EvidencePaths.Order().ToArray();

        var status = manager.ReloadLibrary(allowNewIndex: false);

        Assert.False(status.IsAvailable);
        Assert.Equal(evidence, status.EvidencePaths.Order().ToArray());
        Assert.Equal(2, Directory.GetFiles(_root, "*.corrupt-*").Length);
        AssertCorruptIndexesUntouched();
    }

    [Fact]
    public void ReloadWithNewIndexReplacesPreservedCorruptIndexesAndKeepsTheEvidence()
    {
        WriteCorruptIndexes();
        var manager = CreateManager();
        var evidence = manager.LibraryStatus.EvidencePaths.ToArray();

        var status = manager.ReloadLibrary(allowNewIndex: true);

        Assert.True(status.IsAvailable);
        Assert.Empty(ReadIndex(PrimaryPath));
        Assert.Empty(ReadIndex(BackupPath));
        Assert.Equal(CorruptPrimary, File.ReadAllText(evidence.Single(path => path.Contains(".json.corrupt-"))));
        Assert.Equal(CorruptBackup, File.ReadAllText(evidence.Single(path => path.Contains(".bak.corrupt-"))));
        Assert.True(CreateManager().LibraryStatus.IsAvailable);
        Assert.Equal(2, Directory.GetFiles(_root, "*.corrupt-*").Length);
    }

    [Fact]
    public async Task RecoveryScanIndexesFolderMediaButSkipsExcludedSessionMedia()
    {
        WriteCorruptIndexes();
        var clip = CreateMedia("Valorant", "clip.mp4");
        var session = CreateMedia(Path.Combine("Valorant", "Sessions"), "session.mkv");
        var manager = CreateManager();
        Assert.True(manager.ReloadLibrary(allowNewIndex: true).IsAvailable);

        var scan = await manager.IndexRecoveredMediaAsync(
            new HashSet<string>([session], StringComparer.OrdinalIgnoreCase));

        Assert.Equal(1, scan.Indexed);
        Assert.Equal(0, scan.Failed);
        var restored = Assert.Single(CreateManager().GetAllClips());
        Assert.Equal(Path.GetFullPath(clip), restored.FilePath);
        Assert.Equal("Valorant", restored.GameName);
        Assert.NotNull(restored.RecoveredAt);
    }

    [Fact]
    public async Task RecoveredClipsLostTheirFavoritesSoCleanupNeverDeletesThem()
    {
        WriteCorruptIndexes();
        var recovered = CreateMedia("Game", "maybe-a-favorite.mp4");
        File.SetCreationTimeUtc(recovered, DateTime.UtcNow.AddDays(-10));
        var manager = CreateManager();
        Assert.True(manager.ReloadLibrary(allowNewIndex: true).IsAvailable);
        await manager.IndexRecoveredMediaAsync(new HashSet<string>());
        var captured = CreateMedia("Game", "new-capture.mp4");
        File.SetCreationTimeUtc(captured, DateTime.UtcNow.AddDays(-10));
        await manager.IndexClipAsync(captured, "Game");

        var result = await ((IStorageMaintenance)manager).RunCleanupAsync();

        Assert.False(result.IsSuspended);
        Assert.Equal(1, result.DeletedCount);
        Assert.Equal(1, result.RetainedRecoveredClips);
        Assert.True(File.Exists(recovered));
        Assert.False(File.Exists(captured));
        Assert.NotNull(Assert.Single(manager.GetAllClips()).RecoveredAt);
    }

    [Fact]
    public async Task ChoosingAFavoriteForARecoveredClipEndsItsCleanupExemption()
    {
        WriteCorruptIndexes();
        var recovered = CreateMedia("Game", "not-a-favorite.mp4");
        File.SetCreationTimeUtc(recovered, DateTime.UtcNow.AddDays(-10));
        var manager = CreateManager();
        manager.ReloadLibrary(allowNewIndex: true);
        await manager.IndexRecoveredMediaAsync(new HashSet<string>());

        manager.ToggleFavorite(recovered);
        manager.ToggleFavorite(recovered);
        var result = await ((IStorageMaintenance)manager).RunCleanupAsync();

        Assert.Equal(1, result.DeletedCount);
        Assert.False(File.Exists(recovered));
    }

    [Fact]
    public async Task CaptureReceiptReplacesTheGuessedAttributionOfARecoveredClip()
    {
        WriteCorruptIndexes();
        var media = CreateMedia("Halo Infinite", "capture.mp4");
        var manager = CreateManager();
        manager.ReloadLibrary(allowNewIndex: true);
        await manager.IndexRecoveredMediaAsync(new HashSet<string>());
        var receipt = Receipt(media) with { GameName = "Halo: Infinite" };

        var clip = await manager.IndexCompletedMediaAsync(receipt);

        Assert.Equal("Halo: Infinite", clip.GameName);
        Assert.Equal(receipt.SessionId, clip.SessionId);
        Assert.Equal(receipt.CaptureRequestId, clip.CaptureRequestId);
        Assert.Null(clip.RecoveredAt);
        Assert.Null(Assert.Single(CreateManager().GetAllClips()).RecoveredAt);
    }

    [Fact]
    public async Task RecoveryScanSkipsMediaThatIsStillProtected()
    {
        WriteCorruptIndexes();
        var pending = CreateMedia("Game", "pending-receipt.mp4");
        var registry = new ProtectedMediaRegistry();
        var manager = CreateManager(registry: registry);
        manager.ReloadLibrary(allowNewIndex: true);

        ClipRecoveryScanResult scan;
        using (registry.Protect(pending))
            scan = await manager.IndexRecoveredMediaAsync(new HashSet<string>());

        Assert.Equal(0, scan.Indexed);
        Assert.Empty(manager.GetAllClips());
    }

    [Fact]
    public async Task NewIndexStaysMarkedIncompleteUntilAFolderScanFinishesWithoutFailures()
    {
        WriteCorruptIndexes();
        CreateMedia("Game", "good.mp4");
        CreateMedia("Game", "unreadable.mp4");
        var editor = new FixedDurationClipEditor { FailsFor = path => path.EndsWith("unreadable.mp4") };
        var manager = CreateManager(editor);

        var reloaded = manager.ReloadLibrary(allowNewIndex: true);

        Assert.True(reloaded.IsAvailable);
        Assert.True(reloaded.IsRebuildIncomplete);
        Assert.True(CreateManager().LibraryStatus.IsRebuildIncomplete);

        var partial = await manager.IndexRecoveredMediaAsync(new HashSet<string>());

        Assert.Equal(1, partial.Indexed);
        Assert.Equal(1, partial.Failed);
        Assert.Contains("unreadable.mp4", partial.FirstFailure);
        Assert.True(manager.LibraryStatus.IsRebuildIncomplete);
        Assert.True(CreateManager().LibraryStatus.IsRebuildIncomplete);

        editor.FailsFor = _ => false;
        var finished = await manager.IndexRecoveredMediaAsync(new HashSet<string>());

        Assert.Equal(1, finished.Indexed);
        Assert.False(manager.LibraryStatus.IsRebuildIncomplete);
        Assert.False(CreateManager().LibraryStatus.IsRebuildIncomplete);
        Assert.Equal(2, CreateManager().GetAllClips().Count);
    }

    [Fact]
    public void RestartOnTheSameDamagedIndexReusesItsEvidence()
    {
        WriteCorruptIndexes();

        var first = CreateManager().LibraryStatus.EvidencePaths.Order().ToArray();
        var second = CreateManager().LibraryStatus.EvidencePaths.Order().ToArray();

        Assert.Equal(first, second);
        Assert.Equal(2, Directory.GetFiles(_root, "*.corrupt-*").Length);
    }

    [Fact]
    public async Task ValidIndexWithAnUnrepairableBackupStartsUnavailableWithoutReplacingIt()
    {
        var media = CreateMedia("Game", "kept.mp4");
        await CreateManager().IndexClipAsync(media, "Game");
        var primaryBytes = File.ReadAllBytes(PrimaryPath);
        File.WriteAllText(BackupPath, CorruptBackup);

        ClipStorageManager manager;
        using (File.Open(BackupPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            manager = CreateManager();
            Assert.False(manager.LibraryStatus.IsAvailable);
            Assert.False(manager.LibraryStatus.CanBuildNewIndex);
        }

        Assert.Equal(primaryBytes, File.ReadAllBytes(PrimaryPath));
        Assert.True(manager.ReloadLibrary(allowNewIndex: false).IsAvailable);
        Assert.Single(manager.GetAllClips());
    }

    [Fact]
    public async Task IndexThatCannotBeReadIsNeverReplacedByANewIndex()
    {
        var media = CreateMedia("Game", "favorite.mp4");
        var seed = CreateManager();
        await seed.IndexClipAsync(media, "Game");
        seed.ToggleFavorite(media);
        File.Delete(BackupPath);
        var original = File.ReadAllBytes(PrimaryPath);

        ClipStorageManager manager;
        using (File.Open(PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            manager = CreateManager();
            Assert.False(manager.LibraryStatus.IsAvailable);
            Assert.False(manager.LibraryStatus.CanBuildNewIndex);
            Assert.False(manager.ReloadLibrary(allowNewIndex: true).IsAvailable);
        }

        Assert.Equal(original, File.ReadAllBytes(PrimaryPath));
        Assert.False(File.Exists(BackupPath));
        Assert.True(manager.ReloadLibrary(allowNewIndex: true).IsAvailable);
        Assert.True(Assert.Single(manager.GetAllClips()).IsFavorite);
    }

    private ClipStorageManager CreateManager(
        FixedDurationClipEditor? editor = null,
        IProtectedMediaRegistry? registry = null) => new(
            _config,
            editor ?? new FixedDurationClipEditor(),
            NullLogger<ClipStorageManager>.Instance,
            registry ?? new ProtectedMediaRegistry());

    private void WriteCorruptIndexes()
    {
        File.WriteAllText(PrimaryPath, CorruptPrimary);
        File.WriteAllText(BackupPath, CorruptBackup);
    }

    private void AssertCorruptIndexesUntouched()
    {
        Assert.Equal(CorruptPrimary, File.ReadAllText(PrimaryPath));
        Assert.Equal(CorruptBackup, File.ReadAllText(BackupPath));
    }

    private string CreateMedia(string folder, string name)
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[1024]);
        return path;
    }

    private static CompletedMediaReceipt Receipt(string path) => new(
        path,
        "Game",
        DateTimeOffset.UtcNow,
        Guid.NewGuid(),
        Guid.NewGuid(),
        1,
        null);

    private static JsonElement[] ReadIndex(string path) =>
        JsonSerializer.Deserialize<JsonElement[]>(File.ReadAllText(path))!;

    public void Dispose() => TestFs.DeleteDirectoryWithRetry(_root);
}
