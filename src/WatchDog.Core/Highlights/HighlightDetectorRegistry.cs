using Microsoft.Extensions.Logging;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Highlights.Audio;

namespace WatchDog.Core.Highlights;

public sealed class HighlightDetectorRegistry
{
    private readonly Dictionary<string, IHighlightDetector> _detectors;
    private readonly IHighlightDetector? _audioFallback;
    private readonly IEventBus _eventBus;
    private readonly ILogger<HighlightDetectorRegistry> _logger;
    private IHighlightDetector? _activeDetector;
    private GameInfo? _activeGame;

    /// <summary>Whether the currently active detector is the AI audio fallback.</summary>
    public bool IsAudioFallbackActive => _activeDetector == _audioFallback && _audioFallback is not null;

    /// <summary>Whether a dedicated (non-AI-fallback) detector exists for this game executable.</summary>
    public bool HasDedicatedDetector(string executableName)
        => _detectors.ContainsKey(executableName);

    public HighlightDetectorRegistry(
        IEnumerable<IHighlightDetector> detectors,
        IEventBus eventBus,
        ILogger<HighlightDetectorRegistry> logger)
    {
        _eventBus = eventBus;
        _logger = logger;
        _detectors = new Dictionary<string, IHighlightDetector>(StringComparer.OrdinalIgnoreCase);

        foreach (var detector in detectors)
        {
            // The audio fallback detector uses a sentinel name — store it separately
            if (detector is AudioHighlightDetector audioDetector)
            {
                _audioFallback = audioDetector;
                _logger.LogInformation("Registered AI audio fallback highlight detector");
                continue;
            }

            // NoOp detector is a placeholder when ONNX model is missing — skip entirely
            if (detector is NoOpHighlightDetector)
                continue;

            foreach (var exeName in detector.SupportedExecutableNames)
            {
                _detectors[exeName] = detector;
            }
            _logger.LogInformation("Registered highlight detector for {Game} ({ExeCount} executables)",
                detector.GameExecutableName, detector.SupportedExecutableNames.Count);
        }
    }

    public async Task StartDetectorForGameAsync(GameInfo game, CancellationToken ct = default)
    {
        await StopActiveDetectorAsync(ct);

        if (!_detectors.TryGetValue(game.ExecutableName, out var detector))
        {
            // No dedicated detector — try the AI audio fallback
            if (_audioFallback is not null)
            {
                detector = _audioFallback;
                _logger.LogInformation("No dedicated detector for {Game}, using AI audio fallback",
                    game.DisplayName);
            }
            else
            {
                _logger.LogInformation("No highlight detector available for {Game}", game.DisplayName);
                return;
            }
        }

        _activeDetector = detector;
        _activeGame = game;

        detector.HighlightDetected += OnHighlightDetected;
        await detector.StartAsync(ct);

        _logger.LogInformation("Highlight detector started for {Game}", game.DisplayName);
    }

    public async Task StopActiveDetectorAsync(CancellationToken ct = default)
    {
        if (_activeDetector is null) return;

        _activeDetector.HighlightDetected -= OnHighlightDetected;
        await _activeDetector.StopAsync(ct);

        _logger.LogInformation("Highlight detector stopped for {Game}", _activeGame?.DisplayName);
        _activeDetector = null;
        _activeGame = null;
    }

    private void OnHighlightDetected(HighlightDetectedEventArgs args)
    {
        if (_activeGame is null) return;

        _eventBus.Publish(new HighlightDetectedEvent(
            args.Type,
            _activeGame,
            DateTimeOffset.UtcNow,
            args.Description));

        _logger.LogInformation("Highlight detected: {Type} in {Game} — {Description}",
            args.Type, _activeGame.DisplayName, args.Description ?? "");
    }
}
