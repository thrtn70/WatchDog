using WatchDog.Core.ClipEditor;
using WatchDog.Core.Highlights;
using WatchDog.Core.Storage;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.ClipEditor;

public sealed class ClipExportCoordinatorTests
{
    [Fact]
    public async Task ExportAsync_ProtectsMediaAndPreservesImmutableAttribution()
    {
        var registry = new ProtectedMediaRegistry();
        var source = Source();
        var output = Path.Combine(Path.GetTempPath(), $"export-{Guid.NewGuid():N}.mp4");
        var editor = new FakeEditor
        {
            TrimHandler = (_, _, _, _) =>
            {
                Assert.True(registry.IsProtected(source.FilePath));
                return Task.FromResult(output);
            },
        };
        var ingestion = new FakeIngestion
        {
            AcceptHandler = (receipt, _) =>
            {
                Assert.True(registry.IsProtected(output));
                return Task.FromResult(new CompletedMediaIngestionResult(
                    CompletedMediaIngestionDisposition.Indexed,
                    new ClipMetadata { FilePath = output, FileName = Path.GetFileName(output) }));
            },
        };
        await using var coordinator = new ClipExportCoordinator(editor, ingestion, registry);

        var result = await coordinator.ExportAsync(
            source,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(4));

        Assert.True(result.Indexed);
        var receipt = Assert.Single(ingestion.Accepted);
        Assert.Equal(output, receipt.FilePath);
        Assert.Equal(source.GameName, receipt.GameName);
        Assert.Equal(source.SessionId, receipt.SessionId);
        Assert.Equal(source.CaptureRequestId, receipt.CaptureRequestId);
        Assert.Equal(source.CaptureGeneration, receipt.CaptureGeneration);
        Assert.Equal(source.MatchNumber, receipt.MatchNumber);
        Assert.Equal(CompletedMediaOrigin.DerivedExport, receipt.Origin);
        Assert.False(registry.IsProtected(source.FilePath));
        Assert.False(registry.IsProtected(output));
    }

    [Fact]
    public async Task ExportAsync_ReportsDurablyPendingIndexWithoutLosingPublishedPath()
    {
        var source = Source();
        var output = Path.Combine(Path.GetTempPath(), $"pending-{Guid.NewGuid():N}.mp4");
        var editor = new FakeEditor { TrimHandler = (_, _, _, _) => Task.FromResult(output) };
        var ingestion = new FakeIngestion
        {
            AcceptHandler = (_, _) => Task.FromResult(new CompletedMediaIngestionResult(
                CompletedMediaIngestionDisposition.PendingRetry,
                FailureMessage: "index locked")),
        };
        await using var coordinator = new ClipExportCoordinator(editor, ingestion);

        var result = await coordinator.ExportAsync(source, TimeSpan.Zero, TimeSpan.FromSeconds(2));

        Assert.True(result.PendingRetry);
        Assert.Equal(output, result.OutputPath);
        Assert.Equal("index locked", result.FailureMessage);
    }

    [Fact]
    public async Task DrainAsync_ClosesAdmissionCancelsRendererAndWaitsForItToReap()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var editor = new FakeEditor
        {
            TrimHandler = async (_, _, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return "unreachable.mp4";
            },
        };
        await using var coordinator = new ClipExportCoordinator(editor, new FakeIngestion());
        var export = coordinator.ExportAsync(Source(), TimeSpan.Zero, TimeSpan.FromSeconds(2));
        await entered.Task.WaitAsync(TestTimeouts.Signal);

        var drain = await coordinator.DrainAsync(TimeSpan.FromSeconds(1));

        Assert.True(drain.Drained);
        Assert.Equal(0, drain.RemainingOperations);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export);
        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExportAsync(
            Source(), TimeSpan.Zero, TimeSpan.FromSeconds(1)));
    }

    private static ClipMetadata Source() => new()
    {
        FilePath = Path.Combine(Path.GetTempPath(), $"source-{Guid.NewGuid():N}.mp4"),
        FileName = "source.mp4",
        GameName = "Exact Game",
        SessionId = Guid.NewGuid(),
        CaptureRequestId = Guid.NewGuid(),
        CaptureGeneration = 17,
        MatchNumber = 3,
    };

    private sealed class FakeEditor : IClipEditor
    {
        public Func<string, TimeSpan, TimeSpan, CancellationToken, Task<string>> TrimHandler { get; init; } =
            (_, _, _, _) => Task.FromResult("output.mp4");

        public Task<string> TrimAsync(string inputPath, TimeSpan start, TimeSpan end, CancellationToken ct = default) =>
            TrimHandler(inputPath, start, end, ct);

        public Task<string> GenerateThumbnailAsync(string inputPath, TimeSpan timestamp, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TimeSpan> GetDurationAsync(string inputPath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<string>> GenerateThumbnailStripAsync(
            string inputPath,
            int frameCount,
            int thumbnailWidth,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeIngestion : ICompletedMediaIngestion
    {
        public Func<CompletedMediaReceipt, CancellationToken, Task<CompletedMediaIngestionResult>> AcceptHandler { get; init; } =
            (_, _) => Task.FromResult(new CompletedMediaIngestionResult(CompletedMediaIngestionDisposition.Indexed));

        public List<CompletedMediaReceipt> Accepted { get; } = [];
        public CompletedMediaIngestionStatus Status { get; } = new(0, 0, null);
        public event Action<CompletedMediaIngestionStatus>? StatusChanged { add { } remove { } }

        public Task<CompletedMediaIngestionResult> AcceptAsync(
            CompletedMediaReceipt media,
            CancellationToken cancellationToken = default)
        {
            Accepted.Add(media);
            return AcceptHandler(media, cancellationToken);
        }

        public Task<CompletedMediaIngestionResult> EnrichAsync(
            CompletedMediaReceipt media,
            HighlightType highlightType,
            CancellationToken cancellationToken = default) => AcceptAsync(media, cancellationToken);

        public Task<CompletedMediaDrainResult> DrainAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken = default) => Task.FromResult(
                new CompletedMediaDrainResult(true, true, 0, 0, null));
    }
}
