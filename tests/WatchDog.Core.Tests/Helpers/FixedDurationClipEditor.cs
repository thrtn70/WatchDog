using WatchDog.Core.ClipEditor;

namespace WatchDog.Core.Tests.Helpers;

/// <summary>
/// Clip editor for storage tests: every file lasts 30 seconds and thumbnails are unavailable,
/// so indexing never needs FFmpeg.
/// </summary>
internal sealed class FixedDurationClipEditor : IClipEditor
{
    /// <summary>Files for which reading the duration fails, as an unreadable recording would.</summary>
    public Func<string, bool> FailsFor { get; set; } = _ => false;

    public Task<string> TrimAsync(string inputPath, TimeSpan start, TimeSpan end, CancellationToken ct)
        => Task.FromResult(inputPath);

    public Task<string> GenerateThumbnailAsync(string inputPath, TimeSpan timestamp, CancellationToken ct)
        => Task.FromException<string>(new NotSupportedException("No ffmpeg in tests"));

    public Task<TimeSpan> GetDurationAsync(string inputPath, CancellationToken ct)
        => FailsFor(inputPath)
            ? Task.FromException<TimeSpan>(new InvalidDataException($"Could not read {Path.GetFileName(inputPath)}."))
            : Task.FromResult(TimeSpan.FromSeconds(30));

    public Task<IReadOnlyList<string>> GenerateThumbnailStripAsync(
        string inputPath, int frameCount, int thumbnailWidth, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
}
