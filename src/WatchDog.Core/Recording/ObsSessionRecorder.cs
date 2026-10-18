using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using ObsKit.NET.Encoders;
using ObsKit.NET.Outputs;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Sessions;

namespace WatchDog.Core.Recording;

public sealed class ObsSessionRecorder : ISessionRecorder, ISessionSegmentReceiptSource
{
    public static CaptureLivenessProviderCapabilities SegmentFrameCounterCapabilities { get; } =
        new(
            "obs.session-segment.total-frames",
            maximumSynchronousWork: null,
            cooperativeAsyncCancellation: true,
            maximumCancellationDrain: null);

    private const int DisposeStateActive = 0;
    private const int DisposeStateDisposing = 1;
    private const int DisposeStateDisposed = 2;

    private static readonly AsyncLocal<ObsSessionRecorder?> ScheduledCallbackOwner = new();

    private readonly ILogger<ObsSessionRecorder> _logger;
    private readonly SessionRecordingConfig _config;
    private SessionRecordingConfig _activeConfig;
    private readonly int _videoBitrate;
    private readonly int _audioBitrate;
    private readonly AudioTrackPlan _audioTrackPlan;
    private readonly SessionSegmentLivenessOwner _segmentLiveness;
    private VideoEncoder? _videoEncoder;
    private readonly List<AudioTrackEncoderBinding> _audioEncoders = [];

    private RecordingOutput? _recordingOutput;
    private bool _currentOutputStarted;
    private Timer? _segmentTimer;
    private Timer? _maxDurationTimer;
    private readonly Stopwatch _elapsed = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _scheduledCallbackSync = new();
    private string _outputDirectory = string.Empty;
    private GameInfo? _currentGame;
    private int _segmentIndex;
    private string _normalizedFileFormat = string.Empty;
    private long _recordingGeneration;
    private Guid? _activeOutputEpoch;
    private Guid? _activeMediaId;
    private CaptureSessionContext? _captureContext;
    private ProducerReceiptState? _producerReceiptState;
    private readonly List<SessionSegmentReceipt> _finalizedSegmentReceipts = [];
    private int _disposeState;
    private int _scheduledCallbacksInFlight;

    public bool IsRecording { get; private set; }
    public bool HasActiveResources =>
        _recordingOutput is not null || _videoEncoder is not null || _audioEncoders.Count != 0;
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public string? CurrentOutputPath { get; private set; }

    public IReadOnlyList<SessionSegmentReceipt> GetFinalizedSegments(CaptureSessionContext expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        IReadOnlyList<SessionSegmentReceipt> snapshot = Array.Empty<SessionSegmentReceipt>();
        RunUnderOperationGate(
            _lock,
            () => snapshot = SnapshotFinalizedSegments(_finalizedSegmentReceipts, expected));
        return snapshot;
    }

    public event Action<string>? SegmentSaved;
    public event Action<SessionSegmentReceipt>? SegmentFinalized;
    public event Action<string>? Error;
    public event Action? Stopped;

    public ObsSessionRecorder(
        SessionRecordingConfig config,
        int videoBitrate,
        int audioBitrate,
        AudioTrackPlan audioTrackPlan,
        ILogger<ObsSessionRecorder> logger,
        CaptureLivenessRegistry? livenessRegistry = null,
        SessionSegmentLivenessPolicy? livenessPolicy = null)
    {
        var startupPolicy = new CaptureStartPolicy(new CaptureConfig
        {
            Bitrate = videoBitrate,
            AudioBitrate = audioBitrate,
        }, new(), config).Sanitized();
        _config = startupPolicy.Recording;
        _activeConfig = _config;
        _videoBitrate = startupPolicy.Capture.Bitrate;
        _audioBitrate = startupPolicy.Capture.AudioBitrate;
        _audioTrackPlan = audioTrackPlan;
        _logger = logger;
        _segmentLiveness = new SessionSegmentLivenessOwner(
            livenessRegistry,
            livenessPolicy,
            ReadSegmentFrameCounterAsync);
    }

    public Task StartAsync(string outputDirectory, GameInfo? game, CancellationToken ct = default) =>
        StartCoreAsync(outputDirectory, game, captureContext: null, ct);

    public Task StartAsync(
        string outputDirectory,
        GameInfo? game,
        CaptureSessionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return StartCoreAsync(outputDirectory, game, context, ct);
    }

