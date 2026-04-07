using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ObsKit.NET.Encoders;
using ObsKit.NET.Outputs;
using TikrClipr.Core.GameDetection;

namespace TikrClipr.Core.Recording;

public sealed class ObsSessionRecorder : ISessionRecorder
{
    private readonly ILogger<ObsSessionRecorder> _logger;
    private readonly SessionRecordingConfig _config;
    private readonly VideoEncoder _videoEncoder;
    private readonly AudioEncoder _audioEncoder;

    private RecordingOutput? _recordingOutput;
    private Timer? _segmentTimer;
    private Timer? _maxDurationTimer;
    private readonly Stopwatch _elapsed = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string _outputDirectory = string.Empty;
    private GameInfo? _currentGame;
    private int _segmentIndex;
    private bool _disposed;

    public bool IsRecording { get; private set; }
    public TimeSpan Elapsed => _elapsed.Elapsed;
    public string? CurrentOutputPath { get; private set; }

    public event Action<string>? SegmentSaved;
    public event Action<string>? Error;

    public ObsSessionRecorder(
        SessionRecordingConfig config,
        VideoEncoder videoEncoder,
        AudioEncoder audioEncoder,
        ILogger<ObsSessionRecorder> logger)
    {
        _config = config;
        _videoEncoder = videoEncoder;
        _audioEncoder = audioEncoder;
        _logger = logger;
    }

    public Task StartAsync(string outputDirectory, GameInfo? game, CancellationToken ct = default)
    {
        if (IsRecording)
        {
            _logger.LogWarning("Session recording already in progress");
            return Task.CompletedTask;
        }

        _outputDirectory = outputDirectory;
        _currentGame = game;
        _segmentIndex = 0;
        _elapsed.Restart();

        Directory.CreateDirectory(outputDirectory);
        if (!StartSegment())
        {
            _elapsed.Stop();
            return Task.CompletedTask;
        }

        // Segment splitting timer
        if (_config.SegmentDurationMinutes > 0)
        {
            _segmentTimer = new Timer(
                _ => SplitSegmentSafe(),
                null,
                TimeSpan.FromMinutes(_config.SegmentDurationMinutes),
                TimeSpan.FromMinutes(_config.SegmentDurationMinutes));
        }

        // Max duration timer (auto-stop)
        if (_config.MaxDurationMinutes > 0)
        {
            _maxDurationTimer = new Timer(
                _ => StopSafe(),
                null,
                TimeSpan.FromMinutes(_config.MaxDurationMinutes),
                Timeout.InfiniteTimeSpan);
        }

        IsRecording = true;
        _logger.LogInformation("Session recording started: {Output}", CurrentOutputPath);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!IsRecording)
            return Task.CompletedTask;

        _segmentTimer?.Dispose();
        _segmentTimer = null;
        _maxDurationTimer?.Dispose();
        _maxDurationTimer = null;

        StopCurrentOutput();

        _elapsed.Stop();
        IsRecording = false;

        _logger.LogInformation("Session recording stopped. Total duration: {Duration}", Elapsed);
        return Task.CompletedTask;
    }

    private bool StartSegment()
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
        var suffix = _segmentIndex > 0 ? $"_part{_segmentIndex + 1}" : "";
        var fileName = $"{timestamp}{suffix}.{_config.FileFormat}";
        CurrentOutputPath = Path.Combine(_outputDirectory, fileName);

        try
        {
            var format = _config.FileFormat.ToLowerInvariant() switch
            {
                "mkv" => RecordingFormat.Mkv,
                "flv" => RecordingFormat.Flv,
                _ => RecordingFormat.Mp4,
            };

            _recordingOutput = new RecordingOutput($"TikrClipr Session {_segmentIndex}")
                .SetPath(CurrentOutputPath)
                .SetFormat(format);
            _recordingOutput.SetVideoEncoder(_videoEncoder);
            _recordingOutput.SetAudioEncoder(_audioEncoder, 0);

            _recordingOutput.Start();

            _logger.LogInformation("Recording segment {Index}: {Path}",
                _segmentIndex, Path.GetFileName(CurrentOutputPath));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start recording segment");
            Error?.Invoke($"Recording segment failed: {ex.Message}");
            return false;
        }
    }

    private async void SplitSegmentSafe()
    {
        try
        {
            await _lock.WaitAsync();
            try { SplitSegment(); }
            finally { _lock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during segment split");
        }
    }

    private async void StopSafe()
    {
        try { await StopAsync(); }
        catch (Exception ex) { _logger.LogError(ex, "Error during auto-stop"); }
    }

    private void SplitSegment()
    {
        _logger.LogInformation("Splitting session recording at segment {Index}", _segmentIndex);

        StopCurrentOutput();
        _segmentIndex++;
        StartSegment();
    }

    private void StopCurrentOutput()
    {
        if (_recordingOutput is null) return;

        try
        {
            _recordingOutput.Stop();
            _recordingOutput.Dispose();

            if (CurrentOutputPath is not null && File.Exists(CurrentOutputPath))
            {
                _logger.LogInformation("Segment saved: {Path}", Path.GetFileName(CurrentOutputPath));
                SegmentSaved?.Invoke(CurrentOutputPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping recording output");
        }
        finally
        {
            _recordingOutput = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRecording)
            StopAsync().GetAwaiter().GetResult();

        _segmentTimer?.Dispose();
        _maxDurationTimer?.Dispose();
    }
}
