using ObsKit.NET.Encoders;

namespace WatchDog.Core.Audio;

/// <summary>
/// Immutable mapping between a user-visible 1-based audio track, its zero-based
/// OBS mixer, and its zero-based output track.
/// </summary>
public sealed record AudioTrackRoute
{
    internal AudioTrackRoute(int sourceTrackNumber, int encoderMixerIndex, int outputTrackIndex)
    {
        SourceTrackNumber = sourceTrackNumber;
        EncoderMixerIndex = encoderMixerIndex;
        OutputTrackIndex = outputTrackIndex;
    }

    public int SourceTrackNumber { get; }
    public int EncoderMixerIndex { get; }
    public int OutputTrackIndex { get; }
    public uint SourceMixerMask => 1u << EncoderMixerIndex;
}

/// <summary>Associates an engine-owned encoder with its immutable output route.</summary>
public sealed record AudioTrackEncoderBinding(AudioTrackRoute Route, AudioEncoder Encoder);

/// <summary>
/// Canonical audio routing plan shared by sources and outputs. Disabled sources do
/// not change the plan, so separate-track files retain stable track positions.
/// </summary>
public sealed class AudioTrackPlan
{
    // Reserved exclusively for the silent encoder required by OBS's ffmpeg_muxer when
    // producing an independent webcam file. Product desktop/microphone sources use 0/1.
    internal const int WebcamMuxerCompatibilityMixerIndex = 5;

    private static readonly AudioTrackRoute Track1 = new(1, 0, 0);
    private static readonly AudioTrackRoute Track2 = new(2, 1, 1);
    private static readonly AudioTrackPlan CombinedPlan = new(
        separateAudioTracks: false,
        Track1,
        Track1,
        [Track1]);
    private static readonly AudioTrackPlan SeparatePlan = new(
        separateAudioTracks: true,
        Track1,
        Track2,
        [Track1, Track2]);

    private AudioTrackPlan(
        bool separateAudioTracks,
        AudioTrackRoute desktop,
        AudioTrackRoute microphone,
        AudioTrackRoute[] encoders)
    {
        SeparateAudioTracks = separateAudioTracks;
        Desktop = desktop;
        Microphone = microphone;
        Encoders = Array.AsReadOnly(encoders);
    }

    public bool SeparateAudioTracks { get; }
    public AudioTrackRoute Desktop { get; }
    public AudioTrackRoute Microphone { get; }
    public IReadOnlyList<AudioTrackRoute> Encoders { get; }
    public int EncoderCount => Encoders.Count;

    public static AudioTrackPlan Create(bool separateAudioTracks) =>
        separateAudioTracks ? SeparatePlan : CombinedPlan;
}
