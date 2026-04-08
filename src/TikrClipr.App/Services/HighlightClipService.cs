using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TikrClipr.Core.Capture;
using TikrClipr.Core.Events;
using TikrClipr.Core.Highlights;
using TikrClipr.Core.Recording;
using TikrClipr.Core.Storage;

namespace TikrClipr.App.Services;

/// <summary>
/// Orchestrates auto-clipping when highlights are detected.
/// Subscribes to HighlightDetectedEvent, waits the post-event delay,
/// then saves the replay buffer and indexes the clip with highlight metadata.
/// </summary>
public sealed class HighlightClipService : IHostedService, IDisposable
{
    private readonly ICaptureEngine _captureEngine;
    private readonly IClipStorage _clipStorage;
    private readonly IEventBus _eventBus;
    private readonly HighlightDetectorRegistry _registry;
    private readonly HighlightConfig _config;
    private readonly SessionRecordingConfig _recordingConfig;
    private readonly ILogger<HighlightClipService> _logger;

    private IDisposable? _highlightSub;
    private IDisposable? _gameDetectedSub;
    private IDisposable? _gameExitedSub;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private DateTimeOffset _lastSaveTime = DateTimeOffset.MinValue;

    public HighlightClipService(
        ICaptureEngine captureEngine,
        IClipStorage clipStorage,
        IEventBus eventBus,
        HighlightDetectorRegistry registry,
        HighlightConfig config,
        SessionRecordingConfig recordingConfig,
        ILogger<HighlightClipService> logger)
    {
        _captureEngine = captureEngine;
        _clipStorage = clipStorage;
        _eventBus = eventBus;
        _registry = registry;
        _config = config;
        _recordingConfig = recordingConfig;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_recordingConfig.IsHighlightModeEnabled || !_config.Enabled)
        {
            _logger.LogInformation("Highlight clip service disabled (mode: {Mode}, enabled: {Enabled})",
                _recordingConfig.Mode, _config.Enabled);
            return Task.CompletedTask;
        }

        _cts = new CancellationTokenSource();

        _highlightSub = _eventBus.Subscribe<HighlightDetectedEvent>(OnHighlightDetected);
        _gameDetectedSub = _eventBus.Subscribe<GameDetectedEvent>(OnGameDetected);
        _gameExitedSub = _eventBus.Subscribe<GameExitedEvent>(OnGameExited);

        _logger.LogInformation(
            "Highlight clip service started (delay: {Delay}s, cooldown: {Cooldown}s, types: {Types})",
            _config.PostEventDelaySeconds, _config.CooldownSeconds,
            string.Join(", ", _config.EnabledTypes));

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        await _registry.StopActiveDetectorAsync(cancellationToken);
    }

    private async void OnGameDetected(GameDetectedEvent e)
    {
        try
        {
            await _registry.StartDetectorForGameAsync(e.Game, _cts?.Token ?? default);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start highlight detector for {Game}", e.Game.DisplayName);
        }
    }

    private async void OnGameExited(GameExitedEvent e)
    {
        try
        {
            // Cancel pending saves and replace CTS under lock
            await _saveLock.WaitAsync();
            try
            {
                _cts?.Cancel();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
            }
            finally
            {
                _saveLock.Release();
            }

            await _registry.StopActiveDetectorAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop highlight detector for {Game}", e.Game.DisplayName);
        }
    }

    private async void OnHighlightDetected(HighlightDetectedEvent e)
    {
        try
        {
            // Check if this type is enabled
            if (!_config.EnabledTypes.Contains(e.Type))
            {
                _logger.LogDebug("Ignoring highlight type {Type} (not in enabled set)", e.Type);
                return;
            }

            // Serialize all save operations to prevent concurrent buffer saves
            await _saveLock.WaitAsync();
            CancellationToken ct;
            try
            {
                // Check cooldown (atomic under lock)
                var sinceLastSave = DateTimeOffset.UtcNow - _lastSaveTime;
                if (sinceLastSave.TotalSeconds < _config.CooldownSeconds)
                {
                    _logger.LogDebug("Skipping highlight {Type} — cooldown ({Elapsed:F1}s < {Cooldown}s)",
                        e.Type, sinceLastSave.TotalSeconds, _config.CooldownSeconds);
                    return;
                }

                _lastSaveTime = DateTimeOffset.UtcNow;
                ct = _cts?.Token ?? default;
            }
            finally
            {
                _saveLock.Release();
            }

            _logger.LogInformation("Highlight {Type} detected — saving in {Delay}s: {Description}",
                e.Type, _config.PostEventDelaySeconds, e.Description ?? "");

            // Wait for post-event delay so the buffer captures the aftermath
            await Task.Delay(_config.PostEventDelaySeconds * 1000, ct);

            // Save the replay buffer
            if (_captureEngine.State != CaptureState.Buffering)
            {
                _logger.LogWarning("Cannot save highlight — capture not in buffering state");
                return;
            }

            var path = await _captureEngine.SaveReplayAsync(ct);
            if (path is null)
            {
                _logger.LogWarning("Highlight save returned null path");
                return;
            }

            // Index with highlight metadata — HighlightType is preserved in ClipMetadata
            await _clipStorage.IndexClipAsync(path, e.Game.DisplayName, e.Type, ct);
            _logger.LogInformation("Highlight clip saved: {Type} — {Path}", e.Type, Path.GetFileName(path));
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Highlight save cancelled (game exited or service stopping)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save highlight clip");
        }
    }

    public void Dispose()
    {
        _highlightSub?.Dispose();
        _gameDetectedSub?.Dispose();
        _gameExitedSub?.Dispose();
        _cts?.Dispose();
    }
}
