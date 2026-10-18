using System.Collections.ObjectModel;
using WatchDog.App.ViewModels;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Highlights;
using WatchDog.Core.Storage;

namespace WatchDog.Integration.Tests;

public sealed class ClipEditorPresentationTests
{
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(2, 2)]
    [InlineData(3, 2)]
    [InlineData(0, 11)]
    public async Task InvalidTrimBoundsNeverReachExport(int startSeconds, int endSeconds)
    {
        var export = new FakeExports();
        var vm = new ClipEditorViewModel(new FakeEditor(), export);
        await vm.LoadClipAsync(Source("original.mp4"));
        vm.TrimStart = TimeSpan.FromSeconds(startSeconds);
        vm.TrimEnd = TimeSpan.FromSeconds(endSeconds);

        await vm.ExecuteTrimCommand.ExecuteAsync(null);

        Assert.Equal(0, export.Calls);
        Assert.Contains("range", vm.TrimStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Saved", vm.TrimStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExportCannotStartBeforeValidDurationIsLoaded()
    {
        var export = new FakeExports();
        var vm = new ClipEditorViewModel(new FakeEditor { Duration = TimeSpan.Zero }, export);
        await vm.ExecuteTrimCommand.ExecuteAsync(null);
        Assert.Equal(0, export.Calls);
        await vm.LoadClipAsync(Source("original.mp4"));
        await vm.ExecuteTrimCommand.ExecuteAsync(null);
        Assert.Equal(0, export.Calls);
        Assert.False(vm.StartTrimCommand.CanExecute(null));
        Assert.DoesNotContain("Saved", vm.TrimStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(CompletedMediaIngestionDisposition.Indexed, "indexed")]
    [InlineData(CompletedMediaIngestionDisposition.PendingRetry, "pending retry")]
    [InlineData(CompletedMediaIngestionDisposition.Rejected, "rejected")]
    public async Task PublishedOutputKeepsTypedStatusAndOriginalSource(
        CompletedMediaIngestionDisposition disposition, string expected)
    {
        var export = new FakeExports
        {
            Handler = (source, start, end, _) =>
            {
                Assert.Equal("original.mp4", source.FilePath);
                Assert.Equal(TimeSpan.FromSeconds(1), start);
                Assert.Equal(TimeSpan.FromSeconds(4), end);
                return Task.FromResult(new ClipExportResult("derived.mp4", disposition, @"C:\private\failure"));
            },
        };
        var vm = new ClipEditorViewModel(new FakeEditor(), export);
        await vm.LoadClipAsync(Source("original.mp4"));
        vm.TrimStart = TimeSpan.FromSeconds(1);
        vm.TrimEnd = TimeSpan.FromSeconds(4);
        vm.StartTrimCommand.Execute(null);

        await vm.ExecuteTrimCommand.ExecuteAsync(null);

        Assert.Equal(1, export.Calls);
        Assert.False(vm.IsTrimming);
        Assert.Contains("derived.mp4", vm.TrimStatus);
        Assert.Contains(expected, vm.TrimStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", vm.TrimStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("original.mp4", export.LastSource!.FilePath);
    }

    [Fact]
    public async Task CancellationBeforePublicationNeverClaimsSavedOutput()
    {
        var export = new FakeExports
        {
            Handler = (_, _, _, ct) => Task.FromCanceled<ClipExportResult>(new CancellationToken(true)),
        };
        var vm = new ClipEditorViewModel(new FakeEditor(), export);
        await vm.LoadClipAsync(Source("original.mp4"));

        await vm.ExecuteTrimCommand.ExecuteAsync(null);

        Assert.Contains("canceled", vm.TrimStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Saved", vm.TrimStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LateCancelStillReportsPublishedAndIndexedResult()
    {
        ClipEditorViewModel? vm = null;
        var export = new FakeExports
        {
            Handler = (_, _, _, _) =>
            {
                vm!.CancelTrimCommand.Execute(null);
                return Task.FromResult(new ClipExportResult("derived.mp4", CompletedMediaIngestionDisposition.Indexed));
            },
        };
        vm = new ClipEditorViewModel(new FakeEditor(), export);
        await vm.LoadClipAsync(Source("original.mp4"));
        vm.StartTrimCommand.Execute(null);

        await vm.ExecuteTrimCommand.ExecuteAsync(null);

        Assert.Contains("indexed", vm.TrimStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("derived.mp4", vm.TrimStatus);
    }

    [Fact]
    public async Task ClosedAdmissionAndFailedLoadNeverClaimAnExport()
    {
        var export = new FakeExports
        {
            Handler = (_, _, _, _) => Task.FromException<ClipExportResult>(
                new InvalidOperationException("Clip export admission is closed for shutdown.")),
        };
        var vm = new ClipEditorViewModel(new FakeEditor(), export);
        await vm.LoadClipAsync(Source("original.mp4"));
        await vm.ExecuteTrimCommand.ExecuteAsync(null);
        Assert.Contains("unavailable", vm.TrimStatus, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Saved", vm.TrimStatus, StringComparison.OrdinalIgnoreCase);

        var failed = new ClipEditorViewModel(new FakeEditor { FailLoad = true }, export);
        await failed.LoadClipAsync(Source("bad.mp4"));
        await failed.ExecuteTrimCommand.ExecuteAsync(null);
        Assert.Equal(1, export.Calls);
        Assert.DoesNotContain("Saved", failed.TrimStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetiredSelectionRejectsOldCommandsWithoutAffectingNextClip()
    {
        var export = new FakeExports();
        var old = new ClipEditorViewModel(new FakeEditor(), export);
        await old.LoadClipAsync(Source("old.mp4"));
        old.Retire();
        var current = new ClipEditorViewModel(new FakeEditor(), export);
        await current.LoadClipAsync(Source("current.mp4"));

        await old.ExecuteTrimCommand.ExecuteAsync(null);
        await current.ExecuteTrimCommand.ExecuteAsync(null);

        Assert.Equal(1, export.Calls);
        Assert.Equal("current.mp4", export.LastSource!.FilePath);
        Assert.DoesNotContain("derived.mp4", old.TrimStatus);
    }

    [Fact]
    public async Task PlayRequestedDuringDurationProbeStartsWhenSelectedClipLoads()
    {
        var duration = new TaskCompletionSource<TimeSpan>();
        var vm = new ClipEditorViewModel(
            new FakeEditor { DurationTask = duration.Task }, new FakeExports());
        var load = vm.LoadClipAsync(Source("lesson.mp4"));

        vm.TogglePlayPauseCommand.Execute(null);
        duration.SetResult(TimeSpan.FromSeconds(10));
        await load;

        Assert.True(vm.IsPlaying);
        Assert.Equal("lesson.mp4", vm.Source!.OriginalString);
    }

    [Fact]
    public async Task RetiredDurationProbeCannotStartPlaybackOrReplaceCurrentSelection()
    {
        var duration = new TaskCompletionSource<TimeSpan>();
        var old = new ClipEditorViewModel(new FakeEditor { DurationTask = duration.Task }, new FakeExports());
        var oldLoad = old.LoadClipAsync(Source("old.mp4"));
        old.TogglePlayPauseCommand.Execute(null);
        old.Retire();
        var current = new ClipEditorViewModel(new FakeEditor(), new FakeExports());
        await current.LoadClipAsync(Source("current.mp4"));

        duration.SetResult(TimeSpan.FromSeconds(10));
        await oldLoad;

        Assert.Null(old.Source);
        Assert.False(old.IsPlaying);
        Assert.Equal("current.mp4", current.Source!.OriginalString);
    }

    [Fact]
    public async Task RetiredExportCompletionCannotClaimResultForNewSelection()
    {
        var pending = new TaskCompletionSource<ClipExportResult>();
        var export = new FakeExports
        {
            Handler = (source, _, _, _) => source.FilePath == "old.mp4"
                ? pending.Task
                : Task.FromResult(new ClipExportResult("current-derived.mp4", CompletedMediaIngestionDisposition.Indexed)),
        };
        var old = new ClipEditorViewModel(new FakeEditor(), export);
        await old.LoadClipAsync(Source("old.mp4"));
        var oldExport = old.ExecuteTrimCommand.ExecuteAsync(null);
        old.Retire();
        var current = new ClipEditorViewModel(new FakeEditor(), export);
        await current.LoadClipAsync(Source("current.mp4"));
        await current.ExecuteTrimCommand.ExecuteAsync(null);

        pending.SetResult(new ClipExportResult("old-derived.mp4", CompletedMediaIngestionDisposition.PendingRetry));
        await oldExport;

        Assert.DoesNotContain("old-derived.mp4", old.TrimStatus);
        Assert.Contains("current-derived.mp4", current.TrimStatus);
        Assert.Equal(2, export.Calls);
    }

    [Theory]
    [InlineData(CompletedMediaIngestionDisposition.Indexed)]
    [InlineData(CompletedMediaIngestionDisposition.PendingRetry)]
    [InlineData(CompletedMediaIngestionDisposition.Rejected)]
    public async Task RetiredEditorStillReportsActualPublishedOutcomeToStableOwner(
        CompletedMediaIngestionDisposition disposition)
    {
        ClipEditorViewModel? vm = null;
        var reports = new List<(ClipMetadata Source, ClipExportResult Result)>();
        var export = new FakeExports
        {
            Handler = (_, _, _, _) =>
            {
                vm!.Retire(); // Models library refresh before ingestion returns.
                return Task.FromResult(new ClipExportResult("derived.mp4", disposition));
            },
        };
        vm = new ClipEditorViewModel(new FakeEditor(), export,
            (source, result) => reports.Add((source, result)));
        await vm.LoadClipAsync(Source("original.mp4"));

        await vm.ExecuteTrimCommand.ExecuteAsync(null);

        var report = Assert.Single(reports);
        Assert.Equal("original.mp4", report.Source.FilePath);
        Assert.Equal("derived.mp4", report.Result.OutputPath);
        Assert.Equal(disposition, report.Result.IngestionDisposition);
        Assert.DoesNotContain("derived.mp4", vm.TrimStatus);
    }

    [Fact]
    public void LibraryRefreshKeepsSelectedItemInBoundCollectionWhenSourceIsUnchanged()
    {
        var source = Source("original.mp4");
        var selected = new ClipItemViewModel(source);
        selected.IsSelected = true;
        var clips = new ObservableCollection<ClipItemViewModel>([selected]);
        var derived = new ClipItemViewModel(Source("derived.mp4"));
        Assert.True(MainWindowViewModel.CanReuseSelectedClip(source, source with { }));

        var preserved = MainWindowViewModel.ReconcileClipsPreservingSelection(
            clips, [derived, selected], selected);

        Assert.True(preserved);
        Assert.Same(selected, clips[1]);
        Assert.Same(derived, clips[0]);
        Assert.False(selected.IsSelected);
        Assert.False(MainWindowViewModel.CanReuseSelectedClip(source, source with { IsFavorite = true }));
    }

    [Theory]
    [InlineData(CompletedMediaIngestionDisposition.Indexed, "indexed")]
    [InlineData(CompletedMediaIngestionDisposition.PendingRetry, "pending retry")]
    [InlineData(CompletedMediaIngestionDisposition.Rejected, "rejected")]
    public void StableOutcomeNamesOriginalAndOutputWithoutRawFailure(
        CompletedMediaIngestionDisposition disposition, string expected)
    {
        var status = MainWindowViewModel.FormatExportOutcome(
            Source(@"C:\private\original.mp4"),
            new ClipExportResult(@"C:\private\derived.mp4", disposition, @"C:\private\failure"));

        Assert.Contains("original.mp4", status);
        Assert.Contains("derived.mp4", status);
        Assert.Contains(expected, status, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", status, StringComparison.OrdinalIgnoreCase);
    }

    private static ClipMetadata Source(string path) => new() { FilePath = path, FileName = path };

    private sealed class FakeEditor : IClipEditor
    {
        public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(10);
        public Task<TimeSpan>? DurationTask { get; init; }
        public bool FailLoad { get; init; }
        public Task<TimeSpan> GetDurationAsync(string inputPath, CancellationToken ct = default) =>
            FailLoad ? Task.FromException<TimeSpan>(new IOException("private path")) :
            DurationTask ?? Task.FromResult(Duration);
        public Task<IReadOnlyList<string>> GenerateThumbnailStripAsync(string inputPath, int frameCount, int thumbnailWidth, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string> TrimAsync(string inputPath, TimeSpan start, TimeSpan end, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string> GenerateThumbnailAsync(string inputPath, TimeSpan timestamp, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeExports : IClipExportCoordinator
    {
        public int Calls { get; private set; }
        public ClipMetadata? LastSource { get; private set; }
        public Func<ClipMetadata, TimeSpan, TimeSpan, CancellationToken, Task<ClipExportResult>> Handler { get; init; } =
            (_, _, _, _) => Task.FromResult(new ClipExportResult("derived.mp4", CompletedMediaIngestionDisposition.Indexed));
        public Task<ClipExportResult> ExportAsync(ClipMetadata source, TimeSpan start, TimeSpan end, CancellationToken cancellationToken = default)
        {
            Calls++;
            LastSource = source;
            return Handler(source, start, end, cancellationToken);
        }
        public Task<ClipExportDrainResult> DrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ClipExportDrainResult(true, 0));
    }
}
