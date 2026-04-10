using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
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
    private AppSettings _settings;
    private readonly ILogger<GameDetectorHostedService> _logger;

    public GameDetectorHostedService(
        IGameDetector gameDetector,
        ICaptureEngine captureEngine,
        IEventBus eventBus,
        SessionManager sessionManager,
        AppSettings settings,
        ISettingsService settingsService,
        ILogger<GameDetectorHostedService> logger)
    {
        _gameDetector = gameDetector;
        _captureEngine = captureEngine;
        _eventBus = eventBus;
        _sessionManager = sessionManager;
        _settings = settings;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _gameDetector.GameStarted += OnGameStarted;
        _gameDetector.GameStopped += OnGameStopped;
        _settingsService.SettingsChanged += OnSettingsChanged;
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
            var wasShouldCapture = ShouldHaveDesktopCapture(_settings);
            _settings = newSettings;
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

    private async void OnGameStopped(GameInfo game)
    {
        _logger.LogInformation("Game exited: {Game}", game.DisplayName);

        await _sessionManager.EndSessionAsync(game);
        _eventBus.Publish(new GameExitedEvent(game));

        try
        {
            if (_settings.DesktopCaptureEnabled || _settings.Recording.IsHighlightModeEnabled)
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
