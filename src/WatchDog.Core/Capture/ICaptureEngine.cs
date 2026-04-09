using WatchDog.Core.GameDetection;

namespace WatchDog.Core.Capture;

public interface ICaptureEngine : IDisposable
{
    CaptureState State { get; }
    CaptureConfig Config { get; }
    bool IsDesktopCapture { get; }
    GameInfo? CurrentGame { get; }

    Task StartDesktopCaptureAsync(CancellationToken ct = default);
    Task StartAsync(GameInfo game, CancellationToken ct = default);
    Task SwitchToDesktopCaptureAsync(CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
    Task<string?> SaveReplayAsync(CancellationToken ct = default);

    event Action<CaptureState>? StateChanged;
    event Action<string>? ClipSaved;
    event Action<string>? Error;
}
