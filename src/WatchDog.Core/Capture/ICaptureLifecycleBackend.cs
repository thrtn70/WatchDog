using WatchDog.Core.Storage;
using WatchDog.Core.Devices;

namespace WatchDog.Core.Capture;

/// <summary>
/// Physical capture operations consumed by <see cref="CaptureLifecycleCoordinator"/>.
/// Every operation must return an explicit success or structured failure.
/// </summary>
public interface ICaptureLifecycleBackend
{
    /// <summary>Pure preflight before replacing an existing capture; must not acquire resources.</summary>
    CaptureFailure? ValidateStart(CaptureRequest request) => null;

    Task<CaptureBackendStartResult> StartAsync(
        CaptureSessionContext context,
        CancellationToken cancellationToken);

    Task<CaptureBackendCommandResult> StopAsync(
        CaptureSessionContext context,
        CancellationToken cancellationToken);

    Task<CaptureBackendSaveResult> SaveAsync(
        CaptureSessionContext context,
        CancellationToken cancellationToken);
}

public sealed record CaptureBackendStartResult(
    bool Succeeded,
    CaptureTarget? ActualTarget,
    CaptureFailure? Failure,
    bool CleanupRequired = false)
{
    public CaptureDeviceManifest AdmittedDevices { get; init; } = CaptureDeviceManifest.Empty;

    public static CaptureBackendStartResult Success(
        CaptureTarget actualTarget,
        CaptureDeviceManifest? admittedDevices = null) =>
        new(true, actualTarget, null)
        {
            AdmittedDevices = admittedDevices ?? CaptureDeviceManifest.Empty,
        };

    public static CaptureBackendStartResult Failed(CaptureFailure failure) =>
        new(false, null, failure, false);

    /// <summary>
    /// Reports a failed start whose physical ownership could not be cleaned up. The exact
    /// target is required so the coordinator can retain a truthful cleanup context.
    /// </summary>
    public static CaptureBackendStartResult Failed(
        CaptureFailure failure,
        CaptureTarget ownedActualTarget) =>
        new(false, ownedActualTarget, failure, true);
}

public sealed record CaptureBackendCommandResult(
    bool Succeeded,
    CaptureFailure? Failure,
    bool CleanupRequired = false)
{
    public static CaptureBackendCommandResult Success() => new(true, null, false);

    public static CaptureBackendCommandResult Failed(
        CaptureFailure failure,
        bool cleanupRequired = true) =>
        new(false, failure, cleanupRequired);
}

public sealed record CaptureBackendSaveResult(
    bool Succeeded,
    string? OutputPath,
    CaptureFailure? Failure,
    CompletedMediaReceipt? Receipt = null)
{
    public static CaptureBackendSaveResult Success(
        string outputPath,
        CompletedMediaReceipt? receipt = null) => new(true, outputPath, null, receipt);

    public static CaptureBackendSaveResult Failed(
        CaptureFailure failure,
        string? outputPath = null,
        CompletedMediaReceipt? receipt = null) => new(false, outputPath, failure, receipt);
}
