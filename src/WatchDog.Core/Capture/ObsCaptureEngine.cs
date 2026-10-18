using System.Diagnostics;
using Microsoft.Extensions.Logging;
using ObsKit.NET;
using ObsKit.NET.Core;
using ObsKit.NET.Encoders;
using ObsKit.NET.Filters;
using ObsKit.NET.Scenes;
using ObsKit.NET.Sources;
using WatchDog.Core.Audio;
using WatchDog.Core.Buffer;
using WatchDog.Core.Devices;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Native.Obs;
using WatchDog.Native.Win32;

namespace WatchDog.Core.Capture;

public sealed class ObsCaptureEngine : ICaptureEngine
{
    private readonly ILogger<ObsCaptureEngine> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEventBus _eventBus;
    private BufferConfig _bufferConfig;
    private readonly CaptureConfig _startupCanvas;
    private readonly AudioMixConfig _audioConfig;
    private readonly AudioTrackPlan _audioTrackPlan;
    private readonly IAudioDeviceEnumerator _audioDeviceEnumerator;
    private readonly object _audioSourceGate = new();

    private ObsContext? _obsContext;
    private Scene? _scene;
    private SceneItem? _windowSceneItem;
    private SceneItem? _displaySceneItem;
    private WindowCapture? _windowCapture;
    private MonitorCapture? _displayCapture;
    private CropFilter? _regionCropFilter;
    private VideoEncoder? _videoEncoder;
    private readonly List<AudioTrackEncoderBinding> _audioEncoders = [];
    private AudioOutputCapture? _desktopAudio;
    private AudioInputCapture? _micAudio;
    private ObsReplayBuffer? _replayBuffer;
    private bool _obsInitialized;
    private bool _obsInitializationFailed;
    private bool _audioSourcesConfigured;
    private bool _audioSourceCleanupRequired;
    private CaptureDeviceManifest _admittedAudioDevices = CaptureDeviceManifest.Empty;
    private bool _terminalShutdownRequested;
    private bool _disposed;

    private readonly SemaphoreSlim _stateLock = new(1, 1);

    public CaptureState State { get; private set; } = CaptureState.Idle;
    public CaptureConfig Config { get; private set; }
    public bool IsDesktopCapture { get; private set; }
    public bool IsReplayBufferActive => _replayBuffer?.IsActive ?? false;
    public GameInfo? CurrentGame { get; private set; }
    public CaptureDeviceManifest AdmittedAudioDevices
    {
        get { lock (_audioSourceGate) return _admittedAudioDevices; }
    }

    // Diagnostic access only. Production mixer mutation uses ApplyAudioMixerState
    // so native wrappers cannot be disposed between lookup and mutation.
    public AudioOutputCapture? DesktopAudioSource { get { lock (_audioSourceGate) return _desktopAudio; } }
    public AudioInputCapture? MicAudioSource { get { lock (_audioSourceGate) return _micAudio; } }

    /// <summary>
    /// Raised after per-capture audio sources are recreated so the singleton mixer can
    /// apply its retained live volume and mute state to the new OBS source wrappers.
    /// </summary>
    public event Action? AudioSourcesCreated;

    public event Action<CaptureState>? StateChanged;
    public event Action<string>? ClipSaved;
    public event Action<string>? Error;

    public ObsCaptureEngine(
        CaptureConfig captureConfig,
        BufferConfig bufferConfig,
        AudioMixConfig audioConfig,
        IEventBus eventBus,
        ILoggerFactory loggerFactory,
        IAudioDeviceEnumerator? audioDeviceEnumerator = null,
        AudioTrackPlan? audioTrackPlan = null)
    {
        Config = captureConfig;
        _startupCanvas = captureConfig;
        _bufferConfig = bufferConfig;
        _audioConfig = audioConfig;
        _audioTrackPlan = audioTrackPlan ?? AudioTrackPlan.Create(audioConfig.SeparateAudioTracks);
        if (_audioTrackPlan.SeparateAudioTracks != audioConfig.SeparateAudioTracks)
            throw new ArgumentException("The audio track plan does not match the configured separate-track mode.", nameof(audioTrackPlan));
        _eventBus = eventBus;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ObsCaptureEngine>();
        _audioDeviceEnumerator = audioDeviceEnumerator ?? new WindowsAudioDeviceEnumerator(
            loggerFactory.CreateLogger<WindowsAudioDeviceEnumerator>());
    }

    /// <summary>
    /// Applies the complete retained mixer state while holding the same gate used to
    /// publish and dispose native audio source wrappers.
    /// </summary>
    public void ApplyAudioMixerState(
        float desktopVolume,
        bool desktopMuted,
        float microphoneVolume,
        bool microphoneMuted)
    {
        lock (_audioSourceGate)
        {
            if (_audioSourceCleanupRequired)
                return;

            if (_desktopAudio is not null)
            {
                _desktopAudio.Volume = desktopVolume;
                _desktopAudio.IsMuted = desktopMuted;
            }

            if (_micAudio is not null)
            {
                _micAudio.Volume = microphoneVolume;
                _micAudio.IsMuted = microphoneMuted;
            }
        }
    }

