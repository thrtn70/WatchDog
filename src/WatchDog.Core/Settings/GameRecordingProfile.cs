namespace WatchDog.Core.Settings;

public sealed record GameRecordingProfile
{
    public required string GameExecutableName { get; init; }
    public bool AutoRecord { get; init; }
}
