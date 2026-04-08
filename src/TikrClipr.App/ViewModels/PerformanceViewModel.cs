using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using TikrClipr.Core.Performance;

namespace TikrClipr.App.ViewModels;

public partial class PerformanceViewModel : ObservableObject, IDisposable
{
    private readonly IPerformanceMonitor _monitor;

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
        Application.Current?.Dispatcher.Invoke(() =>
        {
            FpsDisplay = snap.RenderFps > 0 ? $"{snap.RenderFps:F1}" : "N/A";
            DroppedDisplay = snap.TotalFrames > 0
                ? $"{snap.DroppedFrames} ({snap.DropRate:F1}%)"
                : "N/A";
            CpuDisplay = $"{snap.CpuUsage:F1}%";
            RamDisplay = $"{snap.MemoryUsageMb} MB";
        });
    }

    public void Dispose()
    {
        _monitor.SnapshotUpdated -= OnSnapshot;
        _monitor.Stop();
    }
}
