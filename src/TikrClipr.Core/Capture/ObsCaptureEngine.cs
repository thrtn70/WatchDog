using Microsoft.Extensions.Logging;
using ObsKit.NET;
using ObsKit.NET.Core;
using ObsKit.NET.Encoders;
using ObsKit.NET.Scenes;
using ObsKit.NET.Sources;
using TikrClipr.Core.Audio;
using TikrClipr.Core.Buffer;
using TikrClipr.Core.Events;
using TikrClipr.Core.GameDetection;
using TikrClipr.Native.Obs;

namespace TikrClipr.Core.Capture;

public sealed class ObsCaptureEngine : ICaptureEngine
{
    private readonly ILogger<ObsCaptureEngine> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IEventBus _eventBus;
    private readonly BufferConfig _bufferConfig;
    private readonly AudioMixConfig _audioConfig;

    private ObsContext? _obsContext;
    private Scene? _scene;
    private SceneItem? _gameSceneItem;
    private SceneItem? _displaySceneItem;
    private GameCapture? _gameCapture;
    private MonitorCapture? _displayCapture;
    private VideoEncoder? _videoEncoder;
    private AudioEncoder? _audioEncoder;
    private AudioOutputCapture? _desktopAudio;
    private AudioInputCapture? _micAudio;
    private ObsReplayBuffer? _replayBuffer;
    private bool _obsInitialized;
    private bool _disposed;

    private readonly SemaphoreSlim _stateLock = new(1, 1);

    public CaptureState State { get; private set; } = CaptureState.Idle;
    public CaptureConfig Config { get; }
    public bool IsDesktopCapture { get; private set; }
    public GameInfo? CurrentGame { get; private set; }

    // Expose audio sources for the mixer to control volume/mute
    public AudioOutputCapture? DesktopAudioSource => _desktopAudio;
    public AudioInputCapture? MicAudioSource => _micAudio;

    public event Action<CaptureState>? StateChanged;
    public event Action<string>? ClipSaved;
    public event Action<string>? Error;

    public ObsCaptureEngine(
        CaptureConfig captureConfig,
        BufferConfig bufferConfig,
        AudioMixConfig audioConfig,
        IEventBus eventBus,
        ILoggerFactory loggerFactory)
    {
        Config = captureConfig;
        _bufferConfig = bufferConfig;
        _audioConfig = audioConfig;
        _eventBus = eventBus;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ObsCaptureEngine>();
    }

    public async Task StartDesktopCaptureAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            if (State == CaptureState.Buffering && IsDesktopCapture)
                return; // Already in desktop capture mode

            if (!TransitionState(CaptureState.Initializing))
                return;

            _logger.LogInformation("Starting desktop capture");

