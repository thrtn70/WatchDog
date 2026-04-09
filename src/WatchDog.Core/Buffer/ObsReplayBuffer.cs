using Microsoft.Extensions.Logging;
using ObsKit.NET.Encoders;
using ObsKit.NET.Outputs;
using ObsKit.NET.Signals;

namespace WatchDog.Core.Buffer;

public sealed class ObsReplayBuffer : IReplayBuffer
{
    private readonly ILogger<ObsReplayBuffer> _logger;
    private ReplayBuffer? _buffer;
    private SignalConnection? _savedSignal;
    private bool _disposed;

    public bool IsActive => _buffer?.IsActive ?? false;
    public BufferConfig Config { get; }

    public event Action<string>? Saved;
    public event Action<string>? Error;

    public ObsReplayBuffer(BufferConfig config, ILogger<ObsReplayBuffer> logger)
    {
        Config = config;
        _logger = logger;
    }

    public void Initialize(VideoEncoder videoEncoder, AudioEncoder audioEncoder)
    {
        _buffer = new ReplayBuffer(
            name: "WatchDog Replay",
            maxSeconds: Config.MaxSeconds,
            maxSizeMb: Config.MaxSizeMb);

        _buffer
            .SetDirectory(Config.OutputDirectory)
            .SetFilenameFormat(Config.FilenameFormat)
            .WithVideoEncoder(videoEncoder)
            .WithAudioEncoder(audioEncoder, track: 0);

        // Connect save-completed signal
        _savedSignal = _buffer.ConnectSignal(OutputSignal.Saved, _ =>
        {
            var path = _buffer.GetLastReplayPath();
            if (path is not null)
            {
                _logger.LogInformation("Replay saved to {Path}", path);
                Saved?.Invoke(path);
            }
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

        _buffer.Save();

        // Poll for the saved path (ObsKit.NET fires the signal async)
        for (var i = 0; i < 50 && !ct.IsCancellationRequested; i++)
        {
            await Task.Delay(100, ct);
            var path = _buffer.GetLastReplayPath();
            if (path is not null)
                return path;
        }

        _logger.LogWarning("Replay save timed out after 5 seconds");
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _savedSignal?.Dispose();
        _buffer?.Dispose();
    }
}
