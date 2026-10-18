using WatchDog.Core.Capture;
using WatchDog.Core.Settings;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Tests.Webcam;

public sealed class WebcamFoundationTests
{
    private const string ExactDeviceId =
        @"EMEET SmartCam C60E 4K:\\?\usb#22vid_328f&pid_00a5&mi_00#22fixture#22{65e8773d-8f56-11d0-a3b9-00a0c9223196}\global";

    [Fact]
    public void ConfigDefaultsDisabledAtBoundedMode()
    {
        var config = new AppSettings().Webcam;

        Assert.False(config.EnabledForExplicitManualRecording);
        Assert.Equal(string.Empty, config.DeviceId);
        Assert.Equal(string.Empty, config.DisplayName);
        Assert.Equal(1280, config.Width);
        Assert.Equal(720, config.Height);
        Assert.Equal(30, config.FramesPerSecond);
        Assert.Equal("MJPEG", config.VideoFormat);
        Assert.Equal(1_500, config.VideoBitrateKbps);
    }

    [Fact]
    public void MetadataEnumeratorPreservesOpaqueIdentityWithoutOpeningOrFallback()
    {
        var enumerator = new WindowsWebcamDeviceEnumerator(() =>
            [new WebcamDeviceInfo(ExactDeviceId, "EMEET SmartCam C60E 4K")]);

        var device = Assert.Single(enumerator.GetPhysicalDevicesOrThrow());

        Assert.Equal(ExactDeviceId, device.DeviceId);
        Assert.Equal("EMEET SmartCam C60E 4K", device.DisplayName);
    }

    [Fact]
    public void DuplicateOpaqueDeviceIdsAreRejected()
    {
        var enumerator = new WindowsWebcamDeviceEnumerator(() =>
        [
            new WebcamDeviceInfo(ExactDeviceId, "EMEET A"),
            new WebcamDeviceInfo(ExactDeviceId, "EMEET B"),
        ]);

        Assert.Throws<InvalidDataException>(enumerator.GetPhysicalDevicesOrThrow);
    }

    [Theory]
    [InlineData("", "Camera")]
    [InlineData("device", "")]
    [InlineData(" ", "Camera")]
    [InlineData("device", " ")]
    public void MalformedCatalogMetadataIsRejected(string deviceId, string displayName)
    {
        var enumerator = new WindowsWebcamDeviceEnumerator(() =>
            [new WebcamDeviceInfo(deviceId, displayName)]);

        Assert.Throws<InvalidDataException>(enumerator.GetPhysicalDevicesOrThrow);
    }

    [Fact]
    public void ExactSelectionNeverFallsBackToFriendlyNameOrCaseInsensitiveId()
    {
        var devices = new[]
        {
            new WebcamDeviceInfo(ExactDeviceId, "EMEET SmartCam C60E 4K"),
            new WebcamDeviceInfo("different-device", "EMEET SmartCam C60E 4K"),
        };

        Assert.Same(devices[0], WebcamDeviceCatalog.RequireExactDevice(ExactDeviceId, devices));
        Assert.Throws<InvalidOperationException>(() =>
            WebcamDeviceCatalog.RequireExactDevice(ExactDeviceId.ToUpperInvariant(), devices));
        Assert.Throws<InvalidOperationException>(() =>
            WebcamDeviceCatalog.RequireExactDevice("EMEET SmartCam C60E 4K", devices));
    }

    [Fact]
    public void ManualRequestFreezesExactEnabledWebcamPolicy()
    {
        var config = EnabledConfig();
        var selection = WebcamCaptureSelection.FromConfig(
            config,
            new WebcamDeviceInfo(config.DeviceId, config.DisplayName));
        var target = new DisplayCaptureTarget("display-1");

        var request = new CaptureRequest(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            CaptureInitiation.Manual,
            target,
            selection);

        Assert.Same(selection, request.Webcam);
        Assert.Equal(WebcamCaptureAuthorizationScope.ExplicitManualRecordingOnly, selection.AuthorizationScope);
        Assert.Equal(ExactDeviceId, selection.DeviceId);
        Assert.Equal((1280, 720, 30d, "MJPEG", 1_500),
            (selection.Width, selection.Height, selection.FramesPerSecond, selection.VideoFormat, selection.VideoBitrateKbps));
    }

    [Theory]
    [InlineData(CaptureInitiation.ConfiguredStartup)]
    [InlineData(CaptureInitiation.Automatic)]
    public void NonManualRequestCannotCarryWebcamAuthorization(CaptureInitiation initiation)
    {
        var config = EnabledConfig();
        var selection = WebcamCaptureSelection.FromConfig(
            config,
            new WebcamDeviceInfo(config.DeviceId, config.DisplayName));

        Assert.Throws<ArgumentException>(() => new CaptureRequest(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            initiation,
            new DisplayCaptureTarget("display-1"),
            selection));
    }

    [Fact]
    public void DisabledPreferenceCannotCreateSelection()
    {
        var config = EnabledConfig() with { EnabledForExplicitManualRecording = false };

        Assert.Throws<InvalidOperationException>(() => WebcamCaptureSelection.FromConfig(
            config,
            new WebcamDeviceInfo(config.DeviceId, config.DisplayName)));
    }

    [Fact]
    public void ChangedFriendlyNameRemainsPresentationMetadataForExactIdentity()
    {
        var config = EnabledConfig();

        var selection = WebcamCaptureSelection.FromConfig(
            config,
            new WebcamDeviceInfo(config.DeviceId, "Renamed camera"));

        Assert.Equal(config.DeviceId, selection.DeviceId);
        Assert.Equal("Renamed camera", selection.DisplayName);
    }

    private static WebcamConfig EnabledConfig() => new()
    {
        EnabledForExplicitManualRecording = true,
        DeviceId = ExactDeviceId,
        DisplayName = "EMEET SmartCam C60E 4K",
    };
}
