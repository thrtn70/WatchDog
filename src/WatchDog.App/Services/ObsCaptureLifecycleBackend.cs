using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;
using WatchDog.Core.Devices;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Recording;
using WatchDog.Core.Sessions;
using WatchDog.Core.Settings;
using WatchDog.Core.Storage;

namespace WatchDog.App.Services;

/// <summary>
/// Owns the physical OBS replay/session outputs for one serialized lifecycle context.
/// CaptureSourceManager remains an event adapter; it does not manipulate outputs directly.
/// </summary>
public sealed class ObsCaptureLifecycleBackend : ICaptureLifecycleBackend, IDisposable
{
    private readonly ICaptureEngine _captureEngine;
    private readonly ISessionRecorder _sessionRecorder;
    private readonly ISessionSegmentReceiptSource? _segmentReceiptSource;
    private readonly IWebcamRecorder _webcamRecorder;
    private readonly SessionManager _sessionManager;
    private readonly IEventBus _eventBus;
    private readonly ICompletedMediaIngestion _completedMedia;
    private readonly AppSettings _settings;
    private readonly CaptureStartPolicy _startupPolicy;
    private readonly CaptureStartupBaselineResult _startupBaseline;
    private readonly ILogger<ObsCaptureLifecycleBackend> _logger;
    private readonly ConcurrentQueue<PendingSegment> _pendingSegments = new();
    private readonly ConcurrentQueue<PendingMediaSegment> _pendingMediaSegments = new();
    private readonly SemaphoreSlim _segmentDrainGate = new(1, 1);
    private readonly Stopwatch _captureTimeline = new();
    private readonly object _screenSegmentLock = new();

    private CaptureSessionContext? _activeContext;
    private GameInfo? _activeGame;
    private bool _sessionAnnounced;
    private int _recordingAnnounced;
    private int _expectedRecorderStop;
    private int _screenSegmentSequence;
    private TimeSpan _previousScreenSegmentElapsed;
    private SessionSegmentReceiptLedger? _segmentReceiptLedger;
    private SessionMediaSegment? _activeCameraMedia;
    private TimeSpan? _cameraStopBoundary;
    private bool _cameraAwaitingFinalization;
    private bool _disposed;

    public ObsCaptureLifecycleBackend(
        ICaptureEngine captureEngine,
        ISessionRecorder sessionRecorder,
        IWebcamRecorder webcamRecorder,
        SessionManager sessionManager,
        IEventBus eventBus,
        ICompletedMediaIngestion completedMedia,
        AppSettings settings,
        ILogger<ObsCaptureLifecycleBackend> logger,
        CaptureStartupBaselineResult startupBaseline)
    {
        _captureEngine = captureEngine;
        _sessionRecorder = sessionRecorder;
        _segmentReceiptSource = sessionRecorder as ISessionSegmentReceiptSource;
        _webcamRecorder = webcamRecorder;
        _sessionManager = sessionManager;
        _eventBus = eventBus;
        _completedMedia = completedMedia;
        _settings = settings;
        _startupPolicy = new CaptureStartPolicy(settings.Capture, settings.Buffer, settings.Recording).Sanitized();
        _startupBaseline = startupBaseline;
        _logger = logger;

        if (_segmentReceiptSource is not null)
            _segmentReceiptSource.SegmentFinalized += OnSegmentFinalized;
        else
            _sessionRecorder.SegmentSaved += OnSegmentSaved;
        _sessionRecorder.Error += OnRecorderError;
        _sessionRecorder.Stopped += OnRecorderStopped;
    }

    public CaptureFailure? ValidateStart(CaptureRequest request)
    {
        if (_startupBaseline.Validate(request) is { } bindingFailure)
            return bindingFailure;
        if (request.StartupExpectation is not null && _segmentReceiptSource is null)
            return new(CaptureFailureKind.Unsupported, "capture.segment-receipt.unsupported",
                "The session recorder cannot provide the required producer segment receipts.");
        var policy = request.StartPolicy ?? _startupPolicy;
        if (policy.Validate(_settings.Capture, allowLegacyHighlights: request.StartPolicy is null) is { } failure)
            return failure;
        if (request.StartPolicy is not null)
        {
            if (policy.Buffer.OutputDirectory != _startupPolicy.Buffer.OutputDirectory ||
                policy.Buffer.FilenameFormat != _startupPolicy.Buffer.FilenameFormat ||
                policy.Recording.FileFormat != _startupPolicy.Recording.FileFormat ||
                policy.Capture.AudioBitrate != _startupPolicy.Capture.AudioBitrate)
            {
                return new(CaptureFailureKind.Unsupported, "capture.policy.restart-required",
                    "Recording folder, file format and audio bitrate changes require an application restart before the next game start.");
            }
        }
        var capture = policy.Capture;
        var recording = policy.Recording;
        if (EncoderCapabilities.Validate(capture.Encoder) is { } encoderFailure)
            return encoderFailure;
        if (_settings.Audio.SeparateAudioTracks && recording.IsSessionRecordingEnabled &&
            (recording.FileFormat ?? string.Empty).Trim().ToLowerInvariant() is not ("mp4" or "mkv"))
        {
            return new(CaptureFailureKind.Unsupported, "capture.audio.separate-tracks-container-unsupported",
                "Separate desktop and microphone tracks require MP4 or MKV session recording.");
        }
        return null;
    }

