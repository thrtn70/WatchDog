namespace WatchDog.Core.Capture;

public sealed record MonitorInfo(
    int Index,
    string StableDisplayId,
    string DeviceName,
    string DisplayName,
    int Width,
    int Height,
    bool IsPrimary);
