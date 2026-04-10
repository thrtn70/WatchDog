namespace WatchDog.Core.Highlights;

/// <summary>
/// A no-op highlight detector used when the real detector is unavailable
/// (e.g., missing ONNX model). Implements the interface but never fires events.
/// </summary>
public sealed class NoOpHighlightDetector : IHighlightDetector
{
    public string GameExecutableName => "__noop__";
    public bool IsRunning => false;

#pragma warning disable CS0067 // Event is never used (intentional — no-op detector)
    public event Action<HighlightDetectedEventArgs>? HighlightDetected;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
