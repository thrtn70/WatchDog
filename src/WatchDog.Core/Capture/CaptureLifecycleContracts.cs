using WatchDog.Core.Webcam;
using WatchDog.Core.Storage;
using WatchDog.Core.Devices;

namespace WatchDog.Core.Capture;

public enum CaptureInitiation
{
    ConfiguredStartup,
    Automatic,
    Manual,
}

public abstract record CaptureTarget;

public sealed record GameCaptureTarget(
    string GameId,
    int ProcessId,
    string ExecutableName,
    DateTimeOffset? ProcessStartedAt = null,
    string? DisplayName = null) : CaptureTarget;

public sealed record WindowCaptureTarget(
    int ProcessId,
    IntPtr WindowHandle,
    string? WindowTitle = null,
    string? ExecutableName = null,
    string? WindowClass = null) : CaptureTarget;

public sealed record DisplayCaptureTarget(
    string StableDisplayId,
    string? DisplayName = null,
    int? MonitorIndex = null) : CaptureTarget;

/// <summary>
/// A rectangle in physical pixels in the Windows virtual-desktop coordinate space.
/// Negative origins are valid for displays positioned left of or above the primary display.
/// </summary>
public readonly record struct PhysicalCaptureBounds(int X, int Y, int Width, int Height);

/// <summary>
/// An exact physical-pixel region wholly owned by one display. The display bounds are frozen
/// when the target is resolved so a moved or resized display fails closed instead of changing
/// which pixels are captured.
/// </summary>
public sealed record RegionCaptureTarget(
    string OwningDisplayId,
    PhysicalCaptureBounds PhysicalBounds,
    PhysicalCaptureBounds OwningDisplayBounds) : CaptureTarget;

public sealed record CaptureRequest
{
    public CaptureRequest(
        Guid requestId,
        DateTimeOffset requestedAt,
        CaptureInitiation initiation,
        CaptureTarget target,
        WebcamCaptureSelection? webcam = null,
        CaptureStartPolicy? startPolicy = null,
        CaptureStartupExpectation? startupExpectation = null)
    {
        if (webcam is not null && initiation != CaptureInitiation.Manual)
        {
            throw new ArgumentException(
                "A webcam may be authorized only for an explicitly initiated manual recording.",
                nameof(webcam));
        }

        RequestId = requestId;
        RequestedAt = requestedAt;
        Initiation = initiation;
        Target = target;
        Webcam = webcam;
        StartPolicy = startPolicy;
        StartupExpectation = startupExpectation;
    }

    public Guid RequestId { get; }
    public DateTimeOffset RequestedAt { get; }
    public CaptureInitiation Initiation { get; }
    public CaptureTarget Target { get; }
    public WebcamCaptureSelection? Webcam { get; }
    public CaptureStartPolicy? StartPolicy { get; }
    public CaptureStartupExpectation? StartupExpectation { get; }
}

public sealed record CaptureSessionContext(
    Guid SessionId,
    long Generation,
    CaptureRequest Request,
    CaptureTarget RequestedTarget,
    CaptureTarget ActualTarget)
{
    public CaptureDeviceManifest AdmittedDevices { get; init; } = CaptureDeviceManifest.Empty;
}

public enum CaptureFailureKind
{
    InvalidRequest,
    AdmissionClosed,
    ManualPriority,
    Superseded,
    InvalidState,
    Backend,
    SourceLost,
    Liveness,
    Storage,
    Unsupported,
    Cancelled,
    Unexpected,
}

