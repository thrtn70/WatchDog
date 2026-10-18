using WatchDog.Core.Capture;
using WatchDog.Core.Tutorial;

namespace WatchDog.Core.Tests.Tutorial;

public sealed class TutorialTimelineTests
{
    [Fact]
    public void MapsModeledRetainedIntervalsToPauseExcludedPlaybackWithoutPromotingEvidence()
    {
        var (context, epoch, identity) = Fixture();
        var intervals = new List<TutorialTimelineInterval>
        {
            new(identity, TimeSpan.Zero, TimeSpan.FromSeconds(2), TutorialIntervalKind.Recording),
            new(identity, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(7), TutorialIntervalKind.Paused),
            new(identity, TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(10), TutorialIntervalKind.Recording),
        };
        var timeline = TutorialTimelineContract.Create(context, epoch, intervals).Timeline!;
        Assert.Equal(TimeSpan.FromSeconds(5), timeline.PlaybackDuration);
        Assert.Equal(TimeSpan.FromSeconds(10), timeline.CaptureDuration);
        Assert.Equal(2, timeline.PlaybackIntervals.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), timeline.PlaybackIntervals[1].PlaybackStart);
        Assert.Equal(TimeSpan.FromSeconds(7), timeline.PlaybackIntervals[1].CaptureStart);
        Assert.False(timeline.NativePauseProven);
        Assert.False(timeline.NativeAlignmentProven);
        intervals.Clear();
        Assert.Equal(3, timeline.Intervals.Count);
    }

    [Theory]
    [InlineData(-1, 2, 2, 3)]
    [InlineData(0, 0, 0, 2)]
    [InlineData(0, 2, 3, 5)]
    [InlineData(0, 3, 2, 5)]
    [InlineData(1, 2, 2, 5)]
    public void RejectsNegativeEmptyGapOverlapAndMissingEpochStart(int a, int b, int c, int d)
    {
        var (context, epoch, identity) = Fixture();
        var result = TutorialTimelineContract.Create(context, epoch,
            [new(identity, TimeSpan.FromSeconds(a), TimeSpan.FromSeconds(b), TutorialIntervalKind.Recording),
             new(identity, TimeSpan.FromSeconds(c), TimeSpan.FromSeconds(d), TutorialIntervalKind.Paused)]);
        Assert.Null(result.Timeline);
        Assert.Equal("tutorial.timeline.invalid-boundary", result.Failure!.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void RejectsForeignSessionRequestGenerationEpochOrPhysicalTarget(int field)
    {
        var (context, epoch, identity) = Fixture();
        var foreign = field switch
        {
            0 => identity with { SessionId = Guid.NewGuid() },
            1 => identity with { RequestId = Guid.NewGuid() },
            2 => identity with { Generation = identity.Generation + 1 },
            3 => identity with { EpochId = Guid.NewGuid() },
            _ => identity with { ActualTarget = new DisplayCaptureTarget("another") },
        };
        var result = TutorialTimelineContract.Create(context, epoch, [new(foreign, TimeSpan.Zero, TimeSpan.FromSeconds(1), TutorialIntervalKind.Recording)]);
        Assert.Null(result.Timeline);
        Assert.Equal("tutorial.timeline.identity-mismatch", result.Failure!.Code);
    }

    [Fact]
    public void InvalidIdentityAndUndefinedIntervalCannotProduceTimeline()
    {
        var (context, epoch, identity) = Fixture();
        Assert.Null(TutorialTimelineContract.Create(context, Guid.Empty, []).Timeline);
        Assert.Null(TutorialTimelineContract.Create(context with { Generation = 0 }, epoch, []).Timeline);
        Assert.Null(TutorialTimelineContract.Create(context with { SessionId = Guid.Empty }, epoch, []).Timeline);
        Assert.Null(TutorialTimelineContract.Create(context with { ActualTarget = new DisplayCaptureTarget("broader") }, epoch, []).Timeline);
        Assert.Null(TutorialTimelineContract.Create(context, epoch, [new(identity, TimeSpan.Zero, TimeSpan.MaxValue, (TutorialIntervalKind)999)]).Timeline);
    }

    [Fact]
    public void EmptyAndTrailingPauseAreExplicitModelsAndLargeTicksDoNotOverflow()
    {
        var (context, epoch, identity) = Fixture();
        Assert.Equal(TimeSpan.Zero, TutorialTimelineContract.Create(context, epoch, []).Timeline!.PlaybackDuration);
        var timeline = TutorialTimelineContract.Create(context, epoch,
            [new(identity, TimeSpan.Zero, TimeSpan.FromTicks(long.MaxValue - 1), TutorialIntervalKind.Recording),
             new(identity, TimeSpan.FromTicks(long.MaxValue - 1), TimeSpan.MaxValue, TutorialIntervalKind.Paused)]).Timeline!;
        Assert.Equal(TimeSpan.FromTicks(long.MaxValue - 1), timeline.PlaybackDuration);
        Assert.Equal(TimeSpan.MaxValue, timeline.CaptureDuration);
    }

    [Fact]
    public void MultiplePausesKeepExactBoundariesAndReturnedCollectionsAreReadOnly()
    {
        var (context, epoch, identity) = Fixture();
        var timeline = TutorialTimelineContract.Create(context, epoch,
            [new(identity, TimeSpan.Zero, TimeSpan.FromTicks(1), TutorialIntervalKind.Recording),
             new(identity, TimeSpan.FromTicks(1), TimeSpan.FromTicks(2), TutorialIntervalKind.Paused),
             new(identity, TimeSpan.FromTicks(2), TimeSpan.FromTicks(3), TutorialIntervalKind.Recording),
             new(identity, TimeSpan.FromTicks(3), TimeSpan.FromTicks(9), TutorialIntervalKind.Paused),
             new(identity, TimeSpan.FromTicks(9), TimeSpan.FromTicks(10), TutorialIntervalKind.Recording)]).Timeline!;
        Assert.Equal(TimeSpan.FromTicks(3), timeline.PlaybackDuration);
        Assert.Equal(TimeSpan.FromTicks(2), timeline.PlaybackIntervals[2].PlaybackStart);
        Assert.Equal(TimeSpan.FromTicks(9), timeline.PlaybackIntervals[2].CaptureStart);
        Assert.Equal(TimeSpan.FromTicks(10), timeline.PlaybackIntervals[2].CaptureEnd);
        Assert.Throws<NotSupportedException>(() => ((IList<TutorialTimelineInterval>)timeline.Intervals).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<TutorialPlaybackInterval>)timeline.PlaybackIntervals).Clear());
    }

    private static (CaptureSessionContext, Guid, TutorialTimelineIdentity) Fixture()
    {
        var plan = TutorialWorkspaceTests.Plan();
        var context = new CaptureSessionContext(Guid.NewGuid(), 1, plan.Request, plan.Request.Target, plan.Request.Target);
        var epoch = Guid.NewGuid();
        return (context, epoch, TutorialTimelineIdentity.From(context, epoch));
    }
}
