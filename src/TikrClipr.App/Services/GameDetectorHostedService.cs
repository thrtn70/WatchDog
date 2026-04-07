using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TikrClipr.Core.Capture;
using TikrClipr.Core.Events;
using TikrClipr.Core.GameDetection;
using TikrClipr.Core.Settings;

namespace TikrClipr.App.Services;

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
    private readonly ISettingsService _settingsService;
    private AppSettings _settings;
    private readonly ILogger<GameDetectorHostedService> _logger;

    public GameDetectorHostedService(
        IGameDetector gameDetector,
        ICaptureEngine captureEngine,
        IEventBus eventBus,
        AppSettings settings,
        ISettingsService settingsService,
        ILogger<GameDetectorHostedService> logger)
    {
        _gameDetector = gameDetector;
        _captureEngine = captureEngine;
        _eventBus = eventBus;
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

        if (_settings.DesktopCaptureEnabled)
        {
            try
            {
                await _captureEngine.StartDesktopCaptureAsync(cancellationToken);
                _logger.LogInformation("Always-on desktop capture started");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to start desktop capture");
            }
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
        var wasEnabled = _settings.DesktopCaptureEnabled;
        _settings = newSettings;

        if (newSettings.DesktopCaptureEnabled && !wasEnabled)
        {
            // Desktop capture was just enabled
            if (_captureEngine.State == CaptureState.Idle)
            {
                _logger.LogInformation("Desktop capture enabled via settings, starting");
                await _captureEngine.StartDesktopCaptureAsync();
            }
        }
        else if (!newSettings.DesktopCaptureEnabled && wasEnabled)
        {
            // Desktop capture was just disabled
            if (_captureEngine.IsDesktopCapture)
            {
                _logger.LogInformation("Desktop capture disabled via settings, stopping");
                await _captureEngine.StopAsync();
            }
        }
    }

    private async void OnGameStarted(GameInfo game)
    {
        _logger.LogInformation("Game detected: {Game} — starting capture", game.DisplayName);
        _eventBus.Publish(new GameDetectedEvent(game));

        try
        {
            await _captureEngine.StartAsync(game);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start capture for {Game}", game.DisplayName);
        }
    }

    private async void OnGameStopped(GameInfo game)
    {
        _logger.LogInformation("Game exited: {Game}", game.DisplayName);
        _eventBus.Publish(new GameExitedEvent(game));

        try
        {
            if (_settings.DesktopCaptureEnabled)
            {
                _logger.LogInformation("Switching back to desktop capture");
                await _captureEngine.SwitchToDesktopCaptureAsync();
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
