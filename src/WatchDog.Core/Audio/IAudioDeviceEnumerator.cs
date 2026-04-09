namespace WatchDog.Core.Audio;

public interface IAudioDeviceEnumerator
{
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();
    IReadOnlyList<AudioDeviceInfo> GetInputDevices();
}
