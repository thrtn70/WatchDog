using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Data;
using System.Windows.Input;
using WatchDog.App.Services;
using WatchDog.App.ViewModels;
using WatchDog.Core.Webcam;
using WatchDog.Native.Win32;

namespace WatchDog.App.Views;

public partial class TutorialWorkspaceWindow : Window
{
    private readonly TutorialWorkspaceViewModel _viewModel;
    private readonly Func<IReadOnlyList<WindowEnumerator.WindowInfo>> _enumerateWindows;
    private readonly Func<IReadOnlyList<WebcamDeviceInfo>> _enumerateCameras;
    private readonly Func<IReadOnlyList<DisplayCaptureOption>>? _enumerateDisplays;
    private bool _closeAuthorized;
    private bool _closePending;

    public TutorialWorkspaceWindow(TutorialWorkspaceViewModel viewModel,
        Func<IReadOnlyList<WindowEnumerator.WindowInfo>> enumerateWindows,
        Func<IReadOnlyList<WebcamDeviceInfo>> enumerateCameras,
        Func<IReadOnlyList<DisplayCaptureOption>>? enumerateDisplays = null)
    {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _enumerateWindows = enumerateWindows ?? throw new ArgumentNullException(nameof(enumerateWindows));
        _enumerateCameras = enumerateCameras ?? throw new ArgumentNullException(nameof(enumerateCameras));
        _enumerateDisplays = enumerateDisplays;
        InitializeComponent();
        DataContext = viewModel;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshWindowMetadata();
        WindowOptionsList.Focus();
    }

    private void OnRefreshWindowsClick(object sender, RoutedEventArgs e) => RefreshWindowMetadata();

    private void OnRefreshDisplaysClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanEditPlan) return;
        if (_enumerateDisplays is null)
        {
            _viewModel.ReportDisplayListUnavailable();
            return;
        }
        _viewModel.RefreshDisplays(_enumerateDisplays);
    }

    private void OnRefreshCamerasClick(object sender, RoutedEventArgs e)
    {
        if (!_viewModel.CanUseCameraOptions) return;
        _viewModel.OfferCameras([]);
        try
        {
            _viewModel.OfferCameras(_enumerateCameras());
        }
        catch
        {
            _viewModel.ReportCameraListUnavailable();
        }
    }

    private void RefreshWindowMetadata()
    {
        if (!_viewModel.CanEditPlan) return;
        _viewModel.OfferWindows([]);
        try
        {
            _viewModel.OfferWindows(_enumerateWindows());
        }
        catch (Exception)
        {
            _viewModel.ReportWindowListUnavailable();
        }
    }

    private void OnWindowOptionsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_viewModel.CanConfirmWindow) return;
        _viewModel.ConfirmWindowCommand.Execute(null);
        e.Handled = true;
    }

    private void OnDisplayOptionsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_viewModel.CanConfirmDisplay) return;
        _viewModel.ConfirmDisplayCommand.Execute(null);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeAuthorized) return;
        e.Cancel = true;
        if (_closePending) return;

        _closePending = true;
        try
        {
            if (await _viewModel.RequestCloseAsync())
            {
                _closeAuthorized = true;
                // CloseAsync may finish synchronously. Reentering Close while WPF is
                // still raising Closing is invalid; retry after this event returns.
                _ = Dispatcher.BeginInvoke(new Action(Close));
            }
        }
        catch
        {
            _viewModel.ReportCloseFailure();
        }
        finally
        {
            _closePending = false;
        }
    }

    private void OnClosed(object? sender, EventArgs e) => _viewModel.Dispose();

    private void OnStatusTargetUpdated(object sender, DataTransferEventArgs e)
    {
        if (sender is not UIElement element) return;
        var peer = UIElementAutomationPeer.FromElement(element) ??
                   UIElementAutomationPeer.CreatePeerForElement(element);
        peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
