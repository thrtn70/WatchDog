namespace WatchDog.Core.Highlights.Audio;

/// <summary>
/// Lightweight audio resampling and format conversion utilities.
/// Converts stereo 48kHz float32 audio to mono 16kHz float32 for YAMNet.
/// </summary>
public static class AudioResampler
{
    /// <summary>
    /// Convert stereo float32 samples to mono by averaging channels.
    /// Input: interleaved L,R,L,R,... Output: mono samples (half the length).
    /// </summary>
    public static float[] StereoToMono(ReadOnlySpan<float> stereoSamples)
    {
        var monoLength = stereoSamples.Length / 2;
        var mono = new float[monoLength];

        for (int i = 0; i < monoLength; i++)
        {
            mono[i] = (stereoSamples[i * 2] + stereoSamples[i * 2 + 1]) * 0.5f;
        }

        return mono;
    }

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
    /// Full pipeline: stereo 48kHz → mono 16kHz.
    /// </summary>
    public static float[] ConvertForYamnet(ReadOnlySpan<float> stereo48kHz)
    {
        var mono48k = StereoToMono(stereo48kHz);
        return Resample(mono48k, 48000, AudioClassifier.InputSampleRate);
    }
}
