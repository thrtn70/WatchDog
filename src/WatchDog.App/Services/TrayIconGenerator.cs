using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace WatchDog.App.Services;

/// <summary>
/// Generates tray icon variants by applying colored status dot overlays
/// to the base WatchDog icon. Icons are generated at app startup and cached to disk.
/// </summary>
public static class TrayIconGenerator
{
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WatchDog", "icon-cache");

    /// <summary>
    /// Ensures all tray icon variants exist in the cache directory.
    /// Creates them from the base icon if missing.
    /// Returns paths: (idle, buffering, recording, saving).
    /// </summary>
    public static (string Idle, string Buffering, string Recording, string Saving) EnsureIcons(
        string baseIconPath)
    {
        Directory.CreateDirectory(CacheDir);

        var idle = Path.Combine(CacheDir, "tray-idle.ico");
        var buffering = Path.Combine(CacheDir, "tray-buffering.ico");
        var recording = Path.Combine(CacheDir, "tray-recording.ico");
        var saving = Path.Combine(CacheDir, "tray-saving.ico");

        // Regenerate if any are missing
        if (!File.Exists(idle) || !File.Exists(buffering) ||
            !File.Exists(recording) || !File.Exists(saving))
        {
            using var baseIcon = new Icon(baseIconPath, 32, 32);
            using var baseBitmap = baseIcon.ToBitmap();

            SaveIconWithDot(baseBitmap, idle, Color.FromArgb(138, 158, 170));    // Gray — idle
            SaveIconWithDot(baseBitmap, buffering, Color.FromArgb(56, 191, 127)); // Green — buffering
            SaveIconWithDot(baseBitmap, recording, Color.FromArgb(217, 85, 85));  // Red — recording
            SaveIconWithDot(baseBitmap, saving, Color.FromArgb(217, 184, 76));    // Amber — saving
        }

        return (idle, buffering, recording, saving);
    }

    private static void SaveIconWithDot(Bitmap baseBitmap, string outputPath, Color dotColor)
    {
        using var bmp = new Bitmap(baseBitmap);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Draw a colored status dot in the bottom-right corner
        var dotSize = 10;
        var dotX = bmp.Width - dotSize - 2;
        var dotY = bmp.Height - dotSize - 2;

        // Dark outline for visibility
        using var outlineBrush = new SolidBrush(Color.FromArgb(10, 17, 20));
        g.FillEllipse(outlineBrush, dotX - 1, dotY - 1, dotSize + 2, dotSize + 2);

        // Colored dot
        using var dotBrush = new SolidBrush(dotColor);
        g.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);

        // Convert Bitmap to Icon and save.
        // Icon.FromHandle does NOT take ownership of the HICON — do not Dispose it.
        // DestroyIcon in the finally block is the sole owner of the handle.
        var iconHandle = bmp.GetHicon();
        try
        {
            var icon = Icon.FromHandle(iconHandle);
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            icon.Save(fs);
        }
        finally
        {
            DestroyIcon(iconHandle);
        }
    }

    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint handle);
}