    public async Task<CaptureBackendStartResult> StartAsync(
        CaptureSessionContext context,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
            return FailedStart("capture.backend.disposed", "The capture backend has been disposed.");
        if (_activeContext is not null)
            return FailedStart("capture.backend.already-active", "A physical capture context is already active.");
        if (ValidateStart(context.Request) is { } admissionFailure)
            return CaptureBackendStartResult.Failed(admissionFailure);
        var recording = context.Request.StartPolicy?.Recording ?? _startupPolicy.Recording;
        if (_settings.Audio.SeparateAudioTracks &&
            recording.IsSessionRecordingEnabled &&
            (recording.FileFormat ?? string.Empty).Trim().ToLowerInvariant() is not ("mp4" or "mkv"))
        {
            return FailedStart(
                "capture.audio.separate-tracks-container-unsupported",
                "Separate desktop and microphone tracks require MP4 or MKV session recording.");
        }
        if (context.Request.Webcam is not null && !recording.IsSessionRecordingEnabled)
        {
            return FailedStart(
                "capture.webcam.session-recording-required",
                "An independently retained camera requires session recording mode.",
                CaptureFailureKind.InvalidRequest);
        }
        if (context.Request.Webcam is not null &&
            context.RequestedTarget is not (WindowCaptureTarget or DisplayCaptureTarget or RegionCaptureTarget))
        {
            return FailedStart(
                "capture.webcam.manual-target-required",
                "Camera recording is supported only for an explicit manual window, display, or region capture.",
                CaptureFailureKind.InvalidRequest);
        }
        // Reset to startup defaults for manual/configured requests after a per-game override.
        var startPolicy = context.Request.StartPolicy ?? _startupPolicy;
        if (_captureEngine.ApplyNextStartConfiguration(startPolicy.Capture, startPolicy.Buffer) is { } configurationFailure)
        {
            return CaptureBackendStartResult.Failed(configurationFailure);
        }
        // Retain ownership before acquiring any physical resource, including initialization.
        _activeContext = context;
        _activeGame = ToGameInfo(context.RequestedTarget);
        CaptureFailure? startupFailure = null;

        try
        {
            _captureEngine.EnsureInitialized();
            var targetResult = ResolveTarget(context.RequestedTarget);
            if (targetResult.Failure is not null)
            {
                startupFailure = targetResult.Failure;
                throw new InvalidOperationException(startupFailure.Message);
            }

            var game = targetResult.Game!;
            var actualTarget = targetResult.ActualTarget!;
            var resolvedContext = context with { ActualTarget = actualTarget };
            _activeContext = resolvedContext;
            _activeGame = game;

            if (context.Request.StartupExpectation is not null && context.RequestedTarget != actualTarget)
            {
                startupFailure = new CaptureFailure(
                    CaptureFailureKind.SourceLost,
                    "capture.startup-binding.target-changed",
                    "The confirmed tutorial capture target changed before startup completed. Prepare a new tutorial plan.");
                throw new InvalidOperationException(startupFailure.Message);
            }

            // Establish durable attribution before any output can produce completed media.
            if (actualTarget is DisplayCaptureTarget or RegionCaptureTarget)
                await _sessionManager.StartDesktopSessionAsync(cancellationToken, context.SessionId);
            else
                await _sessionManager.StartSessionAsync(game, cancellationToken, context.SessionId);

            _captureTimeline.Restart();
            _screenSegmentSequence = 0;
            _previousScreenSegmentElapsed = TimeSpan.Zero;
            _activeCameraMedia = null;
            _cameraStopBoundary = null;
            _cameraAwaitingFinalization = false;

            switch (actualTarget)
            {
                case WindowCaptureTarget window:
                    await _captureEngine.StartWindowCaptureAsync(
                        CaptureSource.FromWindow(
                            window.ExecutableName!,
                            window.WindowTitle!,
                            window.ProcessId,
                            window.WindowHandle,
                            window.WindowClass),
                        recording.IsReplayBufferEnabled,
                        cancellationToken);
                    break;
                case DisplayCaptureTarget display:
                    await _captureEngine.StartDesktopCaptureAsync(
                        display,
                        recording.IsReplayBufferEnabled,
                        cancellationToken);
                    break;
                case RegionCaptureTarget region:
                    await _captureEngine.StartRegionCaptureAsync(
                        region,
                        recording.IsReplayBufferEnabled,
                        cancellationToken);
                    break;
                default:
                    startupFailure = new CaptureFailure(
                        CaptureFailureKind.Unsupported,
                        "capture.target.unsupported",
                        $"{actualTarget.GetType().Name} is not implemented by the OBS backend.");
                    throw new InvalidOperationException(startupFailure.Message);
            }

            if (_captureEngine.State != CaptureState.Buffering)
                throw new InvalidOperationException("The capture engine did not report a buffering state after start.");

            var admittedDevices = _captureEngine.AdmittedAudioDevices;
            if (admittedDevices.Entries.Any(device => device.Role == CaptureDeviceRole.Webcam))
                throw new InvalidDataException("The capture engine audio manifest contained a webcam role.");

            if (recording.IsSessionRecordingEnabled)
            {
                var outputDirectory = GetSessionOutputDirectory(game, context.Request);
                lock (_screenSegmentLock)
                    _segmentReceiptLedger = new(resolvedContext, outputDirectory);
                await _sessionRecorder.StartAsync(
                    outputDirectory,
                    game,
                    resolvedContext,
                    cancellationToken);
                if (!_sessionRecorder.IsRecording)
                    throw new InvalidOperationException("The session recorder did not report an active output after start.");
            }

            if (context.Request.Webcam is { } webcam)
            {
                var media = CreateCameraMediaPlan(_activeContext, game, _captureTimeline.Elapsed);
                _activeCameraMedia = media;
                // Persist intent before the first operation that can select/open the device.
                await _sessionManager.UpsertMediaSegmentAsync(context.SessionId, media, cancellationToken);
                await _webcamRecorder.StartAsync(webcam, media.FilePath, cancellationToken);
                if (!_webcamRecorder.IsRecording)
                    throw new InvalidOperationException("The webcam recorder did not report an active output after start.");
                _cameraAwaitingFinalization = true;
                // Append only after the exact webcam source reports a successful start.
                admittedDevices = admittedDevices.Append(
                    AdmittedCaptureDevice.Exact(CaptureDeviceRole.Webcam, webcam.DeviceId));
            }

            _activeContext = _activeContext with { AdmittedDevices = admittedDevices };

            if (_sessionRecorder.IsRecording && _sessionRecorder.CurrentOutputPath is { } outputPath)
            {
                Interlocked.Exchange(ref _recordingAnnounced, 1);
                _eventBus.Publish(new SessionRecordingStartedEvent(outputPath, game));
            }
            await DrainSegmentsAsync(_activeContext, game, cancellationToken);
            if (context.RequestedTarget is GameCaptureTarget)
            {
                _sessionAnnounced = true;
                _eventBus.Publish(new GameDetectedEvent(game, _activeContext));
            }
            return CaptureBackendStartResult.Success(actualTarget, admittedDevices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start physical capture for request {RequestId}", context.Request.RequestId);
            // Cleanup is ordered and deliberately uncancelled; failed cleanup remains owned.
            var cleanup = await StopAsync(_activeContext ?? context, CancellationToken.None);
            var mappedDeviceFailure = MapCaptureDeviceStartupFailure(ex);
            var failure = startupFailure ?? mappedDeviceFailure ?? new CaptureFailure(
                    ex is OperationCanceledException ? CaptureFailureKind.Cancelled : CaptureFailureKind.Backend,
                    "capture.backend.start-failed",
                    ex.Message,
                    ex is not OperationCanceledException);
            if (!cleanup.Succeeded && cleanup.Failure is { } cleanupFailure)
                failure = failure.WithCleanupFailure(cleanupFailure);
            return cleanup.Succeeded || !cleanup.CleanupRequired
                ? CaptureBackendStartResult.Failed(failure)
                : CaptureBackendStartResult.Failed(
                    failure,
                    (_activeContext ?? context).ActualTarget);
        }
    }

    public async Task<CaptureBackendCommandResult> StopAsync(
        CaptureSessionContext context,
        CancellationToken cancellationToken = default)
    {
        if (_activeContext is null ||
            _activeContext.SessionId != context.SessionId ||
            _activeContext.Generation != context.Generation)
        {
            return CaptureBackendCommandResult.Failed(new CaptureFailure(
                CaptureFailureKind.Superseded,
                "capture.backend.stop-stale",
                "The stop request does not match the active physical capture context."));
        }

        var ownedContext = _activeContext;
        var game = _activeGame ?? ToGameInfo(ownedContext.ActualTarget);
        var failures = new List<(string Stage, Exception Error)>();
        if (_cameraAwaitingFinalization)
            _cameraStopBoundary ??= _captureTimeline.Elapsed;
        var stopBoundary = _cameraStopBoundary ?? _captureTimeline.Elapsed;
        var cameraClean = _activeContext.Request.Webcam is null && !_webcamRecorder.HasActiveResources;
        var cameraNativeClean = !_webcamRecorder.HasNativeResources;
        var hasUncertainViewRelease = false;
        var cameraShouldFinalize = _cameraAwaitingFinalization;
        var cameraPlanCompensated = _activeCameraMedia is null || cameraShouldFinalize;

        var recorderClean = false;
        Interlocked.Increment(ref _expectedRecorderStop);
        try
        {
            // The recorder shares the OBS context, so it must finalize before the engine tears OBS down.
            await _sessionRecorder.StopAsync(cancellationToken);
            OnRecorderStopped();
            recorderClean = true;
        }
        catch (Exception ex)
        {
            failures.Add(("session recorder", ex));
            // ObsSessionRecorder reports finalized-media validation after releasing its output.
            // Other recorder failures may still own the shared OBS context and must block teardown.
            recorderClean = !_sessionRecorder.HasActiveResources;
        }
        finally
        {
            Interlocked.Decrement(ref _expectedRecorderStop);
        }

        if (_activeContext.Request.Webcam is not null || _webcamRecorder.HasActiveResources)
        {
            try
            {
                await _webcamRecorder.StopAsync(cancellationToken);
                cameraClean = !_webcamRecorder.HasActiveResources;
                cameraNativeClean = !_webcamRecorder.HasNativeResources;
                if (cameraShouldFinalize && cameraClean && _activeCameraMedia is { } cameraMedia)
                {
                    ValidateFinalizedCameraMedia(cameraMedia.FilePath);
                    var duration = stopBoundary - cameraMedia.StartOffset;
                    var finalized = cameraMedia with
                    {
                        State = SessionMediaState.Finalized,
                        Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
                    };
                    _pendingMediaSegments.Enqueue(new PendingMediaSegment(ownedContext, finalized));
                    _activeCameraMedia = null;
                    _cameraStopBoundary = null;
                    _cameraAwaitingFinalization = false;
                }
            }
            catch (Exception ex)
            {
                failures.Add(("webcam recorder", ex));
                cameraClean = !_webcamRecorder.HasActiveResources;
                cameraNativeClean = !_webcamRecorder.HasNativeResources;
            }
            hasUncertainViewRelease = _webcamRecorder.HasUncertainViewRelease;
        }

        if (!cameraShouldFinalize && cameraClean && _activeCameraMedia is { } plannedCamera)
        {
            try
            {
                await _sessionManager.RemovePlannedMediaSegmentAsync(
                    context.SessionId,
                    plannedCamera.MediaId,
                    CancellationToken.None);
                _activeCameraMedia = null;
                _cameraStopBoundary = null;
                cameraPlanCompensated = true;
            }
            catch (Exception ex)
            {
                failures.Add(("webcam plan compensation", ex));
                cameraPlanCompensated = false;
            }
        }

        var engineClean = false;
        if (recorderClean && cameraNativeClean)
        {
            try
            {
                // Once recorder ownership is released, engine teardown is mandatory even if
                // finalized-media validation reported a missing or empty file.
                await _captureEngine.StopAsync(CancellationToken.None);
                engineClean = true;
            }
            catch (Exception ex)
            {
                failures.Add(("capture engine", ex));
            }
        }

        var segmentsDrained = false;
        if (recorderClean && cameraClean && cameraPlanCompensated)
        {
            try
            {
                // A successful split may have released the recorder gate before its event is delivered.
                // Reconcile producer-retained receipts before closing this session, including stop warnings.
                if (_segmentReceiptSource is not null)
                {
                    foreach (var receipt in _segmentReceiptSource.GetFinalizedSegments(ownedContext))
                    {
                        if (!AcceptSegmentFinalized(receipt, scheduleDrain: false))
                            throw new InvalidDataException("The producer's finalized segment snapshot could not be reconciled.");
                    }
                }
                await DrainSegmentsAsync(ownedContext, game, CancellationToken.None);
                segmentsDrained = true;
            }
            catch (Exception ex)
            {
                failures.Add(("segment index", ex));
            }
        }

        var sessionEnded = _sessionManager.CurrentSessionId != context.SessionId;
        if (recorderClean && cameraClean && cameraPlanCompensated && engineClean && segmentsDrained && !sessionEnded)
        {
            try
            {
                await _sessionManager.EndSessionAsync(game, CancellationToken.None);
                sessionEnded = true;
            }
            catch (Exception ex)
            {
                failures.Add(("session index", ex));
            }
        }

        var cleanupRequired = !recorderClean || !cameraClean || !cameraPlanCompensated ||
            !engineClean || !segmentsDrained || !sessionEnded;
        if (!cleanupRequired)
        {
            if (_sessionAnnounced)
                _eventBus.Publish(new GameExitedEvent(game, context));
            _sessionAnnounced = false;

            lock (_screenSegmentLock)
            {
                _segmentReceiptLedger = null;
                _activeContext = null;
            }
            _activeGame = null;
            _activeCameraMedia = null;
            _cameraStopBoundary = null;
            _cameraAwaitingFinalization = false;
            _captureTimeline.Reset();
        }

        foreach (var (stage, error) in failures)
            _logger.LogError(error, "Failed to stop {Stage} for capture session {SessionId}", stage, context.SessionId);
        if (hasUncertainViewRelease)
        {
            return CaptureBackendCommandResult.Failed(
                new CaptureFailure(
                    CaptureFailureKind.Backend,
                    CaptureFailure.WebcamViewReleaseUncertainCode,
                    "Webcam cleanup could not be confirmed.",
                    IsRetryable: false),
                cleanupRequired);
        }
        if (failures.Count == 0)
            return CaptureBackendCommandResult.Success();

        var cancelled = failures.Any(failure => failure.Error is OperationCanceledException);
        return CaptureBackendCommandResult.Failed(
            new CaptureFailure(
                cancelled ? CaptureFailureKind.Cancelled : CaptureFailureKind.Backend,
                "capture.backend.stop-failed",
                string.Join("; ", failures.Select(failure =>
                    $"{failure.Stage}: {failure.Error.Message}")),
                !cancelled),
            cleanupRequired);
    }

    public async Task<CaptureBackendSaveResult> SaveAsync(
        CaptureSessionContext context,
        CancellationToken cancellationToken = default)
    {
        if (!(context.Request.StartPolicy?.Recording ?? _startupPolicy.Recording).IsReplayBufferEnabled)
        {
            return CaptureBackendSaveResult.Failed(new CaptureFailure(
                CaptureFailureKind.Unsupported,
                "capture.save.replay-disabled",
                "Replay saving is unavailable because the replay buffer is disabled."));
        }
        if (_activeContext is null ||
            _activeContext.SessionId != context.SessionId ||
            _activeContext.Generation != context.Generation)
        {
            return CaptureBackendSaveResult.Failed(new CaptureFailure(
                CaptureFailureKind.Superseded,
                "capture.save.stale-context",
                "The save request does not match the active capture context."));
        }

        try
        {
            var path = await _captureEngine.SaveReplayAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(path))
            {
                return CaptureBackendSaveResult.Failed(new CaptureFailure(
                    CaptureFailureKind.Backend,
                    "capture.save.no-output",
                    "The replay output completed without a verified media path."));
            }

            var ownedContext = _activeContext!;
            var game = _activeGame ?? ToGameInfo(ownedContext.ActualTarget);
            var currentSession = _sessionManager.CurrentSession;
            var matchNumber = currentSession?.Id == ownedContext.SessionId && currentSession.Matches.Count > 0
                ? currentSession.Matches[^1].MatchNumber
                : (int?)null;
            var completedAt = DateTimeOffset.UtcNow;
            var receipt = new CompletedMediaReceipt(
                path,
                game.DisplayName,
                completedAt,
                ownedContext.SessionId,
                ownedContext.Request.RequestId,
                ownedContext.Generation,
                matchNumber);
            var ingestion = await _completedMedia.AcceptAsync(receipt, cancellationToken);
            if (!ingestion.Indexed)
            {
                return CaptureBackendSaveResult.Failed(new CaptureFailure(
                    CaptureFailureKind.Storage,
                    "capture.save.ingestion-pending",
                    $"The replay file was saved at '{path}', but its library entry is pending retry: " +
                    (ingestion.FailureMessage ?? "unknown indexing failure"),
                    ingestion.Disposition == CompletedMediaIngestionDisposition.PendingRetry),
                    path,
                    receipt);
            }
            _eventBus.Publish(new ClipSavedEvent(
                path,
                game,
                completedAt,
                ownedContext.SessionId,
                matchNumber,
                ownedContext));
            return CaptureBackendSaveResult.Success(path, receipt);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save replay for session {SessionId}", context.SessionId);
            return CaptureBackendSaveResult.Failed(new CaptureFailure(
                ex is OperationCanceledException ? CaptureFailureKind.Cancelled : CaptureFailureKind.Backend,
                "capture.backend.save-failed",
                ex.Message,
                ex is not OperationCanceledException));
        }
    }

