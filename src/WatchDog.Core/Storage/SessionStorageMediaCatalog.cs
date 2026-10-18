using WatchDog.Core.Sessions;

namespace WatchDog.Core.Storage;

/// <summary>
/// Projects the durable session media graph into storage ownership. Graph-owned screen and
/// camera files are not clips, but they still consume quota and must never be called orphans.
/// </summary>
public sealed class SessionStorageMediaCatalog(
    ISessionRepository sessions,
    StorageConfig storageConfig) : IStorageMediaCatalog
{
    public async Task<StorageMediaCatalogSnapshot> SnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        var recent = await sessions.GetRecentAsync(int.MaxValue, cancellationToken).ConfigureAwait(false);
        var ownedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in recent)
        {
            foreach (var segment in session.MediaSegments)
                AddPath(segment.FilePath, ownedPaths);
            foreach (var path in session.RecordingPaths)
                AddPath(path, ownedPaths);
        }

        long bytes = 0;
        var existingFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<StorageCleanupFailure>();
        foreach (var path in ownedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsUnderActiveRoot(path)) continue;
            try
            {
                var file = new FileInfo(path);
                if (file.Exists)
                {
                    bytes = checked(bytes + file.Length);
                    existingFiles.Add(path, file.Length);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failures.Add(new StorageCleanupFailure(path, ex.Message));
            }
        }

        return new StorageMediaCatalogSnapshot(ownedPaths, bytes, failures.AsReadOnly())
        {
            ExistingFiles = existingFiles,
        };
    }

    private bool IsUnderActiveRoot(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageConfig.BasePath)) +
            Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPath(string path, ISet<string> ownedPaths)
    {
        if (!string.IsNullOrWhiteSpace(path))
            ownedPaths.Add(StoragePath.Canonicalize(path));
    }
}
