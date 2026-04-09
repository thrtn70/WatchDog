using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WatchDog.App.ViewModels;

namespace WatchDog.App.Views;

public partial class StorageDashboardWindow : Window
{
    public StorageDashboardWindow()
    {
        InitializeComponent();
        DataContext = App.Services.GetRequiredService<StorageDashboardViewModel>();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