    private async Task StartCoreAsync(
        string outputDirectory,
        GameInfo? game,
        CaptureSessionContext? captureContext,
        CancellationToken ct)
    {
        if (Volatile.Read(ref _disposeState) != DisposeStateActive)
            throw new ObjectDisposedException(nameof(ObsSessionRecorder));

        Exception? segmentFailure = null;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposeState) != DisposeStateActive)
                throw new ObjectDisposedException(nameof(ObsSessionRecorder));
            if (IsRecording)
            {
                _logger.LogWarning("Session recording already in progress");
                return;
            }
            if (HasActiveResources)
                throw new InvalidOperationException(
                    "Session recording still owns resources from an earlier incomplete cleanup.");
            if (_segmentLiveness.Enabled && captureContext is null)
            {
                throw new InvalidOperationException(
                    "Enabled session-segment liveness requires immutable capture context before native start.");
            }
            var startPolicy = captureContext?.Request.StartPolicy;
            _activeConfig = startPolicy?.Recording ?? _config;
            _normalizedFileFormat = NormalizeFileFormat(
                _activeConfig.FileFormat,
                _audioTrackPlan.SeparateAudioTracks);

            // Create OBS encoders lazily on first recording start — OBS must be initialized
            // by the capture engine before encoder creation succeeds.
            _videoEncoder ??= VideoEncoder.CreateX264(
                name: "Session x264", bitrate: startPolicy?.Capture.Bitrate ?? _videoBitrate, preset: "veryfast");
            foreach (var route in _audioTrackPlan.Encoders)
            {
                var name = _audioTrackPlan.EncoderCount == 1
                    ? "Session Mix"
                    : route == _audioTrackPlan.Desktop
                        ? "Session Desktop"
                        : "Session Microphone";
                _audioEncoders.Add(new AudioTrackEncoderBinding(
                    route,
                    AudioEncoder.CreateAac(name, startPolicy?.Capture.AudioBitrate ?? _audioBitrate, route.EncoderMixerIndex)));
            }

            _outputDirectory = outputDirectory;
            _currentGame = game;
            _captureContext = captureContext;
            _segmentIndex = 0;
            _finalizedSegmentReceipts.Clear();
            _elapsed.Restart();
            _producerReceiptState = BeginProducerReceiptState(captureContext);

            Directory.CreateDirectory(outputDirectory);
            try
            {
                var outputEpoch = Guid.NewGuid();
                StartSegment(outputEpoch);
                if (_captureContext is not null)
                    _segmentLiveness.RegisterInitial(_captureContext, outputEpoch);
            }
            catch (Exception ex)
            {
                segmentFailure = ex;
            }

            if (segmentFailure is null)
            {
                var generation = NextRecordingGenerationLocked();

                // Segment splitting timer
                if (_activeConfig.SegmentDurationMinutes > 0)
                {
                    _segmentTimer = new Timer(
                        _ => QueueScheduledCallback(() => SplitSegmentSafeAsync(generation)),
                        null,
                        TimeSpan.FromMinutes(_activeConfig.SegmentDurationMinutes),
                        Timeout.InfiniteTimeSpan);
                }

                // Max duration timer (auto-stop)
                if (_activeConfig.MaxDurationMinutes > 0)
                {
                    _maxDurationTimer = new Timer(
                        _ => QueueScheduledCallback(() => StopSafeAsync(generation)),
                        null,
                        TimeSpan.FromMinutes(_activeConfig.MaxDurationMinutes),
                        Timeout.InfiniteTimeSpan);
                }

                IsRecording = true;
                _logger.LogInformation("Session recording started: {Output}", CurrentOutputPath);
            }
        }
        finally
        {
            _lock.Release();
        }

        if (segmentFailure is not null)
        {
            Error?.Invoke($"Recording segment failed: {segmentFailure.Message}");
            ExceptionDispatchInfo.Capture(segmentFailure).Throw();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        var result = await StopCoreAsync(ct, expectedGeneration: null).ConfigureAwait(false);
        PublishStopResult(result);
        if (result is not null)
            ThrowCleanupFailures(result.CleanupFailures);
    }

    private async Task<StopResult?> StopCoreAsync(CancellationToken ct, long? expectedGeneration)
    {
        if (Volatile.Read(ref _disposeState) == DisposeStateDisposed)
            return null;

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposeState) == DisposeStateDisposed)
                return null;
            if (expectedGeneration is long generation &&
                !ShouldProcessScheduledCallback(
                    generation,
                    _recordingGeneration,
                    IsRecording,
                    Volatile.Read(ref _disposeState) != DisposeStateActive))
            {
                return null;
            }

            return StopLocked();
        }
        finally
        {
            _lock.Release();
        }
    }

    private void StartSegment(Guid outputEpoch)
    {
        if (outputEpoch == Guid.Empty)
            throw new ArgumentException("A nonempty output epoch is required.", nameof(outputEpoch));

        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        var suffix = _segmentIndex > 0 ? $"_part{_segmentIndex + 1}" : "";
        var fileName = $"{timestamp}{suffix}_{Guid.NewGuid():N}.{_normalizedFileFormat}";
        CurrentOutputPath = Path.GetFullPath(Path.Combine(_outputDirectory, fileName));

        try
        {
            var format = _normalizedFileFormat switch
            {
                "mkv" => RecordingFormat.Mkv,
                "flv" => RecordingFormat.Flv,
                "mp4" => RecordingFormat.Mp4,
                _ => throw new InvalidOperationException("The validated session format was lost."),
            };

            _recordingOutput = new RecordingOutput($"WatchDog Session {_segmentIndex}")
                .SetPath(CurrentOutputPath)
                .SetFormat(format);
            _currentOutputStarted = false;
            _recordingOutput.WithVideoEncoder(_videoEncoder!, takeOwnership: false);
            foreach (var binding in _audioEncoders)
            {
                _recordingOutput.WithAudioEncoder(
                    binding.Encoder,
                    takeOwnership: false,
                    track: binding.Route.OutputTrackIndex);
            }

            if (!_recordingOutput.Start())
                throw new IOException($"Session output failed to start: {_recordingOutput.LastError}");
            _currentOutputStarted = true;
            _activeMediaId = Guid.NewGuid();
            _activeOutputEpoch = outputEpoch;

            _logger.LogInformation("Recording segment {Index}: {Path}",
                _segmentIndex, Path.GetFileName(CurrentOutputPath));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording segment");
            throw;
        }
    }

    private async Task SplitSegmentSafeAsync(long expectedGeneration)
    {
        try
        {
            FinalizedSegmentNotification? finalizedSegment = null;
            StopResult? stopResult = null;
            Exception? splitFailure = null;

            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!ShouldProcessScheduledCallback(
                        expectedGeneration,
                        _recordingGeneration,
                        IsRecording,
                        Volatile.Read(ref _disposeState) != DisposeStateActive))
                {
                    return;
                }

                try
                {
                    SplitSegment(out finalizedSegment);
                }
                catch (Exception ex)
                {
                    splitFailure = ex;
                    // Cleanup stays in this critical section so another start cannot
                    // advance the generation between the failure and its stop.
                    stopResult = StopLocked();
                }
            }
            finally
            {
                _lock.Release();
            }

            PublishFinalizedSegment(finalizedSegment);
            PublishStopResult(stopResult);

            if (stopResult is not null && stopResult.CleanupFailures.Count > 0)
            {
                _logger.LogError(
                    new AggregateException(stopResult.CleanupFailures),
                    "Failed to clean up interrupted segment split");
            }

            if (splitFailure is not null)
            {
                _logger.LogError(splitFailure, "Error during segment split");
                Error?.Invoke($"Session recording stopped after a segment failure: {splitFailure.Message}");
            }
        }
        catch (Exception ex)
        {
            // Timer callbacks are fire-and-forget; contain every exception here.
            _logger.LogError(ex, "Unhandled error in the segment timer callback");
        }
        finally
        {
            await RearmSegmentTimerAsync(expectedGeneration).ConfigureAwait(false);
        }
    }

    private async Task RearmSegmentTimerAsync(long expectedGeneration)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ShouldProcessScheduledCallback(
                    expectedGeneration,
                    _recordingGeneration,
                    IsRecording,
                    Volatile.Read(ref _disposeState) != DisposeStateActive))
            {
                // One-shot rearming prevents a slow finalization from accumulating
                // overlapping same-generation split callbacks.
                _segmentTimer?.Change(
                    TimeSpan.FromMinutes(_activeConfig.SegmentDurationMinutes),
                    Timeout.InfiniteTimeSpan);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task StopSafeAsync(long expectedGeneration)
    {
        try
        {
            var result = await StopCoreAsync(CancellationToken.None, expectedGeneration)
                .ConfigureAwait(false);
            PublishStopResult(result);
            if (result is not null)
                ThrowCleanupFailures(result.CleanupFailures);
        }
        catch (Exception ex)
        {
            // Timer callbacks are fire-and-forget; contain every exception here.
            _logger.LogError(ex, "Error during auto-stop");
        }
    }

    private void SplitSegment(out FinalizedSegmentNotification? finalizedSegment)
    {
        finalizedSegment = null;
        if (!IsRecording)
            return;

        _logger.LogInformation("Splitting session recording at segment {Index}", _segmentIndex);

        var successorEpoch = Guid.NewGuid();
        var replacement = _segmentLiveness.BeginReplacement(successorEpoch);
        try
        {
            finalizedSegment = StopCurrentOutput(retireLiveness: false);
            _segmentIndex++;
            StartSegment(successorEpoch);
            _segmentLiveness.CommitReplacement(replacement);
        }
        catch
        {
            _segmentLiveness.AbortReplacement(replacement);
            throw;
        }
    }

    private FinalizedSegmentNotification? StopCurrentOutput(bool retireLiveness = true)
    {
        if (retireLiveness)
            _segmentLiveness.RetireCurrent();

        if (_recordingOutput is null)
            return null;

        var output = _recordingOutput;
        var validateMedia = _currentOutputStarted;
        var finalized = !output.IsActive;
        var endOffset = _elapsed.Elapsed;
        SessionSegmentReceipt? frozenReceipt = null;
        Exception? failure = null;
        if (validateMedia)
        {
            try
            {
                if (_producerReceiptState is null || _activeMediaId is not { } mediaId ||
                    _producerReceiptState.Sequence != _segmentIndex ||
                    _activeOutputEpoch is not { } outputEpoch || CurrentOutputPath is null)
                {
                    throw new InvalidOperationException(
                        "The active session segment lost its producer receipt identity.");
                }

                frozenReceipt = FreezeProducerReceipt(
                    _producerReceiptState,
                    mediaId,
                    outputEpoch,
                    CurrentOutputPath,
                    endOffset);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }
        try
        {
            if (output.IsActive)
            {
                if (!output.Stop())
                    failure = new IOException("Session output did not finalize before its stop timeout.");
                else
                    finalized = true;
            }
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            output.Dispose();
            _recordingOutput = null;
            _currentOutputStarted = false;
            _activeMediaId = null;
            _activeOutputEpoch = null;
        }
        catch (Exception ex)
        {
            if (failure is null)
                failure = ex;
            else
                _logger.LogError(ex, "Failed to dispose session output after an earlier stop failure");
        }

        if (finalized && validateMedia)
        {
            try
            {
                var finalizedSegment = ConfirmFinalizedSegment(
                    finalized,
                    CurrentOutputPath,
                    frozenReceipt,
                    ValidateFinalizedMedia);
                if (failure is null && finalizedSegment is not null)
                {
                    RetainFinalizedSegment(_finalizedSegmentReceipts, finalizedSegment);
                    _producerReceiptState = AdvanceProducerReceiptState(
                        _producerReceiptState!,
                        endOffset);
                    return finalizedSegment;
                }
            }
            catch (Exception ex)
            {
                failure ??= ex;
            }
        }

        if (failure is not null)
            throw failure;

        return null;
    }

    private StopResult StopLocked()
    {
        var wasRecording = IsRecording;
        var cleanupFailures = new List<Exception>();
        FinalizedSegmentNotification? finalizedSegment = null;

        InvalidateRecordingGenerationLocked();
        DisposeTimerLocked(ref _segmentTimer, cleanupFailures);
        DisposeTimerLocked(ref _maxDurationTimer, cleanupFailures);

        try
        {
            finalizedSegment = StopCurrentOutput();
        }
        catch (Exception ex)
        {
            cleanupFailures.Add(ex);
        }
        finally
        {
            IsRecording = false;
            DisposeEncodersAfterOutputReleased(cleanupFailures);
            if (!HasActiveResources)
            {
                _activeMediaId = null;
                _activeOutputEpoch = null;
                _captureContext = null;
                _producerReceiptState = null;
            }
            _elapsed.Stop();
        }

        return new StopResult(wasRecording, finalizedSegment, _elapsed.Elapsed, cleanupFailures);
    }

    private async ValueTask<CaptureLivenessTelemetryReading> ReadSegmentFrameCounterAsync(
        Guid outputEpoch,
        CaptureLivenessReadContext context,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var output = _recordingOutput;
            var isReadable =
                Volatile.Read(ref _disposeState) == DisposeStateActive &&
                _currentOutputStarted &&
                _activeOutputEpoch == outputEpoch &&
                output is not null &&
                _segmentLiveness.IsReadableEpoch(outputEpoch);
            return ReadOutputFrameCounter(
                isReadable,
                () => output!.TotalFrames,
                context);
        }
        finally
        {
            _lock.Release();
        }
    }

    internal static CaptureLivenessTelemetryReading ReadOutputFrameCounter(
        bool isReadable,
        Func<int> readCounter,
        CaptureLivenessReadContext context)
    {
        ArgumentNullException.ThrowIfNull(readCounter);
        ArgumentNullException.ThrowIfNull(context);
        if (!isReadable)
            return context.Missing();

        try
        {
            var totalFrames = readCounter();
            return totalFrames < 0
                ? context.Failed()
                : context.Available(totalFrames);
        }
        catch
        {
            return context.Failed();
        }
    }

    private static void DisposeTimerLocked(ref Timer? timer, ICollection<Exception> failures)
    {
        var ownedTimer = timer;
        timer = null;
        if (ownedTimer is null)
            return;

        try
        {
            ownedTimer.Dispose();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private long NextRecordingGenerationLocked()
    {
        _recordingGeneration = unchecked(_recordingGeneration + 1);
        return _recordingGeneration;
    }

    private void InvalidateRecordingGenerationLocked()
    {
        _recordingGeneration = unchecked(_recordingGeneration + 1);
    }

    private void PublishFinalizedSegment(FinalizedSegmentNotification? finalized)
    {
        PublishFinalizedSegment(
            finalized,
            receipt => SegmentFinalized?.Invoke(receipt),
            path =>
            {
                _logger.LogInformation("Segment saved: {Path}", Path.GetFileName(path));
                SegmentSaved?.Invoke(path);
            });
    }

    private void PublishStopResult(StopResult? result)
    {
        if (result is null)
            return;

        PublishFinalizedSegment(result.FinalizedSegment);
        _logger.LogInformation("Session recording stopped. Total duration: {Duration}", result.Elapsed);
        if (result.WasRecording)
            Stopped?.Invoke();
    }

    internal static bool ShouldProcessScheduledCallback(
        long expectedGeneration,
        long currentGeneration,
        bool isRecording,
        bool isDisposing)
    {
        return expectedGeneration == currentGeneration && isRecording && !isDisposing;
    }

    internal sealed record ProducerReceiptState(
        CaptureSessionContext? Context,
        Guid ProducerTimingEpoch,
        int Sequence,
        TimeSpan StartOffset);

    internal sealed record FinalizedSegmentNotification(
        string FilePath,
        SessionSegmentReceipt? Receipt);

    internal static ProducerReceiptState BeginProducerReceiptState(CaptureSessionContext? context) =>
        new(context, Guid.NewGuid(), 0, TimeSpan.Zero);

    internal static ProducerReceiptState AdvanceProducerReceiptState(
        ProducerReceiptState state,
        TimeSpan endOffset)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (endOffset < state.StartOffset)
            throw new ArgumentOutOfRangeException(nameof(endOffset));

        return state with
        {
            Sequence = checked(state.Sequence + 1),
            StartOffset = endOffset,
        };
    }

    internal static SessionSegmentReceipt? FreezeProducerReceipt(
        ProducerReceiptState state,
        Guid mediaId,
        Guid outputEpoch,
        string filePath,
        TimeSpan endOffset)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ProducerTimingEpoch == Guid.Empty)
            throw new InvalidOperationException("A producer timing epoch is required.");
        if (state.Sequence < 0)
            throw new InvalidOperationException("A nonnegative segment sequence is required.");
        if (state.StartOffset < TimeSpan.Zero || endOffset < state.StartOffset)
            throw new InvalidOperationException("Segment stopwatch offsets are invalid.");
        if (mediaId == Guid.Empty)
            throw new ArgumentException("A nonempty media ID is required.", nameof(mediaId));
        if (outputEpoch == Guid.Empty)
            throw new ArgumentException("A nonempty output epoch is required.", nameof(outputEpoch));
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("A finalized segment path is required.", nameof(filePath));

        var canonicalPath = Path.GetFullPath(filePath);
        if (state.Context is null)
            return null;

        return new SessionSegmentReceipt(
            SchemaVersion: 1,
            Context: state.Context,
            MediaId: mediaId,
            OutputEpoch: outputEpoch,
            ProducerTimingEpoch: state.ProducerTimingEpoch,
            Sequence: state.Sequence,
            FilePath: canonicalPath,
            StartOffset: state.StartOffset,
            EndOffset: endOffset,
            TimingProvenance: SessionTimingProvenance.RecorderStopwatchEstimate);
    }

    internal static FinalizedSegmentNotification? ConfirmFinalizedSegment(
        bool finalized,
        string? filePath,
        SessionSegmentReceipt? receipt,
        Action<string?> validateMedia)
    {
        ArgumentNullException.ThrowIfNull(validateMedia);
        if (!finalized)
            return null;

        validateMedia(filePath);
        if (string.IsNullOrWhiteSpace(filePath))
            throw new IOException("Session output finalized without a media path.");
        var canonicalPath = Path.GetFullPath(filePath);
        if (receipt is not null && !StringComparer.Ordinal.Equals(receipt.FilePath, canonicalPath))
            throw new InvalidDataException("The finalized segment path changed after receipt freeze.");
        return new FinalizedSegmentNotification(canonicalPath, receipt);
    }

    internal static void RetainFinalizedSegment(
        ICollection<SessionSegmentReceipt> retained,
        FinalizedSegmentNotification? finalized)
    {
        ArgumentNullException.ThrowIfNull(retained);
        if (finalized?.Receipt is not { } receipt)
            return;
        if (!StringComparer.Ordinal.Equals(receipt.FilePath, finalized.FilePath))
            throw new InvalidDataException("The finalized segment notification changed its receipt path.");
        retained.Add(receipt);
    }

    internal static IReadOnlyList<SessionSegmentReceipt> SnapshotFinalizedSegments(
        IEnumerable<SessionSegmentReceipt> retained,
        CaptureSessionContext expected)
    {
        ArgumentNullException.ThrowIfNull(retained);
        ArgumentNullException.ThrowIfNull(expected);
        var matching = retained
            .Where(receipt => SessionSegmentReceiptLedger.SameCapture(expected, receipt.Context))
            .ToArray();
        return Array.AsReadOnly(matching);
    }

    internal static void PublishFinalizedSegment(
        FinalizedSegmentNotification? finalized,
        Action<SessionSegmentReceipt>? publishReceipt,
        Action<string>? publishLegacyPath)
    {
        if (finalized is null)
            return;

        var failures = new List<Exception>();
        if (finalized.Receipt is not null && publishReceipt is not null)
            InvokeSubscriber(() => publishReceipt(finalized.Receipt), failures);
        if (publishLegacyPath is not null)
            InvokeSubscriber(() => publishLegacyPath(finalized.FilePath), failures);

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException("Session segment subscribers failed.", failures);
    }

    private static void InvokeSubscriber(Action subscriber, ICollection<Exception> failures)
    {
        try
        {
            subscriber();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    internal static void RunUnderOperationGate(SemaphoreSlim operationGate, Action action)
    {
        operationGate.Wait();
        try
        {
            action();
        }
        finally
        {
            operationGate.Release();
        }
    }

    internal void QueueScheduledCallback(Func<Task> callback)
    {
        lock (_scheduledCallbackSync)
        {
            if (Volatile.Read(ref _disposeState) != DisposeStateActive)
                return;

            _scheduledCallbacksInFlight++;
        }

        _ = RunScheduledCallbackAsync(callback);
    }

    private async Task RunScheduledCallbackAsync(Func<Task> callback)
    {
        var previousOwner = ScheduledCallbackOwner.Value;
        ScheduledCallbackOwner.Value = this;
        try
        {
            await callback().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unhandled error in a scheduled recorder callback");
        }
        finally
        {
            ScheduledCallbackOwner.Value = previousOwner;
            lock (_scheduledCallbackSync)
            {
                _scheduledCallbacksInFlight--;
                if (_scheduledCallbacksInFlight == 0)
                    Monitor.PulseAll(_scheduledCallbackSync);
            }
        }
    }

    internal void WaitForScheduledCallbacks()
    {
        // Dispose can be invoked synchronously by SegmentSaved/Stopped subscribers.
        // That callback is already delivering its event sequence and must be allowed
        // to unwind instead of waiting on itself.
        if (ReferenceEquals(ScheduledCallbackOwner.Value, this))
            return;

        lock (_scheduledCallbackSync)
        {
            while (_scheduledCallbacksInFlight > 0)
                Monitor.Wait(_scheduledCallbackSync);
        }
    }

    private sealed record StopResult(
        bool WasRecording,
        FinalizedSegmentNotification? FinalizedSegment,
        TimeSpan Elapsed,
        List<Exception> CleanupFailures);

    private void DisposeEncoders(ICollection<Exception> failures)
    {
        if (_videoEncoder is not null)
        {
            try
            {
                _videoEncoder.Dispose();
                _videoEncoder = null;
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }

        for (var index = _audioEncoders.Count - 1; index >= 0; index--)
        {
            try
            {
                _audioEncoders[index].Encoder.Dispose();
                _audioEncoders.RemoveAt(index);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        }
    }

    private void DisposeEncodersAfterOutputReleased(ICollection<Exception> failures)
    {
        ReleaseEncodersIfOutputReleased(_recordingOutput is null, () => DisposeEncoders(failures));
    }

    internal static void ReleaseEncodersIfOutputReleased(bool outputReleased, Action releaseEncoders)
    {
        if (outputReleased)
            releaseEncoders();
    }

    internal static string NormalizeFileFormat(string? fileFormat, bool separateAudioTracks)
    {
        var normalized = fileFormat?.Trim().ToLowerInvariant();
        if (separateAudioTracks && normalized is not ("mp4" or "mkv"))
        {
            throw new NotSupportedException(
                "Separate desktop and microphone tracks require MP4 or MKV session recording.");
        }

        return normalized switch
        {
            "mp4" or "mkv" or "flv" => normalized,
            _ => throw new NotSupportedException("Session recording format must be MP4, MKV, or FLV."),
        };
    }

    private static void ThrowCleanupFailures(IReadOnlyCollection<Exception> failures)
    {
        if (failures.Count == 1)
            throw failures.First();
        if (failures.Count > 1)
            throw new AggregateException("Session recorder cleanup was incomplete.", failures);
    }

    internal static void ValidateFinalizedMedia(string? outputPath)
    {
        if (outputPath is null || new FileInfo(outputPath) is not { Exists: true, Length: > 0 })
            throw new IOException("Session output finalized without a non-empty media file.");
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(
                ref _disposeState,
                DisposeStateDisposing,
                DisposeStateActive) != DisposeStateActive)
        {
            return;
        }

        StopResult? result = null;
        try
        {
            // Every asynchronous gate acquisition uses ConfigureAwait(false), so a
            // synchronous dispose cannot block the context needed by an owner to release.
            RunUnderOperationGate(_lock, () => result = StopLocked());
            WaitForScheduledCallbacks();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _disposeState, DisposeStateActive);
            _logger.LogError(ex, "Failed to serialize session recorder disposal");
            return;
        }

        var cleanupResult = result!;
        // A finalized-media validation warning must not make a successfully released
        // recorder reusable after Dispose. Only retained native/output ownership keeps
        // disposal retryable.
        var ownershipReleased = !HasActiveResources;
        Volatile.Write(
            ref _disposeState,
            ownershipReleased ? DisposeStateDisposed : DisposeStateActive);

        try
        {
            // Preserve the saved-segment notification, but publish only after the gate
            // is released so subscribers can safely call back into Dispose or StopAsync.
            PublishFinalizedSegment(cleanupResult.FinalizedSegment);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A session recorder subscriber failed during disposal");
        }

        if (cleanupResult.CleanupFailures.Count > 0)
        {
            _logger.LogError(
                new AggregateException(cleanupResult.CleanupFailures),
                ownershipReleased
                    ? "Session recorder disposal released ownership with finalization warnings"
                    : "Failed to release all session recorder resources during disposal");
        }

        // Do not dispose the semaphore: callers may already be waiting on it. The
        // disposed recorder retains no unmanaged resource through SemaphoreSlim.
    }
}
