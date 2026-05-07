using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Recording;
using WatchDog.Core.Sessions;
using WatchDog.Core.Settings;

namespace WatchDog.App.Services;

/// <summary>
/// Manages session recording lifecycle based on game detection events.
/// Starts session recording when a game is detected (if mode allows),
/// stops when the game exits.
/// </summary>
public sealed class SessionRecordingHostedService : IHostedService, IDisposable
{
    private readonly ISessionRecorder _recorder;
    private readonly IEventBus _eventBus;
    private readonly SessionManager _sessionManager;
    private readonly AppSettings _settings;
    private readonly ILogger<SessionRecordingHostedService> _logger;

    private IDisposable? _gameDetectedSub;
    private IDisposable? _gameExitedSub;

    public SessionRecordingHostedService(
        ISessionRecorder recorder,
        IEventBus eventBus,
        SessionManager sessionManager,
        AppSettings settings,
        ILogger<SessionRecordingHostedService> logger)
    {
        _recorder = recorder;
        _eventBus = eventBus;
        _sessionManager = sessionManager;
        _settings = settings;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_settings.Recording.IsSessionRecordingEnabled)
        {
            _logger.LogInformation("Session recording disabled (mode: {Mode})", _settings.Recording.Mode);
            return Task.CompletedTask;
        }

        _gameDetectedSub = _eventBus.Subscribe<GameDetectedEvent>(OnGameDetected);
        _gameExitedSub = _eventBus.Subscribe<GameExitedEvent>(OnGameExited);

        _recorder.SegmentSaved += OnSegmentSaved;
        _recorder.Error += OnRecorderError;

        _logger.LogInformation("Session recording service started (mode: {Mode}, segment: {Segment}min)",
            _settings.Recording.Mode, _settings.Recording.SegmentDurationMinutes);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_recorder.IsRecording)
        {
            _logger.LogInformation("Stopping session recording on service shutdown");
            await _recorder.StopAsync(cancellationToken);
            _eventBus.Publish(new SessionRecordingStoppedEvent(null, _recorder.Elapsed));
        }

        _recorder.SegmentSaved -= OnSegmentSaved;
        _recorder.Error -= OnRecorderError;
        _gameDetectedSub?.Dispose();
        _gameExitedSub?.Dispose();
    }

    private async void OnGameDetected(GameDetectedEvent e)
    {
        if (_recorder.IsRecording)
        {
            _logger.LogInformation("Game changed to {Game} — session recording already active", e.Game.DisplayName);
            return;
        }

        var safeName = string.Concat(
            e.Game.DisplayName.Split(Path.GetInvalidFileNameChars()));
        var outputDir = Path.Combine(
            _settings.Storage.SavePath,
            safeName,
            "Sessions");

        // Verify resolved path is strictly underneath the bounded base directory.
        var resolvedBase = Path.GetFullPath(_settings.Storage.SavePath)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolvedOutput = Path.GetFullPath(outputDir);

        if (!resolvedOutput.StartsWith(resolvedBase, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refusing unsafe session path traversal: {Path}, using secure base path", resolvedOutput);
            outputDir = Path.Combine(_settings.Storage.SavePath, "Sessions");
        }

        try
        {
            await _recorder.StartAsync(outputDir, e.Game);
            _eventBus.Publish(new SessionRecordingStartedEvent(
                _recorder.CurrentOutputPath ?? outputDir, e.Game));
            _logger.LogInformation("Session recording started for {Game}", e.Game.DisplayName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start session recording for {Game}", e.Game.DisplayName);
        }
    }

    private async void OnGameExited(GameExitedEvent e)
    {
        if (!_recorder.IsRecording)
            return;

        var elapsed = _recorder.Elapsed;

        try
        {
            await _recorder.StopAsync();
            _eventBus.Publish(new SessionRecordingStoppedEvent(e.Game, elapsed));
            _logger.LogInformation("Session recording stopped for {Game} ({Duration})",
                e.Game.DisplayName, elapsed);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Session recording stop cancelled for {Game}", e.Game.DisplayName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop session recording for {Game}", e.Game.DisplayName);
        }
    }

    private async void OnSegmentSaved(string filePath)
    {
        try
        {
            _eventBus.Publish(new SessionRecordingSegmentSavedEvent(
                filePath, null, _recorder.Elapsed));
            _logger.LogInformation("Session segment saved: {File}", Path.GetFileName(filePath));

            await _sessionManager.AddRecordingPathAsync(filePath);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Segment tracking cancelled for {File}", Path.GetFileName(filePath));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to track recording path in session");
        }
    }

    private void OnRecorderError(string message)
    {
        _logger.LogError("Session recorder error: {Error}", message);
    }

    public void Dispose()
    {
        _recorder.SegmentSaved -= OnSegmentSaved;
        _recorder.Error -= OnRecorderError;
        _gameDetectedSub?.Dispose();
        _gameExitedSub?.Dispose();
    }
}
