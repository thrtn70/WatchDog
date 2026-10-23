using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Storage;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Storage;

public sealed class ClipLibraryRebuilderTests : IDisposable
{
    private const string CorruptPrimary = "corrupt primary bytes";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"WatchDog_LibraryRebuild_{Guid.NewGuid():N}");
    private readonly StorageConfig _config;

    public ClipLibraryRebuilderTests()
    {
        Directory.CreateDirectory(_root);
        _config = new StorageConfig { BasePath = _root };
    }

    private string PrimaryPath => Path.Combine(_root, "clips-index.json");
    private string BackupPath => PrimaryPath + ".bak";

    [Fact]
    public async Task RebuildRestoresFolderClipsAndKeepsAttributionOfCapturesSavedWhileUnavailable()
    {
        WriteCorruptIndexes();
        var older = CreateMedia("Valorant", "older.mp4");
        var session = CreateMedia(Path.Combine("Valorant", "Sessions"), "session.mkv");
        var catalog = new FixedCatalog(session);
        var storage = CreateManager();
        await using var ingestion = CreateIngestion(storage, catalog);
        await ingestion.StartAsync(CancellationToken.None);
        var captured = CreateMedia("Valorant", "captured.mp4");
        var receipt = new CompletedMediaReceipt(
            captured,
            "Valorant",
            DateTimeOffset.UtcNow,
            Guid.NewGuid(),
            Guid.NewGuid(),
            4,
            2);

        var accepted = await ingestion.AcceptAsync(receipt);

        Assert.Equal(CompletedMediaIngestionDisposition.PendingRetry, accepted.Disposition);
        Assert.True(File.Exists(captured));
        Assert.Equal(0, ingestion.Status.UnattributedCount);

        var result = await Rebuilder(storage, ingestion, catalog).RebuildFromFolderAsync(allowNewIndex: true);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.PendingCapturesIndexed);
        Assert.Equal(1, result.ClipsIndexedFromFolder);
        Assert.Equal(0, ingestion.Status.PendingRetryCount);
        var clips = CreateManager().GetAllClips();
        Assert.Equal(2, clips.Count);
        var restoredCapture = Assert.Single(clips, clip => clip.FilePath == Path.GetFullPath(captured));
        Assert.Equal(receipt.SessionId, restoredCapture.SessionId);
        Assert.Equal(receipt.CaptureRequestId, restoredCapture.CaptureRequestId);
        Assert.Equal(receipt.MatchNumber, restoredCapture.MatchNumber);
        Assert.Null(restoredCapture.RecoveredAt);
        Assert.Contains(clips, clip =>
            clip.FilePath == Path.GetFullPath(older) &&
            clip.GameName == "Valorant" &&
            clip.RecoveredAt is not null);
        Assert.DoesNotContain(clips, clip => clip.FilePath == Path.GetFullPath(session));
        Assert.Equal(2, Directory.GetFiles(_root, "*.corrupt-*").Length);
    }

    [Fact]
    public async Task RebuildForgetsUnattributedMediaThatTheFolderScanIndexed()
    {
        var orphan = CreateMedia("Game", "orphan.mp4");
        var catalog = new FixedCatalog();
        await using (var healthy = CreateIngestion(CreateManager(), catalog))
        {
            await healthy.StartAsync(CancellationToken.None);
            Assert.Equal(1, healthy.Status.UnattributedCount);
        }

        WriteCorruptIndexes();
        var storage = CreateManager();
        await using var ingestion = CreateIngestion(storage, catalog);
        await ingestion.StartAsync(CancellationToken.None);
        Assert.Equal(1, ingestion.Status.UnattributedCount);

        var result = await Rebuilder(storage, ingestion, catalog).RebuildFromFolderAsync(allowNewIndex: true);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.ClipsIndexedFromFolder);
        Assert.Equal(0, ingestion.Status.UnattributedCount);
        Assert.Contains(CreateManager().GetAllClips(), clip => clip.FilePath == Path.GetFullPath(orphan));
    }

    [Fact]
    public async Task RebuildNeverReplacesAnIndexThatCouldNotBeRead()
    {
        var media = CreateMedia("Game", "kept.mp4");
        await CreateManager().IndexClipAsync(media, "Game");
        if (File.Exists(BackupPath)) File.Delete(BackupPath);
        var original = File.ReadAllBytes(PrimaryPath);
        var catalog = new FixedCatalog();

        using (File.Open(PrimaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var storage = CreateManager();
            await using var ingestion = CreateIngestion(storage, catalog);
            await ingestion.StartAsync(CancellationToken.None);

            var result = await Rebuilder(storage, ingestion, catalog).RebuildFromFolderAsync(allowNewIndex: true);

            Assert.False(result.Succeeded);
            Assert.False(result.Status.IsAvailable);
            Assert.False(string.IsNullOrWhiteSpace(result.FailureMessage));
        }

        Assert.Equal(original, File.ReadAllBytes(PrimaryPath));
    }

    [Fact]
    public async Task RebuildKeepsTheLibraryUnavailableWhenSessionMediaCannotBeIdentified()
    {
        WriteCorruptIndexes();
        CreateMedia(Path.Combine("Game", "Sessions"), "session.mkv");
        var storage = CreateManager();
        await using var ingestion = CreateIngestion(storage, new FixedCatalog());
        await ingestion.StartAsync(CancellationToken.None);

        var result = await Rebuilder(storage, ingestion, new FailingCatalog()).RebuildFromFolderAsync(allowNewIndex: true);

        Assert.False(result.Succeeded);
        Assert.False(storage.LibraryStatus.IsAvailable);
        Assert.Contains("session", result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CorruptPrimary, File.ReadAllText(PrimaryPath));
    }

    [Fact]
    public async Task RebuildReportsFilesThatCouldNotBeAddedAndCanBeRunAgain()
    {
        WriteCorruptIndexes();
        CreateMedia("Game", "good.mp4");
        CreateMedia("Game", "unreadable.mp4");
        var editor = new FixedDurationClipEditor { FailsFor = path => path.EndsWith("unreadable.mp4") };
        var catalog = new FixedCatalog();
        var storage = CreateManager(editor);
        await using var ingestion = CreateIngestion(storage, catalog);
        await ingestion.StartAsync(CancellationToken.None);
        var rebuilder = Rebuilder(storage, ingestion, catalog);

        var partial = await rebuilder.RebuildFromFolderAsync(allowNewIndex: true);

        Assert.False(partial.Succeeded);
        Assert.True(partial.Status.IsAvailable);
        Assert.True(partial.Status.IsRebuildIncomplete);
        Assert.Equal(1, partial.ClipsIndexedFromFolder);
        Assert.Contains("unreadable.mp4", partial.FailureMessage);

        editor.FailsFor = _ => false;
        var retried = await rebuilder.RebuildFromFolderAsync(allowNewIndex: false);

        Assert.True(retried.Succeeded);
        Assert.Equal(1, retried.ClipsIndexedFromFolder);
        Assert.False(retried.Status.IsRebuildIncomplete);
        Assert.Equal(2, CreateManager().GetAllClips().Count);
    }

    [Fact]
    public async Task RebuildKeepsAPromiseNotToReplaceTheIndex()
    {
        WriteCorruptIndexes();
        var catalog = new FixedCatalog();
        var storage = CreateManager();
        await using var ingestion = CreateIngestion(storage, catalog);
        await ingestion.StartAsync(CancellationToken.None);

        var result = await Rebuilder(storage, ingestion, catalog).RebuildFromFolderAsync(allowNewIndex: false);

        Assert.False(result.Succeeded);
        Assert.False(result.Status.IsAvailable);
        Assert.Equal(CorruptPrimary, File.ReadAllText(PrimaryPath));
    }

    [Fact]
    public async Task SessionMediaThatAppearsDuringTheRebuildIsNotTurnedIntoClips()
    {
        WriteCorruptIndexes();
        var lateSession = Path.Combine(_root, "Game", "Sessions", "started-during-rebuild.mkv");
        var catalog = new GrowingCatalog(lateSession);
        var storage = CreateManager();
        await using var ingestion = CreateIngestion(storage, new FixedCatalog());
        await ingestion.StartAsync(CancellationToken.None);

        var result = await Rebuilder(storage, ingestion, catalog).RebuildFromFolderAsync(allowNewIndex: true);

        Assert.True(result.Succeeded);
        Assert.True(File.Exists(lateSession));
        Assert.DoesNotContain(CreateManager().GetAllClips(), clip => clip.FilePath == Path.GetFullPath(lateSession));
    }

    private ClipStorageManager CreateManager(FixedDurationClipEditor? editor = null) => new(
        _config,
        editor ?? new FixedDurationClipEditor(),
        NullLogger<ClipStorageManager>.Instance,
        new ProtectedMediaRegistry());

    private CompletedMediaIngestionService CreateIngestion(
        IClipStorage storage,
        IStorageMediaCatalog catalog) => new(
            storage,
            _config,
            NullLogger<CompletedMediaIngestionService>.Instance,
            new ProtectedMediaRegistry(),
            catalog);

    private static ClipLibraryRebuilder Rebuilder(
        ClipStorageManager storage,
        CompletedMediaIngestionService ingestion,
        IStorageMediaCatalog catalog) => new(
            storage,
            ingestion,
            catalog,
            NullLogger<ClipLibraryRebuilder>.Instance);

    private void WriteCorruptIndexes()
    {
        File.WriteAllText(PrimaryPath, CorruptPrimary);
        File.WriteAllText(BackupPath, "corrupt backup bytes");
    }

    private string CreateMedia(string folder, string name)
    {
        var directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[1024]);
        return path;
    }

    public void Dispose() => TestFs.DeleteDirectoryWithRetry(_root);

    private sealed class FixedCatalog(params string[] ownedPaths) : IStorageMediaCatalog
    {
        public Task<StorageMediaCatalogSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageMediaCatalogSnapshot(
                ownedPaths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase),
                0,
                []));
    }

    // The first snapshot is taken before the rebuild touches anything. Capture keeps running,
    // so a session segment can start before the folder scan; later snapshots include it.
    private sealed class GrowingCatalog(string lateSessionPath) : IStorageMediaCatalog
    {
        private int _snapshots;

        public Task<StorageMediaCatalogSnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
        {
            var owned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Interlocked.Increment(ref _snapshots) == 1)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lateSessionPath)!);
                File.WriteAllBytes(lateSessionPath, new byte[1024]);
            }
            else
            {
                owned.Add(Path.GetFullPath(lateSessionPath));
            }

            return Task.FromResult(new StorageMediaCatalogSnapshot(owned, 0, []));
        }
    }

    private sealed class FailingCatalog : IStorageMediaCatalog
    {
        public Task<StorageMediaCatalogSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromException<StorageMediaCatalogSnapshot>(new IOException("sessions index is locked"));
    }
}
