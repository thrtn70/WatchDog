using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TikrClipr.App.ViewModels;
using TikrClipr.Core.Settings;

namespace TikrClipr.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        DataContext = App.Services.GetRequiredService<SettingsViewModel>();
    }
}
