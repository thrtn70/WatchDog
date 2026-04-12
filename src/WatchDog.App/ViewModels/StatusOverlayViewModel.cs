using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using WatchDog.Core.Capture;
using WatchDog.Core.Events;
using WatchDog.Core.Storage;

namespace WatchDog.App.ViewModels;

/// <summary>
/// ViewModel for the in-game status overlay.
/// Supports two display modes: compact (dot + status text) and
/// expanded (game name + clip count + status label).
/// Right-click toggles between modes.
/// </summary>
public partial class StatusOverlayViewModel : ObservableObject, IDisposable
{
    private readonly ICaptureEngine _captureEngine;
    private readonly IClipStorage _clipStorage;
    private readonly Action<CaptureState> _stateHandler;
    private readonly IDisposable _gameDetectedSub;
    private readonly IDisposable _gameExitedSub;
    private readonly IDisposable _clipSavedSub;
    private volatile bool _disposed;

    private static readonly System.Windows.Media.SolidColorBrush IdleBrush = MakeBrush("#4A5F6B");
    private static readonly System.Windows.Media.SolidColorBrush BufferingBrush = MakeBrush("#38BF7F");
    private static readonly System.Windows.Media.SolidColorBrush SavingBrush = MakeBrush("#D9B84C");
    private static readonly System.Windows.Media.SolidColorBrush InitBrush = MakeBrush("#4C96D9");

    // Compact mode (minimal dot + short status)
    [ObservableProperty]
    private string _statusLine = "IDLE · No game";

    // Expanded mode (game name + clip count + status label)
    [ObservableProperty]
    private string _gameName = "No game";

    [ObservableProperty]
    private string _clipCountText = "0 clips";

    [ObservableProperty]
    private string _statusLabel = "Idle";

    [ObservableProperty]
    private System.Windows.Media.Brush _statusLabelBrush = IdleBrush;

    // Shared
    [ObservableProperty]
    private System.Windows.Media.Brush _indicatorBrush = IdleBrush;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isCompactMode = true;

    public bool IsExpandedMode => !IsCompactMode;

    partial void OnIsCompactModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsExpandedMode));
    }

    public StatusOverlayViewModel(ICaptureEngine captureEngine, IEventBus eventBus, IClipStorage clipStorage)
    {
        _captureEngine = captureEngine;
        _clipStorage = clipStorage;

        _stateHandler = state =>
            Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() => UpdateStatus(state)));
        captureEngine.StateChanged += _stateHandler;

        _gameDetectedSub = eventBus.Subscribe<GameDetectedEvent>(e =>
            Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() => UpdateStatus(_captureEngine.State))));
        _gameExitedSub = eventBus.Subscribe<GameExitedEvent>(e =>
            Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() => UpdateStatus(_captureEngine.State))));
        _clipSavedSub = eventBus.Subscribe<ClipSavedEvent>(e =>
            Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(UpdateClipCount)));
    }

    /// <summary>Toggle between compact and expanded overlay modes.</summary>
    public void ToggleMode()
    {
        IsCompactMode = !IsCompactMode;
    }

    private void UpdateStatus(CaptureState state)
    {
        var gameName = _captureEngine.CurrentGame?.DisplayName;
        var isDesktop = _captureEngine.IsDesktopCapture;
        var displayName = gameName ?? (isDesktop ? "Desktop" : "No game");

        GameName = displayName;
        UpdateClipCount();

        switch (state)
        {
            case CaptureState.Idle:
                StatusLine = "IDLE · No game";
                GameName = "No game";
                StatusLabel = "Idle";
                IndicatorBrush = IdleBrush;
                StatusLabelBrush = IdleBrush;
                IsRecording = false;
                break;
            case CaptureState.Buffering:
                var highlightText = isDesktop ? "No highlights" : "Highlights \u2713";
                StatusLine = $"BUF · {displayName} · {highlightText}";
                StatusLabel = "Buffering";
                IndicatorBrush = BufferingBrush;
                StatusLabelBrush = BufferingBrush;
                IsRecording = true;
                break;
            case CaptureState.Saving:
                StatusLine = $"SAVE · {gameName}";
                StatusLabel = "Saving...";
                IndicatorBrush = SavingBrush;
                StatusLabelBrush = SavingBrush;
                IsRecording = true;
                break;
            case CaptureState.Initializing:
                StatusLine = "INIT...";
                StatusLabel = "Starting";
                IndicatorBrush = InitBrush;
                StatusLabelBrush = InitBrush;
                IsRecording = false;
                break;
            case CaptureState.Stopping:
                StatusLine = "STOP...";
                StatusLabel = "Stopping";
                IndicatorBrush = IdleBrush;
                StatusLabelBrush = IdleBrush;
                IsRecording = false;
                break;
        }
    }

    private void UpdateClipCount()
    {
        var gameName = _captureEngine.CurrentGame?.DisplayName;
        if (gameName is null)
        {
            ClipCountText = "";
            return;
        }
        var count = _clipStorage.GetClipsByGame(gameName).Count;
        ClipCountText = count == 1 ? "1 clip" : $"{count} clips";
    }

    private void PostToUi(Action action)
    {
        if (_disposed) return;
        try { action(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"StatusOverlay: {ex.Message}"); }
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
        _disposed = true;
        _captureEngine.StateChanged -= _stateHandler;
        _gameDetectedSub.Dispose();
        _gameExitedSub.Dispose();
        _clipSavedSub.Dispose();
    }
}
