using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Settings;

namespace WatchDog.App.ViewModels;

public partial class GameProfilesViewModel : ObservableObject
{
    private readonly GameDatabase _gameDatabase;
    private readonly ISettingsService _settingsService;
    private AppSettings _settings;

    [ObservableProperty] private ObservableCollection<GameProfileEntry> _allGames = [];
    [ObservableProperty] private ObservableCollection<GameProfileEntry> _filteredGames = [];
    [ObservableProperty] private GameProfileEntry? _selectedGame;
    [ObservableProperty] private string _searchText = string.Empty;

    // Profile editor fields (bound to the right panel)
    [ObservableProperty] private GameRecordingMode _selectedMode = GameRecordingMode.ReplayBuffer;
    [ObservableProperty] private bool _useDefaultResolution = true;
    [ObservableProperty] private int _outputWidth = 1920;
    [ObservableProperty] private int _outputHeight = 1080;
    [ObservableProperty] private bool _useDefaultBitrate = true;
    [ObservableProperty] private int _bitrateKbps = 6000;
    [ObservableProperty] private bool _useDefaultBufferDuration = true;
    [ObservableProperty] private int _bufferDurationSeconds = 120;
    [ObservableProperty] private float _highlightSensitivity = 0.6f;
    [ObservableProperty] private bool _useDefaultHighlightSensitivity = true;
    [ObservableProperty] private bool _autoRecord = true;
    [ObservableProperty] private string _profileStatus = string.Empty;
    [ObservableProperty] private bool _hasProfile;

    public GameProfilesViewModel(
        GameDatabase gameDatabase,
        ISettingsService settingsService,
        AppSettings settings)
    {
        _gameDatabase = gameDatabase;
        _settingsService = settingsService;
        _settings = settings;

        LoadGames();
    }

