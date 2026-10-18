namespace WatchDog.Core.Devices;

/// <summary>The bounded capture-device roles admitted by the recording pipeline.</summary>
public enum CaptureDeviceRole
{
    DesktopAudio,
    Microphone,
    Webcam,
}

/// <summary>The device-owned startup stage that failed.</summary>
public enum CaptureDeviceStartupStage
{
    Catalog,
    Selection,
    Binding,
    Readiness,
    Start,
}

/// <summary>
/// Evidence-bounded failure reasons. Unknown deliberately avoids inferring device busy or
/// permission denial from a generic native failure.
/// </summary>
public enum CaptureDeviceStartupReason
{
    CatalogUnavailable,
    InvalidCatalog,
    ExactSelectionUnavailable,
    IdentityMismatch,
    Unknown,
}

/// <summary>
/// Preserves the device role and startup seam while retaining the original exception for logs.
/// </summary>
public sealed class CaptureDeviceStartupException : Exception
{
    public CaptureDeviceStartupException(
        CaptureDeviceRole role,
        CaptureDeviceStartupStage stage,
        CaptureDeviceStartupReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(role))
            throw new ArgumentOutOfRangeException(nameof(role));
        if (!Enum.IsDefined(stage))
            throw new ArgumentOutOfRangeException(nameof(stage));
        if (!Enum.IsDefined(reason))
            throw new ArgumentOutOfRangeException(nameof(reason));
        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("A bounded device startup failure message is required.", nameof(message));

        Role = role;
        Stage = stage;
        Reason = reason;
    }

    public CaptureDeviceRole Role { get; }
    public CaptureDeviceStartupStage Stage { get; }
    public CaptureDeviceStartupReason Reason { get; }

    public static CaptureDeviceStartupException? Find(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current))
                continue;
            if (current is CaptureDeviceStartupException deviceFailure)
                return deviceFailure;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.Reverse())
                    pending.Push(inner);
            }
            else if (current.InnerException is { } inner)
            {
                pending.Push(inner);
            }
        }

        return null;
    }
}