public sealed record CaptureFailure(
    CaptureFailureKind Kind,
    string Code,
    string Message,
    bool IsRetryable = false)
{
    public const string WebcamViewReleaseUncertainCode = "capture.webcam.view-release-uncertain";

    public CaptureFailure? CleanupFailure { get; private init; }

    public bool HasUncertainWebcamViewRelease =>
        IsUncertainWebcamViewReleaseNode(this) ||
        IsUncertainWebcamViewReleaseNode(CleanupFailure);

    public static bool IsUncertainWebcamViewReleaseNode(CaptureFailure? failure) =>
        failure is not null &&
        failure.Kind == CaptureFailureKind.Backend &&
        string.Equals(failure.Code, WebcamViewReleaseUncertainCode, StringComparison.Ordinal);

    public CaptureFailure WithCleanupFailure(CaptureFailure? cleanupFailure)
    {
        if (cleanupFailure is null)
            return this;

        var existingLeaf = NormalizeLeaf(CleanupFailure);
        var incomingLeaf = NormalizeLeaf(cleanupFailure.CleanupFailure);
        CaptureFailure selectedLeaf;
        if (IsUncertainWebcamViewReleaseNode(existingLeaf))
        {
            selectedLeaf = existingLeaf!;
        }
        else if (IsUncertainWebcamViewReleaseNode(cleanupFailure))
        {
            selectedLeaf = NormalizeLeaf(cleanupFailure)!;
        }
        else if (IsUncertainWebcamViewReleaseNode(incomingLeaf))
        {
            selectedLeaf = incomingLeaf!;
        }
        else
        {
            selectedLeaf = existingLeaf ?? incomingLeaf ?? NormalizeLeaf(cleanupFailure)!;
        }

        var isRetryable = IsRetryable &&
                          !IsUncertainWebcamViewReleaseNode(this) &&
                          !IsUncertainWebcamViewReleaseNode(selectedLeaf);
        if (ReferenceEquals(selectedLeaf, CleanupFailure) && isRetryable == IsRetryable)
            return this;

        return this with
        {
            IsRetryable = isRetryable,
            CleanupFailure = selectedLeaf,
        };
    }

    private static CaptureFailure? NormalizeLeaf(CaptureFailure? failure) =>
        failure?.CleanupFailure is null
            ? failure
            : failure with { CleanupFailure = null };
}

public enum CaptureLifecycleStatus
{
    Idle,
    Starting,
    Active,
    Saving,
    Stopping,
    Faulted,
    Shutdown,
}

public enum CaptureReadiness
{
    Available,
    Busy,
    Closed,
}

public enum CaptureHealth
{
    Healthy,
    Degraded,
    Failed,
}

public sealed record CaptureLifecycleSnapshot(
    CaptureLifecycleStatus Status,
    CaptureReadiness Readiness,
    CaptureHealth Health,
    CaptureSessionContext? Context,
    CaptureFailure? LastFailure)
{
    public static CaptureLifecycleSnapshot Initial { get; } = new(
        CaptureLifecycleStatus.Idle,
        CaptureReadiness.Available,
        CaptureHealth.Healthy,
        null,
        null);
}

public enum CaptureOperationKind
{
    Start,
    Switch,
    Stop,
    SourceLost,
    DeviceLost,
    LivenessFailed,
    StoragePressure,
    HealthChanged,
    Pause,
    Resume,
    Split,
    Shutdown,
}

public enum CaptureCommandDisposition
{
    Completed,
    Rejected,
    Failed,
    Ignored,
    Unsupported,
}

public sealed record CaptureTransitionResult(
    Guid OperationId,
    CaptureOperationKind Operation,
    CaptureCommandDisposition Disposition,
    CaptureLifecycleSnapshot Before,
    CaptureLifecycleSnapshot After,
    CaptureFailure? Failure)
{
    public bool Succeeded => Disposition == CaptureCommandDisposition.Completed;
}

public sealed record CaptureSaveResult(
    Guid OperationId,
    CaptureCommandDisposition Disposition,
    CaptureSessionContext? Context,
    string? OutputPath,
    CaptureFailure? Failure,
    CompletedMediaReceipt? Receipt = null)
{
    public bool Saved => Disposition == CaptureCommandDisposition.Completed && OutputPath is not null;
}

public sealed record CaptureSourceLoss(
    Guid SessionId,
    long Generation,
    CaptureTarget Target,
    DateTimeOffset ObservedAt);

/// <summary>
/// A low-space observation correlated to the capture that was active when the storage
/// owner measured it. Null identity is valid only when no capture was active.
/// </summary>
public sealed record CaptureStoragePressure(
    Guid? SessionId,
    long? Generation,
    DateTimeOffset ObservedAt,
    string Reason);
