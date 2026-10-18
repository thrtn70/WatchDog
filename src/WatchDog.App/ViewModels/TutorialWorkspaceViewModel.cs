using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WatchDog.App.Services;
using WatchDog.Core.Capture;
using WatchDog.Core.Tutorial;
using WatchDog.Core.Webcam;
using WatchDog.Native.Win32;

namespace WatchDog.App.ViewModels;

/// <summary>A read-only seam for showing the current lifecycle without admitting commands.</summary>
public interface ITutorialLifecycleObservation
{
    CaptureLifecycleSnapshot Snapshot { get; }
    event Action<CaptureLifecycleSnapshot>? SnapshotChanged;
}

public sealed class TutorialLifecycleObservation(CaptureLifecycleCoordinator coordinator) : ITutorialLifecycleObservation
{
    public CaptureLifecycleSnapshot Snapshot => coordinator.Snapshot;
    public event Action<CaptureLifecycleSnapshot>? SnapshotChanged
    {
        add => coordinator.SnapshotChanged += value;
        remove => coordinator.SnapshotChanged -= value;
    }
}

/// <summary>
/// Tutorial selection and scoped command presentation. The optional capture adapter owns
/// only this workspace's confirmed request and exact cleanup context.
/// </summary>
public partial class TutorialWorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly CaptureStartupBaselineResult? _startupBaseline;
    private readonly ITutorialLifecycleObservation _observation;
    private readonly Func<WindowEnumerator.WindowInfo, bool>? _isWindowCurrent;
    private readonly TutorialWorkspaceCapture? _capture;
    private readonly Func<DisplayCaptureTarget, bool>? _isDisplayCurrent;
    private readonly TutorialRegionSelection? _regionSelection;
    private readonly SynchronizationContext? _uiContext;
    private IReadOnlyList<WindowEnumerator.WindowInfo> _offeredWindows = [];
    private IReadOnlyList<DisplayCaptureOption> _offeredDisplays = [];
    private IReadOnlyList<WebcamDeviceInfo> _offeredCameras = [];
    private WindowEnumerator.WindowInfo? _selectedWindow;
    private DisplayCaptureOption? _selectedDisplay;
    private WebcamDeviceInfo? _selectedCamera;
    private bool _includeCamera;
    private string? _cameraFailure;
    private string? _displayFailure;
    private string? _regionFailure;
    private string _regionXText = string.Empty;
    private string _regionYText = string.Empty;
    private string _regionWidthText = string.Empty;
    private string _regionHeightText = string.Empty;
    private TutorialRecordingPlan? _plan;
    private string? _preflightFailure;
    private string _statusText = string.Empty;
    private string _boundaryText = string.Empty;
    private string _audioText = string.Empty;
    private string _exactWindowText = string.Empty;
    private string _outputText = string.Empty;
    private string? _operationFailure;
    private long _offerRevision;
    private long _selectionRevision;
    private long _displayOfferRevision;
    private long _displaySelectionRevision;
    private long _regionRevision;
    private object? _sourceConfirmationAttempt;
    private long? _confirmedRegionRevision;
    private long? _confirmedRegionOfferRevision;
    private DisplayCaptureOption? _confirmedRegionOwner;
    private long _cameraOfferRevision;
    private long _cameraSelectionRevision;
    private bool _isCloseInFlight;
    private int _capturePublicationDepth;
    private bool _disposed;

    public TutorialWorkspaceViewModel(
        CaptureStartupBaselineResult? startupBaseline,
        ITutorialLifecycleObservation observation,
        Func<WindowEnumerator.WindowInfo, bool>? isWindowCurrent = null,
        TutorialWorkspaceCapture? capture = null,
        Func<DisplayCaptureTarget, bool>? isDisplayCurrent = null,
        TutorialRegionSelection? regionSelection = null)
    {
        _startupBaseline = startupBaseline;
        _observation = observation ?? throw new ArgumentNullException(nameof(observation));
        _isWindowCurrent = isWindowCurrent;
        _capture = capture;
        _isDisplayCurrent = isDisplayCurrent;
        _regionSelection = regionSelection;
        _uiContext = SynchronizationContext.Current;
        StartCommand = new AsyncRelayCommand(StartAsync, () => StartAvailable);
        StopCommand = new AsyncRelayCommand(StopAsync, () => StopAvailable);
        CancelCommand = new RelayCommand(Cancel, () => CancelAvailable);
        _observation.SnapshotChanged += OnSnapshotChanged;
        if (_capture is not null)
            _capture.StateChanged += OnCaptureStateChanged;
        RefreshPresentation();
    }

    public IReadOnlyList<WindowEnumerator.WindowInfo> WindowOptions => _offeredWindows;
    public WindowEnumerator.WindowInfo? SelectedWindow
    {
        get => _selectedWindow;
        set
        {
            if (ReferenceEquals(_selectedWindow, value) || !CanEditPlan) return;
            _selectedWindow = value;
            _selectionRevision = _offerRevision;
            // A newly highlighted row is only a candidate. Never keep a confirmed plan
            // for a different (or cleared/stale) row beside it.
            DiscardDraft();
            ClearDisplaySelection();
            ClearCamera(clearOptions: true, disable: true);
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirmWindow));
            RefreshPresentation();
        }
    }
    public bool CanConfirmWindow => CanEditPlan && _selectedWindow is not null &&
        _selectionRevision == _offerRevision && _offeredWindows.Any(window => ReferenceEquals(window, _selectedWindow));
    public IReadOnlyList<DisplayCaptureOption> DisplayOptions => _offeredDisplays;
    public DisplayCaptureOption? SelectedDisplay
    {
        get => _selectedDisplay;
        set
        {
            if (ReferenceEquals(_selectedDisplay, value) || !CanEditPlan) return;
            _selectedDisplay = value;
            _displaySelectionRevision = _displayOfferRevision;
            DiscardDraft();
            ClearWindowSelection();
            ClearRegionDraft(clearText: true);
            ClearCamera(clearOptions: true, disable: true);
            _displayFailure = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirmDisplay));
            RefreshPresentation();
        }
    }
    public bool CanConfirmDisplay => CanEditPlan && _selectedDisplay is not null &&
        _displaySelectionRevision == _displayOfferRevision &&
        _offeredDisplays.Any(display => ReferenceEquals(display, _selectedDisplay));
    public string RegionXText
    {
        get => _regionXText;
        set => SetRegionText(ref _regionXText, value ?? string.Empty, nameof(RegionXText));
    }
    public string RegionYText
    {
        get => _regionYText;
        set => SetRegionText(ref _regionYText, value ?? string.Empty, nameof(RegionYText));
    }
    public string RegionWidthText
    {
        get => _regionWidthText;
        set => SetRegionText(ref _regionWidthText, value ?? string.Empty, nameof(RegionWidthText));
    }
    public string RegionHeightText
    {
        get => _regionHeightText;
        set => SetRegionText(ref _regionHeightText, value ?? string.Empty, nameof(RegionHeightText));
    }
    public bool CanConfirmRegion => CanEditPlan && _regionSelection is not null && CanConfirmDisplay &&
        !string.IsNullOrWhiteSpace(_regionXText) && !string.IsNullOrWhiteSpace(_regionYText) &&
        !string.IsNullOrWhiteSpace(_regionWidthText) && !string.IsNullOrWhiteSpace(_regionHeightText);
    public IReadOnlyList<WebcamDeviceInfo> CameraOptions => _offeredCameras;
    public WebcamDeviceInfo? SelectedCamera
    {
        get => _selectedCamera;
        set
        {
            if (ReferenceEquals(_selectedCamera, value) || !CanUseCameraOptions) return;
            RemoveConfirmedCameraFromPlan();
            _selectedCamera = value;
            _cameraSelectionRevision = _cameraOfferRevision;
            _cameraFailure = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirmCamera));
            RefreshPresentation();
        }
    }
    public bool IncludeCamera
    {
        get => _includeCamera;
        set
        {
            if (_includeCamera == value || !CanEditPlan || (value && !CanConfigureCamera)) return;
            _includeCamera = value;
            if (!value)
            {
                RemoveConfirmedCameraFromPlan();
                _selectedCamera = null;
                _offeredCameras = [];
                _cameraFailure = null;
                OnPropertyChanged(nameof(SelectedCamera));
                OnPropertyChanged(nameof(CameraOptions));
            }
            else
            {
                RevokePublishedPlan();
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanConfirmCamera));
            OnPropertyChanged(nameof(CanUseCameraOptions));
            RefreshPresentation();
        }
    }
    public bool CanConfirmCamera => CanEditPlan && _includeCamera && _selectedCamera is not null &&
        _cameraSelectionRevision == _cameraOfferRevision &&
        _offeredCameras.Any(camera => ReferenceEquals(camera, _selectedCamera)) &&
        _plan?.Request.Target is WindowCaptureTarget && CanConfirmWindow;
    public bool CanConfigureCamera => CanEditPlan && _plan?.Request.Target is WindowCaptureTarget;
    public bool CanUseCameraOptions => CanConfigureCamera && _includeCamera;
    public TutorialRecordingPlan? Plan => _plan;
    public TutorialRecordingPlan? ActivePlan => _capture?.ActivePlan;
    public string StatusText => _statusText;
    public string BoundaryText => _boundaryText;
    public string ExactWindowText => _exactWindowText;
    public string AudioText => _audioText;
    public string OutputText => _outputText;
    public string CameraText
    {
        get
        {
            var active = _capture?.ActivePlan;
            if ((active ?? _plan)?.Request.Webcam is { } webcam)
                return $"Exact camera selected: {webcam.DisplayName}. Requested {webcam.Width} × {webcam.Height} at {webcam.FramesPerSecond:g} fps; native format and capture are unverified.";
            if (active is not null) return "Camera off for the submitted recording.";
            return _cameraFailure is not null
                ? $"Camera unavailable: {_cameraFailure}"
                : _includeCamera
                    ? "Camera not included. Choose a current physical camera and confirm it for this plan. A saved preference is not consent."
                    : "Camera off. A saved preference is not consent; open the camera list explicitly to choose one.";
        }
    }
    public string DisplayAvailabilityText => _capture?.ActivePlan is not null
        ? "The submitted recording still owns the source shown under Selected boundary. Any new selection is a draft until that recording and its cleanup finish."
        : _displayFailure ?? (_selectedDisplay is { } display
            ? $"Display choice: {display.Target.DisplayName ?? "Display"} ({display.DeviceName}), {display.Width} × {display.Height}{(display.IsPrimary ? ", primary" : string.Empty)}. This choice includes the entire display."
            : "Display selection is unavailable until you refresh the list, choose one exact display, and confirm it. No other display or desktop is substituted.");
    public string RegionAvailabilityText
    {
        get
        {
            var presentedPlan = _capture?.ActivePlan ?? _plan;
            if (presentedPlan?.Request.Target is RegionCaptureTarget region)
                return $"Confirmed region: {region.PhysicalBounds.Width} × {region.PhysicalBounds.Height} physical pixels at global ({region.PhysicalBounds.X}, {region.PhysicalBounds.Y}) on display {region.OwningDisplayId}. The entire frozen rectangle is the selected source.";
            if (_regionSelection is null)
                return "Region selection is unavailable in this workspace.";
            return _regionFailure ??
                "Enter global physical X and Y coordinates plus a positive width and height. The full rectangle must remain inside the selected display.";
        }
    }
    public bool StartAvailable => !_disposed && !_isCloseInFlight && _plan is not null &&
        _capture?.StartAvailable == true;
    public bool StopAvailable => !_disposed && !_isCloseInFlight && _capture?.StopAvailable == true;
    public bool CancelAvailable => !_disposed && !_isCloseInFlight && _capture?.CancelAvailable == true;
    public bool CanEditPlan => !_disposed && !_isCloseInFlight && _capture?.RetainedCleanupContext is null;
    public bool CloseAvailable => !_disposed && !_isCloseInFlight;
    public string StartReason => _capture?.StartReason ??
        TutorialWorkspaceProjection.Create(_plan, _observation.Snapshot).Start.Reason;
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand StopCommand { get; }
    public RelayCommand CancelCommand { get; }

    /// <summary>Call only after the user explicitly opens or refreshes the metadata list.</summary>
    public void OfferWindows(IReadOnlyList<WindowEnumerator.WindowInfo> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (!CanEditPlan) return;
        if (windows.Any(window => window is null || window.ProcessId <= 0 ||
                window.Handle == IntPtr.Zero || string.IsNullOrWhiteSpace(window.Title) ||
                string.IsNullOrWhiteSpace(window.ExecutableName)) ||
            windows.GroupBy(window => (window.ProcessId, window.Handle)).Any(group => group.Count() != 1))
        {
            ReportWindowListUnavailable();
            throw new InvalidDataException("The window catalog contains malformed or duplicate identities.");
        }
        if (_offerRevision == long.MaxValue)
            throw new InvalidOperationException("The window selection revision is exhausted.");
        _offerRevision++;
        DiscardDraft();
        ClearDisplaySelection();
        ClearCamera(clearOptions: true, disable: true);
        _offeredWindows = new ReadOnlyCollection<WindowEnumerator.WindowInfo>(windows.ToArray());
        SelectedWindow = null;
        OnPropertyChanged(nameof(WindowOptions));
        OnPropertyChanged(nameof(CanConfirmWindow));
        RefreshPresentation();
    }

    /// <summary>Accept display metadata only after the user explicitly opens or refreshes the list.</summary>
    public void RefreshDisplays(Func<IReadOnlyList<DisplayCaptureOption>> enumerateDisplays)
    {
        ArgumentNullException.ThrowIfNull(enumerateDisplays);
        if (!CanEditPlan) return;
        // Refresh is accepted now. Revoke before an external enumeration can let a
        // pending Start finish and make the later OfferDisplays edit guard reject it.
        OfferDisplays([]);
        try { OfferDisplays(enumerateDisplays()); }
        catch { ReportDisplayListUnavailable(); }
    }

    /// <summary>Replace an offered snapshot; use RefreshDisplays to revoke before enumeration.</summary>
    public void OfferDisplays(IReadOnlyList<DisplayCaptureOption> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        if (!CanEditPlan) return;
        if (_displayOfferRevision == long.MaxValue)
            throw new InvalidOperationException("The display selection revision is exhausted.");

        _displayOfferRevision++;
        DiscardDraft();
        ClearWindowSelection();
        ClearCamera(clearOptions: true, disable: true);
        _selectedDisplay = null;
        _offeredDisplays = [];
        _displayFailure = null;
        ClearRegionDraft(clearText: true);
        OnPropertyChanged(nameof(SelectedDisplay));
        OnPropertyChanged(nameof(DisplayOptions));
        OnPropertyChanged(nameof(CanConfirmDisplay));

        try
        {
            _offeredDisplays = DisplayCaptureCatalog.ValidateOptions(displays);
        }
        catch (InvalidDataException)
        {
            _displayFailure = "The display list could not be verified. Refresh and choose again; no display was selected.";
            RefreshPresentation();
            throw;
        }
        OnPropertyChanged(nameof(DisplayOptions));
        RefreshPresentation();
    }

    public void ReportDisplayListUnavailable()
    {
        if (!CanEditPlan) return;
        if (_displayOfferRevision == long.MaxValue)
            throw new InvalidOperationException("The display selection revision is exhausted.");
        _displayOfferRevision++;
        DiscardDraft();
        ClearWindowSelection();
        ClearCamera(clearOptions: true, disable: true);
        _selectedDisplay = null;
        _offeredDisplays = [];
        _displayFailure = "The display list is unavailable. Try Refresh; no display was selected.";
        ClearRegionDraft(clearText: true);
        OnPropertyChanged(nameof(SelectedDisplay));
        OnPropertyChanged(nameof(DisplayOptions));
        OnPropertyChanged(nameof(CanConfirmDisplay));
        RefreshPresentation();
    }

    /// <summary>Accept camera metadata only after the user explicitly opens or refreshes the list.</summary>
    public void OfferCameras(IReadOnlyList<WebcamDeviceInfo> cameras)
    {
        ArgumentNullException.ThrowIfNull(cameras);
        if (!CanUseCameraOptions) return;
        if (_cameraOfferRevision == long.MaxValue)
            throw new InvalidOperationException("The camera selection revision is exhausted.");
        _cameraOfferRevision++;
        RemoveConfirmedCameraFromPlan();
        _selectedCamera = null;
        _offeredCameras = [];
        _cameraFailure = null;
        OnPropertyChanged(nameof(SelectedCamera));
        OnPropertyChanged(nameof(CameraOptions));
        OnPropertyChanged(nameof(CanConfirmCamera));
        RefreshPresentation();

        _offeredCameras = WebcamDeviceCatalog.Validate(cameras);
        OnPropertyChanged(nameof(CameraOptions));
        RefreshPresentation();
    }

    public void ReportCameraListUnavailable()
    {
        if (!CanUseCameraOptions) return;
        if (_cameraOfferRevision == long.MaxValue)
            throw new InvalidOperationException("The camera selection revision is exhausted.");
        _cameraOfferRevision++;
        RemoveConfirmedCameraFromPlan();
        _selectedCamera = null;
        _offeredCameras = [];
        _cameraFailure = "The physical camera list could not be read. Try Refresh; no camera is included.";
        OnPropertyChanged(nameof(SelectedCamera));
        OnPropertyChanged(nameof(CameraOptions));
        OnPropertyChanged(nameof(CanConfirmCamera));
        RefreshPresentation();
    }

    public void ReportWindowListUnavailable()
    {
        if (!CanEditPlan) return;
        OfferWindows([]);
        _preflightFailure = "The window list is unavailable. Try Refresh; no source was selected.";
        RefreshPresentation();
    }

    [RelayCommand]
    private void ConfirmWindow()
    {
        if (!CanEditPlan) return;
        var attempt = _sourceConfirmationAttempt = new object();
        if (!CanConfirmWindow)
        {
            DiscardDraft("A stale window selection was ignored. Choose a current window before planning.");
            RefreshPresentation();
            return;
        }
        var window = _selectedWindow!;
        var offerRevision = _offerRevision;
        var selectionRevision = _selectionRevision;
        bool IsCurrent() => CanEditPlan && ReferenceEquals(_sourceConfirmationAttempt, attempt) &&
            ReferenceEquals(_selectedWindow, window) && _offerRevision == offerRevision &&
            _selectionRevision == selectionRevision;
        if (_isWindowCurrent is not null)
        {
            try
            {
                var current = _isWindowCurrent(window);
                if (!IsCurrent()) return;
                if (!current)
                {
                    DiscardDraft("The exact window is unavailable. Refresh and choose a current window; no desktop fallback was selected.");
                    ClearCamera(clearOptions: true, disable: true);
                    RefreshPresentation();
                    return;
                }
            }
            catch
            {
                if (!IsCurrent()) return;
                DiscardDraft("The exact window could not be revalidated. Refresh before planning; no source was selected.");
                ClearCamera(clearOptions: true, disable: true);
                RefreshPresentation();
                return;
            }
        }
        if (_startupBaseline is null)
        {
            DiscardDraft("Startup capture settings are unavailable; a tutorial plan cannot be prepared.");
            RefreshPresentation();
            return;
        }

        try
        {
            ClearDisplaySelection();
            if (!IsCurrent()) return;
            ClearCamera(clearOptions: true, disable: true);
            if (!IsCurrent()) return;
            var target = new WindowCaptureTarget(window.ProcessId, window.Handle,
                window.Title, window.ExecutableName, window.ClassName);
            var result = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UtcNow,
                target, _startupBaseline);
            if (!ReplaceDraftIfCurrent(result.Plan, result.Failure?.Message, IsCurrent)) return;
            _displayFailure = null;
            if (result.Plan is not null)
                PublishDraft(result.Plan);
        }
        catch (Exception)
        {
            if (!IsCurrent()) return;
            DiscardDraft("Startup capture settings could not be used. Correct Settings and restart WatchDog before planning.");
        }
        RefreshPresentation();
    }

    [RelayCommand]
    private void ConfirmDisplay()
    {
        if (!CanEditPlan) return;
        var attempt = _sourceConfirmationAttempt = new object();
        if (!CanConfirmDisplay)
        {
            DiscardDraft();
            _displayFailure = "A stale display selection was ignored. Refresh and choose a current display before planning.";
            RefreshPresentation();
            return;
        }

        var option = _selectedDisplay!;
        var offerRevision = _displayOfferRevision;
        var selectionRevision = _displaySelectionRevision;
        var regionRevision = _regionRevision;
        bool IsCurrent() => CanEditPlan && ReferenceEquals(_sourceConfirmationAttempt, attempt) &&
            ReferenceEquals(_selectedDisplay, option) && _displayOfferRevision == offerRevision &&
            _displaySelectionRevision == selectionRevision && _regionRevision == regionRevision;
        var current = IsExactDisplayCurrent(option.Target);
        if (!IsCurrent()) return;
        if (!current)
        {
            DiscardDraft();
            ClearCamera(clearOptions: true, disable: true);
            _displayFailure = "The exact display is unavailable. Refresh and choose again; no other display was selected.";
            RefreshPresentation();
            return;
        }
        if (_startupBaseline is null)
        {
            DiscardDraft();
            _displayFailure = "Startup capture settings are unavailable; a tutorial plan cannot be prepared.";
            RefreshPresentation();
            return;
        }

        try
        {
            ClearWindowSelection();
            if (!IsCurrent()) return;
            ClearRegionDraft(clearText: true);
            regionRevision++;
            if (!IsCurrent()) return;
            ClearCamera(clearOptions: true, disable: true);
            if (!IsCurrent()) return;
            var result = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UtcNow,
                option.Target, _startupBaseline);
            if (!ReplaceDraftIfCurrent(result.Plan, result.Failure?.Message, IsCurrent)) return;
            _displayFailure = result.Failure?.Message;
            if (result.Plan is not null)
                PublishDraft(result.Plan);
        }
        catch
        {
            if (!IsCurrent()) return;
            DiscardDraft();
            _displayFailure = "The display could not be planned. Refresh and choose the exact display again.";
        }
        RefreshPresentation();
    }

    [RelayCommand]
    private void ConfirmRegion()
    {
        if (!CanEditPlan) return;
        var attempt = _sourceConfirmationAttempt = new object();
        if (!CanConfirmRegion || _regionSelection is null)
        {
            FailRegion("Region unavailable. Select a current display and enter complete global physical coordinates.");
            return;
        }

        var snapshot = new RegionDraftSnapshot(attempt, _selectedDisplay!, _displayOfferRevision,
            _displaySelectionRevision, _regionRevision, _regionXText, _regionYText,
            _regionWidthText, _regionHeightText);
        if (!TutorialRegionSelection.TryParseBounds(snapshot.XText, snapshot.YText,
                snapshot.WidthText, snapshot.HeightText, out var requested))
        {
            FailRegionIfCurrent(snapshot,
                "Region unavailable. X and Y must be whole physical pixels; width and height must be positive whole pixels without overflow.");
            return;
        }

        RegionCaptureTarget resolved;
        try
        {
            resolved = _regionSelection.ResolveOrThrow(snapshot.Owner.Target, requested);
        }
        catch
        {
            FailRegionIfCurrent(snapshot,
                "Region unavailable. Refresh the selected display and enter a rectangle wholly inside it.");
            return;
        }

        // Resolution is an injected boundary and can synchronously edit or confirm a
        // newer draft. A stale invocation must leave that newer state untouched.
        if (!RegionDraftMatches(snapshot)) return;
        if (_startupBaseline is null)
        {
            FailRegionIfCurrent(snapshot,
                "Startup capture settings are unavailable; a tutorial plan cannot be prepared.");
            return;
        }

        try
        {
            var result = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UtcNow,
                resolved, _startupBaseline);
            if (!RegionDraftMatches(snapshot)) return;
            if (result.Plan is null)
            {
                FailRegionIfCurrent(snapshot,
                    "The region could not be planned. Review the exact display and physical coordinates.");
                return;
            }

            PublishRegionIfCurrent(snapshot, result.Plan);
        }
        catch
        {
            FailRegionIfCurrent(snapshot,
                "The region could not be planned. Review the exact display and physical coordinates.");
        }
    }

    private bool RegionDraftMatches(RegionDraftSnapshot snapshot) =>
        CanEditPlan && ReferenceEquals(_sourceConfirmationAttempt, snapshot.Attempt) &&
        ReferenceEquals(_selectedDisplay, snapshot.Owner) &&
        _offeredDisplays.Any(option => ReferenceEquals(option, snapshot.Owner)) &&
        _displayOfferRevision == snapshot.OfferRevision &&
        _displaySelectionRevision == snapshot.SelectionRevision &&
        _regionRevision == snapshot.RegionRevision && _regionXText == snapshot.XText &&
        _regionYText == snapshot.YText && _regionWidthText == snapshot.WidthText &&
        _regionHeightText == snapshot.HeightText;

    private void PublishRegionIfCurrent(RegionDraftSnapshot snapshot, TutorialRecordingPlan plan)
    {
        if (!RegionDraftMatches(snapshot)) return;

        // Revoke can raise adapter invalidation synchronously. Recheck the complete
        // draft identity before making this invocation authoritative.
        _capture?.Revoke();
        if (!RegionDraftMatches(snapshot)) return;

        _plan = plan;
        _preflightFailure = null;
        _confirmedRegionRevision = snapshot.RegionRevision;
        _confirmedRegionOfferRevision = snapshot.OfferRevision;
        _confirmedRegionOwner = snapshot.Owner;
        _regionFailure = null;
        _displayFailure = null;
        ClearCameraState();
        _operationFailure = null;

        if (!RegionPublicationMatches(snapshot, plan)) return;

        // Publish before exposing Plan. If publishing reenters and creates a newer
        // draft, there are no state writes below that can overwrite it.
        ConfirmCaptureWithoutPresentation(plan);
        if (!RegionPublicationMatches(snapshot, plan))
        {
            if (_disposed && ReferenceEquals(_plan, plan))
            {
                _plan = null;
                _confirmedRegionRevision = null;
                _confirmedRegionOfferRevision = null;
                _confirmedRegionOwner = null;
                return;
            }
            // An edit during the adapter's synchronous invalidation normally revokes
            // the stale confirmation itself. If it left this exact plan in the VM,
            // clear only that plan; a newer confirmed plan remains authoritative.
            if (!_disposed && ReferenceEquals(_plan, plan))
            {
                _plan = null;
                _confirmedRegionRevision = null;
                _confirmedRegionOfferRevision = null;
                _confirmedRegionOwner = null;
                _capture?.Revoke();
                if (!_disposed)
                {
                    OnPropertyChanged(nameof(Plan));
                    OnPropertyChanged(nameof(RegionAvailabilityText));
                    RefreshPresentation();
                }
            }
            return;
        }
        if (_disposed) return;
        OnPropertyChanged(nameof(Plan));
        NotifyCameraStateChanged();
        OnPropertyChanged(nameof(RegionAvailabilityText));
        RefreshPresentation();
    }

    private bool RegionPublicationMatches(RegionDraftSnapshot snapshot, TutorialRecordingPlan plan) =>
        RegionDraftMatches(snapshot) && ReferenceEquals(_plan, plan) &&
        _confirmedRegionRevision == snapshot.RegionRevision &&
        _confirmedRegionOfferRevision == snapshot.OfferRevision &&
        ReferenceEquals(_confirmedRegionOwner, snapshot.Owner);

    private void FailRegionIfCurrent(RegionDraftSnapshot snapshot, string message)
    {
        if (!RegionDraftMatches(snapshot)) return;
        ApplyRegionFailure(message);
    }

    private void FailRegion(string message)
    {
        ApplyRegionFailure(message);
    }

    private void ApplyRegionFailure(string message)
    {
        if (!CanEditPlan) return;
        _plan = null;
        _preflightFailure = null;
        _confirmedRegionRevision = null;
        _confirmedRegionOfferRevision = null;
        _confirmedRegionOwner = null;
        _regionFailure = message;
        ClearCameraState();
        _operationFailure = null;
        _capture?.Revoke();
        if (_disposed) return;
        OnPropertyChanged(nameof(Plan));
        NotifyCameraStateChanged();
        OnPropertyChanged(nameof(RegionAvailabilityText));
        RefreshPresentation();
    }

    private readonly record struct RegionDraftSnapshot(
        object Attempt,
        DisplayCaptureOption Owner,
        long OfferRevision,
        long SelectionRevision,
        long RegionRevision,
        string XText,
        string YText,
        string WidthText,
        string HeightText);

    [RelayCommand]
    private void ConfirmCamera()
    {
        if (!CanEditPlan) return;
        _sourceConfirmationAttempt = new object();
        if (!CanConfirmCamera)
        {
            _cameraFailure = "A stale camera selection was ignored. Choose a current physical camera before confirming.";
            RefreshPresentation();
            return;
        }

        if (_isWindowCurrent is not null)
        {
            bool windowCurrent;
            try
            {
                windowCurrent = _isWindowCurrent(_selectedWindow!);
            }
            catch
            {
                windowCurrent = false;
            }
            if (!windowCurrent)
            {
                DiscardDraft();
                ClearCamera(clearOptions: true, disable: true);
                _cameraFailure = "The exact window is unavailable. Refresh it before confirming a camera.";
                RefreshPresentation();
                return;
            }
        }

        try
        {
            var config = _startupBaseline?.Baseline?.WebcamFormatDefaults ?? throw new InvalidOperationException();
            var camera = _selectedCamera!;
            var webcam = new WebcamCaptureSelection(camera.DeviceId, camera.DisplayName,
                config.Width, config.Height, config.FramesPerSecond,
                config.VideoFormat, config.VideoBitrateKbps);
            var target = _plan!.Request.Target;
            var result = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UtcNow,
                target, _startupBaseline!, webcam, explicitWebcamConsent: true);
            if (result.Plan is null)
            {
                RevokePublishedPlan();
                _cameraFailure = result.Failure?.Message ?? "The camera plan is unavailable.";
                RefreshPresentation();
                return;
            }
            ReplaceDraft(result.Plan);
            PublishDraft(result.Plan);
            _cameraFailure = null;
            _preflightFailure = null;
        }
        catch
        {
            RevokePublishedPlan();
            _cameraFailure = "The requested camera format could not be planned. Correct Settings and choose the camera again.";
        }
        RefreshPresentation();
    }

    private void RemoveConfirmedCameraFromPlan()
    {
        RevokePublishedPlan();
        if (_plan is null || _startupBaseline is null) return;
        try
        {
            var result = TutorialRecordingPolicy.Create(Guid.NewGuid(), DateTimeOffset.UtcNow,
                _plan.Request.Target, _startupBaseline);
            ReplaceDraft(result.Plan, result.Failure?.Message);
        }
        catch
        {
            DiscardDraft("The camera choice could not be removed safely. Confirm the exact window again.");
        }
    }

    private void RevokePublishedPlan()
    {
        _capture?.Revoke();
        _operationFailure = null;
    }

    private void DiscardDraft(string? failure = null)
    {
        RevokePublishedPlan();
        _plan = null;
        _preflightFailure = failure;
        OnPropertyChanged(nameof(Plan));
    }

    private void ReplaceDraft(TutorialRecordingPlan? plan, string? failure = null)
    {
        RevokePublishedPlan();
        _plan = plan;
        _preflightFailure = failure;
        OnPropertyChanged(nameof(Plan));
    }

    private void PublishDraft(TutorialRecordingPlan plan)
    {
        _operationFailure = null;
        ConfirmCaptureWithoutPresentation(plan);
    }

    private bool ReplaceDraftIfCurrent(TutorialRecordingPlan? plan, string? failure, Func<bool> isCurrent)
    {
        _capture?.Revoke();
        if (!isCurrent()) return false;
        _plan = plan;
        _preflightFailure = failure;
        OnPropertyChanged(nameof(Plan));
        return isCurrent() && ReferenceEquals(_plan, plan);
    }

    private void ConfirmCaptureWithoutPresentation(TutorialRecordingPlan plan)
    {
        // The adapter binds its returned revision after controller confirmation.
        // Defer VM notifications until that binding is complete, so a presentation
        // observer can revoke the actual revision or confirm a newer plan safely.
        _capturePublicationDepth++;
        try { _capture?.Confirm(plan); }
        finally { _capturePublicationDepth--; }
        // Callers check publication identity, then flush through RefreshPresentation.
    }

    private void ClearWindowSelection()
    {
        _selectedWindow = null;
        _selectionRevision = 0;
        OnPropertyChanged(nameof(SelectedWindow));
        OnPropertyChanged(nameof(CanConfirmWindow));
    }

    private void SetRegionText(ref string field, string value, string propertyName)
    {
        if (!CanEditPlan || field == value) return;
        AdvanceRegionRevision();
        field = value;
        _plan = null;
        _preflightFailure = null;
        _selectedWindow = null;
        _selectionRevision = 0;
        ClearCameraState();
        _confirmedRegionRevision = null;
        _confirmedRegionOfferRevision = null;
        _confirmedRegionOwner = null;
        _regionFailure = null;
        _operationFailure = null;
        _capture?.Revoke();
        if (_disposed) return;
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(SelectedWindow));
        OnPropertyChanged(nameof(CanConfirmWindow));
        NotifyCameraStateChanged();
        OnPropertyChanged(propertyName);
        OnPropertyChanged(nameof(CanConfirmRegion));
        OnPropertyChanged(nameof(RegionAvailabilityText));
        RefreshPresentation();
    }

    private void ClearRegionDraft(bool clearText)
    {
        AdvanceRegionRevision();
        _confirmedRegionRevision = null;
        _confirmedRegionOfferRevision = null;
        _confirmedRegionOwner = null;
        _regionFailure = null;
        if (clearText)
        {
            _regionXText = string.Empty;
            _regionYText = string.Empty;
            _regionWidthText = string.Empty;
            _regionHeightText = string.Empty;
            OnPropertyChanged(nameof(RegionXText));
            OnPropertyChanged(nameof(RegionYText));
            OnPropertyChanged(nameof(RegionWidthText));
            OnPropertyChanged(nameof(RegionHeightText));
        }
        OnPropertyChanged(nameof(CanConfirmRegion));
        OnPropertyChanged(nameof(RegionAvailabilityText));
    }

    private void AdvanceRegionRevision()
    {
        if (_regionRevision == long.MaxValue)
            throw new InvalidOperationException("The region selection revision is exhausted.");
        _regionRevision++;
    }

    private void ClearDisplaySelection()
    {
        _selectedDisplay = null;
        _displaySelectionRevision = 0;
        _displayFailure = null;
        ClearRegionDraft(clearText: true);
        OnPropertyChanged(nameof(SelectedDisplay));
        OnPropertyChanged(nameof(CanConfirmDisplay));
        OnPropertyChanged(nameof(DisplayAvailabilityText));
    }

    private void ClearCamera(bool clearOptions, bool disable)
    {
        _selectedCamera = null;
        if (clearOptions) _offeredCameras = [];
        if (disable) _includeCamera = false;
        _cameraFailure = null;
        NotifyCameraStateChanged();
    }

    private void ClearCameraState()
    {
        _selectedCamera = null;
        _offeredCameras = [];
        _includeCamera = false;
        _cameraFailure = null;
    }

    private void NotifyCameraStateChanged()
    {
        OnPropertyChanged(nameof(SelectedCamera));
        OnPropertyChanged(nameof(CameraOptions));
        OnPropertyChanged(nameof(IncludeCamera));
        OnPropertyChanged(nameof(CanConfirmCamera));
        OnPropertyChanged(nameof(CanConfigureCamera));
        OnPropertyChanged(nameof(CanUseCameraOptions));
        OnPropertyChanged(nameof(CameraText));
    }

    private async Task StartAsync()
    {
        if (!StartAvailable || _capture is null || _plan is null) return;
        var plan = _plan;
        if (!IsExactTargetCurrentForStart(plan))
        {
            if (_disposed || !ReferenceEquals(_plan, plan)) return;
            DiscardDraft("The exact source could not be revalidated. Refresh and confirm it again; no broader source was selected.");
            ClearCamera(clearOptions: true, disable: true);
            RefreshPresentation();
            return;
        }

        _operationFailure = null;
        RefreshPresentation();
        if (!PlanStateMatchesForDispatch(plan))
        {
            if (_disposed || !ReferenceEquals(_plan, plan)) return;
            DiscardDraft("The exact source changed while it was being checked. Review and confirm it again.");
            ClearCamera(clearOptions: true, disable: true);
            RefreshPresentation();
            return;
        }
        try
        {
            if (await _capture.StartAsync() is null)
                _operationFailure = "Recording could not be started. Confirm the exact source and try again.";
        }
        catch
        {
            _operationFailure = "Recording needs attention. Check recording diagnostics before trying again.";
        }
        RefreshPresentation();
    }

    private bool IsExactTargetCurrentForStart(TutorialRecordingPlan plan)
    {
        if (_capture is null || !ReferenceEquals(_plan, plan)) return false;
        var current = plan.Request.Target switch
        {
            WindowCaptureTarget window => IsExactWindowCurrent(window),
            DisplayCaptureTarget display => _selectedDisplay?.Target == display &&
                _displaySelectionRevision == _displayOfferRevision &&
                _offeredDisplays.Any(option => ReferenceEquals(option, _selectedDisplay)) &&
                IsExactDisplayCurrent(display),
            RegionCaptureTarget region => IsExactRegionCurrent(plan, region),
            _ => false,
        };
        return current && PlanStateMatchesForDispatch(plan);
    }

    private bool PlanStateMatchesForDispatch(TutorialRecordingPlan plan) =>
        ReferenceEquals(_plan, plan) && (plan.Request.Target is not RegionCaptureTarget region ||
            RegionPlanMatches(plan, region));

    private bool RegionPlanMatches(TutorialRecordingPlan plan, RegionCaptureTarget region)
    {
        var owner = _selectedDisplay;
        return ReferenceEquals(_plan, plan) && owner is not null &&
            ReferenceEquals(owner, _confirmedRegionOwner) &&
            _offeredDisplays.Any(option => ReferenceEquals(option, owner)) &&
            _confirmedRegionRevision == _regionRevision &&
            _confirmedRegionOfferRevision == _displayOfferRevision &&
            _displaySelectionRevision == _displayOfferRevision &&
            StringComparer.Ordinal.Equals(owner.Target.StableDisplayId, region.OwningDisplayId);
    }

    private bool IsExactRegionCurrent(TutorialRecordingPlan plan, RegionCaptureTarget region)
    {
        if (_regionSelection is null || _isDisplayCurrent is null || !RegionPlanMatches(plan, region))
            return false;
        var owner = _selectedDisplay!;
        var regionRevision = _regionRevision;
        var offerRevision = _displayOfferRevision;
        try
        {
            if (!_isDisplayCurrent(owner.Target) || !_regionSelection.IsCurrent(region))
                return false;
        }
        catch
        {
            return false;
        }
        return regionRevision == _regionRevision && offerRevision == _displayOfferRevision &&
            ReferenceEquals(owner, _selectedDisplay) && RegionPlanMatches(plan, region);
    }

    private bool IsExactWindowCurrent(WindowCaptureTarget target)
    {
        if (_isWindowCurrent is null || _selectedWindow is null ||
            target.ProcessId != _selectedWindow.ProcessId || target.WindowHandle != _selectedWindow.Handle)
            return false;
        try { return _isWindowCurrent(_selectedWindow); }
        catch { return false; }
    }

    private bool IsExactDisplayCurrent(DisplayCaptureTarget target)
    {
        if (_isDisplayCurrent is null) return false;
        try { return _isDisplayCurrent(target); }
        catch { return false; }
    }

    private async Task StopAsync()
    {
        if (!StopAvailable || _capture is null) return;
        _operationFailure = null;
        RefreshPresentation();
        try
        {
            if (await _capture.StopAsync() is null)
                _operationFailure = "Recording cleanup could not be requested. Use Stop to try again.";
        }
        catch
        {
            _operationFailure = "Recording cleanup needs attention. Use Stop to try again.";
        }
        RefreshPresentation();
    }

    private void Cancel()
    {
        if (!CancelAvailable || _capture is null) return;
        _capture.Revoke();
        _operationFailure = null;
        RefreshPresentation();
    }

    public async Task<bool> RequestCloseAsync()
    {
        if (_disposed) return true;
        if (_isCloseInFlight) return false;
        if (_capture is null) return true;

        _isCloseInFlight = true;
        RefreshPresentation();
        try
        {
            var released = await _capture.CloseAsync();
            if (!released)
                _operationFailure = "Recording cleanup is incomplete. Use Stop or Close to try again.";
            return released;
        }
        catch
        {
            _operationFailure = "The tutorial workspace could not close while recording cleanup is pending.";
            return false;
        }
        finally
        {
            _isCloseInFlight = false;
            RefreshPresentation();
        }
    }

    public void ReportCloseFailure()
    {
        if (_disposed) return;
        _operationFailure = "The tutorial workspace could not close while recording cleanup is pending.";
        RefreshPresentation();
    }

    public void RefreshPresentation()
    {
        if (_disposed || _capturePublicationDepth != 0) return;
        // Backend messages can contain local paths or opaque device identities. The tutorial
        // status is also announced by UI Automation, so project only a generic warning here.
        var snapshot = _observation.Snapshot;
        if (snapshot.LastFailure is { } failure)
            snapshot = snapshot with { LastFailure = failure with { Message = "Capture needs attention. Check recording diagnostics." } };
        var presentedPlan = _capture?.ActivePlan ?? _plan;
        var projection = TutorialWorkspaceProjection.Create(presentedPlan, snapshot);
        var status = _preflightFailure is null
            ? _capture?.StatusText ?? projection.StatusText
            : $"{_capture?.StatusText ?? projection.StatusText} Preflight: {_preflightFailure}";
        if (_cameraFailure is not null)
            status += $" Camera: {_cameraFailure}";
        if (_displayFailure is not null)
            status += $" Display: {_displayFailure}";
        if (_regionFailure is not null && presentedPlan?.Request.Target is not RegionCaptureTarget)
            status += $" Region: {_regionFailure}";
        if (_operationFailure is not null)
            status += $" {_operationFailure}";
        SetProperty(ref _statusText, status, nameof(StatusText));
        SetProperty(ref _boundaryText, projection.BoundaryText, nameof(BoundaryText));
        SetProperty(ref _audioText, AudioSummary(presentedPlan), nameof(AudioText));
        SetProperty(ref _exactWindowText, WindowSummary(presentedPlan), nameof(ExactWindowText));
        SetProperty(ref _outputText, OutputSummary(presentedPlan), nameof(OutputText));
        OnPropertyChanged(nameof(CameraText));
        OnPropertyChanged(nameof(CanConfirmCamera));
        OnPropertyChanged(nameof(CanConfirmWindow));
        OnPropertyChanged(nameof(CanConfirmDisplay));
        OnPropertyChanged(nameof(DisplayAvailabilityText));
        OnPropertyChanged(nameof(CanConfirmRegion));
        OnPropertyChanged(nameof(RegionAvailabilityText));
        OnPropertyChanged(nameof(CanConfigureCamera));
        OnPropertyChanged(nameof(CanUseCameraOptions));
        OnPropertyChanged(nameof(ActivePlan));
        OnPropertyChanged(nameof(StartAvailable));
        OnPropertyChanged(nameof(StopAvailable));
        OnPropertyChanged(nameof(CancelAvailable));
        OnPropertyChanged(nameof(CanEditPlan));
        OnPropertyChanged(nameof(CloseAvailable));
        OnPropertyChanged(nameof(StartReason));
        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
    }

    private void OnSnapshotChanged(CaptureLifecycleSnapshot _)
    {
        QueuePresentationRefresh();
    }

    private void OnCaptureStateChanged() => QueuePresentationRefresh();

    private void QueuePresentationRefresh()
    {
        if (_disposed) return;
        if (_uiContext is not null && SynchronizationContext.Current != _uiContext)
        {
            _uiContext.Post(static state =>
            {
                var viewModel = (TutorialWorkspaceViewModel)state!;
                if (!viewModel._disposed) viewModel.RefreshPresentation();
            }, this);
            return;
        }
        RefreshPresentation();
    }

    private static string AudioSummary(TutorialRecordingPlan? plan)
    {
        if (plan is null) return "No startup audio plan.";
        var audio = plan.StartupAudio;
        var systemDevice = string.IsNullOrWhiteSpace(audio.DesktopDeviceId) ? "system default" : audio.DesktopDeviceId;
        var micDevice = string.IsNullOrWhiteSpace(audio.MicDeviceId) ? "system default" : audio.MicDeviceId;
        var tracks = audio.SeparateAudioTracks ? "separate tracks (system 1, microphone 2)" : "combined track 1";
        return $"Startup settings: system audio {(audio.DesktopAudioEnabled ? "on" : "off")} ({systemDevice}); microphone {(audio.MicEnabled ? "on" : "off")} ({micDevice}); {tracks}. Changes to audio settings require restart for a new startup baseline; actual devices require lifecycle admission.";
    }

    private static string WindowSummary(TutorialRecordingPlan? plan) => plan?.Request.Target switch
    {
        WindowCaptureTarget window =>
            $"Exact metadata: {window.ExecutableName}, PID {window.ProcessId}, HWND 0x{window.WindowHandle.ToInt64():X}, class {window.WindowClass ?? "unknown"}. This list is not ongoing source validation.",
        DisplayCaptureTarget display =>
            $"Entire display selected: {display.DisplayName ?? "Display"}. Exact display identity: {display.StableDisplayId}. Everything visible on this display may be recorded.",
        RegionCaptureTarget region =>
            $"Exact region selected on display {region.OwningDisplayId}: {region.PhysicalBounds.Width} × {region.PhysicalBounds.Height} physical pixels at global ({region.PhysicalBounds.X}, {region.PhysicalBounds.Y}). The frozen rectangle remains bound to that exact owning display and never widens to the desktop.",
        _ => "No exact window selected.",
    };

    private static string OutputSummary(TutorialRecordingPlan? plan)
    {
        if (plan?.Request.StartPolicy is not { } policy)
            return "No session output plan.";
        var duration = policy.Recording.MaxDurationMinutes == 0
            ? "unlimited duration" : $"{policy.Recording.MaxDurationMinutes} minute limit";
        return $"Planned session recording: {policy.Capture.Encoder}, {policy.Recording.FileFormat}, " +
            $"{policy.Recording.SegmentDurationMinutes} minute segments, {duration}. " +
            $"Configured Save Path: {plan.CanonicalSaveRoot}. Media admission and save success are unverified.";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _observation.SnapshotChanged -= OnSnapshotChanged;
        if (_capture is not null)
        {
            _capture.StateChanged -= OnCaptureStateChanged;
            _capture.Dispose();
        }
    }
}
