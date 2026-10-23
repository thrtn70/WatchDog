using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using WatchDog.App.ViewModels;
using WatchDog.Core.Buffer;
using WatchDog.Core.Capture;
using WatchDog.Core.Sessions;
using WatchDog.Core.Settings;
using WatchDog.Core.Storage;

namespace WatchDog.Integration.Tests;

public sealed class LibraryUnavailableStartupCompositionTests : IDisposable
{
    private const string CorruptPrimary = "corrupt primary bytes";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), $"WatchDog_StartupLibrary_{Guid.NewGuid():N}");

    [Fact]
    public async Task CorruptClipIndexStillComposesStartupAndKeepsCaptureAdmissionOpen()
    {
        Directory.CreateDirectory(_root);
        var primary = Path.Combine(_root, "clips-index.json");
        File.WriteAllText(primary, CorruptPrimary);
        File.WriteAllText(primary + ".bak", "corrupt backup bytes");
        await using var provider = Compose();
        var sessions = provider.GetRequiredService<ISessionRepository>();
        var orphan = new GameSession
        {
            Id = Guid.NewGuid(),
            GameName = "CS2",
            GameExecutableName = "cs2.exe",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Status = SessionStatus.InProgress,
        };
        await sessions.SaveAsync(orphan);

        // App.StartHostAsync resolves SessionManager first; a corrupt index used to make this throw.
        // Its clips are unknown while the library is unavailable, so the session must survive.
        await provider.GetRequiredService<SessionManager>().RecoverOrphanedSessionsAsync();
        Assert.Equal(SessionStatus.Crashed, (await sessions.GetByIdAsync(orphan.Id))?.Status);
        var health = provider.GetRequiredService<IClipLibraryHealth>();
        Assert.False(health.LibraryStatus.IsAvailable);
        Assert.Same(provider.GetRequiredService<IClipStorage>(), health);
        Assert.IsType<ClipLibraryRebuilder>(provider.GetRequiredService<IClipLibraryRebuild>());
        var recovery = provider.GetRequiredService<LibraryRecoveryViewModel>();
        Assert.True(recovery.IsLibraryUnavailable);
        Assert.Contains("clips-index.json.corrupt-", recovery.BannerText);

        var safety = await provider.GetRequiredService<IStorageSafetyMonitor>().RunOnceAsync();

        Assert.False(safety.IsPressure);
        Assert.True(safety.LastCleanup?.IsSuspended);
        Assert.False(provider.GetRequiredService<CaptureLifecycleCoordinator>().IsStorageAdmissionBlocked);
        Assert.Equal(CorruptPrimary, File.ReadAllText(primary));
    }

    private ServiceProvider Compose()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        WatchDog.App.App.ConfigureServices(
            new HostBuilderContext(new Dictionary<object, object>()),
            services);
        services.AddSingleton<ISettingsService>(new StaticSettingsService(new AppSettings
        {
            Buffer = new BufferConfig { OutputDirectory = _root },
        }));
        services.AddSingleton<IStorageVolumeProbe>(new AmpleVolume());
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private sealed class AmpleVolume : IStorageVolumeProbe
    {
        public StorageVolumeSnapshot Measure(string storagePath) => new(true, long.MaxValue / 2);
    }

    private sealed class StaticSettingsService(AppSettings settings) : ISettingsService
    {
        public AppSettings Load() => settings;

        public void Save(AppSettings updatedSettings) =>
            throw new InvalidOperationException("The composition fixture is read-only.");

        public event Action<AppSettings>? SettingsChanged
        {
            add { }
            remove { }
        }
    }
}
