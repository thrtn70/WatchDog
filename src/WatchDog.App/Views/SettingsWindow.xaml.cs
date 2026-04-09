using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WatchDog.App.ViewModels;
using WatchDog.Core.Settings;

namespace WatchDog.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        DataContext = App.Services.GetRequiredService<SettingsViewModel>();
    }
}
