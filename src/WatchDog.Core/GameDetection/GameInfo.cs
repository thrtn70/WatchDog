namespace WatchDog.Core.GameDetection;

public sealed record GameInfo
{
    public required string ExecutableName { get; init; }
    public required string DisplayName { get; init; }
    public int ProcessId { get; init; }
    public string? WindowTitle { get; init; }
}
