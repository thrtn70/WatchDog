using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikrClipr.Core.Capture;
using TikrClipr.Core.ClipEditor;
using TikrClipr.Core.Events;
using TikrClipr.Core.Settings;
using TikrClipr.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace TikrClipr.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IClipStorage _clipStorage;
    private readonly IClipEditor _clipEditorService;
    private readonly ICaptureEngine _captureEngine;
    private readonly IDisposable _clipSavedSub;

    [ObservableProperty] private ObservableCollection<ClipItemViewModel> _clips = [];
    [ObservableProperty] private ClipItemViewModel? _selectedClip;
    [ObservableProperty] private string _filterGame = "All Games";
    [ObservableProperty] private ObservableCollection<string> _gameNames = ["All Games"];

    // Clip editor (owns playback, trim, timeline state)
    [ObservableProperty] private ClipEditorViewModel? _clipEditor;

    // Audio mixer panel
    public AudioMixerViewModel? AudioMixer { get; }

    // Performance overlay
    public PerformanceViewModel? Performance { get; }

    // Status
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _captureStatusText = "Idle";

    public MainWindowViewModel(
        IClipStorage clipStorage,
        IClipEditor clipEditor,
        ICaptureEngine captureEngine,
        IEventBus eventBus,
        AudioMixerViewModel? audioMixer = null,
        PerformanceViewModel? performance = null)
    {
        AudioMixer = audioMixer;
        Performance = performance;
        _clipStorage = clipStorage;
        _clipEditorService = clipEditor;
        _captureEngine = captureEngine;

        _clipSavedSub = eventBus.Subscribe<ClipSavedEvent>(e =>
        {
            // Auto-index the newly saved clip, then refresh the UI
            Task.Run(async () =>
            {
                try
                {
                    await _clipStorage.IndexClipAsync(e.FilePath, e.Game?.DisplayName);
                }
                catch { /* already indexed or missing file */ }
                Application.Current?.Dispatcher.Invoke(RefreshClips);
            });
        });

        captureEngine.StateChanged += state =>
            Application.Current?.Dispatcher.Invoke(() => UpdateCaptureStatus(state));
    }

    private bool _initialScanDone;

    [RelayCommand]
    private void RefreshClips()
    {
        if (!_initialScanDone)
        {
            _initialScanDone = true;
            // Scan disk for un-indexed clips (runs once on first open)
            Task.Run(async () =>
            {
                await _clipStorage.ScanAndIndexAsync();
                Application.Current?.Dispatcher.Invoke(RefreshClips);
            });
        }

        var allClips = FilterGame == "All Games"
            ? _clipStorage.GetAllClips()
            : _clipStorage.GetClipsByGame(FilterGame);

        Clips = new ObservableCollection<ClipItemViewModel>(
            allClips.Select(c => new ClipItemViewModel(c)));

        // Update game filter list
        var games = _clipStorage.GetAllClips()
            .Select(c => c.GameName ?? "Unknown")
            .Distinct()
            .OrderBy(g => g)
            .ToList();

        GameNames = new ObservableCollection<string>(["All Games", .. games]);

        var totalSize = allClips.Sum(c => c.FileSizeBytes);
        var sizeMb = totalSize / (1024.0 * 1024.0);
        StatusText = $"{allClips.Count} clips ({sizeMb:F0} MB)";
    }

    partial void OnFilterGameChanged(string value) => RefreshClips();

    partial void OnSelectedClipChanged(ClipItemViewModel? value)
    {
        if (value is not null)
        {
            ClipEditor = CreateClipEditor();
            _ = ClipEditor.LoadClipAsync(value.FilePath, value.GameName);
        }
        else
        {
            ClipEditor = null;
        }
    }

    private ClipEditorViewModel CreateClipEditor() => new(_clipEditorService, _clipStorage);

    [RelayCommand]
    private void PlayClip(ClipItemViewModel clip)
    {
        SelectedClip = clip;
        ClipEditor?.TogglePlayPauseCommand.Execute(null);
    }

    [RelayCommand]
    private void DeleteClip(ClipItemViewModel clip)
    {
        _clipStorage.DeleteClip(clip.FilePath);
        if (SelectedClip == clip)
        {
            SelectedClip = null;
        }
        RefreshClips();
    }

    [RelayCommand]
    private void ToggleFavorite(ClipItemViewModel clip)
    {
        _clipStorage.ToggleFavorite(clip.FilePath);
        RefreshClips();
    }

    [RelayCommand]
    private void OpenInExplorer(ClipItemViewModel? clip)
    {
        var target = clip ?? SelectedClip;
        if (target is null) return;

        if (File.Exists(target.FilePath))
        {
            Process.Start("explorer.exe", $"/select,\"{target.FilePath}\"");
        }
        else
        {
            var dir = Path.GetDirectoryName(target.FilePath);
            if (dir is not null && Directory.Exists(dir))
                Process.Start("explorer.exe", $"\"{dir}\"");
        }
    }

    [RelayCommand]
    private void ShareToDiscord(ClipItemViewModel? clip)
    {
        var target = clip ?? SelectedClip;
        if (target is null) return;

        var settings = App.Services.GetRequiredService<ISettingsService>().Load();
        if (string.IsNullOrWhiteSpace(settings.Discord.WebhookUrl))
        {
            System.Windows.MessageBox.Show(
                "No Discord webhook URL configured.\n\nGo to Settings > Discord to set one up.",
                "Discord Share",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
            return;
        }

        var dialog = new Views.DiscordUploadWindow(target.Metadata);
        dialog.Owner = System.Windows.Application.Current.MainWindow;
        dialog.ShowDialog();
    }

    private void UpdateCaptureStatus(CaptureState state)
    {
        CaptureStatusText = state switch
        {
            CaptureState.Idle => "Idle",
            CaptureState.Initializing => "Starting...",
            CaptureState.Buffering when _captureEngine.IsDesktopCapture => "Desktop Capture",
            CaptureState.Buffering => $"Recording {_captureEngine.CurrentGame?.DisplayName}",
            CaptureState.Saving => "Saving...",
            CaptureState.Stopping => "Stopping...",
            _ => state.ToString()
        };
    }

    public void Dispose() => _clipSavedSub.Dispose();
}

