using WatchDog.Core.Storage;

namespace WatchDog.Core.ClipEditor;

public sealed record ClipExportResult(
    string OutputPath,
    CompletedMediaIngestionDisposition IngestionDisposition,
    string? FailureMessage = null)
{
    public bool Indexed => IngestionDisposition == CompletedMediaIngestionDisposition.Indexed;
    public bool PendingRetry => IngestionDisposition == CompletedMediaIngestionDisposition.PendingRetry;
}

public sealed record ClipExportDrainResult(bool Drained, int RemainingOperations);

public interface IClipExportCoordinator
{
    Task<ClipExportResult> ExportAsync(
        ClipMetadata source,
        TimeSpan start,
        TimeSpan end,
        CancellationToken cancellationToken = default);

    Task<ClipExportDrainResult> DrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Application-owned trim/export orchestration. Rendering is cancellation-safe in the
/// editor, while publication is followed immediately by durable completed-media admission.
/// Shutdown closes admission, cancels active renderers, and waits before ingestion drains.
/// </summary>
public sealed class ClipExportCoordinator : IClipExportCoordinator, IAsyncDisposable
{
    private readonly IClipEditor _editor;
    private readonly ICompletedMediaIngestion _ingestion;
    private readonly IProtectedMediaRegistry _protectedMedia;
    private readonly object _sync = new();
    private readonly HashSet<TaskCompletionSource> _active = [];
    private readonly CancellationTokenSource _shutdown = new();
    private bool _accepting = true;

    public ClipExportCoordinator(IClipEditor editor, ICompletedMediaIngestion ingestion)
        : this(editor, ingestion, new ProtectedMediaRegistry())
    {
    }

    public ClipExportCoordinator(
        IClipEditor editor,
        ICompletedMediaIngestion ingestion,
        IProtectedMediaRegistry protectedMedia)
    {
        _editor = editor;
        _ingestion = ingestion;
        _protectedMedia = protectedMedia;
    }

    public async Task<ClipExportResult> ExportAsync(
        ClipMetadata source,
        TimeSpan start,
        TimeSpan end,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (!_accepting)
                throw new InvalidOperationException("Clip export admission is closed for shutdown.");
            _active.Add(completion);
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        try
        {
            using var sourceProtection = _protectedMedia.Protect(source.FilePath);
            var outputPath = await _editor.TrimAsync(
                source.FilePath,
                start,
                end,
                linked.Token).ConfigureAwait(false);
            using var outputProtection = _protectedMedia.Protect(outputPath);
            var receipt = new CompletedMediaReceipt(
                outputPath,
                source.GameName ?? "Unknown",
                DateTimeOffset.UtcNow,
                source.SessionId,
                source.CaptureRequestId ?? Guid.NewGuid(),
                source.CaptureGeneration is > 0 ? source.CaptureGeneration.Value : 1,
                source.MatchNumber,
                CompletedMediaOrigin.DerivedExport);
            // Once the editor atomically publishes a valid output, durable admission must
            // finish even if the user cancels or shutdown starts. The coordinator drain
            // still tracks this operation until ingestion has acknowledged the receipt.
            var ingestion = await _ingestion.AcceptAsync(receipt, CancellationToken.None).ConfigureAwait(false);
            return new ClipExportResult(outputPath, ingestion.Disposition, ingestion.FailureMessage);
        }
        finally
        {
            lock (_sync)
                _active.Remove(completion);
            completion.TrySetResult();
        }
    }

    public async Task<ClipExportDrainResult> DrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Task[] active;
        lock (_sync)
        {
            _accepting = false;
            active = _active.Select(operation => operation.Task).ToArray();
        }
        _shutdown.Cancel();

        try
        {
            var drain = Task.WhenAll(active);
            if (timeout == Timeout.InfiniteTimeSpan)
                await drain.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
                await drain.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            lock (_sync)
                return new ClipExportDrainResult(false, _active.Count);
        }

        lock (_sync)
            return new ClipExportDrainResult(_active.Count == 0, _active.Count);
    }

    public async ValueTask DisposeAsync()
    {
        await DrainAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        _shutdown.Dispose();
    }
}
