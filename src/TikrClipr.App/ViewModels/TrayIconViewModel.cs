using System.IO;
using System.Windows;
using System.Windows.Input;
using Application = System.Windows.Application;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using TikrClipr.Core.Capture;
using TikrClipr.Core.Events;

namespace TikrClipr.App.ViewModels;

public partial class TrayIconViewModel : ObservableObject, IDisposable
{
    private readonly ICaptureEngine _captureEngine;
    private readonly IEventBus _eventBus;
    private readonly IDisposable _clipSavedSub;
    private readonly IDisposable _stateChangedSub;

    [ObservableProperty]
    private string _statusText = "TikrClipr - Idle";

    [ObservableProperty]
    private string _iconSource = "/Resources/Icons/tray-idle.ico";

    [ObservableProperty]
    private bool _isRecording;

    public TrayIconViewModel(ICaptureEngine captureEngine, IEventBus eventBus)
    {
        _captureEngine = captureEngine;
        _eventBus = eventBus;

        _clipSavedSub = eventBus.Subscribe<ClipSavedEvent>(OnClipSaved);
        _stateChangedSub = eventBus.Subscribe<BufferStateChangedEvent>(OnStateChanged);

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

    public void Dispose()
    {
        _clipSavedSub.Dispose();
        _stateChangedSub.Dispose();
    }
}
