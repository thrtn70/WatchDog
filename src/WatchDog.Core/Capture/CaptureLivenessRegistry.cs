namespace WatchDog.Core.Capture;

/// <summary>
/// Owns the exact current output epoch and ordered counter tracker for each capture/owner/stream
/// lane. Providers are always read outside registry locks. Counter advancement does not prove
/// correct source/device binding, visual content or change, audible audio, or A/V sync. No native
/// adapters or qualified production thresholds are installed by this registry.
/// </summary>
public sealed class CaptureLivenessRegistry : ICaptureLivenessFailureClaimSource, IDisposable
{
    private const int DefaultReadDiagnosticCapacity = 256;
    private readonly object _sync = new();
    private readonly object _readDiagnosticsSync = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _readDiagnosticCapacity;
    private readonly Dictionary<LaneKey, LaneState> _lanes = [];
    private readonly HashSet<CaptureLivenessContext> _captureFences = [];
    private readonly Queue<CaptureLivenessReadDiagnostic> _readDiagnostics = [];
    private readonly SemaphoreSlim _changeSignal = new(0, 1);
    private long _nextReadDiagnosticSequence;
    private long _droppedReadDiagnosticCount;
    private bool _disposed;

    public CaptureLivenessRegistry()
        : this(TimeProvider.System)
    {
    }

    public CaptureLivenessRegistry(TimeProvider timeProvider)
        : this(timeProvider, DefaultReadDiagnosticCapacity)
    {
    }

