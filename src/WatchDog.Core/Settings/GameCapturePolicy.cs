using WatchDog.Core.Capture;
using WatchDog.Core.Recording;

namespace WatchDog.Core.Settings;

public enum CapturePolicyDisposition { Ready, Disabled, Unsupported }

public sealed record CapturePolicyDecision(
    CapturePolicyDisposition Disposition,
    CaptureStartPolicy? Policy,
    CaptureFailure? Failure)
{
    public string Message => Failure?.Message ?? (Disposition == CapturePolicyDisposition.Disabled
        ? "Automatic recording is disabled for this executable. Enable Auto Record in its saved profile to start it."
        : "Validated settings will apply to this automatic start.");
}

public static class GameCapturePolicy
{
    public static CapturePolicyDecision Resolve(AppSettings settings, string executableName, CaptureConfig startupCanvas)
    {
        // Copy the matching immutable record now; no profile collection is carried downstream.
        var matches = settings.GameProfiles.Where(profile => string.Equals(
            profile.GameExecutableName, executableName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
            return Reject("capture.profile.ambiguous", "Multiple profiles match this exact executable. Keep one saved profile and retry.");
        var profile = matches.SingleOrDefault();
        if (profile?.AutoRecord == false)
            return new(CapturePolicyDisposition.Disabled, null, null);

        var policy = new CaptureStartPolicy(settings.Capture, settings.Buffer, settings.Recording).Sanitized();
        if (profile is not null)
        {
            profile = profile.Sanitized();
            if (profile.Mode is not (GameRecordingMode.ReplayBuffer or GameRecordingMode.SessionRecording))
                return Reject("capture.profile.mode-unsupported", "Select Replay Buffer or Session Recording; this saved profile mode cannot be applied at the next start.");
            if (profile.HighlightSensitivity is not null)
                return Reject("capture.profile.sensitivity-unsupported", "Clear the saved highlight sensitivity override; next-start sensitivity application is unsupported.");
            var recording = policy.Recording with
            {
                Mode = profile.Mode == GameRecordingMode.ReplayBuffer
                    ? RecordingMode.ReplayBufferOnly : RecordingMode.SessionRecording,
            };
            if (profile.BufferDurationSeconds is not null && !recording.IsReplayBufferEnabled)
                return Reject("capture.profile.buffer-output-unsupported", "Use Replay Buffer or clear the buffer duration override; session-only output cannot consume it.");
            if (profile.BitrateKbps is not null && recording.IsReplayBufferEnabled &&
                policy.Capture.RateControl is RateControlType.CQP or RateControlType.CRF)
            {
                return Reject("capture.profile.replay-bitrate-unsupported",
                    "Clear the bitrate override or explicitly select CBR/VBR in Settings; CQP/CRF replay does not consume a target bitrate override.");
            }
            policy = policy with
            {
                Capture = policy.Capture with
                {
                    OutputWidth = (uint?)profile.OutputWidth ?? policy.Capture.OutputWidth,
                    OutputHeight = (uint?)profile.OutputHeight ?? policy.Capture.OutputHeight,
                    Bitrate = profile.BitrateKbps ?? policy.Capture.Bitrate,
                },
                Buffer = policy.Buffer with { MaxSeconds = profile.BufferDurationSeconds ?? policy.Buffer.MaxSeconds },
                Recording = recording,
            };
            policy = policy.Sanitized();
        }
        return policy.Validate(startupCanvas) is { } failure
            ? new(CapturePolicyDisposition.Unsupported, null, failure)
            : new(CapturePolicyDisposition.Ready, policy, null);
    }

    private static CapturePolicyDecision Reject(string code, string message) =>
        new(CapturePolicyDisposition.Unsupported, null, CaptureStartPolicy.Unsupported(code, message));
}
