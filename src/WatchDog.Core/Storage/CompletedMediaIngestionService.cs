using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Highlights;

namespace WatchDog.Core.Storage;

/// <summary>
/// The single application owner for completed replay media. All clip-index mutations are
/// serialized here, and every accepted item is journaled before indexing begins.
/// </summary>
public sealed class CompletedMediaIngestionService :
    ICompletedMediaIngestion,
    IHostedService,
    IAsyncDisposable
{
    private const int CurrentSchemaVersion = 1;
    private readonly IClipStorage _clipStorage;
    private readonly IProtectedMediaRegistry _protectedMedia;
    private readonly IStorageMediaCatalog _mediaCatalog;
    private readonly ILogger<CompletedMediaIngestionService> _logger;
    private readonly string _ledgerPath;
    private readonly string _backupPath;
    private readonly Channel<WorkItem> _work = Channel.CreateUnbounded<WorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly List<PendingEntry> _pending = [];
    private readonly List<UnattributedEntry> _unattributed = [];
    private readonly Dictionary<string, IDisposable> _pendingProtection =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _stateLock = new();
    private Task _worker = Task.CompletedTask;
    private bool _started;
    private bool _accepting;
    private int _notDurablyAccepted;
    private string? _lastFailure;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public CompletedMediaIngestionService(
        IClipStorage clipStorage,
        StorageConfig storageConfig,
        ILogger<CompletedMediaIngestionService> logger)
        : this(
            clipStorage,
            storageConfig,
            logger,
            new ProtectedMediaRegistry(),
            new EmptyStorageMediaCatalog())
    {
    }

    public CompletedMediaIngestionService(
        IClipStorage clipStorage,
        StorageConfig storageConfig,
        ILogger<CompletedMediaIngestionService> logger,
        IProtectedMediaRegistry protectedMedia)
        : this(clipStorage, storageConfig, logger, protectedMedia, new EmptyStorageMediaCatalog())
    {
    }

    public CompletedMediaIngestionService(
        IClipStorage clipStorage,
        StorageConfig storageConfig,
        ILogger<CompletedMediaIngestionService> logger,
        IProtectedMediaRegistry protectedMedia,
        IStorageMediaCatalog mediaCatalog)
    {
        _clipStorage = clipStorage;
        _protectedMedia = protectedMedia;
        _mediaCatalog = mediaCatalog;
        _logger = logger;
        _ledgerPath = Path.Combine(storageConfig.BasePath, "completed-media-pending.json");
        _backupPath = _ledgerPath + ".bak";
    }

    public event Action<CompletedMediaIngestionStatus>? StatusChanged;

    public CompletedMediaIngestionStatus Status
    {
        get
        {
            lock (_stateLock)
                return SnapshotStatus();
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            if (_started) return;
            _started = true;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_ledgerPath)!);
        LoadLedger();

        foreach (var entry in PendingSnapshot())
            ProtectPending(entry);

        foreach (var entry in PendingSnapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessPersistedEntryAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        await DiscoverUnattributedMediaAsync(cancellationToken).ConfigureAwait(false);
        lock (_stateLock)
            _accepting = true;
        PublishStatus();
        _worker = ProcessQueueAsync();
    }

    public Task<CompletedMediaIngestionResult> AcceptAsync(
        CompletedMediaReceipt media,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(media, highlightType: null, cancellationToken);

    public Task<CompletedMediaIngestionResult> EnrichAsync(
        CompletedMediaReceipt media,
        HighlightType highlightType,
        CancellationToken cancellationToken = default) =>
        EnqueueAsync(media, highlightType, cancellationToken);

    public async Task<CompletedMediaDrainResult> DrainAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        lock (_stateLock)
        {
            _accepting = false;
            _work.Writer.TryComplete();
        }

        try
        {
            if (timeout == Timeout.InfiniteTimeSpan)
                await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
                await _worker.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            var timedOut = Status;
            var durable = Volatile.Read(ref _notDurablyAccepted) == 0;
            return new CompletedMediaDrainResult(
                false,
                durable,
                timedOut.PendingRetryCount,
                timedOut.UnattributedCount,
                durable
                    ? "Completed-media indexing did not finish before the shutdown deadline; accepted work remains queued for retry."
                    : "Completed-media indexing did not finish before the shutdown deadline and some queued work is not durably journaled.");
        }

        var status = Status;
        var warning = status.NeedsAttention
            ? $"{status.PendingRetryCount} clip(s) remain pending retry and " +
              $"{status.UnattributedCount} file(s) need attribution. {status.LastFailure}"
            : null;
        return new CompletedMediaDrainResult(
            true,
            true,
            status.PendingRetryCount,
            status.UnattributedCount,
            warning);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await DrainAsync(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (!_started) return;
        await DrainAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        ReleaseAllPendingProtection();
    }

    private Task<CompletedMediaIngestionResult> EnqueueAsync(
        CompletedMediaReceipt media,
        HighlightType? highlightType,
        CancellationToken cancellationToken)
    {
        var validation = Validate(media);
        if (validation is not null)
        {
            return Task.FromResult(new CompletedMediaIngestionResult(
                CompletedMediaIngestionDisposition.Rejected,
                FailureMessage: validation));
        }

        var normalized = media with { FilePath = Path.GetFullPath(media.FilePath) };
        var completion = new TaskCompletionSource<CompletedMediaIngestionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkItem(normalized, highlightType, completion, cancellationToken);

        lock (_stateLock)
        {
            if (!_started || !_accepting)
            {
                return Task.FromResult(new CompletedMediaIngestionResult(
                    CompletedMediaIngestionDisposition.Rejected,
                    FailureMessage: "Completed-media ingestion is not accepting new work."));
            }
            Interlocked.Increment(ref _notDurablyAccepted);
            if (!_work.Writer.TryWrite(item))
            {
                Interlocked.Decrement(ref _notDurablyAccepted);
                return Task.FromResult(new CompletedMediaIngestionResult(
                    CompletedMediaIngestionDisposition.Rejected,
                    FailureMessage: "Completed-media ingestion is not accepting new work."));
            }
        }

        return completion.Task;
    }

    private async Task ProcessQueueAsync()
    {
        await foreach (var item in _work.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                var result = await ProcessNewEntryAsync(item).ConfigureAwait(false);
                item.Completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected completed-media ingestion failure for {Path}", item.Media.FilePath);
                item.Completion.TrySetResult(new CompletedMediaIngestionResult(
                    CompletedMediaIngestionDisposition.PendingRetry,
                    FailureMessage: ex.Message));
            }
        }
    }

    private async Task<CompletedMediaIngestionResult> ProcessNewEntryAsync(WorkItem item)
    {
        var pending = new PendingEntry(item.Media, item.HighlightType, 0, null);
        try
        {
            ProtectPending(pending);
            UpsertPending(pending);
            PersistLedger();
            Interlocked.Decrement(ref _notDurablyAccepted);
        }
        catch (Exception ex)
        {
            RemovePending(item.Media.FilePath, item.Media.CaptureRequestId);
            ReleasePendingProtection(item.Media.FilePath, item.Media.CaptureRequestId);
            Interlocked.Decrement(ref _notDurablyAccepted);
            SetFailure($"Could not durably accept {Path.GetFileName(item.Media.FilePath)}: {ex.Message}");
            return new CompletedMediaIngestionResult(
                CompletedMediaIngestionDisposition.Rejected,
                FailureMessage: ex.Message);
        }

        return await IndexPendingAsync(pending, item.CancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessPersistedEntryAsync(PendingEntry entry, CancellationToken cancellationToken)
    {
        await IndexPendingAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CompletedMediaIngestionResult> IndexPendingAsync(
        PendingEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            var clip = await _clipStorage.IndexCompletedMediaAsync(
                entry.Media,
                entry.HighlightType,
                cancellationToken).ConfigureAwait(false);
            RemovePending(entry.Media.FilePath, entry.Media.CaptureRequestId);
            RemoveUnattributed(entry.Media.FilePath);
            PersistLedger();
            ReleasePendingProtection(entry.Media.FilePath, entry.Media.CaptureRequestId);
            ClearFailureIfRecovered();
            PublishStatus();
            return new CompletedMediaIngestionResult(
                CompletedMediaIngestionDisposition.Indexed,
                clip);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RecordRetryableFailure(entry, "Indexing was cancelled before completion.");
        }
        catch (Exception ex)
        {
            return RecordRetryableFailure(entry, ex.Message);
        }
    }

    private CompletedMediaIngestionResult RecordRetryableFailure(PendingEntry entry, string message)
    {
        var failed = entry with { Attempts = entry.Attempts + 1, LastFailure = message };
        UpsertPending(failed);
        try
        {
            PersistLedger();
        }
        catch (Exception ledgerFailure)
        {
            _logger.LogError(ledgerFailure, "Could not update ingestion retry details for {Path}", entry.Media.FilePath);
        }
        SetFailure($"{Path.GetFileName(entry.Media.FilePath)}: {message}");
        return new CompletedMediaIngestionResult(
            CompletedMediaIngestionDisposition.PendingRetry,
            FailureMessage: message);
    }

    private async Task DiscoverUnattributedMediaAsync(CancellationToken cancellationToken)
    {
        var catalog = await _mediaCatalog.SnapshotAsync(cancellationToken).ConfigureAwait(false);
        var known = PendingSnapshot()
            .Select(entry => entry.Media.FilePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var path in _clipStorage.FindUnindexedMediaFiles())
        {
            var fullPath = Path.GetFullPath(path);
            if (known.Contains(fullPath) || catalog.OwnedPaths.Contains(fullPath)) continue;
            lock (_stateLock)
            {
                if (_unattributed.Any(entry =>
                        string.Equals(entry.FilePath, fullPath, StringComparison.OrdinalIgnoreCase)))
                    continue;
                _unattributed.Add(new UnattributedEntry(
                    fullPath,
                    "No durable session/request receipt was found. Attribution requires explicit user action."));
                changed = true;
            }
        }

        if (!changed) return;
        PersistLedger();
        var first = Status.UnattributedCount > 0
            ? Path.GetFileName(_unattributed[0].FilePath)
            : "media";
        SetFailure($"{first} is present on disk but has no durable attribution.");
    }

    private void LoadLedger()
    {
        Ledger? ledger = null;
        Exception? primaryFailure = null;
        try
        {
            ledger = ReadLedger(_ledgerPath);
        }
        catch (Exception ex)
        {
            primaryFailure = ex;
            _logger.LogWarning(ex, "Could not read completed-media ledger; trying backup");
        }

        if (ledger is null && File.Exists(_backupPath))
        {
            try
            {
                ledger = ReadLedger(_backupPath);
                ValidateLedger(ledger!);
            }
            catch (Exception backupFailure) when (backupFailure is IOException or
                UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                throw new InvalidDataException(
                    "Neither the completed-media ledger nor its recovery backup is valid.",
                    primaryFailure is null
                        ? backupFailure
                        : new AggregateException(primaryFailure, backupFailure));
            }
            if (primaryFailure is not null && File.Exists(_ledgerPath))
                PreserveCorruptLedger(_ledgerPath);
            RepairPrimaryLedgerFromBackup();
        }
        else if (ledger is null && primaryFailure is not null)
        {
            throw new InvalidDataException(
                "The completed-media ledger is corrupt and no valid recovery backup is available.",
                primaryFailure);
        }
        if (ledger is null) return;
        ValidateLedger(ledger);

        lock (_stateLock)
        {
            _pending.Clear();
            _pending.AddRange(ledger.Pending ?? []);
            _unattributed.Clear();
            _unattributed.AddRange(ledger.Unattributed ?? []);
        }
    }

    private static Ledger? ReadLedger(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<Ledger>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("Completed-media ledger contained null.");
    }

    private static void ValidateLedger(Ledger ledger)
    {
        if (ledger.SchemaVersion <= 0 || ledger.SchemaVersion > CurrentSchemaVersion)
            throw new InvalidDataException("The completed-media ledger schema is unsupported.");

        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ledger.Pending ?? [])
        {
            var validation = Validate(entry.Media);
            if (validation is not null || entry.Attempts < 0)
                throw new InvalidDataException($"The completed-media ledger contains an invalid pending entry: {validation}");
            var key = PendingProtectionKey(entry.Media.FilePath, entry.Media.CaptureRequestId);
            if (!known.Add(key))
                throw new InvalidDataException("The completed-media ledger contains duplicate pending work.");
        }

        var unattributed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ledger.Unattributed ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.FilePath) || !Path.IsPathFullyQualified(entry.FilePath))
                throw new InvalidDataException("The completed-media ledger contains an invalid unattributed path.");
            if (!unattributed.Add(Path.GetFullPath(entry.FilePath)))
                throw new InvalidDataException("The completed-media ledger contains duplicate unattributed work.");
        }
    }

    private void RepairPrimaryLedgerFromBackup()
    {
        var directory = Path.GetDirectoryName(_ledgerPath)!;
        var temp = Path.Combine(directory, $"completed-media-recovery.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(temp, File.ReadAllBytes(_backupPath));
            File.Move(temp, _ledgerPath, overwrite: true);
            _logger.LogWarning("Recovered completed-media ledger from {Backup}", _backupPath);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private static void PreserveCorruptLedger(string path)
    {
        var evidence = $"{path}.corrupt-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}";
        File.Copy(path, evidence, overwrite: false);
    }

    private void PersistLedger()
    {
        Ledger snapshot;
        lock (_stateLock)
            snapshot = new Ledger(CurrentSchemaVersion, [.. _pending], [.. _unattributed]);

        var directory = Path.GetDirectoryName(_ledgerPath)!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"completed-media-pending.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(snapshot, JsonOptions));
            if (File.Exists(_ledgerPath))
                File.Replace(temp, _ledgerPath, _backupPath);
            else
                File.Move(temp, _ledgerPath);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* best effort */ }
        }
    }

    private IReadOnlyList<PendingEntry> PendingSnapshot()
    {
        lock (_stateLock)
            return [.. _pending];
    }

    private void UpsertPending(PendingEntry entry)
    {
        lock (_stateLock)
        {
            var index = _pending.FindIndex(item =>
                string.Equals(item.Media.FilePath, entry.Media.FilePath, StringComparison.OrdinalIgnoreCase) &&
                item.Media.CaptureRequestId == entry.Media.CaptureRequestId);
            if (index < 0)
                _pending.Add(entry);
            else
                _pending[index] = entry;
        }
    }

    private void RemovePending(string path, Guid requestId)
    {
        lock (_stateLock)
            _pending.RemoveAll(entry =>
                string.Equals(entry.Media.FilePath, path, StringComparison.OrdinalIgnoreCase) &&
                entry.Media.CaptureRequestId == requestId);
    }

    private void ProtectPending(PendingEntry entry)
    {
        var key = PendingProtectionKey(entry.Media.FilePath, entry.Media.CaptureRequestId);
        lock (_stateLock)
        {
            if (_pendingProtection.ContainsKey(key)) return;
            _pendingProtection.Add(key, _protectedMedia.Protect(entry.Media.FilePath));
        }
    }

    private void ReleasePendingProtection(string path, Guid requestId)
    {
        IDisposable? lease = null;
        lock (_stateLock)
        {
            var key = PendingProtectionKey(path, requestId);
            if (_pendingProtection.Remove(key, out var current))
                lease = current;
        }
        lease?.Dispose();
    }

    private void ReleaseAllPendingProtection()
    {
        IDisposable[] leases;
        lock (_stateLock)
        {
            leases = [.. _pendingProtection.Values];
            _pendingProtection.Clear();
        }
        foreach (var lease in leases)
            lease.Dispose();
    }

    private static string PendingProtectionKey(string path, Guid requestId) =>
        $"{Path.GetFullPath(path)}|{requestId:D}";

    private void RemoveUnattributed(string path)
    {
        lock (_stateLock)
            _unattributed.RemoveAll(entry =>
                string.Equals(entry.FilePath, path, StringComparison.OrdinalIgnoreCase));
    }

    private void SetFailure(string message)
    {
        lock (_stateLock)
            _lastFailure = message;
        _logger.LogWarning("Completed-media ingestion needs attention: {Failure}", message);
        PublishStatus();
    }

    private void ClearFailureIfRecovered()
    {
        lock (_stateLock)
        {
            if (_pending.Count == 0 && _unattributed.Count == 0)
                _lastFailure = null;
        }
    }

    private void PublishStatus()
    {
        var status = Status;
        try { StatusChanged?.Invoke(status); }
        catch (Exception ex) { _logger.LogWarning(ex, "Completed-media status subscriber failed"); }
    }

    private CompletedMediaIngestionStatus SnapshotStatus() =>
        new(_pending.Count, _unattributed.Count, _lastFailure);

    private static string? Validate(CompletedMediaReceipt media)
    {
        if (string.IsNullOrWhiteSpace(media.FilePath)) return "A completed media path is required.";
        if (string.IsNullOrWhiteSpace(media.GameName)) return "A completed media game identity is required.";
        if (media.Origin == CompletedMediaOrigin.Capture &&
            (media.SessionId is null || media.SessionId == Guid.Empty))
            return "Captured media requires a completed session ID.";
        if (media.SessionId == Guid.Empty) return "A completed media session ID cannot be empty.";
        if (media.CaptureRequestId == Guid.Empty) return "A completed media capture request ID is required.";
        if (media.CaptureGeneration <= 0) return "A completed media capture generation must be positive.";
        if (media.MatchNumber < 0) return "A completed media match number cannot be negative.";
        return null;
    }

    private sealed record WorkItem(
        CompletedMediaReceipt Media,
        HighlightType? HighlightType,
        TaskCompletionSource<CompletedMediaIngestionResult> Completion,
        CancellationToken CancellationToken);

    private sealed record PendingEntry(
        CompletedMediaReceipt Media,
        HighlightType? HighlightType,
        int Attempts,
        string? LastFailure);

    private sealed record UnattributedEntry(string FilePath, string Reason);

    private sealed record Ledger(
        int SchemaVersion,
        IReadOnlyList<PendingEntry>? Pending,
        IReadOnlyList<UnattributedEntry>? Unattributed);
}