public sealed class ClipItemViewModel
{
    private BitmapImage? _thumbnail;

    public ClipItemViewModel(ClipMetadata metadata)
    {
        Metadata = metadata;
        FilePath = metadata.FilePath;
        FileName = metadata.FileName;
        GameName = metadata.GameName ?? "Unknown";
        CreatedAt = metadata.CreatedAt;
        Duration = metadata.Duration;
        FileSizeBytes = metadata.FileSizeBytes;
        ThumbnailPath = metadata.ThumbnailPath;
        IsFavorite = metadata.IsFavorite;
    }

    public ClipMetadata Metadata { get; }

    public string FilePath { get; }
    public string FileName { get; }
    public string GameName { get; }
    public DateTimeOffset CreatedAt { get; }
    public TimeSpan Duration { get; }
    public long FileSizeBytes { get; }
    public string? ThumbnailPath { get; }
    public bool IsFavorite { get; }

    public string DurationDisplay => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public string FileSizeDisplay => FileSizeBytes switch
    {
        >= 1024 * 1024 * 1024 => $"{FileSizeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB",
        >= 1024 * 1024 => $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{FileSizeBytes / 1024.0:F0} KB"
    };

    public string CreatedAtDisplay
    {
        get
        {
            var age = DateTimeOffset.UtcNow - CreatedAt;
            return age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes}m ago"
                : age.TotalHours < 24 ? $"{(int)age.TotalHours}h ago"
                : age.TotalDays < 7 ? $"{(int)age.TotalDays}d ago"
                : CreatedAt.LocalDateTime.ToString("MMM dd, yyyy");
        }
    }

    public BitmapImage? Thumbnail
    {
        get
        {
            if (_thumbnail is not null) return _thumbnail;
            if (ThumbnailPath is null || !File.Exists(ThumbnailPath)) return null;

            try
            {
                _thumbnail = new BitmapImage();
                _thumbnail.BeginInit();
                _thumbnail.UriSource = new Uri(ThumbnailPath);
                _thumbnail.DecodePixelWidth = 280;
                _thumbnail.CacheOption = BitmapCacheOption.OnLoad;
                _thumbnail.EndInit();
                _thumbnail.Freeze();
            }
            catch
            {
                _thumbnail = null;
            }

            return _thumbnail;
        }
    }
}
