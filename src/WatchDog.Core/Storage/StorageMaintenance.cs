namespace WatchDog.Core.Storage;

public interface IStorageMaintenance
{
    Task<StorageCleanupResult> RunCleanupAsync(CancellationToken ct = default);

    Task<StorageCleanupResult> RunCleanupAsync(
        long protectedExternalBytes,
        CancellationToken ct = default) => RunCleanupAsync(ct);

    Task<StorageCleanupResult> RunCleanupAsync(
        IReadOnlyDictionary<string, long> protectedExternalFiles,
        CancellationToken ct = default) => RunCleanupAsync(
            protectedExternalFiles.Values.Aggregate(0L, (total, size) => checked(total + size)),
            ct);
}

public sealed record StorageCleanupResult(
    int DeletedCount,
    long DeletedBytes,
    IReadOnlyList<StorageCleanupFailure> Failures,
    long RemainingIndexedBytes,
    bool QuotaMet,
    long ProtectedExternalBytes = 0)
{
    public long TotalTrackedBytes => checked(RemainingIndexedBytes + ProtectedExternalBytes);

    /// <summary>
    /// Set when cleanup did not run because the clip library is unavailable. Nothing was deleted
    /// and the quota could not be measured.
    /// </summary>
    public string? SuspendedReason { get; init; }

    public bool IsSuspended => SuspendedReason is not null;

    /// <summary>Clips restored by a library rebuild, which cleanup never deletes.</summary>
    public int RetainedRecoveredClips { get; init; }
}

public sealed record StorageCleanupFailure(
    string FilePath,
    string Message,
    bool AbortCleanup = false);

public sealed record StorageMediaCatalogSnapshot(
    IReadOnlySet<string> OwnedPaths,
    long ExistingBytes,
    IReadOnlyList<StorageCleanupFailure> Failures)
{
    public IReadOnlyDictionary<string, long> ExistingFiles { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
}

public interface IStorageMediaCatalog
{
    Task<StorageMediaCatalogSnapshot> SnapshotAsync(CancellationToken cancellationToken = default);
}

internal sealed class EmptyStorageMediaCatalog : IStorageMediaCatalog
{
    public Task<StorageMediaCatalogSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new StorageMediaCatalogSnapshot(
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            0,
            []));
}
