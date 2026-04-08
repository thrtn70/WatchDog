using TikrClipr.Core.Audio;
using TikrClipr.Core.Buffer;
using TikrClipr.Core.Capture;
using TikrClipr.Core.Recording;

namespace TikrClipr.Core.Settings;

public sealed record AppSettings
{
    public CaptureConfig Capture { get; init; } = new();
    public BufferConfig Buffer { get; init; } = new();
    public SessionRecordingConfig Recording { get; init; } = new();
    public AudioMixConfig Audio { get; init; } = new();
    public HotkeySettings Hotkey { get; init; } = new();
    public StorageSettings Storage { get; init; } = new();
    public List<CustomGameEntry> CustomGames { get; init; } = [];
    public bool DesktopCaptureEnabled { get; init; } = true;
    public bool StartWithWindows { get; init; } = false;
    public bool StartMinimized { get; init; } = true;
}

public sealed record HotkeySettings
{
    public int SaveClipKey { get; init; } = 0x78;                // F9
    public uint Modifiers { get; init; } = 0;                    // No modifiers
    public int ToggleRecordingKey { get; init; } = 0x79;         // F10
    public uint ToggleRecordingModifiers { get; init; } = 0;     // No modifiers
}

public sealed record StorageSettings
{
    public string SavePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "TikrClipr");
    public int MaxStorageGb { get; init; } = 50;
    public int AutoDeleteDays { get; init; } = 30;
}

public sealed record CustomGameEntry
{
    public required string ExecutableName { get; init; }
    public required string DisplayName { get; init; }
}
