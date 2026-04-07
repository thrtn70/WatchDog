using TikrClipr.Core.Hotkeys;

namespace TikrClipr.Core.Tests.Hotkeys;

public sealed class HotkeyConfigTests
{
    [Theory]
    [InlineData(0x70, 0u, "F1")]
    [InlineData(0x71, 0u, "F2")]
    [InlineData(0x78, 0u, "F9")]
    [InlineData(0x79, 0u, "F10")]
    [InlineData(0x7B, 0u, "F12")]
    [InlineData(0x87, 0u, "F24")]
    public void FormatDisplay_FunctionKeys_FormatsCorrectly(int virtualKey, uint modifiers, string expected)
    {
        var result = HotkeyConfig.FormatDisplay(virtualKey, modifiers);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0x41, 0u, "A")]
    [InlineData(0x5A, 0u, "Z")]
    [InlineData(0x4D, 0u, "M")]
    public void FormatDisplay_LetterKeys_FormatsCorrectly(int virtualKey, uint modifiers, string expected)
    {
        var result = HotkeyConfig.FormatDisplay(virtualKey, modifiers);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0x30, 0u, "0")]
    [InlineData(0x39, 0u, "9")]
    [InlineData(0x35, 0u, "5")]
    public void FormatDisplay_NumberKeys_FormatsCorrectly(int virtualKey, uint modifiers, string expected)
    {
        var result = HotkeyConfig.FormatDisplay(virtualKey, modifiers);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0x78, 0x0002u, "Ctrl + F9")]        // Ctrl + F9
    [InlineData(0x78, 0x0001u, "Alt + F9")]          // Alt + F9
    [InlineData(0x78, 0x0004u, "Shift + F9")]        // Shift + F9
    [InlineData(0x78, 0x0003u, "Ctrl + Alt + F9")]   // Ctrl+Alt + F9
    [InlineData(0x78, 0x0006u, "Ctrl + Shift + F9")] // Ctrl+Shift + F9
    [InlineData(0x78, 0x0005u, "Alt + Shift + F9")]  // Alt+Shift + F9
    [InlineData(0x78, 0x0007u, "Ctrl + Alt + Shift + F9")] // All three
    [InlineData(0x41, 0x000Au, "Ctrl + Win + A")]    // Ctrl+Win + A
    public void FormatDisplay_ModifierCombinations_FormatsCorrectly(int virtualKey, uint modifiers, string expected)
    {
        var result = HotkeyConfig.FormatDisplay(virtualKey, modifiers);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0x20, 0u, "Space")]
    [InlineData(0x0D, 0u, "Enter")]
    [InlineData(0x09, 0u, "Tab")]
    [InlineData(0x1B, 0u, "Escape")]
    [InlineData(0x08, 0u, "Backspace")]
    [InlineData(0x2E, 0u, "Delete")]
    [InlineData(0x2D, 0u, "Insert")]
    [InlineData(0x24, 0u, "Home")]
    [InlineData(0x23, 0u, "End")]
    [InlineData(0x21, 0u, "PageUp")]
    [InlineData(0x22, 0u, "PageDown")]
    [InlineData(0x25, 0u, "Left")]
    [InlineData(0x26, 0u, "Up")]
    [InlineData(0x27, 0u, "Right")]
    [InlineData(0x28, 0u, "Down")]
    public void FormatDisplay_SpecialKeys_FormatsCorrectly(int virtualKey, uint modifiers, string expected)
    {
        var result = HotkeyConfig.FormatDisplay(virtualKey, modifiers);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0x60, 0u, "Num0")]
    [InlineData(0x69, 0u, "Num9")]
    [InlineData(0x6A, 0u, "Num*")]
    [InlineData(0x6B, 0u, "Num+")]
    [InlineData(0x6D, 0u, "Num-")]
    [InlineData(0x6E, 0u, "Num.")]
    [InlineData(0x6F, 0u, "Num/")]
    public void FormatDisplay_NumpadKeys_FormatsCorrectly(int virtualKey, uint modifiers, string expected)
    {
        var result = HotkeyConfig.FormatDisplay(virtualKey, modifiers);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatDisplay_DefaultSaveClipHotkey_ReturnsF9()
    {
        // Default: F9 (0x78) with no modifiers
        var result = HotkeyConfig.FormatDisplay(0x78, 0);

        Assert.Equal("F9", result);
    }

    [Fact]
    public void FormatDisplay_DefaultToggleRecordingHotkey_ReturnsF10()
    {
        // Default: F10 (0x79) with no modifiers
        var result = HotkeyConfig.FormatDisplay(0x79, 0);

        Assert.Equal("F10", result);
    }

    [Theory]
    [InlineData(0x01, 0u, "0x01")]   // VK_LBUTTON - unusual key
    [InlineData(0xFF, 0u, "0xFF")]   // Out of range
    public void FormatDisplay_UnknownKeys_FallsBackToHex(int virtualKey, uint modifiers, string expected)
    {
        var result = HotkeyConfig.FormatDisplay(virtualKey, modifiers);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatDisplay_CtrlShiftA_FormatsWithModifiersFirst()
    {
        // Modifiers: Ctrl (0x0002) + Shift (0x0004) = 0x0006
        var result = HotkeyConfig.FormatDisplay(0x41, 0x0006);

        Assert.Equal("Ctrl + Shift + A", result);
        Assert.StartsWith("Ctrl", result);
    }

    [Theory]
    [InlineData(0xBA, 0u, ";")]
    [InlineData(0xBB, 0u, "=")]
    [InlineData(0xBC, 0u, ",")]
    [InlineData(0xBD, 0u, "-")]
    [InlineData(0xBE, 0u, ".")]
    [InlineData(0xBF, 0u, "/")]
    [InlineData(0xC0, 0u, "`")]
    [InlineData(0xDB, 0u, "[")]
    [InlineData(0xDC, 0u, "\\")]
    [InlineData(0xDD, 0u, "]")]
    [InlineData(0xDE, 0u, "'")]
    public void FormatDisplay_PunctuationKeys_FormatsCorrectly(int virtualKey, uint modifiers, string expected)
    {
        var result = HotkeyConfig.FormatDisplay(virtualKey, modifiers);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatDisplay_WinModifier_IncludesWin()
    {
        // Win modifier = 0x0008
        var result = HotkeyConfig.FormatDisplay(0x41, 0x0008);

        Assert.Equal("Win + A", result);
    }
}
