using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.Logging;
using WatchDog.Core.ClipEditor;

namespace WatchDog.App.Services;

/// <summary>
/// Lazily generates and caches hover-scrub thumbnail strips for clip cards.
/// Strips are generated on first hover and kept in memory as frozen BitmapImages.
/// Uses CancellationToken.None for shared tasks so dedup works across callers.
/// </summary>
public sealed class ThumbnailStripCache
{
    private const int FrameCount = 10;
    private const int FrameWidth = 210;
    private const int MaxCachedClips = 30;

    private readonly IClipEditor _editor;
    private readonly ILogger<ThumbnailStripCache> _logger;
    private readonly ConcurrentDictionary<string, BitmapImage[]> _cache = new();
    private readonly ConcurrentDictionary<string, Task<BitmapImage[]?>> _pending = new();
    private readonly ConcurrentQueue<string> _lruOrder = new();

    public ThumbnailStripCache(IClipEditor editor, ILogger<ThumbnailStripCache> logger)
    {
        _editor = editor;
        _logger = logger;
    }

    public BitmapImage[]? TryGetCached(string clipPath)
    {
        _cache.TryGetValue(clipPath, out var frames);
        return frames;
    }

    public async Task<BitmapImage[]?> GetOrGenerateAsync(string clipPath, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(clipPath, out var cached))
            return cached;

        // Shared task uses CancellationToken.None so it survives individual hover cancellations.
        // Each caller applies their own ct via WaitAsync.
        var task = _pending.GetOrAdd(clipPath, path => GenerateAndCacheAsync(path));

        try
        {
            return await task.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            // Only remove from pending if the task has completed
            if (task.IsCompleted)
                _pending.TryRemove(clipPath, out _);
        }
    }

    private async Task<BitmapImage[]?> GenerateAndCacheAsync(string clipPath)
    {
        try
        {
            if (!File.Exists(clipPath))
                return null;

            var paths = await _editor.GenerateThumbnailStripAsync(clipPath, FrameCount, FrameWidth);
            if (paths.Count == 0)
                return null;

            var frames = new BitmapImage[paths.Count];
            for (var i = 0; i < paths.Count; i++)
            {
                if (!File.Exists(paths[i]))
                    continue;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(paths[i]);
                bmp.DecodePixelWidth = FrameWidth;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                frames[i] = bmp;
            }

            // Evict oldest entries if cache is full
            _lruOrder.Enqueue(clipPath);
            while (_cache.Count >= MaxCachedClips && _lruOrder.TryDequeue(out var oldest))
                _cache.TryRemove(oldest, out _);

            _cache[clipPath] = frames;
            return frames;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate hover strip for {Path}", clipPath);
            return null;
        }
    }
}
