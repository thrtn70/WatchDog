using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Discord;

namespace WatchDog.App.ViewModels;

public partial class DiscordUploadViewModel : ObservableObject, IDisposable
{
    private readonly IDiscordWebhookService _service;
    private CancellationTokenSource? _cts;

    [ObservableProperty] private string _fileName = string.Empty;
    [ObservableProperty] private string _gameName = string.Empty;
    [ObservableProperty] private double _progressPercent;
    [ObservableProperty] private string _statusText = "Preparing upload...";
    [ObservableProperty] private bool _isUploading;
    [ObservableProperty] private bool _isComplete;
    [ObservableProperty] private bool _succeeded;
    [ObservableProperty] private string _fileSizeWarning = string.Empty;

    public DiscordUploadViewModel(IDiscordWebhookService service)
    {
        _service = service;
    }

    [RelayCommand]
    private async Task UploadAsync(ClipMetadata metadata)
    {
        FileName = metadata.FileName;
        GameName = metadata.GameName ?? "Unknown";
        IsUploading = true;
        var oldCts = _cts;
        _cts = new CancellationTokenSource();
        oldCts?.Cancel();
        oldCts?.Dispose();

        // Warn about file size (Discord default limit is 25MB)
        const long maxDefaultSize = 25 * 1024 * 1024;
        if (metadata.FileSizeBytes > maxDefaultSize)
        {
            var sizeMb = metadata.FileSizeBytes / (1024.0 * 1024.0);
            FileSizeWarning = $"File is {sizeMb:F1} MB — exceeds Discord's 25 MB default limit. Upload may fail unless the server is boosted.";
        }

        var progress = new Progress<double>(p =>
        {
            ProgressPercent = p;
            StatusText = $"Uploading... {p:F0}%";
        });

        try
        {
            StatusText = "Uploading...";
            var result = await _service.UploadClipAsync(metadata.FilePath, metadata, progress, _cts.Token);

            Succeeded = result.Success;
            StatusText = result.Success
                ? "Upload complete!"
                : result.ErrorMessage ?? "Upload failed.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Upload cancelled.";
            RequestClose?.Invoke();
            return;
        }
        catch (Exception ex)
        {
            StatusText = $"Upload failed: {ex.Message}";
        }
        finally
        {
            IsUploading = false;
            IsComplete = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
    }

    /// <summary>Raised when the dialog should close (upload finished or cancelled).</summary>
    public event Action? RequestClose;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
