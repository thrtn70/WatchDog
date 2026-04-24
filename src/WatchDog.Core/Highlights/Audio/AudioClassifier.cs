using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;

namespace WatchDog.Core.Highlights.Audio;

/// <summary>
/// Wraps a YAMNet ONNX model for audio event classification.
/// Uses the full-pipeline YAMNet model (converted via tf2onnx) which accepts
/// raw 16kHz mono float32 waveform input and outputs 521 AudioSet class scores.
/// The mel spectrogram computation is baked into the ONNX graph.
/// </summary>
public sealed class AudioClassifier : IDisposable
{
    private readonly InferenceSession _session;
    private readonly ILogger<AudioClassifier> _logger;
    private readonly string _inputName;
    private readonly string[] _outputNames;
    private int _disposedFlag;

    /// <summary>YAMNet input: 0.975 seconds at 16kHz = 15600 samples.</summary>
    public const int InputSampleRate = 16000;
    public const int InputWindowSamples = 15600;
    public static readonly TimeSpan InputWindowDuration = TimeSpan.FromSeconds(0.975);

    /// <summary>Number of AudioSet output classes.</summary>
    public const int NumClasses = 521;

    public AudioClassifier(string modelPath, ILogger<AudioClassifier> logger)
    {
        _logger = logger;

        if (!File.Exists(modelPath))
        {
            logger.LogWarning("ONNX model not found at {Path}", modelPath);
            throw new FileNotFoundException(
                "ONNX audio classification model not found. Reinstalling the application may fix this.",
                "yamnet.onnx");
        }

        using var options = new SessionOptions();
        // Try DirectML (GPU) first, fall back to CPU
        try
        {
            options.AppendExecutionProvider_DML();
            _logger.LogInformation("ONNX using DirectML (GPU) execution provider");
        }
        catch (Exception ex)
        {
            _logger.LogInformation(ex, "DirectML not available, falling back to CPU execution provider");
        }

        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        _session = new InferenceSession(modelPath, options);
        _inputName = _session.InputMetadata.Keys.First();
        _outputNames = _session.OutputMetadata.Keys.ToArray();

        _logger.LogInformation("Audio classifier loaded: {Model}, input={Input}, outputs=[{Outputs}]",
            Path.GetFileName(modelPath), _inputName, string.Join(", ", _outputNames));
    }

    /// <summary>
    /// Classify an audio window. Input must be 15600 float32 samples at 16kHz mono.
    /// Returns array of 521 class scores (higher = more confident).
    /// Uses the OrtValue API for lower GC pressure and better performance.
    /// </summary>
    public float[] Classify(ReadOnlySpan<float> audioSamples)
    {
        if (audioSamples.Length != InputWindowSamples)
            throw new ArgumentException(
                $"Expected {InputWindowSamples} samples, got {audioSamples.Length}",
                nameof(audioSamples));

        // Pin the input array and create an OrtValue referencing it (zero-copy)
        var inputArray = audioSamples.ToArray();
        using var inputOrtValue = OrtValue.CreateTensorValueFromMemory(
            inputArray, [1, InputWindowSamples]);

        var inputs = new Dictionary<string, OrtValue> { { _inputName, inputOrtValue } };

        using var runOptions = new RunOptions();
        using var outputs = _session.Run(runOptions, inputs, _outputNames);

        // First output is scores: shape (N_frames, 521)
        // For a single 0.975s window, N_frames = 1
        var scoresSpan = outputs[0].GetTensorDataAsSpan<float>();

        // If multiple frames, take the first frame's 521 scores
        var result = new float[NumClasses];
        scoresSpan[..NumClasses].CopyTo(result);
        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposedFlag, 1) != 0) return;
        _session.Dispose();
    }
}
