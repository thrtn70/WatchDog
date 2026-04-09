using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ObsKit.NET.Encoders;
using TikrClipr.Core.Capture;
using TikrClipr.Core.Events;
using TikrClipr.Core.GameDetection;
using TikrClipr.Core.Highlights;
using TikrClipr.Core.Highlights.Cs2;
using TikrClipr.Core.Highlights.Overwatch2;
using TikrClipr.Core.Highlights.RainbowSixSiege;
using TikrClipr.Core.Highlights.Valorant;
using TikrClipr.Core.Hotkeys;
using TikrClipr.Core.Recording;
using TikrClipr.Core.Settings;
using TikrClipr.App.Services;
using TikrClipr.App.ViewModels;

namespace TikrClipr.App;

public partial class App : Application
{
    private IHost? _host;
    private H.NotifyIcon.TaskbarIcon? _trayIcon;

    public static IServiceProvider Services { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Validate runtime dependencies before building the host
        var validation = Core.Runtime.RuntimeValidator.Validate();
        if (!validation.IsValid)
        {
            var msg = "TikrClipr is missing required components:\n\n"
                    + string.Join("\n", validation.MissingComponents)
                    + "\n\nThe app will continue but some features may not work.";
            MessageBox.Show(msg, "TikrClipr — Missing Components",
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

        await _host.StartAsync();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();

        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private void InitializeTrayIcon()
    {
        var viewModel = Services.GetRequiredService<TrayIconViewModel>();

        _trayIcon = new H.NotifyIcon.TaskbarIcon
        {
            ToolTipText = "TikrClipr",
            ContextMenu = CreateTrayContextMenu(viewModel),
            NoLeftClickDelay = true,
        };

        // Load icon from embedded resource stream
        try
        {
            var iconUri = new Uri("pack://application:,,,/Resources/TikrClipr.ico");
            var iconStream = Application.GetResourceStream(iconUri);
            if (iconStream is not null)
            {
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

    private static System.Windows.Controls.ContextMenu CreateTrayContextMenu(TrayIconViewModel vm)
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var openItem = new System.Windows.Controls.MenuItem { Header = "Open TikrClipr" };
        openItem.Click += (_, _) => vm.ShowMainWindowCommand.Execute(null);
        menu.Items.Add(openItem);

        var saveItem = new System.Windows.Controls.MenuItem { Header = "Save Clip" };
        saveItem.Click += async (_, _) => await vm.SaveClipCommand.ExecuteAsync(null);
        menu.Items.Add(saveItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings" };
        settingsItem.Click += (_, _) => vm.ShowSettingsCommand.Execute(null);
        menu.Items.Add(settingsItem);

        menu.Items.Add(new System.Windows.Controls.Separator());

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
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
            return new ObsSessionRecorder(
                config,
                VideoEncoder.CreateX264(name: "Session x264", bitrate: captureConfig.Bitrate, preset: "veryfast"),
                AudioEncoder.CreateAac(bitrate: captureConfig.AudioBitrate),
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

        // Clip editor and storage
        services.AddSingleton<Core.ClipEditor.IClipEditor, Core.ClipEditor.FFmpegClipEditor>();
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

        // Discord webhook
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromMinutes(10) });
        services.AddSingleton<Core.Discord.IDiscordWebhookService, Core.Discord.DiscordWebhookService>();

        // Audio device enumeration
        services.AddSingleton<Core.Audio.IAudioDeviceEnumerator, Core.Audio.WindowsAudioDeviceEnumerator>();

        // Hotkey service (singleton, initialized on UI thread before host starts)
        services.AddSingleton<Win32HotkeyService>();
        services.AddSingleton<IGlobalHotkeyService>(sp => sp.GetRequiredService<Win32HotkeyService>());

        // Performance monitoring
        services.AddSingleton<Core.Performance.IPerformanceMonitor, Core.Performance.ObsPerformanceMonitor>();
        services.AddSingleton<PerformanceViewModel>();

        // ViewModels
        services.AddSingleton<TrayIconViewModel>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<StorageDashboardViewModel>();

        // Highlight detection
        services.AddSingleton(sp => sp.GetRequiredService<AppSettings>().Highlight);
        services.AddSingleton<IHighlightDetector, Cs2HighlightDetector>();
        services.AddSingleton<IHighlightDetector, ValorantHighlightDetector>();
        services.AddSingleton<IHighlightDetector, Ow2HighlightDetector>();
        services.AddSingleton<IHighlightDetector, R6HighlightDetector>();
        services.AddSingleton<HighlightDetectorRegistry>();

        // Hosted services (background workers)
        services.AddHostedService<GameDetectorHostedService>();
        services.AddHostedService<HotkeyListenerHostedService>();
        services.AddHostedService<SessionRecordingHostedService>();
        services.AddHostedService<HighlightClipService>();
    }
}
