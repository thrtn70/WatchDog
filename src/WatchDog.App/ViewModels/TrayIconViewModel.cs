using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using WatchDog.Core.Capture;
using WatchDog.Core.Events;
using WatchDog.Core.Recording;

namespace WatchDog.App.ViewModels;

public partial class TrayIconViewModel : ObservableObject, IDisposable
{
    private readonly ICaptureEngine _captureEngine;
    private readonly IEventBus _eventBus;
    private Controls.ClipSavedToast? _activeToast;
    private readonly IDisposable _clipSavedSub;
    private readonly IDisposable _sessionStartedSub;
    private readonly IDisposable _sessionStoppedSub;
    private readonly IDisposable _gameDetectedSub;
    private readonly IDisposable _gameExitedSub;
    private readonly Action<CaptureState> _captureStateHandler;
    private System.Threading.Timer? _savingRevertTimer;
    private volatile bool _disposed;

    // Icon file paths (set by App.xaml.cs after TrayIconGenerator runs)
    public string IdleIconPath { get; set; } = "/Resources/Icons/tray-idle.ico";
    public string BufferingIconPath { get; set; } = "/Resources/Icons/tray-recording.ico";
    public string RecordingIconPath { get; set; } = "/Resources/Icons/tray-recording.ico";
    public string SavingIconPath { get; set; } = "/Resources/Icons/tray-idle.ico";

    [ObservableProperty]
    private string _statusText = "WatchDog - Idle";

    [ObservableProperty]
    private string _iconSource = "/Resources/Icons/tray-idle.ico";

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isSessionRecording;

    /// <summary>Game name + highlight status for the tray context menu info row.</summary>
    [ObservableProperty]
    private string _currentGameText = "No game detected";

    /// <summary>Buffer status text for the tray context menu info row.</summary>
    [ObservableProperty]
    private string _bufferStatusText = "Replay Buffer OFF";

    public TrayIconViewModel(ICaptureEngine captureEngine, IEventBus eventBus)
    {
        _captureEngine = captureEngine;
        _eventBus = eventBus;

        _clipSavedSub = eventBus.Subscribe<ClipSavedEvent>(OnClipSaved);
        _sessionStartedSub = eventBus.Subscribe<SessionRecordingStartedEvent>(OnSessionStarted);
        _sessionStoppedSub = eventBus.Subscribe<SessionRecordingStoppedEvent>(OnSessionStopped);
        _gameDetectedSub = eventBus.Subscribe<GameDetectedEvent>(OnGameDetected);
        _gameExitedSub = eventBus.Subscribe<GameExitedEvent>(OnGameExited);

        // Use only the direct StateChanged delegate for capture state updates.
        // Do NOT also subscribe to BufferStateChangedEvent — both fire on every
        // state change, causing UpdateState to run twice per transition.
        _captureStateHandler = state =>
            Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() => UpdateState(state)));
        captureEngine.StateChanged += _captureStateHandler;
    }

    [RelayCommand]
    private void ShowMainWindow()
    {
        var mainWindow = Application.Current.MainWindow;
        if (mainWindow is null)
        {
            mainWindow = new MainWindow();
            Application.Current.MainWindow = mainWindow;
        }

        mainWindow.Show();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    [RelayCommand]
    private void ShowSettings()
    {
        var settingsWindow = new Views.SettingsWindow();
        settingsWindow.ShowDialog();
    }

    [RelayCommand]
    private async Task SaveClipAsync()
    {
        if (_captureEngine.State == CaptureState.Buffering)
            await _captureEngine.SaveReplayAsync();
    }

    [RelayCommand]
    private void ExitApplication()
    {
        Application.Current.Shutdown();
    }

    private void UpdateState(CaptureState state)
    {
        // Cancel any pending saving→previous icon revert
        _savingRevertTimer?.Dispose();
        _savingRevertTimer = null;

        switch (state)
        {
            case CaptureState.Idle:
                StatusText = "WatchDog - Idle";
                IconSource = IdleIconPath;
                BufferStatusText = "Replay Buffer OFF";
                IsRecording = false;
                break;
            case CaptureState.Initializing:
                StatusText = "WatchDog - Starting...";
                IsRecording = false;
                break;
            case CaptureState.Buffering:
                if (_captureEngine.IsDesktopCapture)
                {
                    StatusText = "WatchDog - Desktop Capture";
                    CurrentGameText = "No game detected";
                }
                else if (_captureEngine.CurrentGame is { } g)
                {
                    StatusText = $"WatchDog - Recording {g.DisplayName}";
                    CurrentGameText = g.DisplayName;
                }
                else
                {
                    StatusText = "WatchDog - Buffering";
                    CurrentGameText = "No game detected";
                }
                IconSource = BufferingIconPath;
                BufferStatusText = "Replay Buffer ON";
                IsRecording = true;
                break;
            case CaptureState.Saving:
                StatusText = "WatchDog - Saving clip...";
                IconSource = SavingIconPath;
                IsRecording = true;
                // Revert to buffering icon after 3 seconds
                _savingRevertTimer = new System.Threading.Timer(_ =>
                {
                    Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() =>
                    {
                        if (_captureEngine.State == CaptureState.Buffering)
                            IconSource = BufferingIconPath;
                    }));
                }, null, 3000, Timeout.Infinite);
                break;
            case CaptureState.Stopping:
                StatusText = "WatchDog - Stopping...";
                IsRecording = false;
                break;
        }
    }

    private void OnGameDetected(GameDetectedEvent e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() =>
        {
            CurrentGameText = $"{e.Game.DisplayName}";
        }));
    }

    private void OnGameExited(GameExitedEvent e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() =>
        {
            CurrentGameText = "No game detected";
        }));
    }

    private void OnClipSaved(ClipSavedEvent e)
    {
        var fileName = Path.GetFileName(e.FilePath);
        var gameName = e.Game?.DisplayName;
        Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() =>
        {
            StatusText = $"WatchDog - Clip saved: {fileName}";

            try
            {
                _activeToast?.Close();
                var toast = new Controls.ClipSavedToast(fileName, gameName);
                _activeToast = toast;
                toast.Closed += (_, _) => { if (_activeToast == toast) _activeToast = null; };
                toast.Show();
            }
            catch
            {
                // Toast is non-critical — swallow errors
            }
        }));
    }



    private void OnSessionStarted(SessionRecordingStartedEvent e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() =>
        {
            IsSessionRecording = true;
            var game = e.Game?.DisplayName ?? "Desktop";
            StatusText = $"WatchDog - Recording session: {game}";
        }));
    }

    private void OnSessionStopped(SessionRecordingStoppedEvent e)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() =>
        {
            IsSessionRecording = false;
            UpdateState(_captureEngine.State);
        }));
    }

    private void PostToUi(Action action)
    {
        if (_disposed) return;
        try { action(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"TrayIcon: {ex.Message}"); }
    }

    public void Dispose()
    {
        _captureEngine.StateChanged -= _captureStateHandler;
        _clipSavedSub.Dispose();
        _sessionStartedSub.Dispose();
        _sessionStoppedSub.Dispose();
        _gameDetectedSub.Dispose();
        _gameExitedSub.Dispose();
        _disposed = true;
        _savingRevertTimer?.Dispose();
    }
}
