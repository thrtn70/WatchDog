using WatchDog.Core.Devices;

namespace WatchDog.Core.Capture;

/// <summary>
/// Names the pipeline component that owns a liveness counter. Audio owners name the admitted
/// role precisely; they do not imply that audio is audible or bound to the requested device.
/// </summary>
public enum CaptureLivenessPipelineOwner
{
    Replay,
    SessionSegment,
    WebcamIntermediate,
    AdmittedDesktopAudio,
    AdmittedMicrophone,
}

/// <summary>
/// Result of one source-free telemetry read. Available means only that a cumulative counter was
/// returned; it does not prove correct binding, media content, audible audio, or A/V sync.
/// </summary>
public readonly record struct CaptureLivenessTelemetryReading
{
    private CaptureLivenessTelemetryReading(
        CaptureLivenessTelemetryAvailability availability,
        long? counter,
        long observedTimestamp)
    {
        Availability = availability;
        Counter = counter;
        ObservedTimestamp = observedTimestamp;
    }

    public CaptureLivenessTelemetryAvailability Availability { get; }
    public long? Counter { get; }
    public long ObservedTimestamp { get; }

    internal static CaptureLivenessTelemetryReading Create(
        CaptureLivenessTelemetryAvailability availability,
        long? counter,
        long observedTimestamp) =>
        new(availability, counter, observedTimestamp);
}

/// <summary>
/// Registry-owned clock context for one provider read. Providers must create the reading at the
/// instant they measure or decide availability, before any later return delay.
/// </summary>
public sealed class CaptureLivenessReadContext
{
    private readonly TimeProvider _timeProvider;

