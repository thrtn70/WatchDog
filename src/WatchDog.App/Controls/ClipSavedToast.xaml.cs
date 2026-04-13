using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace WatchDog.App.Controls;

public partial class ClipSavedToast : Window
{
    private const int AutoCloseSeconds = 3;
    private const int FadeOutMs = 300;
    private readonly DispatcherTimer _closeTimer;

    public ClipSavedToast(string clipName, string? gameName)
    {
        InitializeComponent();

        ClipNameText.Text = clipName;
        ClipDetailsText.Text = gameName ?? "Desktop";

        // Position near system tray (bottom-right of work area)
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - 340;
        Top = workArea.Bottom - 100;

        // Auto-close after 3 seconds
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AutoCloseSeconds) };
        _closeTimer.Tick += (_, _) => FadeOutAndClose();
        _closeTimer.Start();
    }

    private void FadeOutAndClose()
    {
        _closeTimer.Stop();
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(FadeOutMs));
        fadeOut.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
