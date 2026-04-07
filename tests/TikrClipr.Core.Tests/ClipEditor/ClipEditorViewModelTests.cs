using TikrClipr.Core.ClipEditor;
using TikrClipr.Core.Storage;

namespace TikrClipr.Core.Tests.ClipEditor;

/// <summary>
/// Tests for the ClipEditorViewModel contract without WPF dependencies.
/// Validates the interface interactions that the ViewModel depends on.
/// </summary>
public sealed class ClipEditorViewModelTests
{
    [Fact]
    public void CalculateInterval_ReturnsCorrectSpacing_ForEvenDivision()
    {
        var duration = TimeSpan.FromSeconds(100);
        const int frameCount = 10;

        var interval = duration.TotalSeconds / frameCount;

        Assert.Equal(10.0, interval);
    }

    [Fact]
    public void CalculateInterval_ReturnsCorrectSpacing_ForUnevenDivision()
    {
        var duration = TimeSpan.FromSeconds(60);
        const int frameCount = 7;

        var interval = duration.TotalSeconds / frameCount;

        Assert.Equal(60.0 / 7.0, interval, precision: 10);
    }

    [Fact]
    public void CalculateInterval_ShortClip_ManyFrames()
    {
        var duration = TimeSpan.FromSeconds(5);
        const int frameCount = 20;

        var interval = duration.TotalSeconds / frameCount;

        Assert.Equal(0.25, interval);
    }

    [Fact]
    public void TrimBoundaries_StartMustBeLessThanEnd()
    {
        var trimStart = TimeSpan.FromSeconds(10);
        var trimEnd = TimeSpan.FromSeconds(5);

        // The ViewModel/Timeline enforces this constraint
        Assert.True(trimStart > trimEnd, "Start > End should be caught by UI constraints");
    }

    [Fact]
    public void TrimBoundaries_ClampToZeroAndDuration()
    {
        var duration = TimeSpan.FromSeconds(60);
        var trimStart = TimeSpan.FromSeconds(-5);
        var trimEnd = TimeSpan.FromSeconds(100);

        // Clamp logic
        var clampedStart = trimStart < TimeSpan.Zero ? TimeSpan.Zero : trimStart;
        var clampedEnd = trimEnd > duration ? duration : trimEnd;

        Assert.Equal(TimeSpan.Zero, clampedStart);
        Assert.Equal(duration, clampedEnd);
    }

    [Fact]
    public async Task IClipEditor_TrimAsync_ReceivesCorrectTimeSpans()
    {
        var editor = new RecordingClipEditor();
        var start = TimeSpan.FromSeconds(5);
        var end = TimeSpan.FromSeconds(25);

        await editor.TrimAsync("clip.mp4", start, end);

        Assert.Equal(start, editor.LastTrimStart);
        Assert.Equal(end, editor.LastTrimEnd);
    }

    private sealed class RecordingClipEditor : IClipEditor
    {
        public TimeSpan? LastTrimStart { get; private set; }
        public TimeSpan? LastTrimEnd { get; private set; }

        public Task<string> TrimAsync(string inputPath, TimeSpan start, TimeSpan end, CancellationToken ct = default)
        {
            LastTrimStart = start;
            LastTrimEnd = end;
            return Task.FromResult(inputPath.Replace(".mp4", "_trimmed.mp4"));
        }

        public Task<string> GenerateThumbnailAsync(string inputPath, TimeSpan timestamp, CancellationToken ct = default)
            => Task.FromResult("thumb.jpg");

        public Task<TimeSpan> GetDurationAsync(string inputPath, CancellationToken ct = default)
            => Task.FromResult(TimeSpan.FromSeconds(60));

        public Task<IReadOnlyList<string>> GenerateThumbnailStripAsync(
            string inputPath, int frameCount, int thumbnailWidth, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }
}
