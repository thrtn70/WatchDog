using TikrClipr.Core.GameDetection;

namespace TikrClipr.Core.Recording;

/// <summary>
/// No-op implementation used when session recording is disabled.
/// Avoids allocating OBS encoders in ReplayBufferOnly mode.
/// </summary>
public sealed class NullSessionRecorder : ISessionRecorder
{
    public bool IsRecording => false;
    public TimeSpan Elapsed => TimeSpan.Zero;
    public string? CurrentOutputPath => null;

    public Task StartAsync(string outputDirectory, GameInfo? game, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task StopAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public event Action<string>? SegmentSaved;
    public event Action<string>? Error;

    public void Dispose() { }
}
