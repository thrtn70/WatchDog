using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using WatchDog.Core.Performance;

namespace WatchDog.App.ViewModels;

public partial class PerformanceViewModel : ObservableObject, IDisposable
{
    private readonly IPerformanceMonitor _monitor;
    private volatile bool _disposed;

    [ObservableProperty] private string _fpsDisplay = "N/A";
    [ObservableProperty] private string _droppedDisplay = "N/A";
    [ObservableProperty] private string _cpuDisplay = "—";
    [ObservableProperty] private string _ramDisplay = "—";

    public PerformanceViewModel(IPerformanceMonitor monitor)
    {
        _monitor = monitor;
        _monitor.SnapshotUpdated += OnSnapshot;
        _monitor.Start();
    }

    private void OnSnapshot(PerformanceSnapshot snap)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() =>
        {
            FpsDisplay = snap.RenderFps > 0 ? $"{snap.RenderFps:F1}" : "N/A";
            DroppedDisplay = snap.TotalFrames > 0
                ? $"{snap.DroppedFrames} ({snap.DropRate:F1}%)"
                : "N/A";
            CpuDisplay = $"{snap.CpuUsage:F1}%";
            RamDisplay = $"{snap.MemoryUsageMb} MB";
        }));
    }

    private void PostToUi(Action action)
    {
        if (_disposed) return;
        try { action(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"Performance: {ex.Message}"); }
    }

    public void Dispose()
    {
        _disposed = true;
        _monitor.SnapshotUpdated -= OnSnapshot;
        _monitor.Stop();
    }
}
