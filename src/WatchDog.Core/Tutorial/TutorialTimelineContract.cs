using WatchDog.Core.Capture;

namespace WatchDog.Core.Tutorial;

public enum TutorialIntervalKind
{
    Recording,
    Paused,
}

/// <summary>Correlation for modeled boundaries in one caller-supplied monotonic epoch.</summary>
public sealed record TutorialTimelineIdentity(
    Guid SessionId,
    Guid RequestId,
    long Generation,
    CaptureTarget ActualTarget,
    Guid EpochId)
{
    public static TutorialTimelineIdentity From(CaptureSessionContext context, Guid epochId) =>
        new(context.SessionId, context.Request.RequestId, context.Generation, context.ActualTarget, epochId);
}

public sealed record TutorialTimelineInterval(
    TutorialTimelineIdentity Identity,
    TimeSpan StartOffset,
    TimeSpan EndOffset,
    TutorialIntervalKind Kind);

public sealed record TutorialPlaybackInterval(TimeSpan CaptureStart, TimeSpan CaptureEnd, TimeSpan PlaybackStart);

/// <summary>
/// Pause-excluded arithmetic on modeled intervals, never a native receipt or persisted-media
/// timeline. All tracks would need separately qualified boundaries in this same epoch.
/// </summary>
public sealed class TutorialTimelinePlan
{
    internal TutorialTimelinePlan(
        TutorialTimelineIdentity identity,
        TutorialTimelineInterval[] intervals,
        TutorialPlaybackInterval[] playbackIntervals,
        TimeSpan captureDuration,
        TimeSpan playbackDuration)
    {
        Identity = identity;
        Intervals = Array.AsReadOnly(intervals);
        PlaybackIntervals = Array.AsReadOnly(playbackIntervals);
        CaptureDuration = captureDuration;
        PlaybackDuration = playbackDuration;
    }

    public TutorialTimelineIdentity Identity { get; }
    public IReadOnlyList<TutorialTimelineInterval> Intervals { get; }
    public IReadOnlyList<TutorialPlaybackInterval> PlaybackIntervals { get; }
    public TimeSpan CaptureDuration { get; }
    public TimeSpan PlaybackDuration { get; }
    public bool NativePauseProven => false;
    public bool NativeAlignmentProven => false;
}

public sealed record TutorialTimelinePlanResult(TutorialTimelinePlan? Timeline, CaptureFailure? Failure);

/// <summary>
/// Validates an explicit ordered interval packet without reading a clock, scheduling,
/// acknowledging native commands, filling gaps or altering SessionMediaSegment estimates.
/// </summary>
public static class TutorialTimelineContract
{
    public static TutorialTimelinePlanResult Create(
        CaptureSessionContext context,
        Guid epochId,
        IEnumerable<TutorialTimelineInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(intervals);
        if (context.SessionId == Guid.Empty || context.Request.RequestId == Guid.Empty ||
            context.Generation <= 0 || epochId == Guid.Empty ||
            context.Request.Initiation != CaptureInitiation.Manual ||
            context.Request.StartPolicy?.Recording.IsSessionRecordingEnabled != true ||
            context.RequestedTarget != context.Request.Target ||
            !TutorialRecordingPolicy.IsExactTutorialTarget(context.ActualTarget) ||
            !TutorialRecordingPolicy.SameBoundary(context.Request.Target, context.ActualTarget))
            return Rejected("tutorial.timeline.invalid-context", "A manual session, exact source, positive generation and explicit modeled monotonic epoch are required.");

        var identity = TutorialTimelineIdentity.From(context, epochId);
        var copied = intervals.ToArray();
        var playback = new List<TutorialPlaybackInterval>();
        var captureEnd = TimeSpan.Zero;
        var playbackEnd = TimeSpan.Zero;
        foreach (var interval in copied)
        {
            if (interval is null || interval.Identity != identity)
                return Rejected("tutorial.timeline.identity-mismatch", "Each modeled boundary must name the same session, request, generation, physical target and epoch.");
            if (!Enum.IsDefined(interval.Kind) || interval.StartOffset < TimeSpan.Zero ||
                interval.StartOffset != captureEnd || interval.EndOffset <= interval.StartOffset)
                return Rejected("tutorial.timeline.invalid-boundary", "Use positive, ordered, contiguous intervals from epoch zero. Gaps, overlaps and undefined boundaries cannot imply a pause.");

            if (interval.Kind == TutorialIntervalKind.Recording)
            {
                playback.Add(new(interval.StartOffset, interval.EndOffset, playbackEnd));
                // Disjoint nonnegative spans sum to at most the final valid TimeSpan offset.
                playbackEnd += interval.EndOffset - interval.StartOffset;
            }
            captureEnd = interval.EndOffset;
        }
        return new(new TutorialTimelinePlan(identity, copied, playback.ToArray(), captureEnd, playbackEnd), null);
    }

    private static TutorialTimelinePlanResult Rejected(string code, string message) =>
        new(null, new(CaptureFailureKind.InvalidRequest, code, message));
}
