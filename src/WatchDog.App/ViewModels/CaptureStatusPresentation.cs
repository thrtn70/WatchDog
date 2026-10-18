using WatchDog.Core.Capture;

namespace WatchDog.App.ViewModels;

public sealed record CaptureStatusWarningPresentation(
    string MainWindowText,
    string TrayText,
    string OverlayLine,
    string OverlayLabel);

/// <summary>
/// Pure projection of an owned lifecycle failure into the three user-visible status
/// surfaces. Engine state alone cannot represent failures discovered after native
/// resources have already returned to Idle, such as finalized-media validation.
/// </summary>
public static class CaptureStatusPresentation
{
    public static CaptureStatusWarningPresentation? Warning(CaptureLifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Health == CaptureHealth.Healthy || snapshot.LastFailure is null)
            return null;

        var message = CaptureFailurePresentation.SafeText(snapshot.LastFailure);
        return new CaptureStatusWarningPresentation(
            $"Recording warning: {message}",
            $"WatchDog - Warning: {message}",
            $"WARN · {message}",
            "Recording warning");
    }
}
