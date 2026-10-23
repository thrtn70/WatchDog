namespace WatchDog.Core.Updates;

/// <summary>
/// Startup cleanup for installers an earlier run left in the update download directory, for example
/// when WatchDog was killed mid-download. Best-effort: anything that can't be removed safely is left
/// for the next start.
/// </summary>
public static class StaleUpdateInstallerCleanup
{
    private const string InstallerPrefix = "WatchDog-Setup-";
    private const string InstallerExtension = ".exe";

    /// <summary>Installers written more recently than this are never deleted.</summary>
    public static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(10);

    /// <summary>Where <see cref="GitHubUpdateChecker"/> saves installers by default.</summary>
    public static string DefaultDirectory => Path.Combine(Path.GetTempPath(), "WatchDog-Update");

    /// <summary>
    /// Deletes the WatchDog-Setup-*.exe files directly inside <paramref name="directory"/> that haven't
    /// been written for <see cref="MinimumAge"/> and that nothing has open. Returns how many it deleted.
    /// </summary>
    public static int DeleteStaleInstallers(string directory, DateTime utcNow)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var deleted = 0;
        try
        {
            // Never follow a junction out of the download directory.
            var info = new DirectoryInfo(root);
            if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return 0;

            foreach (var file in info.EnumerateFiles(InstallerPrefix + "*" + InstallerExtension, SearchOption.TopDirectoryOnly))
            {
                if (TryDelete(root, file, utcNow))
                    deleted++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The directory became unreadable mid-scan; what is left waits for the next start.
        }

        return deleted;
    }

    private static bool TryDelete(string root, FileInfo file, DateTime utcNow)
    {
        // Same containment rule InstallUpdateAsync applies before it launches an installer.
        var path = Path.GetFullPath(file.FullName);
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !path.EndsWith(InstallerExtension, StringComparison.OrdinalIgnoreCase)
            || !file.Name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase)
            || file.Attributes.HasFlag(FileAttributes.ReparsePoint)
            || utcNow - file.LastWriteTimeUtc < MinimumAge)
        {
            return false;
        }

        try
        {
            // An exclusive delete-on-close open fails while anything else has the file: a download in
            // progress, LockForLaunch, or the running installer. Those installers are never deleted.
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
