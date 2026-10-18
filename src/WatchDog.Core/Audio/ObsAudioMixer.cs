using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Audio;

public sealed class ObsAudioMixer : IAudioMixer
{
    private readonly ILogger<ObsAudioMixer> _logger;
    private readonly Action<float, bool, float, bool> _applyAudioState;
    private readonly object _sync = new();

    private float _desktopVolume = 1.0f;
    private float _micVolume = 1.0f;
    private bool _isDesktopMuted;
    private bool _isMicMuted;

    public float DesktopVolume { get { lock (_sync) return _desktopVolume; } }
    public float MicVolume { get { lock (_sync) return _micVolume; } }
    public bool IsMicMuted { get { lock (_sync) return _isMicMuted; } }
    public bool IsDesktopMuted { get { lock (_sync) return _isDesktopMuted; } }

    public ObsAudioMixer(
        Action<float, bool, float, bool> applyAudioState,
        ILogger<ObsAudioMixer> logger)
    {
        _applyAudioState = applyAudioState;
        _logger = logger;
    }

    public void SetDesktopVolume(float volume)
    {
        lock (_sync)
        {
            _desktopVolume = Math.Clamp(volume, 0f, 1f);
            ApplyStateLocked();
            _logger.LogDebug("Desktop volume set to {Volume:P0}", _desktopVolume);
        }
    }

    public void SetMicVolume(float volume)
    {
        lock (_sync)
        {
            _micVolume = Math.Clamp(volume, 0f, 1f);
            ApplyStateLocked();
            _logger.LogDebug("Mic volume set to {Volume:P0}", _micVolume);
        }
    }

    public void ToggleDesktopMute()
    {
        lock (_sync)
        {
            _isDesktopMuted = !_isDesktopMuted;
            ApplyStateLocked();
            _logger.LogInformation("Desktop audio {State}", _isDesktopMuted ? "muted" : "unmuted");
        }
    }

    public void ToggleMicMute()
    {
        lock (_sync)
        {
            _isMicMuted = !_isMicMuted;
            ApplyStateLocked();
            _logger.LogInformation("Mic audio {State}", _isMicMuted ? "muted" : "unmuted");
        }
    }

    public void ApplyConfig(AudioMixConfig config)
    {
        lock (_sync)
        {
            _desktopVolume = Math.Clamp(config.DesktopVolume, 0f, 1f);
            _micVolume = Math.Clamp(config.MicVolume, 0f, 1f);
            _isDesktopMuted = !config.DesktopAudioEnabled;
            _isMicMuted = !config.MicEnabled;
            ApplyStateLocked();
        }
    }

    /// <summary>Applies retained mixer state to the engine's current OBS audio sources.</summary>
    public void ApplyToCurrentSources()
    {
        lock (_sync)
            ApplyStateLocked();
    }

    private void ApplyStateLocked()
    {
        try
        {
            _applyAudioState(_desktopVolume, _isDesktopMuted, _micVolume, _isMicMuted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to apply audio mixer state");
        }
    }
}
