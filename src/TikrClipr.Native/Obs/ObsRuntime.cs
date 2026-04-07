namespace TikrClipr.Native.Obs;

/// <summary>
/// Resolves OBS Studio binary paths required by ObsKit.NET.
/// OBS binaries must be deployed alongside the application.
/// </summary>
public static class ObsRuntime
{
    private static readonly string AppDir = AppContext.BaseDirectory;

    public static string ObsDllPath => Path.Combine(AppDir, "obs.dll");
    public static string DataPath => Path.Combine(AppDir, "data", "libobs");
    public static string ModuleBinPath => Path.Combine(AppDir, "obs-plugins", "64bit");
    public static string ModuleDataPath => Path.Combine(AppDir, "data", "obs-plugins", "%module%");

    public static bool IsAvailable()
        => File.Exists(ObsDllPath)
           && Directory.Exists(DataPath)
           && Directory.Exists(Path.Combine(AppDir, "obs-plugins", "64bit"));

    public static string GetMissingComponentsMessage()
    {
        var missing = new List<string>();

        if (!File.Exists(ObsDllPath))
            missing.Add("obs.dll");
        if (!Directory.Exists(DataPath))
            missing.Add("data/libobs/ directory");
        if (!Directory.Exists(Path.Combine(AppDir, "obs-plugins", "64bit")))
            missing.Add("obs-plugins/64bit/ directory");

        return missing.Count == 0
            ? "All OBS components found."
            : $"Missing OBS components: {string.Join(", ", missing)}. Run tools/setup-obs-runtime.ps1 to download them.";
    }
}