            EnsureObsInitialized();
            SetupDesktopSources();
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
            await FullCleanupAsync();
            TransitionState(CaptureState.Idle);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task StartAsync(GameInfo game, CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            CurrentGame = game;
            _logger.LogInformation("Starting capture for {Game} (PID {Pid})",
                game.DisplayName, game.ProcessId);

            if (State == CaptureState.Buffering)
            {
                // Already running (desktop capture) — switch sources
                _logger.LogInformation("Switching from desktop to game capture for {Game}", game.DisplayName);
                StopReplayBuffer();
                SetupGameSources(game);
                StartReplayBuffer(game.DisplayName);
                IsDesktopCapture = false;
                _logger.LogInformation("Switched to game capture for {Game}", game.DisplayName);
            }
            else
            {
                // Cold start
                if (!TransitionState(CaptureState.Initializing))
                    return;

                EnsureObsInitialized();
                SetupGameSources(game);
                StartReplayBuffer(game.DisplayName);

                IsDesktopCapture = false;
                TransitionState(CaptureState.Buffering);
                _logger.LogInformation("Capture started for {Game}", game.DisplayName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start capture for {Game}", game.DisplayName);
            Error?.Invoke($"Failed to start capture: {ex.Message}");
            await FullCleanupAsync();
            TransitionState(CaptureState.Idle);
        }
        finally
        {
            _stateLock.Release();
        }
    }

    public async Task SwitchToDesktopCaptureAsync(CancellationToken ct = default)
    {
        await _stateLock.WaitAsync(ct);
        try
        {
            if (State != CaptureState.Buffering && State != CaptureState.Saving)
            {
                _logger.LogWarning("Cannot switch to desktop capture in state {State}", State);
                return;
            }

            _logger.LogInformation("Switching from game to desktop capture");
            StopReplayBuffer();
            SetupDesktopSources();
            StartReplayBuffer("Desktop");

            IsDesktopCapture = true;
            CurrentGame = null;
            _logger.LogInformation("Switched to desktop capture");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to switch to desktop capture");
            Error?.Invoke($"Failed to switch to desktop capture: {ex.Message}");
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
            if (State == CaptureState.Idle)
                return;

            TransitionState(CaptureState.Stopping);
            _logger.LogInformation("Stopping capture");

            await FullCleanupAsync();

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
        if (State != CaptureState.Buffering)
        {
            _logger.LogWarning("Cannot save replay in state {State}", State);
            return null;
        }

        TransitionState(CaptureState.Saving);

        try
        {
            var path = await (_replayBuffer?.SaveAsync(ct) ?? Task.FromResult<string?>(null));

            if (path is not null)
            {
                _logger.LogInformation("Clip saved: {Path}", path);
                ClipSaved?.Invoke(path);
                _eventBus.Publish(new ClipSavedEvent(path, CurrentGame, DateTimeOffset.UtcNow));
            }

            return path;
        }
        finally
        {
            if (State == CaptureState.Saving)
                TransitionState(CaptureState.Buffering);
        }
    }

    // ── OBS lifecycle (called once) ──────────────────────────────────────

    private void EnsureObsInitialized()
    {
        if (_obsInitialized) return;

        if (!ObsRuntime.IsAvailable())
        {
            var msg = ObsRuntime.GetMissingComponentsMessage();
            _logger.LogError("{Message}", msg);
            Error?.Invoke(msg);
            throw new InvalidOperationException(msg);
        }

        InitializeObs();
        CreateAudioSources();
        CreateEncoders();
        _obsInitialized = true;
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
        if (_audioConfig.DesktopAudioEnabled)
        {
            var desktopId = _audioConfig.DesktopDeviceId;
            _desktopAudio = string.IsNullOrEmpty(desktopId) || desktopId == "default"
                ? AudioOutputCapture.FromDefault()
                : new AudioOutputCapture("Desktop Audio", desktopId);
            _desktopAudio.Volume = _audioConfig.DesktopVolume;
            Obs.SetOutputSource(1, _desktopAudio);
            _logger.LogInformation("Desktop audio enabled (device: {Device}, volume: {Vol:P0})",
                string.IsNullOrEmpty(desktopId) ? "default" : desktopId, _audioConfig.DesktopVolume);
        }
        else
        {
            _logger.LogInformation("Desktop audio disabled by config");
        }

        if (_audioConfig.MicEnabled)
        {
            try
            {
                var micId = _audioConfig.MicDeviceId;
                _micAudio = string.IsNullOrEmpty(micId) || micId == "default"
                    ? AudioInputCapture.FromDefault()
                    : new AudioInputCapture("Microphone", micId);
                _micAudio.Volume = _audioConfig.MicVolume;
                Obs.SetOutputSource(2, _micAudio);
                _logger.LogInformation("Microphone enabled (device: {Device}, volume: {Vol:P0})",
                    string.IsNullOrEmpty(micId) ? "default" : micId, _audioConfig.MicVolume);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No microphone found, continuing without mic audio");
            }
        }
        else
        {
            _logger.LogInformation("Microphone disabled by config");
        }
    }

    private void CreateEncoders()
    {
        _videoEncoder = CreateVideoEncoder();
        _audioEncoder = AudioEncoder.CreateAac(bitrate: Config.AudioBitrate);
        _logger.LogInformation("Encoders created: {Encoder}", Config.Encoder);
    }

    // ── Source switching ─────────────────────────────────────────────────

    private void SetupDesktopSources()
    {
        DisposeSceneSources();

        _scene = Obs.Scenes.Create("TikrClipr Scene");
        _displayCapture = Config.MonitorIndex == 0
            ? MonitorCapture.FromPrimary()
            : MonitorCapture.FromIndex(Config.MonitorIndex);
        _displayCapture.SetCaptureMethod(MonitorCaptureMethod.WindowsGraphicsCapture);
        _displaySceneItem = _scene.AddSource(_displayCapture);
        StretchToCanvas(_displaySceneItem);

        _scene.SetAsProgram();
        _logger.LogInformation("Desktop capture sources created");
    }

    private void SetupGameSources(GameInfo game)
    {
        DisposeSceneSources();

        _scene = Obs.Scenes.Create("TikrClipr Scene");

        _gameCapture = new GameCapture("Game Capture", GameCapture.CaptureMode.SpecificWindow);
        _gameCapture.SetWindow($"*:*:{game.ExecutableName}");
        _gameCapture.SetAntiCheatHook(true);
        _gameCapture.SetCaptureCursor(false);

        _gameCapture.Hooked += gc =>
        {
            _logger.LogInformation("Game capture hooked: {Exe} — hiding fallback display capture", gc.HookedExecutable);
            // Game capture is working — hide the fallback display capture so
            // we don't composite both sources on top of each other
            _displaySceneItem?.SetVisible(false);
        };
        _gameCapture.Unhooked += _ =>
        {
            _logger.LogWarning("Game capture unhooked — showing fallback display capture");
            // Game capture lost the hook — show fallback so the replay buffer
            // captures something instead of black frames
            _displaySceneItem?.SetVisible(true);
        };

        _gameSceneItem = _scene.AddSource(_gameCapture);
        StretchToCanvas(_gameSceneItem);

        // Fallback monitor capture — visible until game capture hooks.
        // Prevents black frames in the replay buffer while the hook is retrying.
        _displayCapture = Config.MonitorIndex == 0
            ? MonitorCapture.FromPrimary()
            : MonitorCapture.FromIndex(Config.MonitorIndex);
        _displayCapture.SetCaptureMethod(MonitorCaptureMethod.WindowsGraphicsCapture);
        _displaySceneItem = _scene.AddSource(_displayCapture);
        StretchToCanvas(_displaySceneItem);

        _scene.SetAsProgram();
        _logger.LogInformation("Game capture sources created for {Game}", game.DisplayName);
    }

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
        _gameSceneItem = null;
        _displaySceneItem = null;

        _gameCapture?.Dispose();
        _gameCapture = null;

        _displayCapture?.Dispose();
        _displayCapture = null;

        _scene?.Dispose();
        _scene = null;
    }

    // ── Replay buffer ────────────────────────────────────────────────────

    private void StartReplayBuffer(string folderName)
    {
        var outputDir = Path.Combine(_bufferConfig.OutputDirectory, folderName);
        Directory.CreateDirectory(outputDir);

        var config = _bufferConfig with { OutputDirectory = outputDir };
        _replayBuffer = new ObsReplayBuffer(config, _loggerFactory.CreateLogger<ObsReplayBuffer>());
        _replayBuffer.Initialize(_videoEncoder!, _audioEncoder!);

        _replayBuffer.Saved += path =>
        {
            ClipSaved?.Invoke(path);
            _eventBus.Publish(new ClipSavedEvent(path, CurrentGame, DateTimeOffset.UtcNow));
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
        _replayBuffer?.Dispose();
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
                name: "TikrClipr x264",
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

        return new VideoEncoder(encoderId, "TikrClipr NVENC", settings);
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

    private Task FullCleanupAsync()
    {
        StopReplayBuffer();
        DisposeSceneSources();

        _desktopAudio?.Dispose();
        _desktopAudio = null;

        _micAudio?.Dispose();
        _micAudio = null;

        _videoEncoder?.Dispose();
        _videoEncoder = null;

        _audioEncoder?.Dispose();
        _audioEncoder = null;

        _obsContext?.Dispose();
        _obsContext = null;
        _obsInitialized = false;

        _logger.LogDebug("OBS resources cleaned up");
        return Task.CompletedTask;
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
        if (_disposed) return;
        _disposed = true;

        FullCleanupAsync().GetAwaiter().GetResult();
        _stateLock.Dispose();
    }
}
