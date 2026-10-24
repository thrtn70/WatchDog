using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Highlights;
using WatchDog.Core.Storage;

namespace WatchDog.Core.Tests.Storage;

public sealed class CompletedMediaIngestionServiceTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), "WatchDog_Ingestion_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task AcceptAsync_SerializesAndPreservesImmutableAttribution()
    {
        var storage = new RecordingClipStorage();
        await using var service = CreateService(storage);
        await service.StartAsync(CancellationToken.None);
        var first = Receipt("first.mp4", 1);
        var second = Receipt("second.mp4", 2);

        var results = await Task.WhenAll(
            service.AcceptAsync(first),
            service.AcceptAsync(second));

        Assert.All(results, result => Assert.Equal(
            CompletedMediaIngestionDisposition.Indexed, result.Disposition));
        Assert.Equal(1, storage.MaximumConcurrency);
        Assert.Collection(
            storage.Indexed.OrderBy(item => item.CaptureGeneration),
            item => Assert.Equal(first, item),
            item => Assert.Equal(second, item));
        Assert.Equal(0, service.Status.PendingRetryCount);
    }

    [Fact]
    public async Task FailedIndex_RemainsDurableAndRetriesOnNextStartup()
    {
        var receipt = Receipt("retry.mp4", 3);
        var failingStorage = new RecordingClipStorage { Failure = new IOException("index unavailable") };
        await using (var first = CreateService(failingStorage))
        {
            await first.StartAsync(CancellationToken.None);
            var result = await first.AcceptAsync(receipt);

            Assert.Equal(CompletedMediaIngestionDisposition.PendingRetry, result.Disposition);
            Assert.Equal(1, first.Status.PendingRetryCount);
            Assert.Contains("index unavailable", first.Status.LastFailure);
        }

        var recoveredStorage = new RecordingClipStorage();
        await using var recovered = CreateService(recoveredStorage);
        await recovered.StartAsync(CancellationToken.None);

        Assert.Equal(receipt, Assert.Single(recoveredStorage.Indexed));
        Assert.Equal(0, recovered.Status.PendingRetryCount);
    }

    [Fact]
    public async Task PendingReceipt_CanDurablyAddHighlightBeforeRestartRetry()
    {
        var receipt = Receipt("highlight-retry.mp4", 8);
        var failingStorage = new RecordingClipStorage { Failure = new IOException("index unavailable") };
        await using (var first = CreateService(failingStorage))
        {
            await first.StartAsync(CancellationToken.None);
            Assert.Equal(
                CompletedMediaIngestionDisposition.PendingRetry,
                (await first.AcceptAsync(receipt)).Disposition);
            Assert.Equal(
                CompletedMediaIngestionDisposition.PendingRetry,
                (await first.EnrichAsync(receipt, HighlightType.Kill)).Disposition);
        }

        var recoveredStorage = new RecordingClipStorage();
        await using var recovered = CreateService(recoveredStorage);
        await recovered.StartAsync(CancellationToken.None);

        Assert.Equal(HighlightType.Kill, Assert.Single(recoveredStorage.IndexedHighlightTypes));
    }

    [Fact]
    public async Task StartupDiscovery_ReportsUnattributedMediaWithoutGuessingContext()
    {
        var orphan = Path.Combine(_path, "orphan.mp4");
        var storage = new RecordingClipStorage { UnindexedFiles = [orphan] };
        await using var service = CreateService(storage);

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(storage.Indexed);
        Assert.Equal(1, service.Status.UnattributedCount);
        Assert.Contains("orphan.mp4", service.Status.LastFailure);
    }

    [Fact]
    public async Task StartupDiscovery_DoesNotReportSessionGraphOwnedMediaAsOrphaned()
    {
        var screen = Path.Combine(_path, "session-screen.mkv");
        var camera = Path.Combine(_path, "session-camera.mp4");
        var storage = new RecordingClipStorage { UnindexedFiles = [screen, camera] };
        var catalog = new FixedMediaCatalog(screen, camera);
        await using var service = new CompletedMediaIngestionService(
            storage,
            new StorageConfig { BasePath = _path },
            NullLogger<CompletedMediaIngestionService>.Instance,
            new ProtectedMediaRegistry(),
            catalog);

        await service.StartAsync(CancellationToken.None);

        Assert.Equal(0, service.Status.UnattributedCount);
        Assert.Null(service.Status.LastFailure);
    }

    [Fact]
    public async Task DrainAsync_IsBoundedWhileAcceptedReceiptRemainsDurable()
    {
        var storage = new RecordingClipStorage { BlockIndexing = true };
        await using var service = CreateService(storage);
        await service.StartAsync(CancellationToken.None);
        var accept = service.AcceptAsync(Receipt("blocked.mp4", 4));
        await storage.IndexingStarted.Task.WaitAsync(Helpers.TestTimeouts.Signal);

        var drain = await service.DrainAsync(TimeSpan.FromMilliseconds(20));

        Assert.False(drain.Drained);
        Assert.True(drain.PendingIsDurable);
        storage.ReleaseIndexing.TrySetResult();
        Assert.Equal(
            CompletedMediaIngestionDisposition.Indexed,
            (await accept).Disposition);
    }

    [Fact]
    public async Task PendingMedia_IsProtectedUntilDurableIndexCompletes()
    {
        var registry = new ProtectedMediaRegistry();
        var storage = new RecordingClipStorage { BlockIndexing = true };
        await using var service = new CompletedMediaIngestionService(
            storage,
            new StorageConfig { BasePath = _path },
            NullLogger<CompletedMediaIngestionService>.Instance,
            registry);
        await service.StartAsync(CancellationToken.None);
        var receipt = Receipt("protected.mp4", 5);

        var accept = service.AcceptAsync(receipt);
        await storage.IndexingStarted.Task.WaitAsync(Helpers.TestTimeouts.Signal);

        Assert.True(registry.IsProtected(receipt.FilePath));
        storage.ReleaseIndexing.TrySetResult();
        Assert.Equal(CompletedMediaIngestionDisposition.Indexed, (await accept).Disposition);
        Assert.False(registry.IsProtected(receipt.FilePath));
    }

    [Fact]
    public async Task MissingPrimary_RecoversPendingWorkFromBackupAndRepairsPrimary()
    {
        var receipt = Receipt("missing-primary.mp4", 6);
        await CreateDurableRetryAsync(receipt);
        var primary = Path.Combine(_path, "completed-media-pending.json");
        File.Delete(primary);

        var recoveredStorage = new RecordingClipStorage();
        await using var recovered = CreateService(recoveredStorage);
        await recovered.StartAsync(CancellationToken.None);

        Assert.Equal(receipt, Assert.Single(recoveredStorage.Indexed));
        Assert.True(File.Exists(primary));
    }

    [Fact]
    public async Task CorruptPrimary_UsesValidBackupAndPreservesCorruptEvidence()
    {
        var receipt = Receipt("corrupt-primary.mp4", 7);
        await CreateDurableRetryAsync(receipt);
        var primary = Path.Combine(_path, "completed-media-pending.json");
        File.WriteAllText(primary, "{ corrupt");

        var recoveredStorage = new RecordingClipStorage();
        await using var recovered = CreateService(recoveredStorage);
        await recovered.StartAsync(CancellationToken.None);

        Assert.Equal(receipt, Assert.Single(recoveredStorage.Indexed));
        Assert.Single(Directory.GetFiles(_path, "completed-media-pending.json.corrupt-*"));
    }

    [Fact]
    public async Task CorruptPrimaryAndBackup_FailStartupVisibly()
    {
        Directory.CreateDirectory(_path);
        File.WriteAllText(Path.Combine(_path, "completed-media-pending.json"), "{ corrupt");
        File.WriteAllText(Path.Combine(_path, "completed-media-pending.json.bak"), "{ also-corrupt");
        await using var service = CreateService(new RecordingClipStorage());

        await Assert.ThrowsAnyAsync<InvalidDataException>(() =>
            service.StartAsync(CancellationToken.None));
    }

    private async Task CreateDurableRetryAsync(CompletedMediaReceipt receipt)
    {
        var failingStorage = new RecordingClipStorage { Failure = new IOException("index unavailable") };
        await using var service = CreateService(failingStorage);
        await service.StartAsync(CancellationToken.None);
        var result = await service.AcceptAsync(receipt);
        Assert.Equal(CompletedMediaIngestionDisposition.PendingRetry, result.Disposition);
        Assert.True(File.Exists(Path.Combine(_path, "completed-media-pending.json.bak")));
    }

    private CompletedMediaIngestionService CreateService(IClipStorage storage) =>
        new(
            storage,
            new StorageConfig { BasePath = _path },
            NullLogger<CompletedMediaIngestionService>.Instance);

    private CompletedMediaReceipt Receipt(string name, long generation) => new(
        Path.Combine(_path, name),
        "Test Game",
        DateTimeOffset.UtcNow,
        Guid.NewGuid(),
        Guid.NewGuid(),
        generation,
        generation == 1 ? null : (int?)generation);

    public void Dispose()
    {
        Helpers.TestFs.DeleteDirectoryWithRetry(_path);
    }

    private sealed class RecordingClipStorage : IClipStorage
    {
        private int _concurrency;
        public int MaximumConcurrency { get; private set; }
        public Exception? Failure { get; init; }
        public bool BlockIndexing { get; init; }
        public IReadOnlyList<string> UnindexedFiles { get; init; } = [];
        public List<CompletedMediaReceipt> Indexed { get; } = [];
        public List<HighlightType?> IndexedHighlightTypes { get; } = [];
        public TaskCompletionSource IndexingStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseIndexing { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ClipMetadata> IndexCompletedMediaAsync(
            CompletedMediaReceipt media,
            HighlightType? highlightType = null,
            CancellationToken ct = default)
        {
            var concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            try
            {
                IndexingStarted.TrySetResult();
                if (BlockIndexing)
                    await ReleaseIndexing.Task.WaitAsync(ct);
                if (Failure is not null)
                    throw Failure;
                Indexed.Add(media);
                IndexedHighlightTypes.Add(highlightType);
                return new ClipMetadata
                {
                    FilePath = media.FilePath,
                    FileName = Path.GetFileName(media.FilePath),
                    GameName = media.GameName,
                    CreatedAt = media.CompletedAt,
                    SessionId = media.SessionId,
                    CaptureRequestId = media.CaptureRequestId,
                    CaptureGeneration = media.CaptureGeneration,
                    MatchNumber = media.MatchNumber,
                    HighlightType = highlightType,
                };
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        public IReadOnlyList<string> FindUnindexedMediaFiles() => UnindexedFiles;
        public IReadOnlyList<ClipMetadata> GetAllClips() => [];
        public IReadOnlyList<ClipMetadata> GetClipsByGame(string gameName) => [];
        public IReadOnlyList<ClipMetadata> GetClipsBySession(Guid sessionId) => [];
        public Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, HighlightType? highlightType, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, HighlightType? highlightType, Guid? sessionId, int? matchNumber, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ScanAndIndexAsync(CancellationToken ct = default) => Task.FromResult(0);
        public void DeleteClip(string filePath) { }
        public void ToggleFavorite(string filePath) { }
        public Task RunCleanupAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void AddTags(string filePath, IEnumerable<string> tags) { }
        public void RemoveTags(string filePath, IEnumerable<string> tags) { }
        public IReadOnlySet<string> GetAllTags() => new HashSet<string>();
        public IReadOnlyList<ClipMetadata> GetClipsByTag(string tag) => [];
    }

    private sealed class FixedMediaCatalog(params string[] paths) : IStorageMediaCatalog
    {
        public Task<StorageMediaCatalogSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageMediaCatalogSnapshot(
                paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase),
                0,
                []));
    }
}
