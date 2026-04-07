using TikrClipr.Core.Capture;
using TikrClipr.Core.Recording;

namespace TikrClipr.App.Helpers;

public static class EnumHelper
{
    public static EncoderType[] EncoderTypes { get; } = Enum.GetValues<EncoderType>();
    public static RateControlType[] RateControlTypes { get; } = Enum.GetValues<RateControlType>();
    public static RecordingMode[] RecordingModes { get; } = Enum.GetValues<RecordingMode>();
}
