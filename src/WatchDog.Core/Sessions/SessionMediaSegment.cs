namespace WatchDog.Core.Sessions;

public enum SessionMediaRole
{
    Screen,
    Camera,
}

public enum SessionMediaState
{
    Planned,
    Finalized,
}

public enum SessionTimingProvenance
{
    BackendStopwatchEstimate = 0,
    NativeTimestamp = 1,
    RecorderStopwatchEstimate = 2,
}

public sealed record SessionSegmentProducerMetadata(
    int SchemaVersion,
    Guid OutputEpoch,
    Guid ProducerTimingEpoch);

/// <summary>
/// Immutable attribution for one independently retained media file. Legacy backend estimates use
/// the capture lifecycle epoch. Producer estimates use their explicit producer timing epoch, which
/// does not establish a common media clock, native timestamps, or cross-output alignment.
/// </summary>
public sealed record SessionMediaSegment
{
    public required Guid MediaId { get; init; }
    public required Guid SessionId { get; init; }
    public required Guid CaptureRequestId { get; init; }
    public required long CaptureGeneration { get; init; }
    public required SessionMediaRole Role { get; init; }
    public required string FilePath { get; init; }
    public required int Sequence { get; init; }
    public required SessionMediaState State { get; init; }
    public required TimeSpan StartOffset { get; init; }
    public TimeSpan? Duration { get; init; }
    public SessionTimingProvenance TimingProvenance { get; init; } =
        SessionTimingProvenance.BackendStopwatchEstimate;
    public SessionSegmentProducerMetadata? ProducerMetadata { get; init; }
}

internal static class SessionMediaSegmentValidation
{
    internal const int CurrentProducerMetadataSchemaVersion = 1;

    public static void ValidateSegment(Guid sessionId, SessionMediaSegment segment)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var metadata = segment.ProducerMetadata;
        if (metadata?.SchemaVersion > CurrentProducerMetadataSchemaVersion)
        {
            throw new NotSupportedException(
                $"Producer metadata schema version {metadata.SchemaVersion} is newer than supported version " +
                $"{CurrentProducerMetadataSchemaVersion}.");
        }

        if (segment.MediaId == Guid.Empty || segment.SessionId != sessionId ||
            segment.CaptureRequestId == Guid.Empty ||
            segment.CaptureGeneration <= 0 || string.IsNullOrWhiteSpace(segment.FilePath) ||
            segment.Sequence < 0 || segment.StartOffset < TimeSpan.Zero || segment.Duration < TimeSpan.Zero ||
            !Enum.IsDefined(segment.Role) || !Enum.IsDefined(segment.State) ||
            !Enum.IsDefined(segment.TimingProvenance))
        {
            throw new ArgumentException("The media segment attribution is incomplete or invalid.", nameof(segment));
        }

        if (segment.State == SessionMediaState.Planned && segment.Duration is not null)
            throw new ArgumentException("A planned media segment cannot have a finalized duration.", nameof(segment));
        if (segment.State == SessionMediaState.Finalized && segment.Duration is null)
            throw new ArgumentException("A finalized media segment requires a duration.", nameof(segment));

        var hasProducerMetadata = metadata is not null;
        var hasProducerTiming = segment.TimingProvenance == SessionTimingProvenance.RecorderStopwatchEstimate;
        if (hasProducerMetadata != hasProducerTiming)
        {
            throw new ArgumentException(
                "Recorder stopwatch provenance and producer metadata must be supplied together.",
                nameof(segment));
        }

        if (metadata is null)
            return;

        if (metadata.SchemaVersion != CurrentProducerMetadataSchemaVersion ||
            metadata.OutputEpoch == Guid.Empty || metadata.ProducerTimingEpoch == Guid.Empty ||
            segment.Role != SessionMediaRole.Screen || segment.State != SessionMediaState.Finalized)
        {
            throw new ArgumentException(
                "Producer metadata requires a finalized screen segment with valid version-one epoch identities.",
                nameof(segment));
        }
    }

    public static void ValidateGraph(Guid sessionId, IReadOnlyList<SessionMediaSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        foreach (var segment in segments)
            ValidateSegment(sessionId, segment);

        var producerSegments = segments
            .Where(segment => segment.ProducerMetadata is not null)
            .ToArray();

        if (producerSegments.Any(producer =>
                segments.Count(segment => segment.MediaId == producer.MediaId) > 1))
            throw new InvalidOperationException("A producer media ID cannot identify more than one segment.");
        if (producerSegments.Any(producer =>
                segments.Count(segment => string.Equals(
                    segment.FilePath,
                    producer.FilePath,
                    StringComparison.OrdinalIgnoreCase)) > 1))
        {
            throw new InvalidOperationException("A producer media path cannot identify more than one segment.");
        }
        if (producerSegments
            .GroupBy(segment => segment.ProducerMetadata!.OutputEpoch)
            .Any(group => group.Count() > 1))
        {
            throw new InvalidOperationException("A producer output epoch cannot identify more than one segment.");
        }

        foreach (var timingGroup in producerSegments.GroupBy(segment => new
        {
            segment.SessionId,
            segment.CaptureRequestId,
            segment.CaptureGeneration,
            segment.ProducerMetadata!.ProducerTimingEpoch,
        }))
        {
            var ordered = timingGroup.OrderBy(segment => segment.Sequence).ToArray();
            if (ordered.GroupBy(segment => segment.Sequence).Any(group => group.Count() > 1))
            {
                throw new InvalidOperationException(
                    "A producer timing epoch cannot assign one sequence to multiple segments.");
            }

            for (var i = 1; i < ordered.Length; i++)
            {
                var previous = ordered[i - 1];
                var current = ordered[i];
                if (previous.StartOffset > current.StartOffset ||
                    previous.Duration!.Value > current.StartOffset - previous.StartOffset)
                {
                    throw new InvalidOperationException(
                        "Producer segments in one timing epoch must follow sequence order without overlap.");
                }
            }
        }
    }

    public static void ValidateProducerUpdate(
        IReadOnlyList<SessionMediaSegment> existing,
        IReadOnlyList<SessionMediaSegment> replacement)
    {
        foreach (var existingReceipt in existing.Where(segment => segment.ProducerMetadata is not null))
        {
            var replacementReceipt = replacement.SingleOrDefault(segment => segment.MediaId == existingReceipt.MediaId);
            if (replacementReceipt != existingReceipt)
                throw new InvalidOperationException("A finalized producer receipt cannot be changed or removed.");
        }

        foreach (var replacementReceipt in replacement.Where(segment => segment.ProducerMetadata is not null))
        {
            var existingSegment = existing.SingleOrDefault(segment => segment.MediaId == replacementReceipt.MediaId);
            if (existingSegment is null || existingSegment.ProducerMetadata is not null)
                continue;

            if (existingSegment.State == SessionMediaState.Finalized ||
                existingSegment.SessionId != replacementReceipt.SessionId ||
                existingSegment.CaptureRequestId != replacementReceipt.CaptureRequestId ||
                existingSegment.CaptureGeneration != replacementReceipt.CaptureGeneration ||
                existingSegment.Role != replacementReceipt.Role ||
                !string.Equals(existingSegment.FilePath, replacementReceipt.FilePath, StringComparison.OrdinalIgnoreCase) ||
                existingSegment.Sequence != replacementReceipt.Sequence ||
                existingSegment.StartOffset != replacementReceipt.StartOffset)
            {
                throw new InvalidOperationException(
                    "Producer metadata cannot reassign an existing media identity.");
            }
        }
    }
}
