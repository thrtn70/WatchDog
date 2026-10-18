using WatchDog.Core.Capture;

namespace WatchDog.Core.Tests.Capture;

public sealed class EncoderCapabilitiesTests
{
    [Theory]
    [InlineData(EncoderType.NvencH264)]
    [InlineData(EncoderType.NvencHevc)]
    [InlineData(EncoderType.NvencAv1)]
    [InlineData(EncoderType.X264)]
    public void ImplementedFactoryIsSelectableWithoutInventingHardwareProof(EncoderType encoder)
    {
        var option = EncoderCapabilities.Describe(encoder);
        Assert.True(option.IsImplemented);
        Assert.Null(EncoderCapabilities.Validate(encoder));
        Assert.Equal(encoder == EncoderType.X264
            ? EncoderAvailability.Software
            : EncoderAvailability.HardwareUnverified, option.Availability);
    }

    [Theory]
    [InlineData(EncoderType.AmfH264)]
    [InlineData(EncoderType.AmfHevc)]
    [InlineData((EncoderType)999)]
    public void UnsupportedSavedSelectionRemainsVisibleAndRequiresExplicitReplacement(EncoderType encoder)
    {
        var config = new CaptureConfig { Encoder = encoder };
        var options = EncoderCapabilities.OptionsForSavedSelection(config.Encoder);
        var saved = Assert.Single(options, option => option.Encoder == encoder);
        Assert.False(saved.IsImplemented);
        Assert.Contains("Select", saved.Status, StringComparison.Ordinal);
        Assert.Equal(encoder, config.Encoder);
        Assert.Equal(CaptureFailureKind.Unsupported, EncoderCapabilities.Validate(encoder)?.Kind);
        Assert.DoesNotContain(options.Where(option => option.IsImplemented),
            option => option.Encoder is EncoderType.AmfH264 or EncoderType.AmfHevc);

        // Describing/rejecting never falls back. Only the explicit editable choice does.
        var explicitlySelected = config with { Encoder = EncoderType.X264 };
        Assert.Null(EncoderCapabilities.Validate(explicitlySelected.Encoder));
        Assert.Equal(encoder, config.Encoder);
    }

    [Fact]
    public void OutputDescriptionsMatchIndependentFactories()
    {
        Assert.Contains("Replay", EncoderCapabilities.OutputBehavior, StringComparison.Ordinal);
        Assert.Contains("session", EncoderCapabilities.OutputBehavior, StringComparison.Ordinal);
        Assert.Contains("webcam", EncoderCapabilities.OutputBehavior, StringComparison.Ordinal);
        Assert.Contains("x264", EncoderCapabilities.OutputBehavior, StringComparison.Ordinal);
        Assert.All(EncoderCapabilities.ImplementedOptions.Where(o => o.Encoder != EncoderType.X264),
            option => Assert.Contains("unverified", option.Status, StringComparison.OrdinalIgnoreCase));
    }
}
