using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Highlights;
using WatchDog.Core.Recording;
using WatchDog.Core.Sessions;
using WatchDog.Core.Storage;

namespace WatchDog.App.Services;

/// <summary>
/// Orchestrates auto-clipping when highlights are detected.
/// Subscribes to HighlightDetectedEvent, waits the post-event delay,
/// then saves the replay buffer and indexes the clip with highlight metadata.
/// </summary>
public sealed class HighlightClipService : IHostedService, IDisposable
{
    private readonly CaptureLifecycleCoordinator _lifecycle;
    private readonly ICompletedMediaIngestion _completedMedia;
    private readonly IEventBus _eventBus;
    private readonly HighlightDetectorRegistry _registry;
    private readonly HighlightConfig _config;
    private readonly SessionRecordingConfig _recordingConfig;
    private readonly ILogger<HighlightClipService> _logger;

    private IDisposable? _highlightSub;
    private IDisposable? _gameDetectedSub;
    private IDisposable? _gameExitedSub;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly SemaphoreSlim _detectorReconcileGate = new(1, 1);
    private readonly object _detectorStateSync = new();
    private readonly object _pendingHighlightSync = new();
    private readonly HashSet<Task> _pendingHighlightTasks = [];
    private bool _stopping;
    private CancellationTokenSource? _cts;
    private DetectorRequest? _desiredDetector;
    private CaptureSessionContext? _activeDetectorContext;
    private DateTimeOffset _lastSaveTime = DateTimeOffset.MinValue;

    public HighlightClipService(
        CaptureLifecycleCoordinator lifecycle,
        ICompletedMediaIngestion completedMedia,
        IEventBus eventBus,
        HighlightDetectorRegistry registry,
        HighlightConfig config,
        SessionRecordingConfig recordingConfig,
        ILogger<HighlightClipService> logger)
    {
        _lifecycle = lifecycle;
        _completedMedia = completedMedia;
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
        lock (_pendingHighlightSync)
            _stopping = false;

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
        _highlightSub?.Dispose();
        _gameDetectedSub?.Dispose();
        _gameExitedSub?.Dispose();
        lock (_pendingHighlightSync)
            _stopping = true;
        _cts?.Cancel();
        lock (_detectorStateSync)
            _desiredDetector = null;
        await ReconcileDetectorAsync();
        Task[] pending;
        lock (_pendingHighlightSync)
            pending = [.. _pendingHighlightTasks];
        if (pending.Length > 0)
            await Task.WhenAll(pending).WaitAsync(cancellationToken);
    }

    private void OnGameDetected(GameDetectedEvent e)
    {
        if (e.Context?.Request.StartPolicy is { Recording.IsHighlightModeEnabled: false })
            return;
        if (e.Context is null)
        {
            _logger.LogWarning("Ignoring unowned game-detected event for {Game}", e.Game.DisplayName);
            return;
        }

        lock (_detectorStateSync)
        {
            var latestGeneration = Math.Max(
                _desiredDetector?.Context.Generation ?? 0,
                _activeDetectorContext?.Generation ?? 0);
            if (e.Context.Generation >= latestGeneration)
                _desiredDetector = new DetectorRequest(e.Context, e.Game);
        }

        _ = ReconcileDetectorAsync();
    }

    private void OnGameExited(GameExitedEvent e)
    {
        if (e.Context is null)
            return;

        lock (_detectorStateSync)
        {
            if (_desiredDetector?.Context == e.Context)
                _desiredDetector = null;
        }

        _ = ReconcileDetectorAsync();
    }

