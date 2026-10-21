namespace WatchDog.Core.Storage;

/// <summary>
/// Builds explorer.exe argument strings. Paths are always quoted: ProcessStartInfo.ArgumentList
/// only quotes arguments that contain whitespace, and Explorer splits an unquoted path at a
/// comma, opening the wrong folder (GitHub #155). Explorer strips the quotes literally rather
/// than applying CommandLineToArgvW escaping, so a trailing backslash is left as-is.
/// </summary>
public static class ExplorerCommandLine
{
    /// <summary>Arguments that open the folder containing <paramref name="filePath"/> with it selected.</summary>
    public static string SelectFile(string filePath) => "/select," + Quote(filePath, nameof(filePath));

    /// <summary>Arguments that open <paramref name="folderPath"/>.</summary>
    public static string OpenFolder(string folderPath) => Quote(folderPath, nameof(folderPath));

    private static string Quote(string path, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, paramName);
        // Windows paths cannot contain '"'; one here would close the quoted argument early.
        if (path.Contains('"'))
            throw new ArgumentException("Path must not contain a double quote.", paramName);

        return $"\"{path}\"";
    }
}
