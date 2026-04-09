namespace WatchDog.Core.ClipEditor;

public interface IClipEditor
{
    Task<string> TrimAsync(string inputPath, TimeSpan start, TimeSpan end, CancellationToken ct = default);
    Task<string> GenerateThumbnailAsync(string inputPath, TimeSpan timestamp, CancellationToken ct = default);
    Task<TimeSpan> GetDurationAsync(string inputPath, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GenerateThumbnailStripAsync(
        string inputPath, int frameCount, int thumbnailWidth, CancellationToken ct = default);
}
