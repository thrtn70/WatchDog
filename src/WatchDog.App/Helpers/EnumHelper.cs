using WatchDog.App.ViewModels;
using WatchDog.Core.Capture;
using WatchDog.Core.Recording;

namespace WatchDog.App.Helpers;

public static class EnumHelper
{
    public static EncoderType[] EncoderTypes { get; } = Enum.GetValues<EncoderType>();
    public static RateControlType[] RateControlTypes { get; } = Enum.GetValues<RateControlType>();
    public static RecordingMode[] RecordingModes { get; } = Enum.GetValues<RecordingMode>();
    public static ClipSortMode[] ClipSortModes { get; } = Enum.GetValues<ClipSortMode>();
}
