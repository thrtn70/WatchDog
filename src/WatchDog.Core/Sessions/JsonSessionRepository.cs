using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Sessions;

public sealed class JsonSessionRepository : ISessionRepository, IDisposable
{
    private readonly string _indexPath;
    private readonly string _backupPath;
    private readonly ILogger<JsonSessionRepository> _logger;
    private readonly List<GameSession> _sessions = [];
    private readonly HashSet<Guid> _deletedSessionIds = [];
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _primaryIndexIsValid;

    private const int CurrentSchemaVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record SessionIndex(
        int SchemaVersion,
        IReadOnlyList<GameSession> Sessions,
        IReadOnlyList<Guid>? DeletedSessionIds = null);

    public JsonSessionRepository(
        string storageBasePath,
        ILogger<JsonSessionRepository> logger,
        IReadOnlyList<string>? legacyReadOnlyPaths = null)
    {
        _indexPath = Path.Combine(storageBasePath, "sessions-index.json");
        _backupPath = _indexPath + ".bak";
        _logger = logger;

        try
        {
            Directory.CreateDirectory(storageBasePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create sessions storage directory: {Path}", storageBasePath);
        }

        LoadIndexes(legacyReadOnlyPaths);
    }

    public async Task<GameSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var session = _sessions.FirstOrDefault(s => s.Id == id);
            return session is null ? null : CloneSession(session);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<GameSession>> GetByGameAsync(string gameName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _sessions
                .Where(s => string.Equals(s.GameName, gameName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.StartedAt)
                .Select(CloneSession)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<GameSession>> GetRecentAsync(int count, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _sessions
                .OrderByDescending(s => s.StartedAt)
                .Take(count)
                .Select(CloneSession)
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(GameSession session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        await _lock.WaitAsync(ct);
        try
        {
            var storedSession = CloneSession(session);
            ValidateMediaGraphForSave(storedSession);
            var previousSessions = _sessions.ToList();
            var previousDeletedSessionIds = _deletedSessionIds.ToHashSet();
            var idx = _sessions.FindIndex(s => s.Id == storedSession.Id);
            if (idx >= 0)
                ValidateProducerUpdateForSave(_sessions[idx], storedSession);
            if (idx >= 0)
                _sessions[idx] = storedSession;
            else
                _sessions.Add(storedSession);
            _deletedSessionIds.Remove(storedSession.Id);

            try
            {
                await SaveIndexAsync(ct);
            }
            catch
            {
                RestoreState(previousSessions, previousDeletedSessionIds);
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var previousSessions = _sessions.ToList();
            var previousDeletedSessionIds = _deletedSessionIds.ToHashSet();
            var removed = _sessions.RemoveAll(s => s.Id == id);
            if (removed > 0)
            {
                _deletedSessionIds.Add(id);
                try
                {
                    await SaveIndexAsync(ct);
                }
                catch
                {
                    RestoreState(previousSessions, previousDeletedSessionIds);
                    throw;
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LoadIndexes(IReadOnlyList<string>? legacyReadOnlyPaths)
    {
        var loadedIds = new HashSet<Guid>();
        var primary = LoadPrimaryIndex();
        AddIndex(primary, loadedIds);

        if (legacyReadOnlyPaths is null)
        {
            _logger.LogInformation("Loaded {Count} sessions from primary index", _sessions.Count);
            return;
        }

        var primaryDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(Path.GetDirectoryName(_indexPath)!));
        foreach (var legacyStoragePath in legacyReadOnlyPaths)
        {
            if (string.IsNullOrWhiteSpace(legacyStoragePath))
                continue;

            try
            {
                var legacyDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(legacyStoragePath));
                if (string.Equals(legacyDirectory, primaryDirectory, StringComparison.OrdinalIgnoreCase))
                    continue;

                var legacyIndexPath = Path.Combine(legacyDirectory, "sessions-index.json");
                var legacy = ReadIndex(legacyIndexPath);
                AddIndex(legacy, loadedIds);
            }
            catch (NotSupportedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to load read-only legacy sessions index from {Path}",
                    legacyStoragePath);
            }
        }

        _logger.LogInformation("Loaded {Count} sessions from primary and legacy indexes", _sessions.Count);
    }

    private SessionIndex? LoadPrimaryIndex()
    {
        var primaryExists = File.Exists(_indexPath);
        var backupExists = File.Exists(_backupPath);
        if (!primaryExists && !backupExists)
            return null;

        Exception? primaryFailure = null;
        if (primaryExists)
        {
            try
            {
                var index = ReadIndex(_indexPath)
                    ?? throw new FileNotFoundException("Primary sessions index disappeared while loading.", _indexPath);
                _primaryIndexIsValid = true;
                return index;
            }
            catch (NotSupportedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                primaryFailure = ex;
                _logger.LogWarning(ex, "Failed to load primary sessions index; attempting backup recovery");
            }
        }

        if (!backupExists)
            throw CreateUnrecoverableIndexException(primaryFailure, null);

        SessionIndex backup;
        try
        {
            backup = ReadIndex(_backupPath)
                ?? throw new FileNotFoundException("Sessions index backup disappeared while loading.", _backupPath);
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception backupFailure)
        {
            throw CreateUnrecoverableIndexException(primaryFailure, backupFailure);
        }

        try
        {
            RecoverPrimaryFromBackup();
            _primaryIndexIsValid = true;
            _logger.LogWarning("Recovered primary sessions index from previous-good backup");
            return backup;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to recover primary sessions index from backup");
            throw new IOException("Failed to recover the primary sessions index from its backup.", ex);
        }
    }

    private InvalidDataException CreateUnrecoverableIndexException(
        Exception? primaryFailure,
        Exception? backupFailure)
    {
        _primaryIndexIsValid = false;
        var failures = new[] { primaryFailure, backupFailure }
            .Where(failure => failure is not null)
            .Cast<Exception>()
            .ToArray();
        var inner = failures.Length switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures),
        };

        return new InvalidDataException(
            $"The existing sessions index could not be recovered. The primary '{_indexPath}' and backup " +
            $"'{_backupPath}' were left unchanged. Restore or remove the damaged index files before retrying.",
            inner);
    }

    private void AddIndex(SessionIndex? index, HashSet<Guid> loadedIds)
    {
        if (index is null)
            return;

        foreach (var session in index.Sessions)
        {
            if (!_deletedSessionIds.Contains(session.Id) && loadedIds.Add(session.Id))
                _sessions.Add(CloneSession(session));
        }

        foreach (var deletedId in index.DeletedSessionIds ?? [])
        {
            if (!loadedIds.Contains(deletedId))
                _deletedSessionIds.Add(deletedId);
        }
    }

    private static SessionIndex? ReadIndex(string path)
    {
        if (!File.Exists(path))
            return null;

        var json = File.ReadAllText(path);
        RejectUnsupportedSchemaVersions(json, path);
        var index = JsonSerializer.Deserialize<SessionIndex>(json, JsonOptions)
            ?? throw new InvalidDataException($"Sessions index at '{path}' is empty.");

        if (index.SchemaVersion > CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Sessions index schema version {index.SchemaVersion} is newer than supported version {CurrentSchemaVersion}.");
        }

        if (index.Sessions is null)
            throw new InvalidDataException($"Sessions index at '{path}' has no sessions collection.");

        foreach (var session in index.Sessions)
        {
            if (session is null || session.Matches is null || session.RecordingPaths is null ||
                session.MediaSegments is null ||
                string.IsNullOrWhiteSpace(session.GameName) || string.IsNullOrWhiteSpace(session.GameExecutableName) ||
                session.RecordingPaths.Any(string.IsNullOrWhiteSpace) ||
                session.Matches.Any(match => match is null || match.ClipIds is null || match.ClipIds.Any(string.IsNullOrWhiteSpace)))
                throw new InvalidDataException($"Sessions index at '{path}' has an invalid session graph.");

            ValidateMediaGraphFromIndex(path, session);
        }
        return index;
    }

    private static void RejectUnsupportedSchemaVersions(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return;

        if (root.TryGetProperty("schemaVersion", out var indexSchemaVersion))
        {
            RejectFutureSchemaVersion(
                indexSchemaVersion,
                CurrentSchemaVersion,
                "Sessions index",
                path);
        }

        if (!root.TryGetProperty("sessions", out var sessions) || sessions.ValueKind != JsonValueKind.Array)
            return;

        foreach (var session in sessions.EnumerateArray())
        {
            if (session.ValueKind != JsonValueKind.Object ||
                !session.TryGetProperty("mediaSegments", out var segments) ||
                segments.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var segment in segments.EnumerateArray())
            {
                if (segment.ValueKind != JsonValueKind.Object ||
                    !segment.TryGetProperty("producerMetadata", out var metadata) ||
                    metadata.ValueKind != JsonValueKind.Object ||
                    !metadata.TryGetProperty("schemaVersion", out var metadataSchemaVersion))
                {
                    continue;
                }

                RejectFutureSchemaVersion(
                    metadataSchemaVersion,
                    SessionMediaSegmentValidation.CurrentProducerMetadataSchemaVersion,
                    "Producer metadata",
                    path);
            }
        }
    }

    private static void RejectFutureSchemaVersion(
        JsonElement versionElement,
        int supportedVersion,
        string schemaName,
        string path)
    {
        if (versionElement.ValueKind != JsonValueKind.Number)
            return;

        double version;
        try
        {
            version = versionElement.GetDouble();
        }
        catch (FormatException)
        {
            return;
        }

        if (version > supportedVersion)
        {
            throw new NotSupportedException(
                $"{schemaName} schema version {versionElement.GetRawText()} at '{path}' is newer than supported " +
                $"version {supportedVersion}.");
        }
    }

    private void RecoverPrimaryFromBackup()
    {
        var dir = Path.GetDirectoryName(_indexPath)!;
        var tmp = Path.Combine(dir, $"sessions-index.{Guid.NewGuid():N}.tmp");

        try
        {
            File.Copy(_backupPath, tmp);
            File.Move(tmp, _indexPath, overwrite: true);
        }
        finally
        {
            TryDeleteTempFile(tmp);
        }
    }

    private async Task SaveIndexAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_indexPath)!;
        var tmp = Path.Combine(dir, $"sessions-index.{Guid.NewGuid():N}.tmp");

        try
        {
            var index = new SessionIndex(
                CurrentSchemaVersion,
                [.. _sessions],
                _deletedSessionIds.OrderBy(id => id).ToArray());
            await using (var stream = new FileStream(
                tmp,
                new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
                }))
            {
                await JsonSerializer.SerializeAsync(stream, index, JsonOptions, ct);
                await stream.FlushAsync(ct);
            }

            if (_primaryIndexIsValid && File.Exists(_indexPath))
                File.Replace(tmp, _indexPath, _backupPath, ignoreMetadataErrors: true);
            else
                File.Move(tmp, _indexPath, overwrite: true);

            _primaryIndexIsValid = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save sessions index");
            throw;
        }
        finally
        {
            TryDeleteTempFile(tmp);
        }
    }

    private void RestoreState(
        IReadOnlyList<GameSession> sessions,
        IReadOnlySet<Guid> deletedSessionIds)
    {
        _sessions.Clear();
        _sessions.AddRange(sessions);
        _deletedSessionIds.Clear();
        _deletedSessionIds.UnionWith(deletedSessionIds);
    }

    private static GameSession CloneSession(GameSession session) =>
        session with
        {
            Matches = session.Matches.Select(CloneMatch).ToArray(),
            RecordingPaths = session.RecordingPaths.ToArray(),
            MediaSegments = session.MediaSegments.ToArray(),
        };

    private static void ValidateMediaGraphForSave(GameSession session)
    {
        try
        {
            SessionMediaSegmentValidation.ValidateGraph(session.Id, session.MediaSegments);
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new ArgumentException("The session contains an invalid media segment graph.", nameof(session), ex);
        }
    }

    private static void ValidateMediaGraphFromIndex(string path, GameSession session)
    {
        try
        {
            SessionMediaSegmentValidation.ValidateGraph(session.Id, session.MediaSegments);
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            throw new InvalidDataException($"Sessions index at '{path}' has an invalid session graph.", ex);
        }
    }

    private static void ValidateProducerUpdateForSave(GameSession existing, GameSession replacement)
    {
        try
        {
            SessionMediaSegmentValidation.ValidateProducerUpdate(
                existing.MediaSegments,
                replacement.MediaSegments);
        }
        catch (InvalidOperationException ex)
        {
            throw new ArgumentException("A persisted producer receipt is immutable.", nameof(replacement), ex);
        }
    }

    private static SessionMatch CloneMatch(SessionMatch match) =>
        match with { ClipIds = match.ClipIds.ToArray() };

    private static void TryDeleteTempFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup must not hide the persistence result.
        }
    }

    public void Dispose() => _lock.Dispose();
}
