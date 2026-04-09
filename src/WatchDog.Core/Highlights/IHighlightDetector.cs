namespace WatchDog.Core.Highlights;

public interface IHighlightDetector : IAsyncDisposable
{
    string GameExecutableName { get; }
    IReadOnlyList<string> SupportedExecutableNames => [GameExecutableName];
    bool IsRunning { get; }

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    event Action<HighlightDetectedEventArgs>? HighlightDetected;
}

public sealed record HighlightDetectedEventArgs(
    HighlightType Type,
    string? Description = null);
