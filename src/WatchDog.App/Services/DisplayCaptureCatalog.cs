using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Forms;
using WatchDog.Core.Capture;

namespace WatchDog.App.Services;

public sealed record DisplayScreenMetadata(string DeviceName, int Width, int Height, bool IsPrimary);

public sealed record DisplayCaptureOption(
    DisplayCaptureTarget Target, string DeviceName, int Width, int Height, bool IsPrimary);

/// <summary>
/// Explicit metadata enumeration using the existing engine's display identities.
/// Construction does not enumerate. No capture resources or identity substitutes are created.
/// </summary>
public sealed class DisplayCaptureCatalog
{
    private readonly Func<IReadOnlyList<DisplayScreenMetadata>> _screens;
    private readonly Func<string, DisplayCaptureTarget?> _resolveDeviceName;
    private readonly Func<string, DisplayCaptureTarget?> _resolveStableId;

    public DisplayCaptureCatalog(ICaptureEngine engine)
        : this(ReadScreens, (engine ?? throw new ArgumentNullException(nameof(engine))).ResolveDisplayTargetByDeviceName,
            engine.ResolveDisplayTarget)
    { }

    public DisplayCaptureCatalog(Func<IReadOnlyList<DisplayScreenMetadata>> screens,
        Func<string, DisplayCaptureTarget?> resolveDeviceName,
        Func<string, DisplayCaptureTarget?> resolveStableId)
    {
        _screens = screens ?? throw new ArgumentNullException(nameof(screens));
        _resolveDeviceName = resolveDeviceName ?? throw new ArgumentNullException(nameof(resolveDeviceName));
        _resolveStableId = resolveStableId ?? throw new ArgumentNullException(nameof(resolveStableId));
    }

    private static IReadOnlyList<DisplayScreenMetadata> ReadScreens() => Screen.AllScreens
        .Select(screen => new DisplayScreenMetadata(screen.DeviceName, screen.Bounds.Width,
            screen.Bounds.Height, screen.Primary)).ToArray();

    /// <summary>Preserves Settings' legacy rows, labels and screen indices, including unresolved IDs.</summary>
    public IReadOnlyList<MonitorInfo> GetMonitorsOrThrow() => Array.AsReadOnly(_screens()
        .Select((screen, index) => new MonitorInfo(index,
            _resolveDeviceName(screen.DeviceName)?.StableDisplayId ?? string.Empty,
            screen.DeviceName,
            $"Monitor {index + 1}: {screen.Width}x{screen.Height}" + (screen.IsPrimary ? " (Primary)" : ""),
            screen.Width, screen.Height, screen.IsPrimary)).ToArray());

    /// <summary>Tutorial selection rejects the complete snapshot if any identity is unresolved or ambiguous.</summary>
    public IReadOnlyList<DisplayCaptureOption> GetDisplaysOrThrow()
    {
        var screens = _screens()?.ToArray() ?? throw InvalidCatalog();
        var options = new List<DisplayCaptureOption>(screens.Length);
        foreach (var screen in screens)
        {
            if (screen is null || string.IsNullOrWhiteSpace(screen.DeviceName) || screen.Width <= 0 || screen.Height <= 0)
                throw InvalidCatalog();
            var target = _resolveDeviceName(screen.DeviceName);
            if (target is null || !IsCurrent(target)) throw InvalidCatalog();
            // The engine's name/index belong to the frozen target. Never reconstruct
            // them from the independent Windows screen ordinal or Settings label.
            options.Add(new(target, screen.DeviceName, screen.Width, screen.Height, screen.IsPrimary));
        }
        return ValidateOptions(options);
    }

    public bool IsCurrent(DisplayCaptureTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return !string.IsNullOrWhiteSpace(target.StableDisplayId) &&
            _resolveStableId(target.StableDisplayId) == target;
    }

    public static IReadOnlyList<DisplayCaptureOption> ValidateOptions(IReadOnlyList<DisplayCaptureOption> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var copy = options.ToArray();
        var stableIds = new HashSet<string>(StringComparer.Ordinal);
        var deviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in copy)
        {
            if (option is null || option.Target is null || string.IsNullOrWhiteSpace(option.Target.StableDisplayId) ||
                string.IsNullOrWhiteSpace(option.DeviceName) || option.Width <= 0 || option.Height <= 0 ||
                !stableIds.Add(option.Target.StableDisplayId) || !deviceNames.Add(option.DeviceName))
                throw InvalidCatalog();
        }
        return new ReadOnlyCollection<DisplayCaptureOption>(copy);
    }

    private static InvalidDataException InvalidCatalog() =>
        new("The display list contains an unavailable or ambiguous identity. Refresh before choosing a display.");
}
