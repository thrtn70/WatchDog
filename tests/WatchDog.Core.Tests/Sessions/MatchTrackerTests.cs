using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Events;
using WatchDog.Core.GameDetection;
using WatchDog.Core.Highlights;
using WatchDog.Core.Sessions;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Sessions;

public sealed class MatchTrackerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly InMemoryEventBus _eventBus;
    private readonly JsonSessionRepository _repo;
    private readonly NullClipStorage _clipStorage;
    private readonly SessionManager _sessionManager;
    private readonly MatchTracker _tracker;

    private static readonly GameInfo TestGame = new()
    {
        ExecutableName = "cs2.exe",
        DisplayName = "CS2",
    };

    public MatchTrackerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WD_MT_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _eventBus = new InMemoryEventBus();
        _repo = new JsonSessionRepository(_tempDir, NullLogger<JsonSessionRepository>.Instance);
        _clipStorage = new NullClipStorage();
        _sessionManager = new SessionManager(_repo, _clipStorage, _eventBus, NullLogger<SessionManager>.Instance);
        _tracker = new MatchTracker(_sessionManager, _eventBus, NullLogger<MatchTracker>.Instance);
    }

    public void Dispose()
    {
        _tracker.Dispose();
        _repo.Dispose();
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private void PublishHighlight(HighlightType type, string? description = null)
    {
        _eventBus.Publish(new HighlightDetectedEvent(type, TestGame, DateTimeOffset.UtcNow, description));
    }

    [Fact]
    public async Task MatchStarted_IncrementsMatchNumber()
    {
        await _sessionManager.StartSessionAsync(TestGame);

        Assert.Null(_tracker.CurrentMatchNumber);

        PublishHighlight(HighlightType.MatchStarted);
        Assert.Equal(1, _tracker.CurrentMatchNumber);

        // End first match, start second
        PublishHighlight(HighlightType.MatchWin, "13-7");
        PublishHighlight(HighlightType.MatchStarted);
        Assert.Equal(2, _tracker.CurrentMatchNumber);
    }

    [Fact]
    public async Task MatchStarted_PublishesMatchStartedEvent()
    {
        MatchStartedEvent? published = null;
        _eventBus.Subscribe<MatchStartedEvent>(e => published = e);

        await _sessionManager.StartSessionAsync(TestGame);
        PublishHighlight(HighlightType.MatchStarted);

        Assert.NotNull(published);
        Assert.Equal(1, published.MatchNumber);
        Assert.Equal(_sessionManager.CurrentSessionId, published.SessionId);
    }

    [Fact]
    public async Task MatchWin_PublishesMatchEndedEvent()
    {
        MatchEndedEvent? published = null;
        _eventBus.Subscribe<MatchEndedEvent>(e => published = e);

        await _sessionManager.StartSessionAsync(TestGame);
        PublishHighlight(HighlightType.MatchStarted);
        PublishHighlight(HighlightType.MatchWin, "13-7");

        Assert.NotNull(published);
        Assert.Equal(1, published.MatchNumber);
        Assert.Equal(MatchResult.Win, published.Result);
        Assert.Equal("13-7", published.Score);
    }

    [Fact]
    public async Task MatchLoss_PublishesMatchEndedEvent()
    {
        MatchEndedEvent? published = null;
        _eventBus.Subscribe<MatchEndedEvent>(e => published = e);

        await _sessionManager.StartSessionAsync(TestGame);
        PublishHighlight(HighlightType.MatchStarted);
        PublishHighlight(HighlightType.MatchLoss, "7-13");

        Assert.NotNull(published);
        Assert.Equal(MatchResult.Loss, published.Result);
    }

    [Fact]
    public void NoSession_IgnoresHighlights()
    {
        // No session started — highlights should be ignored
        PublishHighlight(HighlightType.MatchStarted);

        Assert.Null(_tracker.CurrentMatchNumber);
    }

    [Fact]
    public async Task ConsecutiveMatchStarted_ClosesOpenMatch()
    {
        var ended = new List<MatchEndedEvent>();
        _eventBus.Subscribe<MatchEndedEvent>(e => ended.Add(e));

        await _sessionManager.StartSessionAsync(TestGame);
        PublishHighlight(HighlightType.MatchStarted); // Match 1
        PublishHighlight(HighlightType.MatchStarted); // Match 2 (forces Match 1 to close as Unknown)

        Assert.Single(ended);
        Assert.Equal(MatchResult.Unknown, ended[0].Result);
        Assert.Equal(1, ended[0].MatchNumber);
        Assert.Equal(2, _tracker.CurrentMatchNumber);
    }

    [Fact]
    public async Task Reset_ClearsMatchState()
    {
        await _sessionManager.StartSessionAsync(TestGame);
        PublishHighlight(HighlightType.MatchStarted);
        Assert.Equal(1, _tracker.CurrentMatchNumber);

        _tracker.Reset();

        Assert.Null(_tracker.CurrentMatchNumber);
    }

    [Fact]
    public async Task KillHighlight_DoesNotAffectMatchState()
    {
        await _sessionManager.StartSessionAsync(TestGame);
        PublishHighlight(HighlightType.MatchStarted);

        // Kills and other highlights shouldn't affect match tracking
        PublishHighlight(HighlightType.Kill, "1 kill(s)");
        PublishHighlight(HighlightType.Death);
        PublishHighlight(HighlightType.RoundWin, "5-3");

        Assert.Equal(1, _tracker.CurrentMatchNumber);
    }

    [Fact]
    public async Task NewSession_ResetsMatchCounter()
    {
        await _sessionManager.StartSessionAsync(TestGame);
        PublishHighlight(HighlightType.MatchStarted);
        PublishHighlight(HighlightType.MatchWin, "13-7");
        await _sessionManager.EndSessionAsync(TestGame);

        await _sessionManager.StartSessionAsync(TestGame);
        PublishHighlight(HighlightType.MatchStarted);

        Assert.Equal(1, _tracker.CurrentMatchNumber);
    }
}
