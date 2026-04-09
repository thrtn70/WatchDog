using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TikrClipr.App.Controls;

public partial class ClipSavedToast : Window
{
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
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _closeTimer.Tick += (_, _) => FadeOutAndClose();
        _closeTimer.Start();
    }

    private void FadeOutAndClose()
    {
        _closeTimer.Stop();
        var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300));
        fadeOut.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fadeOut);
    }
}
