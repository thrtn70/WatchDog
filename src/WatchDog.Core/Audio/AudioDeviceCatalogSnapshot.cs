namespace WatchDog.Core.Audio;

public enum AudioEndpointDirection
{
    Output,
    Input,
}

/// <summary>The explicit Windows default-endpoint role observed with a strict catalog.</summary>
public enum WindowsAudioEndpointRole
{
    Console,
    Multimedia,
    Communications,
}

/// <summary>
/// One strict metadata snapshot. The default endpoint is Windows metadata only and does not prove
/// which endpoint an already-created OBS default source is currently bound to.
/// </summary>
public sealed class AudioDeviceCatalogSnapshot
{
    private AudioDeviceCatalogSnapshot(
        AudioEndpointDirection direction,
        WindowsAudioEndpointRole windowsDefaultRole,
        IReadOnlyList<AudioDeviceInfo> activeDevices,
        string? windowsDefaultEndpointId)
    {
        Direction = direction;
        WindowsDefaultRole = windowsDefaultRole;
        ActiveDevices = activeDevices;
        WindowsDefaultEndpointId = windowsDefaultEndpointId;
    }

    public AudioEndpointDirection Direction { get; }
    public WindowsAudioEndpointRole WindowsDefaultRole { get; }
    public IReadOnlyList<AudioDeviceInfo> ActiveDevices { get; }
    public string? WindowsDefaultEndpointId { get; }

    public static AudioDeviceCatalogSnapshot CreateValidated(
        AudioEndpointDirection direction,
        WindowsAudioEndpointRole windowsDefaultRole,
        IEnumerable<AudioDeviceInfo> activeDevices,
        string? windowsDefaultEndpointId)
    {
        if (!Enum.IsDefined(direction))
            throw new InvalidDataException("The strict audio snapshot named an invalid endpoint direction.");
        if (!Enum.IsDefined(windowsDefaultRole))
            throw new InvalidDataException("The strict audio snapshot named an invalid Windows default role.");
        if (activeDevices is null)
            throw new InvalidDataException("The strict audio snapshot returned a null active catalog.");

        var catalog = activeDevices.ToArray();
        if (catalog.Any(device =>
                device is null ||
                string.IsNullOrWhiteSpace(device.DeviceId) ||
                string.IsNullOrWhiteSpace(device.DisplayName) ||
                AudioDeviceSelection.IsSystemDefaultRequest(device.DeviceId) ||
                device.IsDefault ||
                !device.IsAvailable))
        {
            throw new InvalidDataException(
                "The strict audio snapshot contained malformed or non-active physical endpoint metadata.");
        }

        var duplicate = catalog
            .GroupBy(device => device.DeviceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
            throw new InvalidDataException("The strict audio snapshot contained a duplicate endpoint ID.");

        if (windowsDefaultEndpointId is not null &&
            (string.IsNullOrWhiteSpace(windowsDefaultEndpointId) ||
             catalog.Count(device => StringComparer.Ordinal.Equals(
                 device.DeviceId,
                 windowsDefaultEndpointId)) != 1))
        {
            throw new InvalidDataException(
                "The Windows default endpoint ID was not present exactly once in the strict active catalog.");
        }

        return new AudioDeviceCatalogSnapshot(
            direction,
            windowsDefaultRole,
            Array.AsReadOnly(catalog),
            windowsDefaultEndpointId);
    }
}
