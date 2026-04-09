using System.Management;
using Microsoft.Extensions.Logging;

namespace TikrClipr.Core.Audio;

/// <summary>
/// Enumerates audio devices using WMI (System.Management).
/// Falls back to a "Default" entry if enumeration fails.
/// </summary>
public sealed class WindowsAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly ILogger<WindowsAudioDeviceEnumerator> _logger;

    public WindowsAudioDeviceEnumerator(ILogger<WindowsAudioDeviceEnumerator> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>
        {
            new("default", "Default Device", true)
        };

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_SoundDevice WHERE Status = 'OK'");

            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                var deviceId = obj["DeviceID"]?.ToString();

                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(deviceId))
                {
                    devices.Add(new AudioDeviceInfo(deviceId, name, false));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate audio output devices via WMI");
        }

        return devices;
    }

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        // WMI Win32_SoundDevice does not distinguish input vs output devices.
        // OBS handles direction filtering internally when the device ID is passed
        // to AudioInputCapture vs AudioOutputCapture. Return the same device list
        // so the user can select any device; invalid selections will be rejected
        // by OBS at runtime and fall back to the default device.
        return GetOutputDevices();
    }
}
