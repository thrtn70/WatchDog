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
        var devices = new List<AudioDeviceInfo>
        {
            new("default", "Default Device", true)
        };

        try
        {
            // WMI Win32_SoundDevice includes both input and output devices.
            // For a more accurate split, we'd need Core Audio API.
            // For now, return the same list — OBS will filter by device type.
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
            _logger.LogDebug(ex, "Failed to enumerate audio input devices via WMI");
        }

        return devices;
    }
}
