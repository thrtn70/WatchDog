using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Audio;

namespace WatchDog.Core.Tests.Audio;

public sealed class WindowsAudioDeviceEnumeratorTests
{
    [Fact]
    public void KeepsOutputAndInputCatalogsDirectionallySeparated()
    {
        var requestedDirections = new List<AudioEndpointDirection>();
        var enumerator = new WindowsAudioDeviceEnumerator(
            direction =>
            {
                requestedDirections.Add(direction);
                return direction == AudioEndpointDirection.Output
                    ? [new AudioDeviceInfo("speaker", "Speakers", IsDefault: false)]
                    : [new AudioDeviceInfo("microphone", "Microphone", IsDefault: false)];
            },
            NullLogger<WindowsAudioDeviceEnumerator>.Instance);

        var outputs = enumerator.GetOutputDevices();
        var inputs = enumerator.GetInputDevices();

        Assert.Contains(outputs, device => device.DeviceId == "speaker");
        Assert.DoesNotContain(outputs, device => device.DeviceId == "microphone");
        Assert.Contains(inputs, device => device.DeviceId == "microphone");
        Assert.DoesNotContain(inputs, device => device.DeviceId == "speaker");
        Assert.Equal(
            new[] { AudioEndpointDirection.Output, AudioEndpointDirection.Input },
            requestedDirections);
    }

    [Fact]
    public void CatalogFailureDoesNotInventAnEndpointId()
    {
        var enumerator = new WindowsAudioDeviceEnumerator(
            _ => throw new InvalidOperationException("catalog unavailable"),
            NullLogger<WindowsAudioDeviceEnumerator>.Instance);

        var device = Assert.Single(enumerator.GetOutputDevices());

        Assert.Equal(AudioDeviceInfo.SystemDefaultDeviceId, device.DeviceId);
        Assert.True(device.IsDefault);
    }

    [Fact]
    public void StrictCatalogPropagatesEnumerationFailure()
    {
        var enumerator = new WindowsAudioDeviceEnumerator(
            _ => throw new InvalidOperationException("catalog unavailable"),
            NullLogger<WindowsAudioDeviceEnumerator>.Instance);

        Assert.Throws<InvalidOperationException>(enumerator.GetOutputDevicesOrThrow);
        Assert.Throws<InvalidOperationException>(enumerator.GetInputDevicesOrThrow);
    }

    [Fact]
    public void StrictCatalogRejectsMalformedAndDuplicateEndpointIdsBeforeNormalization()
    {
        var malformed = new WindowsAudioDeviceEnumerator(
            _ => [new AudioDeviceInfo(" ", "Broken", IsDefault: false)],
            NullLogger<WindowsAudioDeviceEnumerator>.Instance);
        var duplicate = new WindowsAudioDeviceEnumerator(
            _ =>
            [
                new AudioDeviceInfo("opaque-id", "First", IsDefault: false),
                new AudioDeviceInfo("opaque-id", "Second", IsDefault: false),
            ],
            NullLogger<WindowsAudioDeviceEnumerator>.Instance);

        Assert.Throws<InvalidDataException>(malformed.GetOutputDevicesOrThrow);
        Assert.Throws<InvalidDataException>(duplicate.GetInputDevicesOrThrow);
        Assert.True(Assert.Single(malformed.GetOutputDevices()).IsDefault);
        var forgivingDuplicate = duplicate.GetInputDevices();
        Assert.Contains(forgivingDuplicate, device => device.IsDefault);
        Assert.Single(forgivingDuplicate, device => device.DeviceId == "opaque-id");
    }

    [Fact]
    public void StrictCatalogMethodsAreExposedByTheInterface()
    {
        IAudioDeviceEnumerator enumerator = new WindowsAudioDeviceEnumerator(
            direction => direction == AudioEndpointDirection.Output
                ? [new AudioDeviceInfo("speaker", "Speakers", IsDefault: false)]
                : [new AudioDeviceInfo("microphone", "Microphone", IsDefault: false)],
            NullLogger<WindowsAudioDeviceEnumerator>.Instance);

        Assert.Contains(enumerator.GetOutputDevicesOrThrow(), device => device.DeviceId == "speaker");
        Assert.Contains(enumerator.GetInputDevicesOrThrow(), device => device.DeviceId == "microphone");
    }

    [Fact]
    public void StrictSnapshotsPairActiveCatalogWithExplicitMultimediaDefaultRole()
    {
        var defaultReads = new List<(AudioEndpointDirection Direction, WindowsAudioEndpointRole Role)>();
        IAudioDeviceEnumerator enumerator = new WindowsAudioDeviceEnumerator(
            direction => direction == AudioEndpointDirection.Output
                ? [new AudioDeviceInfo("speaker", "Speakers", IsDefault: false)]
                : [new AudioDeviceInfo("microphone", "Microphone", IsDefault: false)],
            (direction, role) =>
            {
                defaultReads.Add((direction, role));
                return direction == AudioEndpointDirection.Output ? "speaker" : "microphone";
            },
            NullLogger<WindowsAudioDeviceEnumerator>.Instance);

        var output = enumerator.GetOutputSnapshotOrThrow();
        var input = enumerator.GetInputSnapshotOrThrow();

        Assert.Equal("speaker", output.WindowsDefaultEndpointId);
        Assert.Equal("microphone", input.WindowsDefaultEndpointId);
        Assert.Equal(
        [
            (AudioEndpointDirection.Output, WindowsAudioEndpointRole.Multimedia),
            (AudioEndpointDirection.Input, WindowsAudioEndpointRole.Multimedia),
        ],
            defaultReads);
    }

    [Fact]
    public void StrictSnapshotRejectsDefaultIdentityMissingFromActiveCatalog()
    {
        var enumerator = new WindowsAudioDeviceEnumerator(
            _ => [new AudioDeviceInfo("speaker", "Speakers", IsDefault: false)],
            (_, _) => "other-speaker",
            NullLogger<WindowsAudioDeviceEnumerator>.Instance);

        Assert.Throws<InvalidDataException>(enumerator.GetOutputSnapshotOrThrow);
    }
}
