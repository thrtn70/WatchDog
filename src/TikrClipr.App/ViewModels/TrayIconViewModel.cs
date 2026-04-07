using System.IO;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using TikrClipr.Core.Capture;
using TikrClipr.Core.Events;
using TikrClipr.Core.Recording;

namespace TikrClipr.App.ViewModels;

public partial class TrayIconViewModel : ObservableObject, IDisposable
{
    private readonly ICaptureEngine _captureEngine;
    private readonly IEventBus _eventBus;
    private readonly IDisposable _clipSavedSub;
    private readonly IDisposable _stateChangedSub;
    private readonly IDisposable _sessionStartedSub;
    private readonly IDisposable _sessionStoppedSub;

    [ObservableProperty]
    private string _statusText = "TikrClipr - Idle";

    [ObservableProperty]
    private string _iconSource = "/Resources/Icons/tray-idle.ico";

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _isSessionRecording;

    public TrayIconViewModel(ICaptureEngine captureEngine, IEventBus eventBus)
    {
        _captureEngine = captureEngine;
        _eventBus = eventBus;

        _clipSavedSub = eventBus.Subscribe<ClipSavedEvent>(OnClipSaved);
        _stateChangedSub = eventBus.Subscribe<BufferStateChangedEvent>(OnStateChanged);
        _sessionStartedSub = eventBus.Subscribe<SessionRecordingStartedEvent>(OnSessionStarted);
        _sessionStoppedSub = eventBus.Subscribe<SessionRecordingStoppedEvent>(OnSessionStopped);

        captureEngine.StateChanged += state =>
            Application.Current?.Dispatcher.Invoke(() => UpdateState(state));
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
        switch (state)
        {
            case CaptureState.Idle:
                StatusText = "TikrClipr - Idle";
                IsRecording = false;
                break;
            case CaptureState.Initializing:
                StatusText = "TikrClipr - Starting...";
                IsRecording = false;
                break;
            case CaptureState.Buffering:
                if (_captureEngine.IsDesktopCapture)
                    StatusText = "TikrClipr - Desktop Capture";
                else if (_captureEngine.CurrentGame is { } g)
                    StatusText = $"TikrClipr - Recording {g.DisplayName}";
                else
                    StatusText = "TikrClipr - Buffering";
                IsRecording = true;
                break;
            case CaptureState.Saving:
                StatusText = "TikrClipr - Saving clip...";
                IsRecording = true;
                break;
            case CaptureState.Stopping:
                StatusText = "TikrClipr - Stopping...";
                IsRecording = false;
                break;
        }
    }

    private void OnClipSaved(ClipSavedEvent e)
    {
        var fileName = Path.GetFileName(e.FilePath);
        var gameName = e.Game?.DisplayName ?? "Unknown";
        Application.Current?.Dispatcher.Invoke(() =>
        {
            StatusText = $"TikrClipr - Clip saved: {fileName}";
        });
    }

    private void OnStateChanged(BufferStateChangedEvent e)
    {
        Application.Current?.Dispatcher.Invoke(() => UpdateState(e.NewState));
    }

    private void OnSessionStarted(SessionRecordingStartedEvent e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsSessionRecording = true;
            var game = e.Game?.DisplayName ?? "Desktop";
            StatusText = $"TikrClipr - Recording session: {game}";
        });
    }

    private void OnSessionStopped(SessionRecordingStoppedEvent e)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            IsSessionRecording = false;
            UpdateState(_captureEngine.State);
        });
    }

    public void Dispose()
    {
        _clipSavedSub.Dispose();
        _stateChangedSub.Dispose();
        _sessionStartedSub.Dispose();
        _sessionStoppedSub.Dispose();
    }
}
