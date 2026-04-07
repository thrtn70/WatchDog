using TikrClipr.Core.Capture;

namespace TikrClipr.Core.Tests.Capture;

public sealed class CaptureStateTests
{
    [Theory]
    [InlineData(CaptureState.Idle, CaptureState.Initializing, true)]
    [InlineData(CaptureState.Initializing, CaptureState.Buffering, true)]
    [InlineData(CaptureState.Initializing, CaptureState.Idle, true)]
    [InlineData(CaptureState.Buffering, CaptureState.Saving, true)]
    [InlineData(CaptureState.Buffering, CaptureState.Stopping, true)]
    [InlineData(CaptureState.Saving, CaptureState.Buffering, true)]
    [InlineData(CaptureState.Saving, CaptureState.Stopping, true)]
    [InlineData(CaptureState.Stopping, CaptureState.Idle, true)]
    public void ValidTransitions_AreAllowed(CaptureState from, CaptureState to, bool expected)
    {
        Assert.Equal(expected, CaptureStateTransitions.IsValid(from, to));
    }

    [Theory]
    [InlineData(CaptureState.Idle, CaptureState.Buffering)]
    [InlineData(CaptureState.Idle, CaptureState.Saving)]
    [InlineData(CaptureState.Idle, CaptureState.Stopping)]
    [InlineData(CaptureState.Initializing, CaptureState.Saving)]
    [InlineData(CaptureState.Initializing, CaptureState.Stopping)]
    [InlineData(CaptureState.Buffering, CaptureState.Idle)]
    [InlineData(CaptureState.Buffering, CaptureState.Initializing)]
    [InlineData(CaptureState.Saving, CaptureState.Idle)]
    [InlineData(CaptureState.Saving, CaptureState.Initializing)]
    [InlineData(CaptureState.Stopping, CaptureState.Buffering)]
    [InlineData(CaptureState.Stopping, CaptureState.Saving)]
    public void InvalidTransitions_AreRejected(CaptureState from, CaptureState to)
    {
        Assert.False(CaptureStateTransitions.IsValid(from, to));
    }

    [Fact]
    public void SameState_IsNotValid()
    {
        Assert.False(CaptureStateTransitions.IsValid(CaptureState.Idle, CaptureState.Idle));
        Assert.False(CaptureStateTransitions.IsValid(CaptureState.Buffering, CaptureState.Buffering));
    }
}
