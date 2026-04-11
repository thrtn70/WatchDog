using WatchDog.Core.GameDetection;

namespace WatchDog.Core.Capture;

/// <summary>
/// Represents what WatchDog is currently capturing and how it was initiated.
/// All capture modes (auto-detected games, manual window selection, desktop)
/// flow through this unified model.
/// </summary>
public enum CaptureSourceKind
{
    /// <summary>Game was auto-detected via ProcessGameDetector.</summary>
    Auto,

    /// <summary>User manually selected a window to capture.</summary>
    Manual,

    /// <summary>Desktop/monitor fallback capture.</summary>
    Desktop,
}

public sealed record CaptureSource(
    CaptureSourceKind Kind,
    string ExecutableName,
    string DisplayName,
    int ProcessId,
    IntPtr? WindowHandle,
    string? WindowClass,
    GameGenre Genre,
    DateTimeOffset InitiatedAt)
{
    /// <summary>Creates a CaptureSource from an auto-detected game.</summary>
    public static CaptureSource FromGame(GameInfo game) => new(
        Kind: CaptureSourceKind.Auto,
        ExecutableName: game.ExecutableName,
        DisplayName: game.DisplayName,
        ProcessId: game.ProcessId,
        WindowHandle: null,
        WindowClass: null,
        Genre: game.Genre,
        InitiatedAt: DateTimeOffset.UtcNow);

    /// <summary>Creates a CaptureSource from a manually selected window.</summary>
    public static CaptureSource FromWindow(
        string executableName,
        string windowTitle,
        int processId,
        IntPtr windowHandle,
        string? windowClass) => new(
        Kind: CaptureSourceKind.Manual,
        ExecutableName: executableName,
        DisplayName: windowTitle,
        ProcessId: processId,
        WindowHandle: windowHandle,
        WindowClass: windowClass,
        Genre: GameGenre.Unknown,
        InitiatedAt: DateTimeOffset.UtcNow);

    /// <summary>Creates a desktop capture source.</summary>
    public static CaptureSource Desktop() => new(
        Kind: CaptureSourceKind.Desktop,
        ExecutableName: "desktop",
        DisplayName: "Desktop",
        ProcessId: 0,
        WindowHandle: null,
        WindowClass: null,
        Genre: GameGenre.Unknown,
        InitiatedAt: DateTimeOffset.UtcNow);

    /// <summary>Converts to a GameInfo for compatibility with existing event/session APIs.</summary>
    public GameInfo ToGameInfo() => new()
    {
        ExecutableName = ExecutableName,
        DisplayName = DisplayName,
        ProcessId = ProcessId,
        WindowTitle = DisplayName,
        Genre = Genre,
    };
}
