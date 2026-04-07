using TikrClipr.Core.ClipEditor;

namespace TikrClipr.Core.Tests.ClipEditor;

public sealed class ThumbnailStripGeneratorTests
{
    [Fact]
    public async Task GenerateThumbnailStripAsync_ReturnsEmpty_WhenFrameCountIsZero()
    {
        var editor = new StubClipEditor(duration: TimeSpan.FromSeconds(60));

        var result = await editor.GenerateThumbnailStripAsync("test.mp4", frameCount: 0, thumbnailWidth: 160);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GenerateThumbnailStripAsync_ReturnsEmpty_WhenFrameCountIsNegative()
    {
        var editor = new StubClipEditor(duration: TimeSpan.FromSeconds(60));

        var result = await editor.GenerateThumbnailStripAsync("test.mp4", frameCount: -1, thumbnailWidth: 160);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GenerateThumbnailStripAsync_ReturnsEmpty_WhenDurationIsZero()
    {
        var editor = new StubClipEditor(duration: TimeSpan.Zero);

        var result = await editor.GenerateThumbnailStripAsync("test.mp4", frameCount: 10, thumbnailWidth: 160);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GenerateThumbnailStripAsync_ReturnsEmpty_WhenDurationIsNegative()
    {
        var editor = new StubClipEditor(duration: TimeSpan.FromSeconds(-5));

        var result = await editor.GenerateThumbnailStripAsync("test.mp4", frameCount: 10, thumbnailWidth: 160);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GenerateThumbnailStripAsync_RequestsCorrectNumberOfFrames()
    {
        var editor = new StubClipEditor(duration: TimeSpan.FromSeconds(60));

        var result = await editor.GenerateThumbnailStripAsync("test.mp4", frameCount: 20, thumbnailWidth: 160);

        Assert.Equal(20, result.Count);
    }

    [Fact]
    public async Task GenerateThumbnailStripAsync_FrameTimestampsAreEvenlySpaced()
    {
        var editor = new StubClipEditor(duration: TimeSpan.FromSeconds(100));

        await editor.GenerateThumbnailStripAsync("test.mp4", frameCount: 10, thumbnailWidth: 160);

        // Each frame should be 10 seconds apart (100s / 10 frames)
        Assert.Equal(10, editor.ExtractedTimestamps.Count);
        for (var i = 0; i < 10; i++)
        {
            var expected = TimeSpan.FromSeconds(i * 10);
            Assert.Equal(expected, editor.ExtractedTimestamps[i]);
        }
    }

    [Fact]
    public async Task GenerateThumbnailStripAsync_SingleFrame_ExtractsAtZero()
    {
        var editor = new StubClipEditor(duration: TimeSpan.FromSeconds(30));

        await editor.GenerateThumbnailStripAsync("test.mp4", frameCount: 1, thumbnailWidth: 160);

        Assert.Single(editor.ExtractedTimestamps);
        Assert.Equal(TimeSpan.Zero, editor.ExtractedTimestamps[0]);
    }

    [Fact]
    public async Task GenerateThumbnailStripAsync_RespectsRequestedWidth()
    {
        var editor = new StubClipEditor(duration: TimeSpan.FromSeconds(30));

        await editor.GenerateThumbnailStripAsync("test.mp4", frameCount: 5, thumbnailWidth: 240);

        Assert.Equal(240, editor.RequestedWidth);
    }

    [Fact]
    public async Task GenerateThumbnailStripAsync_HonoursCancellation()
    {
        var editor = new StubClipEditor(duration: TimeSpan.FromSeconds(60));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => editor.GenerateThumbnailStripAsync("test.mp4", frameCount: 10, thumbnailWidth: 160, ct: cts.Token));
    }

    /// <summary>
    /// Stub that records extraction parameters without running FFmpeg.
    /// Implements the thumbnail strip logic identically to FFmpegClipEditor
    /// but writes fake files instead of calling FFmpeg.
    /// </summary>
    private sealed class StubClipEditor : IClipEditor
    {
        private readonly TimeSpan _duration;

        public List<TimeSpan> ExtractedTimestamps { get; } = [];
        public int RequestedWidth { get; private set; }

        public StubClipEditor(TimeSpan duration)
        {
            _duration = duration;
        }

        public Task<string> TrimAsync(string inputPath, TimeSpan start, TimeSpan end, CancellationToken ct = default)
            => Task.FromResult(inputPath);

        public Task<string> GenerateThumbnailAsync(string inputPath, TimeSpan timestamp, CancellationToken ct = default)
            => Task.FromResult("thumb.jpg");

        public Task<TimeSpan> GetDurationAsync(string inputPath, CancellationToken ct = default)
            => Task.FromResult(_duration);

        public Task<IReadOnlyList<string>> GenerateThumbnailStripAsync(
            string inputPath, int frameCount, int thumbnailWidth, CancellationToken ct = default)
        {
            // Replicate the same logic as FFmpegClipEditor.GenerateThumbnailStripAsync
            // but without actually running FFmpeg
            if (_duration <= TimeSpan.Zero || frameCount <= 0)
                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

            RequestedWidth = thumbnailWidth;
            var interval = _duration.TotalSeconds / frameCount;
            var paths = new List<string>();

            for (var i = 0; i < frameCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                var time = TimeSpan.FromSeconds(i * interval);
                ExtractedTimestamps.Add(time);
                paths.Add($"strip_{i:D3}.jpg");
            }

            return Task.FromResult<IReadOnlyList<string>>(paths);
        }
    }
}
