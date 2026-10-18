using System.Globalization;
using System.IO;
using WatchDog.Core.Capture;

namespace WatchDog.App.Services;

/// <summary>
/// Explicit physical-pixel region confirmation over existing capture contracts.
/// Construction and parsing perform no enumeration, resolution or resource acquisition.
/// </summary>
public sealed class TutorialRegionSelection
{
    private readonly Func<DisplayCaptureTarget, bool> _isOwnerCurrent;
    private readonly Func<string, PhysicalCaptureBounds, RegionCaptureTarget?> _resolve;
    private readonly Func<RegionCaptureTarget, bool> _isRegionCurrent;

    public TutorialRegionSelection(ICaptureEngine engine)
        : this(owner => engine.IsDisplayTargetAvailable(owner) && engine.ResolveDisplayTarget(owner.StableDisplayId) == owner,
            (engine ?? throw new ArgumentNullException(nameof(engine))).ResolveRegionTarget, engine.IsRegionTargetAvailable)
    { }

    public TutorialRegionSelection(Func<DisplayCaptureTarget, bool> isOwnerCurrent,
        Func<string, PhysicalCaptureBounds, RegionCaptureTarget?> resolve,
        Func<RegionCaptureTarget, bool> isRegionCurrent)
    {
        _isOwnerCurrent = isOwnerCurrent ?? throw new ArgumentNullException(nameof(isOwnerCurrent));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _isRegionCurrent = isRegionCurrent ?? throw new ArgumentNullException(nameof(isRegionCurrent));
    }

    public static bool TryParseBounds(string? xText, string? yText, string? widthText, string? heightText,
        out PhysicalCaptureBounds bounds)
    {
        bounds = default;
        if (!int.TryParse(xText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ||
            !int.TryParse(yText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ||
            !int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var width) ||
            !int.TryParse(heightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var height))
            return false;
        var parsed = new PhysicalCaptureBounds(x, y, width, height);
        if (!HasValidDimensions(parsed)) return false;
        bounds = parsed;
        return true;
    }

    public RegionCaptureTarget ResolveOrThrow(DisplayCaptureTarget owner, PhysicalCaptureBounds requested)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrWhiteSpace(owner.StableDisplayId) || !HasValidDimensions(requested) || !_isOwnerCurrent(owner))
            throw InvalidSelection();

        var resolved = _resolve(owner.StableDisplayId, requested);
        if (resolved is null || !StringComparer.Ordinal.Equals(resolved.OwningDisplayId, owner.StableDisplayId) ||
            resolved.PhysicalBounds != requested || !IsCurrent(resolved) || !_isOwnerCurrent(owner))
            throw InvalidSelection();
        return resolved;
    }

    /// <summary>Revalidate the frozen target without resolving or reconstructing it.</summary>
    public bool IsCurrent(RegionCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return RegionCaptureGeometry.TryGetDisplayLocalBounds(target, out _) && _isRegionCurrent(target);
    }

    private static bool HasValidDimensions(PhysicalCaptureBounds bounds) =>
        bounds.Width > 0 && bounds.Height > 0 &&
        (long)bounds.X + bounds.Width <= int.MaxValue && (long)bounds.Y + bounds.Height <= int.MaxValue;

    private static InvalidDataException InvalidSelection() =>
        new("The exact region could not be confirmed within its selected display. Check the coordinates and choose the display again.");
}
