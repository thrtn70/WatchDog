using Microsoft.Extensions.Logging;
using ObsKit.NET.Sources;

namespace TikrClipr.Core.Audio;

public sealed class ObsAudioMixer : IAudioMixer
{
    private readonly ILogger<ObsAudioMixer> _logger;
    private readonly AudioOutputCapture? _desktopAudio;
    private readonly AudioInputCapture? _micAudio;

    private float _desktopVolume = 1.0f;
    private float _micVolume = 1.0f;
    private bool _isDesktopMuted;
    private bool _isMicMuted;

    public float DesktopVolume => _desktopVolume;
    public float MicVolume => _micVolume;
    public bool IsMicMuted => _isMicMuted;
    public bool IsDesktopMuted => _isDesktopMuted;

    public ObsAudioMixer(
        AudioOutputCapture? desktopAudio,
        AudioInputCapture? micAudio,
        ILogger<ObsAudioMixer> logger)
    {
        _desktopAudio = desktopAudio;
        _micAudio = micAudio;
        _logger = logger;
    }

    public void SetDesktopVolume(float volume)
    {
        _desktopVolume = Math.Clamp(volume, 0f, 1f);
        _desktopAudio?.SetVolume(_desktopVolume);
        _logger.LogDebug("Desktop volume set to {Volume:P0}", _desktopVolume);
    }

    public void SetMicVolume(float volume)
    {
        _micVolume = Math.Clamp(volume, 0f, 1f);
        _micAudio?.SetVolume(_micVolume);
        _logger.LogDebug("Mic volume set to {Volume:P0}", _micVolume);
    }

    public void ToggleDesktopMute()
    {
        _isDesktopMuted = !_isDesktopMuted;
        _desktopAudio?.SetMuted(_isDesktopMuted);
        _logger.LogInformation("Desktop audio {State}", _isDesktopMuted ? "muted" : "unmuted");
    }

    public void ToggleMicMute()
    {
        _isMicMuted = !_isMicMuted;
        _micAudio?.SetMuted(_isMicMuted);
        _logger.LogInformation("Mic audio {State}", _isMicMuted ? "muted" : "unmuted");
    }

    public void ApplyConfig(AudioMixConfig config)
    {
        SetDesktopVolume(config.DesktopVolume);
        SetMicVolume(config.MicVolume);

        if (!config.DesktopAudioEnabled && !_isDesktopMuted)
            ToggleDesktopMute();
        if (!config.MicEnabled && !_isMicMuted)
            ToggleMicMute();
    }
}
