namespace WatchDog.Core.Capture;

/// <summary>
/// Identifies the pipeline stage and media kind whose monotonic counter is observed.
/// Source-delivery and encoded-output streams are intentionally distinct so an encoder's
/// compatibility output or a retained empty track cannot be reported as source delivery.
/// </summary>
public enum CaptureLivenessStreamKind
{
    SourceVideo,
    SourceAudio,
    EncodedOutputVideo,
    EncodedOutputAudio,
    AggregateOutput,
}

/// <summary>
/// Identifies one stream at a specific pipeline stage. Index distinguishes multiple streams
/// of the same kind; aggregate output has exactly one stream at index zero.
/// </summary>
public readonly record struct CaptureLivenessStream
{
    public CaptureLivenessStream(CaptureLivenessStreamKind kind, int index)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        if (index < 0)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (kind == CaptureLivenessStreamKind.AggregateOutput && index != 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(index),
                "The aggregate output stream must use index zero.");
        }

        Kind = kind;
        Index = index;
    }

    public CaptureLivenessStreamKind Kind { get; }
    public int Index { get; }
}

/// <summary>
/// Names the exact monotonic counter being observed. These values describe counter progress
/// only. They do not prove correct source or device binding, visible/nonblack/changing video,
/// audible audio, or audio/video synchronization.
/// </summary>
public enum CaptureLivenessMeasurementKind
{
    SourceDeliveredVideoFrames,
    SourceDeliveredAudioSamples,
    EncodedOutputVideoFrames,
    EncodedOutputAudioSamples,
    OutputBytes,
}

/// <summary>
/// Defines monotonic deadlines for initial progress, progress after becoming live, fresh
/// telemetry, and optional provider execution. Provider execution timeout requests cooperative
/// cancellation but never detaches or abandons an in-flight provider. All deadlines are evaluated
/// with a <see cref="TimeProvider"/> timestamp.
/// </summary>
public sealed record CaptureLivenessTimingPolicy
{
    public CaptureLivenessTimingPolicy(
        TimeSpan startupTimeout,
        TimeSpan stallTimeout,
        TimeSpan telemetryTimeout,
        TimeSpan? providerExecutionTimeout = null)
    {
        ValidateTimeout(startupTimeout, nameof(startupTimeout));
        ValidateTimeout(stallTimeout, nameof(stallTimeout));
        ValidateTimeout(telemetryTimeout, nameof(telemetryTimeout));
        if (providerExecutionTimeout is { } executionTimeout)
        {
            ValidateTimeout(executionTimeout, nameof(providerExecutionTimeout));
            if (executionTimeout.TotalMilliseconds > uint.MaxValue - 1d)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(providerExecutionTimeout),
                    "Provider execution timeout must fit the TimeProvider timer range.");
            }
        }

        StartupTimeout = startupTimeout;
        StallTimeout = stallTimeout;
        TelemetryTimeout = telemetryTimeout;
        ProviderExecutionTimeout = providerExecutionTimeout;
    }

    public TimeSpan StartupTimeout { get; }
    public TimeSpan StallTimeout { get; }
    public TimeSpan TelemetryTimeout { get; }
    public TimeSpan? ProviderExecutionTimeout { get; }

    private static void ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Capture liveness timeouts must be finite and greater than zero.");
        }
    }
}

/// <summary>
/// Freezes the capture and output identity to which liveness evidence belongs. An observation
/// for any other identity is foreign and cannot mutate the tracker's baselines.
/// </summary>
public sealed record CaptureLivenessIdentity
{
    public CaptureLivenessIdentity(
        Guid sessionId,
        Guid requestId,
        long generation,
        CaptureTarget actualTarget,
        Guid outputEpoch,
        CaptureLivenessStream stream,
        CaptureLivenessMeasurementKind measurementKind,
        CaptureLivenessTimingPolicy timingPolicy)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A nonempty session ID is required.", nameof(sessionId));
        if (requestId == Guid.Empty)
            throw new ArgumentException("A nonempty request ID is required.", nameof(requestId));
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        ArgumentNullException.ThrowIfNull(actualTarget);
        if (outputEpoch == Guid.Empty)
            throw new ArgumentException("A nonempty output epoch is required.", nameof(outputEpoch));
        ArgumentNullException.ThrowIfNull(timingPolicy);
        if (!Enum.IsDefined(measurementKind))
            throw new ArgumentOutOfRangeException(nameof(measurementKind));
        if (!IsCompatible(stream.Kind, measurementKind))
        {
            throw new ArgumentException(
                $"Measurement '{measurementKind}' is not valid for stream '{stream.Kind}'.",
                nameof(measurementKind));
        }

