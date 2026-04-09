using System.Runtime.InteropServices;

namespace WatchDog.Native.Win32;

public static partial class User32
{
    public const int WM_HOTKEY = 0x0312;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hWnd, int id);

    // Primary monitor resolution
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);

    public static (int Width, int Height) GetPrimaryMonitorResolution()
        => (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    // Modifier key constants
    public const uint MOD_NONE = 0x0000;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;
}
