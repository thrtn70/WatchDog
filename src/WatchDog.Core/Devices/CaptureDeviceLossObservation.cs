using WatchDog.Core.Audio;

namespace WatchDog.Core.Devices;

public enum CaptureDeviceClosedReason
{
    ExactSelectionUnavailable,
    CatalogUnavailable,
    CatalogInvalid,
    DefaultUnavailable,
    DefaultChanged,
}

/// <summary>
/// Correlated metadata-only evidence that an admitted device identity can no longer be verified.
/// This contract never carries sample/frame, busy, or permission claims.
/// </summary>
public sealed record CaptureDeviceLossObservation
{
    public CaptureDeviceLossObservation(
        Guid sessionId,
        Guid requestId,
        long generation,
        CaptureDeviceRole role,
        CaptureDeviceSelectionMode selectionMode,
        string requestedDeviceId,
        DateTimeOffset observedAt,
        CaptureDeviceClosedReason reason,
        WindowsAudioEndpointRole? windowsDefaultRole = null,
        string? oldWindowsDefaultEndpointId = null,
        string? newWindowsDefaultEndpointId = null)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("A session ID is required.", nameof(sessionId));
        if (requestId == Guid.Empty) throw new ArgumentException("A request ID is required.", nameof(requestId));
        if (generation <= 0) throw new ArgumentOutOfRangeException(nameof(generation));
        if (!Enum.IsDefined(role)) throw new ArgumentOutOfRangeException(nameof(role));
        if (!Enum.IsDefined(selectionMode)) throw new ArgumentOutOfRangeException(nameof(selectionMode));
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));

        _ = new AdmittedCaptureDevice(role, selectionMode, requestedDeviceId);

        var defaultReason = reason is CaptureDeviceClosedReason.DefaultUnavailable or
            CaptureDeviceClosedReason.DefaultChanged;
        if (defaultReason != windowsDefaultRole.HasValue)
        {
            throw new ArgumentException(
                "Windows default-role metadata is required only for default-unavailable/default-changed observations.",
                nameof(windowsDefaultRole));
        }
        if (windowsDefaultRole.HasValue && !Enum.IsDefined(windowsDefaultRole.Value))
            throw new ArgumentOutOfRangeException(nameof(windowsDefaultRole));
        if (defaultReason && selectionMode != CaptureDeviceSelectionMode.SystemDefault)
            throw new ArgumentException("Default metadata reasons require a system-default selection.", nameof(reason));
        if (reason == CaptureDeviceClosedReason.ExactSelectionUnavailable &&
            selectionMode != CaptureDeviceSelectionMode.Exact)
        {
            throw new ArgumentException(
                "An exact-selection-unavailable reason requires an exact selection.",
                nameof(reason));
        }

        var changed = reason == CaptureDeviceClosedReason.DefaultChanged;
        if (changed &&
            (string.IsNullOrWhiteSpace(oldWindowsDefaultEndpointId) ||
             string.IsNullOrWhiteSpace(newWindowsDefaultEndpointId) ||
             AudioDeviceSelection.IsSystemDefaultRequest(oldWindowsDefaultEndpointId) ||
             AudioDeviceSelection.IsSystemDefaultRequest(newWindowsDefaultEndpointId) ||
             StringComparer.Ordinal.Equals(oldWindowsDefaultEndpointId, newWindowsDefaultEndpointId)))
        {
            throw new ArgumentException("A default change requires distinct old and new endpoint IDs.", nameof(reason));
        }
        if (!changed && (oldWindowsDefaultEndpointId is not null || newWindowsDefaultEndpointId is not null))
        {
            throw new ArgumentException(
                "Old/new Windows default IDs are carried only for a known default change.",
                nameof(reason));
        }

        SessionId = sessionId;
        RequestId = requestId;
        Generation = generation;
        Role = role;
        SelectionMode = selectionMode;
        RequestedDeviceId = requestedDeviceId;
        ObservedAt = observedAt;
        Reason = reason;
        WindowsDefaultRole = windowsDefaultRole;
        OldWindowsDefaultEndpointId = oldWindowsDefaultEndpointId;
        NewWindowsDefaultEndpointId = newWindowsDefaultEndpointId;
    }

    public Guid SessionId { get; }
    public Guid RequestId { get; }
    public long Generation { get; }
    public CaptureDeviceRole Role { get; }
    public CaptureDeviceSelectionMode SelectionMode { get; }
    public string RequestedDeviceId { get; }
    public DateTimeOffset ObservedAt { get; }
    public CaptureDeviceClosedReason Reason { get; }
    public WindowsAudioEndpointRole? WindowsDefaultRole { get; }
    public string? OldWindowsDefaultEndpointId { get; }
    public string? NewWindowsDefaultEndpointId { get; }
}
