using WatchDog.Core.Capture;

namespace WatchDog.App.Services;

internal static class ConfiguredDisplayResolver
{
    public static DisplayCaptureTarget? Resolve(CaptureConfig config, ICaptureEngine captureEngine)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(captureEngine);

        // An ordinal cannot prove physical identity after displays are added, removed,
        // or reordered. Legacy index-only settings therefore require explicit reselection.
        return string.IsNullOrWhiteSpace(config.MonitorStableId)
            ? null
            : captureEngine.ResolveDisplayTarget(config.MonitorStableId);
    }
}
