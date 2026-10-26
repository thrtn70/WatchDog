using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WatchDog.Core.ClipEditor;

namespace WatchDog.Core.Storage;

public sealed class ClipStorageManager : IClipStorage, IStorageMaintenance, IClipLibraryRecovery
{
    private readonly StorageConfig _config;
    private readonly IClipEditor _clipEditor;
    private readonly ILogger<ClipStorageManager> _logger;
    private readonly IProtectedMediaRegistry _protectedMedia;
    private readonly List<ClipMetadata> _clips = [];
    private List<ClipMetadata> _persistedClips = [];
    private readonly object _lock = new();
    private readonly object _deletionLock = new();
    private ClipLibraryStatus _libraryStatus = ClipLibraryStatus.Available;
    private bool _primaryNeedsRecovery;

    private const int RecoveryBatchSize = 50;
    private const string LibraryUnavailableMessage =
        "The clip library could not be loaded, so favorites, tags, deletions and cleanup are paused. " +
        "New recordings are still saved to disk. Rebuild the library from the WatchDog window to restore it.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public ClipStorageManager(StorageConfig config, IClipEditor clipEditor, ILogger<ClipStorageManager> logger)
        : this(config, clipEditor, logger, new ProtectedMediaRegistry())
    {
    }

    public ClipStorageManager(
        StorageConfig config,
        IClipEditor clipEditor,
        ILogger<ClipStorageManager> logger,
        IProtectedMediaRegistry protectedMedia)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clipEditor = clipEditor ?? throw new ArgumentNullException(nameof(clipEditor));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _protectedMedia = protectedMedia ?? throw new ArgumentNullException(nameof(protectedMedia));

