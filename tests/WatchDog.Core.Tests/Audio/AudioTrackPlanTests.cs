using WatchDog.Core.Audio;

namespace WatchDog.Core.Tests.Audio;

public sealed class AudioTrackPlanTests
{
    [Fact]
    public void CombinedPlanRoutesBothSourcesOnlyToTrackOne()
    {
        var plan = AudioTrackPlan.Create(separateAudioTracks: false);

        Assert.False(plan.SeparateAudioTracks);
        Assert.Equal(1, plan.EncoderCount);
        Assert.Equal((1, 0, 0, 1u), Describe(plan.Desktop));
        Assert.Equal((1, 0, 0, 1u), Describe(plan.Microphone));
        Assert.Collection(
            plan.Encoders,
            route => Assert.Equal((1, 0, 0, 1u), Describe(route)));
    }

    [Fact]
    public void SeparatePlanKeepsStableDesktopAndMicrophoneTracks()
    {
        var plan = AudioTrackPlan.Create(separateAudioTracks: true);

        Assert.True(plan.SeparateAudioTracks);
        Assert.Equal(2, plan.EncoderCount);
        Assert.Equal((1, 0, 0, 1u), Describe(plan.Desktop));
        Assert.Equal((2, 1, 1, 2u), Describe(plan.Microphone));
        Assert.Collection(
            plan.Encoders,
            route => Assert.Equal((1, 0, 0, 1u), Describe(route)),
            route => Assert.Equal((2, 1, 1, 2u), Describe(route)));
    }

    [Fact]
    public void PlansAndEncoderRoutesAreSharedImmutableInstances()
    {
        var first = AudioTrackPlan.Create(separateAudioTracks: true);
        var second = AudioTrackPlan.Create(separateAudioTracks: true);

        Assert.Same(first, second);
        Assert.Same(first.Desktop, first.Encoders[0]);
        Assert.Same(first.Microphone, first.Encoders[1]);
        Assert.IsAssignableFrom<IReadOnlyList<AudioTrackRoute>>(first.Encoders);
        Assert.False(first.Encoders is AudioTrackRoute[]);
    }

    private static (int SourceTrack, int Mixer, int Output, uint Mask) Describe(AudioTrackRoute route) =>
        (route.SourceTrackNumber, route.EncoderMixerIndex, route.OutputTrackIndex, route.SourceMixerMask);
}
