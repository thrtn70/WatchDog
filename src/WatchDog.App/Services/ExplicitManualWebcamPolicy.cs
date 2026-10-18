using WatchDog.Core.Settings;
using WatchDog.Core.Webcam;

namespace WatchDog.App.Services;

internal static class ExplicitManualWebcamPolicy
{
    public static WebcamCaptureSelection? Snapshot(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var webcam = settings.Webcam;
        if (!webcam.EnabledForExplicitManualRecording)
            return null;

        return new WebcamCaptureSelection(
            webcam.DeviceId,
            webcam.DisplayName,
            webcam.Width,
            webcam.Height,
            webcam.FramesPerSecond,
            webcam.VideoFormat,
            webcam.VideoBitrateKbps);
    }
}
