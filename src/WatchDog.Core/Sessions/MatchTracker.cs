using Microsoft.Extensions.Logging;
using WatchDog.Core.Events;
using WatchDog.Core.Highlights;

namespace WatchDog.Core.Sessions;

public sealed class MatchTracker : IDisposable
{
    private readonly SessionManager _sessionManager;
    private readonly IEventBus _eventBus;
    private readonly ILogger<MatchTracker> _logger;
    private readonly IDisposable _subscription;
    private readonly object _stateLock = new();

    private Guid? _trackedSessionId;
    private int _currentMatchNumber;
    private DateTimeOffset? _currentMatchStartedAt;

    public int? CurrentMatchNumber
    {
        get
        {
            lock (_stateLock)
                return _currentMatchStartedAt is not null ? _currentMatchNumber : null;
        }
    }

    public MatchTracker(
        SessionManager sessionManager,
        IEventBus eventBus,
        ILogger<MatchTracker> logger)
    {
        _sessionManager = sessionManager;
        _eventBus = eventBus;
        _logger = logger;

        _subscription = _eventBus.Subscribe<HighlightDetectedEvent>(OnHighlightDetected);
    }

    private void OnHighlightDetected(HighlightDetectedEvent e)
    {
        var sessionId = _sessionManager.CurrentSessionId;
        if (sessionId is null) return;

        lock (_stateLock)
        {
            if (_trackedSessionId != sessionId.Value)
            {
                _trackedSessionId = sessionId.Value;
                _currentMatchNumber = 0;
                _currentMatchStartedAt = null;
            }

            switch (e.Type)
            {
                case HighlightType.MatchStarted:
                    StartNewMatch(sessionId.Value, e);
                    break;
                case HighlightType.MatchWin:
                    EndCurrentMatch(sessionId.Value, MatchResult.Win, e);
                    break;
                case HighlightType.MatchLoss:
                    EndCurrentMatch(sessionId.Value, MatchResult.Loss, e);
                    break;
            }
        }
    }

    private void StartNewMatch(Guid sessionId, HighlightDetectedEvent e)
    {
        // If there's already an open match, close it as Unknown first
        if (_currentMatchStartedAt is not null)
            EndCurrentMatch(sessionId, MatchResult.Unknown, e);

        _currentMatchNumber++;
        _currentMatchStartedAt = e.Timestamp;

        _eventBus.Publish(new MatchStartedEvent(sessionId, _currentMatchNumber, e.Game));
        _logger.LogInformation("Match {Number} started in session {SessionId}", _currentMatchNumber, sessionId);

        // Snapshot values and persist asynchronously
        var matchNumber = _currentMatchNumber;
        var startedAt = e.Timestamp;
        _ = PersistMatchStartAsync(sessionId, matchNumber, startedAt)
            .ContinueWith(t => _logger.LogError(t.Exception, "PersistMatchStart faulted"),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    private void EndCurrentMatch(Guid sessionId, MatchResult result, HighlightDetectedEvent e)
    {
        if (_currentMatchStartedAt is null) return;

        var score = e.Description; // e.g., "Match won 13-7"
        var matchNumber = _currentMatchNumber;
        var startedAt = _currentMatchStartedAt.Value;

        _eventBus.Publish(new MatchEndedEvent(sessionId, matchNumber, result, score));
        _logger.LogInformation("Match {Number} ended: {Result} in session {SessionId}", matchNumber, result, sessionId);

        _currentMatchStartedAt = null;

        // Snapshot values and persist asynchronously
        _ = PersistMatchEndAsync(sessionId, matchNumber, startedAt, result, score)
            .ContinueWith(t => _logger.LogError(t.Exception, "PersistMatchEnd faulted"),
                TaskContinuationOptions.OnlyOnFaulted);
    }

    private async Task PersistMatchStartAsync(Guid sessionId, int matchNumber, DateTimeOffset startedAt)
    {
        try
        {
            var session = _sessionManager.CurrentSession;
            if (session is null || session.Id != sessionId) return;

            var matches = session.Matches.ToList();
            matches.Add(new SessionMatch
            {
                MatchNumber = matchNumber,
                StartedAt = startedAt,
            });

            await _sessionManager.UpdateMatchesAsync(matches);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist match start for session {SessionId}", sessionId);
        }
    }

    private async Task PersistMatchEndAsync(Guid sessionId, int matchNumber, DateTimeOffset startedAt, MatchResult result, string? score)
    {
        try
        {
            var session = _sessionManager.CurrentSession;
            if (session is null || session.Id != sessionId) return;

            var matches = session.Matches.ToList();
            var existingIdx = matches.FindIndex(m => m.MatchNumber == matchNumber);

            if (existingIdx >= 0)
            {
                matches[existingIdx] = matches[existingIdx] with
                {
                    EndedAt = DateTimeOffset.UtcNow,
                    Result = result,
                    Score = score,
                };
            }
            else
            {
                matches.Add(new SessionMatch
                {
                    MatchNumber = matchNumber,
                    StartedAt = startedAt,
                    EndedAt = DateTimeOffset.UtcNow,
                    Result = result,
                    Score = score,
                });
            }

            await _sessionManager.UpdateMatchesAsync(matches);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist match end for session {SessionId}", sessionId);
        }
    }

    public void Reset()
    {
        lock (_stateLock)
        {
            _trackedSessionId = null;
            _currentMatchNumber = 0;
            _currentMatchStartedAt = null;
        }
    }

    public void Dispose() => _subscription.Dispose();
}
