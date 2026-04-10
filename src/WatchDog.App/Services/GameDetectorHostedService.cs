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
/// Bridges game detection events to the capture engine.
/// When DesktopCaptureEnabled, starts desktop capture on startup and falls back to it on game exit.
/// Otherwise, starts/stops capture per game lifecycle.
/// </summary>
public sealed class GameDetectorHostedService : IHostedService
{
    private readonly IGameDetector _gameDetector;
    private readonly ICaptureEngine _captureEngine;
    private readonly IEventBus _eventBus;
    private readonly SessionManager _sessionManager;
    private readonly ISettingsService _settingsService;
    private readonly HighlightDetectorRegistry _highlightRegistry;
    private volatile AppSettings _settings;
    private readonly ILogger<GameDetectorHostedService> _logger;
    private readonly object _settingsLock = new();
    private Controls.GameLaunchToast? _activeToast;

    public GameDetectorHostedService(
        IGameDetector gameDetector,
        ICaptureEngine captureEngine,
        IEventBus eventBus,
        SessionManager sessionManager,
        AppSettings settings,
        ISettingsService settingsService,
        HighlightDetectorRegistry highlightRegistry,
        ILogger<GameDetectorHostedService> logger)
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
        // needs OBS ready. Without this, a race condition causes:
        // "OBS is not initialized. Call Obs.Initialize() first."
        if (_settings.Recording.IsReplayBufferEnabled)
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

        _logger.LogInformation("Game detector service started — watching for games");

        // Highlights mode always needs the replay buffer running for auto-clips + F9 hotkey
        var shouldStartDesktop = _settings.Recording.IsReplayBufferEnabled &&
            (_settings.DesktopCaptureEnabled || _settings.Recording.IsHighlightModeEnabled);

        if (shouldStartDesktop)
        {
            try
            {
                await _captureEngine.StartDesktopCaptureAsync(cancellationToken);
                await _sessionManager.StartDesktopSessionAsync(cancellationToken);
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

        return Task.CompletedTask;
    }

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

            if (nowShouldCapture && !wasShouldCapture)
            {
                if (_captureEngine.State == CaptureState.Idle)
                {
                    _logger.LogInformation("Desktop capture enabled via settings change, starting");
                    await _captureEngine.StartDesktopCaptureAsync();
                }
            }
            else if (!nowShouldCapture && wasShouldCapture)
            {
                if (_captureEngine.IsDesktopCapture)
                {
                    _logger.LogInformation("Desktop capture disabled via settings change, stopping");
                    await _captureEngine.StopAsync();
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

    private async void OnGameStarted(GameInfo game)
    {
        _logger.LogInformation("Game detected: {Game} — starting capture", game.DisplayName);

        try
        {
            // Check for a saved per-game profile
            var profile = _settings.GameProfiles
                .FirstOrDefault(p => string.Equals(p.GameExecutableName, game.ExecutableName,
                    StringComparison.OrdinalIgnoreCase));

            if (profile is not null)
            {
                // Known game with saved profile — apply silently
                _logger.LogInformation("Applying saved profile for {Game}: {Mode}",
                    game.DisplayName, profile.Mode);
            }
            else
            {
                // New game — show toast on UI thread to let user choose mode
                ShowGameLaunchToast(game);
            }

            if (_settings.Recording.IsReplayBufferEnabled)
                await _captureEngine.StartAsync(game);

            await _sessionManager.StartSessionAsync(game);

            // Publish AFTER capture engine is initialized so session recorder
            // can safely use OBS resources
            _eventBus.Publish(new GameDetectedEvent(game));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start capture for {Game}", game.DisplayName);
        }
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
                // Check if a dedicated detector (CS2 GSI, Valorant lockfile, etc.) exists
                // vs AI audio fallback — determines the label shown on the toast button
                var hasDedicated = _highlightRegistry.HasDedicatedDetector(game.ExecutableName);
                var isAiFallback = highlightsAvailable && !hasDedicated;

                // Close any existing toast before showing a new one
                _activeToast?.Close();

                var toast = new Controls.GameLaunchToast(game, highlightsAvailable, isAiFallback, caveat);
                _activeToast = toast;
                toast.Closed += (_, _) =>
                {
                    if (_activeToast == toast) _activeToast = null;
                };
                toast.ModeSelected += (mode, remember) =>
                {
                    _logger.LogInformation("User selected {Mode} for {Game} (remember={Remember})",
                        mode, game.DisplayName, remember);

                    if (remember)
                    {
                        SaveGameProfile(game, mode);
                    }

                    // TODO: Apply mode to the already-running capture session.
                    // Currently the profile only takes effect on the next game launch.
                    // Full runtime mode switching requires ObsCaptureEngine changes
                    // (reconfigure encoder/buffer mid-session) — tracked for follow-up.
                };
                toast.Show();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to show game launch toast");
            }
        });
    }

    private void SaveGameProfile(GameInfo game, Settings.RecordingMode mode)
    {
        try
        {
            // Lock the read-modify-write cycle to prevent lost updates when
            // OnSettingsChanged fires concurrently (e.g., user changes settings
            // while a game-launch toast is resolving).
            lock (_settingsLock)
            {
                var newProfile = new Settings.GameRecordingProfile
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

    private async void OnGameStopped(GameInfo game)
    {
        _logger.LogInformation("Game exited: {Game}", game.DisplayName);

        try
        {
            await _sessionManager.EndSessionAsync(game);
            _eventBus.Publish(new GameExitedEvent(game));

            if (_settings.Recording.IsReplayBufferEnabled &&
                (_settings.DesktopCaptureEnabled || _settings.Recording.IsHighlightModeEnabled))
            {
                _logger.LogInformation("Switching back to desktop capture");
                await _captureEngine.SwitchToDesktopCaptureAsync();
                await _sessionManager.StartDesktopSessionAsync();
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
}