    private TargetResolution ResolveTarget(CaptureTarget target)
    {
        switch (target)
        {
            case WindowCaptureTarget window when
                !string.IsNullOrWhiteSpace(window.ExecutableName) &&
                !string.IsNullOrWhiteSpace(window.WindowTitle):
                return new TargetResolution(window, ToGameInfo(window), null);
            case WindowCaptureTarget:
                return TargetResolution.Failed(
                    "capture.window.metadata-missing",
                    "Window capture requires executable and title metadata.");
            case GameCaptureTarget game when _captureEngine.ResolveGameWindowTarget(game) is { } window:
                return new TargetResolution(window, ToGameInfo(game), null);
            case GameCaptureTarget:
                return TargetResolution.Failed(
                    "capture.game.exact-window-unavailable",
                    "The detected game process does not currently expose an exact capturable main window.",
                    CaptureFailureKind.SourceLost);
            case DisplayCaptureTarget display when _captureEngine.ResolveDisplayTarget(display.StableDisplayId) is { } resolved:
                return new TargetResolution(resolved, ToGameInfo(resolved), null);
            case DisplayCaptureTarget:
                return TargetResolution.Failed(
                    "capture.display.identity-mismatch",
                    "The selected display identity is unavailable or no longer matches the configured monitor selection.",
                    CaptureFailureKind.SourceLost);
            case RegionCaptureTarget region when _captureEngine.IsRegionTargetAvailable(region):
                return new TargetResolution(region, ToGameInfo(region), null);
            case RegionCaptureTarget:
                return TargetResolution.Failed(
                    "capture.region.unavailable",
                    "The selected region's display identity or geometry is no longer available.",
                    CaptureFailureKind.SourceLost);
            default:
                return TargetResolution.Failed(
                    "capture.target.unsupported",
                    $"{target.GetType().Name} is not supported.",
                    CaptureFailureKind.Unsupported);
        }
    }

