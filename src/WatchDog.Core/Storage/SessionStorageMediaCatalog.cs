using WatchDog.Core.Sessions;

namespace WatchDog.Core.Storage;

/// <summary>
/// Projects the durable session media graph into storage ownership. Graph-owned screen and
/// camera files are not clips and must never be called orphans; those of sessions started by
/// v1.4.2 or later also consume quota.
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
        var countedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var session in recent)
        {
            // Sessions from v1.4.1 and older stay owned, so they are never called orphans, but are
            // not measured: they don't count toward the quota and can't fail the safety check.
            var counted = session.CountsTowardStorageLimit ? countedPaths : null;
            foreach (var segment in session.MediaSegments)
                AddPath(segment.FilePath, ownedPaths, counted);
            foreach (var path in session.RecordingPaths)
                AddPath(path, ownedPaths, counted);
        }

        long bytes = 0;
        var existingFiles = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<StorageCleanupFailure>();
        foreach (var path in countedPaths)
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

    private static void AddPath(string path, ISet<string> ownedPaths, ISet<string>? countedPaths)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var canonical = StoragePath.Canonicalize(path);
        ownedPaths.Add(canonical);
        countedPaths?.Add(canonical);
    }
}
