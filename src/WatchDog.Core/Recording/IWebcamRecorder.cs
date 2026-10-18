using WatchDog.Core.Webcam;

namespace WatchDog.Core.Recording;

/// <summary>
/// Owns an independent, video-only webcam recording for one frozen manual selection.
/// </summary>
public interface IWebcamRecorder : IDisposable
{
    bool IsRecording { get; }
    bool HasActiveResources { get; }
    bool HasNativeResources { get; }
    bool HasUncertainViewRelease { get; }
    string? CurrentOutputPath { get; }

    Task StartAsync(
        WebcamCaptureSelection selection,
        string outputPath,
        CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}
