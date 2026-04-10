using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Sessions;

public sealed class JsonSessionRepository : ISessionRepository, IDisposable
{
    private readonly string _indexPath;
    private readonly ILogger<JsonSessionRepository> _logger;
    private readonly List<GameSession> _sessions = [];
    private readonly SemaphoreSlim _lock = new(1, 1);

    private const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private sealed record SessionIndex(int SchemaVersion, IReadOnlyList<GameSession> Sessions);

    public JsonSessionRepository(string storageBasePath, ILogger<JsonSessionRepository> logger)
    {
        _indexPath = Path.Combine(storageBasePath, "sessions-index.json");
        _logger = logger;

        try
        {
            Directory.CreateDirectory(storageBasePath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create sessions storage directory: {Path}", storageBasePath);
        }

        LoadIndex();
    }

    public async Task<GameSession?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return _sessions.FirstOrDefault(s => s.Id == id);
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
                .ToList();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync(GameSession session, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var idx = _sessions.FindIndex(s => s.Id == session.Id);
            if (idx >= 0)
                _sessions[idx] = session;
            else
                _sessions.Add(session);

            await SaveIndexAsync(ct);
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
            var removed = _sessions.RemoveAll(s => s.Id == id);
            if (removed > 0)
                await SaveIndexAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void LoadIndex()
    {
        try
        {
            if (!File.Exists(_indexPath))
                return;

            var json = File.ReadAllText(_indexPath);
            var index = JsonSerializer.Deserialize<SessionIndex>(json, JsonOptions);

            if (index is null)
                return;

            if (index.SchemaVersion > CurrentSchemaVersion)
            {
                _logger.LogWarning(
                    "Sessions index schema version {Version} is newer than supported {Supported}; loading anyway",
                    index.SchemaVersion, CurrentSchemaVersion);
            }

            _sessions.AddRange(index.Sessions);
            _logger.LogInformation("Loaded {Count} sessions from index", _sessions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load sessions index, starting fresh");
        }
    }

    private async Task SaveIndexAsync(CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(_indexPath)!;
        var tmp = Path.Combine(dir, $"sessions-index.{Guid.NewGuid():N}.tmp");

        try
        {
            var index = new SessionIndex(CurrentSchemaVersion, [.. _sessions]);
            var json = JsonSerializer.Serialize(index, JsonOptions);
            await File.WriteAllTextAsync(tmp, json, ct);
            File.Move(tmp, _indexPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save sessions index");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort */ }
        }
    }

    public void Dispose() => _lock.Dispose();
}
