using WatchDog.Core.Capture;

namespace WatchDog.Core.Tests.Capture;

public sealed class RegionCaptureGeometryTests
{
    [Fact]
    public void NegativeDisplayOriginConvertsToDisplayLocalPixels()
    {
        var target = Target(
            new PhysicalCaptureBounds(-1800, -800, 640, 360),
            new PhysicalCaptureBounds(-1920, -1080, 1920, 1080));

        Assert.True(RegionCaptureGeometry.TryGetDisplayLocalBounds(target, out var local));
        Assert.Equal(new PhysicalCaptureBounds(120, 280, 640, 360), local);
    }

    [Fact]
    public void ExactDisplayEdgesAreAcceptedWithoutClamping()
    {
        var target = Target(
            new PhysicalCaptureBounds(100, 200, 1920, 1080),
            new PhysicalCaptureBounds(100, 200, 1920, 1080));

        Assert.True(RegionCaptureGeometry.TryGetDisplayLocalBounds(target, out var local));
        Assert.Equal(new PhysicalCaptureBounds(0, 0, 1920, 1080), local);
    }

    [Theory]
    [InlineData(99, 200, 100, 100)]
    [InlineData(100, 199, 100, 100)]
    [InlineData(1921, 200, 100, 100)]
    [InlineData(100, 1181, 100, 100)]
    public void AnyOutOfOwnerPixelIsRejected(int x, int y, int width, int height)
    {
        var target = Target(
            new PhysicalCaptureBounds(x, y, width, height),
            new PhysicalCaptureBounds(100, 200, 1920, 1080));

        Assert.False(RegionCaptureGeometry.TryGetDisplayLocalBounds(target, out _));
    }

    [Fact]
    public void CoordinateOverflowIsRejected()
    {
        var target = Target(
            new PhysicalCaptureBounds(int.MaxValue - 5, 0, 10, 10),
            new PhysicalCaptureBounds(int.MaxValue - 10, 0, 20, 20));

        Assert.False(RegionCaptureGeometry.TryGetDisplayLocalBounds(target, out _));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    [InlineData(-1, 10)]
    [InlineData(10, -1)]
    public void NonPositiveRegionDimensionsAreRejected(int width, int height)
    {
        var target = Target(
            new PhysicalCaptureBounds(0, 0, width, height),
            new PhysicalCaptureBounds(0, 0, 1920, 1080));

        Assert.False(RegionCaptureGeometry.TryGetDisplayLocalBounds(target, out _));
    }

    private static RegionCaptureTarget Target(
        PhysicalCaptureBounds region,
        PhysicalCaptureBounds display) =>
        new("display-1", region, display);
}
