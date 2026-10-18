namespace WatchDog.Core.Webcam;

/// <summary>
/// Persisted preference for including one exact physical webcam in an explicitly
/// initiated manual recording. This preference never authorizes startup or automatic capture.
/// </summary>
public sealed record WebcamConfig
{
    public bool EnabledForExplicitManualRecording { get; init; } = false;
    public string DeviceId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;
    public double FramesPerSecond { get; init; } = 30;
    public string VideoFormat { get; init; } = "MJPEG";
    public int VideoBitrateKbps { get; init; } = 1_500;
}
