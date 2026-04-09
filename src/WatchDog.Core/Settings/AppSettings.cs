using WatchDog.Core.Audio;
using WatchDog.Core.Buffer;
using WatchDog.Core.Capture;
using WatchDog.Core.Highlights;
using WatchDog.Core.Recording;

namespace WatchDog.Core.Settings;

public sealed record AppSettings
{
    public CaptureConfig Capture { get; init; } = new();
    public BufferConfig Buffer { get; init; } = new();
    public SessionRecordingConfig Recording { get; init; } = new();
    public AudioMixConfig Audio { get; init; } = new();
    public HighlightConfig Highlight { get; init; } = new();
    public HotkeySettings Hotkey { get; init; } = new();
    public StorageSettings Storage { get; init; } = new();
    public DiscordSettings Discord { get; init; } = new();
    public List<CustomGameEntry> CustomGames { get; init; } = [];
    public bool DesktopCaptureEnabled { get; init; } = true;
    public bool StartWithWindows { get; init; } = false;
    public bool StartMinimized { get; init; } = true;
}

public sealed record DiscordSettings
{
    public string WebhookUrl { get; init; } = string.Empty;
    public string Username { get; init; } = "WatchDog";
    public string MessageTemplate { get; init; } = "{GameName} \u2014 {HighlightType}";
    public bool IncludeEmbed { get; init; } = true;
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
        "WatchDog");
    public int MaxStorageGb { get; init; } = 50;
    public int AutoDeleteDays { get; init; } = 30;
}

public sealed record CustomGameEntry
{
    public required string ExecutableName { get; init; }
    public required string DisplayName { get; init; }
}
