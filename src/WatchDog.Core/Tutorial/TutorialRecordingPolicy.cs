using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Recording;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Tutorial;

/// <summary>
/// An unregistered tutorial start plan. Audio is declared startup metadata, not device
/// admission or a live audio override. Only the existing lifecycle may admit the request.
/// </summary>
public sealed class TutorialRecordingPlan
{
    internal TutorialRecordingPlan(CaptureRequest request)
    {
        Request = request;
        StartupAudio = request.StartupExpectation!.StartupAudio;
        AudioTracks = AudioTrackPlan.Create(StartupAudio.SeparateAudioTracks);
    }

    public CaptureRequest Request { get; }
    public AudioMixConfig StartupAudio { get; }
    public AudioTrackPlan AudioTracks { get; }
    public string CanonicalSaveRoot => Request.StartupExpectation!.CanonicalSaveRoot;
    public bool CanPlanStart => true;
    public bool NativeRecordingProven => false;
}

public sealed record TutorialRecordingPlanResult(TutorialRecordingPlan? Plan, CaptureFailure? Failure);

/// <summary>Pure planning only: no enumeration, preview, device acquisition or lifecycle mutation.</summary>
public static class TutorialRecordingPolicy
{
    public static TutorialRecordingPlanResult Create(
        Guid requestId,
        DateTimeOffset requestedAt,
        CaptureTarget? target,
        CaptureStartupBaselineResult startupBaseline,
        WebcamCaptureSelection? webcam = null,
        bool explicitWebcamConsent = false)
    {
        ArgumentNullException.ThrowIfNull(startupBaseline);
        if (requestId == Guid.Empty)
            return Rejected("tutorial.request.id-empty", "A unique tutorial request is required.");
        if (!IsExactTutorialTarget(target))
            return Rejected("tutorial.target.invalid", "Select one exact window, stable display, or region wholly inside its frozen owning display. No broader source is substituted.");
        if (webcam is not null && !explicitWebcamConsent)
            return Rejected("tutorial.webcam.consent-required", "Confirm the exact webcam for this manual recording, or record without a webcam.");
        if (explicitWebcamConsent && webcam is null)
            return Rejected("tutorial.webcam.selection-required", "Select the exact webcam before confirming it.");

        if (startupBaseline.Baseline is not { } baseline)
            return new(null, startupBaseline.Failure);
        var audio = baseline.Expectation.StartupAudio;

        // A tutorial explicitly plans retained session media even when game defaults use replay.
        // This choice changes only the plan, never saved settings or the running backend.
        var policy = (baseline.DefaultStartPolicy with
        {
            Recording = baseline.DefaultStartPolicy.Recording with { Mode = RecordingMode.SessionRecording },
        }).Sanitized();
        var canvas = baseline.Expectation.StartupCanvas;
        if (policy.Validate(policy.Capture with
        {
            OutputWidth = canvas.OutputWidth,
            OutputHeight = canvas.OutputHeight,
            Fps = canvas.Fps,
        }) is { } failure)
            return new(null, failure);
        var format = (policy.Recording.FileFormat ?? string.Empty).Trim().ToLowerInvariant();
        if (format is not ("mp4" or "mkv" or "flv"))
            return new(null, new(CaptureFailureKind.Unsupported, "tutorial.recording.container-unsupported",
                "Select MP4, MKV or FLV in Settings and restart before planning session recording."));
        if (audio.SeparateAudioTracks && format is not ("mp4" or "mkv"))
        {
            return new(null, new(CaptureFailureKind.Unsupported, "tutorial.audio.container-unsupported",
                "Separate system audio and microphone tracks require MP4 or MKV session recording. Change Settings and restart before planning."));
        }

        return new(new TutorialRecordingPlan(
            new CaptureRequest(requestId, requestedAt, CaptureInitiation.Manual, target!, webcam, policy,
                baseline.Expectation)), null);
    }

    internal static bool IsExactTutorialTarget(CaptureTarget? target) => target switch
    {
        WindowCaptureTarget window => window.ProcessId > 0 && window.WindowHandle != IntPtr.Zero &&
            !string.IsNullOrWhiteSpace(window.ExecutableName) && !string.IsNullOrWhiteSpace(window.WindowTitle),
        DisplayCaptureTarget display => !string.IsNullOrWhiteSpace(display.StableDisplayId),
        RegionCaptureTarget region => RegionCaptureGeometry.TryGetDisplayLocalBounds(region, out _),
        _ => false,
    };

    // Conservative presentation/model correlation includes all frozen metadata. A future
    // resolver must bind the fully resolved target before dispatch, never relax this match.
    internal static bool SameBoundary(CaptureTarget requested, CaptureTarget actual) =>
        IsExactTutorialTarget(requested) && requested == actual;

    private static TutorialRecordingPlanResult Rejected(string code, string message) =>
        new(null, new(CaptureFailureKind.InvalidRequest, code, message));
}
