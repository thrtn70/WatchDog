using WatchDog.Core.Capture;

namespace WatchDog.Core.Tutorial;

public sealed record TutorialControlAvailability(bool Available, string Reason);

/// <summary>
/// Pure source design for a future tutorial workspace. This observes the existing owner;
/// it owns no resources or commands and never infers release of a manual reservation.
/// </summary>
public sealed record TutorialWorkspaceProjection(
    bool PlanReady,
    bool OwnsObservedContext,
    bool CanRequestStop,
    string StatusText,
    string BoundaryText,
    string AudioText)
{
    public TutorialControlAvailability Start { get; } = new(false,
        "Tutorial selection is a managed plan. The tutorial workspace has no production start route yet.");
    public TutorialControlAvailability Preview { get; } = new(false,
        "Live preview is unavailable. Confirm the selected boundary before recording; a source label is not a pixel preview.");
    public TutorialControlAvailability Countdown { get; } = new(false,
        "Countdown is unavailable; no delayed start is scheduled.");
    public TutorialControlAvailability Pause { get; } = new(false,
        "Pause is unsupported by the recording lifecycle. Stop to finish the current recording.");
    public TutorialControlAvailability Resume { get; } = new(false,
        "Resume is unsupported. Start a new recording after stopping; it is a separate recording.");
    public TutorialControlAvailability Split { get; } = new(false,
        "Manual split is unsupported. Existing automatic file segmentation is not a pause or manual split command.");

    public static TutorialWorkspaceProjection Create(TutorialRecordingPlan? plan, CaptureLifecycleSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var context = snapshot.Context;
        var owns = plan is not null && context is not null &&
            context.SessionId != Guid.Empty && context.Generation > 0 &&
            context.Request == plan.Request && context.RequestedTarget == plan.Request.Target &&
            TutorialRecordingPolicy.SameBoundary(plan.Request.Target, context.ActualTarget);
        var canStop = plan is not null && snapshot.Readiness == CaptureReadiness.Available &&
            snapshot.Status is CaptureLifecycleStatus.Idle or CaptureLifecycleStatus.Active or CaptureLifecycleStatus.Faulted &&
            owns;
        var status = snapshot.Readiness == CaptureReadiness.Closed || snapshot.Status == CaptureLifecycleStatus.Shutdown
            ? "Recording admission is closed."
            : context is not null && !owns
                ? "Another capture owns the lifecycle; this tutorial plan is pending."
                : snapshot.LastFailure is { } failure
                    ? $"Recording warning: {failure.Message}"
                    : snapshot.Status switch
                    {
                        CaptureLifecycleStatus.Starting when owns => "Starting the selected recording; admission is pending.",
                        CaptureLifecycleStatus.Starting => "Recording admission is pending; tutorial ownership is unknown.",
                        CaptureLifecycleStatus.Active when owns => "Recording the selected tutorial source.",
                        CaptureLifecycleStatus.Saving => "Saving; wait for completion.",
                        CaptureLifecycleStatus.Stopping => "Stopping; wait for finalized media and resource release.",
                        CaptureLifecycleStatus.Faulted => "Recording failed; cleanup may still be owned. Request Stop to retry.",
                        _ when plan is not null => "Tutorial plan ready. Manual ownership is unknown from an idle snapshot; the lifecycle must resolve any retained reservation.",
                        _ => "Select a window, display, or display-owned region for a tutorial plan.",
                    };
        return new(plan is not null, owns, canStop, status, Boundary(plan?.Request.Target), Audio(plan));
    }

    private static string Boundary(CaptureTarget? target) => target switch
    {
        WindowCaptureTarget window => $"Window: {window.WindowTitle ?? "Selected window"} (PID {window.ProcessId}). Exact window only; no desktop fallback.",
        DisplayCaptureTarget display => $"Display: {display.DisplayName ?? "Selected display"}. The entire selected display is included.",
        RegionCaptureTarget region => $"Region: {region.PhysicalBounds.Width} × {region.PhysicalBounds.Height} physical pixels at ({region.PhysicalBounds.X}, {region.PhysicalBounds.Y}), inside the frozen selected display.",
        _ => "No source selected.",
    };

    private static string Audio(TutorialRecordingPlan? plan)
    {
        if (plan is null) return "No startup audio plan.";
        var audio = plan.StartupAudio;
        var system = audio.DesktopAudioEnabled ? "System audio enabled" : "System audio off";
        var mic = audio.MicEnabled ? "microphone enabled" : "microphone off";
        var tracks = audio.SeparateAudioTracks ? "separate tracks (system 1, microphone 2)" : "combined track 1";
        return $"Startup settings: {system}, {mic}, {tracks}. Actual devices require lifecycle admission.";
    }
}
