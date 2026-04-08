namespace TikrClipr.Core.Runtime;

/// <summary>
/// Resolves FFmpeg/FFprobe binary paths. Checks bundled location first,
/// then falls back to PATH. Mirrors the ObsRuntime pattern.
/// </summary>
public static class FFmpegRuntime
{
    private static readonly string AppDir = AppContext.BaseDirectory;
    private static readonly string FfmpegDir = Path.Combine(AppDir, "ffmpeg");

    public static string FfmpegPath => ResolveBinary("ffmpeg.exe", "ffmpeg");
    public static string FfprobePath => ResolveBinary("ffprobe.exe", "ffprobe");

    public static bool IsAvailable()
        => File.Exists(Path.Combine(FfmpegDir, "ffmpeg.exe"))
           || File.Exists(Path.Combine(AppDir, "ffmpeg.exe"))
           || IsOnPath("ffmpeg");

    public static string GetMissingComponentsMessage()
    {
        if (IsAvailable())
            return "FFmpeg found.";

        return "FFmpeg not found. Run tools/setup-ffmpeg.ps1 to download it, "
             + "or install FFmpeg and add it to your PATH.";
    }

    private static string ResolveBinary(string windowsName, string unixName)
    {
        // 1. Bundled in ffmpeg/ subdirectory
        var bundled = Path.Combine(FfmpegDir, windowsName);
        if (File.Exists(bundled))
            return bundled;

        // 2. Alongside the application
        var local = Path.Combine(AppDir, windowsName);
        if (File.Exists(local))
            return local;

        // 3. Fall back to PATH
        return OperatingSystem.IsWindows() ? windowsName : unixName;
    }

    private static bool IsOnPath(string binary)
    {
        var pathDirs = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator) ?? [];
        var ext = OperatingSystem.IsWindows() ? ".exe" : "";
        return pathDirs.Any(dir => File.Exists(Path.Combine(dir, binary + ext)));
    }
}
