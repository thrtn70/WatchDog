using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Forms;
using System.Reflection;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Hotkeys;
using WatchDog.Core.Recording;
using WatchDog.Core.Settings;
using WatchDog.Core.Updates;
using WatchDog.Core.Webcam;
using WatchDog.App.Services;

namespace WatchDog.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly ICaptureEngine _captureEngine;
    private readonly IAudioDeviceEnumerator? _audioDeviceEnumerator;
    private readonly IUpdateChecker? _updateChecker;
    private readonly IWebcamDeviceEnumerator? _webcamDeviceEnumerator;
    private AppSettings _settings;

    // Capture settings
    [ObservableProperty] private uint _outputWidth;
    [ObservableProperty] private uint _outputHeight;
    [ObservableProperty] private uint _fps;
    [ObservableProperty] private EncoderType _encoder;
    public IReadOnlyList<EncoderOption> EncoderOptions { get; private set; } = EncoderCapabilities.ImplementedOptions;
    public string EncoderSelectionStatus => EncoderCapabilities.Describe(Encoder).Status;
    public string EncoderOutputBehavior => EncoderCapabilities.OutputBehavior;
    [ObservableProperty] private RateControlType _rateControl;
    [ObservableProperty] private int _quality;
    [ObservableProperty] private int _bitrate;

    // Buffer settings
    [ObservableProperty] private int _bufferSeconds;
    [ObservableProperty] private int _bufferMaxSizeMb;

    // Storage settings
    [ObservableProperty] private string _savePath = string.Empty;
    [ObservableProperty] private int _maxStorageGb;
    [ObservableProperty] private int _autoDeleteDays;

    // Audio settings
    [ObservableProperty] private float _desktopAudioVolume;
    [ObservableProperty] private float _micAudioVolume;
    [ObservableProperty] private bool _desktopAudioEnabled;
    [ObservableProperty] private bool _micAudioEnabled;
    [ObservableProperty] private bool _separateAudioTracks;

    // Audio device selection
    [ObservableProperty] private ObservableCollection<AudioDeviceInfo> _desktopDevices = [];
    [ObservableProperty] private AudioDeviceInfo? _selectedDesktopDevice;
    [ObservableProperty] private ObservableCollection<AudioDeviceInfo> _micDevices = [];
    [ObservableProperty] private AudioDeviceInfo? _selectedMicDevice;

    // Webcam selection is metadata-only here. Settings never open the camera.
    [ObservableProperty] private bool _webcamEnabledForManualRecording;
    [ObservableProperty] private ObservableCollection<WebcamDeviceInfo> _webcamDevices = [];
    [ObservableProperty] private WebcamDeviceInfo? _selectedWebcamDevice;
    [ObservableProperty] private int _webcamWidth;
    [ObservableProperty] private int _webcamHeight;
    [ObservableProperty] private double _webcamFramesPerSecond;
    [ObservableProperty] private string _webcamVideoFormat = string.Empty;
    [ObservableProperty] private int _webcamVideoBitrateKbps;
    [ObservableProperty] private string _webcamSelectionStatus = string.Empty;

    // Monitor selection
    [ObservableProperty] private ObservableCollection<MonitorInfo> _monitors = [];
    [ObservableProperty] private MonitorInfo? _selectedMonitor;

    // Highlight settings
    [ObservableProperty] private int _highlightDelaySeconds;
    [ObservableProperty] private int _highlightCooldownSeconds;

    // Desktop capture
    [ObservableProperty] private bool _desktopCaptureEnabled;

    // Recording mode
    [ObservableProperty] private Core.Recording.RecordingMode _recordingMode;
    [ObservableProperty] private int _segmentDurationMinutes;
    [ObservableProperty] private int _maxDurationMinutes;

    // App settings
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;

    // Discord settings
    [ObservableProperty] private string _discordWebhookUrl = string.Empty;
    [ObservableProperty] private string _discordUsername = "WatchDog";
    [ObservableProperty] private string _discordMessageTemplate = "{GameName} \u2014 {HighlightType}";
    [ObservableProperty] private bool _discordIncludeEmbed = true;

    [ObservableProperty] private string _statusMessage = string.Empty;

    public string SessionSegmentLivenessStatusText { get; }
    public bool SessionSegmentLivenessStatusIsWarning { get; }

    // Version & update
    [ObservableProperty] private string _updateStatusText = string.Empty;
    [ObservableProperty] private bool _isCheckingForUpdate;

    public string AppVersion { get; } = GetAppVersion();

    /// <summary>Sub-ViewModel for the Game Profiles tab.</summary>
    public GameProfilesViewModel? GameProfiles { get; }

    // Hotkey settings
    [ObservableProperty] private int _saveClipKey;
    [ObservableProperty] private uint _saveClipModifiers;
    [ObservableProperty] private int _toggleRecordingKey;
    [ObservableProperty] private uint _toggleRecordingModifiers;

    // Computed hotkey display strings
    public string SaveClipHotkeyDisplay =>
        HotkeyConfig.FormatDisplay(SaveClipKey, SaveClipModifiers);

    public string ToggleRecordingHotkeyDisplay =>
        HotkeyConfig.FormatDisplay(ToggleRecordingKey, ToggleRecordingModifiers);

    public SettingsViewModel(
        ISettingsService settingsService,
        ICaptureEngine captureEngine,
        SessionSegmentLivenessStartupDecision sessionSegmentLivenessDecision,
        IAudioDeviceEnumerator? audioDeviceEnumerator = null,
        IUpdateChecker? updateChecker = null,
        GameProfilesViewModel? gameProfiles = null,
        IWebcamDeviceEnumerator? webcamDeviceEnumerator = null)
    {
        _settingsService = settingsService;
        _captureEngine = captureEngine;
        var livenessStatus = SessionSegmentLivenessStatusPresenter.Present(
            sessionSegmentLivenessDecision);
        SessionSegmentLivenessStatusText = livenessStatus.Text;
        SessionSegmentLivenessStatusIsWarning = livenessStatus.IsWarning;
        _audioDeviceEnumerator = audioDeviceEnumerator;
        _updateChecker = updateChecker;
        _webcamDeviceEnumerator = webcamDeviceEnumerator;
        GameProfiles = gameProfiles;
        _settings = settingsService.Load();
        LoadFromSettings(_settings);
    }

    [RelayCommand]
    private void Save()
    {
        if (EncoderCapabilities.Validate(Encoder) is { } encoderFailure)
        {
            StatusMessage = encoderFailure.Message;
            return;
        }
        if (WebcamEnabledForManualRecording && SelectedWebcamDevice is null)
        {
            StatusMessage = "Choose an exact webcam before enabling camera recording.";
            return;
        }

        // Other settings panels may have saved profiles while this window was open.
        _settings = _settingsService.Load();
        var updated = _settings with
        {
            Capture = _settings.Capture with
            {
                OutputWidth = OutputWidth,
                OutputHeight = OutputHeight,
                Fps = Fps,
                Encoder = Encoder,
                RateControl = RateControl,
                Quality = Quality,
                Bitrate = Bitrate,
                MonitorIndex = SelectedMonitor?.Index ?? _settings.Capture.MonitorIndex,
                MonitorStableId = SelectedMonitor?.StableDisplayId ?? _settings.Capture.MonitorStableId,
            },
            Buffer = _settings.Buffer with
            {
                MaxSeconds = BufferSeconds,
                MaxSizeMb = BufferMaxSizeMb,
            },
            Storage = _settings.Storage with
            {
                SavePath = SavePath,
                MaxStorageGb = MaxStorageGb,
                AutoDeleteDays = AutoDeleteDays,
            },
            Recording = _settings.Recording with
            {
                Mode = RecordingMode,
                SegmentDurationMinutes = SegmentDurationMinutes,
                MaxDurationMinutes = MaxDurationMinutes,
            },
            Audio = _settings.Audio with
            {
                DesktopAudioEnabled = DesktopAudioEnabled,
                DesktopVolume = DesktopAudioVolume,
                DesktopDeviceId = SelectedDesktopDevice?.DeviceId ?? string.Empty,
                MicEnabled = MicAudioEnabled,
                MicVolume = MicAudioVolume,
                MicDeviceId = SelectedMicDevice?.DeviceId ?? string.Empty,
                SeparateAudioTracks = SeparateAudioTracks,
            },
            Webcam = _settings.Webcam with
            {
                EnabledForExplicitManualRecording = WebcamEnabledForManualRecording,
                DeviceId = SelectedWebcamDevice?.DeviceId ?? string.Empty,
                DisplayName = SelectedWebcamDevice?.DisplayName ?? string.Empty,
                Width = WebcamWidth,
                Height = WebcamHeight,
                FramesPerSecond = WebcamFramesPerSecond,
                VideoFormat = WebcamVideoFormat,
                VideoBitrateKbps = WebcamVideoBitrateKbps,
            },
            Highlight = _settings.Highlight with
            {
                PostEventDelaySeconds = HighlightDelaySeconds,
                CooldownSeconds = HighlightCooldownSeconds,
            },
            Hotkey = _settings.Hotkey with
            {
                SaveClipKey = SaveClipKey,
                Modifiers = SaveClipModifiers,
                ToggleRecordingKey = ToggleRecordingKey,
                ToggleRecordingModifiers = ToggleRecordingModifiers,
            },
            Discord = _settings.Discord with
            {
                WebhookUrl = DiscordWebhookUrl,
                Username = DiscordUsername,
                MessageTemplate = DiscordMessageTemplate,
                IncludeEmbed = DiscordIncludeEmbed,
            },
            DesktopCaptureEnabled = DesktopCaptureEnabled,
            StartWithWindows = StartWithWindows,
            StartMinimized = StartMinimized,
        };

        try
        {
            _settingsService.Save(updated);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Settings could not be saved: {ex.Message}";
            return;
        }

        StatusMessage = "Settings saved. Supported encoder, bitrate, replay duration and recording mode settings apply at the next automatic game start. Restart WatchDog for resolution/FPS, recording folder, file format, audio, camera, storage and manual recording changes. Camera access occurs only on a later explicit manual recording start.";

        // Apply startup registration when setting changes
        if (updated.StartWithWindows != _settings.StartWithWindows)
        {
            try { Services.StartupRegistration.SetStartWithWindows(updated.StartWithWindows); }
            catch { StatusMessage += " Startup registration failed."; }
        }

        _settings = _settingsService.Load();
    }

    [RelayCommand]
    private void BrowseSavePath()
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select clip save folder",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(SavePath) ? SavePath : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            SavePath = dialog.SelectedPath;
    }

    [RelayCommand]
    private void Reset()
    {
        var defaults = new AppSettings();
        LoadFromSettings(defaults);
        StatusMessage = "Reset to defaults. Click Save to apply.";
    }

    private void LoadFromSettings(AppSettings settings)
    {
        OutputWidth = settings.Capture.OutputWidth;
        OutputHeight = settings.Capture.OutputHeight;
        Fps = settings.Capture.Fps;
        Encoder = settings.Capture.Encoder;
        EncoderOptions = EncoderCapabilities.OptionsForSavedSelection(Encoder);
        OnPropertyChanged(nameof(EncoderOptions));
        RateControl = settings.Capture.RateControl;
        Quality = settings.Capture.Quality;
        Bitrate = settings.Capture.Bitrate;

        // Load monitors
        LoadMonitors(settings.Capture.MonitorIndex, settings.Capture.MonitorStableId);

        BufferSeconds = settings.Buffer.MaxSeconds;
        BufferMaxSizeMb = settings.Buffer.MaxSizeMb;

        SavePath = settings.Storage.SavePath;
        MaxStorageGb = settings.Storage.MaxStorageGb;
        AutoDeleteDays = settings.Storage.AutoDeleteDays;

        RecordingMode = settings.Recording.Mode;
        SegmentDurationMinutes = settings.Recording.SegmentDurationMinutes;
        MaxDurationMinutes = settings.Recording.MaxDurationMinutes;

        DesktopAudioVolume = settings.Audio.DesktopVolume;
        MicAudioVolume = settings.Audio.MicVolume;
        DesktopAudioEnabled = settings.Audio.DesktopAudioEnabled;
        MicAudioEnabled = settings.Audio.MicEnabled;
        SeparateAudioTracks = settings.Audio.SeparateAudioTracks;

        // Load audio devices
        LoadAudioDevices(settings.Audio.DesktopDeviceId, settings.Audio.MicDeviceId);

        WebcamEnabledForManualRecording = settings.Webcam.EnabledForExplicitManualRecording;
        WebcamWidth = settings.Webcam.Width;
        WebcamHeight = settings.Webcam.Height;
        WebcamFramesPerSecond = settings.Webcam.FramesPerSecond;
        WebcamVideoFormat = settings.Webcam.VideoFormat;
        WebcamVideoBitrateKbps = settings.Webcam.VideoBitrateKbps;
        LoadWebcamDevices(settings.Webcam.DeviceId, settings.Webcam.DisplayName);

        HighlightDelaySeconds = settings.Highlight.PostEventDelaySeconds;
        HighlightCooldownSeconds = settings.Highlight.CooldownSeconds;

        SaveClipKey = settings.Hotkey.SaveClipKey;
        SaveClipModifiers = settings.Hotkey.Modifiers;
        ToggleRecordingKey = settings.Hotkey.ToggleRecordingKey;
        ToggleRecordingModifiers = settings.Hotkey.ToggleRecordingModifiers;

        DiscordWebhookUrl = settings.Discord.WebhookUrl;
        DiscordUsername = settings.Discord.Username;
        DiscordMessageTemplate = settings.Discord.MessageTemplate;
        DiscordIncludeEmbed = settings.Discord.IncludeEmbed;

        DesktopCaptureEnabled = settings.DesktopCaptureEnabled;
        StartWithWindows = settings.StartWithWindows;
        StartMinimized = settings.StartMinimized;
    }

    partial void OnSaveClipKeyChanged(int value) =>
        OnPropertyChanged(nameof(SaveClipHotkeyDisplay));

    partial void OnEncoderChanged(EncoderType value) =>
        OnPropertyChanged(nameof(EncoderSelectionStatus));

    [RelayCommand]
    private void SelectSoftwareEncoder() => Encoder = EncoderType.X264;

    partial void OnSaveClipModifiersChanged(uint value) =>
        OnPropertyChanged(nameof(SaveClipHotkeyDisplay));

    partial void OnToggleRecordingKeyChanged(int value) =>
        OnPropertyChanged(nameof(ToggleRecordingHotkeyDisplay));

    partial void OnToggleRecordingModifiersChanged(uint value) =>
        OnPropertyChanged(nameof(ToggleRecordingHotkeyDisplay));

    [RelayCommand]
    private void RefreshAudioDevices()
    {
        LoadAudioDevices(SelectedDesktopDevice?.DeviceId ?? string.Empty,
                         SelectedMicDevice?.DeviceId ?? string.Empty);
    }

    private void LoadMonitors(int selectedIndex, string stableDisplayId)
    {
        var list = new DisplayCaptureCatalog(_captureEngine).GetMonitorsOrThrow();

        Monitors = new ObservableCollection<MonitorInfo>(list);
        SelectedMonitor = string.IsNullOrWhiteSpace(stableDisplayId)
            ? list.ElementAtOrDefault(selectedIndex) ?? list.FirstOrDefault()
            : list.FirstOrDefault(monitor => StringComparer.Ordinal.Equals(
                monitor.StableDisplayId,
                stableDisplayId));
    }

    private void LoadAudioDevices(string desktopDeviceId, string micDeviceId)
    {
        if (_audioDeviceEnumerator is null) return;

        var desktopSelection = AudioDeviceSelection.PreserveSavedSelection(
            _audioDeviceEnumerator.GetOutputDevices(),
            desktopDeviceId);
        DesktopDevices = new ObservableCollection<AudioDeviceInfo>(desktopSelection.Devices);
        SelectedDesktopDevice = desktopSelection.SelectedDevice;

        var microphoneSelection = AudioDeviceSelection.PreserveSavedSelection(
            _audioDeviceEnumerator.GetInputDevices(),
            micDeviceId);
        MicDevices = new ObservableCollection<AudioDeviceInfo>(microphoneSelection.Devices);
        SelectedMicDevice = microphoneSelection.SelectedDevice;
    }

    [RelayCommand]
    private void RefreshWebcamDevices()
    {
        LoadWebcamDevices(
            SelectedWebcamDevice?.DeviceId ?? _settings.Webcam.DeviceId,
            SelectedWebcamDevice?.DisplayName ?? _settings.Webcam.DisplayName);
    }

    private void LoadWebcamDevices(string savedDeviceId, string savedDisplayName)
    {
        if (_webcamDeviceEnumerator is null)
            return;

        try
        {
            var devices = _webcamDeviceEnumerator.GetPhysicalDevicesOrThrow().ToList();
            var selected = devices.FirstOrDefault(device =>
                StringComparer.Ordinal.Equals(device.DeviceId, savedDeviceId));
            if (selected is null && !string.IsNullOrWhiteSpace(savedDeviceId))
            {
                selected = new WebcamDeviceInfo(
                    savedDeviceId,
                    string.IsNullOrWhiteSpace(savedDisplayName) ? "Saved webcam" : savedDisplayName);
                devices.Add(selected);
                WebcamSelectionStatus = $"Unavailable — {selected.DisplayName}. Reselect it if its device identity changed.";
            }
            else
            {
                WebcamSelectionStatus = selected is null
                    ? "No webcam selected. Settings discovery does not open a camera."
                    : "Selected device is available. It remains closed until an explicit manual recording starts.";
            }

            WebcamDevices = new ObservableCollection<WebcamDeviceInfo>(devices);
            SelectedWebcamDevice = selected;
        }
        catch (Exception ex)
        {
            WebcamSelectionStatus = $"Webcam metadata could not be refreshed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CheckForUpdateAsync()
    {
        if (_updateChecker is null)
        {
            UpdateStatusText = "Update checking unavailable.";
            return;
        }

        IsCheckingForUpdate = true;
        UpdateStatusText = "Checking for updates...";

        try
        {
            var result = await _updateChecker.CheckForUpdateAsync();

            if (result is null)
            {
                UpdateStatusText = "Could not check for updates. Try again later.";
            }
            else if (result.IsUpdateAvailable)
            {
                var message = result.DisplayMessage ?? $"Update available: v{result.LatestVersion}.";
                UpdateStatusText = $"{message} Close settings and use the update banner to install.";
            }
            else if (Version.TryParse(result.LatestVersion, out var latest)
                     && Version.TryParse(result.CurrentVersion, out var current)
                     && latest > current)
            {
                // Newer version exists but installer isn't uploaded yet (CI still building)
                UpdateStatusText = $"v{result.LatestVersion} is available but the installer is still building. Try again in a few minutes.";
            }
            else
            {
                var suffix = Core.Updates.BuildInfo.IsPreRelease() ? "latest CI build" : "latest version";
                UpdateStatusText = $"You're on the {suffix} ({result.CurrentVersion}).";
            }
        }
        catch
        {
            UpdateStatusText = "Update check failed. Check your internet connection.";
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    private static string GetAppVersion()
    {
        var version = Core.Updates.BuildInfo.GetVersionString();
        return version is not null ? $"v{version}" : "v?.?.?";
    }
}
