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
    {
        var canvasWidth = FloatingPanelCanvas.ActualWidth;
        var canvasHeight = FloatingPanelCanvas.ActualHeight;

        if (canvasWidth == 0 || canvasHeight == 0) return;

        PerformancePanel.PanelLeft = Math.Max(0, canvasWidth - PerformancePanel.PanelWidth - 8);

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
