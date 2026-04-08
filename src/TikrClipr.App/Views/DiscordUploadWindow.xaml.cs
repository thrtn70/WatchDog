using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using TikrClipr.App.ViewModels;
using TikrClipr.Core.ClipEditor;
using TikrClipr.Core.Discord;

namespace TikrClipr.App.Views;

public partial class DiscordUploadWindow : Window
{
    public DiscordUploadWindow(ClipMetadata metadata)
    {
        InitializeComponent();

        var service = App.Services.GetRequiredService<IDiscordWebhookService>();
        var vm = new DiscordUploadViewModel(service);
        DataContext = vm;

        Loaded += (_, _) => vm.UploadCommand.Execute(metadata);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
