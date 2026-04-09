namespace WatchDog.Core.Audio;

public interface IAudioMixer
{
    float DesktopVolume { get; }
    float MicVolume { get; }
    bool IsMicMuted { get; }
    bool IsDesktopMuted { get; }

    void SetDesktopVolume(float volume);
    void SetMicVolume(float volume);
    void ToggleDesktopMute();
    void ToggleMicMute();
}
