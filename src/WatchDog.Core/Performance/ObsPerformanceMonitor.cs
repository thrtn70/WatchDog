using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Performance;

public sealed class ObsPerformanceMonitor : IPerformanceMonitor
{
    private readonly ILogger<ObsPerformanceMonitor> _logger;
    private readonly object _lock = new();
    private readonly Process _currentProcess = Process.GetCurrentProcess();
    private Timer? _pollTimer;
    private TimeSpan _prevCpuTime;
    private DateTime _prevTimestamp;
    private bool _disposed;

    public PerformanceSnapshot? Latest { get; private set; }
    public bool IsMonitoring { get { lock (_lock) return _pollTimer is not null; } }

    public event Action<PerformanceSnapshot>? SnapshotUpdated;

    public ObsPerformanceMonitor(ILogger<ObsPerformanceMonitor> logger)
    {
        _logger = logger;
        _prevCpuTime = _currentProcess.TotalProcessorTime;
        _prevTimestamp = DateTime.UtcNow;
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

            // Calculate CPU usage as delta over interval
            var now = DateTime.UtcNow;
            var cpuTime = _currentProcess.TotalProcessorTime;
            var elapsed = (now - _prevTimestamp).TotalMilliseconds;
            var cpuDelta = (cpuTime - _prevCpuTime).TotalMilliseconds;
            var cpuPercent = elapsed > 0
                ? cpuDelta / (Environment.ProcessorCount * elapsed) * 100
                : 0;

            _prevCpuTime = cpuTime;
            _prevTimestamp = now;

            var snapshot = new PerformanceSnapshot
            {
                RenderFps = 0,       // OBS FPS not exposed via ObsKit.NET
                EncodeFps = 0,
                DroppedFrames = 0,   // OBS dropped frames not exposed
                TotalFrames = 0,
                CpuUsage = cpuPercent,
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
