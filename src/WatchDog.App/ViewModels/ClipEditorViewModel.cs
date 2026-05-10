using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Storage;

namespace WatchDog.App.ViewModels;

public partial class ClipEditorViewModel : ObservableObject
{
    private readonly IClipEditor _clipEditor;
    private readonly IClipStorage _clipStorage;
    private string? _filePath;
    private string? _gameName;

    [ObservableProperty] private TimeSpan _duration;
    [ObservableProperty] private TimeSpan _position;
    [ObservableProperty] private TimeSpan _trimStart;
    [ObservableProperty] private TimeSpan _trimEnd;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private bool _isTrimming;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string _trimStatus = string.Empty;
    [ObservableProperty] private Uri? _source;
    [ObservableProperty] private List<string> _thumbnailFrames = [];

    public ClipEditorViewModel(IClipEditor clipEditor, IClipStorage clipStorage)
    {
        _clipEditor = clipEditor;
        _clipStorage = clipStorage;
    }

    public async Task LoadClipAsync(string filePath, string? gameName, CancellationToken ct = default)
    {
        _filePath = filePath;
        _gameName = gameName;
        Source = new Uri(filePath);
        var duration = await _clipEditor.GetDurationAsync(filePath, ct);
        Application.Current?.Dispatcher.Invoke(() =>
        {
            Duration = duration;
            TrimStart = TimeSpan.Zero;
            TrimEnd = duration;
            Position = TimeSpan.Zero;
            IsPlaying = false;
            IsTrimming = false;
            TrimStatus = string.Empty;
        });

        // Generate thumbnail strip in background
        try
        {
            var frames = await _clipEditor.GenerateThumbnailStripAsync(filePath, 20, 160, ct);
            Application.Current?.Dispatcher.Invoke(() => ThumbnailFrames = new List<string>(frames));
        }
        catch
        {
            Application.Current?.Dispatcher.Invoke(() => ThumbnailFrames = []);
        }
    }

    [RelayCommand]
    private void TogglePlayPause()
    {
        IsPlaying = !IsPlaying;
    }

    [RelayCommand]
    private void StartTrim()
    {
        IsTrimming = true;
        TrimStatus = string.Empty;
    }

    [RelayCommand]
    private async Task ExecuteTrimAsync()
    {
        if (_filePath is null) return;

        IsExporting = true;
        TrimStatus = "Trimming...";
        try
        {
            var trimStart = TrimStart;
            var trimEnd = TrimEnd;
            var result = await _clipEditor.TrimAsync(_filePath, trimStart, trimEnd);
            await _clipStorage.IndexClipAsync(result, _gameName ?? "Unknown");
            Application.Current?.Dispatcher.Invoke(() =>
            {
                TrimStatus = $"Saved: {Path.GetFileName(result)}";
                IsTrimming = false;
            });
        }
        catch (Exception ex)
        {
            Application.Current?.Dispatcher.Invoke(() => TrimStatus = $"Trim failed: {ex.Message}");
        }
        finally
        {
            Application.Current?.Dispatcher.Invoke(() => IsExporting = false);
        }
    }

    [RelayCommand]
    private void CancelTrim()
    {
        IsTrimming = false;
        TrimStatus = string.Empty;
    }
}
