using WatchDog.Core.Audio;

namespace WatchDog.Core.Tests.Audio;

public sealed class AudioMixConfigTests
{
    [Fact]
    public void Defaults_AllEnabled_FullVolume()
    {
        var config = new AudioMixConfig();

        Assert.True(config.DesktopAudioEnabled);
        Assert.True(config.MicEnabled);
        Assert.Equal(1.0f, config.DesktopVolume);
        Assert.Equal(1.0f, config.MicVolume);
        Assert.False(config.SeparateAudioTracks);
    }

    [Fact]
    public void Config_IsImmutable_WithExpression()
    {
        var original = new AudioMixConfig();
        var modified = original with { DesktopVolume = 0.5f, MicEnabled = false };

        Assert.Equal(1.0f, original.DesktopVolume);
        Assert.True(original.MicEnabled);
        Assert.Equal(0.5f, modified.DesktopVolume);
        Assert.False(modified.MicEnabled);
    }

    [Fact]
    public void SeparateAudioTracks_DefaultsFalse()
    {
        var config = new AudioMixConfig();
        Assert.False(config.SeparateAudioTracks);
    }

    [Fact]
    public void CanDisableDesktopAudio()
    {
        var config = new AudioMixConfig { DesktopAudioEnabled = false };
        Assert.False(config.DesktopAudioEnabled);
    }
}
