using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Capture;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Recording;
using WatchDog.Core.Storage;
using WatchDog.Core.Tests.Helpers;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Tests.Storage;

public sealed class StorageMaintenanceServiceTests
{
    [Fact]
    public async Task QuotaPressure_BlocksCaptureAndSurfacesDeletionFailures()
    {
        var cleanup = new FakeMaintenance
        {
            Result = new StorageCleanupResult(
                0,
                0,
                [new StorageCleanupFailure("locked.mp4", "in use")],
                500,
                false),
        };
        await using var fixture = CreateFixture(cleanup, Available(10_000));

        var status = await fixture.Service.RunOnceAsync();

        Assert.True(status.IsPressure);
        Assert.False(status.QuotaMet);
        Assert.Contains("1 deletion(s) failed", status.Message);
        Assert.True(fixture.Coordinator.IsStorageAdmissionBlocked);
        Assert.Equal(CaptureFailureKind.Storage, fixture.Coordinator.Snapshot.LastFailure?.Kind);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, 0L)]
    public async Task DisconnectedOrFullVolume_StopsAdmission(bool available, long? freeBytes)
    {
        await using var fixture = CreateFixture(
            new FakeMaintenance(),
            new StorageVolumeSnapshot(available, freeBytes, available ? null : "drive disconnected"));

        var status = await fixture.Service.RunOnceAsync();

        Assert.True(status.IsPressure);
        Assert.True(fixture.Coordinator.IsStorageAdmissionBlocked);
        Assert.Contains(available ? "required" : "unavailable", status.Message);
    }

    [Fact]
    public async Task RechecksUseRecoveryHysteresisBeforeReopeningAdmission()
    {
        var probe = new SequenceProbe(Available(50), Available(150), Available(250));
        await using var fixture = CreateFixture(new FakeMaintenance(), probe);

        Assert.True((await fixture.Service.RunOnceAsync()).IsPressure);
        Assert.True((await fixture.Service.RunOnceAsync()).IsPressure);
        Assert.True(fixture.Coordinator.IsStorageAdmissionBlocked);

        var recovered = await fixture.Service.RunOnceAsync();

        Assert.False(recovered.IsPressure);
        Assert.False(fixture.Coordinator.IsStorageAdmissionBlocked);
    }

    [Fact]
    public async Task LowSpaceStopsTheExactActiveGenerationBeforeDiskExhaustion()
    {
        await using var fixture = CreateFixture(new FakeMaintenance(), Available(50));
        var request = new CaptureRequest(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            CaptureInitiation.Manual,
            new GameCaptureTarget(
                "game",
                42,
                "game.exe",
                DateTimeOffset.UtcNow.AddSeconds(-1),
                "Game"));
        var started = await fixture.Coordinator.StartAsync(request);
        Assert.True(started.Succeeded);

        var status = await fixture.Service.RunOnceAsync();

        Assert.True(status.IsPressure);
        Assert.Equal(1, fixture.Backend.StopCalls);
        Assert.Null(fixture.Coordinator.Snapshot.Context);
        Assert.True(fixture.Coordinator.IsStorageAdmissionBlocked);
    }

    [Fact]
    public async Task ActiveGrowingOutputsAreProtectedAndConcurrentCyclesStaySerialized()
    {
        var registry = new ProtectedMediaRegistry();
        var session = new FakeSessionRecorder { CurrentOutputPath = Path.Combine(Path.GetTempPath(), "active-session.mp4") };
        var webcam = new FakeWebcamRecorder { CurrentOutputPath = Path.Combine(Path.GetTempPath(), "active-webcam.mp4") };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new FakeMaintenance
        {
            Handler = async cancellationToken =>
            {
                Assert.True(registry.IsProtected(session.CurrentOutputPath!));
                Assert.True(registry.IsProtected(webcam.CurrentOutputPath!));
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return FakeMaintenance.Healthy;
            },
        };
        await using var fixture = CreateFixture(
            cleanup,
            Available(10_000),
            registry,
            session,
            webcam);

        var first = fixture.Service.RunOnceAsync();
        await entered.Task.WaitAsync(TestTimeouts.Signal);
        session.CurrentOutputPath = Path.Combine(Path.GetTempPath(), "active-session-growing.mp4");
        var second = fixture.Service.RunOnceAsync();
        await Task.Delay(20);
        Assert.Equal(1, cleanup.MaximumConcurrency);
        release.TrySetResult();

        await Task.WhenAll(first, second);

        Assert.Equal(1, cleanup.MaximumConcurrency);
        Assert.Equal(2, cleanup.Calls);
        Assert.False(registry.IsProtected(session.CurrentOutputPath!));
    }

    [Fact]
    public async Task StartAsync_PerformsInitialSafetyCheckBeforeReturning()
    {
        var cleanup = new FakeMaintenance();
        await using var fixture = CreateFixture(cleanup, Available(10_000));

        await fixture.Service.StartAsync(CancellationToken.None);

        Assert.Equal(1, cleanup.Calls);
        Assert.NotEqual(DateTimeOffset.MinValue, fixture.Service.Status.CheckedAt);
        await fixture.Service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task SessionCatalogBytesArePassedIntoQuotaCleanup()
    {
        var cleanup = new FakeMaintenance();
        var ownedPath = Path.Combine(Path.GetTempPath(), "session-owned.mkv");
        var registry = new ProtectedMediaRegistry();
        cleanup.Handler = _ =>
        {
            Assert.True(registry.IsProtected(ownedPath));
            return Task.FromResult(FakeMaintenance.Healthy);
        };
        var catalog = new FixedCatalog(4_096, ownedPath);
        await using var fixture = CreateFixture(
            cleanup,
            Available(10_000),
            registry: registry,
            catalog: catalog);

        await fixture.Service.RunOnceAsync();

        Assert.Equal(4_096, cleanup.LastProtectedExternalBytes);
    }

    private static Fixture CreateFixture(
        FakeMaintenance maintenance,
        StorageVolumeSnapshot volume,
        ProtectedMediaRegistry? registry = null,
        FakeSessionRecorder? session = null,
        FakeWebcamRecorder? webcam = null,
        IStorageMediaCatalog? catalog = null) =>
        CreateFixture(maintenance, new SequenceProbe(volume), registry, session, webcam, catalog);

    private static Fixture CreateFixture(
        FakeMaintenance maintenance,
        IStorageVolumeProbe probe,
        ProtectedMediaRegistry? registry = null,
        FakeSessionRecorder? session = null,
        FakeWebcamRecorder? webcam = null,
        IStorageMediaCatalog? catalog = null)
    {
        registry ??= new ProtectedMediaRegistry();
        session ??= new FakeSessionRecorder();
        webcam ??= new FakeWebcamRecorder();
        var backend = new FakeBackend();
        var coordinator = new CaptureLifecycleCoordinator(backend);
        var config = new StorageConfig
        {
            BasePath = Path.GetTempPath(),
            MinimumFreeSpaceReserveBytes = 100,
            FreeSpaceRecoveryBytes = 200,
            MaintenanceInterval = TimeSpan.FromHours(1),
        };
        var service = new StorageMaintenanceService(
            maintenance,
            catalog ?? new FixedCatalog(0),
            registry,
            session,
            webcam,
            coordinator,
            probe,
            config,
            NullLogger<StorageMaintenanceService>.Instance);
        return new Fixture(service, coordinator, backend);
    }

    private static StorageVolumeSnapshot Available(long bytes) => new(true, bytes);

    private sealed class Fixture(
        StorageMaintenanceService service,
        CaptureLifecycleCoordinator coordinator,
        FakeBackend backend) : IAsyncDisposable
    {
        public StorageMaintenanceService Service { get; } = service;
        public CaptureLifecycleCoordinator Coordinator { get; } = coordinator;
        public FakeBackend Backend { get; } = backend;

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Coordinator.DisposeAsync();
        }
    }

    private sealed class SequenceProbe(params StorageVolumeSnapshot[] snapshots) : IStorageVolumeProbe
    {
        private int _index;

        public StorageVolumeSnapshot Measure(string storagePath)
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, snapshots.Length - 1);
            return snapshots[index];
        }
    }

    private sealed class FakeMaintenance : IStorageMaintenance
    {
        private int _concurrency;
        public static StorageCleanupResult Healthy { get; } = new(0, 0, [], 0, true);
        public StorageCleanupResult Result { get; init; } = Healthy;
        public Func<CancellationToken, Task<StorageCleanupResult>>? Handler { get; set; }
        public int Calls { get; private set; }
        public int MaximumConcurrency { get; private set; }
        public long LastProtectedExternalBytes { get; private set; }

        public async Task<StorageCleanupResult> RunCleanupAsync(CancellationToken ct = default)
        {
            Calls++;
            var concurrency = Interlocked.Increment(ref _concurrency);
            MaximumConcurrency = Math.Max(MaximumConcurrency, concurrency);
            try
            {
                return Handler is null ? Result : await Handler(ct);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        public Task<StorageCleanupResult> RunCleanupAsync(
            long protectedExternalBytes,
            CancellationToken ct = default)
        {
            LastProtectedExternalBytes = protectedExternalBytes;
            return RunCleanupAsync(ct);
        }
    }

    private sealed class FixedCatalog(long bytes, params string[] paths) : IStorageMediaCatalog
    {
        public Task<StorageMediaCatalogSnapshot> SnapshotAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new StorageMediaCatalogSnapshot(
                paths.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase),
                bytes,
                [])
            {
                ExistingFiles = paths.ToDictionary(
                    Path.GetFullPath,
                    _ => bytes,
                    StringComparer.OrdinalIgnoreCase),
            });
    }

    private sealed class FakeSessionRecorder : ISessionRecorder
    {
        public bool IsRecording => CurrentOutputPath is not null;
        public bool HasActiveResources => IsRecording;
        public TimeSpan Elapsed => TimeSpan.Zero;
        public string? CurrentOutputPath { get; set; }
        public event Action<string>? SegmentSaved { add { } remove { } }
        public event Action<string>? Error { add { } remove { } }
        public event Action? Stopped { add { } remove { } }
        public Task StartAsync(string outputDirectory, GameInfo? game, CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeWebcamRecorder : IWebcamRecorder
    {
        public bool IsRecording => CurrentOutputPath is not null;
        public bool HasActiveResources => IsRecording;
        public bool HasNativeResources => IsRecording;
        public bool HasUncertainViewRelease => false;
        public string? CurrentOutputPath { get; set; }
        public Task StartAsync(WebcamCaptureSelection selection, string outputPath, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeBackend : ICaptureLifecycleBackend
    {
        public int StopCalls { get; private set; }

        public Task<CaptureBackendStartResult> StartAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken) => Task.FromResult(
                CaptureBackendStartResult.Success(context.RequestedTarget));

        public Task<CaptureBackendCommandResult> StopAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken)
        {
            StopCalls++;
            return Task.FromResult(CaptureBackendCommandResult.Success());
        }

        public Task<CaptureBackendSaveResult> SaveAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken) => Task.FromResult(
                CaptureBackendSaveResult.Failed(new CaptureFailure(
                    CaptureFailureKind.Unsupported,
                    "test.unsupported",
                    "Not used by this test.")));
    }
}
