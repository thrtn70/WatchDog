namespace WatchDog.Core.Audio;

public interface IAudioDeviceEnumerator
{
    /// <summary>Lists OBS desktop/output capture endpoints without creating capture sources.</summary>
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    /// <summary>Lists OBS microphone/input capture endpoints without creating capture sources.</summary>
    IReadOnlyList<AudioDeviceInfo> GetInputDevices();

    /// <summary>Strict output catalog for explicit-ID capture admission.</summary>
    IReadOnlyList<AudioDeviceInfo> GetOutputDevicesOrThrow();

    /// <summary>Strict input catalog for explicit-ID capture admission.</summary>
    IReadOnlyList<AudioDeviceInfo> GetInputDevicesOrThrow();

    /// <summary>Strict output metadata, including the explicitly named Windows default role.</summary>
    AudioDeviceCatalogSnapshot GetOutputSnapshotOrThrow() =>
        AudioDeviceCatalogSnapshot.CreateValidated(
            AudioEndpointDirection.Output,
            WindowsAudioEndpointRole.Multimedia,
            GetOutputDevicesOrThrow().Where(device => !device.IsDefault),
            windowsDefaultEndpointId: null);

    /// <summary>Strict input metadata, including the explicitly named Windows default role.</summary>
    AudioDeviceCatalogSnapshot GetInputSnapshotOrThrow() =>
        AudioDeviceCatalogSnapshot.CreateValidated(
            AudioEndpointDirection.Input,
            WindowsAudioEndpointRole.Multimedia,
            GetInputDevicesOrThrow().Where(device => !device.IsDefault),
            windowsDefaultEndpointId: null);
}
