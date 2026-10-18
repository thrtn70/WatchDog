using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using WatchDog.App.Services;
using WatchDog.App.ViewModels;
using WatchDog.Core.Audio;
using WatchDog.Core.Buffer;
using WatchDog.Core.Capture;
using WatchDog.Core.Recording;
using WatchDog.Core.Sessions;
using WatchDog.Core.Settings;
using WatchDog.Core.Storage;
using WatchDog.Native.Win32;

namespace WatchDog.Integration.Tests;

public sealed class TutorialStartupBindingCompositionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualCompositionSharesFrozenAuthorityWithoutPhysicalDependencies(bool separateTracks)
    {
        var settings = new AppSettings
        {
            Capture = new() { AudioBitrate = -1 },
            Audio = new() { SeparateAudioTracks = separateTracks, MicEnabled = false },
            Storage = new() { SavePath = @"C:\Tutorial\sub\..\" },
            Buffer = new() { OutputDirectory = @"C:\Tutorial\sub\..\" },
        };
        var service = new SettingsService(settings);
        using var provider = Compose(service, out var dependencies);
        var baseline = provider.GetRequiredService<CaptureStartupBaselineResult>();
        Assert.Same(baseline, provider.GetRequiredService<CaptureStartupBaselineResult>());
        var expectation = baseline.Baseline!.Expectation;
        var backend = provider.GetRequiredService<ObsCaptureLifecycleBackend>();
        using var workspace = Workspace(baseline);
        var plan = workspace.Plan!;
        Assert.Same(expectation, plan.Request.StartupExpectation);
        Assert.Null(backend.ValidateStart(plan.Request));
        Assert.Equal(RecordingMode.SessionRecording, plan.Request.StartPolicy!.Recording.Mode);
        Assert.Equal(RecordingMode.ReplayBufferOnly, provider.GetRequiredService<SessionRecordingConfig>().Mode);
        Assert.Equal(provider.GetRequiredService<CaptureConfig>().OutputWidth, expectation.StartupCanvas.OutputWidth);
        Assert.Equal(provider.GetRequiredService<CaptureConfig>().OutputHeight, expectation.StartupCanvas.OutputHeight);
        Assert.Equal(provider.GetRequiredService<CaptureConfig>().Fps, expectation.StartupCanvas.Fps);
        Assert.Equal(provider.GetRequiredService<AudioMixConfig>(), expectation.StartupAudio);
        Assert.Equal(provider.GetRequiredService<BufferConfig>().OutputDirectory,
            expectation.StartupOnlyConstraints.BufferOutputDirectory);
        Assert.Equal(provider.GetRequiredService<BufferConfig>().FilenameFormat,
            expectation.StartupOnlyConstraints.BufferFilenameFormat);
        Assert.Equal(provider.GetRequiredService<SessionRecordingConfig>().FileFormat,
            expectation.StartupOnlyConstraints.RecordingFileFormat);
        Assert.Equal(new CaptureStartPolicy(provider.GetRequiredService<CaptureConfig>(),
            provider.GetRequiredService<BufferConfig>(), provider.GetRequiredService<SessionRecordingConfig>()).Sanitized(),
            baseline.Baseline.DefaultStartPolicy);
        Assert.Equal(64, expectation.StartupOnlyConstraints.EffectiveAudioBitrate);
        Assert.Equal(-1, provider.GetRequiredService<CaptureConfig>().AudioBitrate);
        var routes = provider.GetRequiredService<AudioTrackPlan>();
        Assert.Same(plan.AudioTracks, routes);
        Assert.Equal(separateTracks ? 2 : 1, routes.EncoderCount);
        Assert.Equal(separateTracks ? 2 : 1, routes.Microphone.SourceTrackNumber);
        Assert.Contains(@"C:\Tutorial", workspace.OutputText);
        Assert.DoesNotContain("sub", workspace.OutputText);

        // Simulate SettingsChanged only after startup singletons have materialized.
        service.Save(settings with
        {
            Audio = settings.Audio with { MicEnabled = true, MicDeviceId = "new-mic" },
            Storage = settings.Storage with { SavePath = @"D:\NewSave" },
        });
        settings.CustomGames.Add(new() { ExecutableName = "new.exe", DisplayName = "New" });
        using var reopened = Workspace(provider.GetRequiredService<CaptureStartupBaselineResult>());
        Assert.Same(expectation, reopened.Plan!.Request.StartupExpectation);
        Assert.Null(backend.ValidateStart(plan.Request));
        Assert.False(reopened.Plan.StartupAudio.MicEnabled);
        Assert.DoesNotContain("NewSave", reopened.OutputText);
        Assert.Equal(1, service.LoadCount);

        using var otherProvider = Compose(new SettingsService(settings), out var otherDependencies);
        var otherBaseline = otherProvider.GetRequiredService<CaptureStartupBaselineResult>();
        Assert.NotEqual(expectation.HostBaselineId, otherBaseline.Baseline!.Expectation.HostBaselineId);
        Assert.Equal("capture.startup-binding.host-changed",
            otherProvider.GetRequiredService<ObsCaptureLifecycleBackend>().ValidateStart(plan.Request)?.Code);
        foreach (var vm in new[] { workspace, reopened })
        {
            Assert.False(vm.StartAvailable);
            Assert.False(vm.StartCommand.CanExecute(null));
            vm.StartCommand.Execute(null);
        }
        Assert.All(dependencies.Concat(otherDependencies), dependency => Assert.Equal(0, dependency.UnexpectedCalls));
    }

    [Fact]
    public void UnavailableBaselineDoesNotBreakLegacyBackendConstructionOrUnboundAdmission()
    {
        var settings = new AppSettings { Storage = new() { SavePath = "relative-private-path" } };
        using var provider = Compose(new SettingsService(settings), out var dependencies);
        var baseline = provider.GetRequiredService<CaptureStartupBaselineResult>();
        Assert.Null(baseline.Baseline);
        var backend = provider.GetRequiredService<ObsCaptureLifecycleBackend>();
        var request = new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            CaptureInitiation.Automatic, new GameCaptureTarget("game", 12, "game.exe"));
        Assert.Null(backend.ValidateStart(request));
        using var vm = Workspace(baseline);
        Assert.Null(vm.Plan);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.All(dependencies, dependency => Assert.Equal(0, dependency.UnexpectedCalls));
    }

    private static TutorialWorkspaceViewModel Workspace(CaptureStartupBaselineResult baseline)
    {
        var vm = new TutorialWorkspaceViewModel(baseline, new Observation(), _ => true);
        var window = new WindowEnumerator.WindowInfo(new IntPtr(1234), "Tutorial", "tutorial.exe", 42, "Fixture");
        vm.OfferWindows([window]);
        vm.SelectedWindow = window;
        vm.ConfirmWindowCommand.Execute(null);
        return vm;
    }

    private static ServiceProvider Compose(SettingsService settings, out List<RejectingDependency> dependencies)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        WatchDog.App.App.ConfigureServices(new HostBuilderContext(new Dictionary<object, object>()), services);
        services.RemoveAll<ISettingsService>();
        services.AddSingleton<ISettingsService>(settings);
        dependencies = [];
        Replace<ICaptureEngine>(services, dependencies);
        var recorder = DispatchProxy.Create<IReceiptSessionRecorder, RejectingDependency>();
        dependencies.Add((RejectingDependency)(object)recorder);
        services.RemoveAll<ISessionRecorder>();
        services.AddSingleton<ISessionRecorder>(recorder);
        Replace<IWebcamRecorder>(services, dependencies);
        Replace<ISessionRepository>(services, dependencies);
        Replace<IClipStorage>(services, dependencies);
        Replace<ICompletedMediaIngestion>(services, dependencies);
        return services.BuildServiceProvider();
    }

    private static void Replace<T>(IServiceCollection services, List<RejectingDependency> dependencies) where T : class
    {
        var fake = DispatchProxy.Create<T, RejectingDependency>();
        dependencies.Add((RejectingDependency)(object)fake);
        services.RemoveAll<T>();
        services.AddSingleton(fake);
    }

    // Proxies avoid accidentally inheriting physical behavior from a convenience fake.
    public interface IReceiptSessionRecorder : ISessionRecorder, ISessionSegmentReceiptSource { }

    public class RejectingDependency : DispatchProxy
    {
        public int UnexpectedCalls { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var name = targetMethod!.Name;
            if (name.StartsWith("add_", StringComparison.Ordinal) ||
                name.StartsWith("remove_", StringComparison.Ordinal) || name == nameof(IDisposable.Dispose)) return null;
            UnexpectedCalls++;
            throw new InvalidOperationException("Unexpected physical or persistence dependency operation.");
        }
    }

    private sealed class SettingsService(AppSettings initial) : ISettingsService
    {
        private AppSettings _current = initial;
        public int LoadCount { get; private set; }
        public AppSettings Load() { LoadCount++; return _current; }
        public void Save(AppSettings settings) { _current = settings; SettingsChanged?.Invoke(settings); }
        public event Action<AppSettings>? SettingsChanged;
    }

    private sealed class Observation : ITutorialLifecycleObservation
    {
        public CaptureLifecycleSnapshot Snapshot => CaptureLifecycleSnapshot.Initial;
        public event Action<CaptureLifecycleSnapshot>? SnapshotChanged { add { } remove { } }
    }
}
