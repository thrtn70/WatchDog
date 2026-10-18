namespace WatchDog.Core.Storage;

public sealed record StorageConfig
{
    public IReadOnlyList<string> LegacyReadOnlyPaths { get; init; } = [];
    public string BasePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "WatchDog");
    public int MaxStorageGb { get; init; } = 50;
    /// <summary>Optional exact byte quota used by storage maintenance and deterministic tests.</summary>
    public long? MaxStorageBytesOverride { get; init; }
    public int AutoDeleteDays { get; init; } = 30;
    public long MinimumFreeSpaceReserveBytes { get; init; } = 5L * 1024 * 1024 * 1024;
    public long FreeSpaceRecoveryBytes { get; init; } = 6L * 1024 * 1024 * 1024;
    public TimeSpan MaintenanceInterval { get; init; } = TimeSpan.FromMinutes(1);
}
