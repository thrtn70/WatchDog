using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Highlights;
using WatchDog.Core.Recording;
using WatchDog.Core.Sessions;
using WatchDog.Core.Settings;

namespace WatchDog.App.Services;

/// <summary>
/// Orchestrates the active capture source lifecycle.
/// Replaces GameDetectorHostedService with unified support for auto-detected games,
/// manual window capture, and desktop fallback.
/// </summary>
public sealed class CaptureSourceManager : IHostedService
{
    private readonly IGameDetector _gameDetector;
    private readonly ICaptureEngine _captureEngine;
    private readonly IEventBus _eventBus;
    private readonly SessionManager _sessionManager;
    private readonly ISettingsService _settingsService;
    private readonly HighlightDetectorRegistry _highlightRegistry;
    private volatile AppSettings _settings;
    private readonly ILogger<CaptureSourceManager> _logger;
    private readonly object _settingsLock = new();
    private Controls.GameLaunchToast? _activeToast;

    /// <summary>The currently active capture source, or null if idle.</summary>
    /// <remarks>Volatile: written from WMI/timer threads (game detection) and UI thread (manual capture).</remarks>
    private volatile CaptureSource? _activeSource;
    public CaptureSource? ActiveSource
    {
        get => _activeSource;
        private set => _activeSource = value;
    }

    /// <summary>Whether a manual window capture is active (suppresses auto-detection).</summary>
    public bool IsManualCaptureActive => ActiveSource?.Kind == CaptureSourceKind.Manual;

    /// <summary>Merge window duration — same-game detections within this period are deduplicated.</summary>
    private static readonly TimeSpan MergeWindow = TimeSpan.FromSeconds(120);

