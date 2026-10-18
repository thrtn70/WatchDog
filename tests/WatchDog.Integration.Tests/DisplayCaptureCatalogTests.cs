using WatchDog.App.Services;
using WatchDog.Core.Capture;

namespace WatchDog.Integration.Tests;

public sealed class DisplayCaptureCatalogTests
{
    private static readonly DisplayScreenMetadata Primary = new("DISPLAY1", 1920, 1080, true);
    private static readonly DisplayCaptureTarget Target = new("stable-a", "Engine display", 7);

    [Fact]
    public void ConstructionDoesNotEnumerateAndTutorialKeepsResolvedTargetInsteadOfScreenOrdinal()
    {
        var enumerations = 0;
        var catalog = new DisplayCaptureCatalog(() => { enumerations++; return [Primary]; }, _ => Target, _ => Target);
        Assert.Equal(0, enumerations);
        var option = Assert.Single(catalog.GetDisplaysOrThrow());
        Assert.Equal(1, enumerations);
        Assert.Same(Target, option.Target);
        Assert.Equal(7, option.Target.MonitorIndex);
        Assert.Equal("DISPLAY1", option.DeviceName);
        Assert.Equal(1920, option.Width);
    }

    [Fact]
    public void SettingsKeepsScreenOrderLabelsUnresolvedIdentitiesAndOriginalIndexes()
    {
        var catalog = new DisplayCaptureCatalog(
            () => [Primary, new("DISPLAY2", 1280, 720, false)],
            name => name == "DISPLAY1" ? Target : null,
            _ => throw new InvalidOperationException("Settings must not gain a stable-id resolution step."));
        var monitors = catalog.GetMonitorsOrThrow();
        Assert.Equal([0, 1], monitors.Select(m => m.Index));
        Assert.Equal(["stable-a", ""], monitors.Select(m => m.StableDisplayId));
        Assert.Equal(["Monitor 1: 1920x1080 (Primary)", "Monitor 2: 1280x720"], monitors.Select(m => m.DisplayName));
        Assert.Equal([true, false], monitors.Select(m => m.IsPrimary));
    }

    [Fact]
    public void SettingsStillKeepsDuplicateResolvedIdentities()
    {
        var catalog = new DisplayCaptureCatalog(() => [Primary, Primary with { DeviceName = "DISPLAY2" }], _ => Target, _ => Target);
        Assert.Equal(2, catalog.GetMonitorsOrThrow().Count);
        Assert.Throws<InvalidDataException>(() => catalog.GetDisplaysOrThrow());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void TutorialRejectsUnresolvedOrBlankStableIdentity(string? id)
    {
        var target = id is null ? null : Target with { StableDisplayId = id };
        var catalog = new DisplayCaptureCatalog(() => [Primary], _ => target, _ => target);
        Assert.Throws<InvalidDataException>(() => catalog.GetDisplaysOrThrow());
    }

    [Fact]
    public void AmbiguousStableResolutionRejectsDeviceNameMatch()
    {
        var catalog = new DisplayCaptureCatalog(() => [Primary], _ => Target, _ => null);
        Assert.Throws<InvalidDataException>(() => catalog.GetDisplaysOrThrow());
    }

    [Fact]
    public void TutorialRejectsDuplicateWindowsDeviceNamesIgnoringCase()
    {
        var catalog = new DisplayCaptureCatalog(() => [Primary, Primary with { DeviceName = "display1" }],
            name => Target with { StableDisplayId = name }, id => Target with { StableDisplayId = id });
        Assert.Throws<InvalidDataException>(() => catalog.GetDisplaysOrThrow());
    }

    [Fact]
    public void CurrentRequiresFullFrozenTargetEquality()
    {
        DisplayCaptureTarget? current = Target;
        var catalog = new DisplayCaptureCatalog(() => throw new InvalidOperationException("No screen enumeration during revalidation."),
            _ => throw new InvalidOperationException("No device-name fallback."), _ => current);
        Assert.True(catalog.IsCurrent(Target));
        foreach (var changed in new DisplayCaptureTarget?[]
        {
            null, Target with { StableDisplayId = "stable-b" },
            Target with { MonitorIndex = 8 }, Target with { DisplayName = "Changed" },
        })
        {
            current = changed;
            Assert.False(catalog.IsCurrent(Target));
        }
    }

    [Fact]
    public void ChangedResolutionDuringListCreationCannotPublishStaleTarget()
    {
        var catalog = new DisplayCaptureCatalog(() => [Primary], _ => Target, _ => Target with { MonitorIndex = 8 });
        Assert.Throws<InvalidDataException>(() => catalog.GetDisplaysOrThrow());
    }

    [Fact]
    public void ResolverFailureIsNotAnEmptyOrGuessedDisplayList()
    {
        var catalog = new DisplayCaptureCatalog(() => [Primary],
            _ => throw new InvalidOperationException("private identity"), _ => Target);
        Assert.Throws<InvalidOperationException>(() => catalog.GetDisplaysOrThrow());
    }

    [Fact]
    public void EmptySnapshotIsValidButAnyMalformedRowRejectsTheWholeSnapshot()
    {
        IReadOnlyList<DisplayScreenMetadata> screens = [];
        var catalog = new DisplayCaptureCatalog(() => screens, _ => Target, _ => Target);
        Assert.Empty(catalog.GetDisplaysOrThrow());
        screens = [Primary, new("DISPLAY2", 0, 720, false)];
        Assert.Throws<InvalidDataException>(() => catalog.GetDisplaysOrThrow());
        screens = null!;
        Assert.Throws<InvalidDataException>(() => catalog.GetDisplaysOrThrow());
    }

    [Fact]
    public void StableResolverFailurePropagatesToTheFailClosedConsumer()
    {
        var catalog = new DisplayCaptureCatalog(() => [Primary], _ => Target,
            _ => throw new InvalidOperationException("private identity"));
        Assert.Throws<InvalidOperationException>(() => catalog.IsCurrent(Target));
        Assert.Throws<InvalidOperationException>(() => catalog.GetDisplaysOrThrow());
    }

    [Fact]
    public void ValidationCopiesCollectionAndRejectsMalformedOptions()
    {
        var valid = new DisplayCaptureOption(Target, "DISPLAY1", 1920, 1080, true);
        var input = new List<DisplayCaptureOption> { valid };
        var offered = DisplayCaptureCatalog.ValidateOptions(input);
        input.Clear();
        Assert.Same(valid, Assert.Single(offered));
        foreach (var malformed in new[]
        {
            valid with { Width = 0 }, valid with { Height = -1 }, valid with { DeviceName = " " },
            valid with { Target = Target with { StableDisplayId = "" } }, valid with { Target = null! },
        })
            Assert.Throws<InvalidDataException>(() => DisplayCaptureCatalog.ValidateOptions([malformed]));
        Assert.Throws<InvalidDataException>(() => DisplayCaptureCatalog.ValidateOptions([null!]));
    }
}
