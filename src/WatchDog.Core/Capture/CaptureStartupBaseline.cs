using WatchDog.Core.Audio;
using WatchDog.Core.Recording;
using WatchDog.Core.Settings;
using WatchDog.Core.Tutorial;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Capture;

public sealed record CaptureStartupCanvas
{
    public CaptureStartupCanvas(uint width, uint height, uint fps) =>
        (OutputWidth, OutputHeight, Fps) = (width, height, fps);
    public uint OutputWidth { get; }
    public uint OutputHeight { get; }
    public uint Fps { get; }
}

public sealed record CaptureStartupConstraints
{
    public CaptureStartupConstraints(int effectiveAudioBitrate, string bufferOutputDirectory,
        string bufferFilenameFormat, string recordingFileFormat) =>
        (EffectiveAudioBitrate, BufferOutputDirectory, BufferFilenameFormat, RecordingFileFormat) =
        (effectiveAudioBitrate, bufferOutputDirectory, bufferFilenameFormat, recordingFileFormat);
    public int EffectiveAudioBitrate { get; }
    public string BufferOutputDirectory { get; }
    public string BufferFilenameFormat { get; }
    public string RecordingFileFormat { get; }
    public override string ToString() => nameof(CaptureStartupConstraints);
}

/// <summary>Configured startup intent only; neither an admission receipt nor a capability.</summary>
public sealed record CaptureStartupExpectation
{
    public CaptureStartupExpectation(int schemaVersion, Guid hostBaselineId, CaptureStartupCanvas startupCanvas,
        AudioMixConfig startupAudio, int trackLayoutVersion, CaptureStartupConstraints startupOnlyConstraints,
        string canonicalSaveRoot) =>
        (SchemaVersion, HostBaselineId, StartupCanvas, StartupAudio, TrackLayoutVersion, StartupOnlyConstraints,
            CanonicalSaveRoot) =
        (schemaVersion, hostBaselineId, startupCanvas, startupAudio, trackLayoutVersion, startupOnlyConstraints,
            canonicalSaveRoot);
    public int SchemaVersion { get; }
    public Guid HostBaselineId { get; }
    public CaptureStartupCanvas StartupCanvas { get; }
    public AudioMixConfig StartupAudio { get; }
    public int TrackLayoutVersion { get; }
    public CaptureStartupConstraints StartupOnlyConstraints { get; }
    public string CanonicalSaveRoot { get; }
    public override string ToString() => nameof(CaptureStartupExpectation);

    internal bool IsWellFormed => SchemaVersion == 1 && HostBaselineId != Guid.Empty &&
        TrackLayoutVersion == 1 && StartupCanvas is not null &&
        CaptureStartupBaselineResult.ValidAudio(StartupAudio) &&
        StartupOnlyConstraints is
        {
            EffectiveAudioBitrate: >= 64 and <= 320,
            BufferFilenameFormat: not null, RecordingFileFormat: not null
        } constraints &&
        CaptureStartupBaselineResult.TryCanonicalRoot(CanonicalSaveRoot, out var root) &&
        StringComparer.Ordinal.Equals(root, CanonicalSaveRoot) &&
        CaptureStartupBaselineResult.TryCanonicalRoot(constraints.BufferOutputDirectory, out var bufferRoot) &&
        StringComparer.Ordinal.Equals(root, bufferRoot);
}

/// <summary>Format seed only. Persisted camera identity and enablement are deliberately absent.</summary>
public sealed record TutorialWebcamFormatDefaults
{
    public TutorialWebcamFormatDefaults(int width, int height, double framesPerSecond, string videoFormat,
        int videoBitrateKbps) =>
        (Width, Height, FramesPerSecond, VideoFormat, VideoBitrateKbps) =
        (width, height, framesPerSecond, videoFormat, videoBitrateKbps);
    public int Width { get; }
    public int Height { get; }
    public double FramesPerSecond { get; }
    public string VideoFormat { get; }
    public int VideoBitrateKbps { get; }
    public override string ToString() => nameof(TutorialWebcamFormatDefaults);

    internal static TutorialWebcamFormatDefaults? From(WebcamConfig? config) =>
        config is { Width: > 0, Height: > 0, VideoBitrateKbps: > 0 } &&
        double.IsFinite(config.FramesPerSecond) && config.FramesPerSecond > 0 &&
        !string.IsNullOrWhiteSpace(config.VideoFormat)
            ? new(config.Width, config.Height, config.FramesPerSecond, config.VideoFormat, config.VideoBitrateKbps)
            : null;
}

