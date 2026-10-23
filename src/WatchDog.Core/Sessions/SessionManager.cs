using Microsoft.Extensions.Logging;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Storage;

namespace WatchDog.Core.Sessions;

public sealed class SessionManager
{
    private readonly ISessionRepository _repository;
    private readonly IClipStorage _clipStorage;
    private readonly IClipLibraryHealth? _libraryHealth;
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
        ILogger<SessionManager> logger,
        IClipLibraryHealth? libraryHealth = null)
    {
        _repository = repository;
        _clipStorage = clipStorage;
        _libraryHealth = libraryHealth;
        _eventBus = eventBus;
        _logger = logger;
    }

    // While the clip library is unavailable a session's clips are unknown, not absent,
    // so the session must never be discarded as empty.
    private bool MayHaveClips(Guid sessionId) =>
        _libraryHealth?.LibraryStatus.IsAvailable == false ||
        _clipStorage.GetClipsBySession(sessionId).Count > 0;

    public async Task StartSessionAsync(
        GameInfo game,
        CancellationToken ct = default,
        Guid? sessionId = null)
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
                Id = sessionId ?? Guid.NewGuid(),
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

    public async Task StartDesktopSessionAsync(
        CancellationToken ct = default,
        Guid? sessionId = null)
    {
        SessionEndedEvent? endedEvent = null;
        GameSession? created = null;

        await _lock.WaitAsync(ct);
        try
        {
            // If there's already a desktop session for today, reuse it
            if (_currentSession is not null
                && _currentSession.GameExecutableName == DesktopExecutableName
                && _currentSession.StartedAt.LocalDateTime.Date == DateTimeOffset.Now.LocalDateTime.Date
                && (sessionId is null || sessionId == _currentSession.Id))
            {
                return;
            }

            if (_currentSession is not null)
                endedEvent = await EndCurrentSessionInternalAsync(ct);

            var session = new GameSession
            {
                Id = sessionId ?? Guid.NewGuid(),
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
            if (_currentSession.RecordingPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) return;

            var updated = _currentSession with
            {
                RecordingPaths = [.. _currentSession.RecordingPaths, path]
            };
            await _repository.SaveAsync(updated, ct);
            _currentSession = updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task UpsertMediaSegmentAsync(
        Guid sessionId,
        SessionMediaSegment segment,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segment);

        await _lock.WaitAsync(ct);
        try
        {
            if (_currentSession?.Id != sessionId)
                throw new InvalidOperationException("Media attribution does not match the active session.");
            SessionMediaSegmentValidation.ValidateSegment(sessionId, segment);

            var existing = _currentSession.MediaSegments.FirstOrDefault(item => item.MediaId == segment.MediaId);
            if (_currentSession.MediaSegments.Any(item =>
                    item.MediaId != segment.MediaId &&
                    string.Equals(item.FilePath, segment.FilePath, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException("A media path cannot be attributed to more than one media ID.");
            }
            if (existing is not null)
            {
                if (existing == segment)
                    return;
                RequireCompatibleMediaUpdate(existing, segment);
            }

            var media = existing is null
                ? [.. _currentSession.MediaSegments, segment]
                : _currentSession.MediaSegments
                    .Select(item => item.MediaId == segment.MediaId ? segment : item)
                    .ToArray();
            SessionMediaSegmentValidation.ValidateGraph(sessionId, media);
            var recordingPaths = _currentSession.RecordingPaths;
            if (segment is { Role: SessionMediaRole.Screen, State: SessionMediaState.Finalized } &&
                !recordingPaths.Contains(segment.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                recordingPaths = [.. recordingPaths, segment.FilePath];
            }

            var updated = _currentSession with
            {
                MediaSegments = media,
                RecordingPaths = recordingPaths,
            };
            await _repository.SaveAsync(updated, ct);
            _currentSession = updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task RemovePlannedMediaSegmentAsync(
        Guid sessionId,
        Guid mediaId,
        CancellationToken ct = default)
    {
        if (mediaId == Guid.Empty)
            throw new ArgumentException("A planned media ID is required.", nameof(mediaId));

        await _lock.WaitAsync(ct);
        try
        {
            if (_currentSession?.Id != sessionId)
                throw new InvalidOperationException("Media compensation does not match the active session.");

            var existing = _currentSession.MediaSegments.SingleOrDefault(item => item.MediaId == mediaId);
            if (existing is null)
                return;
            if (existing.State != SessionMediaState.Planned)
                throw new InvalidOperationException("Only never-activated planned media may be removed.");

            var updated = _currentSession with
            {
                MediaSegments = _currentSession.MediaSegments
                    .Where(item => item.MediaId != mediaId)
                    .ToArray(),
            };
            await _repository.SaveAsync(updated, ct);
            _currentSession = updated;
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

            var matchesSnapshot = matches
                .Select(match => match with { ClipIds = match.ClipIds.ToArray() })
                .ToArray();
            var updated = _currentSession with { Matches = matchesSnapshot };
            await _repository.SaveAsync(updated, ct);
            _currentSession = updated;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Recovers any sessions that were left InProgress (e.g., due to a crash).
    /// Non-empty sessions are closed as crashed; a new capture lifecycle always receives a new ID.
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

                // A lifecycle generation cannot truthfully adopt this prior process instance.
                // Preserve non-empty media as crashed and let capture create a fresh session.
                var hasClips = MayHaveClips(session.Id);
                var hasRecordings = session.RecordingPaths.Count > 0 || session.MediaSegments.Count > 0;
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
        var hasClips = MayHaveClips(sessionId);
        var hasRecordings = _currentSession.RecordingPaths.Count > 0 || _currentSession.MediaSegments.Count > 0;
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

    private static void RequireCompatibleMediaUpdate(
        SessionMediaSegment existing,
        SessionMediaSegment replacement)
    {
        var producerFinalization =
            existing is { State: SessionMediaState.Planned, ProducerMetadata: null } &&
            replacement is { State: SessionMediaState.Finalized, ProducerMetadata: not null };
        var finalizedProducerIdentity =
            existing.State == SessionMediaState.Finalized &&
            (existing.ProducerMetadata is not null || replacement.ProducerMetadata is not null);

        if (existing.SessionId != replacement.SessionId ||
            existing.CaptureRequestId != replacement.CaptureRequestId ||
            existing.CaptureGeneration != replacement.CaptureGeneration ||
            existing.Role != replacement.Role ||
            !string.Equals(existing.FilePath, replacement.FilePath, StringComparison.OrdinalIgnoreCase) ||
            existing.Sequence != replacement.Sequence ||
            existing.StartOffset != replacement.StartOffset ||
            (existing.TimingProvenance != replacement.TimingProvenance && !producerFinalization) ||
            finalizedProducerIdentity ||
            existing.State == SessionMediaState.Finalized && replacement.State != SessionMediaState.Finalized)
        {
            throw new InvalidOperationException("A media ID cannot be reassigned or moved backward in its lifecycle.");
        }
    }
}