        SessionId = sessionId;
        RequestId = requestId;
        Generation = generation;
        ActualTarget = actualTarget;
        OutputEpoch = outputEpoch;
        Stream = stream;
        MeasurementKind = measurementKind;
        TimingPolicy = timingPolicy;
    }

    public Guid SessionId { get; }
    public Guid RequestId { get; }
    public long Generation { get; }
    public CaptureTarget ActualTarget { get; }
    public Guid OutputEpoch { get; }
    public CaptureLivenessStream Stream { get; }
    public CaptureLivenessMeasurementKind MeasurementKind { get; }
    public CaptureLivenessTimingPolicy TimingPolicy { get; }

    private static bool IsCompatible(
        CaptureLivenessStreamKind streamKind,
        CaptureLivenessMeasurementKind measurementKind) =>
        (streamKind, measurementKind) switch
        {
            (CaptureLivenessStreamKind.SourceVideo,
                CaptureLivenessMeasurementKind.SourceDeliveredVideoFrames) => true,
            (CaptureLivenessStreamKind.SourceAudio,
                CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples) => true,
            (CaptureLivenessStreamKind.EncodedOutputVideo,
                CaptureLivenessMeasurementKind.EncodedOutputVideoFrames) => true,
            (CaptureLivenessStreamKind.EncodedOutputAudio,
                CaptureLivenessMeasurementKind.EncodedOutputAudioSamples) => true,
            (CaptureLivenessStreamKind.AggregateOutput,
                CaptureLivenessMeasurementKind.OutputBytes) => true,
            _ => false,
        };
}

public enum CaptureLivenessTelemetryAvailability
{
    Available,
    Missing,
    Failed,
    ProviderExecutionTimedOut,
}

/// <summary>
/// One ordered telemetry observation. Available telemetry carries a nonnegative cumulative
/// counter; missing and failed telemetry deliberately carry no counter. ObservedTimestamp must
/// come from the same monotonic <see cref="TimeProvider"/> domain as the tracker. Delivery time is
/// intentionally separate so delayed telemetry cannot be mistaken for fresh liveness evidence.
/// </summary>
public sealed record CaptureLivenessObservation
{
    public CaptureLivenessObservation(
        CaptureLivenessIdentity identity,
        long sequence,
        long observedTimestamp,
        CaptureLivenessTelemetryAvailability availability,
        long? counter)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (sequence <= 0)
            throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!Enum.IsDefined(availability))
            throw new ArgumentOutOfRangeException(nameof(availability));
        if (availability == CaptureLivenessTelemetryAvailability.Available)
        {
            if (counter is null or < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(counter),
                    "Available telemetry requires a nonnegative cumulative counter.");
            }
        }
        else if (counter is not null)
        {
            throw new ArgumentException(
                "Missing, failed, or execution-timed-out telemetry cannot carry a counter.",
                nameof(counter));
        }

        Identity = identity;
        Sequence = sequence;
        ObservedTimestamp = observedTimestamp;
        Availability = availability;
        Counter = counter;
    }

    public CaptureLivenessIdentity Identity { get; }
    public long Sequence { get; }
    public long ObservedTimestamp { get; }
    public CaptureLivenessTelemetryAvailability Availability { get; }
    public long? Counter { get; }

    public static CaptureLivenessObservation Available(
        CaptureLivenessIdentity identity,
        long sequence,
        long observedTimestamp,
        long counter) =>
        new(identity, sequence, observedTimestamp, CaptureLivenessTelemetryAvailability.Available, counter);

    public static CaptureLivenessObservation Missing(
        CaptureLivenessIdentity identity,
        long sequence,
        long observedTimestamp) =>
        new(identity, sequence, observedTimestamp, CaptureLivenessTelemetryAvailability.Missing, null);

    public static CaptureLivenessObservation Failed(
        CaptureLivenessIdentity identity,
        long sequence,
        long observedTimestamp) =>
        new(identity, sequence, observedTimestamp, CaptureLivenessTelemetryAvailability.Failed, null);

    public static CaptureLivenessObservation ProviderExecutionTimedOut(
        CaptureLivenessIdentity identity,
        long sequence,
        long observedTimestamp) =>
        new(
            identity,
            sequence,
            observedTimestamp,
            CaptureLivenessTelemetryAvailability.ProviderExecutionTimedOut,
            null);
}

public enum CaptureLivenessStatus
{
    AwaitingProgress,
    Live,
    StartupTimedOut,
    Stalled,
    Unobservable,
    InvalidTelemetry,
    Stopped,
}

public enum CaptureLivenessReason
{
    None,
    StartupTimeout,
    StallTimeout,
    TelemetryMissing,
    TelemetryFailed,
    TelemetryTimeout,
    CounterDecreased,
    Stopped,
    ProviderExecutionTimeout,
}

/// <summary>
/// Immutable evidence about one counter. Even when status is <see cref="CaptureLivenessStatus.Live"/>,
/// this snapshot proves only that the named counter increased. It makes no claim about correct
/// source/device binding, visual content or change, audible audio, or audio/video synchronization.
/// Timestamp values are meaningful only with the tracker's injected <see cref="TimeProvider"/>.
/// </summary>
public sealed record CaptureLivenessSnapshot(
    CaptureLivenessIdentity Identity,
    CaptureLivenessStatus Status,
    CaptureLivenessReason Reason,
    long StartedTimestamp,
    long LastTelemetryTimestamp,
    long? LastProgressTimestamp,
    long? LastAcceptedSequence,
    long? AcceptedCounter);

public enum CaptureLivenessUpdateDisposition
{
    Accepted,
    Ignored,
}

public sealed record CaptureLivenessUpdate(
    CaptureLivenessUpdateDisposition Disposition,
    CaptureLivenessSnapshot Before,
    CaptureLivenessSnapshot After)
{
    public bool WasAccepted => Disposition == CaptureLivenessUpdateDisposition.Accepted;
}
