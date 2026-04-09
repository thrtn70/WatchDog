using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows.Forms;
using TikrClipr.Core.Audio;
using TikrClipr.Core.Capture;
using TikrClipr.Core.Hotkeys;
using TikrClipr.Core.Recording;
using TikrClipr.Core.Settings;

namespace TikrClipr.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IAudioDeviceEnumerator? _audioDeviceEnumerator;
    private AppSettings _settings;

    // Capture settings
    [ObservableProperty] private uint _outputWidth;
    [ObservableProperty] private uint _outputHeight;
    [ObservableProperty] private uint _fps;
    [ObservableProperty] private EncoderType _encoder;
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

    // Monitor selection
    [ObservableProperty] private ObservableCollection<MonitorInfo> _monitors = [];
    [ObservableProperty] private MonitorInfo? _selectedMonitor;

    // Highlight settings
    [ObservableProperty] private int _highlightDelaySeconds;
    [ObservableProperty] private int _highlightCooldownSeconds;

    // Desktop capture
    [ObservableProperty] private bool _desktopCaptureEnabled;

    // Recording mode
    [ObservableProperty] private RecordingMode _recordingMode;
    [ObservableProperty] private int _segmentDurationMinutes;
    [ObservableProperty] private int _maxDurationMinutes;

    // App settings
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;

    // Discord settings
    [ObservableProperty] private string _discordWebhookUrl = string.Empty;
    [ObservableProperty] private string _discordUsername = "TikrClipr";
    [ObservableProperty] private string _discordMessageTemplate = "{GameName} \u2014 {HighlightType}";
    [ObservableProperty] private bool _discordIncludeEmbed = true;

    [ObservableProperty] private string _statusMessage = string.Empty;

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

    public SettingsViewModel(ISettingsService settingsService, IAudioDeviceEnumerator? audioDeviceEnumerator = null)
    {
        _settingsService = settingsService;
        _audioDeviceEnumerator = audioDeviceEnumerator;
        _settings = settingsService.Load();
        LoadFromSettings(_settings);
    }

    [RelayCommand]
    private void Save()
    {
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
                MonitorIndex = SelectedMonitor?.Index ?? 0,
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

        _settingsService.Save(updated);

        // Apply startup registration when setting changes
        if (updated.StartWithWindows != _settings.StartWithWindows)
        {
            try { Services.StartupRegistration.SetStartWithWindows(updated.StartWithWindows); }
            catch { StatusMessage = "Settings saved, but startup registration failed."; }
        }

        _settings = updated;
        StatusMessage = "Settings saved. Restart to apply capture changes.";
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
        RateControl = settings.Capture.RateControl;
        Quality = settings.Capture.Quality;
        Bitrate = settings.Capture.Bitrate;

        // Load monitors
        LoadMonitors(settings.Capture.MonitorIndex);

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

    private void LoadMonitors(int selectedIndex)
    {
        var screens = Screen.AllScreens;
        var list = screens.Select((s, i) => new MonitorInfo(
            Index: i,
            DeviceName: s.DeviceName,
            DisplayName: $"Monitor {i + 1}: {s.Bounds.Width}x{s.Bounds.Height}" +
                         (s.Primary ? " (Primary)" : ""),
            Width: s.Bounds.Width,
            Height: s.Bounds.Height,
            IsPrimary: s.Primary
        )).ToList();

        Monitors = new ObservableCollection<MonitorInfo>(list);
        SelectedMonitor = list.ElementAtOrDefault(selectedIndex) ?? list.FirstOrDefault();
    }

    private void LoadAudioDevices(string desktopDeviceId, string micDeviceId)
    {
        if (_audioDeviceEnumerator is null) return;

        var outputs = _audioDeviceEnumerator.GetOutputDevices();
        DesktopDevices = new ObservableCollection<AudioDeviceInfo>(outputs);
        SelectedDesktopDevice = outputs.FirstOrDefault(d => d.DeviceId == desktopDeviceId)
                                ?? outputs.FirstOrDefault(d => d.IsDefault);

        var inputs = _audioDeviceEnumerator.GetInputDevices();
        MicDevices = new ObservableCollection<AudioDeviceInfo>(inputs);
        SelectedMicDevice = inputs.FirstOrDefault(d => d.DeviceId == micDeviceId)
                            ?? inputs.FirstOrDefault(d => d.IsDefault);
    }
}
