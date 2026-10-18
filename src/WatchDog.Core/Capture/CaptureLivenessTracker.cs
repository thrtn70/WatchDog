namespace WatchDog.Core.Capture;

/// <summary>
/// Tracks monotonic counter progress for exactly one frozen capture/output/stream identity.
/// This pure managed component does not inspect media or native state. A live result proves only
/// counter advancement; it does not prove correct source/device binding, visual content or change,
/// audible audio, or audio/video synchronization.
/// </summary>
public sealed class CaptureLivenessTracker
{
    private readonly object _sync = new();
    private readonly TimeProvider _timeProvider;
    private readonly HashSet<Guid> _retiredOutputEpochs = [];

    private CaptureLivenessSnapshot _snapshot;
    private long _lastProviderTimestamp;
    private bool _hasProgress;
    private bool _stopped;

    public CaptureLivenessTracker(
        CaptureLivenessIdentity identity,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(identity);
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

        var timestamp = _timeProvider.GetTimestamp();
        _lastProviderTimestamp = timestamp;
        _snapshot = new CaptureLivenessSnapshot(
            identity,
            CaptureLivenessStatus.AwaitingProgress,
            CaptureLivenessReason.None,
            timestamp,
            timestamp,
            null,
            null,
            null);
    }

    public CaptureLivenessSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Replaces the physical output epoch while retaining every other frozen identity field.
    /// The caller must name the exact current identity it intends to replace, so a delayed command
    /// cannot displace a newer epoch. The prior epoch is permanently retired and the new epoch
    /// starts with no counter baseline.
    /// </summary>
    public CaptureLivenessUpdate ReplaceOutput(
        CaptureLivenessIdentity expectedCurrent,
        CaptureLivenessIdentity replacement)
    {
        ArgumentNullException.ThrowIfNull(expectedCurrent);
        ArgumentNullException.ThrowIfNull(replacement);
        lock (_sync)
        {
            var before = _snapshot;
            if (_stopped ||
                expectedCurrent != before.Identity ||
                !HasSameNonEpochIdentity(before.Identity, replacement) ||
                replacement.OutputEpoch == before.Identity.OutputEpoch ||
                _retiredOutputEpochs.Contains(replacement.OutputEpoch) ||
                !TryReadTimestamp(out var timestamp))
            {
                return Ignored(before);
            }

            _retiredOutputEpochs.Add(before.Identity.OutputEpoch);
            _hasProgress = false;
            var after = new CaptureLivenessSnapshot(
                replacement,
                CaptureLivenessStatus.AwaitingProgress,
                CaptureLivenessReason.None,
                timestamp,
                timestamp,
                null,
                null,
                null);
            Volatile.Write(ref _snapshot, after);
            return Accepted(before, after);
        }
    }

    /// <summary>
    /// Applies one strictly ordered observation after exact identity correlation.
    /// Foreign, duplicate, backward-sequence, retired-epoch, and backward-time observations
    /// are ignored before they can change telemetry or progress baselines.
    /// </summary>
    public CaptureLivenessUpdate Observe(CaptureLivenessObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_sync)
        {
            var before = _snapshot;
            if (_stopped ||
                observation.Identity != before.Identity ||
                (before.LastAcceptedSequence is long lastSequence &&
                  observation.Sequence <= lastSequence) ||
                !TryReadTimestamp(out var deliveryTimestamp) ||
                observation.ObservedTimestamp > deliveryTimestamp ||
                observation.ObservedTimestamp < before.LastTelemetryTimestamp)
            {
                return Ignored(before);
            }

            var updated = before with
            {
                LastTelemetryTimestamp = observation.ObservedTimestamp,
                LastAcceptedSequence = observation.Sequence,
            };

            switch (observation.Availability)
            {
                case CaptureLivenessTelemetryAvailability.Missing:
                    updated = updated with
                    {
                        Status = CaptureLivenessStatus.Unobservable,
                        Reason = CaptureLivenessReason.TelemetryMissing,
                    };
                    updated = EvaluateAt(updated, deliveryTimestamp, preserveExplicitTelemetryState: true);
                    break;

                case CaptureLivenessTelemetryAvailability.Failed:
                    updated = updated with
                    {
                        Status = CaptureLivenessStatus.Unobservable,
                        Reason = CaptureLivenessReason.TelemetryFailed,
                    };
                    updated = EvaluateAt(updated, deliveryTimestamp, preserveExplicitTelemetryState: true);
                    break;

                case CaptureLivenessTelemetryAvailability.ProviderExecutionTimedOut:
                    updated = updated with
                    {
                        Status = CaptureLivenessStatus.Unobservable,
                        Reason = CaptureLivenessReason.ProviderExecutionTimeout,
                    };
                    updated = EvaluateAt(updated, deliveryTimestamp, preserveExplicitTelemetryState: true);
                    break;

                case CaptureLivenessTelemetryAvailability.Available:
                    updated = ApplyAvailableCounter(
                        updated,
                        observation.Counter!.Value,
                        observation.ObservedTimestamp,
                        deliveryTimestamp);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(observation));
            }

