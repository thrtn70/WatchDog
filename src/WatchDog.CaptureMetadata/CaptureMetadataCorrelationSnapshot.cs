namespace WatchDog.CaptureMetadata;

public sealed class CaptureMetadataCorrelationSnapshot
{
    private readonly IReadOnlyList<CaptureDeviceSelection> _deviceSelections;

    public CaptureMetadataCorrelationSnapshot(
        Guid sessionId,
        Guid requestId,
        long generation,
        ResolvedCaptureTarget requestedTarget,
        ResolvedCaptureTarget actualTarget,
        IEnumerable<CaptureDeviceSelection> deviceSelections)
    {
        if (sessionId == Guid.Empty)
            throw new ArgumentException("A nonempty session ID is required.", nameof(sessionId));
        if (requestId == Guid.Empty)
            throw new ArgumentException("A nonempty request ID is required.", nameof(requestId));
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        ArgumentNullException.ThrowIfNull(requestedTarget);
        ArgumentNullException.ThrowIfNull(actualTarget);
        ArgumentNullException.ThrowIfNull(deviceSelections);

        var frozenSelections = deviceSelections.ToArray();
        var roles = new HashSet<CaptureDeviceRole>();
        foreach (var selection in frozenSelections)
        {
            if (selection is null)
                throw new ArgumentException("Device selections cannot contain null.", nameof(deviceSelections));
            if (!roles.Add(selection.Role))
                throw new ArgumentException("Each device role may be selected at most once.", nameof(deviceSelections));
        }

        SessionId = sessionId;
        RequestId = requestId;
        Generation = generation;
        RequestedTarget = requestedTarget;
        ActualTarget = actualTarget;
        _deviceSelections = Array.AsReadOnly(frozenSelections);
    }

    public Guid SessionId { get; }
    public Guid RequestId { get; }
    public long Generation { get; }
    public ResolvedCaptureTarget RequestedTarget { get; }
    public ResolvedCaptureTarget ActualTarget { get; }
    public IReadOnlyList<CaptureDeviceSelection> DeviceSelections => _deviceSelections;

    public override string ToString() =>
        "CaptureMetadataCorrelationSnapshot { Correlation = [redacted], Targets = [redacted], Devices = [redacted] }";
}
