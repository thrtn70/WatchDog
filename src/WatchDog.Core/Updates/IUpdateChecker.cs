namespace WatchDog.Core.Updates;

public interface IUpdateChecker
{
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);

    /// <summary>
    /// Downloads the release installer and returns its path only when its bytes match the
    /// release's update manifest signed by the pinned release key; any refusal leaves nothing on disk.
    /// </summary>
    Task<UpdateDownloadResult> DownloadVerifiedInstallerAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
