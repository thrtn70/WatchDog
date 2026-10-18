using WatchDog.Core.Capture;

namespace WatchDog.Core.Recording;

/// <summary>
/// Explicit, still-unqualified observation policy for one session-segment output. Production
/// leaves this absent until native timing evidence establishes suitable thresholds.
/// </summary>
public sealed record SessionSegmentLivenessPolicy
{
    public SessionSegmentLivenessPolicy(
        TimeSpan pollInterval,
        CaptureLivenessTimingPolicy timingPolicy)
    {
        if (pollInterval <= TimeSpan.Zero || pollInterval == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
        TimingPolicy = timingPolicy ?? throw new ArgumentNullException(nameof(timingPolicy));
        PollInterval = pollInterval;
    }

    public TimeSpan PollInterval { get; }
    public CaptureLivenessTimingPolicy TimingPolicy { get; }
}

/// <summary>
/// Owns registry admission for the session recorder's current physical output epoch. Native
/// ownership remains in <see cref="ObsSessionRecorder"/>; this component stores no native object.
/// </summary>
internal sealed class SessionSegmentLivenessOwner
{
    private readonly CaptureLivenessRegistry? _registry;
    private readonly SessionSegmentLivenessPolicy? _policy;
    private readonly Func<Guid, CaptureLivenessReadContext, CancellationToken,
        ValueTask<CaptureLivenessTelemetryReading>> _readCounter;

    private CaptureLivenessContext? _context;
    private CaptureLivenessPollTarget? _current;
    private CaptureLivenessReplacementReservation? _pendingReplacement;

    public SessionSegmentLivenessOwner(
        CaptureLivenessRegistry? registry,
        SessionSegmentLivenessPolicy? policy,
        Func<Guid, CaptureLivenessReadContext, CancellationToken,
            ValueTask<CaptureLivenessTelemetryReading>> readCounter)
    {
        if (registry is null && policy is not null)
        {
            throw new ArgumentException(
                "A session-segment liveness policy requires a registry.");
        }

        _registry = registry;
        _policy = policy;
        _readCounter = readCounter ?? throw new ArgumentNullException(nameof(readCounter));
    }

    public bool Enabled => _registry is not null && _policy is not null;

    internal CaptureLivenessPollTarget? Current => _current;

    public void RegisterInitial(CaptureSessionContext context, Guid outputEpoch)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!Enabled)
            return;
        if (_current is not null || _pendingReplacement is not null || _context is not null)
            throw new InvalidOperationException("A session-segment liveness lane is already owned.");

        var frozenContext = CaptureLivenessContext.From(context);
        var registration = CreateRegistration(frozenContext, outputEpoch);
        var result = _registry!.Register(registration);
        if (result.Disposition != CaptureLivenessRegistrationDisposition.Registered ||
            result.Current is null)
        {
            throw new InvalidOperationException(
                "The session output started, but its exact liveness lane could not be admitted.");
        }

        _context = frozenContext;
        _current = result.Current;
    }

    public CaptureLivenessReplacementReservation? BeginReplacement(Guid outputEpoch)
    {
        if (!Enabled)
            return null;
        if (_context is null || _current is null || _pendingReplacement is not null)
            throw new InvalidOperationException("No exact current session output can be replaced.");

        var replacement = CreateRegistration(_context, outputEpoch);
        var reservation = _registry!.BeginReplace(_current, replacement)
            ?? throw new InvalidOperationException(
                "The exact current session output could not reserve its successor.");
        _pendingReplacement = reservation;
        return reservation;
    }

    public void CommitReplacement(CaptureLivenessReplacementReservation? reservation)
    {
        if (reservation is null)
            return;
        if (!ReferenceEquals(_pendingReplacement, reservation) &&
            _pendingReplacement != reservation)
        {
            throw new InvalidOperationException("The session output replacement token is stale.");
        }

        var result = _registry!.CommitReplace(reservation);
        if (result.Disposition != CaptureLivenessRegistrationDisposition.Replaced ||
            result.Current is null)
        {
            throw new InvalidOperationException(
                "The started successor output could not publish its liveness lane.");
        }

        _current = result.Current;
        _pendingReplacement = null;
    }

    public void AbortReplacement(CaptureLivenessReplacementReservation? reservation)
    {
        if (reservation is null || _pendingReplacement != reservation)
            return;

        _registry!.AbortReplace(reservation);
        _pendingReplacement = null;
        _current = null;
        _context = null;
    }

    public void RetireCurrent()
    {
        if (!Enabled)
            return;

        if (_pendingReplacement is not null)
        {
            AbortReplacement(_pendingReplacement);
            return;
        }

        if (_current is not null)
            _registry!.Retire(_current);
        _current = null;
        _context = null;
    }

    public bool IsReadableEpoch(Guid outputEpoch) =>
        Enabled &&
        _pendingReplacement is null &&
        _current is { } current &&
        current.Identity.OutputEpoch == outputEpoch;

    private CaptureLivenessRegistration CreateRegistration(
        CaptureLivenessContext context,
        Guid outputEpoch)
    {
        if (outputEpoch == Guid.Empty)
            throw new ArgumentException("A nonempty output epoch is required.", nameof(outputEpoch));

        var identity = new CaptureLivenessIdentity(
            context.SessionId,
            context.RequestId,
            context.Generation,
            context.ActualTarget,
            outputEpoch,
            new CaptureLivenessStream(CaptureLivenessStreamKind.EncodedOutputVideo, 0),
            CaptureLivenessMeasurementKind.EncodedOutputVideoFrames,
            _policy!.TimingPolicy);
        return new CaptureLivenessRegistration(
            Guid.NewGuid(),
            CaptureLivenessPipelineOwner.SessionSegment,
            identity,
            _policy.PollInterval,
            new EpochProvider(outputEpoch, _readCounter));
    }

    private sealed class EpochProvider(
        Guid outputEpoch,
        Func<Guid, CaptureLivenessReadContext, CancellationToken,
            ValueTask<CaptureLivenessTelemetryReading>> readCounter)
        : ICaptureLivenessTelemetryProvider
    {
        public ValueTask<CaptureLivenessTelemetryReading> ReadAsync(
            CaptureLivenessReadContext context,
            CancellationToken cancellationToken) =>
            readCounter(outputEpoch, context, cancellationToken);
    }
}
