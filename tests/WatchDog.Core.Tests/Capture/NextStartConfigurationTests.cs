using WatchDog.Core.Buffer;
using WatchDog.Core.Capture;
using WatchDog.Core.Recording;

namespace WatchDog.Core.Tests.Capture;

public sealed class NextStartConfigurationTests
{
    [Fact]
    public void PolicyAdmissionRejectsCanvasAndEncoderChangesWithoutMutatingInputs()
    {
        var canvas = new CaptureConfig();
        var buffer = new BufferConfig();
        var next = new CaptureStartPolicy(canvas with { Encoder = EncoderType.X264, Bitrate = 7_000 },
            buffer with { MaxSeconds = 30 }, new());
        Assert.Null(next.Validate(canvas));
        Assert.Equal("capture.policy.canvas-change-unsupported",
            (next with { Capture = next.Capture with { OutputWidth = 1280 } }).Validate(canvas)?.Code);
        Assert.Equal("capture.encoder.unsupported",
            (next with { Capture = next.Capture with { Encoder = EncoderType.AmfH264 } }).Validate(canvas)?.Code);
        Assert.Equal(EncoderType.NvencH264, canvas.Encoder);
        Assert.Equal(20_000, canvas.Bitrate);
        Assert.Equal(120, buffer.MaxSeconds);
        Assert.Equal(30, next.Buffer.MaxSeconds);
    }

    [Fact]
    public void InvalidDirectPolicyCannotBypassSanitizationOrModeAdmission()
    {
        var canvas = new CaptureConfig();
        var policy = new CaptureStartPolicy(canvas, new() { MaxSeconds = -1 }, new());
        Assert.Equal("capture.policy.invalid-values", policy.Validate(canvas)?.Code);
        Assert.Null(policy.Sanitized().Validate(canvas));
        Assert.Equal(-1, policy.Buffer.MaxSeconds);
        Assert.Equal("capture.policy.undefined-mode", (policy.Sanitized() with
        {
            Recording = new() { Mode = (RecordingMode)999 },
        }).Validate(canvas)?.Code);
        Assert.Equal("capture.policy.undefined-mode", (policy.Sanitized() with
        {
            Capture = canvas with { RateControl = (RateControlType)999 },
        }).Validate(canvas)?.Code);
    }
}
