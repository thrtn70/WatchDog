using WatchDog.Core.Audio;

namespace WatchDog.Core.Devices;

public enum CaptureDeviceSelectionMode
{
    Exact,
    SystemDefault,
}

/// <summary>An immutable device selection admitted for one physical capture.</summary>
public sealed record AdmittedCaptureDevice
{
    public AdmittedCaptureDevice(
        CaptureDeviceRole role,
        CaptureDeviceSelectionMode selectionMode,
        string requestedDeviceId)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        if (!Enum.IsDefined(selectionMode))
            throw new ArgumentOutOfRangeException(nameof(selectionMode));
        if (string.IsNullOrWhiteSpace(requestedDeviceId))
            throw new ArgumentException("A device ID or default alias is required.", nameof(requestedDeviceId));

        var isDefaultAlias = StringComparer.Ordinal.Equals(
            requestedDeviceId,
            AudioDeviceInfo.SystemDefaultDeviceId);
        if (selectionMode == CaptureDeviceSelectionMode.SystemDefault && !isDefaultAlias)
        {
            throw new ArgumentException(
                "A system-default selection must retain the canonical default alias.",
                nameof(requestedDeviceId));
        }
        if (role == CaptureDeviceRole.Webcam && selectionMode != CaptureDeviceSelectionMode.Exact)
        {
            throw new ArgumentException(
                "Webcam admission requires an exact physical device ID.",
                nameof(selectionMode));
        }
        if (selectionMode == CaptureDeviceSelectionMode.Exact &&
            (isDefaultAlias || AudioDeviceSelection.IsSystemDefaultRequest(requestedDeviceId)))
        {
            throw new ArgumentException(
                "An exact selection must retain a non-default opaque device ID.",
                nameof(requestedDeviceId));
        }

        Role = role;
        SelectionMode = selectionMode;
        RequestedDeviceId = requestedDeviceId;
    }

    public CaptureDeviceRole Role { get; }
    public CaptureDeviceSelectionMode SelectionMode { get; }
    public string RequestedDeviceId { get; }

    public static AdmittedCaptureDevice Exact(CaptureDeviceRole role, string deviceId) =>
        new(role, CaptureDeviceSelectionMode.Exact, deviceId);

    public static AdmittedCaptureDevice SystemDefault(CaptureDeviceRole role) =>
        new(role, CaptureDeviceSelectionMode.SystemDefault, AudioDeviceInfo.SystemDefaultDeviceId);
}

/// <summary>
/// Frozen device selections that actually crossed physical capture admission. Disabled roles are
/// represented by absence, never by placeholder entries.
/// </summary>
public sealed class CaptureDeviceManifest
{
    private readonly IReadOnlyList<AdmittedCaptureDevice> _entries;

    public CaptureDeviceManifest(IEnumerable<AdmittedCaptureDevice> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var frozen = entries.ToArray();
        if (frozen.Any(entry => entry is null))
            throw new ArgumentException("A device manifest cannot contain null entries.", nameof(entries));

        var duplicateRole = frozen
            .GroupBy(entry => entry.Role)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicateRole is not null)
        {
            throw new ArgumentException(
                $"A device manifest cannot contain duplicate {duplicateRole.Key} roles.",
                nameof(entries));
        }

        _entries = Array.AsReadOnly(frozen);
    }

    public static CaptureDeviceManifest Empty { get; } = new([]);

    public IReadOnlyList<AdmittedCaptureDevice> Entries => _entries;

    public bool TryGet(CaptureDeviceRole role, out AdmittedCaptureDevice? device)
    {
        device = _entries.SingleOrDefault(entry => entry.Role == role);
        return device is not null;
    }

    public CaptureDeviceManifest Append(AdmittedCaptureDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return new CaptureDeviceManifest([.. _entries, device]);
    }
}
