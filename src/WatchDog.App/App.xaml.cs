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

    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

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

        // Initialize hotkey service on UI thread (needs HWND message pump)
        var hotkeyService = _host.Services.GetRequiredService<Win32HotkeyService>();
        hotkeyService.Initialize();

        // Create system tray icon
        InitializeTrayIcon();

        try
        {
            // Recover orphaned sessions BEFORE starting hosted services —
            // CaptureSourceManager creates a new desktop session on start,
            // and RecoverOrphanedSessions would incorrectly mark it as Crashed.
            var sessionManager = _host.Services.GetRequiredService<SessionManager>();
            await sessionManager.RecoverOrphanedSessionsAsync();

            await _host.StartAsync();

            // Activate match tracking (resolves singleton, starts event subscription)
            _ = _host.Services.GetRequiredService<MatchTracker>();

            // Initialize status overlay if enabled
            var overlaySettings = _host.Services.GetRequiredService<OverlaySettings>();
            if (overlaySettings.Enabled)
            {
                ShowOverlay();
            }

            // Check for updates (non-blocking, failure is silent)
            _ = Task.Run(async () =>
            {
                try
                {
                    var checker = _host!.Services.GetRequiredService<Core.Updates.IUpdateChecker>();
                    var update = await checker.CheckForUpdateAsync();
                    if (update?.IsUpdateAvailable == true)
                    {
                        await Application.Current!.Dispatcher.InvokeAsync(() =>
                        {
                            var vm = _host.Services.GetRequiredService<MainWindowViewModel>();
                            vm.SetUpdateAvailable(update);
                        });
                    }
                }
                catch { /* update check failure is non-fatal */ }
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"WatchDog failed to start:\n\n{ex.Message}",
                "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _overlayWindow?.Close();
        _trayIcon?.Dispose();

        try
        {
            if (_host is not null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(5));
                _host.Dispose();
            }
        }
        catch { /* best-effort shutdown */ }

        base.OnExit(e);
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
            ToolTipText = "WatchDog",
            ContextMenu = CreateTrayContextMenu(viewModel),
            NoLeftClickDelay = true,
        };

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

    private static void ConfigureServices(HostBuilderContext context, IServiceCollection services)
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
        services.AddSingleton<ISessionRecorder>(sp =>
        {
            var config = sp.GetRequiredService<SessionRecordingConfig>();
            if (!config.IsSessionRecordingEnabled)
                return new NullSessionRecorder();

            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var captureConfig = sp.GetRequiredService<CaptureConfig>();
            // Encoder creation is deferred to StartAsync() — OBS must be initialized first.
            return new ObsSessionRecorder(
                config,
                captureConfig.Bitrate,
                captureConfig.AudioBitrate,
                loggerFactory.CreateLogger<ObsSessionRecorder>());
        });

        // Audio mixer — wraps OBS audio sources from capture engine
        services.AddSingleton(sp => sp.GetRequiredService<AppSettings>().Audio);
        services.AddSingleton<Core.Audio.IAudioMixer>(sp =>
        {
            var engine = sp.GetRequiredService<ICaptureEngine>() as ObsCaptureEngine;
            var audioConfig = sp.GetRequiredService<Core.Audio.AudioMixConfig>();
            var mixer = new Core.Audio.ObsAudioMixer(
                engine?.DesktopAudioSource,
                engine?.MicAudioSource,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<Core.Audio.ObsAudioMixer>());
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
                MaxStorageGb = settings.Storage.MaxStorageGb,
                AutoDeleteDays = settings.Storage.AutoDeleteDays,
            };
        });
        services.AddSingleton<Core.Storage.IClipStorage, Core.Storage.ClipStorageManager>();

        // Session management
        services.AddSingleton<ISessionRepository>(sp =>
        {
            var bufferConfig = sp.GetRequiredService<Core.Buffer.BufferConfig>();
            return new JsonSessionRepository(
                bufferConfig.OutputDirectory,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<JsonSessionRepository>());
        });
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

        // Hosted services (background workers)
        services.AddSingleton<CaptureSourceManager>();
        services.AddHostedService(sp => sp.GetRequiredService<CaptureSourceManager>());
        services.AddHostedService<HotkeyListenerHostedService>();
        services.AddHostedService<SessionRecordingHostedService>();
        services.AddHostedService<HighlightClipService>();
        services.AddHostedService<AudioModelLoaderService>();

        // Manual capture UI — singleton so IsManualCaptureActive state persists across tray menu opens
        services.AddSingleton<ManualCaptureViewModel>();
    }
}
