using TikrClipr.Core.GameDetection;

namespace TikrClipr.Core.Recording;

public interface ISessionRecorder : IDisposable
{
    bool IsRecording { get; }
    TimeSpan Elapsed { get; }
    string? CurrentOutputPath { get; }

    Task StartAsync(string outputDirectory, GameInfo? game, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);

    event Action<string>? SegmentSaved;
    event Action<string>? Error;
}
