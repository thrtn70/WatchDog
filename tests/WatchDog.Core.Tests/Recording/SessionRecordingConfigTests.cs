using WatchDog.Core.Recording;

namespace WatchDog.Core.Tests.Recording;

public sealed class SessionRecordingConfigTests
{
    [Fact]
    public void Defaults_ReplayBufferOnly()
    {
        var config = new SessionRecordingConfig();

        Assert.Equal(RecordingMode.ReplayBufferOnly, config.Mode);
        Assert.True(config.IsReplayBufferEnabled);
        Assert.False(config.IsSessionRecordingEnabled);
    }

    [Fact]
    public void SessionRecordingMode_EnablesSessionRecording()
    {
        var config = new SessionRecordingConfig { Mode = RecordingMode.SessionRecording };

        Assert.True(config.IsSessionRecordingEnabled);
        Assert.False(config.IsReplayBufferEnabled);
    }

    [Fact]
    public void BothMode_EnablesBothFeatures()
    {
        var config = new SessionRecordingConfig { Mode = RecordingMode.Both };

        Assert.True(config.IsSessionRecordingEnabled);
        Assert.True(config.IsReplayBufferEnabled);
    }

    [Fact]
    public void DefaultSegmentDuration_Is30Minutes()
    {
        var config = new SessionRecordingConfig();

        Assert.Equal(30, config.SegmentDurationMinutes);
    }

    [Fact]
    public void DefaultMaxDuration_IsUnlimited()
    {
        var config = new SessionRecordingConfig();

        Assert.Equal(0, config.MaxDurationMinutes);
    }

    [Fact]
    public void DefaultFileFormat_IsMp4()
    {
        var config = new SessionRecordingConfig();

        Assert.Equal("mp4", config.FileFormat);
    }

    [Fact]
    public void Config_IsImmutable_WithExpression()
    {
        var original = new SessionRecordingConfig();
        var modified = original with { Mode = RecordingMode.Both, SegmentDurationMinutes = 15 };

        Assert.Equal(RecordingMode.ReplayBufferOnly, original.Mode);
        Assert.Equal(30, original.SegmentDurationMinutes);
        Assert.Equal(RecordingMode.Both, modified.Mode);
        Assert.Equal(15, modified.SegmentDurationMinutes);
    }
}
