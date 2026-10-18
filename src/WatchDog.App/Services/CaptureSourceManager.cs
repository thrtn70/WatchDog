using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Highlights;
using WatchDog.Core.Settings;
using WatchDog.Native.Win32;

namespace WatchDog.App.Services;

/// <summary>
/// Adapts detector, settings, and UI requests into the single serialized capture lifecycle.
/// Physical replay/session output ownership lives in ObsCaptureLifecycleBackend.
/// </summary>
public sealed class CaptureSourceManager : IHostedService, IDisposable
{
    private static readonly TimeSpan SourceHealthInterval = TimeSpan.FromSeconds(1);

    private readonly IGameDetector _gameDetector;
    private readonly ICaptureEngine _captureEngine;
    private readonly CaptureLifecycleCoordinator _lifecycle;
    private readonly ISettingsService _settingsService;
    private readonly IEventBus _eventBus;
    private readonly HighlightDetectorRegistry _highlightRegistry;
    private readonly ILogger<CaptureSourceManager> _logger;
    private readonly IDisposable _sourceLostSubscription;
    private readonly IDisposable _healthDegradedSubscription;
    private readonly object _settingsLock = new();
    private readonly object _automaticStartLock = new();

    private volatile AppSettings _settings;
    private Controls.GameLaunchToast? _activeToast;
    private Timer? _sourceHealthTimer;
    private CancellationTokenSource? _automaticStartCancellation;
    private GameCaptureTarget? _pendingAutomaticTarget;
    private Task _lastGameStartHandling = Task.CompletedTask;
    private Task _lastSourceLossHandling = Task.CompletedTask;
    private Task _disposalCompletion = Task.CompletedTask;
    private int _sourceHealthCheckActive;
    private bool _disposed;

    public CaptureSourceManager(
        IGameDetector gameDetector,
        ICaptureEngine captureEngine,
        CaptureLifecycleCoordinator lifecycle,
        IEventBus eventBus,
        AppSettings settings,
        ISettingsService settingsService,
        HighlightDetectorRegistry highlightRegistry,
        ILogger<CaptureSourceManager> logger)
    {
        _gameDetector = gameDetector;
        _captureEngine = captureEngine;
        _lifecycle = lifecycle;
        _settings = settings;
        _settingsService = settingsService;
        _eventBus = eventBus;
        _highlightRegistry = highlightRegistry;
        _logger = logger;
        TutorialCapture = new(() => _lifecycle.IsAdmissionClosed, () => _lifecycle.Snapshot,
            StartTutorialAsync, _lifecycle.StopOwnedManualAsync);
        _sourceLostSubscription = eventBus.Subscribe<CaptureSourceLostEvent>(OnCaptureSourceLost);
        _healthDegradedSubscription = eventBus.Subscribe<CaptureHealthDegradedEvent>(OnCaptureHealthDegraded);
    }

    public CaptureSource? ActiveSource => ToCaptureSource(_lifecycle.Snapshot.Context);

    public TutorialCaptureController TutorialCapture { get; }

    public CaptureLifecycleSnapshot LifecycleSnapshot => _lifecycle.Snapshot;

    public bool IsAdmissionClosed => _lifecycle.IsAdmissionClosed;

    public event Action<CaptureLifecycleSnapshot>? SnapshotChanged
    {
        add => _lifecycle.SnapshotChanged += value;
        remove => _lifecycle.SnapshotChanged -= value;
    }

    public bool IsManualCaptureActive =>
        _lifecycle.Snapshot.Status == CaptureLifecycleStatus.Active &&
        _lifecycle.Snapshot.Context?.Request.Initiation == CaptureInitiation.Manual;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _gameDetector.GameStarted += OnGameStarted;
        _gameDetector.GameStopped += OnGameStopped;
        _settingsService.SettingsChanged += OnSettingsChanged;
        _sourceHealthTimer = new Timer(
            _ => _ = CheckActiveSourceHealthAsync(),
            null,
            SourceHealthInterval,
            SourceHealthInterval);

        if (ShouldHaveDesktopCapture(_settings))
            await StartConfiguredDesktopAsync(_settings, cancellationToken);