    private string GetSessionOutputDirectory(GameInfo game, CaptureRequest request)
    {
        var safeName = string.Concat(game.DisplayName.Split(Path.GetInvalidFileNameChars()));
        var basePath = request.StartupExpectation is null
            ? Path.GetFullPath(_settings.Storage.SavePath)
            : _startupBaseline.Baseline!.Expectation.CanonicalSaveRoot;
        var output = Path.GetFullPath(Path.Combine(basePath, safeName, "Sessions"));
        var boundedBase = basePath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!output.StartsWith(boundedBase, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The session output path escaped the configured save folder.");
        return output;
    }

    private SessionMediaSegment CreateCameraMediaPlan(
        CaptureSessionContext context,
        GameInfo game,
        TimeSpan startOffset)
    {
        var mediaId = Guid.NewGuid();
        var sessionDirectory = GetSessionOutputDirectory(game, context.Request);
        var cameraDirectory = Path.GetFullPath(Path.Combine(sessionDirectory, "Camera"));
        var boundedSession = Path.GetFullPath(sessionDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!cameraDirectory.StartsWith(boundedSession, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The webcam output path escaped the session folder.");

        var fileName = $"{DateTime.Now:yyyy-MM-dd HH-mm-ss}_{mediaId:N}.mkv";
        return new SessionMediaSegment
        {
            MediaId = mediaId,
            SessionId = context.SessionId,
            CaptureRequestId = context.Request.RequestId,
            CaptureGeneration = context.Generation,
            Role = SessionMediaRole.Camera,
            FilePath = Path.Combine(cameraDirectory, fileName),
            Sequence = 0,
            State = SessionMediaState.Planned,
            StartOffset = startOffset,
            TimingProvenance = SessionTimingProvenance.BackendStopwatchEstimate,
        };
    }

    private void OnSegmentFinalized(SessionSegmentReceipt receipt) =>
        AcceptSegmentFinalized(receipt, scheduleDrain: true);

    private bool AcceptSegmentFinalized(SessionSegmentReceipt receipt, bool scheduleDrain)
    {
        CaptureSessionContext context;
        CaptureFailure? failure;
        lock (_screenSegmentLock)
        {
            if (_activeContext is not { } active || _segmentReceiptLedger is null)
                return false;
            // Never attach receiving identity, elapsed time or manifest to an old producer event.
            if (receipt is null || !SessionSegmentReceiptLedger.SameCapture(active, receipt.Context))
                return false;
            context = receipt.Context;
            var disposition = _segmentReceiptLedger.Accept(receipt, out var media, out failure);
            if (disposition == SessionSegmentReceiptDisposition.Accepted)
                _pendingSegments.Enqueue(new(context, media!, receipt.EndOffset));
        }
        if (failure is not null)
        {
            _eventBus.Publish(new CaptureHealthDegradedEvent(context, failure));
            return false;
        }
        // An exact duplicate also retries a prior failed write, without enqueuing another segment.
        if (scheduleDrain && _sessionManager.CurrentSessionId == context.SessionId)
            _ = PersistSegmentsSafelyAsync(context, ToGameInfo(context.ActualTarget));
        return true;
    }

    private void OnSegmentSaved(string filePath)
    {
        var context = _activeContext;
        if (context is null)
        {
            _logger.LogWarning("Ignoring unowned session segment {Path}", filePath);
            return;
        }

        var elapsed = _sessionRecorder.Elapsed;
        SessionMediaSegment media;
        lock (_screenSegmentLock)
        {
            var duration = elapsed - _previousScreenSegmentElapsed;
            media = new SessionMediaSegment
            {
                MediaId = Guid.NewGuid(),
                SessionId = context.SessionId,
                CaptureRequestId = context.Request.RequestId,
                CaptureGeneration = context.Generation,
                Role = SessionMediaRole.Screen,
                FilePath = filePath,
                Sequence = _screenSegmentSequence++,
                State = SessionMediaState.Finalized,
                StartOffset = _previousScreenSegmentElapsed,
                Duration = duration < TimeSpan.Zero ? TimeSpan.Zero : duration,
                TimingProvenance = SessionTimingProvenance.BackendStopwatchEstimate,
            };
            _previousScreenSegmentElapsed = elapsed;
        }
        _pendingSegments.Enqueue(new PendingSegment(context, media, elapsed));
        if (_sessionManager.CurrentSessionId == context.SessionId)
            _ = PersistSegmentsSafelyAsync(context, _activeGame ?? ToGameInfo(context.ActualTarget));
    }

    private async Task PersistSegmentsSafelyAsync(CaptureSessionContext context, GameInfo game)
    {
        try { await DrainSegmentsAsync(context, game, CancellationToken.None); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Session segment metadata remains pending for retry in {SessionId}", context.SessionId);
        }
    }

    private async Task DrainSegmentsAsync(
        CaptureSessionContext context,
        GameInfo game,
        CancellationToken cancellationToken)
    {
        await _segmentDrainGate.WaitAsync(cancellationToken);
        try
        {
            // A delayed drain task must not consume a replacement capture's queue.
            if (!SessionSegmentReceiptLedger.SameCapture(context, _activeContext))
                return;
            while (_pendingSegments.TryPeek(out var segment))
            {
                if (!SessionSegmentReceiptLedger.SameCapture(segment.Context, context))
                {
                    _logger.LogWarning(
                        "Skipped stale segment attribution for {Path}; segment session {SegmentSession}, active session {Session}",
                        segment.Media.FilePath,
                        segment.Context.SessionId,
                        context.SessionId);
                    _pendingSegments.TryDequeue(out _);
                    continue;
                }

                await _sessionManager.UpsertMediaSegmentAsync(
                    context.SessionId,
                    segment.Media,
                    cancellationToken);
                _pendingSegments.TryDequeue(out _);
                _eventBus.Publish(new SessionRecordingSegmentSavedEvent(segment.Media.FilePath, game, segment.Elapsed));
            }

            while (_pendingMediaSegments.TryPeek(out var pendingMedia))
            {
                if (pendingMedia.Context.SessionId != context.SessionId ||
                    pendingMedia.Context.Generation != context.Generation)
                {
                    _logger.LogWarning(
                        "Skipped stale media attribution for {Path}; media session {MediaSession}, active session {Session}",
                        pendingMedia.Media.FilePath,
                        pendingMedia.Context.SessionId,
                        context.SessionId);
                    _pendingMediaSegments.TryDequeue(out _);
                    continue;
                }

                await _sessionManager.UpsertMediaSegmentAsync(
                    context.SessionId,
                    pendingMedia.Media,
                    cancellationToken);
                _pendingMediaSegments.TryDequeue(out _);
            }
        }
        finally { _segmentDrainGate.Release(); }
    }

    private void OnRecorderStopped()
    {
        if (Interlocked.Exchange(ref _recordingAnnounced, 0) != 0)
        {
            _eventBus.Publish(new SessionRecordingStoppedEvent(_activeGame, _sessionRecorder.Elapsed));
            if (Volatile.Read(ref _expectedRecorderStop) == 0)
            {
                PublishRecorderDegraded(
                    "capture.session-recording.stopped",
                    "Session recording stopped while replay capture remained active.");
            }
        }
    }

    private void OnRecorderError(string message)
    {
        _logger.LogError("Session recorder error: {Error}", message);
        if (Volatile.Read(ref _expectedRecorderStop) == 0)
            PublishRecorderDegraded("capture.session-recording.failed", message);
    }

    private void PublishRecorderDegraded(string code, string message)
    {
        var context = _activeContext;
        if (context is null)
            return;

        _eventBus.Publish(new CaptureHealthDegradedEvent(
            context,
            new CaptureFailure(
                CaptureFailureKind.Backend,
                code,
                message,
                true)));
    }

    private static GameInfo ToGameInfo(CaptureTarget target) => target switch
    {
        GameCaptureTarget game => new GameInfo
        {
            ExecutableName = game.ExecutableName,
            DisplayName = game.DisplayName ?? game.GameId,
            ProcessId = game.ProcessId,
            ProcessStartedAt = game.ProcessStartedAt,
        },
        WindowCaptureTarget window => new GameInfo
        {
            ExecutableName = window.ExecutableName ?? "unknown.exe",
            DisplayName = window.WindowTitle ?? "Selected window",
            ProcessId = window.ProcessId,
            WindowTitle = window.WindowTitle,
        },
        DisplayCaptureTarget display => new GameInfo
        {
            ExecutableName = "desktop",
            DisplayName = display.DisplayName ?? "Desktop",
        },
        RegionCaptureTarget region => new GameInfo
        {
            ExecutableName = "region",
            DisplayName = $"Region {region.PhysicalBounds.Width}x{region.PhysicalBounds.Height}",
        },
        _ => new GameInfo { ExecutableName = "unknown", DisplayName = "Unknown source" },
    };

    private static CaptureBackendStartResult FailedStart(
        string code,
        string message,
        CaptureFailureKind kind = CaptureFailureKind.Backend) =>
        CaptureBackendStartResult.Failed(new CaptureFailure(kind, code, message));

    private static CaptureFailure? MapCaptureDeviceStartupFailure(Exception exception)
    {
        var deviceFailure = CaptureDeviceStartupException.Find(exception);
        if (deviceFailure is null)
            return null;

        var role = deviceFailure.Role switch
        {
            CaptureDeviceRole.DesktopAudio => (Code: "desktop-audio", Name: "desktop audio device"),
            CaptureDeviceRole.Microphone => (Code: "microphone", Name: "microphone"),
            CaptureDeviceRole.Webcam => (Code: "webcam", Name: "webcam"),
            _ => throw new ArgumentOutOfRangeException(nameof(deviceFailure.Role)),
        };
        var stage = deviceFailure.Stage switch
        {
            CaptureDeviceStartupStage.Catalog => "catalog",
            CaptureDeviceStartupStage.Selection => "selection",
            CaptureDeviceStartupStage.Binding => "binding",
            CaptureDeviceStartupStage.Readiness => "readiness",
            CaptureDeviceStartupStage.Start => "start",
            _ => throw new ArgumentOutOfRangeException(nameof(deviceFailure.Stage)),
        };
        var reason = deviceFailure.Reason switch
        {
            CaptureDeviceStartupReason.CatalogUnavailable => "unavailable",
            CaptureDeviceStartupReason.InvalidCatalog => "invalid",
            CaptureDeviceStartupReason.ExactSelectionUnavailable => "unavailable",
            CaptureDeviceStartupReason.IdentityMismatch => "identity-mismatch",
            CaptureDeviceStartupReason.Unknown => "unknown",
            _ => throw new ArgumentOutOfRangeException(nameof(deviceFailure.Reason)),
        };
        var message = (deviceFailure.Stage, deviceFailure.Reason) switch
        {
            (CaptureDeviceStartupStage.Catalog, CaptureDeviceStartupReason.InvalidCatalog) =>
                $"The {role.Name} catalog returned invalid device identities.",
            (CaptureDeviceStartupStage.Catalog, _) =>
                $"WatchDog could not read the {role.Name} catalog.",
            (CaptureDeviceStartupStage.Selection, _) =>
                $"The selected {role.Name} is unavailable or no longer matches its saved identity.",
            (CaptureDeviceStartupStage.Binding, CaptureDeviceStartupReason.IdentityMismatch) =>
                $"OBS did not retain the exact selected {role.Name} identity.",
            (CaptureDeviceStartupStage.Binding, _) =>
                $"OBS could not bind the selected {role.Name}.",
            (CaptureDeviceStartupStage.Readiness, _) =>
                $"The selected {role.Name} did not become ready for capture.",
            (CaptureDeviceStartupStage.Start, _) =>
                $"OBS could not start the selected {role.Name}.",
            _ => $"The selected {role.Name} could not be admitted for capture.",
        };

        return new CaptureFailure(
            CaptureFailureKind.Backend,
            $"capture.device.{role.Code}.{stage}.{reason}",
            message,
            IsRetryable: true);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_segmentReceiptSource is not null)
            _segmentReceiptSource.SegmentFinalized -= OnSegmentFinalized;
        _sessionRecorder.SegmentSaved -= OnSegmentSaved;
        _sessionRecorder.Error -= OnRecorderError;
        _sessionRecorder.Stopped -= OnRecorderStopped;
    }

    private static void ValidateFinalizedCameraMedia(string outputPath)
    {
        if (new FileInfo(outputPath) is not { Exists: true, Length: > 0 })
            throw new IOException("Webcam output finalized without a non-empty media file.");
    }

    private sealed record PendingSegment(
        CaptureSessionContext Context,
        SessionMediaSegment Media,
        TimeSpan Elapsed);

    private sealed record PendingMediaSegment(
        CaptureSessionContext Context,
        SessionMediaSegment Media);

    private sealed record TargetResolution(
        CaptureTarget? ActualTarget,
        GameInfo? Game,
        CaptureFailure? Failure)
    {
        public static TargetResolution Failed(
            string code,
            string message,
            CaptureFailureKind kind = CaptureFailureKind.InvalidRequest) =>
            new(null, null, new CaptureFailure(kind, code, message));
    }
}
