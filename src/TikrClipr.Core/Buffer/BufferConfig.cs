namespace TikrClipr.Core.Buffer;

public sealed record BufferConfig
{
    public int MaxSeconds { get; init; } = 120;      // 2 minutes default
    public int MaxSizeMb { get; init; } = 512;
    public string OutputDirectory { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "TikrClipr");
    public string FilenameFormat { get; init; } = "Clip %CCYY-%MM-%DD %hh-%mm-%ss";
}