    public DisplayCaptureTarget? ResolveDisplayTarget(int monitorIndex)
    {
        try
        {
            var monitors = MonitorCapture.AvailableMonitors;
            if (monitorIndex < 0 || monitorIndex >= monitors.Count)
                return null;

            var monitor = monitors[monitorIndex];
            var stableId = GetStableDisplayId(monitor);
            return string.IsNullOrWhiteSpace(stableId) || FindMonitor(stableId) is null
                ? null
                : new DisplayCaptureTarget(stableId, monitor.Name, monitorIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve configured display {Index}", monitorIndex);
            return null;
        }
    }

    public DisplayCaptureTarget? ResolveDisplayTarget(string stableDisplayId)
    {
        try
        {
            var monitor = FindMonitor(stableDisplayId);
            return monitor is null
                ? null
                : new DisplayCaptureTarget(stableDisplayId, monitor.Name, monitor.Index);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve stable display identity {StableId}", stableDisplayId);
            return null;
        }
    }

    public DisplayCaptureTarget? ResolveDisplayTargetByDeviceName(string deviceName)
    {
        try
        {
            var matches = MonitorCapture.AvailableMonitors
                .Where(monitor => StringComparer.OrdinalIgnoreCase.Equals(monitor.DeviceName, deviceName))
                .ToArray();
            if (matches.Length != 1)
                return null;

            var monitor = matches[0];
            var stableId = GetStableDisplayId(monitor);
            return string.IsNullOrWhiteSpace(stableId)
                ? null
                : new DisplayCaptureTarget(stableId, monitor.Name, monitor.Index);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to map display device {DeviceName} to a stable identity", deviceName);
            return null;
        }
    }

    public bool IsDisplayTargetAvailable(DisplayCaptureTarget target)
    {
        try
        {
            return FindMonitor(target.StableDisplayId) is not null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to revalidate display identity {StableId}", target.StableDisplayId);
            return false;
        }
    }

    public RegionCaptureTarget? ResolveRegionTarget(
        string owningDisplayId,
        PhysicalCaptureBounds physicalBounds)
    {
        try
        {
            var monitor = FindMonitor(owningDisplayId);
            if (monitor is null)
                return null;

            var target = new RegionCaptureTarget(
                owningDisplayId,
                physicalBounds,
                GetPhysicalBounds(monitor));
            return RegionCaptureGeometry.TryGetDisplayLocalBounds(target, out _) ? target : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve region on display {StableId}", owningDisplayId);
            return null;
        }
    }

    public bool IsRegionTargetAvailable(RegionCaptureTarget target)
    {
        try
        {
            var monitor = FindMonitor(target.OwningDisplayId);
            return monitor is not null &&
                   GetPhysicalBounds(monitor) == target.OwningDisplayBounds &&
                   RegionCaptureGeometry.TryGetDisplayLocalBounds(target, out _);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to revalidate region on display {StableId}", target.OwningDisplayId);
            return false;
        }
    }

    public bool IsWindowTargetAvailable(WindowCaptureTarget target)
    {
        try
        {
            var window = FindUnambiguousWindow(target.WindowHandle, target.ProcessId);
            return window is not null && WindowMetadataMatches(
                window,
                target.WindowTitle,
                target.WindowClass,
                target.ExecutableName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to revalidate window {Handle} for PID {Pid}", target.WindowHandle, target.ProcessId);
            return false;
        }
    }

    public WindowCaptureTarget? ResolveGameWindowTarget(GameCaptureTarget game)
    {
        if (game.ProcessStartedAt is null)
            return null;

        try
        {
            using var process = Process.GetProcessById(game.ProcessId);
            if (process.HasExited ||
                process.StartTime.ToUniversalTime() != game.ProcessStartedAt.Value.UtcDateTime ||
                !string.Equals(
                    process.ProcessName + ".exe",
                    Path.GetFileName(game.ExecutableName),
                    StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var mainWindowHandle = process.MainWindowHandle;
            if (mainWindowHandle == IntPtr.Zero)
                return null;

            var window = FindUnambiguousWindow(mainWindowHandle, game.ProcessId);
            return window is null
                ? null
                : new WindowCaptureTarget(
                    window.ProcessId,
                    window.Handle,
                    window.Title,
                    window.ProcessName,
                    window.ClassName);
        }
        catch
        {
            return null;
        }
    }

    public async Task StartDesktopCaptureAsync(
        DisplayCaptureTarget target,
        bool startReplayBuffer,
        CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            ThrowIfTerminalShutdownRequested();

            if (State == CaptureState.Buffering && IsDesktopCapture)
                return; // Already in desktop capture mode

            if (!TransitionState(CaptureState.Initializing))
                return;

            _logger.LogInformation("Starting desktop capture");

            EnsureCaptureResourcesInitialized();
            SetupDesktopSources(target);
            if (startReplayBuffer)
                StartReplayBuffer("Desktop");

            IsDesktopCapture = true;
            CurrentGame = null;
            TransitionState(CaptureState.Buffering);
            _logger.LogInformation("Desktop capture started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start desktop capture");
            Error?.Invoke($"Failed to start desktop capture: {ex.Message}");
            try
            {
                await CleanupCaptureResourcesAsync();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Desktop capture failed to start and cleanup was incomplete.",
                    ex,
                    cleanupFailure);
            }
            TransitionToIdleAfterCleanup();
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task StartRegionCaptureAsync(
        RegionCaptureTarget target,
        bool startReplayBuffer,
        CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            ThrowIfTerminalShutdownRequested();

            if (!TransitionState(CaptureState.Initializing))
                return;

            _logger.LogInformation(
                "Starting physical region capture on {StableId}: {Bounds}",
                target.OwningDisplayId,
                target.PhysicalBounds);

            EnsureCaptureResourcesInitialized();
            SetupRegionSources(target);
            if (startReplayBuffer)
                StartReplayBuffer("Region");

            IsDesktopCapture = true;
            CurrentGame = null;
            TransitionState(CaptureState.Buffering);
            _logger.LogInformation("Physical region capture started");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start physical region capture");
            Error?.Invoke($"Failed to start region capture: {ex.Message}");
            try
            {
                await CleanupCaptureResourcesAsync();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Region capture failed to start and cleanup was incomplete.",
                    ex,
                    cleanupFailure);
            }
            TransitionToIdleAfterCleanup();
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task StartWindowCaptureAsync(
        CaptureSource source,
        bool startReplayBuffer,
        CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            ThrowIfTerminalShutdownRequested();

            var gameInfo = source.ToGameInfo();
            CurrentGame = gameInfo;
            _logger.LogInformation("Starting window capture for {Window} ({Exe}, PID {Pid})",
                source.DisplayName, source.ExecutableName, source.ProcessId);

            if (State == CaptureState.Buffering)
            {
                // Already running — switch sources to window capture
                StopReplayBuffer();
                SetupWindowCaptureSources(source);
                if (startReplayBuffer)
                    StartReplayBuffer(source.DisplayName);
                IsDesktopCapture = false;
                _logger.LogInformation("Switched to window capture for {Window}", source.DisplayName);
            }
            else
            {
                // Cold start
                if (!TransitionState(CaptureState.Initializing))
                    return;

                EnsureCaptureResourcesInitialized();
                SetupWindowCaptureSources(source);
                if (startReplayBuffer)
                    StartReplayBuffer(source.DisplayName);

                IsDesktopCapture = false;
                TransitionState(CaptureState.Buffering);
                _logger.LogInformation("Window capture started for {Window}", source.DisplayName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start window capture for {Window}", source.DisplayName);
            Error?.Invoke($"Failed to start window capture: {ex.Message}");
            try
            {
                await CleanupCaptureResourcesAsync();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Window capture failed to start and cleanup was incomplete.",
                    ex,
                    cleanupFailure);
            }
            TransitionToIdleAfterCleanup();
            throw;
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            ThrowIfTerminalShutdownRequested();

            // Even Idle can own partially initialized resources after a failed start.
            if (State != CaptureState.Idle)
                TransitionState(CaptureState.Stopping);
            _logger.LogInformation("Stopping capture");

            await CleanupCaptureResourcesAsync();

            CurrentGame = null;
            IsDesktopCapture = false;
            TransitionState(CaptureState.Idle);
            _logger.LogInformation("Capture stopped");
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task<string?> SaveReplayAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            ThrowIfTerminalShutdownRequested();

            if (State != CaptureState.Buffering)
            {
                _logger.LogWarning("Cannot save replay in state {State}", State);
                return null;
            }

            TransitionState(CaptureState.Saving);

            var path = await (_replayBuffer?.SaveAsync(ct) ?? Task.FromResult<string?>(null));

            if (path is not null)
            {
                _logger.LogInformation("Clip saved: {Path}", path);
                // Note: ClipSaved + ClipSavedEvent are published by the _replayBuffer.Saved callback
                // (registered in InitializeReplayBuffer). Do NOT publish again here — it causes duplicate clips.
            }

            return path;
        }
        finally
        {
            if (State == CaptureState.Saving)
                TransitionState(CaptureState.Buffering);
            _stateLock.Release();
        }
    }

    // ── OBS lifecycle (called once) ──────────────────────────────────────

    public CaptureFailure? ApplyNextStartConfiguration(CaptureConfig capture, BufferConfig buffer)
    {
        _stateLock.Wait();
        try
        {
            ThrowIfTerminalShutdownRequested();
            var policy = new CaptureStartPolicy(capture, buffer, new());
            if (policy.Validate(_startupCanvas) is { } failure)
                return failure;
            lock (_audioSourceGate)
            {
                if (_disposed || _obsInitializationFailed || State != CaptureState.Idle ||
                    _replayBuffer is not null || _scene is not null || _windowCapture is not null ||
                    _displayCapture is not null || _regionCropFilter is not null ||
                    _videoEncoder is not null || _audioEncoders.Count != 0 ||
                    _desktopAudio is not null || _micAudio is not null || _audioSourceCleanupRequired)
                {
                    return new(CaptureFailureKind.InvalidState, "capture.policy.engine-not-idle",
                        "Stop capture and finish cleanup before applying next-start configuration.");
                }
                Config = capture;
                _bufferConfig = buffer;
            }
            return null;
        }
        finally { _stateLock.Release(); }
    }

    /// <inheritdoc />
    public void EnsureInitialized()
    {
        // Acquire the state lock to prevent concurrent initialization races
        // (e.g., CaptureSourceManager calling this while OnGameStarted
        // fires on a background thread and enters StartAsync).
        _stateLock.Wait();
        try
        {
            ThrowIfTerminalShutdownRequested();
            EnsureObsContextInitialized();
        }
        finally
        {
            _stateLock.Release();
        }
    }

    private void EnsureObsContextInitialized()
    {
        if (_obsInitialized) return;

        if (_obsInitializationFailed)
        {
            throw new InvalidOperationException(
                "OBS initialization previously failed in this process. Restart WatchDog before trying capture again.");
        }

        if (!ObsRuntime.IsAvailable())
        {
            var msg = ObsRuntime.GetMissingComponentsMessage();
            _logger.LogError("{Message}", msg);
            Error?.Invoke(msg);
            throw new InvalidOperationException(msg);
        }

        try
        {
            InitializeObs();
            _obsInitialized = true;
        }
        catch
        {
            // Native module initialization may have started background threads even
            // when the managed call fails. Never attempt an uncertain unload/reload.
            _obsInitializationFailed = true;
            throw;
        }
    }

    private void EnsureCaptureResourcesInitialized()
    {
        EnsureObsContextInitialized();

        if (AudioSourcesNeedCreation())
            CreateAudioSources();

        if (_videoEncoder is null || _audioEncoders.Count != _audioTrackPlan.EncoderCount)
        {
            _videoEncoder?.Dispose();
            _videoEncoder = null;
            DisposeAudioEncoders();
            CreateEncoders();
        }
    }

    private void InitializeObs()
    {
        Obs.AutoDispose = false;

        // Use the configured output resolution as the OBS canvas.
        // Sources are stretched to fill this canvas via SetBounds,
        // so clips are always at the desired resolution regardless
        // of the game's native resolution or aspect ratio.
        var outW = Config.OutputWidth;
        var outH = Config.OutputHeight;

        _logger.LogInformation("OBS canvas: {Width}x{Height} (configured output)", outW, outH);

        _obsContext = Obs.Initialize(config => config
            .WithLocale("en-US")
            .WithDataPath(ObsRuntime.DataPath)
            .WithModulePath(ObsRuntime.ModuleBinPath, ObsRuntime.ModuleDataPath)
            .ForHeadlessOperation()
            // These bundled hardware UI modules also require OBS frontend callbacks.
            // ForHeadlessOperation excludes the main frontend modules, but not these.
            .ExcludeModule("aja-output-ui")
            .ExcludeModule("decklink-output-ui")
            .ExcludeModule("decklink-captions")
                .ExcludeModule("rtmp-services")
            .WithVideo(v => v
                .Resolution(outW, outH)
                .Fps(Config.Fps))
            .WithAudio(a => a
                .WithSampleRate(48000)
                .WithSpeakers(ObsKit.NET.Native.Types.SpeakerLayout.Stereo))
            .WithLogging((level, message) =>
            {
                var logLevel = level switch
                {
                    ObsKit.NET.Native.Types.ObsLogLevel.Error => LogLevel.Error,
                    ObsKit.NET.Native.Types.ObsLogLevel.Warning => LogLevel.Warning,
                    ObsKit.NET.Native.Types.ObsLogLevel.Info => LogLevel.Information,
                    _ => LogLevel.Debug
                };
                _logger.Log(logLevel, "[OBS] {Message}", message);
            }));

        _logger.LogInformation("OBS initialized: {Version}", Obs.Version);
    }

    private void CreateAudioSources()
    {
        lock (_audioSourceGate)
        {
            _admittedAudioDevices = CaptureDeviceManifest.Empty;
            string? resolvedDesktopDeviceId = null;
            string? resolvedMicrophoneDeviceId = null;
            // Resolve every enabled direction before either native factory runs.
            var created = AudioSourceSetupTransaction.ResolveAndCreate(
                _audioConfig.DesktopAudioEnabled,
                () => resolvedDesktopDeviceId = ResolveAudioDeviceId(
                    _audioConfig.DesktopDeviceId,
                    _audioDeviceEnumerator.GetOutputDevicesOrThrow,
                    "desktop/output",
                    CaptureDeviceRole.DesktopAudio),
                CreateAudioOutput,
                _audioConfig.MicEnabled,
                () => resolvedMicrophoneDeviceId = ResolveAudioDeviceId(
                    _audioConfig.MicDeviceId,
                    _audioDeviceEnumerator.GetInputDevicesOrThrow,
                    "microphone/input",
                    CaptureDeviceRole.Microphone),
                CreateAudioInput,
                (desktop, microphone) =>
                {
                    // The transaction owns both wrappers before any source setter runs,
                    // so a volume/routing/bind failure disposes every partial source.
                    if (desktop is not null)
                    {
                        desktop.Volume = _audioConfig.DesktopVolume;
                        desktop.AudioMixers = _audioTrackPlan.Desktop.SourceMixerMask;
                        Obs.SetOutputSource(1, desktop);
                    }
                    if (microphone is not null)
                    {
                        microphone.Volume = _audioConfig.MicVolume;
                        microphone.AudioMixers = _audioTrackPlan.Microphone.SourceMixerMask;
                        Obs.SetOutputSource(2, microphone);
                    }
                },
                () =>
                {
                    Obs.ClearOutputSource(1);
                    Obs.ClearOutputSource(2);
                },
                (desktop, microphone) =>
                {
                    _desktopAudio = desktop;
                    _micAudio = microphone;
                    _audioSourcesConfigured = false;
                    _audioSourceCleanupRequired = true;
                });

            _desktopAudio = created.Desktop;
            _micAudio = created.Microphone;
            _audioSourcesConfigured = true;
            _audioSourceCleanupRequired = false;
            _admittedAudioDevices = CreateAdmittedAudioManifest(
                resolvedDesktopDeviceId,
                resolvedMicrophoneDeviceId);
        }

        if (_audioConfig.DesktopAudioEnabled)
        {
            _logger.LogInformation(
                "Desktop audio enabled (device: {Device}, volume: {Volume:P0})",
                DisplayRequestedDevice(_audioConfig.DesktopDeviceId),
                _audioConfig.DesktopVolume);
        }
        else
        {
            _logger.LogInformation("Desktop audio disabled by config");
        }

        if (_audioConfig.MicEnabled)
        {
            _logger.LogInformation(
                "Microphone enabled (device: {Device}, volume: {Volume:P0})",
                DisplayRequestedDevice(_audioConfig.MicDeviceId),
                _audioConfig.MicVolume);
        }
        else
        {
            _logger.LogInformation("Microphone disabled by config");
        }

        AudioSourcesCreated?.Invoke();
    }

    private void CreateEncoders()
    {
        _videoEncoder = CreateVideoEncoder();
        foreach (var route in _audioTrackPlan.Encoders)
        {
            var encoder = AudioEncoder.CreateAac(
                name: $"WatchDog Track {route.SourceTrackNumber}",
                bitrate: Config.AudioBitrate,
                mixerIdx: route.EncoderMixerIndex);
            _audioEncoders.Add(new AudioTrackEncoderBinding(route, encoder));
        }
        _logger.LogInformation(
            "Encoders created: {Encoder}, {AudioTrackCount} audio track(s)",
            Config.Encoder,
            _audioEncoders.Count);
    }

    // ── Source switching ─────────────────────────────────────────────────

    private void SetupDesktopSources(DisplayCaptureTarget target)
    {
        DisposeSceneSources();

        _scene = Obs.Scenes.Create("WatchDog Scene");
        _displayCapture = CreateMonitorCapture(target);
        _displayCapture.SetCaptureMethod(MonitorCaptureMethod.WindowsGraphicsCapture);
        _displaySceneItem = _scene.AddSource(_displayCapture);
        StretchToCanvas(_displaySceneItem);

        Obs.SetOutputSource(0, _scene);
        _logger.LogInformation("Desktop capture sources created");
    }

    private void SetupRegionSources(RegionCaptureTarget target)
    {
        DisposeSceneSources();

        if (!RegionCaptureGeometry.TryGetDisplayLocalBounds(target, out var localBounds))
            throw new InvalidOperationException("The selected region is outside its owning display.");

        var monitor = FindMonitor(target.OwningDisplayId);
        if (monitor is null || GetPhysicalBounds(monitor) != target.OwningDisplayBounds)
            throw new InvalidOperationException("The selected region's display identity or geometry changed.");

        _scene = Obs.Scenes.Create("WatchDog Region Scene");
        _displayCapture = CreateMonitorCapture(monitor, target.OwningDisplayId);
        _displayCapture.SetCaptureMethod(MonitorCaptureMethod.WindowsGraphicsCapture);

        _regionCropFilter = new CropFilter("WatchDog Exact Region");
        _regionCropFilter.SetAbsoluteCrop(
            localBounds.X,
            localBounds.Y,
            localBounds.Width,
            localBounds.Height);
        _displayCapture.AddFilter(_regionCropFilter);

        if (!IsRegionTargetAvailable(target))
            throw new InvalidOperationException("The selected region changed before output admission.");

        _displaySceneItem = _scene.AddSource(_displayCapture);
        _displaySceneItem.SetScaleFilter(ObsKit.NET.Native.Types.ObsScaleType.Point);
        StretchToCanvas(_displaySceneItem);
        Obs.SetOutputSource(0, _scene);
        _logger.LogInformation(
            "Exact region source created at display-local bounds {Bounds}",
            localBounds);
    }

    private void SetupWindowCaptureSources(CaptureSource source)
    {
        DisposeSceneSources();

        _scene = Obs.Scenes.Create("WatchDog Scene");

        if (source.WindowHandle is null)
            throw new InvalidOperationException("The selected window does not have a physical window handle.");

        var selectedWindow = FindUnambiguousWindow(source.WindowHandle.Value, source.ProcessId);
        if (selectedWindow is not null && !WindowMetadataMatches(
                selectedWindow,
                source.DisplayName,
                source.WindowClass,
                source.ExecutableName))
        {
            selectedWindow = null;
        }
        if (selectedWindow is null)
        {
            throw new InvalidOperationException(
                $"The selected window '{source.DisplayName}' is unavailable or ambiguous. Select it again.");
        }

        _windowCapture = WindowCapture.FromWindow(selectedWindow, captureCursor: true);
        _windowCapture.SetWindow(selectedWindow);
        _windowCapture.SetClientArea(true);

        using (var settings = _windowCapture.GetSettings())
        {
            if (!StringComparer.Ordinal.Equals(settings.GetString("window"), selectedWindow.ObsId) ||
                FindUnambiguousWindow(source.WindowHandle.Value, source.ProcessId) is not { } rebound ||
                !WindowMetadataMatches(rebound, source.DisplayName, source.WindowClass, source.ExecutableName))
            {
                throw new InvalidOperationException("The exact selected window could not be bound without ambiguity.");
            }
        }

        _windowSceneItem = _scene.AddSource(_windowCapture);
        StretchToCanvas(_windowSceneItem);

        Obs.SetOutputSource(0, _scene);
        _logger.LogInformation(
            "Exact window capture source created for {Window} ({Exe}, PID {Pid}, HWND {Handle})",
            source.DisplayName, source.ExecutableName, source.ProcessId, source.WindowHandle.Value);
    }

    private AudioOutputCapture CreateAudioOutput(string deviceId)
    {
        if (AudioDeviceSelection.IsSystemDefaultRequest(deviceId))
            return AudioOutputCapture.FromDefault("Desktop Audio");
        try
        {
            return CreateExactAudioOutput(deviceId);
        }
        catch (Exception ex) when (CaptureDeviceStartupException.Find(ex) is null)
        {
            throw new CaptureDeviceStartupException(
                CaptureDeviceRole.DesktopAudio,
                CaptureDeviceStartupStage.Binding,
                CaptureDeviceStartupReason.Unknown,
                "OBS could not bind the selected desktop audio device.",
                ex);
        }
    }

    private AudioInputCapture CreateAudioInput(string deviceId)
    {
        if (AudioDeviceSelection.IsSystemDefaultRequest(deviceId))
            return AudioInputCapture.FromDefault("Microphone");
        try
        {
            return CreateExactAudioInput(deviceId);
        }
        catch (Exception ex) when (CaptureDeviceStartupException.Find(ex) is null)
        {
            throw new CaptureDeviceStartupException(
                CaptureDeviceRole.Microphone,
                CaptureDeviceStartupStage.Binding,
                CaptureDeviceStartupReason.Unknown,
                "OBS could not bind the selected microphone.",
                ex);
        }
    }

    internal static string ResolveAudioDeviceId(
        string? requestedDeviceId,
        Func<IReadOnlyList<WatchDog.Core.Audio.AudioDeviceInfo>> getAvailableDevices,
        string direction,
        CaptureDeviceRole role)
    {
        if (AudioDeviceSelection.IsSystemDefaultRequest(requestedDeviceId))
            return WatchDog.Core.Audio.AudioDeviceInfo.SystemDefaultDeviceId;

        IReadOnlyList<WatchDog.Core.Audio.AudioDeviceInfo> catalog;
        try
        {
            catalog = getAvailableDevices()
                ?? throw new InvalidDataException("The strict audio endpoint catalog was null.");
        }
        catch (CaptureDeviceStartupException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CaptureDeviceStartupException(
                role,
                CaptureDeviceStartupStage.Catalog,
                ex is InvalidDataException
                    ? CaptureDeviceStartupReason.InvalidCatalog
                    : CaptureDeviceStartupReason.CatalogUnavailable,
                ex is InvalidDataException
                    ? $"The strict {direction} audio catalog returned invalid endpoint identities."
                    : $"WatchDog could not read the strict {direction} audio catalog.",
                ex);
        }

        try
        {
            return AudioDeviceSelection.RequireExactAvailableDevice(
                requestedDeviceId,
                catalog,
                direction);
        }
        catch (Exception ex)
        {
            throw new CaptureDeviceStartupException(
                role,
                CaptureDeviceStartupStage.Selection,
                CaptureDeviceStartupReason.ExactSelectionUnavailable,
                $"The selected {direction} audio endpoint is unavailable or no longer matches its saved identity.",
                ex);
        }
    }

    private static AudioOutputCapture CreateExactAudioOutput(string exactDeviceId)
    {
        var source = AudioOutputCapture.FromDevice(exactDeviceId, "Desktop Audio");
        try
        {
            using var settings = source.GetSettings();
            if (!StringComparer.Ordinal.Equals(settings.GetString("device_id"), exactDeviceId))
            {
                throw new CaptureDeviceStartupException(
                    CaptureDeviceRole.DesktopAudio,
                    CaptureDeviceStartupStage.Binding,
                    CaptureDeviceStartupReason.IdentityMismatch,
                    "OBS did not retain the exact selected desktop audio endpoint identity.");
            }
            return source;
        }
        catch (Exception bindingFailure)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Desktop audio binding failed and source cleanup was incomplete.",
                    bindingFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    private static AudioInputCapture CreateExactAudioInput(string exactDeviceId)
    {
        var source = AudioInputCapture.FromDevice(exactDeviceId, "Microphone");
        try
        {
            using var settings = source.GetSettings();
            if (!StringComparer.Ordinal.Equals(settings.GetString("device_id"), exactDeviceId))
            {
                throw new CaptureDeviceStartupException(
                    CaptureDeviceRole.Microphone,
                    CaptureDeviceStartupStage.Binding,
                    CaptureDeviceStartupReason.IdentityMismatch,
                    "OBS did not retain the exact selected microphone endpoint identity.");
            }
            return source;
        }
        catch (Exception bindingFailure)
        {
            try
            {
                source.Dispose();
            }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException(
                    "Microphone binding failed and source cleanup was incomplete.",
                    bindingFailure,
                    cleanupFailure);
            }
            throw;
        }
    }

    private static string DisplayRequestedDevice(string? deviceId) =>
        AudioDeviceSelection.IsSystemDefaultRequest(deviceId) ? "default" : deviceId!;

    private static IEnumerable<AdmittedCaptureDevice> CreateAudioAdmission(
        CaptureDeviceRole role,
        string? resolvedDeviceId)
    {
        if (resolvedDeviceId is null)
            return [];
        return AudioDeviceSelection.IsSystemDefaultRequest(resolvedDeviceId)
            ? [AdmittedCaptureDevice.SystemDefault(role)]
            : [AdmittedCaptureDevice.Exact(role, resolvedDeviceId)];
    }

    internal static CaptureDeviceManifest CreateAdmittedAudioManifest(
        string? resolvedDesktopDeviceId,
        string? resolvedMicrophoneDeviceId) =>
        new(
        [
            .. CreateAudioAdmission(CaptureDeviceRole.DesktopAudio, resolvedDesktopDeviceId),
            .. CreateAudioAdmission(CaptureDeviceRole.Microphone, resolvedMicrophoneDeviceId),
        ]);

    private MonitorCapture CreateMonitorCapture(DisplayCaptureTarget target)
    {
        var monitor = FindMonitor(target.StableDisplayId);
        if (monitor is null)
            throw new InvalidOperationException($"The selected display '{target.DisplayName}' is no longer available.");

        return CreateMonitorCapture(monitor, target.StableDisplayId);
    }

    private MonitorCapture CreateMonitorCapture(
        ObsKit.NET.Platform.MonitorInfo monitor,
        string expectedStableDisplayId)
    {
        var capture = MonitorCapture.FromMonitor(monitor, captureCursor: true);
        try
        {
            capture.SetMonitor(monitor);

            using var settings = capture.GetSettings();
            if (!StringComparer.Ordinal.Equals(settings.GetString("monitor_id"), expectedStableDisplayId) ||
                FindMonitor(expectedStableDisplayId) is null)
            {
                throw new InvalidOperationException("The exact selected display could not be bound by stable identity.");
            }

            _logger.LogInformation(
                "Binding stable display {StableId} at current index {Index} ({DeviceName})",
                expectedStableDisplayId, monitor.Index, monitor.DeviceName);
            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }

    private static ObsKit.NET.Platform.MonitorInfo? FindMonitor(string stableDisplayId)
    {
        var matches = MonitorCapture.AvailableMonitors
            .Where(candidate => StringComparer.Ordinal.Equals(
                GetStableDisplayId(candidate),
                stableDisplayId))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static PhysicalCaptureBounds GetPhysicalBounds(ObsKit.NET.Platform.MonitorInfo monitor) =>
        new(monitor.X, monitor.Y, monitor.Width, monitor.Height);

    private static ObsKit.NET.Platform.WindowInfo? FindUnambiguousWindow(IntPtr handle, int processId)
    {
        var windows = EnumerateObsWindows();
        var selected = windows.FirstOrDefault(candidate =>
            candidate.Handle == handle && candidate.ProcessId == processId);
        if (selected is null)
            return null;

        return windows.Count(candidate => StringComparer.Ordinal.Equals(candidate.ObsId, selected.ObsId)) == 1
            ? selected
            : null;
    }

    private static IReadOnlyList<ObsKit.NET.Platform.WindowInfo> EnumerateObsWindows() =>
        WindowEnumerator.GetVisibleWindows()
            .Select(window => new ObsKit.NET.Platform.WindowInfo
            {
                Handle = window.Handle,
                Title = window.Title,
                ClassName = window.ClassName ?? string.Empty,
                ProcessName = window.ExecutableName,
                ProcessId = window.ProcessId,
                ExecutablePath = string.Empty,
                IsVisible = true,
            })
            .ToArray();

    private static bool WindowMetadataMatches(
        ObsKit.NET.Platform.WindowInfo window,
        string? title,
        string? windowClass,
        string? executableName) =>
        (title is null || StringComparer.Ordinal.Equals(window.Title, title)) &&
        (windowClass is null || StringComparer.Ordinal.Equals(window.ClassName, windowClass)) &&
        (executableName is null || string.Equals(
            Path.GetFileName(window.ProcessName),
            Path.GetFileName(executableName),
            StringComparison.OrdinalIgnoreCase));

    private static string? GetStableDisplayId(ObsKit.NET.Platform.MonitorInfo monitor) =>
        string.IsNullOrWhiteSpace(monitor.DeviceId) ? null : monitor.DeviceId;

    /// <summary>
    /// Stretch the source to fill the entire OBS canvas, ignoring aspect ratio.
    /// This ensures clips are always at the configured output resolution
    /// regardless of the game's native resolution (e.g., 4:3 games on a 16:9 output).
    /// </summary>
    private void StretchToCanvas(SceneItem? item)
    {
        if (item is null) return;
        item.SetBounds(ObsKit.NET.Native.Types.ObsBoundsType.Stretch, Config.OutputWidth, Config.OutputHeight);
    }

    private void DisposeSceneSources()
    {
        if (_obsInitialized)
            Obs.ClearOutputSource(0);

        if (_obsInitialized)
        {
            _windowSceneItem?.Remove();
            _displaySceneItem?.Remove();
            _scene?.Remove();
        }

        _windowSceneItem?.Dispose();
        _windowSceneItem = null;

        _displaySceneItem?.Dispose();
        _displaySceneItem = null;

        _scene?.Dispose();
        _scene = null;

        _windowCapture?.Dispose();
        _windowCapture = null;

        if (_displayCapture is not null && _regionCropFilter is not null)
            _displayCapture.RemoveFilter(_regionCropFilter);
        _regionCropFilter?.Dispose();
        _regionCropFilter = null;

        _displayCapture?.Dispose();
        _displayCapture = null;
    }

    // ── Replay buffer ────────────────────────────────────────────────────

    private void StartReplayBuffer(string folderName)
    {
        // Sanitize folder name to prevent path traversal (e.g., custom game entries)
        var safeName = string.Concat(folderName.Split(Path.GetInvalidFileNameChars()));
        var outputDir = Path.Combine(_bufferConfig.OutputDirectory, safeName);

        // Verify resolved path is still under the configured output directory.
        // Append separator to prevent prefix confusion (e.g., "WatchDog-Exfil" matching "WatchDog").
        var resolvedBase = Path.GetFullPath(_bufferConfig.OutputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var resolvedOutput = Path.GetFullPath(outputDir);
        if (!resolvedOutput.StartsWith(resolvedBase, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Refusing unsafe output path: {Path}, using base dir", resolvedOutput);
            outputDir = resolvedBase;
        }

        Directory.CreateDirectory(outputDir);

        if (_videoEncoder is null || _audioEncoders.Count != _audioTrackPlan.EncoderCount)
            throw new InvalidOperationException("Cannot start replay buffer: encoders not initialized");

        var config = _bufferConfig with { OutputDirectory = outputDir };
        _replayBuffer = new ObsReplayBuffer(config, _loggerFactory.CreateLogger<ObsReplayBuffer>());
        _replayBuffer.Initialize(_videoEncoder, _audioEncoders);

        _replayBuffer.Saved += path =>
        {
            ClipSaved?.Invoke(path);
        };

        _replayBuffer.Error += msg =>
        {
            _logger.LogError("Replay buffer error: {Error}", msg);
            Error?.Invoke(msg);
        };

        if (!_replayBuffer.Start())
            throw new InvalidOperationException("Failed to start replay buffer");
    }

    private void StopReplayBuffer()
    {
        if (_replayBuffer is null)
            return;

        // Preserve ownership when disposal fails so a later stop/dispose can retry.
        _replayBuffer.Dispose();
        _replayBuffer = null;
    }

    // ── Encoder creation ─────────────────────────────────────────────────

    private VideoEncoder CreateVideoEncoder()
    {
        return Config.Encoder switch
        {
            EncoderType.NvencH264 => CreateNvencEncoder("obs_nvenc_h264_tex"),
            EncoderType.NvencHevc => CreateNvencEncoder("obs_nvenc_hevc_tex"),
            EncoderType.NvencAv1 => CreateNvencEncoder("obs_nvenc_av1_tex"),
            EncoderType.X264 => VideoEncoder.CreateX264(
                name: "WatchDog x264",
                bitrate: Config.Bitrate,
                preset: "veryfast",
                rateControl: MapRateControl(Config.RateControl),
                cqLevel: Config.Quality),
            _ => throw new NotSupportedException($"Encoder {Config.Encoder} not supported")
        };
    }

    private VideoEncoder CreateNvencEncoder(string encoderId)
    {
        using var settings = new ObsKit.NET.Core.Settings();
        settings.Set("preset2", Config.Preset);
        settings.Set("profile", Config.Profile);
        settings.Set("rate_control", Config.RateControl.ToString());
        settings.Set("bitrate", Config.Bitrate);
        settings.Set("max_bitrate", Config.MaxBitrate);
        settings.Set("keyint_sec", 2);
        settings.Set("lookahead", false);
        settings.Set("psycho_aq", true);
        settings.Set("bf", 2);

        if (Config.RateControl == RateControlType.CQP)
            settings.Set("cqp", Config.Quality);

        return new VideoEncoder(encoderId, "WatchDog NVENC", settings);
    }

    private static ObsKit.NET.Encoders.RateControl MapRateControl(RateControlType type)
        => type switch
        {
            RateControlType.CBR => ObsKit.NET.Encoders.RateControl.CBR,
            RateControlType.VBR => ObsKit.NET.Encoders.RateControl.VBR,
            RateControlType.CQP => ObsKit.NET.Encoders.RateControl.CQP,
            RateControlType.CRF => ObsKit.NET.Encoders.RateControl.CRF,
            _ => ObsKit.NET.Encoders.RateControl.CQP
        };

    // ── Cleanup ──────────────────────────────────────────────────────────

    private Task CleanupCaptureResourcesAsync()
    {
        lock (_audioSourceGate)
            _admittedAudioDevices = CaptureDeviceManifest.Empty;

        StopReplayBuffer();
        DisposeSceneSources();

        lock (_audioSourceGate)
        {
            try
            {
                if (_obsInitialized)
                {
                    Obs.ClearOutputSource(1);
                    Obs.ClearOutputSource(2);
                }

                _desktopAudio?.Dispose();
                _desktopAudio = null;

                _micAudio?.Dispose();
                _micAudio = null;
                _audioSourcesConfigured = false;
                _audioSourceCleanupRequired = false;
            }
            catch
            {
                _audioSourcesConfigured = false;
                _audioSourceCleanupRequired = true;
                throw;
            }
        }

        _videoEncoder?.Dispose();
        _videoEncoder = null;

        DisposeAudioEncoders();

        CurrentGame = null;
        IsDesktopCapture = false;

        _logger.LogDebug("OBS capture resources cleaned up; context retained");
        return Task.CompletedTask;
    }

    private bool AudioSourcesNeedCreation()
    {
        lock (_audioSourceGate)
        {
            if (_audioSourceCleanupRequired)
            {
                throw new InvalidOperationException(
                    "Audio source cleanup is incomplete. Retry capture stop before starting again.");
            }

            return !_audioSourcesConfigured;
        }
    }

    private void DisposeAudioEncoders()
    {
        if (_audioEncoders.Count == 0)
            return;

        var retained = new List<AudioTrackEncoderBinding>();
        var failures = new List<Exception>();
        foreach (var binding in _audioEncoders)
        {
            try
            {
                binding.Encoder.Dispose();
            }
            catch (Exception ex)
            {
                retained.Add(binding);
                failures.Add(ex);
            }
        }

        _audioEncoders.Clear();
        _audioEncoders.AddRange(retained);
        if (failures.Count != 0)
            throw new AggregateException("One or more audio encoders could not be disposed.", failures);
    }

    private void DisposeObsContext()
    {
        _obsContext?.Dispose();
        _obsContext = null;
        _obsInitialized = false;
        _logger.LogDebug("OBS context disposed at terminal engine shutdown");
    }

    private void ThrowIfTerminalShutdownRequested()
    {
        if (_terminalShutdownRequested || _disposed)
            throw new ObjectDisposedException(nameof(ObsCaptureEngine));
    }

    private void TransitionToIdleAfterCleanup()
    {
        if (State is CaptureState.Buffering or CaptureState.Saving)
            TransitionState(CaptureState.Stopping);
        if (State != CaptureState.Idle)
            TransitionState(CaptureState.Idle);
    }

    private bool TransitionState(CaptureState newState)
    {
        if (State == newState)
            return true;

        if (!CaptureStateTransitions.IsValid(State, newState))
        {
            _logger.LogWarning("Invalid state transition: {From} -> {To}", State, newState);
            return false;
        }

        var oldState = State;
        State = newState;
        _logger.LogDebug("State: {From} -> {To}", oldState, newState);

        StateChanged?.Invoke(newState);
        _eventBus.Publish(new BufferStateChangedEvent(newState));
        return true;
    }

    public void Dispose()
    {
        _stateLock.Wait();
        try
        {
            if (_disposed) return;

            // Keep terminal intent set if cleanup fails so no new capture can be admitted.
            // A later Dispose call may retry any resource whose Dispose threw.
            _terminalShutdownRequested = true;
            CleanupCaptureResourcesAsync().GetAwaiter().GetResult();
            DisposeObsContext();
            _disposed = true;
        }
        finally
        {
            _stateLock.Release();
        }
    }
}

internal readonly record struct CreatedAudioSources<TDesktop, TMicrophone>(
    TDesktop? Desktop,
    TMicrophone? Microphone)
    where TDesktop : class, IDisposable
    where TMicrophone : class, IDisposable;

/// <summary>
/// Creates and binds both enabled audio sources as one transaction. A failure at any point
/// unbinds the OBS slots and disposes every source created during the attempt.
/// </summary>
internal static class AudioSourceSetupTransaction
{
    internal static CreatedAudioSources<TDesktop, TMicrophone> ResolveAndCreate<TDesktop, TMicrophone>(
        bool desktopEnabled,
        Func<string> resolveDesktop,
        Func<string, TDesktop> createDesktop,
        bool microphoneEnabled,
        Func<string> resolveMicrophone,
        Func<string, TMicrophone> createMicrophone,
        Action<TDesktop?, TMicrophone?> bind,
        Action unbind,
        Action<TDesktop?, TMicrophone?> retainForRetry)
        where TDesktop : class, IDisposable
        where TMicrophone : class, IDisposable
    {
        var desktopDeviceId = desktopEnabled ? resolveDesktop() : null;
        var microphoneDeviceId = microphoneEnabled ? resolveMicrophone() : null;
        return Create(
            desktopEnabled,
            () => createDesktop(desktopDeviceId!),
            microphoneEnabled,
            () => createMicrophone(microphoneDeviceId!),
            bind,
            unbind,
            retainForRetry);
    }

    internal static CreatedAudioSources<TDesktop, TMicrophone> Create<TDesktop, TMicrophone>(
        bool desktopEnabled,
        Func<TDesktop> createDesktop,
        bool microphoneEnabled,
        Func<TMicrophone> createMicrophone,
        Action<TDesktop?, TMicrophone?> bind,
        Action unbind,
        Action<TDesktop?, TMicrophone?> retainForRetry)
        where TDesktop : class, IDisposable
        where TMicrophone : class, IDisposable
    {
        TDesktop? desktop = null;
        TMicrophone? microphone = null;
        try
        {
            if (desktopEnabled)
                desktop = createDesktop();
            if (microphoneEnabled)
                microphone = createMicrophone();
            bind(desktop, microphone);
            return new CreatedAudioSources<TDesktop, TMicrophone>(desktop, microphone);
        }
        catch (Exception setupFailure)
        {
            var cleanupFailures = new List<Exception>();
            TryCleanup(unbind, cleanupFailures);
            TryDispose(ref microphone, cleanupFailures);
            TryDispose(ref desktop, cleanupFailures);
            if (cleanupFailures.Count != 0)
            {
                TryCleanup(() => retainForRetry(desktop, microphone), cleanupFailures);
                throw new AggregateException(
                    "Audio source setup failed and rollback was incomplete.",
                    [setupFailure, .. cleanupFailures]);
            }
            throw;
        }
    }

    private static void TryCleanup(Action cleanup, ICollection<Exception> failures)
    {
        try
        {
            cleanup();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }

    private static void TryDispose<T>(ref T? resource, ICollection<Exception> failures)
        where T : class, IDisposable
    {
        if (resource is null)
            return;

        try
        {
            resource.Dispose();
            resource = null;
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
    }
}
