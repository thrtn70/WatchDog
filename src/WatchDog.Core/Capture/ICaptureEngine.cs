using WatchDog.Core.GameDetection;
using WatchDog.Core.Devices;

namespace WatchDog.Core.Capture;

public interface ICaptureEngine : IDisposable
{
    CaptureState State { get; }
    CaptureConfig Config { get; }
    bool IsDesktopCapture { get; }
    bool IsReplayBufferActive { get; }
    GameInfo? CurrentGame { get; }
    /// <summary>The frozen audio selections that actually crossed source admission.</summary>
    CaptureDeviceManifest AdmittedAudioDevices => CaptureDeviceManifest.Empty;

    /// <summary>
    /// Ensures the underlying capture backend is initialized and ready.
    /// Safe to call multiple times — subsequent calls are no-ops.
    /// </summary>
    void EnsureInitialized();

    /// <summary>Applies validated managed configuration while idle; no native operation or live reset.</summary>
    CaptureFailure? ApplyNextStartConfiguration(CaptureConfig capture, WatchDog.Core.Buffer.BufferConfig buffer) =>
        new(CaptureFailureKind.Unsupported, "capture.policy.engine-unsupported",
            "This engine cannot apply next-start configuration. Use a backend with an admitted managed configuration path.");

    /// <summary>Resolves the configured monitor index to a physical display identity.</summary>
    DisplayCaptureTarget? ResolveDisplayTarget(int monitorIndex);

    /// <summary>Resolves a previously persisted stable physical display identity.</summary>
    DisplayCaptureTarget? ResolveDisplayTarget(string stableDisplayId);

    /// <summary>Maps a Windows display device name to its stable physical identity.</summary>
    DisplayCaptureTarget? ResolveDisplayTargetByDeviceName(string deviceName);

    /// <summary>Checks whether a previously resolved physical display identity still exists.</summary>
    bool IsDisplayTargetAvailable(DisplayCaptureTarget target);

    /// <summary>
    /// Resolves a physical-pixel region against the current geometry of its stable owning display.
    /// Returns null rather than clamping, crossing displays, or choosing another display.
    /// </summary>
    RegionCaptureTarget? ResolveRegionTarget(
        string owningDisplayId,
        PhysicalCaptureBounds physicalBounds);

    /// <summary>Checks that the region's stable display identity and frozen geometry still match.</summary>
    bool IsRegionTargetAvailable(RegionCaptureTarget target);

    /// <summary>Checks that the exact window remains uniquely representable by the OBS binding.</summary>
    bool IsWindowTargetAvailable(WindowCaptureTarget target);

    /// <summary>
    /// Resolves an exact process identity to its current main window. Returns null when
    /// the process or window cannot be proven, rather than widening to executable-wide capture.
    /// </summary>
    WindowCaptureTarget? ResolveGameWindowTarget(GameCaptureTarget game);

    Task StartDesktopCaptureAsync(
        DisplayCaptureTarget target,
        bool startReplayBuffer,
        CancellationToken ct = default);

    Task StartRegionCaptureAsync(
        RegionCaptureTarget target,
        bool startReplayBuffer,
        CancellationToken ct = default);

    /// <summary>
    /// Starts capturing a specific window by its capture source metadata.
    /// Fails closed if the exact selected window is no longer available.
    /// </summary>
    Task StartWindowCaptureAsync(
        CaptureSource source,
        bool startReplayBuffer,
        CancellationToken ct = default);

    /// <summary>
    /// Stops capture and releases per-capture sources, audio devices, outputs, and encoders.
    /// The initialized backend may remain owned until terminal disposal.
    /// </summary>
    Task StopAsync(CancellationToken ct = default);
    Task<string?> SaveReplayAsync(CancellationToken ct = default);

    event Action<CaptureState>? StateChanged;
    event Action<string>? ClipSaved;
    event Action<string>? Error;
}
