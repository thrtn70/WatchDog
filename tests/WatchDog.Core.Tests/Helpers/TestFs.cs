namespace WatchDog.Core.Tests.Helpers;

/// <summary>
/// Filesystem helpers for tests. Windows can leave handles open briefly after
/// async writes complete, so directory cleanup needs short retry loops.
/// </summary>
internal static class TestFs
{
    /// <summary>
    /// Recursively delete a directory, retrying on IOException for up to ~500ms.
    /// IOException can happen on Windows when an async write's temp file is
    /// still being closed by the OS at the moment of deletion.
    /// </summary>
    public static void DeleteDirectoryWithRetry(string path)
    {
        if (!Directory.Exists(path)) return;

        const int maxAttempts = 10;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(50);
            }
        }
    }
}
