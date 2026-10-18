namespace WatchDog.Core.Audio;

public sealed record AudioDeviceInfo(
    string DeviceId,
    string DisplayName,
    bool IsDefault,
    bool IsAvailable = true)
{
    public const string SystemDefaultDeviceId = "default";

    public static AudioDeviceInfo SystemDefault { get; } =
        new(SystemDefaultDeviceId, "Default Device", IsDefault: true);
}

public sealed record AudioDeviceSelectionResult(
    IReadOnlyList<AudioDeviceInfo> Devices,
    AudioDeviceInfo SelectedDevice);

/// <summary>Exact endpoint-ID validation and missing-selection preservation shared by capture and UI.</summary>
public static class AudioDeviceSelection
{
    public static bool IsSystemDefaultRequest(string? deviceId) =>
        string.IsNullOrWhiteSpace(deviceId) ||
        StringComparer.OrdinalIgnoreCase.Equals(deviceId, AudioDeviceInfo.SystemDefaultDeviceId);

    public static IReadOnlyList<AudioDeviceInfo> NormalizeAvailableCatalog(
        IEnumerable<AudioDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var normalized = new List<AudioDeviceInfo> { AudioDeviceInfo.SystemDefault };
        var seen = new HashSet<string>(StringComparer.Ordinal)
        {
            AudioDeviceInfo.SystemDefaultDeviceId,
        };

        foreach (var device in devices)
        {
            if (string.IsNullOrWhiteSpace(device.DeviceId) ||
                IsSystemDefaultRequest(device.DeviceId) ||
                !seen.Add(device.DeviceId))
            {
                continue;
            }

            normalized.Add(device with
            {
                IsDefault = false,
                IsAvailable = true,
                DisplayName = string.IsNullOrWhiteSpace(device.DisplayName)
                    ? device.DeviceId
                    : device.DisplayName,
            });
        }

        return normalized;
    }

    public static IReadOnlyList<AudioDeviceInfo> ValidateStrictCatalog(
        IEnumerable<AudioDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var catalog = devices.ToArray();
        if (catalog.Any(device =>
                device is null ||
                string.IsNullOrWhiteSpace(device.DeviceId) ||
                IsSystemDefaultRequest(device.DeviceId)))
        {
            throw new InvalidDataException(
                "The strict audio endpoint catalog contained a malformed or reserved device ID.");
        }

        var duplicate = catalog
            .GroupBy(device => device.DeviceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
            throw new InvalidDataException("The strict audio endpoint catalog contained a duplicate device ID.");

        return NormalizeAvailableCatalog(catalog);
    }

    public static AudioDeviceSelectionResult PreserveSavedSelection(
        IEnumerable<AudioDeviceInfo> availableDevices,
        string? savedDeviceId)
    {
        var devices = NormalizeAvailableCatalog(availableDevices).ToList();
        if (IsSystemDefaultRequest(savedDeviceId))
            return new AudioDeviceSelectionResult(devices, devices[0]);

        var selected = devices.SingleOrDefault(device =>
            device.IsAvailable && StringComparer.Ordinal.Equals(device.DeviceId, savedDeviceId));
        if (selected is not null)
            return new AudioDeviceSelectionResult(devices, selected);

        var unavailable = new AudioDeviceInfo(
            savedDeviceId!,
            $"Unavailable: {savedDeviceId}",
            IsDefault: false,
            IsAvailable: false);
        devices.Add(unavailable);
        return new AudioDeviceSelectionResult(devices, unavailable);
    }

    public static string RequireExactAvailableDevice(
        string? requestedDeviceId,
        IEnumerable<AudioDeviceInfo> availableDevices,
        string direction)
    {
        if (IsSystemDefaultRequest(requestedDeviceId))
            return AudioDeviceInfo.SystemDefaultDeviceId;

        ArgumentNullException.ThrowIfNull(availableDevices);
        var matches = availableDevices
            .Where(device =>
                device.IsAvailable &&
                !device.IsDefault &&
                StringComparer.Ordinal.Equals(device.DeviceId, requestedDeviceId))
            .Take(2)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"The requested {direction} audio endpoint '{requestedDeviceId}' is unavailable or ambiguous.");
        }

        return matches[0].DeviceId;
    }
}