    internal CaptureLivenessRegistry(TimeProvider timeProvider, int readDiagnosticCapacity)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (readDiagnosticCapacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(readDiagnosticCapacity));
        _readDiagnosticCapacity = readDiagnosticCapacity;
    }

    public CaptureLivenessRegistrationResult Register(CaptureLivenessRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = LaneKey.From(registration.Owner, registration.Identity);
            if (_captureFences.Contains(key.Context))
                return new(CaptureLivenessRegistrationDisposition.Rejected, null);
            if (_lanes.TryGetValue(key, out var existing))
                return Result(CaptureLivenessRegistrationDisposition.Rejected, existing.Current);

            var lane = new LaneState(registration.AdmittedAudioDevice);
            lane.Current = CurrentRegistration.Create(registration, _timeProvider);
            _lanes.Add(key, lane);
            SignalChange();
            return Result(CaptureLivenessRegistrationDisposition.Registered, lane.Current);
        }
    }

    public CaptureLivenessRegistrationResult Replace(
        CaptureLivenessPollTarget expectedCurrent,
        CaptureLivenessRegistration replacement)
    {
        ArgumentNullException.ThrowIfNull(expectedCurrent);
        ArgumentNullException.ThrowIfNull(replacement);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var expectedKey = LaneKey.From(expectedCurrent.Owner, expectedCurrent.Identity);
            var replacementKey = LaneKey.From(replacement.Owner, replacement.Identity);
            _lanes.TryGetValue(expectedKey, out var lane);
            if (expectedKey != replacementKey ||
                _captureFences.Contains(expectedKey.Context) ||
                lane is null ||
                lane.Current is null ||
                !Matches(lane.Current, expectedCurrent) ||
                lane.Current.FailureClaimed ||
                lane.PendingReplacement is not null ||
                lane.AdmittedAudioDevice != replacement.AdmittedAudioDevice ||
                lane.RetiredEpochs.Contains(replacement.Identity.OutputEpoch) ||
                !CanReplaceIdentity(expectedCurrent.Identity, replacement.Identity))
            {
                return Result(CaptureLivenessRegistrationDisposition.Rejected, lane?.Current);
            }

            var current = lane.Current;
            var trackerReplacement = current.Tracker.ReplaceOutput(
                expectedCurrent.Identity,
                replacement.Identity);
            if (!trackerReplacement.WasAccepted)
                return Result(CaptureLivenessRegistrationDisposition.Rejected, current);

            lane.RetiredEpochs.Add(current.Registration.Identity.OutputEpoch);
            lane.Current = current.Replace(replacement);
            SignalChange();
            return Result(CaptureLivenessRegistrationDisposition.Replaced, lane.Current);
        }
    }

    public CaptureLivenessReplacementReservation? BeginReplace(
        CaptureLivenessPollTarget expectedCurrent,
        CaptureLivenessRegistration replacement)
    {
        ArgumentNullException.ThrowIfNull(expectedCurrent);
        ArgumentNullException.ThrowIfNull(replacement);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var expectedKey = LaneKey.From(expectedCurrent.Owner, expectedCurrent.Identity);
            var replacementKey = LaneKey.From(replacement.Owner, replacement.Identity);
            _lanes.TryGetValue(expectedKey, out var lane);
            if (expectedKey != replacementKey ||
                _captureFences.Contains(expectedKey.Context) ||
                lane is null ||
                lane.Current is null ||
                !Matches(lane.Current, expectedCurrent) ||
                lane.Current.FailureClaimed ||
                lane.PendingReplacement is not null ||
                lane.AdmittedAudioDevice != replacement.AdmittedAudioDevice ||
                lane.RetiredEpochs.Contains(replacement.Identity.OutputEpoch) ||
                !CanReplaceIdentity(expectedCurrent.Identity, replacement.Identity))
            {
                return null;
            }

            var current = lane.Current;
            var reservation = new CaptureLivenessReplacementReservation(
                Guid.NewGuid(),
                expectedCurrent,
                replacement);
            current.Revision = checked(current.Revision + 1);
            lane.PendingReplacement = new PendingReplacement(
                reservation,
                current.Revision);
            SignalChange();
            return reservation;
        }
    }

    public CaptureLivenessRegistrationResult CommitReplace(
        CaptureLivenessReplacementReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = LaneKey.From(
                reservation.ExpectedCurrent.Owner,
                reservation.ExpectedCurrent.Identity);
            if (!_lanes.TryGetValue(key, out var lane))
            {
                return new(CaptureLivenessRegistrationDisposition.Ignored, null);
            }

            var pendingReplacement = lane.PendingReplacement;
            if (pendingReplacement is null ||
                !ReferenceEquals(pendingReplacement.Reservation, reservation))
            {
                return new(CaptureLivenessRegistrationDisposition.Ignored, null);
            }

            var current = lane.Current;
            var replacement = reservation.Replacement;
            if (_captureFences.Contains(key.Context) ||
                current is null ||
                !Matches(current, reservation.ExpectedCurrent) ||
                current.FailureClaimed ||
                current.Revision != pendingReplacement.ReservedRevision ||
                lane.AdmittedAudioDevice != replacement.AdmittedAudioDevice ||
                lane.RetiredEpochs.Contains(replacement.Identity.OutputEpoch) ||
                !CanReplaceIdentity(current.Registration.Identity, replacement.Identity))
            {
                return Result(CaptureLivenessRegistrationDisposition.Rejected, current);
            }

            var trackerReplacement = current.Tracker.ReplaceOutput(
                reservation.ExpectedCurrent.Identity,
                replacement.Identity);
            if (!trackerReplacement.WasAccepted)
                return Result(CaptureLivenessRegistrationDisposition.Rejected, current);

            lane.RetiredEpochs.Add(current.Registration.Identity.OutputEpoch);
            lane.Current = current.Replace(replacement);
            lane.PendingReplacement = null;
            SignalChange();
            return Result(CaptureLivenessRegistrationDisposition.Replaced, lane.Current);
        }
    }

    public CaptureLivenessRegistrationResult AbortReplace(
        CaptureLivenessReplacementReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = LaneKey.From(
                reservation.ExpectedCurrent.Owner,
                reservation.ExpectedCurrent.Identity);
            if (!_lanes.TryGetValue(key, out var lane))
            {
                return new(CaptureLivenessRegistrationDisposition.Ignored, null);
            }

            var pendingReplacement = lane.PendingReplacement;
            if (pendingReplacement is null ||
                !ReferenceEquals(pendingReplacement.Reservation, reservation))
            {
                return new(CaptureLivenessRegistrationDisposition.Ignored, null);
            }

            if (lane.Current is not null)
            {
                lane.Current.Tracker.Stop(lane.Current.Registration.Identity);
                lane.RetiredEpochs.Add(lane.Current.Registration.Identity.OutputEpoch);
            }
            lane.RetiredEpochs.Add(reservation.Replacement.Identity.OutputEpoch);
            lane.Current = null;
            lane.PendingReplacement = null;
            SignalChange();
            return new(CaptureLivenessRegistrationDisposition.Retired, null);
        }
    }

    public CaptureLivenessRegistrationResult Retire(CaptureLivenessPollTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = LaneKey.From(target.Owner, target.Identity);
            if (!_lanes.TryGetValue(key, out var lane) ||
                lane.PendingReplacement is not null ||
                lane.Current is null ||
                !Matches(lane.Current, target))
            {
                return new(CaptureLivenessRegistrationDisposition.Ignored, null);
            }

            var current = lane.Current;
            current.Tracker.Stop(current.Registration.Identity);
            lane.RetiredEpochs.Add(current.Registration.Identity.OutputEpoch);
            lane.Current = null;
            SignalChange();
            return new(CaptureLivenessRegistrationDisposition.Retired, null);
        }
    }

    public IReadOnlyList<CaptureLivenessPollTarget> GetPollTargets()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _lanes.Values
                .Where(lane => lane.PendingReplacement is null)
                .Select(lane => lane.Current)
                .Where(current => current is not null &&
                                  !current.FailureClaimed &&
                                  !_captureFences.Contains(CaptureLivenessContext.From(
                                      current.Registration.Identity)))
                .Select(current => ToTarget(current!))
                .ToArray();
        }
    }

    /// <summary>
    /// Returns an ordered immutable copy without changing the journal. This remains available
    /// after disposal so diagnostics from reads that were already admitted can still be observed.
    /// </summary>
    public CaptureLivenessReadDiagnosticsSnapshot SnapshotReadDiagnostics()
    {
        lock (_readDiagnosticsSync)
        {
            return new CaptureLivenessReadDiagnosticsSnapshot(
                _readDiagnostics.ToArray(),
                _droppedReadDiagnosticCount);
        }
    }

    /// <summary>
    /// Atomically returns and clears retained events and their overflow count. This remains
    /// available after disposal; later completion of an already-started read begins a new batch.
    /// </summary>
    public CaptureLivenessReadDiagnosticsSnapshot DrainReadDiagnostics()
    {
        lock (_readDiagnosticsSync)
        {
            var snapshot = new CaptureLivenessReadDiagnosticsSnapshot(
                _readDiagnostics.ToArray(),
                _droppedReadDiagnosticCount);
            _readDiagnostics.Clear();
            _droppedReadDiagnosticCount = 0;
            return snapshot;
        }
    }

    public async ValueTask<CaptureLivenessFailureCandidate?> ObserveAsync(
        CaptureLivenessPollTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        PendingRead pending;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!TryCaptureRead(target, out pending))
                return null;
        }

        var diagnostics = StartReadDiagnostics(pending, CaptureLivenessReadPhase.Observation);
        var readContext = new CaptureLivenessReadContext(_timeProvider);
        var read = await ReadAsync(
            pending.Provider,
            readContext,
            pending.Identity.TimingPolicy.ProviderExecutionTimeout,
            cancellationToken,
            diagnostics).ConfigureAwait(false);

        CaptureLivenessFailureCandidate? candidate = null;
        CaptureLivenessReadPostDisposition disposition;
        lock (_sync)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                disposition = CaptureLivenessReadPostDisposition.CallerCanceled;
            }
            else if (!TryRevalidate(pending, out var current))
            {
                disposition = CaptureLivenessReadPostDisposition.RejectedStale;
            }
            else if (!ApplyReading(current, read.Reading, read.Reading.ObservedTimestamp))
            {
                disposition = CaptureLivenessReadPostDisposition.RejectedByTracker;
            }
            else
            {
                disposition = CaptureLivenessReadPostDisposition.ObservationApplied;
                candidate = IsFailClosed(current.Tracker.Snapshot.Status)
                    ? Candidate(current)
                    : null;
            }
        }

        var callerCanceled = disposition == CaptureLivenessReadPostDisposition.CallerCanceled;
        diagnostics.Completed(read.CompletionOutcome, callerCanceled, disposition);
        if (callerCanceled)
            cancellationToken.ThrowIfCancellationRequested();
        return candidate;
    }

    public async ValueTask<CaptureLivenessFailureClaim?> TryClaimAsync(
        CaptureLivenessFailureCandidate candidate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        PendingRead pending;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!TryCaptureClaim(candidate, out pending))
                return null;
        }

        var diagnostics = StartReadDiagnostics(pending, CaptureLivenessReadPhase.ClaimRevalidation);
        var readContext = new CaptureLivenessReadContext(_timeProvider);
        var read = await ReadAsync(
            pending.Provider,
            readContext,
            pending.Identity.TimingPolicy.ProviderExecutionTimeout,
            cancellationToken,
            diagnostics).ConfigureAwait(false);

        CaptureLivenessFailureClaim? claim = null;
        CaptureLivenessReadPostDisposition disposition;
        lock (_sync)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                disposition = CaptureLivenessReadPostDisposition.CallerCanceled;
            }
            else if (!TryRevalidate(pending, out var current) ||
                current.FailureClaimed ||
                current.Revision != candidate.EvidenceRevision ||
                current.Tracker.Snapshot != candidate.Evidence)
            {
                disposition = CaptureLivenessReadPostDisposition.RejectedStale;
            }
            else if (!ApplyReading(current, read.Reading, read.Reading.ObservedTimestamp))
            {
                disposition = CaptureLivenessReadPostDisposition.RejectedByTracker;
            }
            else
            {
                if (IsFailClosed(current.Tracker.Snapshot.Status))
                {
                    disposition = CaptureLivenessReadPostDisposition.ClaimAccepted;
                    current.FailureClaimed = true;
                    _captureFences.Add(CaptureLivenessContext.From(current.Registration.Identity));
                    SignalChange();
                    claim = new CaptureLivenessFailureClaim(Candidate(current));
                }
                else
                {
                    disposition = CaptureLivenessReadPostDisposition.ClaimRejectedAfterRecovery;
                }
            }
        }

        var callerCanceled = disposition == CaptureLivenessReadPostDisposition.CallerCanceled;
        diagnostics.Completed(read.CompletionOutcome, callerCanceled, disposition);
        if (callerCanceled)
            cancellationToken.ThrowIfCancellationRequested();
        return claim;
    }

    public Task WaitForChangeAsync(CancellationToken cancellationToken) =>
        _changeSignal.WaitAsync(cancellationToken);

    public void ConsumePendingChange()
    {
        if (!_disposed)
            _changeSignal.Wait(0);
    }

    public void CompleteCapture(CaptureLivenessContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_sync)
        {
            if (_disposed)
                return;

            var keys = _lanes.Keys.Where(key => key.Context == context).ToArray();
            foreach (var key in keys)
            {
                var lane = _lanes[key];
                if (lane.Current is not null)
                    lane.Current.Tracker.Stop(lane.Current.Registration.Identity);
                _lanes.Remove(key);
            }
            var fenceAdded = _captureFences.Add(context);
            if (keys.Length != 0 || fenceAdded)
                SignalChange();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var lane in _lanes.Values)
            {
                if (lane.Current is not null)
                    lane.Current.Tracker.Stop(lane.Current.Registration.Identity);
            }
            _lanes.Clear();
            _captureFences.Clear();
        }
        _changeSignal.Dispose();
    }

    private bool TryCaptureRead(
        CaptureLivenessPollTarget target,
        out PendingRead pending)
    {
        var key = LaneKey.From(target.Owner, target.Identity);
        if (!_lanes.TryGetValue(key, out var lane) ||
            _captureFences.Contains(key.Context) ||
            lane.PendingReplacement is not null ||
            lane.Current is null ||
            lane.Current.FailureClaimed ||
            !Matches(lane.Current, target))
        {
            pending = default;
            return false;
        }

        pending = new PendingRead(
            key,
            lane.Current.Registration.RegistrationId,
            lane.Current.Registration.Identity,
            lane.Current.Revision,
            lane.Current.Registration.Provider);
        return true;
    }

    private bool TryRevalidate(PendingRead pending, out CurrentRegistration current)
    {
        if (_disposed ||
            !_lanes.TryGetValue(pending.Key, out var lane) ||
            _captureFences.Contains(pending.Key.Context) ||
            lane.PendingReplacement is not null ||
            lane.Current is null ||
            lane.Current.FailureClaimed ||
            lane.Current.Registration.RegistrationId != pending.RegistrationId ||
            lane.Current.Registration.Identity != pending.Identity ||
            lane.Current.Revision != pending.Revision)
        {
            current = null!;
            return false;
        }

        current = lane.Current;
        return true;
    }

    private bool TryCaptureClaim(
        CaptureLivenessFailureCandidate candidate,
        out PendingRead pending)
    {
        var key = LaneKey.From(candidate.Owner, candidate.Identity);
        if (!_lanes.TryGetValue(key, out var lane) ||
            _captureFences.Contains(key.Context) ||
            lane.PendingReplacement is not null ||
            lane.Current is null ||
            lane.Current.FailureClaimed ||
            lane.Current.Registration.RegistrationId != candidate.RegistrationId ||
            lane.Current.Registration.Owner != candidate.Owner ||
            lane.Current.Registration.Identity != candidate.Identity ||
            lane.Current.Registration.AdmittedAudioDevice != candidate.AdmittedAudioDevice ||
            lane.Current.Revision != candidate.EvidenceRevision ||
            lane.Current.Tracker.Snapshot != candidate.Evidence)
        {
            pending = default;
            return false;
        }

        pending = new PendingRead(
            key,
            lane.Current.Registration.RegistrationId,
            lane.Current.Registration.Identity,
            lane.Current.Revision,
            lane.Current.Registration.Provider);
        return true;
    }

    private bool ApplyReading(
        CurrentRegistration current,
        CaptureLivenessTelemetryReading reading,
        long observedTimestamp)
    {
        var sequence = checked(current.NextSequence + 1);
        var observation = reading.Availability switch
        {
            CaptureLivenessTelemetryAvailability.Available => CaptureLivenessObservation.Available(
                current.Registration.Identity,
                sequence,
                observedTimestamp,
                reading.Counter!.Value),
            CaptureLivenessTelemetryAvailability.Missing => CaptureLivenessObservation.Missing(
                current.Registration.Identity,
                sequence,
                observedTimestamp),
            CaptureLivenessTelemetryAvailability.Failed => CaptureLivenessObservation.Failed(
                current.Registration.Identity,
                sequence,
                observedTimestamp),
            CaptureLivenessTelemetryAvailability.ProviderExecutionTimedOut =>
                CaptureLivenessObservation.ProviderExecutionTimedOut(
                    current.Registration.Identity,
                    sequence,
                    observedTimestamp),
            _ => throw new ArgumentOutOfRangeException(nameof(reading)),
        };
        var update = current.Tracker.Observe(observation);
        if (!update.WasAccepted)
            return false;

        current.NextSequence = sequence;
        current.Revision = checked(current.Revision + 1);
        return true;
    }

    private async ValueTask<DrainedRead> ReadAsync(
        ICaptureLivenessTelemetryProvider provider,
        CaptureLivenessReadContext context,
        TimeSpan? providerExecutionTimeout,
        CancellationToken cancellationToken,
        ReadDiagnosticScope diagnostics)
    {
        if (providerExecutionTimeout is null)
            return await ReadWithoutDeadlineAsync(provider, context, cancellationToken)
                .ConfigureAwait(false);

        var timeout = providerExecutionTimeout.Value;
        var startedTimestamp = _timeProvider.GetTimestamp();
        var timedOutReading = new TaskCompletionSource<CaptureLivenessTelemetryReading>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadlineCancellation = new CancellationTokenSource(timeout, _timeProvider);
        using var deadlineRegistration = deadlineCancellation.Token.Register(
            () =>
            {
                diagnostics.DeadlineReached();
                timedOutReading.TrySetResult(context.ProviderExecutionTimedOut());
            });
        using var providerCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadlineCancellation.Token);

        CaptureLivenessTelemetryReading reading = default;
        CaptureLivenessReadCompletionOutcome completionOutcome;
        try
        {
            reading = await provider.ReadAsync(context, providerCancellation.Token)
                .ConfigureAwait(false);
            completionOutcome = GetCompletionOutcome(reading);
        }
        catch (OperationCanceledException)
        {
            completionOutcome = CaptureLivenessReadCompletionOutcome.Canceled;
            reading = context.Failed();
        }
        catch
        {
            completionOutcome = CaptureLivenessReadCompletionOutcome.Faulted;
            reading = context.Failed();
        }

        var elapsed = _timeProvider.GetElapsedTime(
            startedTimestamp,
            _timeProvider.GetTimestamp());
        if (deadlineCancellation.IsCancellationRequested || elapsed >= timeout)
        {
            diagnostics.DeadlineReached();
            var deadlineReading = timedOutReading.Task.IsCompletedSuccessfully
                ? timedOutReading.Task.Result
                : context.ProviderExecutionTimedOut();
            return new DrainedRead(deadlineReading, completionOutcome);
        }

        return new DrainedRead(NormalizeReading(reading, context), completionOutcome);
    }

    private static async ValueTask<DrainedRead> ReadWithoutDeadlineAsync(
        ICaptureLivenessTelemetryProvider provider,
        CaptureLivenessReadContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var reading = await provider.ReadAsync(context, cancellationToken).ConfigureAwait(false);
            return new DrainedRead(
                NormalizeReading(reading, context),
                GetCompletionOutcome(reading));
        }
        catch (OperationCanceledException)
        {
            return new DrainedRead(
                context.Failed(),
                CaptureLivenessReadCompletionOutcome.Canceled);
        }
        catch
        {
            return new DrainedRead(
                context.Failed(),
                CaptureLivenessReadCompletionOutcome.Faulted);
        }
    }

    private static CaptureLivenessReadCompletionOutcome GetCompletionOutcome(
        CaptureLivenessTelemetryReading reading) =>
        reading.Availability switch
        {
            CaptureLivenessTelemetryAvailability.Available when reading.Counter is >= 0 =>
                CaptureLivenessReadCompletionOutcome.ReturnedAvailable,
            CaptureLivenessTelemetryAvailability.Missing when reading.Counter is null =>
                CaptureLivenessReadCompletionOutcome.ReturnedMissing,
            CaptureLivenessTelemetryAvailability.Failed when reading.Counter is null =>
                CaptureLivenessReadCompletionOutcome.ReturnedFailed,
            CaptureLivenessTelemetryAvailability.ProviderExecutionTimedOut when reading.Counter is null =>
                CaptureLivenessReadCompletionOutcome.ReturnedProviderExecutionTimedOut,
            _ => CaptureLivenessReadCompletionOutcome.ReturnedInvalid,
        };

    private static CaptureLivenessTelemetryReading NormalizeReading(
        CaptureLivenessTelemetryReading reading,
        CaptureLivenessReadContext context) =>
        reading.Availability switch
        {
            CaptureLivenessTelemetryAvailability.Available when reading.Counter is >= 0 => reading,
            CaptureLivenessTelemetryAvailability.Missing when reading.Counter is null => reading,
            CaptureLivenessTelemetryAvailability.Failed when reading.Counter is null => reading,
            CaptureLivenessTelemetryAvailability.ProviderExecutionTimedOut when reading.Counter is null =>
                reading,
            _ => context.Failed(),
        };

    private ReadDiagnosticScope StartReadDiagnostics(
        PendingRead pending,
        CaptureLivenessReadPhase phase) =>
        new(this, pending, phase);

    private void AppendReadDiagnostic(
        ReadDiagnosticScope scope,
        CaptureLivenessReadEventKind kind,
        CaptureLivenessReadCompletionOutcome? completionOutcome = null,
        bool callerCancellationRequested = false,
        CaptureLivenessReadPostDisposition? postReadDisposition = null)
    {
        lock (_readDiagnosticsSync)
        {
            var diagnostic = new CaptureLivenessReadDiagnostic(
                ++_nextReadDiagnosticSequence,
                scope.ReadId,
                scope.Phase,
                kind,
                scope.Pending.RegistrationId,
                scope.Pending.Key.Owner,
                scope.Pending.Identity.SessionId,
                scope.Pending.Identity.RequestId,
                scope.Pending.Identity.Generation,
                scope.Pending.Identity.OutputEpoch,
                scope.Pending.Identity.Stream,
                scope.Pending.Identity.MeasurementKind,
                scope.Pending.Identity.TimingPolicy.ProviderExecutionTimeout,
                _timeProvider.GetTimestamp(),
                completionOutcome,
                callerCancellationRequested,
                postReadDisposition);
            if (_readDiagnostics.Count == _readDiagnosticCapacity)
            {
                _readDiagnostics.Dequeue();
                _droppedReadDiagnosticCount++;
            }
            _readDiagnostics.Enqueue(diagnostic);
        }
    }

    private void SignalChange()
    {
        if (_changeSignal.CurrentCount == 0)
            _changeSignal.Release();
    }

    private static bool Matches(CurrentRegistration current, CaptureLivenessPollTarget target) =>
        current.Registration.RegistrationId == target.RegistrationId &&
        current.Registration.Owner == target.Owner &&
        current.Registration.Identity == target.Identity &&
        current.Registration.PollInterval == target.PollInterval &&
        current.Registration.AdmittedAudioDevice == target.AdmittedAudioDevice;

    private static bool CanReplaceIdentity(
        CaptureLivenessIdentity expectedCurrent,
        CaptureLivenessIdentity replacement) =>
        expectedCurrent.SessionId == replacement.SessionId &&
        expectedCurrent.RequestId == replacement.RequestId &&
        expectedCurrent.Generation == replacement.Generation &&
        expectedCurrent.ActualTarget == replacement.ActualTarget &&
        expectedCurrent.Stream == replacement.Stream &&
        expectedCurrent.MeasurementKind == replacement.MeasurementKind &&
        expectedCurrent.TimingPolicy == replacement.TimingPolicy &&
        expectedCurrent.OutputEpoch != replacement.OutputEpoch;

    private static bool IsFailClosed(CaptureLivenessStatus status) =>
        status is CaptureLivenessStatus.StartupTimedOut or
            CaptureLivenessStatus.Stalled or
            CaptureLivenessStatus.Unobservable or
            CaptureLivenessStatus.InvalidTelemetry;

    private static CaptureLivenessFailureCandidate Candidate(CurrentRegistration current) =>
        new(
            current.Registration.RegistrationId,
            current.Registration.Owner,
            current.Registration.Identity,
            current.Registration.AdmittedAudioDevice,
            current.Revision,
            current.Tracker.Snapshot);

    private static CaptureLivenessPollTarget ToTarget(CurrentRegistration current) =>
        new(
            current.Registration.RegistrationId,
            current.Registration.Owner,
            current.Registration.Identity,
            current.Registration.PollInterval,
            current.Registration.AdmittedAudioDevice);

    private static CaptureLivenessRegistrationResult Result(
        CaptureLivenessRegistrationDisposition disposition,
        CurrentRegistration? current) =>
        new(disposition, current is null ? null : ToTarget(current));

    private sealed class LaneState(WatchDog.Core.Devices.AdmittedCaptureDevice? admittedAudioDevice)
    {
        public WatchDog.Core.Devices.AdmittedCaptureDevice? AdmittedAudioDevice { get; } = admittedAudioDevice;
        public HashSet<Guid> RetiredEpochs { get; } = [];
        public CurrentRegistration? Current { get; set; }
        public PendingReplacement? PendingReplacement { get; set; }
    }

    private sealed class CurrentRegistration
    {
        private CurrentRegistration(
            CaptureLivenessRegistration registration,
            CaptureLivenessTracker tracker,
            long revision)
        {
            Registration = registration;
            Tracker = tracker;
            Revision = revision;
        }

        public CaptureLivenessRegistration Registration { get; }
        public CaptureLivenessTracker Tracker { get; }
        public long Revision { get; set; }
        public long NextSequence { get; set; }
        public bool FailureClaimed { get; set; }

        public static CurrentRegistration Create(
            CaptureLivenessRegistration registration,
            TimeProvider timeProvider) =>
            new(registration, new CaptureLivenessTracker(registration.Identity, timeProvider), 0);

        public CurrentRegistration Replace(CaptureLivenessRegistration replacement) =>
            new(replacement, Tracker, checked(Revision + 1));
    }

    private sealed record LaneKey(
        CaptureLivenessContext Context,
        CaptureLivenessPipelineOwner Owner,
        CaptureLivenessStream Stream)
    {
        public static LaneKey From(
            CaptureLivenessPipelineOwner owner,
            CaptureLivenessIdentity identity) =>
            new(CaptureLivenessContext.From(identity), owner, identity.Stream);
    }

    private readonly record struct DrainedRead(
        CaptureLivenessTelemetryReading Reading,
        CaptureLivenessReadCompletionOutcome CompletionOutcome);

    private sealed class ReadDiagnosticScope
    {
        private readonly CaptureLivenessRegistry _registry;
        private int _deadlineReached;
        private int _completed;

        public ReadDiagnosticScope(
            CaptureLivenessRegistry registry,
            PendingRead pending,
            CaptureLivenessReadPhase phase)
        {
            _registry = registry;
            Pending = pending;
            Phase = phase;
            ReadId = Guid.NewGuid();
            _registry.AppendReadDiagnostic(this, CaptureLivenessReadEventKind.Started);
        }

        public PendingRead Pending { get; }
        public CaptureLivenessReadPhase Phase { get; }
        public Guid ReadId { get; }

        public void DeadlineReached()
        {
            if (Interlocked.Exchange(ref _deadlineReached, 1) == 0)
                _registry.AppendReadDiagnostic(this, CaptureLivenessReadEventKind.DeadlineReached);
        }

        public void Completed(
            CaptureLivenessReadCompletionOutcome completionOutcome,
            bool callerCancellationRequested,
            CaptureLivenessReadPostDisposition postReadDisposition)
        {
            if (Interlocked.Exchange(ref _completed, 1) == 0)
            {
                _registry.AppendReadDiagnostic(
                    this,
                    CaptureLivenessReadEventKind.Completed,
                    completionOutcome,
                    callerCancellationRequested,
                    postReadDisposition);
            }
        }
    }

    private readonly record struct PendingRead(
        LaneKey Key,
        Guid RegistrationId,
        CaptureLivenessIdentity Identity,
        long Revision,
        ICaptureLivenessTelemetryProvider Provider);

    private sealed record PendingReplacement(
        CaptureLivenessReplacementReservation Reservation,
        long ReservedRevision);
}
