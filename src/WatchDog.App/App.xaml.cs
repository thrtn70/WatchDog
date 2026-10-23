using System.IO;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Highlights;
using WatchDog.Core.Highlights.Cs2;
using WatchDog.Core.Highlights.Overwatch2;
using WatchDog.Core.Highlights.Audio;
using WatchDog.Core.Highlights.Valorant;
using WatchDog.Core.Hotkeys;
using WatchDog.Core.Recording;
using WatchDog.Core.Sessions;
using WatchDog.Core.Settings;
using WatchDog.App.Services;
using WatchDog.App.ViewModels;

namespace WatchDog.App;

public partial class App : Application
{
    private IHost? _host;
    private H.NotifyIcon.TaskbarIcon? _trayIcon;
    private Views.StatusOverlayWindow? _overlayWindow;
    private Core.Runtime.ExclusiveFileLease? _instanceLease;
    private CaptureLifecycleCoordinator? _captureLifecycle;
    private Task _startupTask = Task.CompletedTask;
    private Task<bool>? _shutdownTask;
    private bool _shutdownRequested;
    private bool _finalShutdown;
    private bool _sessionEndingNoticeShown;

    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var instanceLockPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WatchDog",
            "watchdog.lock");
        try
        {
            _instanceLease = Core.Runtime.ExclusiveFileLease.TryAcquire(instanceLockPath);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"WatchDog could not acquire its instance lock:\n\n{ex.Message}",
                "WatchDog Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            _finalShutdown = true;
            Shutdown(2);
            return;
        }

        if (_instanceLease is null)
        {
            MessageBox.Show(
                "WatchDog is already running, or its instance lock is unavailable.",
                "WatchDog",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            _finalShutdown = true;
            Shutdown(2);
            return;
        }

        try
        {
            // Validate runtime dependencies before building the host
            var validation = Core.Runtime.RuntimeValidator.Validate();
            if (!validation.IsValid)
            {
                var msg = "WatchDog is missing required components:\n\n"
                        + string.Join("\n", validation.MissingComponents)
                        + "\n\nThe app will continue but some features may not work.";
                MessageBox.Show(msg, "WatchDog — Missing Components",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _host = Host.CreateDefaultBuilder()
                .ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Debug);
                    logging.AddProvider(new Logging.FileLoggerProvider(LogLevel.Information));
                })
                .ConfigureServices(ConfigureServices)
                .Build();

            Services = _host.Services;
            // Freeze the restart-only liveness request before any hosted producer can start.
            _ = _host.Services.GetRequiredService<SessionSegmentLivenessStartupDecision>();
            _captureLifecycle = _host.Services.GetRequiredService<CaptureLifecycleCoordinator>();

            // Initialize hotkey service on UI thread (needs HWND message pump)
            var hotkeyService = _host.Services.GetRequiredService<Win32HotkeyService>();
            hotkeyService.Initialize();

            // Create system tray icon
            InitializeTrayIcon();

            _startupTask = StartHostAsync(_host);
            await _startupTask;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WatchDog failed to start:\n\n{ex.Message}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            await RequestShutdownAsync(1);
        }
    }

    private async Task StartHostAsync(IHost host)
    {
        // Recover orphaned sessions BEFORE starting hosted services —
        // CaptureSourceManager creates a new desktop session on start,
        // and RecoverOrphanedSessions would incorrectly mark it as Crashed.
        var sessionManager = host.Services.GetRequiredService<SessionManager>();
        await sessionManager.RecoverOrphanedSessionsAsync();
        if (_shutdownRequested) return;

        await host.StartAsync();
        if (_shutdownRequested) return;

        // Activate match tracking (resolves singleton, starts event subscription)
        _ = host.Services.GetRequiredService<MatchTracker>();

        // Initialize status overlay if enabled
        var overlaySettings = host.Services.GetRequiredService<OverlaySettings>();
        if (overlaySettings.Enabled)
            ShowOverlay();

        // Check for updates (non-blocking, failure is silent)
        _ = Task.Run(async () =>
        {
            // Before the check, so it always finishes before this run can start a download of its own.
            DeleteStaleUpdateInstallers(host.Services);
            try
            {
                var checker = host.Services.GetRequiredService<Core.Updates.IUpdateChecker>();
                var update = await checker.CheckForUpdateAsync();
                if (update?.IsUpdateAvailable == true && !_shutdownRequested)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (_shutdownRequested) return;
                        var vm = host.Services.GetRequiredService<MainWindowViewModel>();
                        vm.SetUpdateAvailable(update);
                    });
                }
            }
            catch { /* update check failure is non-fatal */ }
        });
    }

    /// <summary>Removes installers an earlier run left behind, e.g. when it was killed mid-download.</summary>
    private static void DeleteStaleUpdateInstallers(IServiceProvider services)
    {
        try
        {
            var deleted = Core.Updates.StaleUpdateInstallerCleanup.DeleteStaleInstallers(
                Core.Updates.StaleUpdateInstallerCleanup.DefaultDirectory, DateTime.UtcNow);
            if (deleted > 0)
            {
                services.GetRequiredService<ILoggerFactory>().CreateLogger<App>()
                    .LogInformation("Deleted {Count} stale update installer(s)", deleted);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"Stale update installer cleanup failed: {ex.Message}");
        }
    }

    public Task<bool> RequestShutdownAsync(
        int exitCode = 0,
        Func<Task>? afterDrainBeforeExit = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            return Dispatcher
                .InvokeAsync(() => RequestShutdownAsync(exitCode, afterDrainBeforeExit))
                .Task
                .Unwrap();
        }

        if (_shutdownTask is not null)
            return _shutdownTask;

        var request = RequestShutdownCoreAsync(exitCode, afterDrainBeforeExit);
        _shutdownTask = request;
        _ = ClearFailedShutdownRequestAsync(request);
        return request;
    }

    private async Task<bool> RequestShutdownCoreAsync(
        int exitCode,
        Func<Task>? afterDrainBeforeExit)
    {
        _shutdownRequested = true;
        var drain = new Core.Runtime.ApplicationShutdownDrain(
            BeginCaptureShutdown,
            () => _startupTask,
            () => _host is null
                ? Task.CompletedTask
                : _host.StopAsync(TimeSpan.FromSeconds(10)),
            async () =>
            {
                // Dispose the native engine explicitly before the container. If terminal
                // OBS cleanup throws, the host remains available so a later Exit retries it.
                var host = _host;
                host?.Services.GetService<ICaptureEngine>()?.Dispose();
                if (host is IAsyncDisposable asyncDisposable)
                    await asyncDisposable.DisposeAsync();
                else
                    host?.Dispose();
                _host = null;
            });

        var result = await drain.DrainAsync();
        if (!result.Succeeded)
        {
            MessageBox.Show(
                CaptureFailurePresentation.SafeShutdownFailureText(result),
                "WatchDog Could Not Exit Safely",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(result.WarningMessage) || result.CaptureDiagnostic is not null)
        {
            MessageBox.Show(
                CaptureFailurePresentation.SafeShutdownWarningText(result),
                "WatchDog Recording Warning",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        if (afterDrainBeforeExit is not null)
        {
            try
            {
                await afterDrainBeforeExit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"WatchDog stopped safely, but the final action failed:\n\n{ex.Message}",
                    "Final Action Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        PrepareFinalExit();
        _finalShutdown = true;
        Shutdown(exitCode);
        return true;
    }

    private Task<Core.Runtime.ShutdownStepResult>? BeginCaptureShutdown()
    {
        var lifecycle = _captureLifecycle;
        var completedMedia = _host?.Services.GetService<Core.Storage.ICompletedMediaIngestion>();
        var highlightClips = _host?.Services.GetService<HighlightClipService>();
        var clipExports = _host?.Services.GetService<Core.ClipEditor.IClipExportCoordinator>();
        if (lifecycle is null && completedMedia is null && highlightClips is null && clipExports is null)
            return null;

        return ObserveCaptureShutdownAsync(
            lifecycle?.ShutdownAsync(),
            highlightClips,
            clipExports,
            completedMedia);
    }

    private static async Task<Core.Runtime.ShutdownStepResult> ObserveCaptureShutdownAsync(
        Task<CaptureTransitionResult>? shutdown,
        HighlightClipService? highlightClips,
        Core.ClipEditor.IClipExportCoordinator? clipExports,
        Core.Storage.ICompletedMediaIngestion? completedMedia)
    {
        string? warning = null;
        CaptureFailure? captureDiagnostic = null;
        if (shutdown is not null)
        {
            var result = await shutdown;
            if (!result.Succeeded)
            {
                return Core.Runtime.ShutdownStepResult.Failed(
                    result.Failure?.Message ?? "Capture lifecycle shutdown failed.", result.Failure);
            }
            warning = result.After.LastFailure?.Message;
            captureDiagnostic = result.After.LastFailure;
        }

        if (highlightClips is not null)
        {
            using var producerStop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await highlightClips.StopAsync(producerStop.Token);
            }
            catch (Exception ex)
            {
                return Core.Runtime.ShutdownStepResult.Failed(
                    ex, captureDiagnostic, warning) with
                {
                    FailureMessage = $"Highlight save work could not stop before media ingestion drained: {ex.Message}",
                };
            }
        }

        if (clipExports is not null)
        {
            try
            {
                var exports = await clipExports.DrainAsync(TimeSpan.FromSeconds(5));
                if (!exports.Drained)
                {
                    return Core.Runtime.ShutdownStepResult.Failed(
                        $"{exports.RemainingOperations} clip export(s) could not stop safely before media ingestion drained.",
                        captureDiagnostic, warning);
                }
            }
            catch (Exception ex)
            {
                return Core.Runtime.ShutdownStepResult.Failed(
                    ex, captureDiagnostic, warning);
            }
        }

        if (completedMedia is not null)
        {
            try
            {
                var ingestion = await completedMedia.DrainAsync(TimeSpan.FromSeconds(5));
                if (!ingestion.Drained && !ingestion.PendingIsDurable)
                {
                    return Core.Runtime.ShutdownStepResult.Failed(
                        ingestion.WarningMessage ?? "Completed-media work could not be durably drained.",
                        captureDiagnostic, warning);
                }
                warning = string.Join(
                    Environment.NewLine,
                    new[] { warning, ingestion.WarningMessage }.Where(message => !string.IsNullOrWhiteSpace(message)));
                if (string.IsNullOrWhiteSpace(warning)) warning = null;
            }
            catch (Exception ex)
            {
                return Core.Runtime.ShutdownStepResult.Failed(
                    ex, captureDiagnostic, warning);
            }
        }

        return Core.Runtime.ShutdownStepResult.Success(warning, captureDiagnostic);
    }

    private async Task ClearFailedShutdownRequestAsync(Task<bool> request)
    {
        if (!await request && ReferenceEquals(_shutdownTask, request))
            _shutdownTask = null;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (!_finalShutdown)
        {
            e.Cancel = true;
            _ = RequestShutdownAsync();

            if (!_sessionEndingNoticeShown)
            {
                _sessionEndingNoticeShown = true;
                MessageBox.Show(
                    CaptureFailurePresentation.SafeSessionEndingText(
                        e.ReasonSessionEnding == ReasonSessionEnding.Logoff),
                    "WatchDog Is Finishing Recording",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
        }

        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        PrepareFinalExit();
        _instanceLease?.Dispose();
        _instanceLease = null;
        base.OnExit(e);
    }

    private void PrepareFinalExit()
    {
        _overlayWindow?.Close();
        _overlayWindow = null;
        _trayIcon?.Dispose();
        _trayIcon = null;

        _captureLifecycle = null;
    }

    private void InitializeTrayIcon()
    {
        var viewModel = Services.GetRequiredService<TrayIconViewModel>();

        // Generate tray icon color variants (idle/buffering/recording/saving)
        try
        {
            var baseIconPath = Path.Combine(AppContext.BaseDirectory, "Resources", "WatchDog.ico");
            if (File.Exists(baseIconPath))
            {
                var icons = WatchDog.App.Services.TrayIconGenerator.EnsureIcons(baseIconPath);
                viewModel.IdleIconPath = icons.Idle;
                viewModel.BufferingIconPath = icons.Buffering;
                viewModel.RecordingIconPath = icons.Recording;
                viewModel.SavingIconPath = icons.Saving;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Tray icon generation failed: {ex.Message}");
            // Non-critical — fall back to embedded default icons
        }

        _trayIcon = new H.NotifyIcon.TaskbarIcon
        {
            ToolTipText = viewModel.TraySummaryText,
            ContextMenu = CreateTrayContextMenu(viewModel),
            NoLeftClickDelay = true,
        };
        _trayIcon.SetBinding(
            H.NotifyIcon.TaskbarIcon.ToolTipTextProperty,
            new System.Windows.Data.Binding(nameof(TrayIconViewModel.TraySummaryText))
            {
                Source = viewModel,
                Mode = System.Windows.Data.BindingMode.OneWay,
            });

        // Load icon from embedded resource stream
        try
        {
            var iconUri = new Uri("pack://application:,,,/Resources/WatchDog.ico");
            var iconStream = Application.GetResourceStream(iconUri);
            if (iconStream is not null)
            {
                // Don't dispose the stream — System.Drawing.Icon reads from it
                // lazily and will fail to redraw if the stream is closed early.
                _trayIcon.Icon = new System.Drawing.Icon(iconStream.Stream);
            }
        }
        catch
        {
            // Fallback: use default application icon
        }

        _trayIcon.TrayLeftMouseDown += (_, _) => viewModel.ShowMainWindowCommand.Execute(null);
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    private void ShowOverlay()
    {
        if (_overlayWindow is not null) return;

        var vm = Services.GetRequiredService<StatusOverlayViewModel>();
        var settings = Services.GetRequiredService<OverlaySettings>();
        _overlayWindow = new Views.StatusOverlayWindow(settings)
        {
            DataContext = vm,
        };
        _overlayWindow.Closed += (_, _) =>
        {
            vm.Dispose();
            _overlayWindow = null;
        };
        _overlayWindow.Show();
    }

    /// <summary>Toggle the status overlay. Must be called on the UI thread.</summary>
    public void ToggleOverlay()
    {
        if (_overlayWindow is not null)
        {
            _overlayWindow.Close(); // Closed handler disposes VM and nulls _overlayWindow
        }
        else
        {
            ShowOverlay();
        }
    }

    private static System.Windows.Controls.ContextMenu CreateTrayContextMenu(TrayIconViewModel vm)
    {
        var menuItemStyle = (Style)Current.FindResource("TrayMenuItemStyle");
        var infoStyle = (Style)Current.FindResource("TrayMenuInfoStyle");
        var separatorStyle = (Style)Current.FindResource("TrayMenuSeparatorStyle");

        var menu = new System.Windows.Controls.ContextMenu
        {
            Style = (Style)Current.FindResource("TrayContextMenuStyle"),
        };

        // Dynamic info rows (non-interactive status display)
        var statusInfoItem = new System.Windows.Controls.MenuItem { Style = infoStyle };
        statusInfoItem.SetBinding(System.Windows.Controls.HeaderedItemsControl.HeaderProperty,
            new System.Windows.Data.Binding(nameof(TrayIconViewModel.TraySummaryText)) { Source = vm });
        menu.Items.Add(statusInfoItem);

        var gameInfoItem = new System.Windows.Controls.MenuItem { Style = infoStyle };
        gameInfoItem.SetBinding(System.Windows.Controls.HeaderedItemsControl.HeaderProperty,
            new System.Windows.Data.Binding(nameof(TrayIconViewModel.CurrentGameText)) { Source = vm });
        menu.Items.Add(gameInfoItem);

        var bufferInfoItem = new System.Windows.Controls.MenuItem { Style = infoStyle };
        bufferInfoItem.SetBinding(System.Windows.Controls.HeaderedItemsControl.HeaderProperty,
            new System.Windows.Data.Binding(nameof(TrayIconViewModel.BufferStatusText)) { Source = vm });
        menu.Items.Add(bufferInfoItem);

        menu.Items.Add(new System.Windows.Controls.Separator { Style = separatorStyle });

        var openItem = new System.Windows.Controls.MenuItem { Header = "Open WatchDog", Style = menuItemStyle };
        openItem.Click += (_, _) => vm.ShowMainWindowCommand.Execute(null);
        menu.Items.Add(openItem);

        var saveItem = new System.Windows.Controls.MenuItem { Header = "Save Clip", Style = menuItemStyle };
        saveItem.Click += async (_, _) => await vm.SaveClipCommand.ExecuteAsync(null);
        menu.Items.Add(saveItem);

        // Manual window capture
        var captureItem = new System.Windows.Controls.MenuItem { Header = "Capture Window...", Style = menuItemStyle };
        captureItem.Click += (_, _) =>
        {
            var manualVm = Services.GetRequiredService<ManualCaptureViewModel>();
            var picker = new Controls.WindowPickerPopup(manualVm);
            picker.ShowNearTray();
        };
        menu.Items.Add(captureItem);

        menu.Items.Add(new System.Windows.Controls.Separator { Style = separatorStyle });

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings", Style = menuItemStyle };
        settingsItem.Click += (_, _) => vm.ShowSettingsCommand.Execute(null);
        menu.Items.Add(settingsItem);

        menu.Items.Add(new System.Windows.Controls.Separator { Style = separatorStyle });

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit", Style = menuItemStyle };
        exitItem.Click += (_, _) => vm.ExitApplicationCommand.Execute(null);
        menu.Items.Add(exitItem);

        return menu;
    }

    internal static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
    {
        // Core infrastructure
        services.AddSingleton<IEventBus, InMemoryEventBus>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();

        // Load settings and register config objects
        services.AddSingleton(sp =>
        {
            var settingsService = sp.GetRequiredService<ISettingsService>();
            return settingsService.Load();
        });
        services.AddSingleton(sp => sp.GetRequiredService<AppSettings>().Capture);
        services.AddSingleton(sp => sp.GetRequiredService<AppSettings>().Buffer);
        services.AddSingleton(sp => sp.GetRequiredService<AppSettings>().Audio);
        services.AddSingleton(sp => CaptureStartupBaselineResult.Create(sp.GetRequiredService<AppSettings>()));
        services.AddSingleton(sp => Core.Audio.AudioTrackPlan.Create(
            sp.GetRequiredService<Core.Audio.AudioMixConfig>().SeparateAudioTracks));

        // Game detection
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            return new GameDatabase(settings.CustomGames);
        });
        services.AddSingleton<ProcessGameDetector>();
        services.AddSingleton<IGameDetector>(sp => sp.GetRequiredService<ProcessGameDetector>());

        // Capture engine
        services.AddSingleton<ICaptureEngine, ObsCaptureEngine>();

        // Session recording — only allocate OBS encoders when mode requires it
        services.AddSingleton(sp => sp.GetRequiredService<AppSettings>().Recording);
        services.AddSingleton(ObsSessionRecorder.SegmentFrameCounterCapabilities);
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var recording = sp.GetRequiredService<SessionRecordingConfig>();
            var provider = sp.GetRequiredService<CaptureLivenessProviderCapabilities>();
            var decision = SessionSegmentLivenessStartupGate.Evaluate(
                settings.SessionSegmentLiveness,
                recording.IsSessionRecordingEnabled,
                provider);
            var logger = sp.GetRequiredService<ILoggerFactory>()
                .CreateLogger("SessionSegmentLivenessStartup");
            if (decision.State == SessionSegmentLivenessStartupState.ProviderNotAdmitted)
            {
                logger.LogWarning(
                    "Session-segment encoded-output monitoring was requested but remains inactive. " +
                    "Provider {ProviderId} was not admitted: {AdmissionFailure}. " +
                    "Configuration changes require application restart.",
                    decision.ProviderId,
                    decision.AdmissionFailure);
            }
            else
            {
                logger.LogInformation(
                    "Session-segment encoded-output monitoring startup decision: {State} ({ReasonCode}). " +
                    "Configuration changes require application restart.",
                    decision.State,
                    decision.ReasonCode);
            }
            return decision;
        });
        services.AddSingleton<ISessionRecorder>(sp =>
        {
            var config = sp.GetRequiredService<SessionRecordingConfig>();
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var captureConfig = sp.GetRequiredService<CaptureConfig>();
            var audioTrackPlan = sp.GetRequiredService<Core.Audio.AudioTrackPlan>();
            // Encoder creation is deferred to StartAsync() — OBS must be initialized first.
            return new ObsSessionRecorder(
                config,
                captureConfig.Bitrate,
                captureConfig.AudioBitrate,
                audioTrackPlan,
                loggerFactory.CreateLogger<ObsSessionRecorder>(),
                sp.GetRequiredService<CaptureLivenessRegistry>(),
                sp.GetRequiredService<SessionSegmentLivenessStartupDecision>().EffectivePolicy);
        });
        // The webcam recorder is inert until an immutable explicit-manual request authorizes it.
        services.AddSingleton<IWebcamRecorder>(sp => new ObsWebcamRecorder(
            sp.GetRequiredService<Core.Webcam.IWebcamDeviceEnumerator>()));

        // Audio mixer — engine owns the native source lifetime and applies state under its source gate.
        services.AddSingleton<Core.Audio.IAudioMixer>(sp =>
        {
            var engine = sp.GetRequiredService<ICaptureEngine>() as ObsCaptureEngine ??
                         throw new InvalidOperationException("The OBS audio mixer requires the OBS capture engine.");
            var audioConfig = sp.GetRequiredService<Core.Audio.AudioMixConfig>();
            var mixer = new Core.Audio.ObsAudioMixer(
                engine.ApplyAudioMixerState,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Core.Audio.ObsAudioMixer>());
            engine.AudioSourcesCreated += mixer.ApplyToCurrentSources;
            mixer.ApplyConfig(audioConfig);
            return mixer;
        });
        services.AddSingleton<AudioMixerViewModel>();

        // Clip editor, thumbnail cache, and storage
        services.AddSingleton<Core.ClipEditor.IClipEditor, Core.ClipEditor.FFmpegClipEditor>();
        services.AddSingleton<ThumbnailStripCache>();
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            var bufferConfig = sp.GetRequiredService<Core.Buffer.BufferConfig>();
            // Use the same root as the replay buffer so clip indexing finds saved files
            return new Core.Storage.StorageConfig
            {
                BasePath = bufferConfig.OutputDirectory,
                LegacyReadOnlyPaths = settings.Storage.LegacySavePaths,
                MaxStorageGb = settings.Storage.MaxStorageGb,
                AutoDeleteDays = settings.Storage.AutoDeleteDays,
            };
        });
        services.AddSingleton<Core.Storage.IProtectedMediaRegistry, Core.Storage.ProtectedMediaRegistry>();
        services.AddSingleton<Core.Storage.ClipStorageManager>();
        services.AddSingleton<Core.Storage.IClipStorage>(sp =>
            sp.GetRequiredService<Core.Storage.ClipStorageManager>());
        services.AddSingleton<Core.Storage.IStorageMaintenance>(sp =>
            sp.GetRequiredService<Core.Storage.ClipStorageManager>());
        services.AddSingleton<Core.Storage.CompletedMediaIngestionService>();
        services.AddSingleton<Core.Storage.ICompletedMediaIngestion>(sp =>
            sp.GetRequiredService<Core.Storage.CompletedMediaIngestionService>());
        services.AddSingleton<Core.ClipEditor.IClipExportCoordinator, Core.ClipEditor.ClipExportCoordinator>();
        services.AddSingleton<Core.Storage.IStorageVolumeProbe, Core.Storage.DriveInfoStorageVolumeProbe>();
        services.AddSingleton<Core.Storage.StorageMaintenanceService>();
        services.AddSingleton<Core.Storage.IStorageSafetyMonitor>(sp =>
            sp.GetRequiredService<Core.Storage.StorageMaintenanceService>());

        // Session management
        services.AddSingleton<ISessionRepository>(sp =>
        {
            var bufferConfig = sp.GetRequiredService<Core.Buffer.BufferConfig>();
            return new JsonSessionRepository(
                bufferConfig.OutputDirectory,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<JsonSessionRepository>(),
                sp.GetRequiredService<AppSettings>().Storage.LegacySavePaths);
        });
        services.AddSingleton<Core.Storage.IStorageMediaCatalog, Core.Storage.SessionStorageMediaCatalog>();
        services.AddSingleton<SessionManager>();
        services.AddSingleton<MatchTracker>();

        // Discord webhook (10-minute timeout for large file uploads)
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });

        // Auto-update (separate HttpClient with short default timeout)
        services.AddSingleton<Core.Updates.IUpdateChecker>(sp =>
        {
            var updateHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            return new Core.Updates.GitHubUpdateChecker(
                updateHttp,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Core.Updates.GitHubUpdateChecker>(),
                sp.GetRequiredService<Core.Settings.ISettingsService>());
        });
        services.AddSingleton<Core.Discord.IDiscordWebhookService, Core.Discord.DiscordWebhookService>();

        // Audio device enumeration
        services.AddSingleton<Core.Audio.IAudioDeviceEnumerator, Core.Audio.WindowsAudioDeviceEnumerator>();
        // Webcam enumeration is metadata-only; it never constructs or opens a camera source.
        services.AddSingleton<Core.Webcam.IWebcamDeviceEnumerator, Core.Webcam.WindowsWebcamDeviceEnumerator>();

        // Hotkey service (singleton, initialized on UI thread before host starts)
        services.AddSingleton<Win32HotkeyService>();
        services.AddSingleton<IGlobalHotkeyService>(sp => sp.GetRequiredService<Win32HotkeyService>());

        // Performance monitoring
        services.AddSingleton<Core.Performance.IPerformanceMonitor, Core.Performance.ObsPerformanceMonitor>();
        services.AddSingleton<PerformanceViewModel>();

        // Overlay settings
        services.AddSingleton(sp => sp.GetRequiredService<AppSettings>().Overlay);

        // ViewModels
        services.AddSingleton<TrayIconViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<StatusOverlayViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<GameProfilesViewModel>();
        services.AddTransient<StorageDashboardViewModel>();

        // Highlight detection
        services.AddSingleton(sp => sp.GetRequiredService<AppSettings>().Highlight);
        services.AddSingleton<IHighlightDetector, Cs2HighlightDetector>();
        services.AddSingleton<IHighlightDetector, ValorantHighlightDetector>();
        services.AddSingleton<IHighlightDetector, Ow2HighlightDetector>();

        // AI audio highlight detector — starts as NoOp, upgraded to real detector
        // in the background once the ONNX model is downloaded (non-blocking).
        services.AddSingleton<IHighlightDetector>(new NoOpHighlightDetector());

        services.AddSingleton<HighlightDetectorRegistry>();

        // One serialized owner coordinates replay/session outputs and source identity.
        services.AddSingleton<ObsCaptureLifecycleBackend>();
        services.AddSingleton<ICaptureLifecycleBackend>(sp =>
            sp.GetRequiredService<ObsCaptureLifecycleBackend>());
        // The current native frame-counter provider is capability-rejected even when requested,
        // so the registry and observer remain inert until a future provider is admitted.
        services.AddSingleton<CaptureLivenessRegistry>();
        services.AddSingleton<ICaptureLivenessFailureClaimSource>(sp =>
            sp.GetRequiredService<CaptureLivenessRegistry>());
        services.AddSingleton<CaptureLifecycleCoordinator>();

        // Hosted services (background workers)
        // Start ingestion before capture producers. The host stops services in reverse order,
        // so it also remains alive until every producer has stopped.
        services.AddHostedService(sp =>
            sp.GetRequiredService<Core.Storage.CompletedMediaIngestionService>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<Core.Storage.StorageMaintenanceService>());
        services.AddSingleton<CaptureSourceManager>();
        services.AddHostedService(sp => sp.GetRequiredService<CaptureSourceManager>());
        // Register after CaptureSourceManager so reverse host shutdown drains observations first.
        services.AddSingleton<ActiveCaptureDeviceObserver>();
        services.AddHostedService(sp => sp.GetRequiredService<ActiveCaptureDeviceObserver>());
        // Register after source and device observers so reverse shutdown drains liveness first.
        services.AddSingleton<ActiveCaptureLivenessObserver>();
        services.AddHostedService(sp => sp.GetRequiredService<ActiveCaptureLivenessObserver>());
        services.AddHostedService<HotkeyListenerHostedService>();
        services.AddSingleton<HighlightClipService>();
        services.AddHostedService(sp => sp.GetRequiredService<HighlightClipService>());
        services.AddHostedService<AudioModelLoaderService>();

        // Manual capture UI — singleton so IsManualCaptureActive state persists across tray menu opens
        services.AddSingleton<ManualCaptureViewModel>();
    }
}
