using WatchDog.Core.Audio;
using WatchDog.Core.Devices;

namespace WatchDog.Core.Tests.Devices;

public sealed class CaptureDeviceManifestTests
{
    [Fact]
    public void ConstructorDefensivelyCopiesEntriesAndRejectsDuplicateRoles()
    {
        var entries = new List<AdmittedCaptureDevice>
        {
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.DesktopAudio, "speaker-id"),
        };
        var manifest = new CaptureDeviceManifest(entries);

        entries.Clear();

        Assert.Equal("speaker-id", Assert.Single(manifest.Entries).RequestedDeviceId);
        Assert.Throws<ArgumentException>(() => new CaptureDeviceManifest(
        [
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-a"),
            AdmittedCaptureDevice.Exact(CaptureDeviceRole.Microphone, "mic-b"),
        ]));
    }

    [Fact]
    public void SelectionModeRequiresEitherAnExactIdOrTheCanonicalDefaultAlias()
    {
        Assert.Equal(
            AudioDeviceInfo.SystemDefaultDeviceId,
            AdmittedCaptureDevice.SystemDefault(CaptureDeviceRole.DesktopAudio).RequestedDeviceId);
        Assert.Throws<ArgumentException>(() => new AdmittedCaptureDevice(
            CaptureDeviceRole.Microphone,
            CaptureDeviceSelectionMode.Exact,
            AudioDeviceInfo.SystemDefaultDeviceId));
        Assert.Throws<ArgumentException>(() => new AdmittedCaptureDevice(
            CaptureDeviceRole.Microphone,
            CaptureDeviceSelectionMode.SystemDefault,
            "not-default"));
    }
}
