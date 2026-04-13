using System.Windows.Media;

namespace WatchDog.App.Themes;

/// <summary>
/// Single source of truth for WatchDog theme color hex values.
/// Mirrors the Color resources in WatchDogTheme.xaml for use in C# code
/// that cannot resolve XAML StaticResources (e.g. static frozen brushes,
/// string-typed binding properties).
/// </summary>
public static class ThemeColors
{
    // ── Semantic state colors ─────────────────────────────────────────
    public const string Accent = "#2EC4B6";
    public const string Success = "#38BF7F";
    public const string Warning = "#D9B84C";
    public const string Danger = "#D95555";
    public const string Info = "#4C96D9";
    public const string Overlay = "#6E889A";

    /// <summary>Creates a frozen <see cref="SolidColorBrush"/> from a hex string.</summary>
    public static SolidColorBrush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush(
            (Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
