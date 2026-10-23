namespace WatchDog.Core.Updates;

/// <summary>
/// Runs one installer download at a time so the user can cancel it. Once <see cref="Cancel"/> returns
/// true, that download ends as <see cref="UpdateVerificationFailure.Cancelled"/> and never as a verified
/// installer: one that finished verifying just as the cancel arrived is deleted instead of returned.
/// </summary>
public sealed class UpdateDownloadSession
{
    private readonly object _sync = new();
    private CancellationTokenSource? _active;
    private bool _cancelRequested;

    public bool IsDownloading
    {
        get { lock (_sync) return _active is not null; }
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        IUpdateChecker checker,
        UpdateInfo update,
        IProgress<double>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(checker);
        ArgumentNullException.ThrowIfNull(update);

        CancellationTokenSource cts;
        lock (_sync)
        {
            if (_active is not null)
                throw new InvalidOperationException("An update download is already in progress.");
            _active = cts = new CancellationTokenSource();
            _cancelRequested = false;
        }

        UpdateDownloadResult? result = null;
        bool cancelled;
        try
        {
            result = await checker.DownloadVerifiedInstallerAsync(update, progress, cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            // Only Cancel() cancels this token, so this is reported as Cancelled below.
        }
        finally
        {
            // Under the lock so Cancel() never touches the token source while it is being disposed.
            lock (_sync)
            {
                cancelled = _cancelRequested;
                _active = null;
                cts.Dispose();
            }
        }

        if (!cancelled && result is not null)
            return result;

        DeleteInstaller(result);
        return UpdateDownloadResult.Refused(UpdateVerificationFailure.Cancelled);
    }

    /// <summary>
    /// Cancels the download in progress. True when there was one; it then ends as
    /// <see cref="UpdateVerificationFailure.Cancelled"/>.
    /// </summary>
    public bool Cancel()
    {
        lock (_sync)
        {
            if (_active is null) return false;
            _cancelRequested = true;
            try { _active.Cancel(); }
            catch (AggregateException)
            {
                // A cancellation callback failed. The download still ends as Cancelled, and throwing
                // here would break the Cancel button or window teardown instead.
            }
            return true;
        }
    }

    private static void DeleteInstaller(UpdateDownloadResult? result)
    {
        if (result?.InstallerPath is not { } path) return;

        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: StaleUpdateInstallerCleanup removes it on a later start.
        }
    }
}
