using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WatchDog.Core.Highlights.Audio;

/// <summary>
/// AI-powered audio highlight detector. Captures system audio via WASAPI loopback,
/// feeds 0.975s windows to a YAMNet ONNX model, and fires highlight events
/// when game-relevant audio patterns (gunfire, explosions, crowd cheers) exceed
/// the confidence threshold.
///
/// Used as a universal fallback for games without dedicated detectors.
/// </summary>
public sealed class AudioHighlightDetector : IHighlightDetector
{
    private readonly AudioClassifier _classifier;
    private readonly ILogger<AudioHighlightDetector> _logger;
    private readonly float _confidenceThreshold;

    private WasapiLoopbackCapture? _capture;
    private CancellationTokenSource? _cts;
    private readonly List<float> _sampleBuffer = [];
    private readonly object _bufferLock = new();

    // Thread-safe debounce via Interlocked on ticks
    private long _lastHighlightTimeTicks = DateTimeOffset.MinValue.UtcTicks;
    private static readonly long DebounceWindowTicks = TimeSpan.FromSeconds(3).Ticks;

    // Back-pressure: only one inference task in-flight at a time
    private int _inferenceInFlight;

    // WASAPI loopback captures at the system's mix format (typically 48kHz stereo float32).
    // We convert to 16kHz mono for YAMNet.
    private int _captureRate;
    private int _captureChannels;

    /// <summary>
    /// Special sentinel: audio detector supports ALL games as a fallback.
    /// The registry checks for this value to use it when no dedicated detector exists.
    /// </summary>
    public string GameExecutableName => "__audio_fallback__";

    public IReadOnlyList<string> SupportedExecutableNames => [GameExecutableName];

    public bool IsRunning { get; private set; }

    public event Action<HighlightDetectedEventArgs>? HighlightDetected;

