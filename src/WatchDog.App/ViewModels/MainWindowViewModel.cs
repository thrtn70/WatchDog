using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WatchDog.Core.Capture;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Events;
using WatchDog.Core.Settings;
using WatchDog.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace WatchDog.App.ViewModels;

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
    [ObservableProperty] private ClipSortMode _sortMode = ClipSortMode.Newest;

    // Clip editor (owns playback, trim, timeline state)
    [ObservableProperty] private ClipEditorViewModel? _clipEditor;

    // Audio mixer panel
    public AudioMixerViewModel? AudioMixer { get; }

    // Performance overlay
    public PerformanceViewModel? Performance { get; }

    // Status
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _captureStatusText = "Idle";
    [ObservableProperty] private bool _isScanning;

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

                try
                {
                    await Application.Current!.Dispatcher.InvokeAsync(RefreshClips);
                }
                catch { /* refresh failure is non-fatal */ }
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
            IsScanning = true;
            // Show already-indexed clips immediately, then scan for new ones in background
            Task.Run(async () =>
            {
                try
                {
                    await _clipStorage.ScanAndIndexAsync();
                }
                catch { /* scan failure is non-fatal — existing index is still usable */ }
                finally
                {
                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        IsScanning = false;
                        RefreshClips();
                    });
                }
            });
        }

        var allClips = FilterGame switch
        {
            "All Games" => _clipStorage.GetAllClips(),
            "\u2605 Favorites" => _clipStorage.GetAllClips().Where(c => c.IsFavorite).ToList(),
            _ => _clipStorage.GetClipsByGame(FilterGame),
        };

        var sorted = SortMode switch
        {
            ClipSortMode.Newest => allClips.OrderByDescending(c => c.CreatedAt),
            ClipSortMode.Oldest => allClips.OrderBy(c => c.CreatedAt),
            ClipSortMode.Largest => allClips.OrderByDescending(c => c.FileSizeBytes),
            ClipSortMode.Longest => allClips.OrderByDescending(c => c.Duration),
            ClipSortMode.Name => allClips.OrderBy(c => c.FileName, StringComparer.OrdinalIgnoreCase),
            _ => allClips.OrderByDescending(c => c.CreatedAt),
        };

        Clips = new ObservableCollection<ClipItemViewModel>(
            sorted.Select(c => new ClipItemViewModel(c)));

        // Update game filter list
        var games = _clipStorage.GetAllClips()
            .Select(c => c.GameName ?? "Unknown")
            .Distinct()
            .OrderBy(g => g)
            .ToList();

        GameNames = new ObservableCollection<string>(["All Games", "\u2605 Favorites", .. games]);

        var totalSize = allClips.Sum(c => c.FileSizeBytes);
        var sizeMb = totalSize / (1024.0 * 1024.0);
        StatusText = $"{allClips.Count} clips ({sizeMb:F0} MB)";
    }

    partial void OnFilterGameChanged(string value) => RefreshClips();
    partial void OnSortModeChanged(ClipSortMode value) => RefreshClips();

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
    private void DeleteClip(ClipItemViewModel? clip)
    {
        if (clip is null) return;

        var result = MessageBox.Show(
            $"Delete \"{clip.FileName}\"?\n\nThis cannot be undone.",
            "Delete Clip",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            _clipStorage.DeleteClip(clip.FilePath);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not delete clip: {ex.Message}", "Delete Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

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
        try
        {
            var target = clip ?? SelectedClip;
            if (target is null)
            {
                System.Windows.MessageBox.Show("No clip selected.", "Discord Share");
                return;
            }

            var settingsService = App.Services.GetRequiredService<ISettingsService>();
            var settings = settingsService.Load();
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
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to open Discord share dialog:\n\n{ex.Message}\n\n{ex.GetType().Name}",
                "Discord Share Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
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
