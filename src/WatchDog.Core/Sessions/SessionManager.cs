using System.Diagnostics;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Storage;

namespace WatchDog.Core.Sessions;

public sealed class SessionManager : IDisposable
{
    private readonly ISessionRepository _repository;
    private readonly IClipStorage _clipStorage;
    private readonly IEventBus _eventBus;
    private readonly ILogger<SessionManager> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private GameSession? _currentSession;

    public Guid? CurrentSessionId => _currentSession?.Id;
    public GameSession? CurrentSession => _currentSession;

    private const string DesktopGameName = "Desktop";
    private const string DesktopExecutableName = "desktop";

    public SessionManager(
        ISessionRepository repository,
        IClipStorage clipStorage,
        IEventBus eventBus,
        ILogger<SessionManager> logger)
    {
        _repository = repository;
        _clipStorage = clipStorage;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task StartSessionAsync(GameInfo game, CancellationToken ct = default)
    {
        // Collect events to publish after releasing the lock
        SessionEndedEvent? endedEvent = null;
        GameSession? created = null;

        await _lock.WaitAsync(ct);
        try
        {
            if (_currentSession is not null)
                endedEvent = await EndCurrentSessionInternalAsync(ct);

            var session = new GameSession
            {
                Id = Guid.NewGuid(),
                GameName = game.DisplayName,
                GameExecutableName = game.ExecutableName,
                StartedAt = DateTimeOffset.UtcNow,
            };

            await _repository.SaveAsync(session, ct);
            _currentSession = session;
            created = session;
        }
        finally
        {
            _lock.Release();
        }

        // Publish events outside the lock to prevent deadlock via re-entrant subscribers
        if (endedEvent is not null)
            _eventBus.Publish(endedEvent);
        _eventBus.Publish(new SessionStartedEvent(created!.Id, game));
        _logger.LogInformation("Session started: {Game} ({Id})", game.DisplayName, created.Id);
    }

    public async Task StartDesktopSessionAsync(CancellationToken ct = default)
    {
        SessionEndedEvent? endedEvent = null;
        GameSession? created = null;

        await _lock.WaitAsync(ct);
        try
        {
            // If there's already a desktop session for today, reuse it
            if (_currentSession is not null
                && _currentSession.GameExecutableName == DesktopExecutableName
                && _currentSession.StartedAt.LocalDateTime.Date == DateTimeOffset.Now.LocalDateTime.Date)
            {
                return;
            }

            if (_currentSession is not null)
                endedEvent = await EndCurrentSessionInternalAsync(ct);

            var session = new GameSession
            {
                Id = Guid.NewGuid(),
                GameName = DesktopGameName,
                GameExecutableName = DesktopExecutableName,
                StartedAt = DateTimeOffset.UtcNow,
            };

            await _repository.SaveAsync(session, ct);
            _currentSession = session;
            created = session;
        }
        finally
        {
            _lock.Release();
        }

        if (created is not null)
        {
            if (endedEvent is not null)
                _eventBus.Publish(endedEvent);

            var desktopGameInfo = new GameInfo
            {
                ExecutableName = DesktopExecutableName,
                DisplayName = DesktopGameName,
            };
            _eventBus.Publish(new SessionStartedEvent(created.Id, desktopGameInfo));
            _logger.LogInformation("Desktop session started ({Id})", created.Id);
        }
    }

    public async Task EndSessionAsync(GameInfo game, CancellationToken ct = default)
    {
        SessionEndedEvent? endedEvent = null;

        await _lock.WaitAsync(ct);
        try
        {
            if (_currentSession is null) return;

            if (!string.Equals(_currentSession.GameExecutableName, game.ExecutableName, StringComparison.OrdinalIgnoreCase))
                return;

            endedEvent = await EndCurrentSessionInternalAsync(ct);
        }
        finally
        {
            _lock.Release();
        }

        if (endedEvent is not null)
            _eventBus.Publish(endedEvent);
    }

    public async Task AddRecordingPathAsync(string path, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_currentSession is null) return;

            _currentSession = _currentSession with
            {
                RecordingPaths = [.. _currentSession.RecordingPaths, path]
            };
            await _repository.SaveAsync(_currentSession, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpdateMatchesAsync(IReadOnlyList<SessionMatch> matches, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_currentSession is null) return;

            _currentSession = _currentSession with { Matches = matches };
            await _repository.SaveAsync(_currentSession, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Recovers any sessions that were left InProgress (e.g., due to a crash).
    /// If the session's game is still running, resumes it instead of marking it crashed.
    /// Must be called on startup before normal operation begins.
    /// </summary>
    public async Task RecoverOrphanedSessionsAsync(CancellationToken ct = default)
    {
        SessionStartedEvent? resumedEvent = null;

        await _lock.WaitAsync(ct);
        try
        {
            var recent = await _repository.GetRecentAsync(50, ct);
            var runningExeNames = GetRunningExecutableNames();

            foreach (var session in recent)
            {
                if (session.Status != SessionStatus.InProgress) continue;

                // If the game is still running and we don't already have an active session,
                // resume this session instead of marking it crashed
                if (_currentSession is null && runningExeNames.Contains(session.GameExecutableName))
                {
                    _currentSession = session;
                    resumedEvent = new SessionStartedEvent(session.Id, new GameInfo
                    {
                        ExecutableName = session.GameExecutableName,
                        DisplayName = session.GameName,
                    });
                    _logger.LogInformation("Resumed orphaned session: {Game} ({Id}) — game still running",
                        session.GameName, session.Id);
                    continue;
                }

                // Game is no longer running — check if the session has any content
                var hasClips = _clipStorage.GetClipsBySession(session.Id).Count > 0;
                var hasRecordings = session.RecordingPaths.Count > 0;
                var hasMatches = session.Matches.Count > 0;

                if (!hasClips && !hasRecordings && !hasMatches)
                {
                    // Empty orphaned session — discard
                    await _repository.DeleteAsync(session.Id, ct);
                    _logger.LogInformation("Discarded empty orphaned session: {Game} ({Id})",
                        session.GameName, session.Id);
                }
                else
                {
                    var recovered = session with
                    {
                        Status = SessionStatus.Crashed,
                        EndedAt = session.StartedAt,
                    };
                    await _repository.SaveAsync(recovered, ct);
                    _logger.LogWarning("Recovered orphaned session: {Game} ({Id})", session.GameName, session.Id);
                }
            }
        }
        finally
        {
            _lock.Release();
        }

        // Publish outside the lock to prevent deadlock via re-entrant subscribers
        if (resumedEvent is not null)
            _eventBus.Publish(resumedEvent);
    }

    /// <summary>Returns normalized executable names of all currently running processes.</summary>
    private static HashSet<string> GetRunningExecutableNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[]? processes = null;
        try
        {
            processes = Process.GetProcesses();
            foreach (var proc in processes)
            {
                try
                {
                    names.Add(proc.ProcessName + ".exe");
                }
                catch
                {
                    // Access denied for system processes — skip
                }
            }
        }
        catch
        {
            // Process enumeration failed — no resume possible
        }
        finally
        {
            if (processes is not null)
                foreach (var p in processes)
                    p.Dispose();
        }
        return names;
    }

    /// <summary>
    /// Checks if the current desktop session has crossed a day boundary and rolls it over.
    /// Call periodically (e.g., every minute) from a timer.
    /// </summary>
    public async Task CheckDesktopSessionBoundaryAsync(CancellationToken ct = default)
    {
        SessionEndedEvent? endedEvent = null;
        GameSession? created = null;

        await _lock.WaitAsync(ct);
        try
        {
            if (_currentSession is null) return;
            if (_currentSession.GameExecutableName != DesktopExecutableName) return;

            if (_currentSession.StartedAt.LocalDateTime.Date != DateTimeOffset.Now.LocalDateTime.Date)
            {
                endedEvent = await EndCurrentSessionInternalAsync(ct);

                var session = new GameSession
                {
                    Id = Guid.NewGuid(),
                    GameName = DesktopGameName,
                    GameExecutableName = DesktopExecutableName,
                    StartedAt = DateTimeOffset.UtcNow,
                };

                await _repository.SaveAsync(session, ct);
                _currentSession = session;
                created = session;
            }
        }
        finally
        {
            _lock.Release();
        }

        if (created is not null)
        {
            if (endedEvent is not null)
                _eventBus.Publish(endedEvent);

            var desktopGameInfo = new GameInfo
            {
                ExecutableName = DesktopExecutableName,
                DisplayName = DesktopGameName,
            };
            _eventBus.Publish(new SessionStartedEvent(created.Id, desktopGameInfo));
            _logger.LogInformation("Desktop session rolled to new day ({Id})", created.Id);
        }
    }

    /// <summary>
    /// Ends the current session. Must be called while holding _lock.
    /// Auto-deletes sessions that have no clips, no recording paths, and no match data.
    /// Returns the event to publish after the lock is released (null if session was discarded).
    /// </summary>
    private async Task<SessionEndedEvent?> EndCurrentSessionInternalAsync(CancellationToken ct)
    {
        if (_currentSession is null) return null;

        var duration = DateTimeOffset.UtcNow - _currentSession.StartedAt;
        var sessionId = _currentSession.Id;
        var hasClips = _clipStorage.GetClipsBySession(sessionId).Count > 0;
        var hasRecordings = _currentSession.RecordingPaths.Count > 0;
        var hasMatches = _currentSession.Matches.Count > 0;

        if (!hasClips && !hasRecordings && !hasMatches)
        {
            // Empty session — discard instead of persisting
            await _repository.DeleteAsync(sessionId, ct);
            _logger.LogInformation("Discarded empty session: {Game} ({Id}, {Duration:hh\\:mm\\:ss})",
                _currentSession.GameName, sessionId, duration);
            _currentSession = null;
            return null;
        }

        var ended = _currentSession with
        {
            Status = SessionStatus.Completed,
            EndedAt = DateTimeOffset.UtcNow,
        };

        await _repository.SaveAsync(ended, ct);

        var gameInfo = new GameInfo
        {
            ExecutableName = ended.GameExecutableName,
            DisplayName = ended.GameName,
        };

        _logger.LogInformation("Session ended: {Game} ({Id}, {Duration:hh\\:mm\\:ss})", ended.GameName, ended.Id, duration);
        _currentSession = null;

        return new SessionEndedEvent(ended.Id, gameInfo, duration);
    }

    public void Dispose()
    {
        _lock.Dispose();
    }
}