public sealed class CaptureStartupBaseline
{
    internal CaptureStartupBaseline(CaptureStartupExpectation expectation, CaptureStartPolicy defaultStartPolicy,
        TutorialWebcamFormatDefaults? webcamFormatDefaults)
    {
        Expectation = expectation;
        DefaultStartPolicy = defaultStartPolicy;
        WebcamFormatDefaults = webcamFormatDefaults;
    }

    public CaptureStartupExpectation Expectation { get; }
    public CaptureStartPolicy DefaultStartPolicy { get; }
    public TutorialWebcamFormatDefaults? WebcamFormatDefaults { get; }
    public override string ToString() => nameof(CaptureStartupBaseline);
}

/// <summary>
/// One immutable result per host, derived from the same startup settings as the consumers.
/// Pure lexical extraction and comparison: no settings reload, I/O, devices or native calls.
/// Unavailable tutorial metadata does not change unbound legacy admission.
/// </summary>
public sealed class CaptureStartupBaselineResult
{
    private CaptureStartupBaselineResult(CaptureStartupBaseline? baseline, CaptureFailure? failure)
    {
        Baseline = baseline;
        Failure = failure;
    }

    public CaptureStartupBaseline? Baseline { get; }
    public CaptureFailure? Failure { get; }
    public override string ToString() => nameof(CaptureStartupBaselineResult);

    public static CaptureStartupBaselineResult Create(AppSettings? settings, Guid? hostBaselineId = null)
    {
        var id = hostBaselineId ?? Guid.NewGuid();
        if (id == Guid.Empty || settings?.Capture is null || settings.Buffer is null ||
            settings.Recording is null || settings.Storage is null || !ValidAudio(settings.Audio) ||
            settings.Buffer.FilenameFormat is null || settings.Recording.FileFormat is null ||
            !TryCanonicalRoot(settings.Storage.SavePath, out var root) ||
            !TryCanonicalRoot(settings.Buffer.OutputDirectory, out var bufferRoot) ||
            !StringComparer.Ordinal.Equals(root, bufferRoot))
            return new(null, Unavailable());

        // Preserve the original canvas, while sharing the existing effective policy semantics.
        var policy = new CaptureStartPolicy(settings.Capture with { }, settings.Buffer with { },
            settings.Recording with { }).Sanitized();
        var expectation = new CaptureStartupExpectation(1, id,
            new(settings.Capture.OutputWidth, settings.Capture.OutputHeight, settings.Capture.Fps),
            settings.Audio with { }, 1,
            new(policy.Capture.AudioBitrate, settings.Buffer.OutputDirectory,
                settings.Buffer.FilenameFormat, settings.Recording.FileFormat), root!);
        return new(new(expectation, policy, TutorialWebcamFormatDefaults.From(settings.Webcam)), null);
    }

    public CaptureFailure? Validate(CaptureRequest request)
    {
        if (request.StartupExpectation is not { } expected) return null;
        if (!expected.IsWellFormed || request.RequestId == Guid.Empty ||
            request.Initiation != CaptureInitiation.Manual ||
            !TutorialRecordingPolicy.IsExactTutorialTarget(request.Target) ||
            request.StartPolicy is not
            {
                Capture: not null, Buffer: not null,
                Recording: { Mode: RecordingMode.SessionRecording }
            })
            return new(CaptureFailureKind.InvalidRequest, "capture.startup-binding.invalid",
                "The tutorial startup binding is invalid. Prepare a new tutorial plan.");
        if (Baseline is null) return Unavailable();
        if (expected.HostBaselineId != Baseline.Expectation.HostBaselineId)
            return new(CaptureFailureKind.Superseded, "capture.startup-binding.host-changed",
                "Reopen the tutorial workspace and prepare a plan for this application session.");
        if (expected != Baseline.Expectation)
            return new(CaptureFailureKind.Unsupported, "capture.startup-binding.mismatch",
                "Prepare a plan from the startup configuration. Restart first if changed settings are intended.");
        return null;
    }

    internal static bool ValidAudio(AudioMixConfig? audio) => audio is not null &&
        ValidSelector(audio.DesktopDeviceId) && ValidSelector(audio.MicDeviceId) &&
        float.IsFinite(audio.DesktopVolume) && audio.DesktopVolume is >= 0 and <= 1 &&
        float.IsFinite(audio.MicVolume) && audio.MicVolume is >= 0 and <= 1;

    private static bool ValidSelector(string? value) => value is not null &&
        (value.Length == 0 || !string.IsNullOrWhiteSpace(value));

    internal static bool TryCanonicalRoot(string? value, out string? canonical)
    {
        canonical = null;
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value)) return false;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException)
        {
            return false;
        }
    }

    private static CaptureFailure Unavailable() => new(CaptureFailureKind.Unsupported,
        "capture.startup-binding.unavailable",
        "Startup configuration is unavailable for tutorials. Correct Settings and restart before planning.");
}
