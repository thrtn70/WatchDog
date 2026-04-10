namespace WatchDog.Core.Highlights.Audio;

/// <summary>
/// Lightweight audio resampling and format conversion utilities.
/// Converts stereo 48kHz float32 audio to mono 16kHz float32 for YAMNet.
/// </summary>
public static class AudioResampler
{
    /// <summary>
    /// Downsample audio from a higher sample rate to a lower one using linear interpolation.
    /// Simple but adequate for classification (not production audio processing).
    /// </summary>
    public static float[] Resample(ReadOnlySpan<float> input, int inputRate, int outputRate)
    {
        if (inputRate == outputRate)
            return input.ToArray();

        var ratio = (double)inputRate / outputRate;
        var outputLength = (int)(input.Length / ratio);
        var output = new float[outputLength];

        for (int i = 0; i < outputLength; i++)
        {
            var srcIndex = i * ratio;
            var srcIndexInt = (int)srcIndex;
            var frac = (float)(srcIndex - srcIndexInt);

            if (srcIndexInt + 1 < input.Length)
                output[i] = input[srcIndexInt] * (1 - frac) + input[srcIndexInt + 1] * frac;
            else
                output[i] = input[srcIndexInt];
        }

        return output;
    }

    /// <summary>
    /// Full pipeline: multi-channel audio at any sample rate → mono 16kHz for YAMNet.
    /// </summary>
    public static float[] ConvertForYamnet(ReadOnlySpan<float> samples, int sampleRate, int channels)
    {
        var mono = channels >= 2 ? DownmixToMono(samples, channels) : samples.ToArray();
        return Resample(mono, sampleRate, AudioClassifier.InputSampleRate);
    }

    /// <summary>
    /// Convert interleaved multi-channel samples to mono by averaging all channels.
    /// Works for stereo (2ch), 5.1 (6ch), 7.1 (8ch), etc.
    /// </summary>
    public static float[] DownmixToMono(ReadOnlySpan<float> interleavedSamples, int channels)
    {
        if (channels <= 1) return interleavedSamples.ToArray();

        var monoLength = interleavedSamples.Length / channels;
        var mono = new float[monoLength];
        var scale = 1f / channels;

        for (int i = 0; i < monoLength; i++)
        {
            var sum = 0f;
            for (int ch = 0; ch < channels; ch++)
                sum += interleavedSamples[i * channels + ch];
            mono[i] = sum * scale;
        }

        return mono;
    }

    /// <summary>
    /// Convert stereo (2-channel) interleaved samples to mono. Kept for backwards compatibility.
    /// </summary>
    public static float[] StereoToMono(ReadOnlySpan<float> stereoSamples)
        => DownmixToMono(stereoSamples, 2);
}