    internal CaptureLivenessReadContext(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public CaptureLivenessTelemetryReading Available(long counter)
    {
        if (counter < 0)
            throw new ArgumentOutOfRangeException(nameof(counter));
        return CaptureLivenessTelemetryReading.Create(
            CaptureLivenessTelemetryAvailability.Available,
            counter,
            _timeProvider.GetTimestamp());
    }

    public CaptureLivenessTelemetryReading Missing() =>
        CaptureLivenessTelemetryReading.Create(
            CaptureLivenessTelemetryAvailability.Missing,
            null,
            _timeProvider.GetTimestamp());

    public CaptureLivenessTelemetryReading Failed() =>
        CaptureLivenessTelemetryReading.Create(
            CaptureLivenessTelemetryAvailability.Failed,
            null,
            _timeProvider.GetTimestamp());

    public CaptureLivenessTelemetryReading ProviderExecutionTimedOut() =>
        CaptureLivenessTelemetryReading.Create(
            CaptureLivenessTelemetryAvailability.ProviderExecutionTimedOut,
            null,
            _timeProvider.GetTimestamp());
}

public interface ICaptureLivenessTelemetryProvider
{
    /// <summary>
    /// Reads one counter without starting detached native work. Implementations must promptly
    /// honor cancellation during asynchronous waits and keep synchronous native work bounded.
    /// Cancellation drain is the monotonic interval from the cancellation request until this read
    /// settles, releases its ownership gate, and leaves no work associated with the read able to
    /// run later. Implementations must not detach work beyond that interval. Registry deadlines
    /// still await and drain this operation before accepting evidence.
    /// </summary>
    ValueTask<CaptureLivenessTelemetryReading> ReadAsync(
        CaptureLivenessReadContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Names the registry operation that owns a provider read. Claim revalidation is a fresh read of
/// telemetry before a previously offered failure candidate can be claimed.
/// </summary>
public enum CaptureLivenessReadPhase
{
    Observation,
    ClaimRevalidation,
}

public enum CaptureLivenessReadEventKind
{
    Started,
    DeadlineReached,
    Completed,
}

/// <summary>
/// Describes how provider execution completed, independently of whether the registry later
/// accepted the reading. No exception, provider, target, or device details are retained.
/// </summary>
public enum CaptureLivenessReadCompletionOutcome
{
    ReturnedAvailable,
    ReturnedMissing,
    ReturnedFailed,
    ReturnedProviderExecutionTimedOut,
    ReturnedInvalid,
    Canceled,
    Faulted,
}

/// <summary>
/// Describes what the registry did after provider execution drained.
/// </summary>
public enum CaptureLivenessReadPostDisposition
{
    ObservationApplied,
    ClaimAccepted,
    ClaimRejectedAfterRecovery,
    RejectedStale,
    RejectedByTracker,
    CallerCanceled,
}

/// <summary>
/// One privacy-safe registry read lifecycle event. Correlation is limited to immutable capture
/// IDs and enum/value metadata; targets, display strings, devices, provider IDs, exceptions,
/// paths, handles, and callbacks are deliberately excluded.
/// </summary>
public sealed class CaptureLivenessReadDiagnostic
{
    internal CaptureLivenessReadDiagnostic(
        long sequence,
        Guid readId,
        CaptureLivenessReadPhase phase,
        CaptureLivenessReadEventKind kind,
        Guid registrationId,
        CaptureLivenessPipelineOwner owner,
        Guid sessionId,
        Guid requestId,
        long generation,
        Guid outputEpoch,
        CaptureLivenessStream stream,
        CaptureLivenessMeasurementKind measurementKind,
        TimeSpan? configuredExecutionTimeout,
        long registryTimestamp,
        CaptureLivenessReadCompletionOutcome? completionOutcome,
        bool callerCancellationRequested,
        CaptureLivenessReadPostDisposition? postReadDisposition)
    {
        Sequence = sequence;
        ReadId = readId;
        Phase = phase;
        Kind = kind;
        RegistrationId = registrationId;
        Owner = owner;
        SessionId = sessionId;
        RequestId = requestId;
        Generation = generation;
        OutputEpoch = outputEpoch;
        Stream = stream;
        MeasurementKind = measurementKind;
        ConfiguredExecutionTimeout = configuredExecutionTimeout;
        RegistryTimestamp = registryTimestamp;
        CompletionOutcome = completionOutcome;
        CallerCancellationRequested = callerCancellationRequested;
        PostReadDisposition = postReadDisposition;
    }

    public long Sequence { get; }
    public Guid ReadId { get; }
    public CaptureLivenessReadPhase Phase { get; }
    public CaptureLivenessReadEventKind Kind { get; }
    public Guid RegistrationId { get; }
    public CaptureLivenessPipelineOwner Owner { get; }
    public Guid SessionId { get; }
    public Guid RequestId { get; }
    public long Generation { get; }
    public Guid OutputEpoch { get; }
    public CaptureLivenessStream Stream { get; }
    public CaptureLivenessMeasurementKind MeasurementKind { get; }
    public TimeSpan? ConfiguredExecutionTimeout { get; }
    public long RegistryTimestamp { get; }
    public CaptureLivenessReadCompletionOutcome? CompletionOutcome { get; }
    public bool CallerCancellationRequested { get; }
    public CaptureLivenessReadPostDisposition? PostReadDisposition { get; }
}

/// <summary>
/// Stable ordered copy of the bounded diagnostics journal. DroppedCount counts events evicted
/// before this snapshot; draining atomically resets both retained events and this count.
/// </summary>
public sealed class CaptureLivenessReadDiagnosticsSnapshot
{
    internal CaptureLivenessReadDiagnosticsSnapshot(
        CaptureLivenessReadDiagnostic[] events,
        long droppedCount)
    {
        Events = Array.AsReadOnly(events);
        DroppedCount = droppedCount;
    }

    public IReadOnlyList<CaptureLivenessReadDiagnostic> Events { get; }
    public long DroppedCount { get; }
}

public sealed record CaptureLivenessContext(
    Guid SessionId,
    Guid RequestId,
    long Generation,
    CaptureTarget ActualTarget)
{
    public static CaptureLivenessContext From(CaptureLivenessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new(
            identity.SessionId,
            identity.RequestId,
            identity.Generation,
            identity.ActualTarget);
    }

    public static CaptureLivenessContext From(CaptureSessionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new(
            context.SessionId,
            context.Request.RequestId,
            context.Generation,
            context.ActualTarget);
    }
}

/// <summary>
/// Immutable admission of one provider into one exact capture/output/owner/stream lane.
/// Poll cadence and failure thresholds are supplied by the adapter and require later native and
/// workload qualification; this registry supplies no guessed production thresholds.
/// </summary>
public sealed record CaptureLivenessRegistration
{
    public CaptureLivenessRegistration(
        Guid registrationId,
        CaptureLivenessPipelineOwner owner,
        CaptureLivenessIdentity identity,
        TimeSpan pollInterval,
        ICaptureLivenessTelemetryProvider provider,
        AdmittedCaptureDevice? admittedAudioDevice = null)
    {
        if (registrationId == Guid.Empty)
            throw new ArgumentException("A nonempty registration ID is required.", nameof(registrationId));
        if (!Enum.IsDefined(owner))
            throw new ArgumentOutOfRangeException(nameof(owner));
        ArgumentNullException.ThrowIfNull(identity);
        if (pollInterval <= TimeSpan.Zero || pollInterval == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        ArgumentNullException.ThrowIfNull(provider);
        ValidateCapability(owner, identity, admittedAudioDevice);

        RegistrationId = registrationId;
        Owner = owner;
        Identity = identity;
        PollInterval = pollInterval;
        Provider = provider;
        AdmittedAudioDevice = admittedAudioDevice;
    }

    public Guid RegistrationId { get; }
    public CaptureLivenessPipelineOwner Owner { get; }
    public CaptureLivenessIdentity Identity { get; }
    public TimeSpan PollInterval { get; }
    public ICaptureLivenessTelemetryProvider Provider { get; }
    public AdmittedCaptureDevice? AdmittedAudioDevice { get; }

    private static void ValidateCapability(
        CaptureLivenessPipelineOwner owner,
        CaptureLivenessIdentity identity,
        AdmittedCaptureDevice? admittedAudioDevice)
    {
        var isAudioOwner = owner is
            CaptureLivenessPipelineOwner.AdmittedDesktopAudio or
            CaptureLivenessPipelineOwner.AdmittedMicrophone;
        if (isAudioOwner)
        {
            ArgumentNullException.ThrowIfNull(admittedAudioDevice);
            var expectedRole = owner == CaptureLivenessPipelineOwner.AdmittedDesktopAudio
                ? CaptureDeviceRole.DesktopAudio
                : CaptureDeviceRole.Microphone;
            if (admittedAudioDevice.Role != expectedRole ||
                identity.Stream.Kind != CaptureLivenessStreamKind.SourceAudio ||
                identity.MeasurementKind != CaptureLivenessMeasurementKind.SourceDeliveredAudioSamples)
            {
                throw new ArgumentException(
                    "An admitted-audio owner requires its exact admitted role and source-audio samples.",
                    nameof(admittedAudioDevice));
            }
            return;
        }

        if (admittedAudioDevice is not null)
        {
            throw new ArgumentException(
                "Only admitted-audio owners may carry admitted device identity.",
                nameof(admittedAudioDevice));
        }

        // This first production boundary admits only the currently supportable encoded-video
        // seam. Source video, encoded audio, and aggregate bytes remain theoretical tracker
        // measurements until passive adapters and reset semantics are qualified.
        var isOutputOwner = owner is
            CaptureLivenessPipelineOwner.Replay or
            CaptureLivenessPipelineOwner.SessionSegment or
            CaptureLivenessPipelineOwner.WebcamIntermediate;
        var supported = isOutputOwner &&
            identity.Stream.Kind == CaptureLivenessStreamKind.EncodedOutputVideo &&
            identity.MeasurementKind == CaptureLivenessMeasurementKind.EncodedOutputVideoFrames;
        if (!supported)
        {
            throw new ArgumentException(
                $"Owner '{owner}' cannot admit stream '{identity.Stream.Kind}'.",
                nameof(identity));
        }
    }
}

public sealed record CaptureLivenessPollTarget(
    Guid RegistrationId,
    CaptureLivenessPipelineOwner Owner,
    CaptureLivenessIdentity Identity,
    TimeSpan PollInterval,
    AdmittedCaptureDevice? AdmittedAudioDevice);

/// <summary>
/// Immutable registry-issued capability for one exact-current replacement. A reservation does not
/// publish the replacement or start its liveness deadlines; only a successful commit does so.
/// </summary>
public sealed class CaptureLivenessReplacementReservation
{
    internal CaptureLivenessReplacementReservation(
        Guid reservationId,
        CaptureLivenessPollTarget expectedCurrent,
        CaptureLivenessRegistration replacement)
    {
        ReservationId = reservationId;
        ExpectedCurrent = expectedCurrent;
        Replacement = replacement;
    }

    public Guid ReservationId { get; }
    public CaptureLivenessPollTarget ExpectedCurrent { get; }
    public CaptureLivenessRegistration Replacement { get; }
}

/// <summary>
/// Exact fail-closed evidence offered to the lifecycle owner. Revision and snapshot are both
/// required so a delayed candidate cannot claim a recovered or replaced registration.
/// </summary>
public sealed record CaptureLivenessFailureCandidate(
    Guid RegistrationId,
    CaptureLivenessPipelineOwner Owner,
    CaptureLivenessIdentity Identity,
    AdmittedCaptureDevice? AdmittedAudioDevice,
    long EvidenceRevision,
    CaptureLivenessSnapshot Evidence);

public sealed record CaptureLivenessFailureClaim(CaptureLivenessFailureCandidate Candidate);

public interface ICaptureLivenessFailureClaimSource
{
    ValueTask<CaptureLivenessFailureClaim?> TryClaimAsync(
        CaptureLivenessFailureCandidate candidate,
        CancellationToken cancellationToken);

    void CompleteCapture(CaptureLivenessContext context);
}

public enum CaptureLivenessRegistrationDisposition
{
    Registered,
    Replaced,
    Retired,
    Rejected,
    Ignored,
}

public sealed record CaptureLivenessRegistrationResult(
    CaptureLivenessRegistrationDisposition Disposition,
    CaptureLivenessPollTarget? Current)
{
    public bool Succeeded => Disposition is
        CaptureLivenessRegistrationDisposition.Registered or
        CaptureLivenessRegistrationDisposition.Replaced or
        CaptureLivenessRegistrationDisposition.Retired;
}