    public CaptureSourceManager(
        IGameDetector gameDetector,
        ICaptureEngine captureEngine,
        IEventBus eventBus,
        SessionManager sessionManager,
        AppSettings settings,
        ISettingsService settingsService,
        HighlightDetectorRegistry highlightRegistry,
        ILogger<CaptureSourceManager> logger)
    {
        _gameDetector = gameDetector;
        _captureEngine = captureEngine;
        _eventBus = eventBus;
        _sessionManager = sessionManager;
        _settings = settings;
        _settingsService = settingsService;
        _highlightRegistry = highlightRegistry;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _gameDetector.GameStarted += OnGameStarted;
        _gameDetector.GameStopped += OnGameStopped;
        _settingsService.SettingsChanged += OnSettingsChanged;

        // Initialize OBS before starting game detection — if a game is already
        // running when WatchDog launches, OnGameStarted fires immediately and
        // needs OBS ready. OBS must be initialized for ANY recording mode.
        if (_settings.Recording.IsReplayBufferEnabled || _settings.Recording.IsSessionRecordingEnabled)
        {
            try
            {
                _captureEngine.EnsureInitialized();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to initialize capture engine — recording will be unavailable");
            }
        }

        _gameDetector.Start();

        _logger.LogInformation("Capture source manager started — watching for games");

        // Highlights mode always needs the replay buffer running for auto-clips + hotkey
        var shouldStartDesktop = _settings.Recording.IsReplayBufferEnabled &&
            (_settings.DesktopCaptureEnabled || _settings.Recording.IsHighlightModeEnabled);

        if (shouldStartDesktop)
        {
            try
            {
                await _captureEngine.StartDesktopCaptureAsync(cancellationToken);
                await _sessionManager.StartDesktopSessionAsync(cancellationToken);
                ActiveSource = CaptureSource.Desktop();
                _logger.LogInformation("Desktop capture started (desktopCapture={Desktop}, highlights={Highlights})",
                    _settings.DesktopCaptureEnabled, _settings.Recording.IsHighlightModeEnabled);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start desktop capture");
            }
        }
        else if (!_settings.Recording.IsReplayBufferEnabled)
        {
            _logger.LogInformation("Replay buffer disabled (mode: {Mode}) — skipping desktop capture",
                _settings.Recording.Mode);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _gameDetector.GameStarted -= OnGameStarted;
        _gameDetector.GameStopped -= OnGameStopped;
        _settingsService.SettingsChanged -= OnSettingsChanged;
        _gameDetector.Stop();
        ActiveSource = null;

        return Task.CompletedTask;
    }

    // ── Manual Capture ──────────────────────────────────────────────────

    /// <summary>
    /// Starts manual capture of a specific window. Suppresses auto-detection
    /// and activates the AI audio highlight detector as fallback.
    /// </summary>
    public async Task StartManualCaptureAsync(
        string executableName,
        string windowTitle,
        int processId,
        IntPtr windowHandle,
        string? windowClass,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Starting manual capture: {Window} ({Exe}, PID {Pid})",
            windowTitle, executableName, processId);

        try
        {
            // Stop any current capture source
            if (ActiveSource is not null)
            {
                await StopActiveSourceAsync(ct);
            }

            var source = CaptureSource.FromWindow(
                executableName, windowTitle, processId, windowHandle, windowClass);

            // Initialize OBS if not already done
            _captureEngine.EnsureInitialized();

            // Start window capture via OBS
            await _captureEngine.StartWindowCaptureAsync(source, ct);

            // Start session
            await _sessionManager.StartSessionAsync(source.ToGameInfo(), ct);

            // Start AI audio highlight detector as fallback
            var gameInfo = source.ToGameInfo();
            _eventBus.Publish(new GameDetectedEvent(gameInfo));

            ActiveSource = source;
            _logger.LogInformation("Manual capture active: {Window}", windowTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start manual capture for {Window}", windowTitle);
        }
    }

    /// <summary>
    /// Stops manual capture and resumes auto-detection.
    /// </summary>
    public async Task StopManualCaptureAsync(CancellationToken ct = default)
    {
        if (!IsManualCaptureActive)
            return;

        _logger.LogInformation("Stopping manual capture: {Window}", ActiveSource!.DisplayName);

        try
        {
            await StopActiveSourceAsync(ct);

            // Resume desktop capture if configured
            if (ShouldHaveDesktopCapture(_settings))
            {
                await _captureEngine.StartDesktopCaptureAsync(ct);
                await _sessionManager.StartDesktopSessionAsync(ct);
                ActiveSource = CaptureSource.Desktop();
                _logger.LogInformation("Resumed desktop capture after manual capture ended");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop manual capture");
        }
    }

    // ── Auto-Detection Handlers ─────────────────────────────────────────

    private async void OnGameStarted(GameInfo game)
    {
        try
        {
            // Suppress auto-detection while manual capture is active
            if (IsManualCaptureActive)
            {
                _logger.LogInformation("Game detected: {Game} — suppressed (manual capture active)", game.DisplayName);
                return;
            }

            // Deduplication: same executable within merge window = same session
            if (ActiveSource is not null &&
                string.Equals(ActiveSource.ExecutableName, game.ExecutableName, StringComparison.OrdinalIgnoreCase) &&
                DateTimeOffset.UtcNow - ActiveSource.InitiatedAt < MergeWindow)
            {
                _logger.LogInformation("Game detected: {Game} — deduplicated (same exe within {Window}s merge window)",
                    game.DisplayName, MergeWindow.TotalSeconds);
                return;
            }

            _logger.LogInformation("Game detected: {Game} — starting capture", game.DisplayName);
            // Check for a saved per-game profile
            var profile = _settings.GameProfiles
                .FirstOrDefault(p => string.Equals(p.GameExecutableName, game.ExecutableName,
                    StringComparison.OrdinalIgnoreCase));

            if (profile is not null)
            {
                _logger.LogInformation("Applying saved profile for {Game}: {Mode}",
                    game.DisplayName, profile.Mode);
            }
            else
            {
                ShowGameLaunchToast(game);
            }

            if (_settings.Recording.IsReplayBufferEnabled)
                await _captureEngine.StartAsync(game);

            await _sessionManager.StartSessionAsync(game);

            ActiveSource = CaptureSource.FromGame(game);

            // Publish AFTER capture engine is initialized so session recorder
            // can safely use OBS resources
            _eventBus.Publish(new GameDetectedEvent(game));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start capture for {Game}", game.DisplayName);
        }
    }

    private async void OnGameStopped(GameInfo game)
    {
        try
        {
            // If manual capture is active and the manually captured process exits
            if (IsManualCaptureActive &&
                string.Equals(ActiveSource!.ExecutableName, game.ExecutableName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Manually captured process exited: {Game}", game.DisplayName);
                await StopManualCaptureAsync();
                return;
            }

            // If manual capture is active for a DIFFERENT window, ignore stale game-exit
            // events from previously auto-detected games (ProcessGameDetector still tracks them)
            if (IsManualCaptureActive)
            {
                _logger.LogDebug("Game exited: {Game} — ignored (manual capture active for {Window})",
                    game.DisplayName, ActiveSource!.DisplayName);
                return;
            }

            _logger.LogInformation("Game exited: {Game}", game.DisplayName);
            await _sessionManager.EndSessionAsync(game);
            _eventBus.Publish(new GameExitedEvent(game));
            ActiveSource = null;

            if (_settings.Recording.IsReplayBufferEnabled &&
                (_settings.DesktopCaptureEnabled || _settings.Recording.IsHighlightModeEnabled))
            {
                _logger.LogInformation("Switching back to desktop capture");
                await _captureEngine.SwitchToDesktopCaptureAsync();
                await _sessionManager.StartDesktopSessionAsync();
                ActiveSource = CaptureSource.Desktop();
            }
            else
            {
                await _captureEngine.StopAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle game exit for {Game}", game.DisplayName);
        }
    }

    // ── Settings ────────────────────────────────────────────────────────

    private async void OnSettingsChanged(AppSettings newSettings)
    {
        try
        {
            bool wasShouldCapture;
            lock (_settingsLock)
            {
                wasShouldCapture = ShouldHaveDesktopCapture(_settings);
                _settings = newSettings;
            }
            var nowShouldCapture = ShouldHaveDesktopCapture(newSettings);

            // Don't change capture mode while manual capture is active
            if (IsManualCaptureActive)
                return;

            if (nowShouldCapture && !wasShouldCapture)
            {
                if (_captureEngine.State == CaptureState.Idle)
                {
                    _logger.LogInformation("Desktop capture enabled via settings change, starting");
                    await _captureEngine.StartDesktopCaptureAsync();
                    await _sessionManager.StartDesktopSessionAsync();
                    ActiveSource = CaptureSource.Desktop();
                }
            }
            else if (!nowShouldCapture && wasShouldCapture)
            {
                if (_captureEngine.IsDesktopCapture)
                {
                    _logger.LogInformation("Desktop capture disabled via settings change, stopping");
                    var desktop = ActiveSource?.ToGameInfo() ?? new GameInfo
                    {
                        ExecutableName = "desktop",
                        DisplayName = "Desktop",
                    };
                    await _sessionManager.EndSessionAsync(desktop);
                    await _captureEngine.StopAsync();
                    ActiveSource = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle settings change for desktop capture");
        }
    }

    private static bool ShouldHaveDesktopCapture(AppSettings settings) =>
        settings.Recording.IsReplayBufferEnabled &&
        (settings.DesktopCaptureEnabled || settings.Recording.IsHighlightModeEnabled);

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Stops the current active source — capture engine, session, and events.</summary>
    private async Task StopActiveSourceAsync(CancellationToken ct = default)
    {
        if (ActiveSource is null) return;

        var gameInfo = ActiveSource.ToGameInfo();

        await _sessionManager.EndSessionAsync(gameInfo, ct);
        _eventBus.Publish(new GameExitedEvent(gameInfo));
        await _captureEngine.StopAsync(ct);

        _logger.LogInformation("Stopped capture source: {Name} ({Kind})",
            ActiveSource.DisplayName, ActiveSource.Kind);
        ActiveSource = null;
    }

    private void ShowGameLaunchToast(GameInfo game)
    {
        // InvokeAsync: non-blocking — don't stall the WMI/timer detection thread
        System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                var highlightsAvailable = GenreClassification.SupportsHighlights(game.Genre);
                var caveat = GenreClassification.GetHighlightCaveat(game.Genre);
                var hasDedicated = _highlightRegistry.HasDedicatedDetector(game.ExecutableName);
                var isAiFallback = highlightsAvailable && !hasDedicated;

                _activeToast?.Close();

                var toast = new Controls.GameLaunchToast(game, highlightsAvailable, isAiFallback, caveat);
                _activeToast = toast;
                toast.Closed += (_, _) =>
                {
                    if (_activeToast == toast) _activeToast = null;
                };
                toast.ModeSelected += async (mode, remember) =>
                {
                    try
                    {
                        _logger.LogInformation("User selected {Mode} for {Game} (remember={Remember})",
                            mode, game.DisplayName, remember);

                        if (remember)
                        {
                            SaveGameProfile(game, mode);
                        }

                        await ApplyModeToActiveSessionAsync(game, mode);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unhandled error in ModeSelected handler for {Game}",
                            game.DisplayName);
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
        try
        {
            lock (_settingsLock)
            {
                var newProfile = new GameRecordingProfile
                {
                    GameExecutableName = game.ExecutableName,
                    Mode = mode,
                    AutoRecord = true,
                };

                var existingProfiles = _settings.GameProfiles.ToList();
                existingProfiles.RemoveAll(p => string.Equals(p.GameExecutableName, game.ExecutableName,
                    StringComparison.OrdinalIgnoreCase));
                existingProfiles.Add(newProfile);

                var updatedSettings = _settings with { GameProfiles = existingProfiles.AsReadOnly() };
                _settingsService.Save(updatedSettings);
                _settings = updatedSettings;
            }

            _logger.LogInformation("Saved profile for {Game}: {Mode}", game.DisplayName, mode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save game profile for {Game}", game.DisplayName);
        }
    }

    private async Task ApplyModeToActiveSessionAsync(GameInfo game, GameRecordingMode mode)
    {
        try
        {
            _logger.LogInformation("Applying {Mode} to active session for {Game}", mode, game.DisplayName);

            await _captureEngine.StopAsync();
            await _captureEngine.StartAsync(game);

            _logger.LogInformation("Applied {Mode} to active session for {Game}", mode, game.DisplayName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply {Mode} to active session for {Game}",
                mode, game.DisplayName);
        }
    }
}
