using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using WatchDog.Core.Capture;
using WatchDog.Core.Events;

namespace WatchDog.App.ViewModels;

public partial class StatusOverlayViewModel : ObservableObject, IDisposable
{
    private readonly ICaptureEngine _captureEngine;
    private readonly Action<CaptureState> _stateHandler;
    private readonly IDisposable _gameDetectedSub;
    private readonly IDisposable _gameExitedSub;

    [ObservableProperty]
    private string _statusLine = "IDLE · No game";

    [ObservableProperty]
    private System.Windows.Media.Brush _indicatorBrush =
        new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#4A5F6B"));

    public StatusOverlayViewModel(ICaptureEngine captureEngine, IEventBus eventBus)
    {
        _captureEngine = captureEngine;

        _stateHandler = state =>
            Application.Current?.Dispatcher.Invoke(() => UpdateStatus(state));
        captureEngine.StateChanged += _stateHandler;

        _gameDetectedSub = eventBus.Subscribe<GameDetectedEvent>(e =>
            Application.Current?.Dispatcher.Invoke(() => UpdateStatus(_captureEngine.State)));
        _gameExitedSub = eventBus.Subscribe<GameExitedEvent>(e =>
            Application.Current?.Dispatcher.Invoke(() => UpdateStatus(_captureEngine.State)));
    }

    private void UpdateStatus(CaptureState state)
    {
        var gameName = _captureEngine.CurrentGame?.DisplayName ?? "Desktop";
        var isDesktop = _captureEngine.IsDesktopCapture;

        switch (state)
        {
            case CaptureState.Idle:
                StatusLine = "IDLE · No game";
                IndicatorBrush = MakeBrush("#4A5F6B");
                break;
            case CaptureState.Buffering:
                var highlightText = isDesktop ? "No highlights" : "Highlights \u2713";
                StatusLine = $"BUF · {gameName} · {highlightText}";
                IndicatorBrush = MakeBrush("#38BF7F");
                break;
            case CaptureState.Saving:
                StatusLine = $"SAVE · {gameName}";
                IndicatorBrush = MakeBrush("#D9B84C");
                break;
            case CaptureState.Initializing:
                StatusLine = "INIT...";
                IndicatorBrush = MakeBrush("#4C96D9");
                break;
            case CaptureState.Stopping:
                StatusLine = "STOP...";
                IndicatorBrush = MakeBrush("#4A5F6B");
                break;
        }
    }

    private static System.Windows.Media.SolidColorBrush MakeBrush(string hex)
    {
        var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex);
        var brush = new System.Windows.Media.SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Dispose()
    {
        _captureEngine.StateChanged -= _stateHandler;
        _gameDetectedSub.Dispose();
        _gameExitedSub.Dispose();
    }
}
