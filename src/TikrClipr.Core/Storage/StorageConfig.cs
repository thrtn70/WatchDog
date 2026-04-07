namespace TikrClipr.Core.Storage;

public sealed record StorageConfig
{
    public string BasePath { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "TikrClipr");
    public int MaxStorageGb { get; init; } = 50;
    public int AutoDeleteDays { get; init; } = 30;
}
