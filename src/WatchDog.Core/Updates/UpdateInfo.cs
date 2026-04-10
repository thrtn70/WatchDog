namespace WatchDog.Core.Updates;

public sealed record UpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    string DownloadUrl,
    string ReleaseNotesUrl,
    bool IsUpdateAvailable);
