using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.App.Logging;
using WatchDog.App.Services;
using WatchDog.App.ViewModels;
using WatchDog.Core.Capture;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Highlights;
using WatchDog.Core.Settings;
using WatchDog.Core.Webcam;
using WatchDog.Native.Win32;

namespace WatchDog.Integration.Tests;

public sealed class CaptureSourceManagerTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task DisabledOrUnsupportedProfileHasTypedReasonBeforeWindowAcquisition(bool autoRecord, bool unsupported)
    {
        var settings = PolicyHarness.Defaults with
        {
            GameProfiles = [new() { GameExecutableName = "game.exe", AutoRecord = autoRecord,
                Mode = unsupported ? GameRecordingMode.Highlight : GameRecordingMode.ReplayBuffer }],
        };
        await using var harness = new PolicyHarness(settings);
        await harness.StartAsync();
        AutomaticCapturePolicyDecisionEvent? published = null;
        using var subscription = harness.Bus.Subscribe<AutomaticCapturePolicyDecisionEvent>(e => published = e);
        harness.Detector.EmitStarted(PolicyHarness.Game("game.exe", 42));
        await harness.Manager.AwaitLastGameStartHandlingAsync();
        Assert.NotNull(published);
        Assert.Equal(autoRecord ? CapturePolicyDisposition.Unsupported : CapturePolicyDisposition.Disabled, published.Decision.Disposition);
        Assert.NotEmpty(published.Decision.Message);
        Assert.Equal(0, harness.Engine.ResolveAttempts);
        Assert.Empty(harness.Backend.StartCalls);
        Assert.Equal(0, harness.Settings.SaveCount);
    }

    [Fact]
    public async Task ProfileAndDefaultsEditsDuringAcquisitionCannotChangeFrozenRequest()
    {
        var profile = new GameRecordingProfile { GameExecutableName = "game.exe", BitrateKbps = 7_000, BufferDurationSeconds = 30 };
        var profiles = new List<GameRecordingProfile> { profile };
        var settings = PolicyHarness.Defaults with { GameProfiles = profiles };
        await using var harness = new PolicyHarness(settings);
        await harness.StartAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Engine.BeforeSuccessfulResolution = () => { reached.SetResult(); release.Task.GetAwaiter().GetResult(); };
        var detection = Task.Run(() => harness.Detector.EmitStarted(PolicyHarness.Game("game.exe", 42)));
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            profiles[0] = profile with { BitrateKbps = 99_000, Mode = GameRecordingMode.SessionRecording };
            harness.Settings.Save(settings with { Capture = settings.Capture with { Quality = 40 }, GameProfiles = profiles });
        }
        finally { release.TrySetResult(); }
        await detection;
        await harness.Manager.AwaitLastGameStartHandlingAsync();
        var context = Assert.Single(harness.Backend.StartCalls);
        Assert.Equal(7_000, context.Request.StartPolicy!.Capture.Bitrate);
        Assert.Equal(20, context.Request.StartPolicy.Capture.Quality);
        Assert.Equal(30, context.Request.StartPolicy.Buffer.MaxSeconds);
        Assert.Equal(Core.Recording.RecordingMode.ReplayBufferOnly, context.Request.StartPolicy.Recording.Mode);
        Assert.Equal(20_000, settings.Capture.Bitrate);
        Assert.Equal(7_000, profile.BitrateKbps);
    }

    [Fact]
    public async Task SecondGameCancelsStaleAcquisitionAndReceivesItsOwnProfile()
    {
        var settings = PolicyHarness.Defaults with
        {
            GameProfiles = [
            new() { GameExecutableName = "old.exe", BitrateKbps = 7_000 },
            new() { GameExecutableName = "new.exe", BitrateKbps = 9_000 }]
        };
        await using var harness = new PolicyHarness(settings);
        await harness.StartAsync();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        harness.Engine.BeforeSuccessfulResolution = () =>
        {
            if (Interlocked.Increment(ref attempts) == 1) { reached.SetResult(); release.Task.GetAwaiter().GetResult(); }
        };
        var oldGame = PolicyHarness.Game("old.exe", 42);
        var detection = Task.Run(() => harness.Detector.EmitStarted(oldGame));
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(3));
            harness.Detector.EmitStarted(PolicyHarness.Game("new.exe", 43));
            await harness.Manager.AwaitLastGameStartHandlingAsync();
        }
        finally { release.TrySetResult(); }
        await detection;
        await harness.Manager.AwaitLastGameStartHandlingAsync();
        var context = Assert.Single(harness.Backend.StartCalls);
        Assert.Equal("new.exe", Assert.IsType<GameCaptureTarget>(context.RequestedTarget).ExecutableName);
        Assert.Equal(9_000, context.Request.StartPolicy!.Capture.Bitrate);
        harness.Detector.EmitStopped(oldGame);
        Assert.Empty(harness.Backend.StopCalls);
    }
    [Fact]
    public async Task LegacyIndexOnlyDesktopConfiguration_FailsClosedWithoutPersistingReorderedDisplay()
    {
        var engine = new DelayedWindowEngine(resolveOnAttempt: 1);
        var backend = new Backend();
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        var detector = new Detector();
        var eventBus = new InMemoryEventBus();
        var settings = new AppSettings
        {
            DesktopCaptureEnabled = true,
            Capture = new CaptureConfig
            {
                MonitorIndex = 0,
                MonitorStableId = string.Empty,
            },
        };
        var settingsService = new SettingsService(settings);
        var registry = new HighlightDetectorRegistry(
            [],
            eventBus,
            NullLogger<HighlightDetectorRegistry>.Instance);
        using var manager = new CaptureSourceManager(
            detector,
            engine,
            lifecycle,
            eventBus,
            settings,
            settingsService,
            registry,
            NullLogger<CaptureSourceManager>.Instance);

        await manager.StartAsync(CancellationToken.None);

        Assert.Empty(backend.StartCalls);
        Assert.Equal(0, settingsService.SaveCount);
        Assert.Equal(0, engine.ResolveDisplayByDeviceNameCalls);
        Assert.Equal(string.Empty, settingsService.Load().Capture.MonitorStableId);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task DelayedGameWindow_IsRetriedAndSubmittedWithExactIdentity()
    {
        var engine = new DelayedWindowEngine(resolveOnAttempt: 2);
        var backend = new Backend();
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        var detector = new Detector();
        var eventBus = new InMemoryEventBus();
        var settings = new AppSettings { DesktopCaptureEnabled = false };
        var registry = new HighlightDetectorRegistry(
            [],
            eventBus,
            NullLogger<HighlightDetectorRegistry>.Instance);
        using var manager = new CaptureSourceManager(
            detector,
            engine,
            lifecycle,
            eventBus,
            settings,
            new SettingsService(settings),
            registry,
            NullLogger<CaptureSourceManager>.Instance);
        await manager.StartAsync(CancellationToken.None);
        var game = new GameInfo
        {
            ExecutableName = "delayed-game.exe",
            DisplayName = "Delayed Game",
            ProcessId = 42,
            ProcessStartedAt = DateTimeOffset.UnixEpoch,
        };

        detector.EmitStarted(game);
        var context = await backend.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var requested = Assert.IsType<GameCaptureTarget>(context.RequestedTarget);
        Assert.Equal(game.ProcessId, requested.ProcessId);
        Assert.Equal(game.ProcessStartedAt, requested.ProcessStartedAt);
        Assert.Equal(new IntPtr(0x1234), lifecycle.Snapshot.Context?.ActualTarget is WindowCaptureTarget window
            ? window.WindowHandle
            : IntPtr.Zero);
        Assert.Equal(2, engine.ResolveAttempts);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ManualTakeoverDuringWindowResolution_CancelsAutomaticStartWithoutDisposalRace()
    {
        var engine = new DelayedWindowEngine(resolveOnAttempt: 1);
        var backend = new Backend();
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        var detector = new Detector();
        var eventBus = new InMemoryEventBus();
        var settings = new AppSettings
        {
            DesktopCaptureEnabled = false,
            Capture = new() { RateControl = RateControlType.CBR },
            GameProfiles = [new() { GameExecutableName = "delayed-game.exe", BitrateKbps = 7_000 }],
        };
        var registry = new HighlightDetectorRegistry(
            [],
            eventBus,
            NullLogger<HighlightDetectorRegistry>.Instance);
        var managerLogger = new RecordingLogger<CaptureSourceManager>();
        using var manager = new CaptureSourceManager(
            detector,
            engine,
            lifecycle,
            eventBus,
            settings,
            new SettingsService(settings),
            registry,
            managerLogger);
        await manager.StartAsync(CancellationToken.None);
        var resolutionReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseResolution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.BeforeSuccessfulResolution = () =>
        {
            resolutionReached.SetResult();
            releaseResolution.Task.GetAwaiter().GetResult();
        };

        var detection = Task.Run(() => detector.EmitStarted(new GameInfo
        {
            ExecutableName = "delayed-game.exe",
            DisplayName = "Delayed Game",
            ProcessId = 42,
            ProcessStartedAt = DateTimeOffset.UnixEpoch,
        }));

        await resolutionReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var manualStart = manager.StartManualCaptureAsync(
            "manual.exe",
            "Manual Window",
            77,
            new IntPtr(0x7777),
            "ManualClass");
        releaseResolution.SetResult();

        Assert.True((await manualStart).Succeeded);
        await detection.WaitAsync(TimeSpan.FromSeconds(3));
        await manager.AwaitLastGameStartHandlingAsync().WaitAsync(TimeSpan.FromSeconds(3));
        var onlyStart = Assert.Single(backend.StartCalls);
        Assert.IsType<WindowCaptureTarget>(onlyStart.RequestedTarget);
        Assert.Null(onlyStart.Request.StartPolicy);
        Assert.True(manager.IsManualCaptureActive);
        Assert.Empty(managerLogger.Errors);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RegionOwnerGeometryLossStopsTheExactManualContext()
    {
        var engine = new DelayedWindowEngine(resolveOnAttempt: 1);
        var backend = new Backend();
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        var detector = new Detector();
        var eventBus = new InMemoryEventBus();
        var settings = new AppSettings { DesktopCaptureEnabled = false };
        var registry = new HighlightDetectorRegistry(
            [],
            eventBus,
            NullLogger<HighlightDetectorRegistry>.Instance);
        using var manager = new CaptureSourceManager(
            detector,
            engine,
            lifecycle,
            eventBus,
            settings,
            new SettingsService(settings),
            registry,
            NullLogger<CaptureSourceManager>.Instance);
        await manager.StartAsync(CancellationToken.None);

        var start = await manager.StartManualRegionCaptureAsync(
            "display-1",
            new PhysicalCaptureBounds(120, 80, 640, 360));

        Assert.True(start.Succeeded);
        Assert.Equal(CaptureSourceKind.Region, manager.ActiveSource?.Kind);
        var active = Assert.IsType<RegionCaptureTarget>(lifecycle.Snapshot.Context?.ActualTarget);
        engine.RegionAvailable = false;

        var stopped = await backend.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(active, stopped.ActualTarget);
        Assert.Equal(CaptureLifecycleStatus.Idle, lifecycle.Snapshot.Status);
        Assert.Null(manager.ActiveSource);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StaleBackendSourceLossEvent_AfterManualSwitchCannotStopCurrentCapture()
    {
        var engine = new DelayedWindowEngine(resolveOnAttempt: 1);
        var backend = new Backend();
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        var detector = new Detector();
        var eventBus = new InMemoryEventBus();
        var settings = new AppSettings { DesktopCaptureEnabled = false };
        var registry = new HighlightDetectorRegistry(
            [],
            eventBus,
            NullLogger<HighlightDetectorRegistry>.Instance);
        using var manager = new CaptureSourceManager(
            detector,
            engine,
            lifecycle,
            eventBus,
            settings,
            new SettingsService(settings),
            registry,
            NullLogger<CaptureSourceManager>.Instance);
        await manager.StartAsync(CancellationToken.None);

        Assert.True((await manager.StartManualCaptureAsync(
            "first.exe",
            "First Window",
            77,
            new IntPtr(0x7777),
            "FirstClass")).Succeeded);
        var staleContext = Assert.IsType<CaptureSessionContext>(lifecycle.Snapshot.Context);

        Assert.True((await manager.StartManualCaptureAsync(
            "second.exe",
            "Second Window",
            88,
            new IntPtr(0x8888),
            "SecondClass")).Succeeded);
        var currentContext = Assert.IsType<CaptureSessionContext>(lifecycle.Snapshot.Context);
        var stopCountAfterSwitch = backend.StopCalls.Count;

        eventBus.Publish(new CaptureSourceLostEvent(staleContext, "late callback"));
        await manager.AwaitLastSourceLossHandlingAsync().WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(stopCountAfterSwitch, backend.StopCalls.Count);
        Assert.Equal(currentContext.SessionId, lifecycle.Snapshot.Context?.SessionId);
        Assert.Equal(currentContext.Generation, lifecycle.Snapshot.Context?.Generation);
        Assert.Equal(CaptureLifecycleStatus.Active, lifecycle.Snapshot.Status);

        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExplicitManualStart_FreezesConfiguredWebcamSelection()
    {
        var engine = new DelayedWindowEngine(resolveOnAttempt: 1);
        var backend = new Backend();
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        var detector = new Detector();
        var eventBus = new InMemoryEventBus();
        var settings = new AppSettings
        {
            DesktopCaptureEnabled = false,
            Webcam = new WebcamConfig
            {
                EnabledForExplicitManualRecording = true,
                DeviceId = "exact-camera-id",
                DisplayName = "Physical camera",
            },
        };
        var registry = new HighlightDetectorRegistry(
            [],
            eventBus,
            NullLogger<HighlightDetectorRegistry>.Instance);
        using var manager = new CaptureSourceManager(
            detector,
            engine,
            lifecycle,
            eventBus,
            settings,
            new SettingsService(settings),
            registry,
            NullLogger<CaptureSourceManager>.Instance);
        await manager.StartAsync(CancellationToken.None);

        var manual = await manager.StartManualCaptureAsync(
            "manual.exe",
            "Manual Window",
            77,
            new IntPtr(0x7777),
            "ManualClass");

        Assert.True(manual.Succeeded);
        var frozen = Assert.IsType<WebcamCaptureSelection>(Assert.Single(backend.StartCalls).Request.Webcam);
        Assert.Equal("exact-camera-id", frozen.DeviceId);
        Assert.Equal(WebcamCaptureAuthorizationScope.ExplicitManualRecordingOnly, frozen.AuthorizationScope);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PickerRequestIsFrozenBeforeDispatchAndExactStopCannotAffectReplacement()
    {
        var engine = new DelayedWindowEngine(resolveOnAttempt: 1);
        var backend = new Backend();
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        var detector = new Detector();
        var eventBus = new InMemoryEventBus();
        var settings = new AppSettings { DesktopCaptureEnabled = false };
        var registry = new HighlightDetectorRegistry(
            [], eventBus, NullLogger<HighlightDetectorRegistry>.Instance);
        using var manager = new CaptureSourceManager(
            detector, engine, lifecycle, eventBus, settings, new SettingsService(settings),
            registry, NullLogger<CaptureSourceManager>.Instance);
        await manager.StartAsync(CancellationToken.None);

        var first = manager.CreateManualWindowRequest(new WindowCaptureTarget(
            77, new IntPtr(0x7777), "First Window", "first.exe", "FirstClass"));
        Assert.Equal(CaptureInitiation.Manual, first.Initiation);
        Assert.NotEqual(Guid.Empty, first.RequestId);
        Assert.True((await manager.StartManualCaptureAsync(first)).Succeeded);
        Assert.Same(first, Assert.Single(backend.StartCalls).Request);
        var old = lifecycle.Snapshot.Context!;

        var second = manager.CreateManualWindowRequest(new WindowCaptureTarget(
            88, new IntPtr(0x8888), "Second Window", "second.exe", "SecondClass"));
        Assert.True((await manager.StartManualCaptureAsync(second)).Succeeded);
        var current = lifecycle.Snapshot.Context!;
        var count = backend.StopCalls.Count;
        var rejected = await manager.StopManualCaptureAsync(old);
        Assert.Equal(CaptureCommandDisposition.Rejected, rejected.Disposition);
        Assert.Same(current, lifecycle.Snapshot.Context);
        Assert.Equal(count, backend.StopCalls.Count);
        Assert.True((await manager.StopManualCaptureAsync(current)).Succeeded);
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedManualStopLogsTypedOutcomeWithoutWindowOrFailurePayload()
    {
        var lines = new List<string>();
        using var fileProvider = new FileLoggerProvider(lines.Add);
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(fileProvider));
        var engine = new DelayedWindowEngine(resolveOnAttempt: 1);
        var backend = new Backend { FailStop = true };
        await using var lifecycle = new CaptureLifecycleCoordinator(backend);
        var detector = new Detector();
        var bus = new InMemoryEventBus();
        var settings = new AppSettings { DesktopCaptureEnabled = false };
        var registry = new HighlightDetectorRegistry([], bus, NullLogger<HighlightDetectorRegistry>.Instance);
        using var manager = new CaptureSourceManager(detector, engine, lifecycle, bus, settings,
            new SettingsService(settings), registry, loggerFactory.CreateLogger<CaptureSourceManager>());
        await manager.StartAsync(CancellationToken.None);
        var title = "Private tutorial window title";
        var secret = @"C:\Users\Private Person\device-private-opaque-id";
        backend.StopFailure = new CaptureFailure(CaptureFailureKind.Backend, secret, $"Cleanup failed for {title}");

        var started = await manager.StartManualCaptureAsync("private.exe", title, 77, new IntPtr(0x7777), "PrivateClass");
        var stopped = await manager.StopManualCaptureAsync(started.After.Context!);

        Assert.Equal(CaptureCommandDisposition.Failed, stopped.Disposition);
        var failureLine = Assert.Single(lines, line => line.Contains("disposition=Failed", StringComparison.Ordinal));
        Assert.Contains("operation=Stop", failureLine);
        Assert.Contains("kind=Backend", failureLine);
        Assert.Contains($"operationId={stopped.OperationId:D}", failureLine);
        Assert.Contains($"beforeSessionId={started.After.Context!.SessionId:D}", failureLine);
        Assert.Contains("afterContextPresent=true", failureLine);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain(title, line);
            Assert.DoesNotContain(secret, line);
        });

        var degradation = new CaptureFailure(CaptureFailureKind.Backend, secret, $"Replay stopped for {title}");
        manager.LogTransition(new CaptureTransitionResult(Guid.NewGuid(), CaptureOperationKind.HealthChanged,
            CaptureCommandDisposition.Completed, started.After,
            started.After with { Health = CaptureHealth.Degraded, LastFailure = degradation }, null));
        var degradedLine = Assert.Single(lines, line => line.Contains("operation=HealthChanged", StringComparison.Ordinal));
        Assert.Contains("level=warning", degradedLine);
        Assert.Contains("disposition=Completed", degradedLine);
        Assert.Contains("afterHealth=Degraded", degradedLine);
        Assert.Contains("afterLastFailureKind=Backend", degradedLine);
        Assert.DoesNotContain(secret, degradedLine);
        Assert.DoesNotContain(title, degradedLine);

        backend.FailStop = false;
        await manager.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PickerViewModelCannotClaimOrStopUnrelatedManualCapture()
    {
        await using var harness = new PolicyHarness(PolicyHarness.Defaults);
        using var picker = new ManualCaptureViewModel(harness.Manager);
        await picker.SelectWindowCommand.ExecuteAsync(new WindowEnumerator.WindowInfo(
            new IntPtr(0x7777), "Selected Window", "selected.exe", 77, "SelectedClass"));
        Assert.True(picker.IsManualCaptureActive);
        Assert.True(picker.CanRequestStop);
        Assert.Equal("Selected Window", picker.ActiveCaptureTitle);

        Assert.True((await harness.Manager.StartManualCaptureAsync(
            "other.exe", "Other Window", 88, new IntPtr(0x8888), "OtherClass")).Succeeded);
        Assert.False(picker.IsManualCaptureActive);
        Assert.False(picker.CanRequestStop);
        Assert.Equal(string.Empty, picker.ActiveCaptureTitle);
        var stopCount = harness.Backend.StopCalls.Count;
        await picker.StopCaptureCommand.ExecuteAsync(null);
        Assert.Equal(stopCount, harness.Backend.StopCalls.Count);
    }

    [Fact]
    public async Task PickerViewModelRetainsFailedStopForExactRetry()
    {
        await using var harness = new PolicyHarness(PolicyHarness.Defaults);
        using var picker = new ManualCaptureViewModel(harness.Manager);
        await picker.SelectWindowCommand.ExecuteAsync(new WindowEnumerator.WindowInfo(
            new IntPtr(0x7777), "Selected Window", "selected.exe", 77, "SelectedClass"));
        harness.Backend.FailStop = true;
        await picker.StopCaptureCommand.ExecuteAsync(null);
        Assert.False(picker.IsManualCaptureActive);
        Assert.True(picker.CanRequestStop);
        Assert.Contains("cleanup", picker.StatusText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cleanup failed", picker.StatusText);
        harness.Backend.FailStop = false;
        await picker.StopCaptureCommand.ExecuteAsync(null);
        Assert.False(picker.CanRequestStop);
        Assert.Equal(string.Empty, picker.ActiveCaptureTitle);
    }

    [Fact]
    public async Task PickerQueuedObservationReadsLatestOwnerAndDisposeIgnoresPendingCallback()
    {
        await using var harness = new PolicyHarness(PolicyHarness.Defaults);
        await harness.StartAsync();
        var posted = new QueuedSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(posted);
        ManualCaptureViewModel picker;
        try
        {
            picker = new ManualCaptureViewModel(harness.Manager);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
        using (picker)
        {
            await picker.SelectWindowCommand.ExecuteAsync(new WindowEnumerator.WindowInfo(
                new IntPtr(0x7777), "Selected Window", "selected.exe", 77, "SelectedClass"));
            Assert.True(picker.IsManualCaptureActive);
            Assert.True((await harness.Manager.StartManualCaptureAsync(
                "other.exe", "Other Window", 88, new IntPtr(0x8888), "OtherClass")).Succeeded);
            Assert.True(posted.Count > 0);
            posted.DispatchOne();
            Assert.False(picker.IsManualCaptureActive);
            Assert.False(picker.CanRequestStop);
            Assert.Contains("Another capture", picker.StatusText);
            var status = picker.StatusText;
            picker.Dispose();
            posted.DispatchAll();
            Assert.Equal(status, picker.StatusText);
        }
    }

    [Fact]
    public async Task PickerShowsPreDispatchSettingsFailureWithoutStartingBackend()
    {
        var settings = PolicyHarness.Defaults with
        {
            Webcam = new WebcamConfig { EnabledForExplicitManualRecording = true, DeviceId = string.Empty },
        };
        await using var harness = new PolicyHarness(settings);
        await harness.StartAsync();
        using var picker = new ManualCaptureViewModel(harness.Manager);
        await picker.SelectWindowCommand.ExecuteAsync(new WindowEnumerator.WindowInfo(
            new IntPtr(0x7777), "Selected Window", "selected.exe", 77, "SelectedClass"));
        Assert.Empty(harness.Backend.StartCalls);
        Assert.False(picker.IsManualCaptureActive);
        Assert.False(picker.CanRequestStop);
        Assert.Contains("capture settings are invalid", picker.StatusText);
        Assert.DoesNotContain("DeviceId", picker.StatusText);
    }

    [Fact]
    public async Task PickerKeepsSuccessfulStopResultWhenConfiguredReacquireThrows()
    {
        var settings = PolicyHarness.Defaults with
        {
            DesktopCaptureEnabled = true,
            Capture = new() { MonitorStableId = "configured-display" },
        };
        await using var harness = new PolicyHarness(settings);
        await harness.StartAsync();
        using var picker = new ManualCaptureViewModel(harness.Manager);
        await picker.SelectWindowCommand.ExecuteAsync(new WindowEnumerator.WindowInfo(
            new IntPtr(0x7777), "Selected Window", "selected.exe", 77, "SelectedClass"));
        Assert.True(picker.CanRequestStop);
        harness.Engine.BeforeDisplayResolution = () => throw new InvalidOperationException("fixture resolver failure");
        await picker.StopCaptureCommand.ExecuteAsync(null);
        Assert.Single(harness.Backend.StopCalls);
        Assert.False(picker.CanRequestStop);
        Assert.DoesNotContain("Stop did not complete", picker.StatusText);
        Assert.Null(harness.Manager.LifecycleSnapshot.Context);
    }

#pragma warning disable CS0067
    private sealed class Detector : IGameDetector
    {
        public GameInfo? CurrentGame { get; private set; }
        public event Action<GameInfo>? GameStarted;
        public event Action<GameInfo>? GameStopped;
        public void EmitStarted(GameInfo game)
        {
            CurrentGame = game;
            GameStarted?.Invoke(game);
        }
        public void EmitStopped(GameInfo game) => GameStopped?.Invoke(game);
        public void Start() { }
        public void Stop() => CurrentGame = null;
        public void Dispose() { }
    }

    private sealed class SettingsService(AppSettings settings) : ISettingsService
    {
        private AppSettings _settings = settings;
        public int SaveCount { get; private set; }
        public event Action<AppSettings>? SettingsChanged;
        public AppSettings Load() => _settings;
        public void Save(AppSettings settings)
        {
            SaveCount++;
            _settings = settings;
            SettingsChanged?.Invoke(settings);
        }
    }
#pragma warning restore CS0067

    private sealed class Backend : ICaptureLifecycleBackend
    {
        public bool FailStop { get; set; }
        public CaptureFailure? StopFailure { get; set; }
        public TaskCompletionSource<CaptureSessionContext> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<CaptureSessionContext> StartCalls { get; } = [];
        public TaskCompletionSource<CaptureSessionContext> Stopped { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<CaptureSessionContext> StopCalls { get; } = [];

        public Task<CaptureBackendStartResult> StartAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken)
        {
            StartCalls.Add(context);
            Started.TrySetResult(context);
            var actualTarget = context.RequestedTarget switch
            {
                GameCaptureTarget game => new WindowCaptureTarget(
                    game.ProcessId,
                    new IntPtr(0x1234),
                    "Delayed Game",
                    game.ExecutableName),
                WindowCaptureTarget window => window,
                _ => context.RequestedTarget,
            };
            return Task.FromResult(CaptureBackendStartResult.Success(actualTarget));
        }

        public Task<CaptureBackendCommandResult> StopAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken)
        {
            StopCalls.Add(context);
            Stopped.TrySetResult(context);
            return Task.FromResult(FailStop
                ? CaptureBackendCommandResult.Failed(StopFailure ?? new(CaptureFailureKind.Backend,
                    "fixture.stop", "cleanup failed"))
                : CaptureBackendCommandResult.Success());
        }

        public Task<CaptureBackendSaveResult> SaveAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(CaptureBackendSaveResult.Success("clip.mkv"));
    }

    private sealed class PolicyHarness : IAsyncDisposable
    {
        public static AppSettings Defaults => new()
        {
            DesktopCaptureEnabled = false,
            Capture = new() { Encoder = EncoderType.X264, RateControl = RateControlType.CBR },
        };
        public static GameInfo Game(string executable, int pid) => new()
        {
            ExecutableName = executable,
            DisplayName = executable,
            ProcessId = pid,
            ProcessStartedAt = DateTimeOffset.UnixEpoch,
        };
        public Detector Detector { get; } = new();
        public DelayedWindowEngine Engine { get; } = new(1);
        public Backend Backend { get; } = new();
        public InMemoryEventBus Bus { get; } = new();
        public SettingsService Settings { get; }
        private readonly CaptureLifecycleCoordinator _lifecycle;
        public CaptureSourceManager Manager { get; }
        public PolicyHarness(AppSettings settings)
        {
            Settings = new(settings);
            _lifecycle = new(Backend);
            Manager = new(Detector, Engine, _lifecycle, Bus, settings, Settings,
                new HighlightDetectorRegistry([], Bus, NullLogger<HighlightDetectorRegistry>.Instance),
                NullLogger<CaptureSourceManager>.Instance);
        }
        public Task StartAsync() => Manager.StartAsync(CancellationToken.None);
        public async ValueTask DisposeAsync()
        {
            await Manager.StopAsync(CancellationToken.None);
            Manager.Dispose();
            await _lifecycle.DisposeAsync();
        }
    }

#pragma warning disable CS0067
    private sealed class DelayedWindowEngine(int resolveOnAttempt) : ICaptureEngine
    {
        public int ResolveAttempts { get; private set; }
        public Action? BeforeSuccessfulResolution { get; set; }
        public Action? BeforeDisplayResolution { get; set; }
        public bool RegionAvailable { get; set; } = true;
        public int ResolveDisplayByDeviceNameCalls { get; private set; }
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
        public DisplayCaptureTarget? ResolveDisplayTarget(string stableDisplayId)
        {
            BeforeDisplayResolution?.Invoke();
            return null;
        }
        public DisplayCaptureTarget? ResolveDisplayTargetByDeviceName(string deviceName)
        {
            ResolveDisplayByDeviceNameCalls++;
            return new DisplayCaptureTarget("unexpected-stable-id", deviceName, 0);
        }
        public bool IsDisplayTargetAvailable(DisplayCaptureTarget target) => false;
        public RegionCaptureTarget? ResolveRegionTarget(string owningDisplayId, PhysicalCaptureBounds physicalBounds) =>
            new(owningDisplayId, physicalBounds, new PhysicalCaptureBounds(0, 0, 1920, 1080));
        public bool IsRegionTargetAvailable(RegionCaptureTarget target) => RegionAvailable;
        public bool IsWindowTargetAvailable(WindowCaptureTarget target) => true;
        public WindowCaptureTarget? ResolveGameWindowTarget(GameCaptureTarget game)
        {
            if (++ResolveAttempts < resolveOnAttempt)
                return null;

            BeforeSuccessfulResolution?.Invoke();
            return new WindowCaptureTarget(
                game.ProcessId,
                new IntPtr(0x1234),
                "Delayed Game",
                game.ExecutableName);
        }
        public Task StartDesktopCaptureAsync(DisplayCaptureTarget target, bool startReplayBuffer, CancellationToken ct = default) => Task.CompletedTask;
        public Task StartRegionCaptureAsync(RegionCaptureTarget target, bool startReplayBuffer, CancellationToken ct = default) => Task.CompletedTask;
        public Task StartWindowCaptureAsync(CaptureSource source, bool startReplayBuffer, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> SaveReplayAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);
        public void Dispose() { }
    }

    private sealed class QueuedSynchronizationContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public int Count => _callbacks.Count;
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public void DispatchOne()
        {
            var (callback, state) = _callbacks.Dequeue();
            callback(state);
        }
        public void DispatchAll()
        {
            while (_callbacks.Count > 0)
                DispatchOne();
        }
    }
#pragma warning restore CS0067

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Errors.Add(formatter(state, exception));
        }
    }
}
