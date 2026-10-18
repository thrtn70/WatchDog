using System.Text.Json;
using System.Text.Json.Serialization;

namespace WatchDog.CaptureIsolation;

public sealed record RecoveryJournalLimits
{
    public RecoveryJournalLimits(
        int maxEntries,
        int maxMediaPerEntry,
        int maxSerializedBytes,
        int maxTokenLength)
    {
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        if (maxMediaPerEntry <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxMediaPerEntry));
        if (maxSerializedBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSerializedBytes));
        if (maxTokenLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTokenLength));
        MaxEntries = maxEntries;
        MaxMediaPerEntry = maxMediaPerEntry;
        MaxSerializedBytes = maxSerializedBytes;
        MaxTokenLength = maxTokenLength;
    }

    public int MaxEntries { get; }
    public int MaxMediaPerEntry { get; }
    public int MaxSerializedBytes { get; }
    public int MaxTokenLength { get; }
}

public sealed record RecoveryIdentity(
    Guid ParentInstanceId,
    Guid HelperInstanceId,
    Guid SessionId,
    Guid RequestId,
    long Generation,
    string PlanDigest)
{
    internal void Validate()
    {
        if (ParentInstanceId == Guid.Empty || HelperInstanceId == Guid.Empty ||
            SessionId == Guid.Empty || RequestId == Guid.Empty || Generation <= 0 ||
            PlanDigest is not { Length: 64 } ||
            !PlanDigest.All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F'))
        {
            throw new InvalidOperationException("Recovery identity is invalid.");
        }
    }
}

public enum RecoveryOwnershipState
{
    Planned,
    Launching,
    Assigned,
    CleanupPending,
    Reaped,
}

public enum RecoveryMediaState
{
    Planned,
    Writing,
    NativeClosed,
    Finalized,
    Indexed,
    Interrupted,
    Invalid,
    RecoveryRequired,
}

public sealed record RecoveryMediaRecord(
    Guid MediaId,
    Guid OutputEpoch,
    string StagingToken,
    string FinalToken,
    RecoveryMediaState State);

public sealed record RecoveryJournalEntry(
    RecoveryIdentity Identity,
    RecoveryOwnershipState OwnershipState,
    IReadOnlyList<RecoveryMediaRecord> Media);

public sealed record RecoveryMediaObservation(Guid MediaId, RecoveryMediaState State);

public sealed record RecoveryObservation(
    RecoveryIdentity Identity,
    RecoveryOwnershipState? OwnershipState,
    IReadOnlyList<RecoveryMediaObservation> Media);

public sealed record RecoveryJournalSnapshot(IReadOnlyList<RecoveryJournalEntry> Entries);

public interface IRecoveryJournalStorage
{
    ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken = default);

    ValueTask WriteAtomicallyAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default);
}

public sealed class RecoveryJournalCorruptedException : Exception
{
    public RecoveryJournalCorruptedException()
        : base("The capture recovery journal is invalid or unsupported.")
    {
    }
}

public sealed class CaptureRecoveryJournal
{
    private const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IRecoveryJournalStorage _storage;
    private readonly RecoveryJournalLimits _limits;

