namespace WatchDog.Core.Updates;

public interface IUpdateChecker
{
    Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default);

    Task<string?> DownloadInstallerAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        CancellationToken ct = default);
}