    public AudioHighlightDetector(
        AudioClassifier classifier,
        ILogger<AudioHighlightDetector> logger,
        float confidenceThreshold = 0.6f)
    {
        _classifier = classifier;
        _logger = logger;
        _confidenceThreshold = confidenceThreshold;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return Task.CompletedTask;

        // Dispose previous CTS if leftover from a prior stop-start cycle
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        try
        {
            _capture = new WasapiLoopbackCapture();

            // Validate WASAPI delivers IEEE float32 — other formats would corrupt classifier input
            if (_capture.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat)
            {
                _logger.LogError(
                    "Unsupported WASAPI format {Encoding} — expected IeeeFloat. " +
                    "Audio highlight detection disabled.", _capture.WaveFormat.Encoding);
                _cts.Dispose();
                _cts = null;
                CleanupCapture();
                return Task.CompletedTask;
            }

            _captureRate = _capture.WaveFormat.SampleRate;
            _captureChannels = _capture.WaveFormat.Channels;

            _logger.LogInformation(
                "Audio highlight detector starting: {Rate}Hz, {Channels}ch, {Bits}bit",
                _captureRate, _captureChannels, _capture.WaveFormat.BitsPerSample);

            _capture.DataAvailable += OnAudioDataAvailable;
            _capture.RecordingStopped += OnRecordingStopped;
            _capture.StartRecording();

            IsRunning = true;
            _logger.LogInformation("Audio highlight detector started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start audio highlight detector");
            _cts?.Dispose();
            _cts = null;
            CleanupCapture();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct = default)
    {
        if (!IsRunning) return Task.CompletedTask;

        IsRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        CleanupCapture();

        lock (_bufferLock)
        {
            _sampleBuffer.Clear();
        }

        _logger.LogInformation("Audio highlight detector stopped");
        return Task.CompletedTask;
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (!IsRunning || _cts?.IsCancellationRequested == true) return;

        // WASAPI loopback in shared mode outputs IEEE float32 (validated in StartAsync)
        var sampleCount = e.BytesRecorded / sizeof(float);
        if (sampleCount == 0) return;

        var samples = new float[sampleCount];
        Buffer.BlockCopy(e.Buffer, 0, samples, 0, e.BytesRecorded);

        lock (_bufferLock)
        {
            _sampleBuffer.AddRange(samples);

            // Calculate how many raw samples we need for one YAMNet window.
            // YAMNet needs 15600 samples at 16kHz mono.
            // At 48kHz stereo, that's 15600 * 3 * 2 = 93600 raw samples.
            var rawSamplesPerWindow = (int)(AudioClassifier.InputWindowSamples
                * ((double)_captureRate / AudioClassifier.InputSampleRate)
                * _captureChannels);

            // Cap buffer size to prevent unbounded growth if inference lags capture
            var maxBufferSamples = rawSamplesPerWindow * 4;
            if (_sampleBuffer.Count > maxBufferSamples)
            {
                var excess = _sampleBuffer.Count - maxBufferSamples;
                _sampleBuffer.RemoveRange(0, excess);
                _logger.LogDebug("Audio buffer overflow — dropped {Excess} samples", excess);
            }

            while (_sampleBuffer.Count >= rawSamplesPerWindow)
            {
                var window = _sampleBuffer.GetRange(0, rawSamplesPerWindow);
                // Advance by half a window for overlapping analysis
                _sampleBuffer.RemoveRange(0, rawSamplesPerWindow / 2);

                // Back-pressure: only one inference task in-flight at a time.
                // If already busy, drop this window — audio is continuous.
                var windowArray = window.ToArray();
                if (Interlocked.CompareExchange(ref _inferenceInFlight, 1, 0) == 0)
                {
                    _ = Task.Run(() =>
                    {
                        try { ProcessAudioWindow(windowArray); }
                        finally { Interlocked.Exchange(ref _inferenceInFlight, 0); }
                    });
                }
            }
        }
    }

    private void ProcessAudioWindow(float[] rawSamples)
    {
        try
        {
            // Convert: any format → mono 16kHz (handles stereo, 5.1, 7.1, any sample rate)
            var yamnetInput = AudioResampler.ConvertForYamnet(rawSamples, _captureRate, _captureChannels);

            // Pad or trim to exactly 15600 samples
            if (yamnetInput.Length < AudioClassifier.InputWindowSamples)
            {
                var padded = new float[AudioClassifier.InputWindowSamples];
                Array.Copy(yamnetInput, padded, yamnetInput.Length);
                yamnetInput = padded;
            }
            else if (yamnetInput.Length > AudioClassifier.InputWindowSamples)
            {
                yamnetInput = yamnetInput[..AudioClassifier.InputWindowSamples];
            }

            // Classify
            var scores = _classifier.Classify(yamnetInput);
            var candidate = AudioEventMapping.Evaluate(scores, _confidenceThreshold);

            if (candidate is null) return;

            // Thread-safe debounce via Interlocked compare-and-swap
            var nowTicks = DateTimeOffset.UtcNow.UtcTicks;
            var lastTicks = Interlocked.Read(ref _lastHighlightTimeTicks);
            if (nowTicks - lastTicks < DebounceWindowTicks) return;
            if (Interlocked.CompareExchange(ref _lastHighlightTimeTicks, nowTicks, lastTicks) != lastTicks)
                return; // Another thread won the race

            _logger.LogDebug("Audio highlight: {Type} ({Confidence:P0}) — {Desc}",
                candidate.Type, candidate.Confidence, candidate.Description);

            HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                candidate.Type,
                $"[AI Audio] {candidate.Description} ({candidate.Confidence:P0})"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Audio classification error (non-fatal)");
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _logger.LogWarning(e.Exception, "WASAPI loopback recording stopped with error");
        }
    }

    private void CleanupCapture()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnAudioDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.StopRecording(); } catch { /* best-effort */ }
            _capture.Dispose();
            _capture = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        IsRunning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        CleanupCapture();
        return ValueTask.CompletedTask;
    }
}
