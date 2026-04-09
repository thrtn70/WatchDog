namespace WatchDog.Core.Storage;

public sealed record StorageConfig
{
    public string BasePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "WatchDog");
    public int MaxStorageGb { get; init; } = 50;
    public int AutoDeleteDays { get; init; } = 30;
}
