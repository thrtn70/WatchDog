using Microsoft.Extensions.Logging;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;

namespace WatchDog.Core.Sessions;

public sealed class SessionManager
{
    private readonly ISessionRepository _repository;
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
        IEventBus eventBus,
        ILogger<SessionManager> logger)
    {
        _repository = repository;
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
    /// Must be called on startup before normal operation begins.
    /// </summary>
    public async Task RecoverOrphanedSessionsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var recent = await _repository.GetRecentAsync(50, ct);

            foreach (var session in recent)
            {
                if (session.Status != SessionStatus.InProgress) continue;

                var recovered = session with
                {
                    Status = SessionStatus.Crashed,
                    EndedAt = session.StartedAt,
                };
                await _repository.SaveAsync(recovered, ct);
                _logger.LogWarning("Recovered orphaned session: {Game} ({Id})", session.GameName, session.Id);
            }
        }
        finally
        {
            _lock.Release();
        }
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
    /// Returns the event to publish after the lock is released.
    /// </summary>
    private async Task<SessionEndedEvent?> EndCurrentSessionInternalAsync(CancellationToken ct)
    {
        if (_currentSession is null) return null;

        var duration = DateTimeOffset.UtcNow - _currentSession.StartedAt;
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
}
