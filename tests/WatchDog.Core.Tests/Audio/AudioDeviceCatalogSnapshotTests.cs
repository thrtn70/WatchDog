using WatchDog.Core.Audio;

namespace WatchDog.Core.Tests.Audio;

public sealed class AudioDeviceCatalogSnapshotTests
{
    [Fact]
    public void StrictSnapshotRetainsDirectionRoleAndDefaultMetadata()
    {
        var snapshot = AudioDeviceCatalogSnapshot.CreateValidated(
            AudioEndpointDirection.Output,
            WindowsAudioEndpointRole.Multimedia,
            [new AudioDeviceInfo("speaker", "Speakers", IsDefault: false)],
            "speaker");

        Assert.Equal(AudioEndpointDirection.Output, snapshot.Direction);
        Assert.Equal(WindowsAudioEndpointRole.Multimedia, snapshot.WindowsDefaultRole);
        Assert.Equal("speaker", snapshot.WindowsDefaultEndpointId);
        Assert.Equal("speaker", Assert.Single(snapshot.ActiveDevices).DeviceId);
    }

    [Fact]
    public void StrictSnapshotRejectsMalformedDuplicateAndUncataloguedDefaultIds()
    {
        Assert.Throws<InvalidDataException>(() => AudioDeviceCatalogSnapshot.CreateValidated(
            AudioEndpointDirection.Input,
            WindowsAudioEndpointRole.Multimedia,
            [new AudioDeviceInfo("mic", "", IsDefault: false)],
            "mic"));
        Assert.Throws<InvalidDataException>(() => AudioDeviceCatalogSnapshot.CreateValidated(
            AudioEndpointDirection.Input,
            WindowsAudioEndpointRole.Multimedia,
            [
                new AudioDeviceInfo("mic", "One", IsDefault: false),
                new AudioDeviceInfo("mic", "Two", IsDefault: false),
            ],
            "mic"));
        Assert.Throws<InvalidDataException>(() => AudioDeviceCatalogSnapshot.CreateValidated(
            AudioEndpointDirection.Output,
            WindowsAudioEndpointRole.Multimedia,
            [new AudioDeviceInfo("speaker", "Speakers", IsDefault: false)],
            "SPEAKER"));
    }

    [Fact]
    public void MissingWindowsDefaultIsValidObservableMetadata()
    {
        var snapshot = AudioDeviceCatalogSnapshot.CreateValidated(
            AudioEndpointDirection.Output,
            WindowsAudioEndpointRole.Multimedia,
            [new AudioDeviceInfo("speaker", "Speakers", IsDefault: false)],
            windowsDefaultEndpointId: null);

        Assert.Null(snapshot.WindowsDefaultEndpointId);
    }
}
