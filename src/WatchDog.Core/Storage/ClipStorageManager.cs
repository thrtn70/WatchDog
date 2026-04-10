using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using WatchDog.Core.ClipEditor;

namespace WatchDog.Core.Storage;

public sealed class ClipStorageManager : IClipStorage
{
    private readonly StorageConfig _config;
    private readonly IClipEditor _clipEditor;
    private readonly ILogger<ClipStorageManager> _logger;
    private readonly List<ClipMetadata> _clips = [];
    private readonly object _lock = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public ClipStorageManager(StorageConfig config, IClipEditor clipEditor, ILogger<ClipStorageManager> logger)
    {
        _config = config;
        _clipEditor = clipEditor;
        _logger = logger;

        Directory.CreateDirectory(config.BasePath);
        LoadIndex();
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
    {
        // Deduplicate: if this file is already indexed, return existing metadata
        lock (_lock)
        {
            var existing = _clips.FirstOrDefault(c =>
                string.Equals(c.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
                return existing;
        }

        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("Clip file not found", filePath);

        var duration = await _clipEditor.GetDurationAsync(filePath, ct);

        // Generate thumbnail at 1 second in (or start if shorter)
        var thumbTs = duration.TotalSeconds > 1 ? TimeSpan.FromSeconds(1) : TimeSpan.Zero;
        string? thumbPath = null;
        try
        {
            thumbPath = await _clipEditor.GenerateThumbnailAsync(filePath, thumbTs, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate thumbnail for {File}", filePath);
        }

        var metadata = new ClipMetadata
        {
            FilePath = filePath,
            FileName = fileInfo.Name,
            GameName = gameName,
            CreatedAt = fileInfo.CreationTimeUtc,
            Duration = duration,
            FileSizeBytes = fileInfo.Length,
            ThumbnailPath = thumbPath,
            HighlightType = highlightType,
            SessionId = sessionId,
            MatchNumber = matchNumber,
        };

        lock (_lock)
        {
            // Second dedup check under lock — guards against concurrent IndexClipAsync calls
            // that both passed the first check before either added the clip
            var duplicate = _clips.FirstOrDefault(c =>
                string.Equals(c.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (duplicate is not null)
                return duplicate;

            _clips.Add(metadata);
        }

        SaveIndex();
        var highlightTag = highlightType is not null ? $" [{highlightType}]" : "";
        _logger.LogInformation("Indexed clip: {File} ({Duration:mm\\:ss}, {Size:F1}MB){Tag}",
            metadata.FileName, duration, fileInfo.Length / (1024.0 * 1024.0), highlightTag);

        return metadata;
    }

    public async Task<int> ScanAndIndexAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_config.BasePath))
            return 0;

        HashSet<string> indexed;
        lock (_lock)
            indexed = _clips.Select(c => c.FilePath).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var newFiles = Directory.EnumerateFiles(_config.BasePath, "*.mp4", SearchOption.AllDirectories)
            .Where(f => !indexed.Contains(f))
            .ToList();

        var count = 0;
        foreach (var file in newFiles)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                // Infer game name from parent folder
                var parentDir = Path.GetFileName(Path.GetDirectoryName(file));
                var gameName = string.Equals(parentDir, "WatchDog", StringComparison.OrdinalIgnoreCase)
                    ? null : parentDir;

                await IndexClipAsync(file, gameName, ct);
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

    public void DeleteClip(string filePath)
    {
        string? thumbnailPath;
        lock (_lock)
        {
            var clip = _clips.FirstOrDefault(c => c.FilePath == filePath);
            if (clip is null) return;

            thumbnailPath = clip.ThumbnailPath;
            _clips.Remove(clip);
        }

        // Persist index first (atomic write), then best-effort delete files
        SaveIndex();

        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
            if (thumbnailPath is not null && File.Exists(thumbnailPath))
                File.Delete(thumbnailPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete clip file(s) for {File}", filePath);
        }

        _logger.LogInformation("Deleted clip: {File}", filePath);
    }

    public void ToggleFavorite(string filePath)
    {
        lock (_lock)
        {
            var idx = _clips.FindIndex(c => c.FilePath == filePath);
            if (idx < 0) return;

            _clips[idx] = _clips[idx] with { IsFavorite = !_clips[idx].IsFavorite };
        }

        SaveIndex();
    }

    public async Task RunCleanupAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var maxAge = TimeSpan.FromDays(_config.AutoDeleteDays);
        var minAge = TimeSpan.FromHours(24); // Never delete clips less than 24 hours old
        var maxBytes = (long)_config.MaxStorageGb * 1024 * 1024 * 1024;

        List<ClipMetadata> toDelete;
        lock (_lock)
        {
            // Delete clips older than max age (unless favorited or too new)
            toDelete = _clips
                .Where(c => !c.IsFavorite
                    && now - c.CreatedAt > maxAge
                    && now - c.CreatedAt > minAge)
                .ToList();
        }

        foreach (var clip in toDelete)
            DeleteClip(clip.FilePath);

        if (toDelete.Count > 0)
            _logger.LogInformation("Cleanup: deleted {Count} clips older than {Days} days", toDelete.Count, _config.AutoDeleteDays);

        // Check total size and delete oldest non-favorite clips if over limit
        List<ClipMetadata> overBudget;
        lock (_lock)
        {
            var totalSize = _clips.Sum(c => c.FileSizeBytes);
            if (totalSize <= maxBytes)
            {
                overBudget = [];
            }
            else
            {
                var running = totalSize;
                overBudget = _clips
                    .Where(c => !c.IsFavorite && now - c.CreatedAt > minAge)
                    .OrderBy(c => c.CreatedAt)
                    .TakeWhile(c =>
                    {
                        if (running <= maxBytes) return false;
                        running -= c.FileSizeBytes;
                        return true;
                    })
                    .ToList();
            }
        }

        foreach (var clip in overBudget)
            DeleteClip(clip.FilePath);

        await Task.CompletedTask;
    }

    private string IndexPath => Path.Combine(_config.BasePath, "clips-index.json");

    private void LoadIndex()
    {
        try
        {
            if (!File.Exists(IndexPath))
                return;

            var json = File.ReadAllText(IndexPath);
            var clips = JsonSerializer.Deserialize<List<ClipMetadata>>(json, JsonOptions);

            if (clips is not null)
            {
                // Only include clips whose files still exist
                lock (_lock)
                {
                    _clips.AddRange(clips.Where(c => File.Exists(c.FilePath)));
                }
            }

            _logger.LogInformation("Loaded {Count} clips from index", _clips.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load clip index, starting fresh");
        }
    }

    private void SaveIndex()
    {
        var dir = Path.GetDirectoryName(IndexPath)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $"clips-index.{Guid.NewGuid():N}.tmp");

        try
        {
            List<ClipMetadata> snapshot;
            lock (_lock)
                snapshot = [.. _clips];

            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(tmp, json);
            File.Move(tmp, IndexPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save clip index");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        }
    }
}
