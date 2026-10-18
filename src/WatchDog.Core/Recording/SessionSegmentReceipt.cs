using WatchDog.Core.Capture;
using WatchDog.Core.Sessions;

namespace WatchDog.Core.Recording;

/// <summary>Additive producer metadata. The legacy path event remains available.</summary>
public interface ISessionSegmentReceiptSource
{
    event Action<SessionSegmentReceipt>? SegmentFinalized;
    // Successful receipts remain available through Stop, independently of event delivery.
    IReadOnlyList<SessionSegmentReceipt> GetFinalizedSegments(CaptureSessionContext expected);
}

/// <summary>
/// Producer-owned attribution for one finalized screen segment. The timing epoch identifies
/// only this recorder's stopwatch origin; it is not a shared audio/camera clock or native PTS.
/// </summary>
public sealed record SessionSegmentReceipt(
    int SchemaVersion,
    CaptureSessionContext Context,
    Guid MediaId,
    Guid OutputEpoch,
    Guid ProducerTimingEpoch,
    int Sequence,
    string FilePath,
    TimeSpan StartOffset,
    TimeSpan EndOffset,
    SessionTimingProvenance TimingProvenance)
{
    public override string ToString() => nameof(SessionSegmentReceipt);
}

public enum SessionSegmentReceiptDisposition
{
    Accepted,
    Duplicate,
    Rejected,
}

/// <summary>One capture's pure receipt admission ledger; it owns no files or resources.</summary>
public sealed class SessionSegmentReceiptLedger(CaptureSessionContext context, string outputDirectory)
{
    private readonly Dictionary<Guid, (SessionSegmentReceipt Receipt, SessionMediaSegment Media)> _accepted = new();

    // Device admission is enriched after the producer starts. It is not receipt identity.
    public static bool SameCapture(CaptureSessionContext? expected, CaptureSessionContext? actual) =>
        expected is not null && actual is not null && expected.SessionId == actual.SessionId &&
        expected.Generation == actual.Generation && expected.Request == actual.Request &&
        expected.RequestedTarget == actual.RequestedTarget && expected.ActualTarget == actual.ActualTarget;

    public SessionSegmentReceiptDisposition Accept(SessionSegmentReceipt? receipt,
        out SessionMediaSegment? media, out CaptureFailure? failure)
    {
        media = null;
        failure = new(CaptureFailureKind.InvalidRequest, "capture.segment-receipt.invalid",
            "The finalized segment receipt is malformed or outside its owned output directory.");
        if (receipt is null || receipt.Context is null || receipt.Context.Request is null)
            return SessionSegmentReceiptDisposition.Rejected;
        if (!SameCapture(context, receipt.Context))
        {
            failure = new(CaptureFailureKind.Superseded, "capture.segment-receipt.foreign",
                "The finalized segment receipt belongs to a different capture boundary.");
            return SessionSegmentReceiptDisposition.Rejected;
        }
        if (receipt.SchemaVersion != 1 || context.SessionId == Guid.Empty || context.Generation <= 0 ||
            context.Request.RequestId == Guid.Empty || context.RequestedTarget is null || context.ActualTarget is null ||
            context.RequestedTarget != context.Request.Target || receipt.MediaId == Guid.Empty ||
            receipt.OutputEpoch == Guid.Empty || receipt.ProducerTimingEpoch == Guid.Empty || receipt.Sequence < 0 ||
            receipt.StartOffset < TimeSpan.Zero || receipt.EndOffset < receipt.StartOffset ||
            receipt.TimingProvenance != SessionTimingProvenance.RecorderStopwatchEstimate || !OwnedPath(receipt.FilePath))
            return SessionSegmentReceiptDisposition.Rejected;

        if (_accepted.TryGetValue(receipt.MediaId, out var existing) && existing.Receipt == receipt)
        {
            media = existing.Media;
            failure = null;
            return SessionSegmentReceiptDisposition.Duplicate;
        }
        if (_accepted.Values.Any(entry => Conflicts(entry.Receipt, receipt)))
        {
            failure = new(CaptureFailureKind.InvalidRequest, "capture.segment-receipt.conflict",
                "The finalized segment receipt conflicts with previously accepted producer metadata.");
            return SessionSegmentReceiptDisposition.Rejected;
        }
        media = new SessionMediaSegment
        {
            MediaId = receipt.MediaId,
            SessionId = receipt.Context.SessionId,
            CaptureRequestId = receipt.Context.Request.RequestId,
            CaptureGeneration = receipt.Context.Generation,
            Role = SessionMediaRole.Screen,
            FilePath = receipt.FilePath,
            Sequence = receipt.Sequence,
            State = SessionMediaState.Finalized,
            StartOffset = receipt.StartOffset,
            Duration = receipt.EndOffset - receipt.StartOffset,
            TimingProvenance = receipt.TimingProvenance,
            ProducerMetadata = new(receipt.SchemaVersion, receipt.OutputEpoch, receipt.ProducerTimingEpoch),
        };
        _accepted.Add(receipt.MediaId, (receipt, media));
        failure = null;
        return SessionSegmentReceiptDisposition.Accepted;
    }

    private static bool Conflicts(SessionSegmentReceipt first, SessionSegmentReceipt next) =>
        first.MediaId == next.MediaId || first.OutputEpoch == next.OutputEpoch ||
        StringComparer.OrdinalIgnoreCase.Equals(first.FilePath, next.FilePath) ||
        (first.ProducerTimingEpoch == next.ProducerTimingEpoch &&
         (first.Sequence == next.Sequence || (first.Sequence < next.Sequence
             ? first.EndOffset > next.StartOffset : next.EndOffset > first.StartOffset)));

    private bool OwnedPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
                !Path.IsPathFullyQualified(outputDirectory) ||
                !StringComparer.Ordinal.Equals(path, Path.GetFullPath(path)))
                return false;
            var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory)) + Path.DirectorySeparatorChar;
            return path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) && path.Length > directory.Length &&
                !Path.EndsInDirectorySeparator(path) && path.AsSpan(Path.GetPathRoot(path)!.Length).IndexOf(':') < 0;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
