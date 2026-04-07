using TikrClipr.Core.Capture;

namespace TikrClipr.App.Helpers;

public static class EnumHelper
{
    public static EncoderType[] EncoderTypes { get; } = Enum.GetValues<EncoderType>();
    public static RateControlType[] RateControlTypes { get; } = Enum.GetValues<RateControlType>();
}
