namespace WatchDog.Core.Webcam;

public enum WebcamCaptureAuthorizationScope
{
    ExplicitManualRecordingOnly,
}

/// <summary>An immutable, exact webcam policy frozen into one manual capture request.</summary>
public sealed record WebcamCaptureSelection
{
    public WebcamCaptureSelection(
        string deviceId,
        string displayName,
        int width,
        int height,
        double framesPerSecond,
        string videoFormat,
        int videoBitrateKbps)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("An exact opaque webcam device ID is required.", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("A webcam display name is required.", nameof(displayName));
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (!double.IsFinite(framesPerSecond) || framesPerSecond <= 0)
            throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
        if (string.IsNullOrWhiteSpace(videoFormat))
            throw new ArgumentException("A webcam video format is required.", nameof(videoFormat));
        if (videoBitrateKbps <= 0) throw new ArgumentOutOfRangeException(nameof(videoBitrateKbps));

        DeviceId = deviceId;
        DisplayName = displayName;
        Width = width;
        Height = height;
        FramesPerSecond = framesPerSecond;
        VideoFormat = videoFormat;
        VideoBitrateKbps = videoBitrateKbps;
    }

    public string DeviceId { get; }
    public string DisplayName { get; }
    public int Width { get; }
    public int Height { get; }
    public double FramesPerSecond { get; }
    public string VideoFormat { get; }
    public int VideoBitrateKbps { get; }
    public WebcamCaptureAuthorizationScope AuthorizationScope { get; } =
        WebcamCaptureAuthorizationScope.ExplicitManualRecordingOnly;

    public static WebcamCaptureSelection FromConfig(
        WebcamConfig config,
        WebcamDeviceInfo exactDevice)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(exactDevice);
        if (!config.EnabledForExplicitManualRecording)
            throw new InvalidOperationException("The webcam preference is not enabled for explicit manual recording.");
        if (!StringComparer.Ordinal.Equals(config.DeviceId, exactDevice.DeviceId))
            throw new InvalidOperationException("The exact webcam identity changed.");

        return new WebcamCaptureSelection(
            config.DeviceId,
            exactDevice.DisplayName,
            config.Width,
            config.Height,
            config.FramesPerSecond,
            config.VideoFormat,
            config.VideoBitrateKbps);
    }
}
