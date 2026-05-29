using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WatchDog.App.ViewModels;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Discord;

namespace WatchDog.App.Views;

public partial class DiscordUploadWindow : Window
{
    public DiscordUploadWindow(ClipMetadata metadata)
    {
        InitializeComponent();

        var service = App.Services.GetRequiredService<IDiscordWebhookService>();
        var vm = new DiscordUploadViewModel(service);
        DataContext = vm;

        Action closeHandler = () => { if (IsLoaded) Dispatcher.InvokeAsync(Close); };
        vm.RequestClose += closeHandler;

        Closed += (_, _) =>
        {
            vm.RequestClose -= closeHandler;
            vm.CancelCommand.Execute(null);
        };

        Loaded += (_, _) => vm.UploadCommand.Execute(metadata);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
