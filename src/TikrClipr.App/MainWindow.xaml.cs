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

    private void OnStorageDashboardClick(object sender, RoutedEventArgs e)
    {
        var dashboard = new Views.StorageDashboardWindow();
        dashboard.ShowDialog();
    }
}
