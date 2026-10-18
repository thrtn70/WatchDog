using Microsoft.Extensions.Logging;
using ObsKit.NET.Encoders;
using ObsKit.NET.Outputs;
using ObsKit.NET.Signals;
using WatchDog.Core.Audio;

namespace WatchDog.Core.Buffer;

public sealed class ObsReplayBuffer : IReplayBuffer
{
    private readonly ILogger<ObsReplayBuffer> _logger;
    private ReplayBuffer? _buffer;
    private SignalConnection? _savedSignal;
    private readonly ReplaySaveCoordinator _saves;
    private bool _disposed;

    public bool IsActive => _buffer?.IsActive ?? false;
    public BufferConfig Config { get; }

    public event Action<string>? Saved;
    public event Action<string>? Error;

    public ObsReplayBuffer(BufferConfig config, ILogger<ObsReplayBuffer> logger)
    {
        Config = config;
        _logger = logger;
        _saves = new ReplaySaveCoordinator(path =>
        {
            var root = Path.GetFullPath(Config.OutputDirectory).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var file = new FileInfo(path);
            return file.FullName.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && file.Exists && file.Length > 0;
        });
    }

    public void Initialize(
        VideoEncoder videoEncoder,
        IReadOnlyList<AudioTrackEncoderBinding> audioEncoders)
    {
        if (audioEncoders.Count == 0)
            throw new ArgumentException("At least one audio encoder binding is required.", nameof(audioEncoders));

        _buffer = new ReplayBuffer(
            name: "WatchDog Replay",
            maxSeconds: Config.MaxSeconds,
            maxSizeMb: Config.MaxSizeMb);

        _buffer
            .SetDirectory(Config.OutputDirectory)
            .SetFilenameFormat(Config.FilenameFormat)
            .WithVideoEncoder(videoEncoder, takeOwnership: false);

        foreach (var binding in audioEncoders)
        {
            _buffer.WithAudioEncoder(
                binding.Encoder,
                takeOwnership: false,
                track: binding.Route.OutputTrackIndex);
        }

        // Connect save-completed signal
        _savedSignal = _buffer.ConnectSignal(OutputSignal.Saved, _ =>
        {
            _saves.Complete(_buffer?.GetLastReplayPath());
        });

        _logger.LogInformation("Replay buffer initialized: {MaxSeconds}s, {MaxSizeMb}MB",
            Config.MaxSeconds, Config.MaxSizeMb);
    }

    public bool Start()
    {
        if (_buffer is null)
        {
            Error?.Invoke("Replay buffer not initialized. Call Initialize() first.");
            return false;
        }

        var started = _buffer.Start();
        if (!started)
        {
            var error = _buffer.LastError ?? "Unknown error starting replay buffer";
            _logger.LogError("Failed to start replay buffer: {Error}", error);
            Error?.Invoke(error);
        }
        else
        {
            _logger.LogInformation("Replay buffer started");
        }

        return started;
    }

    public bool Stop()
    {
        if (_buffer is null || !_buffer.IsActive)
            return true;

        var stopped = _buffer.Stop(waitForCompletion: true, timeoutMs: 10_000);
        if (!stopped)
        {
            _logger.LogWarning("Replay buffer stop timed out, forcing stop");
            _buffer.ForceStop();
        }

        _logger.LogInformation("Replay buffer stopped");
        return true;
    }

    public async Task<string?> SaveAsync(CancellationToken ct = default)
    {
        if (_buffer is null || !_buffer.IsActive)
        {
            Error?.Invoke("Cannot save: replay buffer is not active.");
            return null;
        }

        try
        {
            var path = await _saves.SaveAsync(() => _buffer.Save(), ct).ConfigureAwait(false);
            _logger.LogInformation("Replay completed and file verified: {Path}", path);
            Saved?.Invoke(path);
            return path;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Replay save failed");
            Error?.Invoke(ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Stop();
        if (_savedSignal is not null)
        {
            _savedSignal.Dispose();
            _savedSignal = null;
        }
        if (_buffer is not null)
        {
            _buffer.Dispose();
            _buffer = null;
        }
        _saves.Dispose();
        _disposed = true;
    }
}
