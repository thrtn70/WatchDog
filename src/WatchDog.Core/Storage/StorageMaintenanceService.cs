using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;
using WatchDog.Core.Recording;

namespace WatchDog.Core.Storage;

public sealed record StorageVolumeSnapshot(
    bool IsAvailable,
    long? AvailableFreeSpaceBytes,
    string? FailureMessage = null);

public interface IStorageVolumeProbe
{
    StorageVolumeSnapshot Measure(string storagePath);
}

public sealed class DriveInfoStorageVolumeProbe : IStorageVolumeProbe
{
    public StorageVolumeSnapshot Measure(string storagePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(storagePath);
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root))
                return new StorageVolumeSnapshot(false, null, "The storage volume could not be resolved.");

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
                return new StorageVolumeSnapshot(false, null, $"Storage volume {root} is not ready.");

            return new StorageVolumeSnapshot(true, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new StorageVolumeSnapshot(false, null, ex.Message);
        }
    }
}

public sealed record StorageSafetyStatus(
    bool IsPressure,
    bool QuotaMet,
    long? AvailableFreeSpaceBytes,
    string Message,
    DateTimeOffset CheckedAt,
    StorageCleanupResult? LastCleanup)
{
    public static StorageSafetyStatus NotChecked { get; } = new(
        false,
        true,
        null,
        "Storage safety has not run yet.",
        DateTimeOffset.MinValue,
        null);
}

public interface IStorageSafetyMonitor
{
    StorageSafetyStatus Status { get; }
    event Action<StorageSafetyStatus>? StatusChanged;
    Task<StorageSafetyStatus> RunOnceAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The single production owner for periodic cleanup and live free-space admission.
/// Its first check completes before later hosted capture producers can start.
/// </summary>
public sealed class StorageMaintenanceService :
    IStorageSafetyMonitor,
    IHostedService,
    IAsyncDisposable
{
    private readonly IStorageMaintenance _maintenance;
    private readonly IStorageMediaCatalog _mediaCatalog;
    private readonly IProtectedMediaRegistry _protectedMedia;
    private readonly ISessionRecorder _sessionRecorder;
    private readonly IWebcamRecorder _webcamRecorder;
    private readonly CaptureLifecycleCoordinator _captureLifecycle;
    private readonly IStorageVolumeProbe _volumeProbe;
    private readonly StorageConfig _config;
    private readonly ILogger<StorageMaintenanceService> _logger;
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private readonly object _lifecycleLock = new();
    private CancellationTokenSource? _stop;
    private Task _loop = Task.CompletedTask;
    private StorageSafetyStatus _status = StorageSafetyStatus.NotChecked;

    public StorageMaintenanceService(
        IStorageMaintenance maintenance,
        IProtectedMediaRegistry protectedMedia,
        ISessionRecorder sessionRecorder,
        IWebcamRecorder webcamRecorder,
        CaptureLifecycleCoordinator captureLifecycle,
        IStorageVolumeProbe volumeProbe,
        StorageConfig config,
        ILogger<StorageMaintenanceService> logger)
        : this(
            maintenance,
            new EmptyStorageMediaCatalog(),
            protectedMedia,
            sessionRecorder,
            webcamRecorder,
            captureLifecycle,
            volumeProbe,
            config,
            logger)
    {
    }

    public StorageMaintenanceService(
        IStorageMaintenance maintenance,
        IStorageMediaCatalog mediaCatalog,
        IProtectedMediaRegistry protectedMedia,
        ISessionRecorder sessionRecorder,
        IWebcamRecorder webcamRecorder,
        CaptureLifecycleCoordinator captureLifecycle,
        IStorageVolumeProbe volumeProbe,
        StorageConfig config,
        ILogger<StorageMaintenanceService> logger)
    {
        _maintenance = maintenance;
        _mediaCatalog = mediaCatalog;
        _protectedMedia = protectedMedia;
        _sessionRecorder = sessionRecorder;
        _webcamRecorder = webcamRecorder;
        _captureLifecycle = captureLifecycle;
        _volumeProbe = volumeProbe;
        _config = config;
        _logger = logger;
        ValidateConfig(config);
    }

    public StorageSafetyStatus Status => Volatile.Read(ref _status);

    public event Action<StorageSafetyStatus>? StatusChanged;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource stop;
        lock (_lifecycleLock)
        {
            if (_stop is not null) return;
            stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _stop = stop;
        }

        // Hosted services start in registration order. This synchronous first pass
        // establishes storage admission before any capture producer is started.
        await RunOnceAsync(cancellationToken).ConfigureAwait(false);
        _loop = RunLoopAsync(stop.Token);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? stop;
        Task loop;
        lock (_lifecycleLock)
        {
            stop = _stop;
            loop = _loop;
        }

        if (stop is null) return;
        stop.Cancel();
        try
        {
            await loop.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // Expected when the periodic wait is interrupted for shutdown.
        }
    }

