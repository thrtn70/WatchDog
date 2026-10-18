using WatchDog.Core.Capture;
using WatchDog.Core.GameDetection;

namespace WatchDog.Core.Recording;

public interface ISessionRecorder : IDisposable
{
    bool IsRecording { get; }
    bool HasActiveResources { get; }
    TimeSpan Elapsed { get; }
    string? CurrentOutputPath { get; }

    Task StartAsync(string outputDirectory, GameInfo? game, CancellationToken ct = default);

    Task StartAsync(
        string outputDirectory,
        GameInfo? game,
        CaptureSessionContext context,
        CancellationToken ct = default) =>
        StartAsync(outputDirectory, game, ct);

    Task StopAsync(CancellationToken ct = default);

    event Action<string>? SegmentSaved;
    event Action<string>? Error;
    event Action? Stopped;
}
