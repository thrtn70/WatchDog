using WatchDog.Core.Capture;
using WatchDog.Core.GameDetection;

namespace WatchDog.Core.Tests.Capture;

public sealed class ExactGameWindowAcquisitionTests
{
    [Fact]
    public async Task DelayedMainWindow_IsResolvedWithoutWideningTarget()
    {
        var engine = new Engine(resolveOnAttempt: 3);
        var target = new GameCaptureTarget(
            "game",
            42,
            "game.exe",
            DateTimeOffset.UnixEpoch,
            "Game");

        var resolved = await ExactGameWindowAcquisition.ResolveAsync(
            engine,
            target,
            maxAttempts: 5,
            retryDelay: TimeSpan.Zero,
            delayAsync: static (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(target.ProcessId, resolved.ProcessId);
        Assert.Equal(new IntPtr(0x1234), resolved.WindowHandle);
        Assert.Equal(3, engine.ResolveAttempts);
    }

    [Fact]
    public async Task CancellationStopsResolutionBeforeAnotherAttempt()
    {
        var engine = new Engine(resolveOnAttempt: int.MaxValue);
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ExactGameWindowAcquisition.ResolveAsync(
                engine,
                new GameCaptureTarget("game", 42, "game.exe", DateTimeOffset.UnixEpoch),
                maxAttempts: 5,
                retryDelay: TimeSpan.Zero,
                delayAsync: (_, _) =>
                {
                    cancellation.Cancel();
                    return Task.CompletedTask;
                },
                cancellation.Token));

        Assert.Equal(1, engine.ResolveAttempts);
    }

#pragma warning disable CS0067
    private sealed class Engine(int resolveOnAttempt) : ICaptureEngine
    {
        public int ResolveAttempts { get; private set; }
        public CaptureState State => CaptureState.Idle;
        public CaptureConfig Config { get; } = new();
        public bool IsDesktopCapture => false;
        public bool IsReplayBufferActive => false;
        public GameInfo? CurrentGame => null;
        public event Action<CaptureState>? StateChanged;
        public event Action<string>? ClipSaved;
        public event Action<string>? Error;
        public void EnsureInitialized() { }
        public DisplayCaptureTarget? ResolveDisplayTarget(int monitorIndex) => null;
        public DisplayCaptureTarget? ResolveDisplayTarget(string stableDisplayId) => null;
        public DisplayCaptureTarget? ResolveDisplayTargetByDeviceName(string deviceName) => null;
        public bool IsDisplayTargetAvailable(DisplayCaptureTarget target) => false;
        public RegionCaptureTarget? ResolveRegionTarget(string owningDisplayId, PhysicalCaptureBounds physicalBounds) => null;
        public bool IsRegionTargetAvailable(RegionCaptureTarget target) => false;
        public bool IsWindowTargetAvailable(WindowCaptureTarget target) => true;
        public WindowCaptureTarget? ResolveGameWindowTarget(GameCaptureTarget game) =>
            ++ResolveAttempts >= resolveOnAttempt
                ? new WindowCaptureTarget(game.ProcessId, new IntPtr(0x1234), "Game", game.ExecutableName)
                : null;
        public Task StartDesktopCaptureAsync(DisplayCaptureTarget target, bool startReplayBuffer, CancellationToken ct = default) => Task.CompletedTask;
        public Task StartRegionCaptureAsync(RegionCaptureTarget target, bool startReplayBuffer, CancellationToken ct = default) => Task.CompletedTask;
        public Task StartWindowCaptureAsync(CaptureSource source, bool startReplayBuffer, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> SaveReplayAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public void Dispose() { }
    }
#pragma warning restore CS0067
}
