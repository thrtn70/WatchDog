namespace WatchDog.Core.Storage;

public sealed record GameStorageUsage(
    string GameName,
    int ClipCount,
    long TotalBytes,
    DateTimeOffset? OldestClip,
    DateTimeOffset? NewestClip)
{
    public double TotalMb => TotalBytes / (1024.0 * 1024.0);
    public double TotalGb => TotalBytes / (1024.0 * 1024.0 * 1024.0);
}

public sealed record StorageReport
{
    public required IReadOnlyList<GameStorageUsage> ByGame { get; init; }
    public long TotalBytes { get; init; }
    public int TotalClips { get; init; }
    public int MaxStorageGb { get; init; }

    public double TotalGb => TotalBytes / (1024.0 * 1024.0 * 1024.0);
    public double UsagePercent => MaxStorageGb > 0 ? TotalGb / MaxStorageGb * 100 : 0;
    public double RemainingGb => Math.Max(0, MaxStorageGb - TotalGb);
}

public static class StorageAnalytics
{
    public static StorageReport Analyze(IClipStorage clipStorage, StorageConfig config)
    {
        var clips = clipStorage.GetAllClips();

        var byGame = clips
            .GroupBy(c => c.GameName ?? "Unknown")
            .Select(g => new GameStorageUsage(
                GameName: g.Key,
                ClipCount: g.Count(),
                TotalBytes: g.Sum(c => c.FileSizeBytes),
                OldestClip: g.Min(c => c.CreatedAt),
                NewestClip: g.Max(c => c.CreatedAt)))
            .OrderByDescending(g => g.TotalBytes)
            .ToList();

        return new StorageReport
        {
            ByGame = byGame,
            TotalBytes = byGame.Sum(g => g.TotalBytes),
            TotalClips = clips.Count,
            MaxStorageGb = config.MaxStorageGb,
        };
    }
}
