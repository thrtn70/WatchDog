namespace WatchDog.Core.Capture;

/// <summary>Fail-closed physical-region validation and coordinate conversion.</summary>
public static class RegionCaptureGeometry
{
    public static bool TryGetDisplayLocalBounds(
        RegionCaptureTarget target,
        out PhysicalCaptureBounds localBounds)
    {
        localBounds = default;
        var display = target.OwningDisplayBounds;
        var region = target.PhysicalBounds;

        if (string.IsNullOrWhiteSpace(target.OwningDisplayId) ||
            display.Width <= 0 || display.Height <= 0 ||
            region.Width <= 0 || region.Height <= 0)
        {
            return false;
        }

        var displayRight = (long)display.X + display.Width;
        var displayBottom = (long)display.Y + display.Height;
        var regionRight = (long)region.X + region.Width;
        var regionBottom = (long)region.Y + region.Height;

        if (displayRight > int.MaxValue || displayBottom > int.MaxValue ||
            regionRight > int.MaxValue || regionBottom > int.MaxValue)
        {
            return false;
        }

        if (region.X < display.X || region.Y < display.Y ||
            regionRight > displayRight || regionBottom > displayBottom)
        {
            return false;
        }

        var localX = (long)region.X - display.X;
        var localY = (long)region.Y - display.Y;
        if (localX is < 0 or > int.MaxValue || localY is < 0 or > int.MaxValue)
            return false;

        localBounds = new PhysicalCaptureBounds((int)localX, (int)localY, region.Width, region.Height);
        return true;
    }
}
