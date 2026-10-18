using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;

namespace WatchDog.Core.Audio;

/// <summary>
/// Enumerates direction-specific Windows MMDevice endpoints without constructing OBS sources.
/// MMDevice endpoint IDs are the exact IDs consumed by OBS WASAPI FromDevice bindings.
/// </summary>
public sealed class WindowsAudioDeviceEnumerator : IAudioDeviceEnumerator
{
    private readonly Func<AudioEndpointDirection, IReadOnlyList<AudioDeviceInfo>> _readDevices;
    private readonly Func<AudioEndpointDirection, WindowsAudioEndpointRole, string?> _readDefaultEndpointId;
    private readonly ILogger<WindowsAudioDeviceEnumerator> _logger;

    public WindowsAudioDeviceEnumerator(ILogger<WindowsAudioDeviceEnumerator> logger)
        : this(ReadWindowsEndpoints, ReadWindowsDefaultEndpointId, logger)
    {
    }

    internal WindowsAudioDeviceEnumerator(
        Func<AudioEndpointDirection, IReadOnlyList<AudioDeviceInfo>> readDevices,
        ILogger<WindowsAudioDeviceEnumerator> logger)
        : this(readDevices, (_, _) => null, logger)
    {
    }

    internal WindowsAudioDeviceEnumerator(
        Func<AudioEndpointDirection, IReadOnlyList<AudioDeviceInfo>> readDevices,
        Func<AudioEndpointDirection, WindowsAudioEndpointRole, string?> readDefaultEndpointId,
        ILogger<WindowsAudioDeviceEnumerator> logger)
    {
        _readDevices = readDevices;
        _readDefaultEndpointId = readDefaultEndpointId;
        _logger = logger;
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() =>
        ReadCatalog(AudioEndpointDirection.Output);

    public IReadOnlyList<AudioDeviceInfo> GetInputDevices() =>
        ReadCatalog(AudioEndpointDirection.Input);

    /// <summary>
    /// Enumerates output endpoints without converting catalog failures into the default alias.
    /// Intended for qualification and diagnostics that must distinguish failure from an empty catalog.
    /// </summary>
    public IReadOnlyList<AudioDeviceInfo> GetOutputDevicesOrThrow() =>
        ReadCatalogOrThrow(AudioEndpointDirection.Output);

    /// <summary>
    /// Enumerates input endpoints without converting catalog failures into the default alias.
    /// Intended for qualification and diagnostics that must distinguish failure from an empty catalog.
    /// </summary>
    public IReadOnlyList<AudioDeviceInfo> GetInputDevicesOrThrow() =>
        ReadCatalogOrThrow(AudioEndpointDirection.Input);

    public AudioDeviceCatalogSnapshot GetOutputSnapshotOrThrow() =>
        ReadSnapshotOrThrow(AudioEndpointDirection.Output);

    public AudioDeviceCatalogSnapshot GetInputSnapshotOrThrow() =>
        ReadSnapshotOrThrow(AudioEndpointDirection.Input);

    private IReadOnlyList<AudioDeviceInfo> ReadCatalog(AudioEndpointDirection direction)
    {
        try
        {
            return AudioDeviceSelection.NormalizeAvailableCatalog(_readDevices(direction));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate Windows {Direction} audio endpoints", direction);
            return [AudioDeviceInfo.SystemDefault];
        }
    }

    private IReadOnlyList<AudioDeviceInfo> ReadCatalogOrThrow(AudioEndpointDirection direction) =>
        AudioDeviceSelection.ValidateStrictCatalog(_readDevices(direction));

    private AudioDeviceCatalogSnapshot ReadSnapshotOrThrow(AudioEndpointDirection direction)
    {
        const WindowsAudioEndpointRole role = WindowsAudioEndpointRole.Multimedia;
        return AudioDeviceCatalogSnapshot.CreateValidated(
            direction,
            role,
            _readDevices(direction),
            _readDefaultEndpointId(direction, role));
    }

    private static IReadOnlyList<AudioDeviceInfo> ReadWindowsEndpoints(AudioEndpointDirection direction)
    {
        using var enumerator = new MMDeviceEnumerator();
        var flow = direction == AudioEndpointDirection.Output
            ? DataFlow.Render
            : DataFlow.Capture;
        var result = new List<AudioDeviceInfo>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                result.Add(new AudioDeviceInfo(
                    device.ID,
                    device.FriendlyName,
                    IsDefault: false));
            }
        }
        return result;
    }

    private static string? ReadWindowsDefaultEndpointId(
        AudioEndpointDirection direction,
        WindowsAudioEndpointRole role)
    {
        using var enumerator = new MMDeviceEnumerator();
        var flow = direction == AudioEndpointDirection.Output ? DataFlow.Render : DataFlow.Capture;
        var nativeRole = role switch
        {
            WindowsAudioEndpointRole.Console => Role.Console,
            WindowsAudioEndpointRole.Multimedia => Role.Multimedia,
            WindowsAudioEndpointRole.Communications => Role.Communications,
            _ => throw new ArgumentOutOfRangeException(nameof(role)),
        };
        try
        {
            using var device = enumerator.GetDefaultAudioEndpoint(flow, nativeRole);
            return device.ID;
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80070490))
        {
            return null;
        }
    }
}