    public CaptureRecoveryJournal(IRecoveryJournalStorage storage, RecoveryJournalLimits limits)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
    }

    public async Task<RecoveryJournalSnapshot> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadEntriesAsync(cancellationToken).ConfigureAwait(false);
            return new(entries.Select(CloneEntry).ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpsertAsync(
        RecoveryJournalEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ValidateEntry(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadEntriesAsync(cancellationToken).ConfigureAwait(false);
            var index = entries.FindIndex(existing => existing.Identity == entry.Identity);
            if (index < 0)
            {
                if (entries.Count >= _limits.MaxEntries)
                    throw new InvalidOperationException("The recovery journal entry limit was reached.");
                entries.Add(CloneEntry(entry));
            }
            else
            {
                ValidateEntryTransition(entries[index], entry);
                entries[index] = CloneEntry(entry);
            }

            await PersistAsync(entries, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> ReconcileAsync(
        RecoveryObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        observation.Identity?.Validate();
        if (observation.Media is null)
            throw new ArgumentException("Media observations are required.", nameof(observation));
        if (observation.Media.Select(static item => item.MediaId).Distinct().Count() !=
            observation.Media.Count)
        {
            throw new InvalidOperationException("Duplicate media observations were rejected.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var entries = await LoadEntriesAsync(cancellationToken).ConfigureAwait(false);
            var index = entries.FindIndex(entry => entry.Identity == observation.Identity);
            if (index < 0)
                throw new InvalidOperationException("The exact recovery identity is not journaled.");

            var current = entries[index];
            var ownership = observation.OwnershipState ?? current.OwnershipState;
            EnsureOwnershipTransition(current.OwnershipState, ownership);
            var media = current.Media.Select(static item => item with { }).ToList();
            var changed = ownership != current.OwnershipState;

            foreach (var update in observation.Media)
            {
                if (update.MediaId == Guid.Empty || !Enum.IsDefined(update.State))
                    throw new InvalidOperationException("A media observation is invalid.");
                var mediaIndex = media.FindIndex(item => item.MediaId == update.MediaId);
                if (mediaIndex < 0)
                    throw new InvalidOperationException("A foreign media observation was rejected.");
                var existing = media[mediaIndex];
                EnsureMediaTransition(existing.State, update.State);
                if (existing.State != update.State)
                {
                    media[mediaIndex] = existing with { State = update.State };
                    changed = true;
                }
            }

            if (!changed)
                return false;
            var replacement = new RecoveryJournalEntry(current.Identity, ownership, media.ToArray());
            ValidateEntry(replacement);
            entries[index] = replacement;
            await PersistAsync(entries, cancellationToken).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<List<RecoveryJournalEntry>> LoadEntriesAsync(CancellationToken cancellationToken)
    {
        var bytes = await _storage.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (bytes is null || bytes.Length == 0)
            return [];
        if (bytes.Length > _limits.MaxSerializedBytes)
            throw new RecoveryJournalCorruptedException();

        try
        {
            var document = JsonSerializer.Deserialize<PersistedJournal>(bytes, JsonOptions);
            if (document is null || document.Version != CurrentVersion || document.Entries is null ||
                document.Entries.Count > _limits.MaxEntries)
            {
                throw new RecoveryJournalCorruptedException();
            }

            foreach (var entry in document.Entries)
            {
                if (entry is null)
                    throw new RecoveryJournalCorruptedException();
                ValidateEntry(entry);
            }
            var entries = document.Entries.Select(static entry => CloneEntry(entry!)).ToList();
            if (entries.Select(static entry => entry.Identity).Distinct().Count() != entries.Count)
                throw new RecoveryJournalCorruptedException();
            return entries;
        }
        catch (RecoveryJournalCorruptedException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new RecoveryJournalCorruptedException();
        }
    }

    private async Task PersistAsync(
        IReadOnlyList<RecoveryJournalEntry> entries,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            new PersistedJournal(
                CurrentVersion,
                entries.Select(static entry => (RecoveryJournalEntry?)CloneEntry(entry)).ToList()),
            JsonOptions);
        if (bytes.Length > _limits.MaxSerializedBytes)
            throw new InvalidOperationException("The recovery journal serialized size limit was reached.");
        await _storage.WriteAtomicallyAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private void ValidateEntry(RecoveryJournalEntry entry)
    {
        if (entry is null || entry.Identity is null || entry.Media is null)
            throw new InvalidOperationException("A recovery journal entry is invalid.");
        entry.Identity.Validate();
        if (!Enum.IsDefined(entry.OwnershipState) ||
            entry.Media.Count > _limits.MaxMediaPerEntry)
        {
            throw new InvalidOperationException("A recovery journal entry is invalid.");
        }

        var mediaIds = new HashSet<Guid>();
        foreach (var media in entry.Media)
        {
            if (media is null || media.MediaId == Guid.Empty || media.OutputEpoch == Guid.Empty ||
                !Enum.IsDefined(media.State) ||
                !IsBoundedToken(media.StagingToken) || !IsBoundedToken(media.FinalToken) ||
                !mediaIds.Add(media.MediaId))
            {
                throw new InvalidOperationException("A recovery media record is invalid.");
            }
        }
    }

    private void ValidateEntryTransition(RecoveryJournalEntry current, RecoveryJournalEntry next)
    {
        EnsureOwnershipTransition(current.OwnershipState, next.OwnershipState);
        foreach (var currentMedia in current.Media)
        {
            if (!next.Media.Any(item => item.MediaId == currentMedia.MediaId))
                throw new InvalidOperationException("Recovery media records cannot be removed.");
        }
        foreach (var nextMedia in next.Media)
        {
            var currentMedia = current.Media.SingleOrDefault(item => item.MediaId == nextMedia.MediaId);
            if (currentMedia is not null)
            {
                if (currentMedia.OutputEpoch != nextMedia.OutputEpoch ||
                    !string.Equals(currentMedia.StagingToken, nextMedia.StagingToken, StringComparison.Ordinal) ||
                    !string.Equals(currentMedia.FinalToken, nextMedia.FinalToken, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Recovery media identity is immutable.");
                }
                EnsureMediaTransition(currentMedia.State, nextMedia.State);
            }
        }
    }

    private static void EnsureOwnershipTransition(
        RecoveryOwnershipState current,
        RecoveryOwnershipState next)
    {
        if (!Enum.IsDefined(next) || next < current)
            throw new InvalidOperationException("Recovery ownership state cannot regress.");
    }

    private static void EnsureMediaTransition(RecoveryMediaState current, RecoveryMediaState next)
    {
        if (!Enum.IsDefined(next) || current == next)
            return;

        var valid = current switch
        {
            RecoveryMediaState.Planned => true,
            RecoveryMediaState.Writing => next is RecoveryMediaState.NativeClosed or
                RecoveryMediaState.Finalized or RecoveryMediaState.Interrupted or
                RecoveryMediaState.Invalid or RecoveryMediaState.RecoveryRequired,
            RecoveryMediaState.NativeClosed => next is RecoveryMediaState.Finalized or
                RecoveryMediaState.Interrupted or RecoveryMediaState.Invalid or
                RecoveryMediaState.RecoveryRequired,
            RecoveryMediaState.Finalized => next is RecoveryMediaState.Indexed or
                RecoveryMediaState.Invalid or RecoveryMediaState.RecoveryRequired,
            RecoveryMediaState.RecoveryRequired => next is RecoveryMediaState.Finalized or
                RecoveryMediaState.Indexed or RecoveryMediaState.Interrupted or RecoveryMediaState.Invalid,
            _ => false,
        };
        if (!valid)
            throw new InvalidOperationException("Recovery media state transition is invalid.");
    }

    private bool IsBoundedToken(string? value) =>
        value is { Length: > 0 } && value.Length <= _limits.MaxTokenLength &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static RecoveryJournalEntry CloneEntry(RecoveryJournalEntry entry) => new(
        entry.Identity with { },
        entry.OwnershipState,
        entry.Media.Select(static media => media with { }).ToArray());

    private sealed record PersistedJournal(int Version, List<RecoveryJournalEntry?>? Entries);
}

/// <summary>
/// Same-volume write-and-replace storage. Path admission, reparse-point policy, and network-path
/// policy remain explicit Phase 0 decisions; this type is not registered in production.
/// </summary>
public sealed class FileRecoveryJournalStorage : IRecoveryJournalStorage
{
    private readonly string _path;
    private readonly int _maximumReadBytes;

    public FileRecoveryJournalStorage(string path, int maximumReadBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("The journal path must be absolute.", nameof(path));
        if (maximumReadBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumReadBytes));
        _path = Path.GetFullPath(path);
        _maximumReadBytes = maximumReadBytes;
    }

    public async ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
            return null;
        await using var stream = new FileStream(
            _path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > _maximumReadBytes)
            throw new RecoveryJournalCorruptedException();
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.Position != stream.Length)
            throw new RecoveryJournalCorruptedException();
        return bytes;
    }

    public async ValueTask WriteAtomicallyAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("The journal directory is unavailable.");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(
            directory,
            $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
            throw;
        }
    }
}