            Volatile.Write(ref _snapshot, updated);
            return Accepted(before, updated);
        }
    }

    /// <summary>
    /// Evaluates telemetry silence and progress deadlines for the exact current identity.
    /// </summary>
    public CaptureLivenessUpdate Evaluate(CaptureLivenessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_sync)
        {
            var before = _snapshot;
            if (_stopped || identity != before.Identity || !TryReadTimestamp(out var timestamp))
                return Ignored(before);

            var after = EvaluateAt(before, timestamp, preserveExplicitTelemetryState: true);
            Volatile.Write(ref _snapshot, after);
            return Accepted(before, after);
        }
    }

    /// <summary>
    /// Terminally stops the exact current identity. Later observations, evaluations, and output
    /// replacements are ignored and cannot resurrect the tracker.
    /// </summary>
    public CaptureLivenessUpdate Stop(CaptureLivenessIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_sync)
        {
            var before = _snapshot;
            if (_stopped || identity != before.Identity)
                return Ignored(before);

            _stopped = true;
            var after = before with
            {
                Status = CaptureLivenessStatus.Stopped,
                Reason = CaptureLivenessReason.Stopped,
            };
            Volatile.Write(ref _snapshot, after);
            return Accepted(before, after);
        }
    }

    private CaptureLivenessSnapshot ApplyAvailableCounter(
        CaptureLivenessSnapshot snapshot,
        long counter,
        long observedTimestamp,
        long deliveryTimestamp)
    {
        if (snapshot.AcceptedCounter is null)
        {
            return EvaluateAt(
                snapshot with { AcceptedCounter = counter },
                deliveryTimestamp,
                preserveExplicitTelemetryState: false);
        }

        if (counter < snapshot.AcceptedCounter.Value)
        {
            return snapshot with
            {
                Status = CaptureLivenessStatus.InvalidTelemetry,
                Reason = CaptureLivenessReason.CounterDecreased,
            };
        }

        if (counter > snapshot.AcceptedCounter.Value)
        {
            _hasProgress = true;
            return EvaluateAt(snapshot with
            {
                LastProgressTimestamp = observedTimestamp,
                AcceptedCounter = counter,
            }, deliveryTimestamp, preserveExplicitTelemetryState: false);
        }

        return EvaluateAt(snapshot, deliveryTimestamp, preserveExplicitTelemetryState: false);
    }

    private CaptureLivenessSnapshot EvaluateAt(
        CaptureLivenessSnapshot snapshot,
        long timestamp,
        bool preserveExplicitTelemetryState)
    {
        var policy = snapshot.Identity.TimingPolicy;
        if (preserveExplicitTelemetryState &&
            snapshot.Reason == CaptureLivenessReason.ProviderExecutionTimeout)
        {
            return snapshot;
        }

        if (Elapsed(snapshot.LastTelemetryTimestamp, timestamp) >= policy.TelemetryTimeout)
        {
            return snapshot with
            {
                Status = CaptureLivenessStatus.Unobservable,
                Reason = CaptureLivenessReason.TelemetryTimeout,
            };
        }

        if (preserveExplicitTelemetryState &&
            (snapshot.Reason is CaptureLivenessReason.TelemetryMissing or
             CaptureLivenessReason.TelemetryFailed or
             CaptureLivenessReason.ProviderExecutionTimeout or
             CaptureLivenessReason.CounterDecreased or
             CaptureLivenessReason.TelemetryTimeout))
        {
            return snapshot;
        }

        if (!_hasProgress)
        {
            return Elapsed(snapshot.StartedTimestamp, timestamp) >= policy.StartupTimeout
                ? snapshot with
                {
                    Status = CaptureLivenessStatus.StartupTimedOut,
                    Reason = CaptureLivenessReason.StartupTimeout,
                }
                : snapshot with
                {
                    Status = CaptureLivenessStatus.AwaitingProgress,
                    Reason = CaptureLivenessReason.None,
                };
        }

        var lastProgress = snapshot.LastProgressTimestamp
            ?? throw new InvalidOperationException("Live counter progress requires a timestamp.");
        return Elapsed(lastProgress, timestamp) >= policy.StallTimeout
            ? snapshot with
            {
                Status = CaptureLivenessStatus.Stalled,
                Reason = CaptureLivenessReason.StallTimeout,
            }
            : snapshot with
            {
                Status = CaptureLivenessStatus.Live,
                Reason = CaptureLivenessReason.None,
            };
    }

    private bool TryReadTimestamp(out long timestamp)
    {
        timestamp = _timeProvider.GetTimestamp();
        if (timestamp < _lastProviderTimestamp)
            return false;

        _lastProviderTimestamp = timestamp;
        return true;
    }

    private TimeSpan Elapsed(long startTimestamp, long endTimestamp) =>
        _timeProvider.GetElapsedTime(startTimestamp, endTimestamp);

    private static bool HasSameNonEpochIdentity(
        CaptureLivenessIdentity current,
        CaptureLivenessIdentity replacement) =>
        current.SessionId == replacement.SessionId &&
        current.RequestId == replacement.RequestId &&
        current.Generation == replacement.Generation &&
        current.ActualTarget == replacement.ActualTarget &&
        current.Stream == replacement.Stream &&
        current.MeasurementKind == replacement.MeasurementKind &&
        current.TimingPolicy == replacement.TimingPolicy;

    private static CaptureLivenessUpdate Accepted(
        CaptureLivenessSnapshot before,
        CaptureLivenessSnapshot after) =>
        new(CaptureLivenessUpdateDisposition.Accepted, before, after);

    private static CaptureLivenessUpdate Ignored(CaptureLivenessSnapshot snapshot) =>
        new(CaptureLivenessUpdateDisposition.Ignored, snapshot, snapshot);
}
