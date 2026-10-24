using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Audio;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Audio;

public sealed class ObsAudioMixerTests
{
    [Fact]
    public void ApplyConfigPublishesOneCombinedSnapshot()
    {
        var snapshots = new List<(float DesktopVolume, bool DesktopMuted, float MicVolume, bool MicMuted)>();
        var mixer = new ObsAudioMixer(
            (desktopVolume, desktopMuted, micVolume, micMuted) =>
                snapshots.Add((desktopVolume, desktopMuted, micVolume, micMuted)),
            NullLogger<ObsAudioMixer>.Instance);

        mixer.ApplyConfig(new AudioMixConfig
        {
            DesktopAudioEnabled = true,
            DesktopVolume = 0.25f,
            MicEnabled = false,
            MicVolume = 0.75f,
        });

        Assert.Equal([(0.25f, false, 0.75f, true)], snapshots);
    }

    [Fact]
    public void RebindAppliesRetainedVolumeAndMuteState()
    {
        (float Volume, bool IsMuted) desktop = default;
        (float Volume, bool IsMuted) mic = default;
        var mixer = new ObsAudioMixer(
            (desktopVolume, desktopMuted, micVolume, micMuted) =>
            {
                desktop = (desktopVolume, desktopMuted);
                mic = (micVolume, micMuted);
            },
            NullLogger<ObsAudioMixer>.Instance);

        mixer.SetDesktopVolume(0.25f);
        mixer.ToggleDesktopMute();
        mixer.SetMicVolume(0.75f);
        mixer.ToggleMicMute();

        desktop = default;
        mic = default;
        mixer.ApplyToCurrentSources();

        Assert.Equal((0.25f, true), desktop);
        Assert.Equal((0.75f, true), mic);
    }

    [Fact]
    public async Task ConcurrentRebindAndMutationLeaveAppliedStateConsistent()
    {
        var firstApplyEntered = new ManualResetEventSlim();
        var releaseFirstApply = new ManualResetEventSlim();
        var mutationAttempting = new ManualResetEventSlim();
        var applyCount = 0;
        (float Volume, bool IsMuted) applied = default;
        var mixer = new ObsAudioMixer(
            (desktopVolume, desktopMuted, _, _) =>
            {
                if (Interlocked.Increment(ref applyCount) == 1)
                {
                    firstApplyEntered.Set();
                    releaseFirstApply.Wait(TestTimeouts.Signal);
                }
                applied = (desktopVolume, desktopMuted);
            },
            NullLogger<ObsAudioMixer>.Instance);

        var rebind = Task.Run(mixer.ApplyToCurrentSources);
        Assert.True(firstApplyEntered.Wait(TestTimeouts.Signal));
        var mutation = Task.Run(() =>
        {
            mutationAttempting.Set();
            mixer.SetDesktopVolume(0.4f);
            mixer.ToggleDesktopMute();
        });

        Assert.True(mutationAttempting.Wait(TestTimeouts.Signal));
        Assert.False(mutation.IsCompleted);
        releaseFirstApply.Set();
        await Task.WhenAll(rebind, mutation);

        Assert.Equal((mixer.DesktopVolume, mixer.IsDesktopMuted), applied);
        Assert.Equal((0.4f, true), applied);
    }
}
