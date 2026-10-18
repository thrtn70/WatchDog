using WatchDog.CaptureMetadata;

namespace WatchDog.Core.Tests.Metadata;

public sealed class CaptureDeviceMetadataTests
{
    [Fact]
    public void ExactSelectionPreservesOpaqueOrdinalIdentityWithoutSentinelInference()
    {
        var upper = CaptureDeviceSelection.Exact(CaptureDeviceRole.Microphone, "Device-A");
        var lower = CaptureDeviceSelection.Exact(CaptureDeviceRole.Microphone, "device-a");
        var sentinelLooking = CaptureDeviceSelection.Exact(CaptureDeviceRole.DesktopAudio, "default");

        Assert.Equal("Device-A", upper.OpaqueDeviceId);
        Assert.Equal("device-a", lower.OpaqueDeviceId);
        Assert.NotEqual(upper.OpaqueDeviceId, lower.OpaqueDeviceId);
        Assert.Equal(CaptureDeviceSelectionMode.Exact, sentinelLooking.Mode);
        Assert.Equal("default", sentinelLooking.OpaqueDeviceId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExactSelectionRejectsNullOrBlankIdentity(string? identity)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            CaptureDeviceSelection.Exact(CaptureDeviceRole.Microphone, identity!));
    }

    [Fact]
    public void SystemDefaultIsExplicitAndRestrictedToAudioRoles()
    {
        var desktop = CaptureDeviceSelection.SystemDefault(CaptureDeviceRole.DesktopAudio);
        var microphone = CaptureDeviceSelection.SystemDefault(CaptureDeviceRole.Microphone);

        Assert.Equal(CaptureDeviceSelectionMode.SystemDefault, desktop.Mode);
        Assert.Null(desktop.OpaqueDeviceId);
        Assert.Equal(CaptureDeviceSelectionMode.SystemDefault, microphone.Mode);
        Assert.Throws<ArgumentException>(() =>
            CaptureDeviceSelection.SystemDefault(CaptureDeviceRole.Webcam));
    }

    [Fact]
    public void CatalogUsesCaseSensitiveIdsAndRejectsExactDuplicates()
    {
        var entries = new[]
        {
            new CaptureDeviceCatalogEntry(CaptureDeviceRole.Microphone, "Device-A", isSystemDefault: true),
            new CaptureDeviceCatalogEntry(CaptureDeviceRole.Microphone, "device-a", isSystemDefault: false),
        };

        var snapshot = new CaptureDeviceCatalogSnapshot(entries);

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Throws<ArgumentException>(() =>
            new CaptureDeviceCatalogSnapshot(
            [
                entries[0],
                new CaptureDeviceCatalogEntry(CaptureDeviceRole.Microphone, "Device-A", false),
            ]));
    }

    [Fact]
    public void CatalogRequiresAtMostOneAudioDefaultAndNoWebcamDefault()
    {
        Assert.Throws<ArgumentException>(() =>
            new CaptureDeviceCatalogSnapshot(
            [
                new CaptureDeviceCatalogEntry(CaptureDeviceRole.DesktopAudio, "one", true),
                new CaptureDeviceCatalogEntry(CaptureDeviceRole.DesktopAudio, "two", true),
            ]));
        Assert.Throws<ArgumentException>(() =>
            new CaptureDeviceCatalogEntry(CaptureDeviceRole.Webcam, "camera", true));
    }

    [Fact]
    public void CatalogAndObservationDefensivelyFreezeCallerCollections()
    {
        var source = new List<CaptureDeviceCatalogEntry>
        {
            new(CaptureDeviceRole.Webcam, "camera-1", false),
        };
        var snapshot = new CaptureDeviceCatalogSnapshot(source);
        source.Add(new CaptureDeviceCatalogEntry(CaptureDeviceRole.Webcam, "camera-2", false));

        var available = CaptureDeviceCatalogObservation.Available(snapshot);
        var unavailable = CaptureDeviceCatalogObservation.Unavailable();
        var invalid = CaptureDeviceCatalogObservation.Invalid();

        Assert.Single(snapshot.Entries);
        Assert.Same(snapshot, available.Snapshot);
        Assert.Equal(CaptureDeviceCatalogObservationKind.Available, available.Kind);
        Assert.Null(unavailable.Snapshot);
        Assert.Equal(CaptureDeviceCatalogObservationKind.Unavailable, unavailable.Kind);
        Assert.Null(invalid.Snapshot);
        Assert.Equal(CaptureDeviceCatalogObservationKind.Invalid, invalid.Kind);
    }

    [Fact]
    public void DeviceToStringNeverContainsOpaqueIdentity()
    {
        var selection = CaptureDeviceSelection.Exact(CaptureDeviceRole.Microphone, "secret-endpoint-id");
        var entry = new CaptureDeviceCatalogEntry(
            CaptureDeviceRole.Microphone,
            "secret-endpoint-id",
            true);
        var snapshot = new CaptureDeviceCatalogSnapshot([entry]);

        Assert.DoesNotContain("secret-endpoint-id", selection.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-endpoint-id", entry.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-endpoint-id", snapshot.ToString(), StringComparison.Ordinal);
    }
}
