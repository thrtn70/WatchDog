using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Storage;

public sealed record ClipLibraryRebuildResult(
    ClipLibraryStatus Status,
    int ClipsIndexedFromFolder,
    int PendingCapturesIndexed,
    string? FailureMessage)
{
    public bool Succeeded => Status.IsAvailable && FailureMessage is null;
}

public interface IClipLibraryRebuild
{
    /// <summary>
    /// Reloads the library and adds clips from the recordings folder. <paramref name="allowNewIndex"/>
    /// is what the user consented to: without it a damaged index is never replaced.
    /// </summary>
    Task<ClipLibraryRebuildResult> RebuildFromFolderAsync(bool allowNewIndex, CancellationToken ct = default);
}

/// <summary>
/// Restores an unavailable clip library after the user explicitly asks for it.
/// </summary>
public sealed class ClipLibraryRebuilder : IClipLibraryRebuild
{
    private readonly IClipLibraryRecovery _library;
    private readonly ICompletedMediaReconciliation _completedMedia;
    private readonly IStorageMediaCatalog _mediaCatalog;
    private readonly ILogger<ClipLibraryRebuilder> _logger;

    public ClipLibraryRebuilder(
        IClipLibraryRecovery library,
        ICompletedMediaReconciliation completedMedia,
        IStorageMediaCatalog mediaCatalog,
        ILogger<ClipLibraryRebuilder> logger)
    {
        _library = library;
        _completedMedia = completedMedia;
        _mediaCatalog = mediaCatalog;
        _logger = logger;
    }

    public async Task<ClipLibraryRebuildResult> RebuildFromFolderAsync(
        bool allowNewIndex,
        CancellationToken ct = default)
    {
        // Session recordings live in the same folder but are not clips. Without the session
        // catalog they cannot be told apart, so nothing is replaced or scanned.
        try
        {
            _ = await _mediaCatalog.SnapshotAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Clip library rebuild stopped because session recordings could not be identified");
            return Failed($"Session recordings could not be identified, so the folder was not scanned. {ex.Message}");
        }

        try
        {
            var status = await Task.Run(() => _library.ReloadLibrary(allowNewIndex), ct)
                .ConfigureAwait(false);
            if (!status.IsAvailable)
                return new ClipLibraryRebuildResult(status, 0, 0, status.Problem ?? "The clip library is still unavailable.");

            // Pending receipts go first: they carry the session and capture attribution that a
            // folder scan can only guess from the folder name.
            var pending = await _completedMedia.ReconcileWithLibraryAsync(ct).ConfigureAwait(false);
            // Capture kept running, so a session segment may have started since the first snapshot.
            var catalog = await _mediaCatalog.SnapshotAsync(ct).ConfigureAwait(false);
            var scan = await _library.IndexRecoveredMediaAsync(catalog.OwnedPaths, ct).ConfigureAwait(false);
            var settled = await _completedMedia.ReconcileWithLibraryAsync(ct).ConfigureAwait(false);
            var pendingIndexed = pending.PendingIndexed + settled.PendingIndexed;
            _logger.LogWarning(
                "Clip library rebuilt: {Pending} pending captures and {Scanned} folder clips indexed; {Failed} files failed",
                pendingIndexed,
                scan.Indexed,
                scan.Failed);
            var failure = scan.Failed > 0
                ? $"{scan.Failed} file(s) could not be added to the library. First problem: {scan.FirstFailure}"
                : null;
            return new ClipLibraryRebuildResult(_library.LibraryStatus, scan.Indexed, pendingIndexed, failure);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Clip library rebuild failed");
            return Failed(ex.Message);
        }
    }

    private ClipLibraryRebuildResult Failed(string message) =>
        new(_library.LibraryStatus, 0, 0, message);
}
