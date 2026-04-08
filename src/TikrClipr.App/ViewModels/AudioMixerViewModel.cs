using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TikrClipr.Core.Audio;

namespace TikrClipr.App.ViewModels;

public partial class AudioMixerViewModel : ObservableObject
{
    private readonly IAudioMixer _mixer;

    [ObservableProperty] private float _desktopVolume;
    [ObservableProperty] private float _micVolume;
    [ObservableProperty] private bool _isDesktopMuted;
    [ObservableProperty] private bool _isMicMuted;

    public bool IsDesktopUnmuted => !IsDesktopMuted;
    public bool IsMicUnmuted => !IsMicMuted;

    public string DesktopMuteIcon => IsDesktopMuted ? "\U0001F507" : "\U0001F50A";
    public string MicMuteIcon => IsMicMuted ? "\U0001F507" : "\U0001F3A4";

    public string DesktopVolumePercent => $"{(int)(DesktopVolume * 100)}%";
    public string MicVolumePercent => $"{(int)(MicVolume * 100)}%";

    public AudioMixerViewModel(IAudioMixer mixer)
    {
        _mixer = mixer;
        _desktopVolume = mixer.DesktopVolume;
        _micVolume = mixer.MicVolume;
        _isDesktopMuted = mixer.IsDesktopMuted;
        _isMicMuted = mixer.IsMicMuted;
    }

    partial void OnDesktopVolumeChanged(float value)
    {
        _mixer.SetDesktopVolume(value);
        OnPropertyChanged(nameof(DesktopVolumePercent));
    }

    partial void OnMicVolumeChanged(float value)
    {
        _mixer.SetMicVolume(value);
        OnPropertyChanged(nameof(MicVolumePercent));
    }

    partial void OnIsDesktopMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDesktopUnmuted));
        OnPropertyChanged(nameof(DesktopMuteIcon));
    }

    partial void OnIsMicMutedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsMicUnmuted));
        OnPropertyChanged(nameof(MicMuteIcon));
    }

    [RelayCommand]
    private void ToggleDesktopMute()
    {
        _mixer.ToggleDesktopMute();
        IsDesktopMuted = _mixer.IsDesktopMuted;
    }

    [RelayCommand]
    private void ToggleMicMute()
    {
        _mixer.ToggleMicMute();
        IsMicMuted = _mixer.IsMicMuted;
    }
}
