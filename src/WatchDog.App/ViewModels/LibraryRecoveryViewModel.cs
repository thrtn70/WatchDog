using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WatchDog.Core.Storage;

namespace WatchDog.App.ViewModels;

/// <summary>
/// Drives the "library unavailable" banner. A rebuild can replace the damaged index, so it only
/// runs after the user confirms a prompt that says what will and will not come back.
/// </summary>
public sealed partial class LibraryRecoveryViewModel : ObservableObject
{
    private const string DialogTitle = "Rebuild Clip Library";
    private readonly IClipLibraryHealth _health;
    private readonly IClipLibraryRebuild _rebuild;
    private readonly Func<string, bool> _confirm;
    private readonly Action<string, bool> _notify;

    [ObservableProperty] private bool _isLibraryUnavailable;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildLibraryCommand))]
    private bool _isBannerVisible;

    [ObservableProperty] private string _bannerText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RebuildLibraryCommand))]
    private bool _isRebuilding;

    public LibraryRecoveryViewModel(IClipLibraryHealth health, IClipLibraryRebuild rebuild)
        : this(health, rebuild, ConfirmWithDialog, NotifyWithDialog)
    {
    }

    internal LibraryRecoveryViewModel(
        IClipLibraryHealth health,
        IClipLibraryRebuild rebuild,
        Func<string, bool> confirm,
        Action<string, bool> notify)
    {
        _health = health;
        _rebuild = rebuild;
        _confirm = confirm;
        _notify = notify;
        ShowStatus(health.LibraryStatus, failure: null);
    }

    /// <summary>Raised after a rebuild makes the library available again.</summary>
    public event Action? LibraryRestored;

    [RelayCommand(CanExecute = nameof(CanRebuildLibrary))]
    private async Task RebuildLibraryAsync()
    {
        var status = _health.LibraryStatus;
        if (status.IsAvailable && !status.IsRebuildIncomplete)
        {
            ShowStatus(status, failure: null);
            return;
        }

        if (!_confirm(RebuildPrompt(status)))
            return;

        IsRebuilding = true;
        ClipLibraryRebuildResult result;
        try
        {
            // Only what the user just agreed to: an index that may still be valid is never replaced.
            result = await _rebuild.RebuildFromFolderAsync(status.CanBuildNewIndex);
        }
        catch (Exception ex)
        {
            result = new ClipLibraryRebuildResult(_health.LibraryStatus, 0, 0, ex.Message);
        }
        finally
        {
            IsRebuilding = false;
        }

        ShowStatus(result.Status, result.FailureMessage);
        if (result.Status.IsAvailable)
            LibraryRestored?.Invoke();
        _notify(ResultMessage(result), !result.Succeeded);
    }

    private bool CanRebuildLibrary() => IsBannerVisible && !IsRebuilding;

    private void ShowStatus(ClipLibraryStatus status, string? failure)
    {
        IsLibraryUnavailable = !status.IsAvailable;
        IsBannerVisible = !status.IsAvailable || status.IsRebuildIncomplete;
        BannerText = !status.IsAvailable
            ? UnavailableBanner(status)
            : status.IsRebuildIncomplete
                ? IncompleteBanner(failure)
                : string.Empty;
    }

    private static string UnavailableBanner(ClipLibraryStatus status)
    {
        var evidence = status.EvidencePaths.Count > 0
            ? $"The damaged index was kept at {string.Join(" and ", status.EvidencePaths)}."
            : "The index file was left unchanged.";
        return $"Your clip library could not be loaded. {status.Problem} New clips are still being saved to disk, " +
            $"but favorites, tags, deleting clips and automatic cleanup are paused. {evidence}";
    }

    private static string IncompleteBanner(string? failure) =>
        "The clip library rebuild did not finish, so some clips may be missing from the library. " +
        "Their files are safe on disk." +
        (failure is null ? string.Empty : $" {failure}");

    private static string RebuildPrompt(ClipLibraryStatus status)
    {
        if (status.IsAvailable)
        {
            return "WatchDog will scan the recordings folder again and add clips that are missing from the " +
                "library. Nothing already in the library changes.\n\nScan now?";
        }

        if (!status.CanBuildNewIndex)
        {
            return "WatchDog will try to load your clip library again. It will not replace the existing index, " +
                "so first close any program that may be holding it open, such as a backup or antivirus scan.\n\n" +
                "If the library loads, clips in the recordings folder that are missing from it are added.\n\n" +
                "Try now?";
        }

        return "WatchDog will try to load your clip library again. If it is still damaged, WatchDog starts a new " +
            "library and adds every clip in the recordings folder to it.\n\n" +
            "Favorites and tags saved in the damaged index will not come back. Clips added this way are never " +
            "removed by automatic cleanup; if they fill your storage limit, recording pauses until you delete " +
            "clips or raise the limit. The damaged index stays at:\n" +
            string.Join("\n", status.EvidencePaths) +
            "\n\nRebuild the library now?";
    }

    private static string ResultMessage(ClipLibraryRebuildResult result)
    {
        if (result.Succeeded)
        {
            return "Your clip library is available again. " +
                $"{result.ClipsIndexedFromFolder} clip(s) were added from the recordings folder and " +
                $"{result.PendingCapturesIndexed} recent capture(s) were indexed.";
        }

        return result.Status.IsAvailable
            ? $"Your clip library is available again, but the rebuild did not finish: {result.FailureMessage} " +
              "Use the banner to scan the folder again."
            : $"The clip library is still unavailable: {result.FailureMessage}";
    }

    private static bool ConfirmWithDialog(string prompt) =>
        MessageBox.Show(
            prompt,
            DialogTitle,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    private static void NotifyWithDialog(string message, bool isError) =>
        MessageBox.Show(
            message,
            DialogTitle,
            MessageBoxButton.OK,
            isError ? MessageBoxImage.Warning : MessageBoxImage.Information);
}
