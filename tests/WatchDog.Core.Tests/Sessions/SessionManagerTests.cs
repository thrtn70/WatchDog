using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Sessions;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Sessions;

public sealed class SessionManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryEventBus _eventBus;
    private readonly JsonSessionRepository _repo;
    private readonly NullClipStorage _clipStorage;
    private readonly SessionManager _manager;

    public SessionManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WD_SM_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _eventBus = new InMemoryEventBus();
        _repo = new JsonSessionRepository(_tempDir, NullLogger<JsonSessionRepository>.Instance);
        _clipStorage = new NullClipStorage();
        _manager = new SessionManager(_repo, _clipStorage, _eventBus, NullLogger<SessionManager>.Instance);
    }

    public void Dispose()
    {
        _repo.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private static GameInfo MakeGame(string name = "CS2", string exe = "cs2.exe") =>
        new() { ExecutableName = exe, DisplayName = name };

    [Fact]
    public async Task StartSessionAsync_CreatesSessionAndPublishesEvent()
    {
        SessionStartedEvent? published = null;
        _eventBus.Subscribe<SessionStartedEvent>(e => published = e);

        await _manager.StartSessionAsync(MakeGame());

        Assert.NotNull(_manager.CurrentSessionId);
        Assert.Equal("CS2", _manager.CurrentSession!.GameName);
        Assert.Equal(SessionStatus.InProgress, _manager.CurrentSession.Status);
        Assert.NotNull(published);
        Assert.Equal(_manager.CurrentSessionId, published.SessionId);
    }

    [Fact]
    public async Task EndSessionAsync_ClosesSessionAndPublishesEvent()
    {
        SessionEndedEvent? published = null;
        _eventBus.Subscribe<SessionEndedEvent>(e => published = e);

        var game = MakeGame();
        await _manager.StartSessionAsync(game);
        var sessionId = _manager.CurrentSessionId;

        // Add a recording path so the session has content; otherwise the
        // SessionManager discards empty sessions on End instead of saving them.
        await _manager.AddRecordingPathAsync("/path/test.mp4");

        await _manager.EndSessionAsync(game);

        Assert.Null(_manager.CurrentSessionId);
        Assert.NotNull(published);
        Assert.Equal(sessionId, published.SessionId);

        // Verify persisted as Completed
        var saved = await _repo.GetByIdAsync(sessionId!.Value);
        Assert.Equal(SessionStatus.Completed, saved!.Status);
        Assert.NotNull(saved.EndedAt);
    }

    [Fact]
    public async Task EndSessionAsync_IgnoresWrongGame()
    {
        await _manager.StartSessionAsync(MakeGame("CS2", "cs2.exe"));
        var sessionId = _manager.CurrentSessionId;

        await _manager.EndSessionAsync(MakeGame("Valorant", "valorant.exe"));

        // Session still active — wrong game was "exiting"
        Assert.Equal(sessionId, _manager.CurrentSessionId);
    }

    [Fact]
    public async Task StartSessionAsync_EndsExistingSession()
    {
        await _manager.StartSessionAsync(MakeGame("CS2", "cs2.exe"));
        var firstId = _manager.CurrentSessionId;

        // Give the first session content so it's not discarded as empty when
        // the second StartSessionAsync ends it.
        await _manager.AddRecordingPathAsync("/path/cs2-session.mp4");

        await _manager.StartSessionAsync(MakeGame("Valorant", "valorant.exe"));

        Assert.NotEqual(firstId, _manager.CurrentSessionId);

        // First session should be completed
        var first = await _repo.GetByIdAsync(firstId!.Value);
        Assert.Equal(SessionStatus.Completed, first!.Status);
    }

    [Fact]
    public async Task StartDesktopSessionAsync_CreatesDesktopSession()
    {
        await _manager.StartDesktopSessionAsync();

        Assert.NotNull(_manager.CurrentSessionId);
        Assert.Equal("Desktop", _manager.CurrentSession!.GameName);
        Assert.Equal("desktop", _manager.CurrentSession.GameExecutableName);
    }

    [Fact]
    public async Task StartDesktopSessionAsync_ReusesTodaySession()
    {
        await _manager.StartDesktopSessionAsync();
        var firstId = _manager.CurrentSessionId;

        // Calling again today should reuse the same session
        await _manager.StartDesktopSessionAsync();

        Assert.Equal(firstId, _manager.CurrentSessionId);
    }

    [Fact]
    public async Task AddRecordingPathAsync_AppendsPath()
    {
        await _manager.StartSessionAsync(MakeGame());

        await _manager.AddRecordingPathAsync("/path/recording1.mp4");
        await _manager.AddRecordingPathAsync("/path/recording2.mp4");

        Assert.Equal(2, _manager.CurrentSession!.RecordingPaths.Count);

        // Verify persisted
        var saved = await _repo.GetByIdAsync(_manager.CurrentSessionId!.Value);
        Assert.Equal(2, saved!.RecordingPaths.Count);
    }

    [Fact]
    public async Task RecoverOrphanedSessionsAsync_MarksAsCrashed()
    {
        // Simulate a session that was left InProgress (crash). It needs a
        // recording so recovery doesn't treat it as empty and discard it.
        var orphan = new GameSession
        {
            Id = Guid.NewGuid(),
            GameName = "CS2",
            GameExecutableName = "cs2.exe",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            Status = SessionStatus.InProgress,
            RecordingPaths = ["/path/orphan-recording.mp4"],
        };
        await _repo.SaveAsync(orphan);

        await _manager.RecoverOrphanedSessionsAsync();

        var recovered = await _repo.GetByIdAsync(orphan.Id);
        Assert.Equal(SessionStatus.Crashed, recovered!.Status);
    }

    [Fact]
    public async Task RecoverOrphanedSessionsAsync_SkipsCompletedSessions()
    {
        var completed = new GameSession
        {
            Id = Guid.NewGuid(),
            GameName = "CS2",
            GameExecutableName = "cs2.exe",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            EndedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Status = SessionStatus.Completed,
        };
        await _repo.SaveAsync(completed);

        await _manager.RecoverOrphanedSessionsAsync();

        var saved = await _repo.GetByIdAsync(completed.Id);
        Assert.Equal(SessionStatus.Completed, saved!.Status);
    }

    [Fact]
    public async Task UpdateMatchesAsync_PersistsMatches()
    {
        await _manager.StartSessionAsync(MakeGame());

        var matches = new List<SessionMatch>
        {
            new() { MatchNumber = 1, StartedAt = DateTimeOffset.UtcNow },
        };
        await _manager.UpdateMatchesAsync(matches);

        var saved = await _repo.GetByIdAsync(_manager.CurrentSessionId!.Value);
        Assert.Single(saved!.Matches);
        Assert.Equal(1, saved.Matches[0].MatchNumber);
    }
}
