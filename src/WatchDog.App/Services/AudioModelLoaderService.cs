using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Highlights;
using WatchDog.Core.Highlights.Audio;

namespace WatchDog.App.Services;

/// <summary>
/// Background service that downloads the YAMNet ONNX model on first launch
/// and activates the AI audio highlight detector without blocking startup.
/// The app starts immediately with a NoOp detector, then upgrades to the
/// real detector once the model is available.
/// </summary>
public sealed class AudioModelLoaderService : IHostedService
{
    private readonly HighlightDetectorRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<AudioModelLoaderService> _logger;

    private static readonly string ModelPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WatchDog", "Models", "yamnet.onnx");

    public AudioModelLoaderService(
        HighlightDetectorRegistry registry,
        ILoggerFactory loggerFactory,
        ILogger<AudioModelLoaderService> logger)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Run in the background — don't block hosted service startup
        _ = Task.Run(() => LoadModelAsync(cancellationToken), cancellationToken)
            .ContinueWith(t => _logger.LogError(t.Exception, "Background model load faulted"),
                TaskContinuationOptions.OnlyOnFaulted);
        return Task.CompletedTask;
    }

    private async Task LoadModelAsync(CancellationToken ct)
    {
        try
        {
            // Download model if missing (no timeout — let it take as long as needed)
            if (!File.Exists(ModelPath))
            {
                _logger.LogInformation("Downloading AI audio model in background...");
                var downloaded = await AudioModelDownloader.EnsureModelAsync(ModelPath, _logger, ct);
                if (!downloaded)
                {
                    _logger.LogWarning("ONNX model download failed — AI audio highlights unavailable");
                    return;
                }
            }

            // Load the classifier
            var classifier = new AudioClassifier(ModelPath,
                _loggerFactory.CreateLogger<AudioClassifier>());

            var detector = new AudioHighlightDetector(classifier,
                _loggerFactory.CreateLogger<AudioHighlightDetector>());

            // Swap into the registry — replaces the NoOp fallback
            _registry.SetAudioFallback(detector);

            _logger.LogInformation("AI audio highlight detector ready");
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Model loading cancelled (app shutting down)");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load AI audio model — highlights unavailable");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
