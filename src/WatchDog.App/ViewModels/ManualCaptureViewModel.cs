using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WatchDog.App.Services;
using WatchDog.Core.Capture;
using WatchDog.Native.Win32;

namespace WatchDog.App.ViewModels;

/// <summary>
/// ViewModel for the manual window capture picker.
/// Presents a list of visible windows and lets the user start/stop manual capture.
/// </summary>
public partial class ManualCaptureViewModel : ObservableObject, IDisposable
{
    private readonly CaptureSourceManager _sourceManager;
    private readonly SynchronizationContext? _uiContext;
    private ManualWindowCaptureState _captureState = ManualWindowCaptureState.Initial;
    private bool _disposed;

    [ObservableProperty]
    private ObservableCollection<WindowEnumerator.WindowInfo> _windowList = [];

    [ObservableProperty]
    private bool _isManualCaptureActive;

    [ObservableProperty]
    private string _activeCaptureTitle = string.Empty;

    [ObservableProperty]
    private string _ownedCaptureTitle = string.Empty;

    [ObservableProperty]
    private string _statusText = "No selected window is confirmed active; manual reservation ownership is unknown.";

    [ObservableProperty]
    private bool _canRequestStop;

    [ObservableProperty]
    private bool _canSelectWindow = true;

    public ManualCaptureViewModel(CaptureSourceManager sourceManager)
    {
        _sourceManager = sourceManager;
        _uiContext = SynchronizationContext.Current;
        _sourceManager.SnapshotChanged += OnSnapshotChanged;
        RefreshStatus();
    }

    public void RefreshStatus()
    {
        if (_disposed) return;
        var projection = ManualWindowCapturePolicy.Project(
            _captureState, _sourceManager.LifecycleSnapshot, _sourceManager.IsAdmissionClosed);
        IsManualCaptureActive = projection.IsManualCaptureActive;
        CanRequestStop = projection.CanRequestStop;
        CanSelectWindow = projection.CanSelectWindow;
        ActiveCaptureTitle = projection.ActiveCaptureTitle;
        OwnedCaptureTitle = projection.OwnedCaptureTitle;
        StatusText = projection.StatusText;
    }

    private void OnSnapshotChanged(CaptureLifecycleSnapshot _)
    {
        if (_disposed) return;
        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
            _uiContext.Post(static state => ((ManualCaptureViewModel)state!).RefreshStatus(), this);
        else
            RefreshStatus();
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
        if (window is null || _disposed) return;
        RefreshStatus();
        if (!CanSelectWindow) return;

        CaptureRequest request;
        try
        {
            var target = new WindowCaptureTarget(window.ProcessId, window.Handle,
                window.Title, window.ExecutableName, window.ClassName);
            request = _sourceManager.CreateManualWindowRequest(target);
            _captureState = ManualWindowCapturePolicy.BeginSelection(_captureState, request);
        }
        catch (Exception)
        {
            if (_disposed) return;
            _captureState = _captureState with
            {
                LastFailure = new CaptureFailure(CaptureFailureKind.Unexpected,
                    "capture.manual.selection-invalid", "The selected window or capture settings are invalid."),
            };
            RefreshStatus();
            return;
        }
        var revision = _captureState.Revision;
        RefreshStatus();
        try
        {
            var result = await _sourceManager.StartManualCaptureAsync(request);
            if (_disposed) return;
            _captureState = ManualWindowCapturePolicy.CompleteStart(
                _captureState, revision, request, result, _sourceManager.LifecycleSnapshot);
            RefreshStatus();
        }
        catch (Exception)
        {
            if (_disposed || _captureState.Revision != revision) return;
            _captureState = _captureState with
            {
                IsPending = false,
                LastFailure = new CaptureFailure(CaptureFailureKind.Unexpected,
                    "capture.manual.start-unexpected", "The selected window could not be started."),
            };
            RefreshStatus();
        }
    }

    /// <summary>Stops the current manual capture session.</summary>
    [RelayCommand]
    private async Task StopCaptureAsync()
    {
        if (_disposed) return;
        RefreshStatus();
        var expected = _captureState.OwnedContext;
        if (!CanRequestStop || expected is null) return;
        _captureState = ManualWindowCapturePolicy.BeginStop(_captureState);
        var revision = _captureState.Revision;
        RefreshStatus();
        try
        {
            var result = await _sourceManager.StopManualCaptureAsync(expected);
            if (_disposed) return;
            _captureState = ManualWindowCapturePolicy.CompleteStop(
                _captureState, revision, expected, result, _sourceManager.LifecycleSnapshot);
            RefreshStatus();
        }
        catch (Exception)
        {
            if (_disposed || _captureState.Revision != revision) return;
            _captureState = _captureState with
            {
                IsPending = false,
                LastFailure = new CaptureFailure(CaptureFailureKind.Unexpected,
                    "capture.manual.stop-unexpected", "Stop did not complete; check the current capture status."),
            };
            RefreshStatus();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sourceManager.SnapshotChanged -= OnSnapshotChanged;
    }
}