    private async Task ReconcileDetectorAsync()
    {
        await _detectorReconcileGate.WaitAsync();
        try
        {
            while (true)
            {
                DetectorRequest? desired;
                CaptureSessionContext? active;
                lock (_detectorStateSync)
                {
                    desired = _desiredDetector;
                    active = _activeDetectorContext;
                }

                if (active == desired?.Context)
                    return;

                if (active is not null)
                {
                    await RotateHighlightCancellationAsync();
                    try
                    {
                        await _registry.StopActiveDetectorAsync();
                    }
                    finally
                    {
                        lock (_detectorStateSync)
                        {
                            if (_activeDetectorContext == active)
                                _activeDetectorContext = null;
                        }
                    }
                    continue;
                }

                if (desired is null)
                    return;

                await _registry.StartDetectorForGameAsync(
                    desired.Game,
                    desired.Context,
                    _cts?.Token ?? default);
                lock (_detectorStateSync)
                    _activeDetectorContext = desired.Context;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile highlight detector ownership");
        }
        finally
        {
            _detectorReconcileGate.Release();
        }
    }

    private async Task RotateHighlightCancellationAsync()
    {
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
    }

    private void OnHighlightDetected(HighlightDetectedEvent e)
    {
        if (e.Context?.Request.StartPolicy is { Recording.IsHighlightModeEnabled: false })
            return;
        lock (_pendingHighlightSync)
        {
            if (_stopping) return;
            var task = HandleHighlightDetectedAsync(e);
            _pendingHighlightTasks.Add(task);
            _ = ObserveHighlightTaskAsync(task);
        }
    }

    private async Task ObserveHighlightTaskAsync(Task task)
    {
        try { await task; }
        finally
        {
            lock (_pendingHighlightSync)
                _pendingHighlightTasks.Remove(task);
        }
    }

    private async Task HandleHighlightDetectedAsync(HighlightDetectedEvent e)
    {
        try
        {
            // MatchStarted is a session signal, not a clip-worthy event
            if (e.Type == HighlightType.MatchStarted)
                return;

            // Check if this type is enabled
            if (!_config.EnabledTypes.Contains(e.Type))
            {
                _logger.LogDebug("Ignoring highlight type {Type} (not in enabled set)", e.Type);
                return;
            }

            var detectedSnapshot = _lifecycle.Snapshot;
            var detectedContext = detectedSnapshot.Context;
            if (detectedSnapshot.Status != CaptureLifecycleStatus.Active ||
                detectedContext?.RequestedTarget is not GameCaptureTarget detectedGame ||
                e.Context != detectedContext ||
                !GameProcessIdentity.Matches(e.Game, detectedGame))
            {
                _logger.LogWarning(
                    "Ignoring highlight for {Game} PID {Pid}; it does not match the active game capture context",
                    e.Game.DisplayName,
                    e.Game.ProcessId);
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
            var snapshot = _lifecycle.Snapshot;
            if (snapshot.Status != CaptureLifecycleStatus.Active ||
                snapshot.Context is null ||
                snapshot.Context != detectedContext)
            {
                _logger.LogWarning("Cannot save highlight — its capture context is no longer active");
                return;
            }

            var saveResult = await _lifecycle.SaveAsync(detectedContext, ct);
            if (saveResult.OutputPath is null || saveResult.Receipt is null)
            {
                _logger.LogWarning(
                    "Highlight save failed: {Code} — {Message}",
                    saveResult.Failure?.Code,
                    saveResult.Failure?.Message);
                return;
            }
            var path = saveResult.OutputPath;

            // Enrich the exact receipt durably accepted by the save backend. Reconstructing
            // session/match data here would race later session state.
            var enrichment = await _completedMedia.EnrichAsync(
                saveResult.Receipt,
                e.Type,
                ct);
            if (!enrichment.Indexed)
            {
                _logger.LogWarning(
                    "Highlight metadata for {Path} is pending retry: {Failure}",
                    Path.GetFileName(path),
                    enrichment.FailureMessage);
                return;
            }
            if (!saveResult.Saved)
            {
                _logger.LogWarning(
                    "Highlight media was retained and classified, but its original save remains degraded: {Code} — {Message}",
                    saveResult.Failure?.Code,
                    saveResult.Failure?.Message);
                return;
            }
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
        _saveLock.Dispose();
        _detectorReconcileGate.Dispose();
    }

    private sealed record DetectorRequest(CaptureSessionContext Context, GameInfo Game);
}
