using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ObsKit.NET;

namespace TikrClipr.Core.Performance;

public sealed class ObsPerformanceMonitor : IPerformanceMonitor
{
    private readonly ILogger<ObsPerformanceMonitor> _logger;
    private readonly object _lock = new();
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private Timer? _pollTimer;
    private bool _disposed;

    public PerformanceSnapshot? Latest { get; private set; }
    public bool IsMonitoring { get { lock (_lock) return _pollTimer is not null; } }

    public event Action<PerformanceSnapshot>? SnapshotUpdated;

    public ObsPerformanceMonitor(ILogger<ObsPerformanceMonitor> logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        lock (_lock)
        {
            if (_pollTimer is not null) return;

            _pollTimer = new Timer(_ => Poll(), null,
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        }
        _logger.LogInformation("Performance monitor started (1s interval)");
    }

    public void Stop()
    {
        lock (_lock)
        {
            _pollTimer?.Dispose();
            _pollTimer = null;
        }
        _logger.LogInformation("Performance monitor stopped");
    }

    private void Poll()
    {
        try
        {
            _currentProcess.Refresh();
            var snapshot = new PerformanceSnapshot
            {
                RenderFps = Obs.ActiveFps,
                EncodeFps = Obs.ActiveFps,
                DroppedFrames = (int)Obs.LaggedFrames,
                TotalFrames = (int)Obs.TotalFrames,
                CpuUsage = Obs.CurrentCpuUsage,
                MemoryUsageMb = _currentProcess.WorkingSet64 / (1024 * 1024),
                Timestamp = DateTimeOffset.UtcNow,
            };

            Latest = snapshot;
            SnapshotUpdated?.Invoke(snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to poll performance metrics");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _currentProcess.Dispose();
    }
}
