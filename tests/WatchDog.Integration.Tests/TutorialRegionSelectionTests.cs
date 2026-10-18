using WatchDog.App.Services;
using WatchDog.Core.Capture;

namespace WatchDog.Integration.Tests;

public sealed class TutorialRegionSelectionTests
{
    private static readonly DisplayCaptureTarget Owner = new("display-a", "Display", 1);
    private static readonly PhysicalCaptureBounds OwnerBounds = new(-1920, -100, 1920, 1080);

    [Theory]
    [InlineData("-1920", "-100", "1920", "1080")]
    [InlineData("-10", "0", "10", "20")]
    [InlineData(" +10 ", "20", "30", "40")]
    public void PhysicalIntegerDraftAcceptsNegativeOriginsAndExplicitSizes(string x, string y, string width, string height)
    {
        Assert.True(TutorialRegionSelection.TryParseBounds(x, y, width, height, out var bounds));
        Assert.True(bounds.Width > 0);
        Assert.True(bounds.Height > 0);
    }

    [Theory]
    [InlineData("", "0", "20", "20")]
    [InlineData("-", "0", "20", "20")]
    [InlineData("1.5", "0", "20", "20")]
    [InlineData("1e2", "0", "20", "20")]
    [InlineData("1,000", "0", "20", "20")]
    [InlineData("2147483648", "0", "20", "20")]
    [InlineData("0", "0", "0", "20")]
    [InlineData("0", "0", "20", "-1")]
    [InlineData("2147483647", "0", "1", "20")]
    [InlineData("0", "2147483647", "20", "1")]
    [InlineData(null, "0", "20", "20")]
    public void InvalidOrIncompleteDraftCannotReuseEarlierCoordinates(string? x, string y, string width, string height)
    {
        Assert.False(TutorialRegionSelection.TryParseBounds(x, y, width, height, out var bounds));
        Assert.Equal(default, bounds);
    }

    [Fact]
    public void ResolvesOnceAtConfirmationAndRetainsExactEdgeTarget()
    {
        var returned = new RegionCaptureTarget(Owner.StableDisplayId, OwnerBounds, OwnerBounds);
        var resolutions = 0;
        var selection = new TutorialRegionSelection(owner => owner == Owner,
            (id, requested) =>
            {
                resolutions++;
                Assert.Equal(Owner.StableDisplayId, id);
                Assert.Equal(OwnerBounds, requested);
                return returned;
            }, target => ReferenceEquals(returned, target));
        Assert.Equal(0, resolutions);
        Assert.Same(returned, selection.ResolveOrThrow(Owner, OwnerBounds));
        Assert.True(selection.IsCurrent(returned));
        Assert.Equal(1, resolutions);
    }

    [Fact]
    public void OwnerMustStillMatchAfterResolverReturns()
    {
        var ownerChecks = 0;
        var selection = new TutorialRegionSelection(_ => ++ownerChecks == 1,
            (id, bounds) => new(id, bounds, OwnerBounds), _ => true);
        Assert.Throws<InvalidDataException>(() => selection.ResolveOrThrow(Owner, OwnerBounds));
        Assert.Equal(2, ownerChecks);
    }

    [Fact]
    public void MissingOrChangedOwnerRejectsBeforeRegionResolution()
    {
        var selection = new TutorialRegionSelection(_ => false,
            (_, _) => throw new InvalidOperationException("Must not resolve a stale owner."), _ => true);
        Assert.Throws<InvalidDataException>(() => selection.ResolveOrThrow(Owner, OwnerBounds));
        Assert.Throws<InvalidDataException>(() => selection.ResolveOrThrow(Owner with { StableDisplayId = " " }, OwnerBounds));
    }

    [Fact]
    public void ResolverCannotSubstituteOwnerOrRectangleOrMalformedFrozenBounds()
    {
        var requested = new PhysicalCaptureBounds(-100, 0, 50, 50);
        var valid = new RegionCaptureTarget(Owner.StableDisplayId, requested, OwnerBounds);
        foreach (var result in new RegionCaptureTarget?[]
        {
            null,
            valid with { OwningDisplayId = "display-b" },
            valid with { OwningDisplayId = "DISPLAY-A" },
            valid with { PhysicalBounds = requested with { Width = 51 } },
            valid with { OwningDisplayBounds = OwnerBounds with { Width = 0 } },
            valid with { OwningDisplayBounds = new(0, 0, 1920, 1080) },
            valid with { OwningDisplayBounds = new(-10, 0, int.MaxValue, 100) },
            valid with { OwningDisplayBounds = new(10, 0, int.MaxValue, 100) },
        })
        {
            var selection = new TutorialRegionSelection(_ => true, (_, _) => result, _ => true);
            Assert.Throws<InvalidDataException>(() => selection.ResolveOrThrow(Owner, requested));
        }
    }

    [Theory]
    [InlineData(10, 0, int.MaxValue, 100, 20, 20)]
    [InlineData(0, 10, 100, int.MaxValue, 20, 20)]
    public void OverflowingOwnerIsRejectedEvenWhenTheRequestedRegionDoesNotOverflow(
        int ownerX, int ownerY, int ownerWidth, int ownerHeight, int regionX, int regionY)
    {
        var requested = new PhysicalCaptureBounds(regionX, regionY, 10, 10);
        var selection = new TutorialRegionSelection(_ => true,
            (id, bounds) => new(id, bounds, new(ownerX, ownerY, ownerWidth, ownerHeight)), _ => true);
        Assert.Throws<InvalidDataException>(() => selection.ResolveOrThrow(Owner, requested));
    }

    [Theory]
    [InlineData(-1921, -100, 20, 20)]
    [InlineData(-10, 0, 11, 20)]
    [InlineData(-100, 970, 20, 11)]
    [InlineData(0, 0, 0, 20)]
    public void InvalidRegionDoesNotClampOrWidenToOwningDisplay(int x, int y, int width, int height)
    {
        var requested = new PhysicalCaptureBounds(x, y, width, height);
        var selection = new TutorialRegionSelection(_ => true,
            (id, bounds) => new(id, bounds, OwnerBounds), _ => true);
        Assert.Throws<InvalidDataException>(() => selection.ResolveOrThrow(Owner, requested));
    }

    [Fact]
    public void CurrentCheckRejectsInvalidGeometryAndReportsOwnerLossWithoutResolvingAgain()
    {
        var current = true;
        var target = new RegionCaptureTarget(Owner.StableDisplayId, OwnerBounds, OwnerBounds);
        var selection = new TutorialRegionSelection(_ => true,
            (_, _) => throw new InvalidOperationException("No reconstruction during current check."), _ => current);
        Assert.True(selection.IsCurrent(target));
        current = false;
        Assert.False(selection.IsCurrent(target));
        current = true;
        Assert.False(selection.IsCurrent(target with { PhysicalBounds = OwnerBounds with { Width = 0 } }));
    }

    [Fact]
    public void UnavailableOrThrowingBoundaryNeverReturnsAnApprovedRegion()
    {
        var target = new RegionCaptureTarget(Owner.StableDisplayId, OwnerBounds, OwnerBounds);
        var unavailable = new TutorialRegionSelection(_ => true, (_, _) => target, _ => false);
        Assert.Throws<InvalidDataException>(() => unavailable.ResolveOrThrow(Owner, OwnerBounds));
        var throwing = new TutorialRegionSelection(_ => true,
            (_, _) => throw new InvalidOperationException("private device"), _ => true);
        Assert.Throws<InvalidOperationException>(() => throwing.ResolveOrThrow(Owner, OwnerBounds));
    }
}
