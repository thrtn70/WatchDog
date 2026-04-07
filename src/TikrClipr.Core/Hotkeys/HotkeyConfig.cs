using System.Collections.Generic;

namespace TikrClipr.Core.Hotkeys;

public sealed record HotkeyConfig
{
    public int Id { get; init; }
    public uint Modifiers { get; init; }
    public uint VirtualKey { get; init; }
    public string Description { get; init; } = string.Empty;

    // Win32 modifier flags
    private const uint ModAlt = 0x0001;
    private const uint ModCtrl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;

    /// <summary>
    /// Formats a virtual key code and modifier flags into a human-readable string (e.g. "Ctrl + Shift + F9").
    /// </summary>
    public static string FormatDisplay(int virtualKey, uint modifiers)
    {
        var parts = new List<string>();

        if ((modifiers & ModCtrl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");

        var keyName = virtualKey switch
        {
            // Letters A-Z (0x41 - 0x5A)
            >= 0x41 and <= 0x5A => ((char)virtualKey).ToString(),

            // Digits 0-9 (0x30 - 0x39)
            >= 0x30 and <= 0x39 => ((char)virtualKey).ToString(),

            // Function keys F1-F24 (0x70 - 0x87)
            >= 0x70 and <= 0x87 => $"F{virtualKey - 0x6F}",

            // Numpad 0-9 (0x60 - 0x69)
            >= 0x60 and <= 0x69 => $"Num{virtualKey - 0x60}",

            // Common keys
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x1B => "Escape",
            0x20 => "Space",
            0x21 => "PageUp",
            0x22 => "PageDown",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            0x6A => "Num*",
            0x6B => "Num+",
            0x6D => "Num-",
            0x6E => "Num.",
            0x6F => "Num/",
            0xBE => ".",
            0xBC => ",",
            0xBD => "-",
            0xBB => "=",
            0xBA => ";",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",

            // Fallback to hex
            _ => $"0x{virtualKey:X2}",
        };

        parts.Add(keyName);
        return string.Join(" + ", parts);
    }
}
