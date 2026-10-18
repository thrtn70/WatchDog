using WatchDog.CaptureMetadata;

namespace WatchDog.Core.Tests.Metadata;

public sealed class CaptureMetadataCorrelationSnapshotTests
{
    [Fact]
    public void SnapshotPreservesExactContextAndDefensivelyCopiesSelections()
    {
        var requested = new ResolvedDisplayTarget(
            "Display-A",
            new PhysicalCaptureBounds(0, 0, 1920, 1080));
        var actual = new ResolvedDisplayTarget(
            "Display-A",
            new PhysicalCaptureBounds(0, 0, 1920, 1080));
        var selections = new List<CaptureDeviceSelection>
        {
            CaptureDeviceSelection.Exact(CaptureDeviceRole.Microphone, "Mic-A"),
        };
        var sessionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var requestId = Guid.Parse("20000000-0000-0000-0000-000000000002");

        var snapshot = new CaptureMetadataCorrelationSnapshot(
            sessionId,
            requestId,
            3,
            requested,
            actual,
            selections);
        selections.Clear();

        Assert.Equal(sessionId, snapshot.SessionId);
        Assert.Equal(requestId, snapshot.RequestId);
        Assert.Equal(3, snapshot.Generation);
        Assert.Same(requested, snapshot.RequestedTarget);
        Assert.Same(actual, snapshot.ActualTarget);
        Assert.Single(snapshot.DeviceSelections);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CaptureDeviceSelection>)snapshot.DeviceSelections).Add(
                CaptureDeviceSelection.Exact(CaptureDeviceRole.Webcam, "Camera")));
    }

    [Theory]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(false, false, 0)]
    [InlineData(false, false, -1)]
    public void SnapshotRejectsInvalidCorrelation(bool emptySession, bool emptyRequest, long generation)
    {
        var target = new ResolvedWindowTarget(10, 100);

        Assert.ThrowsAny<ArgumentException>(() => new CaptureMetadataCorrelationSnapshot(
            emptySession ? Guid.Empty : Guid.NewGuid(),
            emptyRequest ? Guid.Empty : Guid.NewGuid(),
            generation,
            target,
            target,
            []));
    }

    [Fact]
    public void SnapshotRejectsNullTargetsSelectionsAndDuplicateRoles()
    {
        var target = new ResolvedWindowTarget(10, 100);

        Assert.Throws<ArgumentNullException>(() => new CaptureMetadataCorrelationSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), 1, null!, target, []));
        Assert.Throws<ArgumentNullException>(() => new CaptureMetadataCorrelationSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), 1, target, null!, []));
        Assert.Throws<ArgumentNullException>(() => new CaptureMetadataCorrelationSnapshot(
            Guid.NewGuid(), Guid.NewGuid(), 1, target, target, null!));
        Assert.Throws<ArgumentException>(() => new CaptureMetadataCorrelationSnapshot(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            target,
            target,
            [
                CaptureDeviceSelection.Exact(CaptureDeviceRole.Microphone, "one"),
                CaptureDeviceSelection.Exact(CaptureDeviceRole.Microphone, "two"),
            ]));
    }

    [Fact]
    public void SnapshotToStringRedactsCorrelationAndPhysicalIdentities()
    {
        var sessionId = Guid.Parse("10000000-0000-0000-0000-000000000001");
        var requestId = Guid.Parse("20000000-0000-0000-0000-000000000002");
        var target = new ResolvedWindowTarget(12345, 0xABCDEF);
        var snapshot = new CaptureMetadataCorrelationSnapshot(
            sessionId,
            requestId,
            6789,
            target,
            target,
            [CaptureDeviceSelection.Exact(CaptureDeviceRole.Microphone, "secret-device")]);
        var text = snapshot.ToString();

        Assert.DoesNotContain(sessionId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(requestId.ToString(), text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12345", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEF", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-device", text, StringComparison.Ordinal);
    }
}
