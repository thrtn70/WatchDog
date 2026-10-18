using WatchDog.Core.Capture;

namespace WatchDog.Core.Runtime;

public enum ShutdownDrainFailureStage
{
    Capture,
    HostStop,
    HostDispose,
}

public sealed record ShutdownStepResult(
    bool Succeeded,
    string? FailureMessage = null,
    string? WarningMessage = null,
    CaptureFailure? CaptureDiagnostic = null)
{
    public Exception? Exception { get; private init; }

    public static ShutdownStepResult Success(
        string? warningMessage = null,
        CaptureFailure? captureDiagnostic = null) =>
        new(true, WarningMessage: warningMessage, CaptureDiagnostic: captureDiagnostic);

    public static ShutdownStepResult Failed(
        string message,
        CaptureFailure? captureDiagnostic = null,
        string? warningMessage = null) =>
        new(false, message, warningMessage, captureDiagnostic);

    public static ShutdownStepResult Failed(
        Exception exception,
        CaptureFailure? captureDiagnostic = null,
        string? warningMessage = null) =>
        new(false, exception.Message, warningMessage, captureDiagnostic) { Exception = exception };
}

public sealed record ApplicationShutdownDrainResult(
    bool Succeeded,
    ShutdownDrainFailureStage? FailedStage = null,
    string? FailureMessage = null,
    Exception? Exception = null,
    string? WarningMessage = null,
    CaptureFailure? CaptureDiagnostic = null);

/// <summary>
/// Orders application shutdown without depending on a UI dispatcher or a concrete host.
/// The capture delegate is invoked synchronously before the first await so it can close
/// admission while startup finishes unwinding.
/// </summary>
public sealed class ApplicationShutdownDrain
{
    private readonly Func<Task<ShutdownStepResult>?> _beginCaptureShutdown;
    private readonly Func<Task> _waitForStartup;
    private readonly Func<Task> _stopHost;
    private readonly Func<ValueTask> _disposeHost;

    public ApplicationShutdownDrain(
        Func<Task<ShutdownStepResult>?> beginCaptureShutdown,
        Func<Task> waitForStartup,
        Func<Task> stopHost,
        Action disposeHost)
        : this(
            beginCaptureShutdown,
            waitForStartup,
            stopHost,
            () =>
            {
                disposeHost();
                return ValueTask.CompletedTask;
            })
    {
    }

    public ApplicationShutdownDrain(
        Func<Task<ShutdownStepResult>?> beginCaptureShutdown,
        Func<Task> waitForStartup,
        Func<Task> stopHost,
        Func<ValueTask> disposeHost)
    {
        _beginCaptureShutdown = beginCaptureShutdown;
        _waitForStartup = waitForStartup;
        _stopHost = stopHost;
        _disposeHost = disposeHost;
    }

    public async Task<ApplicationShutdownDrainResult> DrainAsync()
    {
        string? captureWarning = null;
        CaptureFailure? captureDiagnostic = null;
        Task<ShutdownStepResult>? captureShutdown;
        try
        {
            captureShutdown = _beginCaptureShutdown();
        }
        catch (Exception ex)
        {
            return Failure(ShutdownDrainFailureStage.Capture, ex);
        }

        try
        {
            await _waitForStartup();
        }
        catch
        {
            // Startup failure is handled by the application. Capture and any partially
            // started host still need the same ordered drain.
        }

        if (captureShutdown is not null)
        {
            ShutdownStepResult captureResult;
            try
            {
                captureResult = await captureShutdown;
            }
            catch (Exception ex)
            {
                return Failure(ShutdownDrainFailureStage.Capture, ex);
            }

            if (!captureResult.Succeeded)
            {
                return new ApplicationShutdownDrainResult(
                    false,
                    ShutdownDrainFailureStage.Capture,
                    captureResult.FailureMessage ?? "Recording could not finish safely.",
                    captureResult.Exception,
                    captureResult.WarningMessage,
                    captureResult.CaptureDiagnostic);
            }

            captureWarning = captureResult.WarningMessage;
            captureDiagnostic = captureResult.CaptureDiagnostic;
        }

        try
        {
            await _stopHost();
        }
        catch (Exception ex)
        {
            return Failure(ShutdownDrainFailureStage.HostStop, ex, captureWarning, captureDiagnostic);
        }

        try
        {
            await _disposeHost();
        }
        catch (Exception ex)
        {
            return Failure(ShutdownDrainFailureStage.HostDispose, ex, captureWarning, captureDiagnostic);
        }

        return new ApplicationShutdownDrainResult(
            true, WarningMessage: captureWarning, CaptureDiagnostic: captureDiagnostic);
    }

    private static ApplicationShutdownDrainResult Failure(
        ShutdownDrainFailureStage stage,
        Exception exception,
        string? warningMessage = null,
        CaptureFailure? captureDiagnostic = null) =>
        new(false, stage, exception.Message, exception, warningMessage, captureDiagnostic);
}
