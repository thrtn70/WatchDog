using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WatchDog.Core.Capture;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Highlights;
using WatchDog.Core.Events;
using WatchDog.Core.Sessions;
using WatchDog.Core.Settings;
using WatchDog.Core.Storage;
using WatchDog.Core.Updates;
using Microsoft.Extensions.DependencyInjection;

namespace WatchDog.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly IClipStorage _clipStorage;
    private readonly IClipEditor _clipEditorService;
    private readonly ICaptureEngine _captureEngine;
    private readonly ISessionRepository _sessionRepository;
    private readonly SessionManager _sessionManager;
    private readonly int _maxStorageGb;
    private readonly IDisposable _clipSavedSub;
    private readonly Action<CaptureState> _stateChangedHandler;
    private volatile bool _disposed;

    // Session-grouped view
    [ObservableProperty] private ObservableCollection<SessionGroupViewModel> _sessionGroups = [];
    [ObservableProperty] private SessionGroupViewModel? _selectedSession;
    [ObservableProperty] private bool _isSessionDetailView;

    // Flat clip view (used within session detail and for unsorted clips)
    [ObservableProperty] private ObservableCollection<ClipItemViewModel> _clips = [];
    [ObservableProperty] private ClipItemViewModel? _selectedClip;
    [ObservableProperty] private string _filterGame = "All Games";
    [ObservableProperty] private ObservableCollection<string> _gameNames = ["All Games"];
    [ObservableProperty] private ClipSortMode _sortMode = ClipSortMode.Newest;

    // Search & filter (Phase 5)
    private bool _suppressFilterRefresh;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private string _filterHighlightType = "All Types";
    [ObservableProperty] private string _filterTag = "All Tags";
    [ObservableProperty] private string _filterDuration = "Any Duration";
    [ObservableProperty] private ObservableCollection<string> _availableTags = ["All Tags"];
    [ObservableProperty] private ObservableCollection<string> _highlightTypes = ["All Types"];

    // Batch selection (Phase 5)
    [ObservableProperty] private int _selectedClipCount;
    [ObservableProperty] private bool _isBatchMode;

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
    [ObservableProperty] private bool _isRecording;

    // Status bar indicators — brush defaults are set in InitializeStatusBar()
    // to avoid calling FindResource at field-init time (crashes in unit tests).
    [ObservableProperty] private string _bufferStatusText = "Buffer OFF";
    [ObservableProperty] private System.Windows.Media.Brush? _bufferIndicatorBrush;
    [ObservableProperty] private string _currentGameName = "No game detected";
    [ObservableProperty] private System.Windows.Media.Brush? _gameIndicatorBrush;
    [ObservableProperty] private string _highlightStatusText = "No highlights";
    [ObservableProperty] private System.Windows.Media.Brush? _highlightStatusBrush;
    [ObservableProperty] private string _encoderInfoText = string.Empty;
    [ObservableProperty] private string _storageText = string.Empty;
    [ObservableProperty] private System.Windows.Media.Brush? _storageTextBrush;

    // Cached theme brushes for status bar (resolved once, reused on every state change)
    private System.Windows.Media.Brush? _successBrush;
    private System.Windows.Media.Brush? _dangerBrush;
    private System.Windows.Media.Brush? _accentBrush;
    private System.Windows.Media.Brush? _overlayBrush;
    private System.Windows.Media.Brush? _subtextBrush;
    private System.Windows.Media.Brush? _warningBrush;

    // App version (read once from assembly at startup)
    public string AppVersion { get; } = GetAppVersion();

    // Auto-update
    [ObservableProperty] private UpdateInfo? _availableUpdate;
    [ObservableProperty] private bool _isUpdateDownloading;
    [ObservableProperty] private double _updateDownloadProgress;

    public string UpdateBannerText =>
        AvailableUpdate is null ? string.Empty
        : AvailableUpdate.DisplayMessage ?? $"WatchDog {AvailableUpdate.LatestVersion} is available.";

    partial void OnAvailableUpdateChanged(UpdateInfo? value) =>
        OnPropertyChanged(nameof(UpdateBannerText));

    public MainWindowViewModel(
        IClipStorage clipStorage,
        IClipEditor clipEditor,
        ICaptureEngine captureEngine,
        IEventBus eventBus,
        ISessionRepository sessionRepository,
        SessionManager sessionManager,
        Core.Storage.StorageConfig storageConfig,
        AudioMixerViewModel? audioMixer = null,
        PerformanceViewModel? performance = null)
    {
        AudioMixer = audioMixer;
        Performance = performance;
        _clipStorage = clipStorage;
        _clipEditorService = clipEditor;
        _captureEngine = captureEngine;
        _sessionRepository = sessionRepository;
        _sessionManager = sessionManager;
        _maxStorageGb = storageConfig.MaxStorageGb;

        _clipSavedSub = eventBus.Subscribe<ClipSavedEvent>(e =>
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // Use session context from the event if available, otherwise look up current session
                    var sessionId = e.SessionId ?? _sessionManager.CurrentSessionId;
                    var matchNumber = e.MatchNumber;
                    await _clipStorage.IndexClipAsync(e.FilePath, e.Game?.DisplayName,
                        highlightType: null, sessionId, matchNumber);
                }
                catch { /* already indexed or missing file */ }

                Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(RefreshClips));
            });
        });

        _stateChangedHandler = state =>
            Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() => UpdateCaptureStatus(state)));
        captureEngine.StateChanged += _stateChangedHandler;

        InitializeStatusBarBrushes();
    }

    private void InitializeStatusBarBrushes()
    {
        if (Application.Current is null) return; // unit test safety

        _successBrush = Application.Current.TryFindResource("SuccessBrush") as System.Windows.Media.Brush;
        _dangerBrush = Application.Current.TryFindResource("DangerBrush") as System.Windows.Media.Brush;
        _accentBrush = Application.Current.TryFindResource("AccentBrush") as System.Windows.Media.Brush;
        _overlayBrush = Application.Current.TryFindResource("OverlayBrush") as System.Windows.Media.Brush;
        _subtextBrush = Application.Current.TryFindResource("SubtextBrush") as System.Windows.Media.Brush;
        _warningBrush = Application.Current.TryFindResource("WarningBrush") as System.Windows.Media.Brush;

        // Set initial values
        BufferIndicatorBrush = _dangerBrush;
        GameIndicatorBrush = _overlayBrush;
        HighlightStatusBrush = _overlayBrush;
        StorageTextBrush = _subtextBrush;
    }

    private bool _initialScanDone;

    [RelayCommand]
    private void RefreshClips()
    {
        if (!_initialScanDone)
        {
            _initialScanDone = true;
            IsScanning = true;
            _ = Task.Run(async () =>
            {
                try
                {
                    await _clipStorage.ScanAndIndexAsync();
                }
                catch { /* scan failure is non-fatal */ }
                finally
                {
                    Application.Current?.Dispatcher.InvokeAsync(() => PostToUi(() =>
                    {
                        IsScanning = false;
                        RefreshClips();
                    }));
                }
            });
        }

        // Load session groups
        LoadSessionGroups();

        // Also load flat clip list for the current view
        var allClips = FilterGame switch
        {
            "All Games" => _clipStorage.GetAllClips(),
            "\u2605 Favorites" => _clipStorage.GetAllClips().Where(c => c.IsFavorite).ToList(),
            _ => _clipStorage.GetClipsByGame(FilterGame),
        };

        // Apply additional filters (Phase 5)
        IEnumerable<ClipMetadata> filtered = allClips;

        // Text search (debounced by UI, applied here)
        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var search = SearchText.Trim();
            filtered = filtered.Where(c =>
                c.FileName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (c.GameName?.Contains(search, StringComparison.OrdinalIgnoreCase) == true) ||
                c.Tags.Any(t => t.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        // Highlight type filter
        if (FilterHighlightType != "All Types" && Enum.TryParse<HighlightType>(FilterHighlightType, out var ht))
        {
            filtered = filtered.Where(c => c.HighlightType == ht);
        }

        // Tag filter
        if (FilterTag != "All Tags")
        {
            filtered = filtered.Where(c =>
                c.Tags.Any(t => string.Equals(t, FilterTag, StringComparison.OrdinalIgnoreCase)));
        }

        // Duration filter
        filtered = FilterDuration switch
        {
            "Under 30s" => filtered.Where(c => c.Duration.TotalSeconds < 30),
            "30s\u20132min" => filtered.Where(c => c.Duration.TotalSeconds >= 30 && c.Duration.TotalMinutes < 2),
            "Over 2min" => filtered.Where(c => c.Duration.TotalMinutes >= 2),
            _ => filtered,
        };

        var sorted = SortMode switch
        {
            ClipSortMode.Newest => filtered.OrderByDescending(c => c.CreatedAt),
            ClipSortMode.Oldest => filtered.OrderBy(c => c.CreatedAt),
            ClipSortMode.Largest => filtered.OrderByDescending(c => c.FileSizeBytes),
            ClipSortMode.Longest => filtered.OrderByDescending(c => c.Duration),
            ClipSortMode.Name => filtered.OrderBy(c => c.FileName, StringComparer.OrdinalIgnoreCase),
            _ => filtered.OrderByDescending(c => c.CreatedAt),
        };

        Clips = new ObservableCollection<ClipItemViewModel>(
            sorted.Select(c => new ClipItemViewModel(c)));

        // Reset batch selection
        SelectedClipCount = 0;
        IsBatchMode = false;

        // Update game filter list (reuse allClips — avoid redundant storage read)
        var games = allClips
            .Select(c => c.GameName ?? "Unknown")
            .Distinct()
            .OrderBy(g => g)
            .ToList();

        GameNames = new ObservableCollection<string>(["All Games", "\u2605 Favorites", .. games]);

        var totalSize = allClips.Sum(c => c.FileSizeBytes);
        var sizeMb = totalSize / (1024.0 * 1024.0);
        StatusText = $"{allClips.Count} clips ({sizeMb:F0} MB)";
    }

    private void LoadSessionGroups()
    {
        // Load sessions and clips off the UI thread, then dispatch results
        var filterGame = FilterGame;
        _ = Task.Run(async () =>
        {
            try
            {
                var sessions = await _sessionRepository.GetRecentAsync(100);
                var allClips = _clipStorage.GetAllClips();
                Application.Current?.Dispatcher.InvokeAsync(() =>
                    PostToUi(() => BuildSessionGroups(sessions, allClips, filterGame)));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Session group load failed: {ex.Message}");
            }
        });
    }

    private void BuildSessionGroups(IReadOnlyList<GameSession> sessions, IReadOnlyList<ClipMetadata> allClips, string filterGame)
    {
        try
        {

            var groups = new List<SessionGroupViewModel>();

            foreach (var session in sessions)
            {
                // Filter by game if needed
                if (filterGame != "All Games" && filterGame != "\u2605 Favorites"
                    && !string.Equals(session.GameName, filterGame, StringComparison.OrdinalIgnoreCase))
                    continue;

                var sessionClips = allClips
                    .Where(c => c.SessionId == session.Id)
                    .OrderByDescending(c => c.CreatedAt)
                    .Select(c => new ClipItemViewModel(c))
                    .ToList();

                groups.Add(new SessionGroupViewModel(session, sessionClips));
            }

            // Add "Unsorted" group for clips without a session
            var unsortedClips = allClips
                .Where(c => c.SessionId is null)
                .OrderByDescending(c => c.CreatedAt)
                .Select(c => new ClipItemViewModel(c))
                .ToList();

            if (unsortedClips.Count > 0)
            {
                var unsortedSession = new GameSession
                {
                    Id = Guid.Empty,
                    GameName = "Unsorted",
                    GameExecutableName = "unsorted",
                    StartedAt = unsortedClips[^1].CreatedAt,
                    EndedAt = unsortedClips[0].CreatedAt,
                    Status = SessionStatus.Completed,
                };
                groups.Add(new SessionGroupViewModel(unsortedSession, unsortedClips));
            }

            SessionGroups = new ObservableCollection<SessionGroupViewModel>(groups);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError($"Session group build failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void OpenSession(SessionGroupViewModel? session)
    {
        if (session is null) return;
        SelectedSession = session;
        Clips = new ObservableCollection<ClipItemViewModel>(session.Clips);
        IsSessionDetailView = true;
    }

    [RelayCommand]
    private void BackToSessions()
    {
        SelectedSession = null;
        IsSessionDetailView = false;
        RefreshClips();
    }

    [RelayCommand]
    private async Task DeleteSessionAsync(SessionGroupViewModel? session)
    {
        if (session is null) return;

        var clipCount = session.ClipCount;
        var msg = clipCount > 0
            ? $"Delete session \"{session.GameName}\" and its {clipCount} clip(s)?\n\nClip files will be permanently deleted."
            : $"Delete session \"{session.GameName}\"?\n\nThis cannot be undone.";

        var result = MessageBox.Show(msg, "Delete Session",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
            return;

        try
        {
            // Delete all clips in the session
            var failures = new List<string>();
            foreach (var clip in session.Clips)
            {
                try { _clipStorage.DeleteClip(clip.FilePath); }
                catch { failures.Add(clip.FileName); }
            }

            if (failures.Count > 0)
            {
                MessageBox.Show(
                    $"Could not delete {failures.Count} clip file(s):\n{string.Join("\n", failures)}",
                    "Delete Session", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // Delete the session record (skip for the synthetic "Unsorted" group)
            if (session.SessionId != Guid.Empty)
                await _sessionRepository.DeleteAsync(session.SessionId);

            // If we were viewing this session's clips, go back
            if (SelectedSession == session)
            {
                SelectedSession = null;
                IsSessionDetailView = false;
            }

            RefreshClips();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not delete session: {ex.Message}", "Delete Failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    partial void OnFilterGameChanged(string value) { if (!_suppressFilterRefresh) RefreshClips(); }
    partial void OnSortModeChanged(ClipSortMode value) { if (!_suppressFilterRefresh) RefreshClips(); }
    partial void OnSearchTextChanged(string value) { if (!_suppressFilterRefresh) RefreshClips(); }
    partial void OnFilterHighlightTypeChanged(string value) { if (!_suppressFilterRefresh) RefreshClips(); }
    partial void OnFilterTagChanged(string value) { if (!_suppressFilterRefresh) RefreshClips(); }
    partial void OnFilterDurationChanged(string value) { if (!_suppressFilterRefresh) RefreshClips(); }

    [RelayCommand]
    private void ClearAllFilters()
    {
        _suppressFilterRefresh = true;
        SearchText = string.Empty;
        FilterGame = "All Games";
        FilterHighlightType = "All Types";
        FilterTag = "All Tags";
        FilterDuration = "Any Duration";
        _suppressFilterRefresh = false;
        RefreshClips();
    }

    partial void OnSelectedClipChanged(ClipItemViewModel? value)
    {
        if (value is not null)
        {
            ClipEditor = CreateClipEditor();
            _ = ClipEditor.LoadClipAsync(value.FilePath, value.GameName)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted)
                        System.Diagnostics.Trace.TraceError($"Clip load failed: {t.Exception?.InnerException?.Message}");
                }, TaskContinuationOptions.OnlyOnFaulted);
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
            SelectedClip = null;

        // Remove from the live observable collection so the UI updates instantly
        Clips.Remove(clip);

        // Rebuild session groups in the background (updates counts, etc.)
        LoadSessionGroups();
    }

    [RelayCommand]
    private void ToggleFavorite(ClipItemViewModel clip)
    {
        _clipStorage.ToggleFavorite(clip.FilePath);
        RefreshClips();
    }

    // ── Batch Operations (Phase 5) ──────────────────────────────

    [RelayCommand]
    private void ToggleClipSelection(ClipItemViewModel clip)
    {
        clip.IsSelected = !clip.IsSelected;
        SelectedClipCount = Clips.Count(c => c.IsSelected);
        IsBatchMode = SelectedClipCount > 0;
    }

    [RelayCommand]
    private void SelectAllClips()
    {
        foreach (var clip in Clips) clip.IsSelected = true;
        SelectedClipCount = Clips.Count;
        IsBatchMode = true;
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var clip in Clips) clip.IsSelected = false;
        SelectedClipCount = 0;
        IsBatchMode = false;
    }

    // Tag validation: alphanumeric, spaces, hyphens, dots, underscores, max 32 chars
    private static readonly System.Text.RegularExpressions.Regex TagPattern =
        new(@"^[\w\s\-\.]{1,32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

    [RelayCommand]
    private void BatchDeleteSelected()
    {
        var selected = Clips.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0) return;

        var result = MessageBox.Show(
            $"Delete {selected.Count} clip(s)? This cannot be undone.",
            "Confirm Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        var failures = new List<string>();
        foreach (var clip in selected)
        {
            try { _clipStorage.DeleteClip(clip.FilePath); }
            catch { failures.Add(clip.FileName); }
        }

        if (failures.Count > 0)
        {
            MessageBox.Show(
                $"Could not delete {failures.Count} file(s):\n{string.Join("\n", failures)}",
                "Batch Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        RefreshClips();
    }

    [RelayCommand]
    private void BatchTagSelected(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return;

        var trimmed = tag.Trim();
        if (!TagPattern.IsMatch(trimmed)) return;

        var selected = Clips.Where(c => c.IsSelected).ToList();
        foreach (var clip in selected)
        {
            _clipStorage.AddTags(clip.FilePath, [trimmed]);
        }

        RefreshClips();
    }

    [RelayCommand]
    private void AddTagToClip(ClipItemViewModel? clip)
    {
        // Placeholder — the actual tag input UI would call this with a tag string.
        // For now this is wired from the clip card "+" button.
        // Full implementation requires a tag picker popup (Phase 5 follow-up).
    }

    // ── Navigation ───────────────────────────────────────────

    [RelayCommand]
    private void OpenInExplorer(ClipItemViewModel? clip)
    {
        var target = clip ?? SelectedClip;
        if (target is null) return;

        if (File.Exists(target.FilePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { "/select,", target.FilePath },
                UseShellExecute = false,
            })?.Dispose();
        }
        else
        {
            var dir = Path.GetDirectoryName(target.FilePath);
            if (dir is not null && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    ArgumentList = { dir },
                    UseShellExecute = false,
                })?.Dispose();
            }
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
        IsRecording = (state is CaptureState.Buffering or CaptureState.Saving)
                      && !_captureEngine.IsDesktopCapture;
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

        // Status bar indicators (use cached brushes — no FindResource per call)
        var isBuffering = state is CaptureState.Buffering or CaptureState.Saving;

        // Buffer state
        BufferStatusText = isBuffering ? "Buffer ON" : "Buffer OFF";
        BufferIndicatorBrush = isBuffering ? _successBrush : _dangerBrush;

        // Game name
        if (_captureEngine.CurrentGame is { } game)
        {
            CurrentGameName = game.DisplayName;
            GameIndicatorBrush = _accentBrush;
        }
        else if (_captureEngine.IsDesktopCapture)
        {
            CurrentGameName = "Desktop";
            GameIndicatorBrush = _overlayBrush;
        }
        else
        {
            CurrentGameName = "No game detected";
            GameIndicatorBrush = _overlayBrush;
        }

        // Highlight status (placeholder — will be enriched in Phase 3 with AI detection)
        if (_captureEngine.CurrentGame is not null && !_captureEngine.IsDesktopCapture)
        {
            HighlightStatusText = "\u2713 Highlights";
            HighlightStatusBrush = _successBrush;
        }
        else
        {
            HighlightStatusText = "No highlights";
            HighlightStatusBrush = _overlayBrush;
        }

        // Encoder info
        var config = _captureEngine.Config;
        EncoderInfoText = $"{config.OutputWidth}x{config.OutputHeight} @ {config.Fps}fps";

        // Storage usage
        UpdateStorageStatus();
    }

    private void UpdateStorageStatus()
    {
        try
        {
            var clips = _clipStorage.GetAllClips();
            var totalBytes = clips.Sum(c => c.FileSizeBytes);
            var totalGb = totalBytes / (1024.0 * 1024.0 * 1024.0);
            var maxGb = _maxStorageGb;
            var usagePercent = maxGb > 0 ? totalGb / maxGb * 100 : 0;

            StorageText = $"{totalGb:F1} / {maxGb} GB";

            StorageTextBrush = usagePercent switch
            {
                > 95 => _dangerBrush,
                > 80 => _warningBrush,
                _ => _subtextBrush,
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[StatusBar] Storage status update failed: {ex.Message}");
            StorageText = string.Empty;
        }
    }

    // ── Auto-update ────────────────────────────────────────────────

    public void SetUpdateAvailable(UpdateInfo update) => AvailableUpdate = update;

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (AvailableUpdate is null) return;

        IsUpdateDownloading = true;
        try
        {
            var checker = App.Services.GetRequiredService<IUpdateChecker>();
            var progress = new Progress<double>(p => UpdateDownloadProgress = p);
            var installerPath = await checker.DownloadInstallerAsync(
                AvailableUpdate.DownloadUrl, progress);

            if (installerPath is null)
            {
                AvailableUpdate = null;
                return;
            }

            // Validate installer path is within expected temp directory before executing
            var allowedDir = Path.Combine(Path.GetTempPath(), "WatchDog-Update");
            var resolvedInstaller = Path.GetFullPath(installerPath);
            if (!resolvedInstaller.StartsWith(allowedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !resolvedInstaller.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Debug.WriteLine($"Installer path outside expected directory: {resolvedInstaller}");
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = resolvedInstaller,
                UseShellExecute = true,
                Arguments = "/SILENT /NORESTART",
            })?.Dispose();

            Application.Current.Shutdown(0);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Update install failed: {ex.Message}");
            MessageBox.Show(
                $"Could not download the update:\n\n{ex.Message}",
                "Update Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            IsUpdateDownloading = false;
        }
    }

    [RelayCommand]
    private void DismissUpdate() => AvailableUpdate = null;

    private static string GetAppVersion()
    {
        var version = Core.Updates.BuildInfo.GetVersionString();
        return version is not null ? $"v{version}" : "v?.?.?";
    }

    private void PostToUi(Action action)
    {
        if (_disposed) return;
        try { action(); }
        catch (Exception ex) { System.Diagnostics.Trace.TraceError($"MainWindow: {ex.Message}"); }
    }

    public void Dispose()
    {
        _captureEngine.StateChanged -= _stateChangedHandler;
        _clipSavedSub.Dispose();
        _disposed = true;
    }
}

public sealed class ClipItemViewModel : System.ComponentModel.INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;
    private bool _isSelected;

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

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
        Tags = metadata.Tags;
        HighlightType = metadata.HighlightType;
        HighlightSource = metadata.HighlightSource;
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
    public IReadOnlyList<string> Tags { get; }
    public HighlightType? HighlightType { get; }
    public string? HighlightSource { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string DurationDisplay => Duration.TotalHours >= 1
        ? Duration.ToString(@"h\:mm\:ss")
        : Duration.ToString(@"m\:ss");

    public string FileSizeDisplay => FileSizeBytes switch
    {
        >= 1024 * 1024 * 1024 => $"{FileSizeBytes / (1024.0 * 1024.0 * 1024.0):F1} GB",
        >= 1024 * 1024 => $"{FileSizeBytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{FileSizeBytes / 1024.0:F0} KB"
    };

    /// <summary>Abbreviated highlight badge text for thumbnail overlay.</summary>
    public string? HighlightBadgeText => HighlightType switch
    {
        WatchDog.Core.Highlights.HighlightType.Kill => "K",
        WatchDog.Core.Highlights.HighlightType.Ace => "ACE",
        WatchDog.Core.Highlights.HighlightType.Multikill => "MK",
        WatchDog.Core.Highlights.HighlightType.RoundWin => "RW",
        WatchDog.Core.Highlights.HighlightType.RoundLoss => "RL",
        WatchDog.Core.Highlights.HighlightType.MatchWin => "W",
        WatchDog.Core.Highlights.HighlightType.MatchLoss => "L",
        WatchDog.Core.Highlights.HighlightType.Death => "D",
        _ => null,
    };

    /// <summary>Badge color hex for thumbnail overlay.</summary>
    public string HighlightBadgeColor => HighlightType switch
    {
        WatchDog.Core.Highlights.HighlightType.Kill => Themes.ThemeColors.Danger,
        WatchDog.Core.Highlights.HighlightType.Ace => Themes.ThemeColors.Warning,
        WatchDog.Core.Highlights.HighlightType.Multikill => Themes.ThemeColors.Success,
        WatchDog.Core.Highlights.HighlightType.RoundWin => Themes.ThemeColors.Info,
        WatchDog.Core.Highlights.HighlightType.MatchWin => Themes.ThemeColors.Accent,
        _ => Themes.ThemeColors.Overlay,
    };

    public bool HasTags => Tags.Count > 0;

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