        // Initial game detection can now submit a serialized switch instead of racing startup.
        _gameDetector.Start();
        _logger.LogInformation("Capture source manager started — watching for games");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        TutorialCapture.CloseAdmission();
        CancelPendingAutomaticStart();
        _gameDetector.GameStarted -= OnGameStarted;
        _gameDetector.GameStopped -= OnGameStopped;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _sourceHealthTimer?.Dispose();
        _sourceHealthTimer = null;
        _gameDetector.Stop();

        await DrainTutorialShutdownAsync().ConfigureAwait(false);
    }

    private async Task DrainTutorialShutdownAsync()
    {
        // Close coordinator admission immediately, then await both the terminal owner and
        // the controller's already-submitted command. Neither may abandon a late receipt.
        var shutdown = _lifecycle.ShutdownAsync();
        await TutorialCapture.DrainAsync().ConfigureAwait(false);
        var result = await shutdown.ConfigureAwait(false);
        TutorialCapture.ObserveShutdown(result);
        LogTransition(result);
    }

    internal Task AwaitDisposalAsync() => Volatile.Read(ref _disposalCompletion);

    private async Task<CaptureTransitionResult> StartTutorialAsync(CaptureRequest request, CancellationToken token)
    {
        var before = _lifecycle.Snapshot;
        CaptureFailure? failure = null;
        if (token.IsCancellationRequested)
            failure = new(CaptureFailureKind.Cancelled, "tutorial.cancelled", "Tutorial recording intent was cancelled.");
        else
        {
            try
            {
                var available = request.Target switch
                {
                    WindowCaptureTarget window => _captureEngine.IsWindowTargetAvailable(window),
                    DisplayCaptureTarget display => _captureEngine.IsDisplayTargetAvailable(display) &&
                        _captureEngine.ResolveDisplayTarget(display.StableDisplayId) == display,
                    RegionCaptureTarget region => _captureEngine.IsRegionTargetAvailable(region),
                    _ => false,
                };
                if (!available)
                    failure = new(CaptureFailureKind.SourceLost, "tutorial.source-changed", "Reconfirm the exact tutorial source before recording.");
            }
            catch (Exception)
            {
                failure = new(CaptureFailureKind.SourceLost, "tutorial.source-unavailable", "The exact tutorial source could not be revalidated.");
            }
        }
        if (token.IsCancellationRequested)
            failure = new(CaptureFailureKind.Cancelled, "tutorial.cancelled", "Tutorial recording intent was cancelled.");
        if (failure is not null)
            return new(Guid.NewGuid(), CaptureOperationKind.Start, CaptureCommandDisposition.Rejected, before, before, failure);

        CancelPendingAutomaticStart();
        return await BeginAsync(request, token).ConfigureAwait(false);
    }

    public Task<CaptureTransitionResult> StartManualCaptureAsync(
        string executableName,
        string windowTitle,
        int processId,
        IntPtr windowHandle,
        string? windowClass,
        CancellationToken ct = default) =>
        StartManualCaptureAsync(CreateManualWindowRequest(new WindowCaptureTarget(
            processId, windowHandle, windowTitle, executableName, windowClass)), ct);

    /// <summary>Freezes a picker-visible request before the lifecycle is dispatched.</summary>
    public CaptureRequest CreateManualWindowRequest(WindowCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return NewRequest(CaptureInitiation.Manual, target);
    }

    public async Task<CaptureTransitionResult> StartManualCaptureAsync(
        CaptureRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Initiation != CaptureInitiation.Manual || request.Target is not WindowCaptureTarget)
            throw new ArgumentException("An exact manual window request is required.", nameof(request));
        CancelPendingAutomaticStart();
        _logger.LogInformation("Manual window capture requested (request {RequestId})", request.RequestId);

        var result = await BeginAsync(request, ct);
        LogTransition(result);
        return result;
    }

    public async Task<CaptureTransitionResult> StartManualRegionCaptureAsync(
        string owningDisplayId,
        PhysicalCaptureBounds physicalBounds,
        CancellationToken ct = default)
    {
        CancelPendingAutomaticStart();
        var target = _captureEngine.ResolveRegionTarget(owningDisplayId, physicalBounds) ??
                     new RegionCaptureTarget(owningDisplayId, physicalBounds, default);
        var request = NewRequest(CaptureInitiation.Manual, target);

        _logger.LogInformation(
            "Manual region capture requested on {StableId}: {Bounds} (request {RequestId})",
            owningDisplayId,
            physicalBounds,
            request.RequestId);

        var result = await BeginAsync(request, ct);
        LogTransition(result);
        return result;
    }

    public Task<CaptureTransitionResult> StopManualCaptureAsync(CancellationToken ct = default) =>
        StopManualCaptureCoreAsync(null, ct);

    public Task<CaptureTransitionResult> StopManualCaptureAsync(
        CaptureSessionContext expected,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return StopManualCaptureCoreAsync(expected, ct);
    }

    private async Task<CaptureTransitionResult> StopManualCaptureCoreAsync(
        CaptureSessionContext? expected,
        CancellationToken ct)
    {
        var result = expected is null
            ? await _lifecycle.StopAsync(CaptureInitiation.Manual, ct)
            : await _lifecycle.StopOwnedManualAsync(expected, ct);
        LogTransition(result);

        if (result.Succeeded && !_lifecycle.IsAdmissionClosed)
        {
            try
            {
                var detectedGame = _gameDetector.CurrentGame;
                if (detectedGame?.ProcessStartedAt is not null)
                {
                    var target = ToGameTarget(detectedGame);
                    if (IsGameProcessCurrent(target))
                    {
                        var reacquire = await StartAutomaticGameCaptureAsync(detectedGame, ct);
                        if (reacquire is not null)
                            LogTransition(reacquire);
                        return result;
                    }
                }

                if (ShouldHaveDesktopCapture(_settings))
                    await StartConfiguredDesktopAsync(_settings, ct);
            }
            catch (Exception ex)
            {
                // Reacquisition is a separate operation. It cannot change the completed Stop result.
                _logger.LogWarning("Post-stop capture reacquisition failed ({ExceptionType}); Stop result remains completed",
                    ex.GetType().Name);
            }
        }

        return result;
    }

    private void OnGameStarted(GameInfo game) =>
        Volatile.Write(ref _lastGameStartHandling, HandleGameStartedAsync(game));

    internal Task AwaitLastGameStartHandlingAsync() =>
        Volatile.Read(ref _lastGameStartHandling);

    internal async Task HandleGameStartedAsync(GameInfo game)
    {
        try
        {
            var snapshot = _lifecycle.Snapshot;
            if (snapshot.Context?.RequestedTarget is GameCaptureTarget activeGame &&
                GameProcessIdentity.Matches(game, activeGame))
            {
                _logger.LogDebug("Ignoring duplicate detection for {Game} PID {Pid}", game.DisplayName, game.ProcessId);
                return;
            }

            if (game.ProcessStartedAt is null)
            {
                _logger.LogWarning(
                    "Ignoring game detection without an exact process start time for {Game} PID {Pid}",
                    game.DisplayName,
                    game.ProcessId);
                return;
            }

            if (IsManualCaptureActive)
            {
                _logger.LogDebug(
                    "Deferring automatic capture for {Game} PID {Pid} while manual capture owns the lifecycle",
                    game.DisplayName,
                    game.ProcessId);
                return;
            }

            var profile = _settings.GameProfiles.FirstOrDefault(profile =>
                string.Equals(profile.GameExecutableName, game.ExecutableName, StringComparison.OrdinalIgnoreCase));
            if (profile is null)
                ShowGameLaunchToast(game);

            var result = await StartAutomaticGameCaptureAsync(game);
            if (result is not null)
                LogTransition(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to submit automatic capture for {Game}", game.DisplayName);
        }
    }

    private async void OnGameStopped(GameInfo game)
    {
        try
        {
            CancelPendingAutomaticStart(game);
            var snapshot = _lifecycle.Snapshot;
            var context = snapshot.Context;
            if (snapshot.Status is not (CaptureLifecycleStatus.Active or CaptureLifecycleStatus.Saving) ||
                context is null)
                return;

            if (context.RequestedTarget is not GameCaptureTarget requestedGame ||
                !GameProcessIdentity.Matches(game, requestedGame))
            {
                _logger.LogDebug(
                    "Ignoring stale game-exit event for {Game} PID {Pid}; active generation is {Generation}",
                    game.DisplayName,
                    game.ProcessId,
                    context.Generation);
                return;
            }

            var result = await _lifecycle.HandleSourceLostAsync(new CaptureSourceLoss(
                context.SessionId,
                context.Generation,
                context.ActualTarget,
                DateTimeOffset.UtcNow));
            LogTransition(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle game exit for {Game}", game.DisplayName);
        }
    }

    private async void OnSettingsChanged(AppSettings newSettings)
    {
        try
        {
            bool hadDesktopCapture;
            lock (_settingsLock)
            {
                hadDesktopCapture = ShouldHaveDesktopCapture(_settings);
                _settings = newSettings;
            }

            if (IsManualCaptureActive)
                return;

            var shouldHaveDesktopCapture = ShouldHaveDesktopCapture(newSettings);
            var isDesktopActive = _lifecycle.Snapshot.Context?.ActualTarget is DisplayCaptureTarget;
            if (shouldHaveDesktopCapture && !hadDesktopCapture && _lifecycle.Snapshot.Status != CaptureLifecycleStatus.Active)
                await StartConfiguredDesktopAsync(newSettings, CancellationToken.None);
            else if (!shouldHaveDesktopCapture && hadDesktopCapture && isDesktopActive)
                LogTransition(await _lifecycle.StopAsync(CaptureInitiation.ConfiguredStartup));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply capture settings change");
        }
    }

    internal async Task CheckActiveSourceHealthAsync()
    {
        if (Interlocked.Exchange(ref _sourceHealthCheckActive, 1) != 0)
            return;

        try
        {
            var snapshot = _lifecycle.Snapshot;
            var context = snapshot.Context;
            if (snapshot.Status is not (CaptureLifecycleStatus.Active or CaptureLifecycleStatus.Saving) ||
                context is null)
            {
                return;
            }

            if ((context.Request.StartPolicy?.Recording ?? _settings.Recording).IsReplayBufferEnabled &&
                !_captureEngine.IsReplayBufferActive &&
                snapshot.LastFailure?.Code != "capture.replay.unexpected-stop")
            {
                LogTransition(
                    await _lifecycle.ReportDegradedAsync(
                        context,
                        new CaptureFailure(
                            CaptureFailureKind.Backend,
                            "capture.replay.unexpected-stop",
                            "The replay output stopped while its capture context remained active.",
                            true)));
                return;
            }

            var lostTarget = context.ActualTarget switch
            {
                WindowCaptureTarget window when
                    !WindowEnumerator.IsWindowOwnedByProcess(window.WindowHandle, window.ProcessId) ||
                    !_captureEngine.IsWindowTargetAvailable(window) =>
                    (CaptureTarget?)window,
                GameCaptureTarget game when !IsGameProcessCurrent(game) =>
                    (CaptureTarget?)game,
                DisplayCaptureTarget display when !IsDisplayCurrent(display) =>
                    (CaptureTarget?)display,
                RegionCaptureTarget region when !_captureEngine.IsRegionTargetAvailable(region) =>
                    (CaptureTarget?)region,
                _ => null,
            };
            if (lostTarget is null)
                return;

            if (TutorialCapture.HandleSourceLoss(context) is { } tutorialLoss)
            {
                if (await tutorialLoss.ConfigureAwait(false)) return;
            }

            LogTransition(await _lifecycle.HandleSourceLostAsync(new CaptureSourceLoss(
                context.SessionId,
                context.Generation,
                lostTarget,
                DateTimeOffset.UtcNow)));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check capture-source health");
        }
        finally
        {
            Volatile.Write(ref _sourceHealthCheckActive, 0);
        }
    }

    private Task<CaptureTransitionResult> BeginAsync(
        CaptureRequest request,
        CancellationToken cancellationToken = default) =>
        _lifecycle.Snapshot.Status == CaptureLifecycleStatus.Active
            ? _lifecycle.SwitchAsync(request, cancellationToken)
            : _lifecycle.StartAsync(request, cancellationToken);

    private async Task<CaptureTransitionResult?> StartAutomaticGameCaptureAsync(
        GameInfo game,
        CancellationToken cancellationToken = default)
    {
        var target = ToGameTarget(game);
        var pending = BeginAutomaticStart(target, cancellationToken);
        var pendingToken = pending.Token;
        try
        {
            var settings = _settings;
            var decision = GameCapturePolicy.Resolve(settings, game.ExecutableName, _captureEngine.Config);
            if (decision.Disposition != CapturePolicyDisposition.Ready)
            {
                _eventBus.Publish(new AutomaticCapturePolicyDecisionEvent(target, decision));
                _logger.LogWarning("Automatic capture policy for {Game}: {Disposition} — {Reason}",
                    game.DisplayName, decision.Disposition, decision.Message);
                return null;
            }
            // Freeze before the first asynchronous acquisition. Later edits cannot change this request.
            var request = NewRequest(CaptureInitiation.Automatic, target, decision.Policy);
            var resolved = await ExactGameWindowAcquisition.ResolveAsync(
                _captureEngine,
                target,
                pendingToken);
            if (resolved is null)
            {
                _logger.LogWarning(
                    "Exact main window did not become capturable for {Game} PID {Pid}; automatic capture remains stopped",
                    game.DisplayName,
                    game.ProcessId);
                return null;
            }

            pendingToken.ThrowIfCancellationRequested();
            var currentGame = _gameDetector.CurrentGame;
            if (currentGame is null ||
                !GameProcessIdentity.Matches(currentGame, target) ||
                IsManualCaptureActive ||
                _lifecycle.IsAdmissionClosed)
            {
                return null;
            }

            var result = await BeginAsync(request, pendingToken);
            if (result.Failure is { } failure)
                _eventBus.Publish(new AutomaticCapturePolicyDecisionEvent(target,
                    new(CapturePolicyDisposition.Unsupported, null, failure)));
            return result;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Cancelled pending automatic capture for {Game} PID {Pid}",
                game.DisplayName,
                game.ProcessId);
            return null;
        }
        finally
        {
            CompleteAutomaticStart(pending);
        }
    }

    private CancellationTokenSource BeginAutomaticStart(
        GameCaptureTarget target,
        CancellationToken cancellationToken)
    {
        var next = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationTokenSource? previous;
        lock (_automaticStartLock)
        {
            previous = _automaticStartCancellation;
            _automaticStartCancellation = next;
            _pendingAutomaticTarget = target;
        }

        CancelAndDispose(previous);
        return next;
    }

    private void CancelPendingAutomaticStart(GameInfo? expectedGame = null)
    {
        CancellationTokenSource? pending;
        lock (_automaticStartLock)
        {
            if (expectedGame is not null &&
                (_pendingAutomaticTarget is null ||
                 !GameProcessIdentity.Matches(expectedGame, _pendingAutomaticTarget)))
            {
                return;
            }

            pending = _automaticStartCancellation;
            _automaticStartCancellation = null;
            _pendingAutomaticTarget = null;
        }

        CancelAndDispose(pending);
    }

    private void CompleteAutomaticStart(CancellationTokenSource completed)
    {
        var ownsDisposal = false;
        lock (_automaticStartLock)
        {
            if (ReferenceEquals(_automaticStartCancellation, completed))
            {
                _automaticStartCancellation = null;
                _pendingAutomaticTarget = null;
                ownsDisposal = true;
            }
        }

        if (ownsDisposal)
            completed.Dispose();
    }

    private static void CancelAndDispose(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
            return;

        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
    }

    private void OnCaptureSourceLost(CaptureSourceLostEvent sourceLost) =>
        Volatile.Write(ref _lastSourceLossHandling, HandleCaptureSourceLostAsync(sourceLost));

    internal Task AwaitLastSourceLossHandlingAsync() =>
        Volatile.Read(ref _lastSourceLossHandling);

    private void OnCaptureHealthDegraded(CaptureHealthDegradedEvent health) =>
        _ = HandleCaptureHealthDegradedAsync(health);

    private async Task HandleCaptureSourceLostAsync(CaptureSourceLostEvent sourceLost)
    {
        try
        {
            var context = sourceLost.Context;
            if (TutorialCapture.HandleSourceLoss(context) is { } tutorialLoss)
            {
                if (await tutorialLoss.ConfigureAwait(false)) return;
            }
            var result = await _lifecycle.HandleSourceLostAsync(new CaptureSourceLoss(
                context.SessionId,
                context.Generation,
                context.ActualTarget,
                DateTimeOffset.UtcNow));
            LogTransition(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle backend source loss: {Reason}", sourceLost.Reason);
        }
    }

    private async Task HandleCaptureHealthDegradedAsync(CaptureHealthDegradedEvent health)
    {
        try
        {
            LogTransition(await _lifecycle.ReportDegradedAsync(health.Context, health.Failure));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record capture health degradation: {Code}", health.Failure.Code);
        }
    }

    private async Task StartConfiguredDesktopAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        var target = ConfiguredDisplayResolver.Resolve(settings.Capture, _captureEngine);
        if (target is null)
        {
            _logger.LogWarning(
                "Configured display {StableId} is unavailable or has no stable physical identity; capture was not started",
                settings.Capture.MonitorStableId);
            return;
        }

        var request = NewRequest(
            CaptureInitiation.ConfiguredStartup,
            target);
        LogTransition(await BeginAsync(request, cancellationToken));
    }

    private void ShowGameLaunchToast(GameInfo game)
    {
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var highlightsAvailable = GenreClassification.SupportsHighlights(game.Genre);
                var caveat = GenreClassification.GetHighlightCaveat(game.Genre);
                var hasDedicated = _highlightRegistry.HasDedicatedDetector(game.ExecutableName);
                var toast = new Controls.GameLaunchToast(
                    game,
                    highlightsAvailable,
                    highlightsAvailable && !hasDedicated,
                    caveat);

                _activeToast?.Close();
                _activeToast = toast;
                toast.Closed += (_, _) =>
                {
                    if (_activeToast == toast)
                        _activeToast = null;
                };
                toast.ModeSelected += async (mode, remember) =>
                {
                    try
                    {
                        if (remember)
                            SaveGameProfile(game, mode);
                        await ApplyModeToActiveSessionAsync(game, mode);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to apply selected mode for {Game}", game.DisplayName);
                    }
                };
                toast.Show();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show game launch toast");
            }
        });
    }

    private void SaveGameProfile(GameInfo game, GameRecordingMode mode)
    {
        lock (_settingsLock)
        {
            var profiles = _settings.GameProfiles.ToList();
            profiles.RemoveAll(profile => string.Equals(
                profile.GameExecutableName,
                game.ExecutableName,
                StringComparison.OrdinalIgnoreCase));
            profiles.Add(new GameRecordingProfile
            {
                GameExecutableName = game.ExecutableName,
                Mode = mode,
                AutoRecord = true,
            });

            var updated = _settings with { GameProfiles = profiles.AsReadOnly() };
            _settingsService.Save(updated);
            _settings = updated;
        }
    }

    private Task ApplyModeToActiveSessionAsync(GameInfo game, GameRecordingMode mode)
    {
        var current = _lifecycle.Snapshot.Context;
        if (current?.RequestedTarget is not GameCaptureTarget target ||
            !GameProcessIdentity.Matches(game, target))
        {
            _logger.LogInformation("Ignored stale mode selection for {Game} PID {Pid}", game.DisplayName, game.ProcessId);
            return Task.CompletedTask;
        }

        _logger.LogInformation(
            "Selected {Mode} for {Game}; save a supported profile to apply it at the next automatic start. Live output reconfiguration is unsupported",
            mode,
            game.DisplayName);
        return Task.CompletedTask;
    }

    private CaptureRequest NewRequest(CaptureInitiation initiation, CaptureTarget target, CaptureStartPolicy? startPolicy = null)
    {
        var webcam = initiation == CaptureInitiation.Manual
            ? ExplicitManualWebcamPolicy.Snapshot(_settings)
            : null;
        return new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UtcNow, initiation, target, webcam, startPolicy);
    }

    private static GameCaptureTarget ToGameTarget(GameInfo game) => new(
        game.ExecutableName,
        game.ProcessId,
        game.ExecutableName,
        game.ProcessStartedAt,
        game.DisplayName);

    private static bool IsGameProcessCurrent(GameCaptureTarget target)
    {
        try
        {
            using var process = Process.GetProcessById(target.ProcessId);
            return !process.HasExited && GameProcessIdentity.Matches(
                process.Id,
                process.StartTime.ToUniversalTime(),
                process.ProcessName + ".exe",
                target);
        }
        catch
        {
            return false;
        }
    }

    private bool IsDisplayCurrent(DisplayCaptureTarget target)
        => _captureEngine.IsDisplayTargetAvailable(target);

    private static bool ShouldHaveDesktopCapture(AppSettings settings) =>
        (settings.DesktopCaptureEnabled &&
         (settings.Recording.IsReplayBufferEnabled || settings.Recording.IsSessionRecordingEnabled)) ||
        settings.Recording.IsHighlightModeEnabled;

    private static CaptureSource? ToCaptureSource(CaptureSessionContext? context)
    {
        if (context is null)
            return null;

        return context.ActualTarget switch
        {
            GameCaptureTarget game => new CaptureSource(
                CaptureSourceKind.Auto,
                game.ExecutableName,
                game.DisplayName ?? game.GameId,
                game.ProcessId,
                null,
                null,
                GameGenre.Unknown,
                context.Request.RequestedAt),
            WindowCaptureTarget window => new CaptureSource(
                context.Request.Initiation == CaptureInitiation.Automatic
                    ? CaptureSourceKind.Auto
                    : CaptureSourceKind.Manual,
                window.ExecutableName ?? "unknown.exe",
                window.WindowTitle ?? "Selected window",
                window.ProcessId,
                window.WindowHandle,
                window.WindowClass,
                GameGenre.Unknown,
                context.Request.RequestedAt),
            DisplayCaptureTarget display => new CaptureSource(
                CaptureSourceKind.Desktop,
                "desktop",
                display.DisplayName ?? "Desktop",
                0,
                null,
                null,
                GameGenre.Unknown,
                context.Request.RequestedAt),
            RegionCaptureTarget region => new CaptureSource(
                CaptureSourceKind.Region,
                "region",
                $"Region {region.PhysicalBounds.Width}x{region.PhysicalBounds.Height}",
                0,
                null,
                null,
                GameGenre.Unknown,
                context.Request.RequestedAt),
            _ => null,
        };
    }

    internal void LogTransition(CaptureTransitionResult result)
    {
        var degraded = result.Operation == CaptureOperationKind.HealthChanged &&
            result.Disposition == CaptureCommandDisposition.Completed &&
            result.After.Health == CaptureHealth.Degraded;
        _logger.Log(
            result.Succeeded && !degraded ? LogLevel.Information : LogLevel.Warning,
            "Capture transition {Operation}: {Disposition}, {Kind}, {OperationId}, {BeforeSessionId}, {AfterSessionId}, {BeforeGeneration}, {AfterGeneration}, {BeforeStatus}, {AfterStatus}, {BeforeHealth}, {AfterHealth}, {AfterLastFailureKind}, {AfterContextPresent}",
            result.Operation,
            result.Disposition,
            result.Failure?.Kind,
            result.OperationId,
            result.Before.Context?.SessionId,
            result.After.Context?.SessionId,
            result.Before.Context?.Generation,
            result.After.Context?.Generation,
            result.Before.Status,
            result.After.Status,
            result.Before.Health,
            result.After.Health,
            degraded ? result.After.LastFailure?.Kind : null,
            result.After.Context is not null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        TutorialCapture.CloseAdmission();
        CancelPendingAutomaticStart();
        _gameDetector.GameStarted -= OnGameStarted;
        _gameDetector.GameStopped -= OnGameStopped;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _gameDetector.Stop();
        _sourceHealthTimer?.Dispose();
        _sourceLostSubscription.Dispose();
        _healthDegradedSubscription.Dispose();
        _activeToast?.Close();
        Volatile.Write(ref _disposalCompletion, ObserveDisposalAsync());
    }

    private async Task ObserveDisposalAsync()
    {
        try { await DrainTutorialShutdownAsync().ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning("Capture disposal drain failed ({ExceptionType})", ex.GetType().Name);
        }
    }
}
