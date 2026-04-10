namespace WatchDog.Core.Highlights;

/// <summary>
/// A no-op highlight detector used when the real detector is unavailable
/// (e.g., missing ONNX model). Implements the interface but never fires events.
/// </summary>
public sealed class NoOpHighlightDetector : IHighlightDetector
{
    public string GameExecutableName => "__noop__";
    public bool IsRunning => false;

    public event Action<HighlightDetectedEventArgs>? HighlightDetected;

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
