namespace WatchDog.Core.Performance;

public sealed record PerformanceSnapshot
{
    public double RenderFps { get; init; }
    public double EncodeFps { get; init; }
    public int DroppedFrames { get; init; }
    public int TotalFrames { get; init; }
    public double CpuUsage { get; init; }
    public long MemoryUsageMb { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public double DropRate => TotalFrames > 0
        ? (double)DroppedFrames / TotalFrames * 100
        : 0;

    public string Summary =>
        $"{RenderFps:F1} fps | {DroppedFrames} dropped ({DropRate:F1}%) | CPU {CpuUsage:F0}% | RAM {MemoryUsageMb}MB";
}