        Directory.CreateDirectory(config.BasePath);
        _libraryStatus = LoadLibrary(allowNewIndex: false);
    }

    public ClipLibraryStatus LibraryStatus => Volatile.Read(ref _libraryStatus);

    public ClipLibraryStatus ReloadLibrary(bool allowNewIndex)
    {
        lock (_deletionLock)
        {
            lock (_lock)
            {
                var current = LibraryStatus;
                if (current.IsAvailable) return current;

                ClipLibraryStatus reloaded;
                try
                {
                    reloaded = LoadLibrary(allowNewIndex);
                }
                catch
                {
                    _clips.Clear();
                    _persistedClips = [];
                    throw;
                }

                Volatile.Write(ref _libraryStatus, reloaded);
                if (reloaded.IsAvailable)
                    _logger.LogWarning("Clip library restored from {Index}", IndexPath);
                return reloaded;
            }
        }
    }

    // A damaged active index starts the library unavailable instead of failing startup.
    // Capture continues; every mutation and cleanup is refused until ReloadLibrary succeeds.
    private ClipLibraryStatus LoadLibrary(bool allowNewIndex)
    {
        try
        {
            LoadIndex(allowNewIndex);
        }
        catch (ActiveIndexUnavailableException ex)
        {
            lock (_lock)
            {
                _clips.Clear();
                _persistedClips = [];
            }
            _logger.LogError(
                ex.InnerException,
                "Clip library is unavailable: {Problem} Damaged bytes preserved at: {Evidence}",
                ex.Message,
                string.Join(", ", ex.EvidencePaths));
            return new ClipLibraryStatus(false, ex.Message, ex.EvidencePaths, ex.CanBuildNewIndex);
        }

        _persistedClips = [.. _clips];
        ReconcilePendingDeletion();
        _persistedClips = [.. _clips];
        return File.Exists(RebuildMarkerPath)
            ? ClipLibraryStatus.RebuildIncomplete
            : ClipLibraryStatus.Available;
    }

    private void ThrowIfLibraryUnavailable()
    {
        if (!LibraryStatus.IsAvailable)
            throw new ClipLibraryUnavailableException(LibraryUnavailableMessage);
    }

    public IReadOnlyList<ClipMetadata> GetAllClips()
    {
        lock (_lock)
            return [.. _clips.OrderByDescending(c => c.CreatedAt)];
    }

    public IReadOnlyList<ClipMetadata> GetClipsByGame(string gameName)
    {
        lock (_lock)
            return [.. _clips
                .Where(c => string.Equals(c.GameName, gameName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.CreatedAt)];
    }

    public IReadOnlyList<ClipMetadata> GetClipsBySession(Guid sessionId)
    {
        lock (_lock)
            return [.. _clips
                .Where(c => c.SessionId == sessionId)
                .OrderByDescending(c => c.CreatedAt)];
    }

    public Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, CancellationToken ct = default)
        => IndexClipAsync(filePath, gameName, highlightType: null, sessionId: null, matchNumber: null, ct);

    public Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, Highlights.HighlightType? highlightType, CancellationToken ct = default)
        => IndexClipAsync(filePath, gameName, highlightType, sessionId: null, matchNumber: null, ct);

    public async Task<ClipMetadata> IndexClipAsync(string filePath, string? gameName, Highlights.HighlightType? highlightType, Guid? sessionId, int? matchNumber, CancellationToken ct = default)
        => await IndexClipCoreAsync(
            filePath,
            gameName,
            highlightType,
            sessionId,
            captureRequestId: null,
            captureGeneration: null,
            matchNumber,
            createdAt: null,
            ct);

    public Task<ClipMetadata> IndexCompletedMediaAsync(
        CompletedMediaReceipt media,
        Highlights.HighlightType? highlightType = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(media);
        return IndexClipCoreAsync(
            media.FilePath,
            media.GameName,
            highlightType,
            media.SessionId,
            media.CaptureRequestId,
            media.CaptureGeneration,
            media.MatchNumber,
            media.CompletedAt,
            ct);
    }

    private async Task<ClipMetadata> IndexClipCoreAsync(
        string filePath,
        string? gameName,
        Highlights.HighlightType? highlightType,
        Guid? sessionId,
        Guid? captureRequestId,
        long? captureGeneration,
        int? matchNumber,
        DateTimeOffset? createdAt,
        CancellationToken ct)
    {
        ThrowIfLibraryUnavailable();
        ct.ThrowIfCancellationRequested();
        var canonicalPath = StoragePath.Canonicalize(filePath);

        // Deduplicate: if this file is already indexed, return existing metadata
        lock (_lock)
        {
            var existing = _clips.FirstOrDefault(c =>
                PathsEqual(c.FilePath, canonicalPath));
            if (existing is not null)
                return Enrich(
                    existing,
                    gameName,
                    highlightType,
                    sessionId,
                    captureRequestId,
                    captureGeneration,
                    matchNumber);
        }

        var metadata = await MeasureAsync(
            new ClipMetadata
            {
                FilePath = canonicalPath,
                FileName = Path.GetFileName(canonicalPath),
                GameName = gameName,
                HighlightType = highlightType,
                SessionId = sessionId,
                CaptureRequestId = captureRequestId,
                CaptureGeneration = captureGeneration,
                MatchNumber = matchNumber,
            },
            createdAt,
            ct);

        lock (_lock)
        {
            // Second dedup check under lock — guards against concurrent IndexClipAsync calls
            // that both passed the first check before either added the clip
            var duplicate = _clips.FirstOrDefault(c =>
                PathsEqual(c.FilePath, canonicalPath));
            if (duplicate is not null)
                return Enrich(
                    duplicate,
                    gameName,
                    highlightType,
                    sessionId,
                    captureRequestId,
                    captureGeneration,
                    matchNumber);

            _clips.Add(metadata);
            SaveIndex();
        }

        var highlightTag = highlightType is not null ? $" [{highlightType}]" : "";
        _logger.LogInformation("Indexed clip: {File} ({Duration:mm\\:ss}, {Size:F1}MB){Tag}",
            metadata.FileName, metadata.Duration, metadata.FileSizeBytes / (1024.0 * 1024.0), highlightTag);

        return metadata;
    }

    // Reads size, duration and thumbnail from the file; identity fields come from the caller.
    private async Task<ClipMetadata> MeasureAsync(
        ClipMetadata identity,
        DateTimeOffset? createdAt,
        CancellationToken ct)
    {
        var fileInfo = new FileInfo(identity.FilePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("Clip file not found", identity.FilePath);

        var duration = await _clipEditor.GetDurationAsync(identity.FilePath, ct);

        // Generate thumbnail at 1 second in (or start if shorter)
        var thumbTs = duration.TotalSeconds > 1 ? TimeSpan.FromSeconds(1) : TimeSpan.Zero;
        string? thumbPath = null;
        try
        {
            if (IsManagedPath(identity.FilePath))
                thumbPath = await _clipEditor.GenerateThumbnailAsync(identity.FilePath, thumbTs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate thumbnail for {File}", identity.FilePath);
        }

        return identity with
        {
            FileName = fileInfo.Name,
            CreatedAt = createdAt ?? fileInfo.CreationTimeUtc,
            Duration = duration,
            FileSizeBytes = fileInfo.Length,
            ThumbnailPath = thumbPath,
        };
    }

    public IReadOnlyList<string> FindUnindexedMediaFiles()
    {
        // Without an index every file would look unindexed; unknown is not the same as unindexed.
        if (!LibraryStatus.IsAvailable) return [];

        HashSet<string> indexed;
        lock (_lock)
            indexed = _clips.Select(c => StoragePath.Canonicalize(c.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var extensions = new[] { "*.mp4", "*.mkv", "*.flv" };
        return EnumerateMedia(_config.BasePath, extensions)
            .Select(StoragePath.Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !indexed.Contains(path))
            .ToArray();
    }

    public async Task<int> ScanAndIndexAsync(CancellationToken ct = default)
    {
        ThrowIfLibraryUnavailable();
        var newFiles = FindUnindexedMediaFiles();

        var count = 0;
        foreach (var file in newFiles)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                await IndexClipAsync(file, InferGameName(file), ct);
                count++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to index {File}, skipping", file);
            }
        }

        if (count > 0)
            _logger.LogInformation("Scanned and indexed {Count} new clips from disk", count);

        return count;
    }

    public async Task<ClipRecoveryScanResult> IndexRecoveredMediaAsync(
        IReadOnlySet<string> excludedPaths,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(excludedPaths);
        ThrowIfLibraryUnavailable();
        var excluded = excludedPaths
            .Select(StoragePath.Canonicalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // Protected media is still being written or waits for its capture receipt, which
        // carries the real attribution and indexes it.
        var candidates = FindUnindexedMediaFiles()
            .Where(path => !excluded.Contains(path) && !_protectedMedia.IsProtected(path))
            .ToArray();

        var recoveredAt = DateTimeOffset.UtcNow;
        var batch = new List<ClipMetadata>(RecoveryBatchSize);
        var indexed = 0;
        var failed = 0;
        string? firstFailure = null;
        foreach (var file in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                batch.Add(await MeasureAsync(
                    new ClipMetadata
                    {
                        FilePath = file,
                        FileName = Path.GetFileName(file),
                        GameName = InferGameName(file),
                        RecoveredAt = recoveredAt,
                    },
                    createdAt: null,
                    ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                firstFailure ??= $"{Path.GetFileName(file)}: {ex.Message}";
                _logger.LogWarning(ex, "Could not add {File} to the rebuilt clip library", file);
            }

            // One index write per clip would rewrite a large library thousands of times.
            if (batch.Count >= RecoveryBatchSize)
                indexed += CommitRecoveredClips(batch);
        }

        indexed += CommitRecoveredClips(batch);
        _logger.LogInformation(
            "Recovery scan added {Indexed} clips from disk; {Failed} files could not be added",
            indexed,
            failed);
        if (failed == 0)
            CompleteRebuild();
        return new ClipRecoveryScanResult(indexed, failed, firstFailure);
    }

    private int CommitRecoveredClips(List<ClipMetadata> batch)
    {
        if (batch.Count == 0) return 0;
        lock (_lock)
        {
            // A capture receipt may have indexed a file while the scan was measuring it.
            var known = _clips.Select(clip => clip.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var added = batch.Where(clip => known.Add(clip.FilePath)).ToList();
            batch.Clear();
            if (added.Count == 0) return 0;

            _clips.AddRange(added);
            SaveIndex();
            return added.Count;
        }
    }

    private void CompleteRebuild()
    {
        lock (_lock)
        {
            if (!LibraryStatus.IsRebuildIncomplete) return;
            try
            {
                File.Delete(RebuildMarkerPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not clear the library rebuild marker {Marker}", RebuildMarkerPath);
                return;
            }

            Volatile.Write(ref _libraryStatus, ClipLibraryStatus.Available);
            _logger.LogInformation("Clip library rebuild finished");
        }
    }

    // Infer game name from parent folder
    private static string? InferGameName(string file)
    {
        var parentDir = Path.GetFileName(Path.GetDirectoryName(file));
        return string.Equals(parentDir, "WatchDog", StringComparison.OrdinalIgnoreCase)
            ? null : parentDir;
    }

    public void DeleteClip(string filePath)
    {
        ThrowIfLibraryUnavailable();
        var canonicalPath = StoragePath.Canonicalize(filePath);
        // Path containment: only allow deleting files under the configured base directory
        if (!IsManagedPath(canonicalPath))
        {
            _logger.LogWarning("Blocked DeleteClip attempt outside base path: {Path}", canonicalPath);
            throw new InvalidOperationException("Recordings in previous folders are read-only. Open the folder to manage the original file.");
        }

        lock (_deletionLock)
        {
            ReconcilePendingDeletion();
            DeleteClipCore(canonicalPath);
        }

        _logger.LogInformation("Deleted clip: {File}", canonicalPath);
    }

    private void DeleteClipCore(string canonicalPath)
    {
        using var deletionReservation = _protectedMedia.TryReserveDeletion(canonicalPath);
        if (deletionReservation is null)
            throw new InvalidOperationException("This clip is currently in use and cannot be deleted safely.");

        lock (_lock)
        {
            var clip = _clips.FirstOrDefault(c =>
                PathsEqual(c.FilePath, canonicalPath));
            if (clip is null) return;

            // Prove that the index replacement path is currently writable before the
            // irreversible file deletion. A later external failure is still reconciled
            // from the durable intent on the next startup.
            SaveIndex();
            var intent = new DeletionIntent(canonicalPath, clip.ThumbnailPath);
            PersistDeletionIntent(intent);
            try
            {
                if (File.Exists(canonicalPath))
                    File.Delete(canonicalPath);
            }
            catch
            {
                ClearDeletionIntent();
                throw;
            }

            _clips.Remove(clip);
            SaveIndex();
            DeleteThumbnail(intent.ThumbnailPath);
            ClearDeletionIntent();
        }
    }

    public void ToggleFavorite(string filePath)
    {
        ThrowIfLibraryUnavailable();
        lock (_lock)
        {
            var idx = _clips.FindIndex(c =>
                PathsEqual(c.FilePath, filePath));
            if (idx < 0) return;

            // An explicit favorite choice replaces the favorite a recovered clip lost.
            _clips[idx] = _clips[idx] with { IsFavorite = !_clips[idx].IsFavorite, RecoveredAt = null };
            SaveIndex();
        }
    }

    public void AddTags(string filePath, IEnumerable<string> tags)
    {
        ThrowIfLibraryUnavailable();
        var validTags = tags
            .Select(t => t.Trim())
            .Where(t => t.Length > 0 && t.Length <= 32)
            .ToList();

        if (validTags.Count == 0) return;

        lock (_lock)
        {
            var idx = _clips.FindIndex(c =>
                PathsEqual(c.FilePath, filePath));
            if (idx < 0) return;

            var existing = _clips[idx].Tags.ToList();
            foreach (var tag in validTags)
            {
                if (!existing.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    existing.Add(tag);
            }

            // Cap at 20 tags per clip
            if (existing.Count > 20)
                existing = existing.Take(20).ToList();

            _clips[idx] = _clips[idx] with { Tags = existing.AsReadOnly() };
            SaveIndex();
        }
    }

    public void RemoveTags(string filePath, IEnumerable<string> tags)
    {
        ThrowIfLibraryUnavailable();
        var tagsToRemove = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);

        lock (_lock)
        {
            var idx = _clips.FindIndex(c =>
                PathsEqual(c.FilePath, filePath));
            if (idx < 0) return;

            var filtered = _clips[idx].Tags
                .Where(t => !tagsToRemove.Contains(t))
                .ToList();

            _clips[idx] = _clips[idx] with { Tags = filtered.AsReadOnly() };
            SaveIndex();
        }
    }

    public IReadOnlySet<string> GetAllTags()
    {
        lock (_lock)
        {
            return _clips
                .SelectMany(c => c.Tags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<ClipMetadata> GetClipsByTag(string tag)
    {
        lock (_lock)
        {
            return _clips
                .Where(c => c.Tags.Any(t =>
                    string.Equals(t, tag, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }
    }

    public async Task RunCleanupAsync(CancellationToken ct = default) =>
        _ = await RunCleanupWithResultAsync(ct);

    Task<StorageCleanupResult> IStorageMaintenance.RunCleanupAsync(CancellationToken ct) =>
        RunCleanupWithResultAsync(0, ct);

    Task<StorageCleanupResult> IStorageMaintenance.RunCleanupAsync(
        long protectedExternalBytes,
        CancellationToken ct) => RunCleanupWithResultAsync(protectedExternalBytes, ct);

    Task<StorageCleanupResult> IStorageMaintenance.RunCleanupAsync(
        IReadOnlyDictionary<string, long> protectedExternalFiles,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(protectedExternalFiles);
        if (protectedExternalFiles.Values.Any(size => size < 0))
            throw new ArgumentOutOfRangeException(nameof(protectedExternalFiles));
        lock (_deletionLock)
        {
            HashSet<string> indexedPaths;
            lock (_lock)
            {
                indexedPaths = _clips
                    .Where(clip => IsManagedPath(clip.FilePath))
                    .Select(clip => StoragePath.Canonicalize(clip.FilePath))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }

            var uniqueExternalBytes = protectedExternalFiles
                .Where(item => !indexedPaths.Contains(StoragePath.Canonicalize(item.Key)))
                .Select(item => item.Value)
                .Aggregate(0L, (total, size) => checked(total + size));
            return RunCleanupWithResultLocked(uniqueExternalBytes, ct);
        }
    }

    public Task<StorageCleanupResult> RunCleanupWithResultAsync(CancellationToken ct = default)
        => RunCleanupWithResultAsync(0, ct);

    private Task<StorageCleanupResult> RunCleanupWithResultAsync(
        long protectedExternalBytes,
        CancellationToken ct)
    {
        lock (_deletionLock)
            return RunCleanupWithResultLocked(protectedExternalBytes, ct);
    }

    private Task<StorageCleanupResult> RunCleanupWithResultLocked(
        long protectedExternalBytes,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (protectedExternalBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(protectedExternalBytes));
        // Media whose metadata is unknown is never deleted, and a pending deletion intent is
        // left for reconciliation once the index is back.
        if (!LibraryStatus.IsAvailable)
        {
            _logger.LogDebug("Storage cleanup skipped because the clip library is unavailable");
            return Task.FromResult(new StorageCleanupResult(0, 0, [], 0, false, protectedExternalBytes)
            {
                SuspendedReason = "The clip library is unavailable, so WatchDog cannot tell which files are clips.",
            });
        }

        var now = DateTimeOffset.UtcNow;
        var maxAge = TimeSpan.FromDays(_config.AutoDeleteDays);
        var minAge = TimeSpan.FromHours(24);
        var maxBytes = GetMaxStorageBytes();
        var attemptedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<StorageCleanupFailure>();
        var deletedCount = 0;
        long deletedBytes = 0;

        try
        {
            ReconcilePendingDeletion();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            long blockedBytes;
            lock (_lock)
                blockedBytes = CalculateRemainingIndexedBytes();
            return Task.FromResult(new StorageCleanupResult(
                0,
                0,
                [new StorageCleanupFailure(DeletionIntentPath, ex.Message, true)],
                blockedBytes,
                false,
                protectedExternalBytes));
        }

        List<string> expiredCandidates;
        lock (_lock)
        {
            expiredCandidates = _clips
                .Where(clip => IsManagedPath(clip.FilePath) &&
                    now - clip.CreatedAt > maxAge &&
                    now - clip.CreatedAt > minAge)
                .Select(clip => clip.FilePath)
                .ToList();
        }

        foreach (var candidatePath in expiredCandidates)
        {
            ct.ThrowIfCancellationRequested();
            attemptedPaths.Add(StoragePath.Canonicalize(candidatePath));
            var outcome = TryDeleteForCleanup(candidatePath, now, minAge, maxAge, requireExpired: true, ct);
            RecordCleanupOutcome(
                outcome,
                failures,
                ref deletedCount,
                ref deletedBytes);
            if (outcome.AbortCleanup)
                break;
        }

        while (failures.All(failure => !failure.AbortCleanup))
        {
            ct.ThrowIfCancellationRequested();
            string? candidatePath;
            lock (_lock)
            {
                if (IsQuotaMet(CalculateRemainingIndexedBytes(), protectedExternalBytes, maxBytes))
                    break;

                candidatePath = _clips
                    .Where(clip => IsManagedPath(clip.FilePath) &&
                        now - clip.CreatedAt > minAge &&
                        !attemptedPaths.Contains(StoragePath.Canonicalize(clip.FilePath)))
                    .OrderBy(clip => clip.CreatedAt)
                    .Select(clip => clip.FilePath)
                    .FirstOrDefault();
            }

            if (candidatePath is null)
                break;

            attemptedPaths.Add(StoragePath.Canonicalize(candidatePath));
            var outcome = TryDeleteForCleanup(candidatePath, now, minAge, maxAge, requireExpired: false, ct);
            RecordCleanupOutcome(
                outcome,
                failures,
                ref deletedCount,
                ref deletedBytes);
            if (outcome.AbortCleanup)
                break;
        }

        ct.ThrowIfCancellationRequested();
        long remainingBytes;
        int retainedRecovered;
        lock (_lock)
        {
            remainingBytes = CalculateRemainingIndexedBytes();
            retainedRecovered = _clips.Count(clip => clip.RecoveredAt is not null && IsManagedPath(clip.FilePath));
        }

        var result = new StorageCleanupResult(
            deletedCount,
            deletedBytes,
            failures.AsReadOnly(),
            remainingBytes,
            IsQuotaMet(remainingBytes, protectedExternalBytes, maxBytes),
            protectedExternalBytes)
        {
            RetainedRecoveredClips = retainedRecovered,
        };
        if (deletedCount > 0 || failures.Count > 0)
        {
            _logger.LogInformation(
                "Cleanup completed: {DeletedCount} clips and {DeletedBytes} bytes deleted; " +
                "{FailureCount} failures; {RemainingBytes} indexed bytes remain; quota met: {QuotaMet}",
                result.DeletedCount,
                result.DeletedBytes,
                result.Failures.Count,
                result.RemainingIndexedBytes,
                result.QuotaMet);
        }

        return Task.FromResult(result);
    }

    private CleanupDeletionOutcome TryDeleteForCleanup(
        string candidatePath,
        DateTimeOffset now,
        TimeSpan minAge,
        TimeSpan maxAge,
        bool requireExpired,
        CancellationToken ct)
    {
        using var deletionReservation = _protectedMedia.TryReserveDeletion(candidatePath);
        if (deletionReservation is null)
            return CleanupDeletionOutcome.Skipped;

        lock (_lock)
        {
            var clip = _clips.FirstOrDefault(current => PathsEqual(current.FilePath, candidatePath));
            if (clip is null ||
                !IsManagedPath(clip.FilePath) ||
                clip.IsFavorite ||
                clip.RecoveredAt is not null ||
                now - clip.CreatedAt <= minAge ||
                (requireExpired && now - clip.CreatedAt <= maxAge) ||
                !File.Exists(clip.FilePath))
            {
                return CleanupDeletionOutcome.Skipped;
            }

            ct.ThrowIfCancellationRequested();
            long fileSize;
            var intent = new DeletionIntent(clip.FilePath, clip.ThumbnailPath);
            try
            {
                PersistDeletionIntent(intent);
                fileSize = new FileInfo(clip.FilePath).Length;
                File.Delete(clip.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { ClearDeletionIntent(); }
                catch (Exception clearFailure)
                {
                    return CleanupDeletionOutcome.Failed(
                        clip.FilePath,
                        $"{ex.Message} Deletion intent cleanup also failed: {clearFailure.Message}",
                        abortCleanup: true);
                }
                _logger.LogWarning(ex, "Cleanup could not delete {File}; metadata was retained", clip.FilePath);
                return CleanupDeletionOutcome.Failed(clip.FilePath, ex.Message);
            }

            _clips.Remove(clip);
            try
            {
                SaveIndex();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup deleted {File} but could not commit its metadata removal", clip.FilePath);
                return CleanupDeletionOutcome.Failed(clip.FilePath, ex.Message, abortCleanup: true);
            }

            StorageCleanupFailure? thumbnailFailure = null;
            try
            {
                DeleteThumbnail(clip.ThumbnailPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Cleanup could not delete thumbnail {File}", clip.ThumbnailPath);
                thumbnailFailure = new StorageCleanupFailure(clip.ThumbnailPath!, ex.Message);
            }

            try
            {
                ClearDeletionIntent();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Cleanup committed deletion of {File} but could not clear its recovery intent", clip.FilePath);
                return new CleanupDeletionOutcome(
                    true,
                    fileSize,
                    new StorageCleanupFailure(DeletionIntentPath, ex.Message, true),
                    true);
            }

            return new CleanupDeletionOutcome(true, fileSize, thumbnailFailure, false);
        }
    }

    private static void RecordCleanupOutcome(
        CleanupDeletionOutcome outcome,
        List<StorageCleanupFailure> failures,
        ref int deletedCount,
        ref long deletedBytes)
    {
        if (outcome.Deleted)
        {
            deletedCount++;
            deletedBytes = checked(deletedBytes + outcome.DeletedBytes);
        }

        if (outcome.Failure is not null)
            failures.Add(outcome.Failure);
    }

    private long CalculateRemainingIndexedBytes() =>
        _clips.Where(clip => IsManagedPath(clip.FilePath)).Sum(clip => clip.FileSizeBytes);

    private static bool IsQuotaMet(long indexedBytes, long protectedExternalBytes, long maxBytes) =>
        protectedExternalBytes <= maxBytes && indexedBytes <= maxBytes - protectedExternalBytes;

    private void DeleteThumbnail(string? thumbnailPath)
    {
        if (thumbnailPath is null || !File.Exists(thumbnailPath)) return;
        if (!IsManagedPath(thumbnailPath))
        {
            _logger.LogWarning("Blocked thumbnail deletion outside base path: {Path}", thumbnailPath);
            return;
        }
        File.Delete(thumbnailPath);
    }

    private void PersistDeletionIntent(DeletionIntent intent)
    {
        var directory = Path.GetDirectoryName(DeletionIntentPath)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"storage-deletion.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(intent, JsonOptions));
            File.Move(temp, DeletionIntentPath, overwrite: false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private void ClearDeletionIntent()
    {
        if (File.Exists(DeletionIntentPath))
            File.Delete(DeletionIntentPath);
    }

    private void ReconcilePendingDeletion()
    {
        if (!File.Exists(DeletionIntentPath)) return;

        DeletionIntent intent;
        try
        {
            intent = JsonSerializer.Deserialize<DeletionIntent>(
                File.ReadAllText(DeletionIntentPath),
                JsonOptions) ?? throw new InvalidDataException("Storage deletion intent contained null.");
            if (string.IsNullOrWhiteSpace(intent.FilePath) ||
                !Path.IsPathFullyQualified(intent.FilePath) ||
                !IsManagedPath(intent.FilePath))
            {
                throw new InvalidDataException("Storage deletion intent contains an invalid media path.");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException(
                "A pending storage deletion could not be reconciled safely.",
                ex);
        }

        if (File.Exists(intent.FilePath))
        {
            // The process stopped before physical deletion. Retain both media and metadata.
            ClearDeletionIntent();
            return;
        }

        lock (_lock)
        {
            var clip = _clips.FirstOrDefault(item => PathsEqual(item.FilePath, intent.FilePath));
            if (clip is not null)
            {
                _clips.Remove(clip);
                SaveIndex();
            }
            DeleteThumbnail(intent.ThumbnailPath);
            ClearDeletionIntent();
        }
    }

    private long GetMaxStorageBytes()
    {
        if (_config.MaxStorageBytesOverride is < 0)
            throw new InvalidOperationException("Storage byte quota must not be negative.");
        if (_config.MaxStorageBytesOverride is long exactBytes)
            return exactBytes;
        if (_config.MaxStorageGb < 0)
            throw new InvalidOperationException("Storage quota must not be negative.");
        return checked((long)_config.MaxStorageGb * 1024 * 1024 * 1024);
    }

    private sealed record CleanupDeletionOutcome(
        bool Deleted,
        long DeletedBytes,
        StorageCleanupFailure? Failure,
        bool AbortCleanup)
    {
        public static CleanupDeletionOutcome Skipped { get; } = new(false, 0, null, false);

        public static CleanupDeletionOutcome Failed(
            string filePath,
            string message,
            bool abortCleanup = false) =>
            new(false, 0, new StorageCleanupFailure(filePath, message, abortCleanup), abortCleanup);
    }

    private sealed record DeletionIntent(string FilePath, string? ThumbnailPath);

    private string IndexPath => Path.Combine(StoragePath.Canonicalize(_config.BasePath), "clips-index.json");
    private string RebuildMarkerPath => Path.Combine(
        StoragePath.Canonicalize(_config.BasePath),
        "clips-index.rebuild-pending");
    private string DeletionIntentPath => Path.Combine(
        StoragePath.Canonicalize(_config.BasePath),
        "storage-deletion-pending.json");
    private bool IsManagedPath(string path)
    {
        var full = StoragePath.Canonicalize(path);
        var activeRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_config.BasePath)) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(activeRoot, StringComparison.OrdinalIgnoreCase)) return false;
        // A legacy folder inside the new root remains protected. An older parent
        // of the new root does not make newly recorded media read-only.
        return !_config.LegacyReadOnlyPaths.Any(root =>
        {
            var legacy = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return legacy.StartsWith(activeRoot, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(legacy, activeRoot, StringComparison.OrdinalIgnoreCase) &&
                full.StartsWith(legacy, StringComparison.OrdinalIgnoreCase);
        });
    }
    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            StoragePath.Canonicalize(left),
            StoragePath.Canonicalize(right),
            StringComparison.OrdinalIgnoreCase);
    private IEnumerable<string> MediaRoots => new[] { _config.BasePath }
        .Concat(_config.LegacyReadOnlyPaths).Distinct(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<string> EnumerateMedia(string root, string[] extensions)
    {
        try
        {
            return Directory.Exists(root)
                ? extensions.SelectMany(ext => Directory.EnumerateFiles(root, ext, SearchOption.AllDirectories)).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Recording folder is unavailable: {Folder}", root);
            return [];
        }
    }

    // Called under _lock. Repeated ingestion may add context, but must never erase
    // user edits or richer metadata merely because a second producer lacks it.
    private ClipMetadata Enrich(
        ClipMetadata existing,
        string? gameName,
        Highlights.HighlightType? highlightType,
        Guid? sessionId,
        Guid? captureRequestId,
        long? captureGeneration,
        int? matchNumber)
    {
        // A recovered clip's game was guessed from its folder. A capture receipt is
        // authoritative, and the clip held no favorite: favorites were frozen while the
        // library was unavailable, and choosing one clears RecoveredAt.
        if (existing.RecoveredAt is not null && captureRequestId is not null)
        {
            var claimed = existing with
            {
                GameName = gameName ?? existing.GameName,
                HighlightType = highlightType ?? existing.HighlightType,
                SessionId = sessionId ?? existing.SessionId,
                CaptureRequestId = captureRequestId,
                CaptureGeneration = captureGeneration ?? existing.CaptureGeneration,
                MatchNumber = matchNumber ?? existing.MatchNumber,
                RecoveredAt = null,
            };
            _clips[_clips.IndexOf(existing)] = claimed;
            SaveIndex();
            return claimed;
        }

        RequireCompatibleAttribution(
            existing,
            gameName,
            sessionId,
            captureRequestId,
            captureGeneration,
            matchNumber);
        var enriched = existing with
        {
            GameName = gameName ?? existing.GameName,
            HighlightType = highlightType ?? existing.HighlightType,
            SessionId = sessionId ?? existing.SessionId,
            CaptureRequestId = captureRequestId ?? existing.CaptureRequestId,
            CaptureGeneration = captureGeneration ?? existing.CaptureGeneration,
            MatchNumber = matchNumber ?? existing.MatchNumber,
        };
        if (enriched != existing)
        {
            _clips[_clips.IndexOf(existing)] = enriched;
            SaveIndex();
        }
        return enriched;
    }

    private static void RequireCompatibleAttribution(
        ClipMetadata existing,
        string? gameName,
        Guid? sessionId,
        Guid? captureRequestId,
        long? captureGeneration,
        int? matchNumber)
    {
        if (existing.GameName is not null && gameName is not null &&
            !string.Equals(existing.GameName, gameName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A clip path cannot be reassigned to another game.");
        if (existing.SessionId is not null && sessionId is not null && existing.SessionId != sessionId)
            throw new InvalidOperationException("A clip path cannot be reassigned to another session.");
        if (existing.CaptureRequestId is not null && captureRequestId is not null &&
            existing.CaptureRequestId != captureRequestId)
            throw new InvalidOperationException("A clip path cannot be reassigned to another capture request.");
        if (existing.CaptureGeneration is not null && captureGeneration is not null &&
            existing.CaptureGeneration != captureGeneration)
            throw new InvalidOperationException("A clip path cannot be reassigned to another capture generation.");
        if (existing.MatchNumber is not null && matchNumber is not null && existing.MatchNumber != matchNumber)
            throw new InvalidOperationException("A clip path cannot be reassigned to another match.");
    }

    private void LoadIndex(bool allowNewIndex)
    {
        var activeRoot = StoragePath.Canonicalize(_config.BasePath);
        foreach (var root in MediaRoots
            .Select(StoragePath.Canonicalize)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var primary = Path.Combine(root, "clips-index.json");
            if (string.Equals(root, activeRoot, StringComparison.OrdinalIgnoreCase))
                LoadActiveIndex(primary, allowNewIndex);
            else
                LoadLegacyIndex(primary);
        }
    }

    private void LoadActiveIndex(string primaryPath, bool allowNewIndex)
    {
        var primary = ReadIndexCandidate(primaryPath);
        var backup = ReadIndexCandidate(primaryPath + ".bak");
        var evidence = new List<string>(2);

        PreserveForRecovery(primary, evidence);
        PreserveForRecovery(backup, evidence);

        if (primary.State == IndexCandidateState.Valid)
        {
            if (backup.State == IndexCandidateState.Invalid)
            {
                try
                {
                    RepairIndexFromBytes(backup.Path, primary.Bytes!);
                }
                catch (Exception ex)
                {
                    throw new ActiveIndexUnavailableException(
                        "The clip index backup is damaged and could not be repaired.",
                        evidence,
                        canBuildNewIndex: false,
                        ex);
                }
            }

            AddLoadedClips(primary.Clips!);
            _primaryNeedsRecovery = false;
            _logger.LogInformation("Loaded metadata from {Index}", primary.Path);
            return;
        }

        if (backup.State == IndexCandidateState.Valid)
        {
            try
            {
                RepairIndexFromBytes(primaryPath, backup.Bytes!);
            }
            catch (Exception ex)
            {
                throw new ActiveIndexUnavailableException(
                    "The clip index is damaged and could not be repaired from its backup.",
                    evidence,
                    canBuildNewIndex: false,
                    ex);
            }

            AddLoadedClips(backup.Clips!);
            _primaryNeedsRecovery = false;
            _logger.LogWarning(
                "Recovered metadata from {Backup} and atomically repaired {Primary}",
                backup.Path,
                primaryPath);
            return;
        }

        if (primary.State == IndexCandidateState.Missing && backup.State == IndexCandidateState.Missing)
            return;

        if (!allowNewIndex)
        {
            throw new ActiveIndexUnavailableException(
                "The clip index and its backup are both damaged.",
                evidence,
                canBuildNewIndex: true,
                primary.Error ?? backup.Error);
        }

        WriteNewActiveIndex(primaryPath, evidence);
    }

    // Only reached when every damaged candidate was preserved. Both copies are rewritten so the
    // next start does not treat the old damaged backup as fresh corruption.
    private void WriteNewActiveIndex(string primaryPath, IReadOnlyList<string> evidence)
    {
        var emptyIndex = JsonSerializer.SerializeToUtf8Bytes(Array.Empty<ClipMetadata>(), JsonOptions);
        try
        {
            // The marker survives a crash between these writes or during the folder scan, so
            // the next start still offers to finish the rebuild.
            File.WriteAllText(RebuildMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
            RepairIndexFromBytes(primaryPath, emptyIndex);
            RepairIndexFromBytes(primaryPath + ".bak", emptyIndex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ActiveIndexUnavailableException(
                "A new clip index could not be written.",
                evidence,
                canBuildNewIndex: true,
                ex);
        }

        _primaryNeedsRecovery = false;
        _logger.LogWarning(
            "Started a new clip index at {Index}; the damaged index remains at {Evidence}",
            primaryPath,
            string.Join(", ", evidence));
    }

    private void PreserveForRecovery(IndexCandidate candidate, List<string> evidence)
    {
        if (candidate.State != IndexCandidateState.Invalid)
            return;
        try
        {
            evidence.Add(PreserveCorruptCandidate(candidate));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new ActiveIndexUnavailableException(
                "The clip index could not be read, or its damaged bytes could not be saved for recovery.",
                evidence,
                canBuildNewIndex: false,
                ex);
        }
    }

    private void LoadLegacyIndex(string primaryPath)
    {
        var primary = ReadIndexCandidate(primaryPath);
        var backup = ReadIndexCandidate(primaryPath + ".bak");
        var selected = primary.State == IndexCandidateState.Valid
            ? primary
            : backup.State == IndexCandidateState.Valid
                ? backup
                : null;
        if (selected is not null)
        {
            AddLoadedClips(selected.Clips!);
            _logger.LogInformation("Loaded legacy metadata from {Index}", selected.Path);
            return;
        }

        if (primary.State == IndexCandidateState.Invalid || backup.State == IndexCandidateState.Invalid)
        {
            _logger.LogWarning(
                primary.Error ?? backup.Error,
                "Legacy clip index under {Root} is corrupt; its bytes were left untouched",
                Path.GetDirectoryName(primaryPath));
        }
    }

    private IndexCandidate ReadIndexCandidate(string path)
    {
        byte[]? bytes = null;
        try
        {
            bytes = File.ReadAllBytes(path);
            var deserialized = JsonSerializer.Deserialize<List<ClipMetadata?>>(bytes, JsonOptions)
                ?? throw new JsonException("Clip index contains null instead of a list.");
            var clips = ValidateIndexSnapshot(deserialized, path);
            return IndexCandidate.Valid(path, bytes, clips);
        }
        catch (FileNotFoundException)
        {
            return IndexCandidate.Missing(path);
        }
        catch (DirectoryNotFoundException)
        {
            return IndexCandidate.Missing(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            JsonException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            _logger.LogWarning(ex, "Clip index candidate {Index} is invalid", path);
            return IndexCandidate.Invalid(path, bytes, ex);
        }
    }

    private static IReadOnlyList<ClipMetadata> ValidateIndexSnapshot(
        IReadOnlyList<ClipMetadata?> clips,
        string sourcePath)
    {
        var normalized = new List<ClipMetadata>(clips.Count);
        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < clips.Count; index++)
        {
            var clip = clips[index]
                ?? throw new InvalidDataException($"Clip index {sourcePath} contains a null entry at position {index}.");
            if (string.IsNullOrWhiteSpace(clip.FilePath) || !Path.IsPathFullyQualified(clip.FilePath))
                throw new InvalidDataException($"Clip index {sourcePath} contains an invalid media path at position {index}.");
            if (string.IsNullOrWhiteSpace(clip.FileName))
                throw new InvalidDataException($"Clip index {sourcePath} contains an invalid file name at position {index}.");
            if (clip.CreatedAt == default || clip.Duration < TimeSpan.Zero || clip.FileSizeBytes < 0)
                throw new InvalidDataException($"Clip index {sourcePath} contains invalid metadata at position {index}.");
            if (clip.Tags is null || clip.Tags.Any(tag => tag is null))
                throw new InvalidDataException($"Clip index {sourcePath} contains invalid tags at position {index}.");
            if (clip.CaptureGeneration is < 0)
                throw new InvalidDataException($"Clip index {sourcePath} contains an invalid capture generation at position {index}.");

            var canonicalPath = StoragePath.Canonicalize(clip.FilePath);
            if (!knownPaths.Add(canonicalPath))
                throw new InvalidDataException($"Clip index {sourcePath} contains duplicate equivalent media paths.");
            normalized.Add(clip with { FilePath = canonicalPath });
        }

        return normalized;
    }

    private void AddLoadedClips(IReadOnlyList<ClipMetadata> clips)
    {
        lock (_lock)
        {
            var known = _clips
                .Select(clip => StoragePath.Canonicalize(clip.FilePath))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            // An unplugged drive is not evidence that a clip was deleted. Retain its
            // metadata until an explicit library operation removes it.
            _clips.AddRange(clips.Where(clip => known.Add(StoragePath.Canonicalize(clip.FilePath))));
        }
    }

    private string PreserveCorruptCandidate(IndexCandidate candidate)
    {
        if (candidate.Bytes is null)
        {
            throw new InvalidDataException(
                $"The corrupt clip index {candidate.Path} could not be read for recovery preservation.",
                candidate.Error);
        }

        // A restart or retry on the same damaged index reuses its evidence instead of adding
        // another index-sized copy on every launch.
        var preserved = FindPreservedEvidence(candidate.Path, candidate.Bytes);
        if (preserved is not null)
            return preserved;

        var evidencePath = $"{candidate.Path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        using var evidence = new FileStream(
            evidencePath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        evidence.Write(candidate.Bytes);
        evidence.Flush(flushToDisk: true);
        _logger.LogWarning("Preserved corrupt clip index bytes at {Evidence}", evidencePath);
        return evidencePath;
    }

    // Evidence is only read here, never changed.
    private static string? FindPreservedEvidence(string indexPath, byte[] bytes)
    {
        var directory = Path.GetDirectoryName(indexPath);
        if (directory is null || !Directory.Exists(directory)) return null;

        var prefix = Path.GetFileName(indexPath) + ".corrupt-";
        foreach (var path in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            if (!Path.GetFileName(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                if (new FileInfo(path).Length == bytes.Length &&
                    File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                {
                    return path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable evidence is never reused; a fresh copy is written instead.
            }
        }

        return null;
    }

    private static void RepairIndexFromBytes(string indexPath, byte[] validBytes)
    {
        var directory = Path.GetDirectoryName(indexPath)
            ?? throw new InvalidDataException("The active clip index has no parent directory.");
        var temporaryPath = Path.Combine(directory, $"clips-index.recovery-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var temporary = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                temporary.Write(validBytes);
                temporary.Flush(flushToDisk: true);
            }

            if (File.Exists(indexPath))
                File.Replace(temporaryPath, indexPath, destinationBackupFileName: null);
            else
                File.Move(temporaryPath, indexPath, overwrite: false);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
                // The repair failure remains primary; a unique temporary file is safe
                // recovery evidence and must not hide that failure.
            }
        }
    }

    private void SaveIndex()
    {
        // Serialize snapshot creation AND replacement. Otherwise an older snapshot
        // can finish writing after a newer one and silently discard indexed clips.
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(IndexPath)!;
            var tmp = Path.Combine(dir, $"clips-index.{Guid.NewGuid():N}.tmp");
            try
            {
                _ = ValidateIndexSnapshot(_clips.Cast<ClipMetadata?>().ToArray(), IndexPath);
                Directory.CreateDirectory(dir);
                File.WriteAllText(tmp, JsonSerializer.Serialize(_clips, JsonOptions));
                if (File.Exists(IndexPath) && !_primaryNeedsRecovery)
                    File.Replace(tmp, IndexPath, IndexPath + ".bak");
                else
                    File.Move(tmp, IndexPath, overwrite: true);
                _primaryNeedsRecovery = false;
                _persistedClips = [.. _clips];
            }
            catch (Exception ex)
            {
                _clips.Clear();
                _clips.AddRange(_persistedClips);
                _logger.LogError(ex, "Failed to save clip index; media files were retained");
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
                throw;
            }
        }
    }

    private sealed class ActiveIndexUnavailableException(
        string message,
        IReadOnlyList<string> evidencePaths,
        bool canBuildNewIndex,
        Exception? innerException) : Exception(message, innerException)
    {
        public IReadOnlyList<string> EvidencePaths { get; } = [.. evidencePaths];
        public bool CanBuildNewIndex { get; } = canBuildNewIndex;
    }

    private enum IndexCandidateState
    {
        Missing,
        Valid,
        Invalid,
    }

    private sealed record IndexCandidate(
        string Path,
        IndexCandidateState State,
        byte[]? Bytes,
        IReadOnlyList<ClipMetadata>? Clips,
        Exception? Error)
    {
        public static IndexCandidate Missing(string path) =>
            new(path, IndexCandidateState.Missing, null, null, null);

        public static IndexCandidate Valid(
            string path,
            byte[] bytes,
            IReadOnlyList<ClipMetadata> clips) =>
            new(path, IndexCandidateState.Valid, bytes, clips, null);

        public static IndexCandidate Invalid(string path, byte[]? bytes, Exception error) =>
            new(path, IndexCandidateState.Invalid, bytes, null, error);
    }
}
