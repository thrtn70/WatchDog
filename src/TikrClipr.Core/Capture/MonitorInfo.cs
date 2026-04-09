namespace TikrClipr.Core.Capture;

public sealed record MonitorInfo(
    int Index,
    string DeviceName,
    string DisplayName,
    int Width,
    int Height,
    bool IsPrimary);
