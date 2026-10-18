using WatchDog.CaptureMetadata;

namespace WatchDog.Core.Tests.Metadata;

public sealed class ResolvedCaptureTargetTests
{
    [Fact]
    public void TargetIdentityEqualityIsCompleteAndOrdinal()
    {
        Assert.Equal(new ResolvedWindowTarget(42, 100), new ResolvedWindowTarget(42, 100));
        Assert.NotEqual(new ResolvedWindowTarget(42, 100), new ResolvedWindowTarget(43, 100));
        Assert.NotEqual(new ResolvedWindowTarget(42, 100), new ResolvedWindowTarget(42, 101));

        var bounds = new PhysicalCaptureBounds(-10, -20, 100, 200);
        Assert.Equal(
            new ResolvedDisplayTarget("Display-A", bounds),
            new ResolvedDisplayTarget("Display-A", new PhysicalCaptureBounds(-10, -20, 100, 200)));
        Assert.NotEqual(
            new ResolvedDisplayTarget("Display-A", bounds),
            new ResolvedDisplayTarget("display-a", bounds));
        Assert.NotEqual(
            new ResolvedDisplayTarget("Display-A", bounds),
            new ResolvedDisplayTarget("Display-A", new PhysicalCaptureBounds(-10, -20, 101, 200)));

        Assert.Equal(
            new ResolvedRegionTarget("Display-A", bounds, bounds),
            new ResolvedRegionTarget(
                "Display-A",
                new PhysicalCaptureBounds(-10, -20, 100, 200),
                new PhysicalCaptureBounds(-10, -20, 100, 200)));
        Assert.NotEqual(
            new ResolvedRegionTarget("Display-A", bounds, bounds),
            new ResolvedRegionTarget("display-a", bounds, bounds));
    }

    [Fact]
    public void DisplayAndRegionRejectMissingBounds()
    {
        var valid = new PhysicalCaptureBounds(0, 0, 10, 10);

        Assert.Throws<ArgumentNullException>(() => new ResolvedDisplayTarget("display", null!));
        Assert.Throws<ArgumentNullException>(() => new ResolvedRegionTarget("display", null!, valid));
        Assert.Throws<ArgumentNullException>(() => new ResolvedRegionTarget("display", valid, null!));
        Assert.Throws<ArgumentNullException>(() => valid.Contains(null!));
    }

    [Fact]
    public void WindowPreservesExactPidAndOpaqueHandle()
    {
        var target = new ResolvedWindowTarget(42, 0xABCDEF);

        Assert.Equal(42, target.ProcessId);
        Assert.Equal(0xABCDEF, target.OpaqueWindowHandleValue);
        Assert.Equal(ResolvedCaptureTargetKind.Window, target.Kind);
        Assert.DoesNotContain("42", target.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEF", target.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    public void WindowRejectsInvalidPhysicalIdentity(int processId, long handle)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResolvedWindowTarget(processId, handle));
    }

    [Fact]
    public void DisplayPreservesOrdinalStableIdAndFrozenBounds()
    {
        var target = new ResolvedDisplayTarget(
            " Display-ID ",
            new PhysicalCaptureBounds(-1920, -10, 1920, 1080));

        Assert.Equal(" Display-ID ", target.StableDisplayId);
        Assert.Equal(new PhysicalCaptureBounds(-1920, -10, 1920, 1080), target.FrozenPhysicalBounds);
        Assert.DoesNotContain("Display-ID", target.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("1920", target.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DisplayRejectsNullOrBlankStableId(string? stableId)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            new ResolvedDisplayTarget(stableId!, new PhysicalCaptureBounds(0, 0, 10, 10)));
    }

    [Fact]
    public void RegionAllowsNegativeOriginsAndExactBoundaryContainment()
    {
        var owner = new PhysicalCaptureBounds(-2000, -1200, 2000, 1200);
        var selected = new PhysicalCaptureBounds(-2000, -1200, 2000, 1200);

        var target = new ResolvedRegionTarget("DISPLAY-a", selected, owner);

        Assert.Equal(selected, target.SelectedPhysicalBounds);
        Assert.Equal(owner, target.FrozenOwningDisplayBounds);
    }

    [Theory]
    [InlineData(-2001, -1200, 2000, 1200)]
    [InlineData(-2000, -1201, 2000, 1200)]
    [InlineData(-2000, -1200, 2001, 1200)]
    [InlineData(-2000, -1200, 2000, 1201)]
    public void RegionRejectsAnyPixelOutsideFrozenOwner(int x, int y, int width, int height)
    {
        var owner = new PhysicalCaptureBounds(-2000, -1200, 2000, 1200);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResolvedRegionTarget(
                "DISPLAY-a",
                new PhysicalCaptureBounds(x, y, width, height),
                owner));
    }

    [Fact]
    public void ContainmentIsOverflowSafeNearIntegerLimits()
    {
        var owner = new PhysicalCaptureBounds(int.MaxValue - 100, int.MinValue, 100, 100);
        var contained = new PhysicalCaptureBounds(int.MaxValue - 50, int.MinValue + 25, 50, 75);
        var outside = new PhysicalCaptureBounds(int.MaxValue - 50, int.MinValue + 25, 51, 75);

        Assert.True(owner.Contains(contained));
        Assert.False(owner.Contains(outside));
        _ = new ResolvedRegionTarget("DISPLAY-max", contained, owner);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResolvedRegionTarget("DISPLAY-max", outside, owner));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public void BoundsRequirePositiveDimensions(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PhysicalCaptureBounds(0, 0, width, height));
    }

    [Fact]
    public void GeometryAndRegionToStringAreRedacted()
    {
        var bounds = new PhysicalCaptureBounds(-1234, 5678, 99, 88);
        var target = new ResolvedRegionTarget(
            "sensitive-display-id",
            bounds,
            new PhysicalCaptureBounds(-2000, 5000, 3000, 3000));

        Assert.Equal("PhysicalCaptureBounds { redacted }", bounds.ToString());
        Assert.DoesNotContain("sensitive-display-id", target.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("1234", target.ToString(), StringComparison.Ordinal);
    }
}
