using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WatchDog.App.Services;
using WatchDog.Native.Win32;

namespace WatchDog.App.ViewModels;

/// <summary>
/// ViewModel for the manual window capture picker.
/// Presents a list of visible windows and lets the user start/stop manual capture.
/// </summary>
public partial class ManualCaptureViewModel : ObservableObject
{
    private readonly CaptureSourceManager _sourceManager;

    [ObservableProperty]
    private ObservableCollection<WindowEnumerator.WindowInfo> _windowList = [];

    [ObservableProperty]
    private bool _isManualCaptureActive;

    [ObservableProperty]
    private string _activeCaptureTitle = string.Empty;

    public ManualCaptureViewModel(CaptureSourceManager sourceManager)
    {
        _sourceManager = sourceManager;
    }

    /// <summary>Refreshes the list of visible windows. Call when the picker is opened.</summary>
    [RelayCommand]
    private void RefreshWindows()
    {
        var windows = WindowEnumerator.GetVisibleWindows();
        WindowList = new ObservableCollection<WindowEnumerator.WindowInfo>(windows);
    }

    /// <summary>Starts manual capture for the selected window.</summary>
    [RelayCommand]
    private async Task SelectWindowAsync(WindowEnumerator.WindowInfo? window)
    {
        if (window is null) return;

        await _sourceManager.StartManualCaptureAsync(
            window.ExecutableName,
            window.Title,
            window.ProcessId,
            window.Handle,
            window.ClassName);

        // Derive state from the service — don't assume success
        IsManualCaptureActive = _sourceManager.IsManualCaptureActive;
        ActiveCaptureTitle = _sourceManager.IsManualCaptureActive ? window.Title : string.Empty;
    }

    /// <summary>Stops the current manual capture session.</summary>
    [RelayCommand]
    private async Task StopCaptureAsync()
    {
        await _sourceManager.StopManualCaptureAsync();
        IsManualCaptureActive = false;
        ActiveCaptureTitle = string.Empty;
    }
}
