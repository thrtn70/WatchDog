namespace TikrClipr.Core.Performance;

public interface IPerformanceMonitor : IDisposable
{
    PerformanceSnapshot? Latest { get; }
    bool IsMonitoring { get; }

    void Start();
    void Stop();

    event Action<PerformanceSnapshot>? SnapshotUpdated;
}
