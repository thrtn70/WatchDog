using ObsKit.NET.Sources;

namespace WatchDog.Core.Webcam;

/// <summary>
/// Reads only the ObsKit Windows DirectShow device catalog. It never constructs a
/// WebcamCapture source and never selects or opens a device.
/// </summary>
public sealed class WindowsWebcamDeviceEnumerator : IWebcamDeviceEnumerator
{
    private readonly Func<IReadOnlyList<WebcamDeviceInfo>> _readCatalog;

    public WindowsWebcamDeviceEnumerator()
        : this(ReadObsKitCatalog)
    {
    }

    internal WindowsWebcamDeviceEnumerator(Func<IReadOnlyList<WebcamDeviceInfo>> readCatalog)
    {
        _readCatalog = readCatalog;
    }

    public IReadOnlyList<WebcamDeviceInfo> GetPhysicalDevicesOrThrow() =>
        WebcamDeviceCatalog.Validate(_readCatalog());

    private static IReadOnlyList<WebcamDeviceInfo> ReadObsKitCatalog()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The DirectShow webcam catalog is available only on Windows.");

        return WebcamCapture.ListDevices(includeVirtualDevices: false)
            .Select(device => new WebcamDeviceInfo(device.DeviceId, device.Name))
            .ToArray();
    }
}

public static class WebcamDeviceCatalog
{
    public static IReadOnlyList<WebcamDeviceInfo> Validate(IEnumerable<WebcamDeviceInfo> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var validated = devices.ToArray();
        if (validated.Any(device =>
                device is null ||
                string.IsNullOrWhiteSpace(device.DeviceId) ||
                string.IsNullOrWhiteSpace(device.DisplayName)))
        {
            throw new InvalidDataException("The physical webcam catalog contained malformed device metadata.");
        }

        var duplicate = validated
            .GroupBy(device => device.DeviceId, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() != 1);
        if (duplicate is not null)
            throw new InvalidDataException("The physical webcam catalog contained a duplicate opaque device ID.");

        return Array.AsReadOnly(validated);
    }

    public static WebcamDeviceInfo RequireExactDevice(
        string deviceId,
        IReadOnlyList<WebcamDeviceInfo> devices)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("An exact opaque webcam device ID is required.", nameof(deviceId));

        var validated = Validate(devices);
        var matches = validated
            .Where(device => StringComparer.Ordinal.Equals(device.DeviceId, deviceId))
            .ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("The exact physical webcam device is unavailable or ambiguous.");
        return matches[0];
    }
}
