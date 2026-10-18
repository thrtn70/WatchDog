using WatchDog.Core.Runtime;

namespace WatchDog.Core.Capture;

/// <summary>
/// Fixed, user-facing failure descriptions. CaptureFailure.Code and Message may contain
/// backend paths, device identifiers, or exception details and must stay out of UI text.
/// The original failure remains available to the existing diagnostic owner.
/// </summary>
public static class CaptureFailurePresentation
{
    private const string UncertainWebcamCleanupText =
        "Webcam cleanup could not be confirmed. The camera may still be active, and recording and normal Exit remain blocked. " +
        "Repeating Stop or Exit cannot resolve this retained state. No supported in-app recovery is available.";

    private const string RecordingCleanupFailureText =
        "Recording cleanup did not complete. Exit remains blocked. Check recording status before trying Exit again.";

    public static string SafeText(CaptureFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.HasUncertainWebcamViewRelease)
            return UncertainWebcamCleanupText;

        // Exact local codes may keep more useful guidance; never render a code itself.
        if (failure.Kind == CaptureFailureKind.Unexpected)
        {
            var local = failure.Code switch
            {
                "capture.manual.selection-invalid" =>
                    "The selected window or capture settings are invalid. Review the selection and Settings.",
                "capture.manual.start-unexpected" =>
                    "The selected window could not be started. Check capture status before retrying.",
                "capture.manual.stop-unexpected" =>
                    "Stop did not complete. Check whether cleanup remains owned before retrying.",
                _ => null,
            };
            if (local is not null) return local;
        }
        return failure.Kind switch
        {
            CaptureFailureKind.InvalidRequest =>
                "Capture request was rejected. Review the selected source and settings.",
            CaptureFailureKind.AdmissionClosed =>
                "Capture admission is closed. Restart WatchDog before recording again.",
            CaptureFailureKind.ManualPriority =>
                "Manual recording has priority. Stop it before starting another capture.",
            CaptureFailureKind.Superseded =>
                "Capture request was superseded. Select the current source again.",
            CaptureFailureKind.InvalidState =>
                "Capture is busy or cleanup is pending. Check capture status before retrying.",
            CaptureFailureKind.SourceLost =>
                "The exact capture source is unavailable. Check capture status before selecting it again.",
            CaptureFailureKind.Liveness =>
                "Recording output may have stalled. Check capture status before retrying.",
            CaptureFailureKind.Storage =>
                "Recording storage needs attention. Check Save Path and available space.",
            CaptureFailureKind.Unsupported =>
                "This capture action is unavailable in the current configuration.",
            CaptureFailureKind.Cancelled =>
                "The capture action was cancelled.",
            _ => "Recording needs attention. Check capture status before retrying.",
        };
    }

    public static string SafeShutdownFailureText(ApplicationShutdownDrainResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.FailedStage switch
        {
            ShutdownDrainFailureStage.HostStop =>
                "WatchDog could not stop its background services. Exit did not complete.",
            ShutdownDrainFailureStage.HostDispose =>
                "WatchDog could not finish closing its background services. Exit did not complete.",
            ShutdownDrainFailureStage.Capture when
                result.CaptureDiagnostic?.HasUncertainWebcamViewRelease == true =>
                UncertainWebcamCleanupText,
            ShutdownDrainFailureStage.Capture => RecordingCleanupFailureText,
            _ => "WatchDog could not finish closing. Exit remains blocked.",
        };
    }

    public static string SafeShutdownWarningText(ApplicationShutdownDrainResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.CaptureDiagnostic?.HasUncertainWebcamViewRelease == true
            ? UncertainWebcamCleanupText
            : "Some recording output could not be verified. Check the recording files.";
    }

    public static string SafeSessionEndingText(bool isSignOut) =>
        isSignOut
            ? "WatchDog canceled this sign-out request while checking recording cleanup. " +
              "Exit remains blocked if cleanup cannot be confirmed."
            : "WatchDog canceled this Windows shutdown request while checking recording cleanup. " +
              "Exit remains blocked if cleanup cannot be confirmed.";
}
