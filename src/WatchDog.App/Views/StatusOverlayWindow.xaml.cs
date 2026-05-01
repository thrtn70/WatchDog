using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using WatchDog.Core.Settings;
using WatchDog.Native.Win32;

namespace WatchDog.App.Views;

public partial class StatusOverlayWindow : Window
{
    private readonly OverlaySettings _settings;

    public StatusOverlayWindow(OverlaySettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Apply saved position immediately if available (bounds-validated)
        var workArea = SystemParameters.WorkArea;
        if (double.IsFinite(_settings.PositionX) && double.IsFinite(_settings.PositionY)
            && _settings.PositionX >= 0 && _settings.PositionY >= 0)
        {
            Left = Math.Clamp(_settings.PositionX, workArea.Left, workArea.Right - 50);
            Top = Math.Clamp(_settings.PositionY, workArea.Top, workArea.Bottom - 20);
        }

        SetClickThrough(true);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        // Default position: top-right with 20px margin. Done here (not OnSourceInitialized)
        // because ActualWidth is 0 before layout runs.
        if (_settings.PositionX < 0 || _settings.PositionY < 0
            || !double.IsFinite(_settings.PositionX) || !double.IsFinite(_settings.PositionY))
        {
            var workArea = SystemParameters.WorkArea;
            Left = workArea.Right - ActualWidth - 20;
            Top = workArea.Top + 20;
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        SetClickThrough(false);
        Opacity = 1.0;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        SetClickThrough(true);
        Opacity = 0.85;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ViewModels.StatusOverlayViewModel vm)
        {
            vm.ToggleMode();

            // Re-clamp position after mode switch changes the window width
            Dispatcher.InvokeAsync(() =>
            {
                UpdateLayout();
                var workArea = SystemParameters.WorkArea;
                if (Left + ActualWidth > workArea.Right)
                    Left = workArea.Right - ActualWidth - 12;
                if (Left < workArea.Left)
                    Left = workArea.Left + 12;
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void SetClickThrough(bool enable)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        Marshal.SetLastSystemError(0);
        var style = User32.GetWindowLongPtr(hwnd, User32.GWL_EXSTYLE);
        if (style == 0 && Marshal.GetLastWin32Error() != 0)
            return; // GetWindowLongPtr failed

        if (enable)
            style |= User32.WS_EX_TRANSPARENT;
        else
            style &= ~User32.WS_EX_TRANSPARENT;

        Marshal.SetLastSystemError(0);
        User32.SetWindowLongPtr(hwnd, User32.GWL_EXSTYLE, style);
    }
}
