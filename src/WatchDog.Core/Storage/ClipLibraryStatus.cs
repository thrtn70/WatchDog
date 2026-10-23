namespace WatchDog.Core.Storage;

/// <summary>
/// Whether the active clip index could be loaded. While it cannot, WatchDog keeps capturing,
/// but every library mutation and cleanup is refused so that a partial index can never be
/// written over the user's favorites and tags.
/// </summary>
public sealed record ClipLibraryStatus(
    bool IsAvailable,
    string? Problem,
    IReadOnlyList<string> EvidencePaths,
    bool CanBuildNewIndex)
{
    public static ClipLibraryStatus Available { get; } = new(true, null, [], false);

    /// <summary>
    /// The index is usable, but a rebuild from the recordings folder has not finished, so some
    /// clips may still be missing from it.
    /// </summary>
    public static ClipLibraryStatus RebuildIncomplete { get; } = new(
        true,
        "A library rebuild did not finish, so some clips may be missing from the library.",
        [],
        false)
    {
        IsRebuildIncomplete = true,
    };

    public bool IsRebuildIncomplete { get; init; }
}

public sealed record ClipRecoveryScanResult(int Indexed, int Failed, string? FirstFailure);

public interface IClipLibraryHealth
{
    ClipLibraryStatus LibraryStatus { get; }
}

public interface IClipLibraryRecovery : IClipLibraryHealth
{
    /// <summary>
    /// Retries loading the active index. With <paramref name="allowNewIndex"/>, an index whose
    /// damaged bytes were all preserved as evidence is replaced by a new index. An index that
    /// could not be read, or a valid index that could not be repaired, is never replaced.
    /// </summary>
    ClipLibraryStatus ReloadLibrary(bool allowNewIndex);

    /// <summary>
    /// Indexes media under the active folder that is neither indexed, excluded nor protected,
    /// marking each clip as recovered: its favorite and tags were lost, so cleanup never deletes
    /// it. A scan without failures completes an unfinished rebuild.
    /// </summary>
    Task<ClipRecoveryScanResult> IndexRecoveredMediaAsync(
        IReadOnlySet<string> excludedPaths,
        CancellationToken ct = default);
}

public sealed class ClipLibraryUnavailableException : InvalidOperationException
{
    public ClipLibraryUnavailableException(string message) : base(message)
    {
    }
}
