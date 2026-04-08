namespace TikrClipr.Core.Audio;

public sealed record AudioMixConfig
{
    public bool DesktopAudioEnabled { get; init; } = true;
    public float DesktopVolume { get; init; } = 1.0f;       // 0.0 – 1.0
    public bool MicEnabled { get; init; } = true;
    public float MicVolume { get; init; } = 1.0f;           // 0.0 – 1.0
    public bool SeparateAudioTracks { get; init; } = false;  // Mic on track 2
}