    private void LoadGames()
    {
        var allDbGames = _gameDatabase.GetAllGames();
        var profiles = _settings.GameProfiles.GroupBy(p => p.GameExecutableName, StringComparer.OrdinalIgnoreCase).ToDictionary(
            group => group.Key,
            group => group.First(),
            StringComparer.OrdinalIgnoreCase);

        var entries = allDbGames.Select(g => new GameProfileEntry
        {
            ExecutableName = g.ExecutableName,
            DisplayName = g.DisplayName,
            Genre = g.Genre,
            HasProfile = profiles.ContainsKey(g.ExecutableName),
            SupportsHighlights = GenreClassification.SupportsHighlights(g.Genre),
        })
        .OrderByDescending(g => g.HasProfile) // Configured first
        .ThenBy(g => g.DisplayName)
        .ToList();

        AllGames = new ObservableCollection<GameProfileEntry>(entries);
        ApplyFilter();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        if (string.IsNullOrWhiteSpace(SearchText))
        {
            FilteredGames = new ObservableCollection<GameProfileEntry>(AllGames);
        }
        else
        {
            var search = SearchText.Trim();
            FilteredGames = new ObservableCollection<GameProfileEntry>(
                AllGames.Where(g =>
                    g.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    g.ExecutableName.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }
    }

    partial void OnSelectedGameChanged(GameProfileEntry? value)
    {
        if (value is null) return;

        var profile = _settings.GameProfiles
            .FirstOrDefault(p => string.Equals(p.GameExecutableName, value.ExecutableName,
                StringComparison.OrdinalIgnoreCase));

        if (profile is not null)
        {
            HasProfile = true;
            SelectedMode = profile.Mode;
            UseDefaultResolution = profile.OutputWidth is null;
            OutputWidth = profile.OutputWidth ?? (int)_settings.Capture.OutputWidth;
            OutputHeight = profile.OutputHeight ?? (int)_settings.Capture.OutputHeight;
            UseDefaultBitrate = profile.BitrateKbps is null;
            BitrateKbps = profile.BitrateKbps ?? _settings.Capture.Bitrate;
            UseDefaultBufferDuration = profile.BufferDurationSeconds is null;
            BufferDurationSeconds = profile.BufferDurationSeconds ?? _settings.Buffer.MaxSeconds;
            HighlightSensitivity = profile.HighlightSensitivity ?? 0.6f;
            UseDefaultHighlightSensitivity = profile.HighlightSensitivity is null;
            AutoRecord = profile.AutoRecord;
        }
        else
        {
            HasProfile = false;
            SelectedMode = GameRecordingMode.ReplayBuffer;
            UseDefaultResolution = true;
            OutputWidth = (int)_settings.Capture.OutputWidth;
            OutputHeight = (int)_settings.Capture.OutputHeight;
            UseDefaultBitrate = true;
            BitrateKbps = _settings.Capture.Bitrate;
            UseDefaultBufferDuration = true;
            BufferDurationSeconds = _settings.Buffer.MaxSeconds;
            HighlightSensitivity = 0.6f;
            UseDefaultHighlightSensitivity = true;
            AutoRecord = true;
        }
        ProfileStatus = GameCapturePolicy.Resolve(_settings, value.ExecutableName, _settings.Capture).Message;
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (SelectedGame is null) return;

        var profile = new GameRecordingProfile
        {
            GameExecutableName = SelectedGame.ExecutableName,
            Mode = SelectedMode,
            OutputWidth = UseDefaultResolution ? null : OutputWidth,
            OutputHeight = UseDefaultResolution ? null : OutputHeight,
            BitrateKbps = UseDefaultBitrate ? null : BitrateKbps,
            BufferDurationSeconds = UseDefaultBufferDuration ? null : BufferDurationSeconds,
            HighlightSensitivity = UseDefaultHighlightSensitivity ? null : HighlightSensitivity,
            AutoRecord = AutoRecord,
        }.Sanitized();

        _settings = _settingsService.Load();
        var existingProfiles = _settings.GameProfiles.ToList();
        existingProfiles.RemoveAll(p => string.Equals(p.GameExecutableName,
            SelectedGame.ExecutableName, StringComparison.OrdinalIgnoreCase));
        existingProfiles.Add(profile);

        var updatedSettings = _settings with { GameProfiles = existingProfiles.AsReadOnly() };
        _settingsService.Save(updatedSettings);
        _settings = updatedSettings;
        var decision = GameCapturePolicy.Resolve(updatedSettings, SelectedGame.ExecutableName, _settings.Capture);
        ProfileStatus = decision.Disposition == CapturePolicyDisposition.Ready
            ? "Profile saved for the next automatic start. The current recording is unchanged; resolution/FPS changes require restart."
            : $"Profile saved. {decision.Message}";

        HasProfile = true;
        SelectedGame.HasProfile = true;

        // Refresh the list to move the game to the "Configured" group
        LoadGames();
    }

    [RelayCommand]
    private void ResetProfile()
    {
        if (SelectedGame is null) return;

        _settings = _settingsService.Load();
        var existingProfiles = _settings.GameProfiles.ToList();
        existingProfiles.RemoveAll(p => string.Equals(p.GameExecutableName,
            SelectedGame.ExecutableName, StringComparison.OrdinalIgnoreCase));

        var updatedSettings = _settings with { GameProfiles = existingProfiles.AsReadOnly() };
        _settingsService.Save(updatedSettings);
        _settings = updatedSettings;

        HasProfile = false;
        SelectedGame.HasProfile = false;

        // Reset to defaults
        OnSelectedGameChanged(SelectedGame);
        LoadGames();
    }
}

public partial class GameProfileEntry : ObservableObject
{
    public required string ExecutableName { get; init; }
    public required string DisplayName { get; init; }
    public required GameGenre Genre { get; init; }
    [ObservableProperty] private bool _hasProfile;
    public bool SupportsHighlights { get; init; }

    public string GenreDisplay => Genre.ToString();
    public string StatusDisplay => HasProfile ? "Configured" : "Default";

    partial void OnHasProfileChanged(bool value) =>
        OnPropertyChanged(nameof(StatusDisplay));
}
