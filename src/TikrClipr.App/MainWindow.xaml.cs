using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TikrClipr.App.ViewModels;

namespace TikrClipr.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<MainWindowViewModel>();
        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        (DataContext as MainWindowViewModel)?.RefreshClipsCommand.Execute(null);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Hide instead of close (tray app)
        e.Cancel = true;
        Hide();
    }

    private void FloatingPanelCanvas_Loaded(object sender, RoutedEventArgs e)
    {
        var canvasWidth = FloatingPanelCanvas.ActualWidth;
        var canvasHeight = FloatingPanelCanvas.ActualHeight;

        if (canvasWidth == 0 || canvasHeight == 0) return; // window not yet laid out

        // Performance: top-right (PanelTop set in XAML to 8)
        PerformancePanel.PanelLeft = Math.Max(0, canvasWidth - PerformancePanel.PanelWidth - 8);

        // Audio: bottom-right
        AudioPanel.PanelLeft = Math.Max(0, canvasWidth - AudioPanel.PanelWidth - 8);
        AudioPanel.PanelTop = Math.Max(0, canvasHeight - AudioPanel.PanelHeight - 8);
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var settings = new Views.SettingsWindow();
        settings.Owner = this;
        settings.ShowDialog();
    }

    private void OnStorageDashboardClick(object sender, RoutedEventArgs e)
    {
        var dashboard = new Views.StorageDashboardWindow();
        dashboard.Owner = this;
        dashboard.ShowDialog();
    }
}
