using WatchDog.Core.ClipEditor;
using WatchDog.Core.Highlights;

namespace WatchDog.Core.Storage;

public enum CompletedMediaOrigin
{
    Capture,
    DerivedExport,
}

/// <summary>
/// Immutable attribution captured at the instant a replay file completes.
/// It must never be reconstructed from whichever session happens to be current later.
/// </summary>
public sealed record CompletedMediaReceipt(
    string FilePath,
    string GameName,
    DateTimeOffset CompletedAt,
    Guid? SessionId,
    Guid CaptureRequestId,
    long CaptureGeneration,
    int? MatchNumber,
    CompletedMediaOrigin Origin = CompletedMediaOrigin.Capture);

public enum CompletedMediaIngestionDisposition
{
    Indexed,
    PendingRetry,
    Rejected,
}

public sealed record CompletedMediaIngestionResult(
    CompletedMediaIngestionDisposition Disposition,
    ClipMetadata? Clip = null,
    string? FailureMessage = null)
{
    public bool Indexed => Disposition == CompletedMediaIngestionDisposition.Indexed;
}

public sealed record CompletedMediaIngestionStatus(
    int PendingRetryCount,
    int UnattributedCount,
    string? LastFailure)
{
    public bool NeedsAttention => PendingRetryCount > 0 || UnattributedCount > 0;
}

public sealed record CompletedMediaDrainResult(
    bool Drained,
    bool PendingIsDurable,
    int PendingRetryCount,
    int UnattributedCount,
    string? WarningMessage);

public sealed record CompletedMediaReconciliationResult(
    int PendingIndexed,
    int PendingRemaining,
    int UnattributedForgotten);

public interface ICompletedMediaReconciliation
{
    /// <summary>
    /// Retries every pending receipt through the ingestion queue, keeping its capture attribution,
    /// then forgets unattributed files that the clip library has since indexed.
    /// </summary>
    Task<CompletedMediaReconciliationResult> ReconcileWithLibraryAsync(CancellationToken ct = default);
}

public interface ICompletedMediaIngestion
{
    CompletedMediaIngestionStatus Status { get; }
    event Action<CompletedMediaIngestionStatus>? StatusChanged;

    Task<CompletedMediaIngestionResult> AcceptAsync(
        CompletedMediaReceipt media,
        CancellationToken cancellationToken = default);

    Task<CompletedMediaIngestionResult> EnrichAsync(
        CompletedMediaReceipt media,
        HighlightType highlightType,
        CancellationToken cancellationToken = default);

    Task<CompletedMediaDrainResult> DrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}
