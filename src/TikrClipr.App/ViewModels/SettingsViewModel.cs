using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikrClipr.Core.Capture;
using TikrClipr.Core.Hotkeys;
using TikrClipr.Core.Settings;

namespace TikrClipr.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
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

    // Desktop capture
    [ObservableProperty] private bool _desktopCaptureEnabled;

    // App settings
    [ObservableProperty] private bool _startWithWindows;
    [ObservableProperty] private bool _startMinimized;

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

    public SettingsViewModel(ISettingsService settingsService)
    {
        _settingsService = settingsService;
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
            Hotkey = _settings.Hotkey with
            {
                SaveClipKey = SaveClipKey,
                Modifiers = SaveClipModifiers,
                ToggleRecordingKey = ToggleRecordingKey,
                ToggleRecordingModifiers = ToggleRecordingModifiers,
            },
            DesktopCaptureEnabled = DesktopCaptureEnabled,
            StartWithWindows = StartWithWindows,
            StartMinimized = StartMinimized,
        };

        _settingsService.Save(updated);
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

        BufferSeconds = settings.Buffer.MaxSeconds;
        BufferMaxSizeMb = settings.Buffer.MaxSizeMb;

        SavePath = settings.Storage.SavePath;
        MaxStorageGb = settings.Storage.MaxStorageGb;
        AutoDeleteDays = settings.Storage.AutoDeleteDays;

        SaveClipKey = settings.Hotkey.SaveClipKey;
        SaveClipModifiers = settings.Hotkey.Modifiers;
        ToggleRecordingKey = settings.Hotkey.ToggleRecordingKey;
        ToggleRecordingModifiers = settings.Hotkey.ToggleRecordingModifiers;

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
}
