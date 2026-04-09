using System.Windows;
using System.Windows.Controls.Primitives;
using Microsoft.Extensions.DependencyInjection;
using WatchDog.App.ViewModels;

namespace WatchDog.App;

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
        => RepositionFloatingPanels();

    private void FloatingPanelCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => ClampFloatingPanels();

    private void RepositionFloatingPanels()
    {
        var w = FloatingPanelCanvas.ActualWidth;
        var h = FloatingPanelCanvas.ActualHeight;
        if (w == 0 || h == 0) return;

        PerformancePanel.PanelLeft = Math.Max(0, w - PerformancePanel.PanelWidth - 8);

        AudioPanel.PanelLeft = Math.Max(0, w - AudioPanel.PanelWidth - 8);
        AudioPanel.PanelTop = Math.Max(0, h - AudioPanel.PanelHeight - 8);
    }

    private void ClampFloatingPanels()
    {
        var w = FloatingPanelCanvas.ActualWidth;
        var h = FloatingPanelCanvas.ActualHeight;
        if (w == 0 || h == 0) return;

        // Keep panels within canvas bounds after resize
        PerformancePanel.PanelLeft = Math.Clamp(PerformancePanel.PanelLeft, 0, Math.Max(0, w - PerformancePanel.PanelWidth));
        PerformancePanel.PanelTop = Math.Clamp(PerformancePanel.PanelTop, 0, Math.Max(0, h - PerformancePanel.PanelHeight));

        AudioPanel.PanelLeft = Math.Clamp(AudioPanel.PanelLeft, 0, Math.Max(0, w - AudioPanel.PanelWidth));
        AudioPanel.PanelTop = Math.Clamp(AudioPanel.PanelTop, 0, Math.Max(0, h - AudioPanel.PanelHeight));
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

    // ── View Switching ─────────────────────────────────────────────────
    private void OnGridViewChecked(object sender, RoutedEventArgs e)
    {
        if (ListViewToggle is null || ClipGridView is null || ClipListView is null) return;
        ListViewToggle.IsChecked = false;
        ClipGridView.Visibility = Visibility.Visible;
        ClipListView.Visibility = Visibility.Collapsed;
    }

    private void OnListViewChecked(object sender, RoutedEventArgs e)
    {
        if (GridViewToggle is null) return;
        GridViewToggle.IsChecked = false;
        ClipGridView.Visibility = Visibility.Collapsed;
        ClipListView.Visibility = Visibility.Visible;
    }
}