    public async Task<StorageSafetyStatus> RunOnceAsync(
        CancellationToken cancellationToken = default)
    {
        await _cycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RunCycleAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_lifecycleLock)
        {
            _stop?.Dispose();
            _stop = null;
        }
        _cycleGate.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_config.MaintenanceInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_captureLifecycle.IsAdmissionClosed) return;
                await RunOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal hosted-service shutdown.
        }
    }

    private async Task<StorageSafetyStatus> RunCycleAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        StorageCleanupResult? cleanup = null;
        Exception? cleanupFailure = null;
        StorageMediaCatalogSnapshot? catalog = null;
        using var activeMedia = ProtectActiveOutputs();
        try
        {
            catalog = await _mediaCatalog.SnapshotAsync(cancellationToken).ConfigureAwait(false);
            using var catalogMedia = ProtectPaths(catalog.OwnedPaths);
            cleanup = catalog.ExistingFiles.Count > 0
                ? await _maintenance.RunCleanupAsync(
                    catalog.ExistingFiles,
                    cancellationToken).ConfigureAwait(false)
                : await _maintenance.RunCleanupAsync(
                    catalog.ExistingBytes,
                    cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            cleanupFailure = ex;
            _logger.LogError(ex, "Storage cleanup failed; capture admission will be blocked");
        }

        var volume = _volumeProbe.Measure(_config.BasePath);
        var measured = cleanupFailure is null && catalog?.Failures.Count == 0;
        var quotaMet = cleanup?.QuotaMet == true && measured;
        // Suspended cleanup deleted nothing and could not measure the quota. Capture stays
        // admitted so new recordings still reach disk; the free-space reserve still applies.
        var cleanupSuspended = cleanup?.IsSuspended == true && measured;
        var quotaAdmits = quotaMet || cleanupSuspended;
        var belowReserve = volume.AvailableFreeSpaceBytes is long available &&
            available < _config.MinimumFreeSpaceReserveBytes;
        var pressure = !volume.IsAvailable || !quotaAdmits || belowReserve;
        var canRecover = volume.IsAvailable &&
            quotaAdmits &&
            volume.AvailableFreeSpaceBytes >= _config.FreeSpaceRecoveryBytes;

        string message;
        if (cleanupFailure is not null)
            message = $"Storage cleanup failed: {cleanupFailure.Message}";
        else if (catalog?.Failures.Count > 0)
            message = $"{catalog.Failures.Count} session media file(s) could not be measured safely.";
        else if (!volume.IsAvailable)
            message = $"Storage volume is unavailable: {volume.FailureMessage ?? "unknown error"}";
        else if (!quotaAdmits)
            message = cleanup?.Failures.Count > 0
                ? $"Storage quota remains exceeded after cleanup; {cleanup.Failures.Count} deletion(s) failed."
                : cleanup?.RetainedRecoveredClips > 0
                    ? $"Storage quota remains exceeded: {cleanup.RetainedRecoveredClips} clip(s) restored by a library rebuild " +
                      "are never deleted automatically. Delete clips you don't need or raise the storage limit."
                    : "Storage quota remains exceeded because no eligible media can be deleted safely.";
        else if (belowReserve)
            message = $"Storage has {FormatBytes(volume.AvailableFreeSpaceBytes!.Value)} free; " +
                $"{FormatBytes(_config.MinimumFreeSpaceReserveBytes)} is required.";
        else if (_captureLifecycle.IsStorageAdmissionBlocked && !canRecover)
            message = $"Storage is recovering; {FormatBytes(_config.FreeSpaceRecoveryBytes)} free is required to resume capture.";
        else if (cleanupSuspended)
            message = $"Automatic cleanup is paused: {cleanup!.SuspendedReason} The free-space reserve is still enforced.";
        else
            message = "Storage quota and free-space reserve are healthy.";

        if (pressure)
            await ApplyPressureAsync(message, cancellationToken).ConfigureAwait(false);
        else if (_captureLifecycle.IsStorageAdmissionBlocked && canRecover)
            await _captureLifecycle.ClearStoragePressureAsync(cancellationToken).ConfigureAwait(false);

        var isPressure = pressure || _captureLifecycle.IsStorageAdmissionBlocked;
        var status = new StorageSafetyStatus(
            isPressure,
            quotaMet,
            volume.AvailableFreeSpaceBytes,
            message,
            DateTimeOffset.UtcNow,
            cleanup);
        Volatile.Write(ref _status, status);
        PublishStatus(status);
        return status;
    }

    private async Task ApplyPressureAsync(string reason, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var context = _captureLifecycle.Snapshot.Context;
            var result = await _captureLifecycle.HandleStoragePressureAsync(
                new CaptureStoragePressure(
                    context?.SessionId,
                    context?.Generation,
                    DateTimeOffset.UtcNow,
                    reason),
                cancellationToken).ConfigureAwait(false);
            if (result.Disposition != CaptureCommandDisposition.Ignored)
                return;
        }

        _logger.LogWarning(
            "Storage pressure changed capture admission but the active generation changed twice before a correlated stop completed");
    }

    private IDisposable ProtectActiveOutputs()
    {
        var leases = new List<IDisposable>(2);
        try
        {
            AddProtection(_sessionRecorder.CurrentOutputPath, leases);
            AddProtection(_webcamRecorder.CurrentOutputPath, leases);
            return new LeaseGroup(leases);
        }
        catch
        {
            foreach (var lease in leases)
                lease.Dispose();
            throw;
        }
    }

    private IDisposable ProtectPaths(IEnumerable<string> paths)
    {
        var leases = new List<IDisposable>();
        try
        {
            foreach (var path in paths)
                AddProtection(path, leases);
            return new LeaseGroup(leases);
        }
        catch
        {
            foreach (var lease in leases)
                lease.Dispose();
            throw;
        }
    }

    private void AddProtection(string? path, ICollection<IDisposable> leases)
    {
        if (!string.IsNullOrWhiteSpace(path))
            leases.Add(_protectedMedia.Protect(path));
    }

    private void PublishStatus(StorageSafetyStatus status)
    {
        try { StatusChanged?.Invoke(status); }
        catch (Exception ex) { _logger.LogWarning(ex, "Storage safety status subscriber failed"); }
    }

    private static void ValidateConfig(StorageConfig config)
    {
        if (config.MinimumFreeSpaceReserveBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(config), "The free-space reserve cannot be negative.");
        if (config.FreeSpaceRecoveryBytes < config.MinimumFreeSpaceReserveBytes)
            throw new ArgumentOutOfRangeException(nameof(config), "The recovery threshold must be at least the reserve.");
        if (config.MaintenanceInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(config), "The maintenance interval must be positive.");
    }

    private static string FormatBytes(long bytes) =>
        $"{bytes / (1024d * 1024 * 1024):F1} GB";

    private sealed class LeaseGroup(IReadOnlyList<IDisposable> leases) : IDisposable
    {
        public void Dispose()
        {
            foreach (var lease in leases)
                lease.Dispose();
        }
    }
}
