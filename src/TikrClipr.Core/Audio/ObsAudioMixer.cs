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
        if (_desktopAudio is not null)
            _desktopAudio.Volume = _desktopVolume;
        _logger.LogDebug("Desktop volume set to {Volume:P0}", _desktopVolume);
    }

    public void SetMicVolume(float volume)
    {
        _micVolume = Math.Clamp(volume, 0f, 1f);
        if (_micAudio is not null)
            _micAudio.Volume = _micVolume;
        _logger.LogDebug("Mic volume set to {Volume:P0}", _micVolume);
    }

    public void ToggleDesktopMute()
    {
        var newState = !_isDesktopMuted;
        try
        {
            if (_desktopAudio is not null)
                _desktopAudio.IsMuted = newState;
            _isDesktopMuted = newState;
            _logger.LogInformation("Desktop audio {State}", _isDesktopMuted ? "muted" : "unmuted");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle desktop mute");
        }
    }

    public void ToggleMicMute()
    {
        var newState = !_isMicMuted;
        try
        {
            if (_micAudio is not null)
                _micAudio.IsMuted = newState;
            _isMicMuted = newState;
            _logger.LogInformation("Mic audio {State}", _isMicMuted ? "muted" : "unmuted");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to toggle mic mute");
        }
    }

    public void ApplyConfig(AudioMixConfig config)
    {
        SetDesktopVolume(config.DesktopVolume);
        SetMicVolume(config.MicVolume);

        // Sync mute state to match config
        if (!config.DesktopAudioEnabled && !_isDesktopMuted)
            ToggleDesktopMute();
        else if (config.DesktopAudioEnabled && _isDesktopMuted)
            ToggleDesktopMute();

        if (!config.MicEnabled && !_isMicMuted)
            ToggleMicMute();
        else if (config.MicEnabled && _isMicMuted)
            ToggleMicMute();
    }
}
