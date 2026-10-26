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
        TestFs.DeleteDirectoryWithRetry(_tempDir);
    }

    private static GameInfo MakeGame(string name = "CS2", string exe = "cs2.exe") =>
        new() { ExecutableName = exe, DisplayName = name };

    private SessionManager CreateManager(ISessionRepository repository) =>
        new(repository, _clipStorage, _eventBus, NullLogger<SessionManager>.Instance);

    [Fact]
    public async Task ExplicitDesktopSessionIdentity_DoesNotReusePreviousSession()
    {
        await _manager.StartDesktopSessionAsync();
        var previous = _manager.CurrentSessionId;
        var requested = Guid.NewGuid();
        await _manager.StartDesktopSessionAsync(sessionId: requested);
        Assert.NotEqual(previous, _manager.CurrentSessionId);
        Assert.Equal(requested, _manager.CurrentSessionId);
    }

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
    public async Task UpsertMediaSegmentAsync_PersistsPlanThenFinalizationForExactSession()
    {
        await _manager.StartDesktopSessionAsync();
        var sessionId = _manager.CurrentSessionId!.Value;
        var planned = MakeMediaSegment(sessionId, SessionMediaRole.Camera, SessionMediaState.Planned);

        await _manager.UpsertMediaSegmentAsync(sessionId, planned);
        await _manager.UpsertMediaSegmentAsync(
            sessionId,
            planned with { State = SessionMediaState.Finalized, Duration = TimeSpan.FromSeconds(3) });

        var saved = await _repo.GetByIdAsync(sessionId);
        var segment = Assert.Single(saved!.MediaSegments);
        Assert.Equal(SessionMediaState.Finalized, segment.State);
        Assert.Equal(TimeSpan.FromSeconds(3), segment.Duration);
        Assert.Empty(saved.RecordingPaths);
    }

    [Fact]
    public async Task UpsertMediaSegmentAsync_RejectsStaleSessionAndConflictingIdentity()
    {
        await _manager.StartDesktopSessionAsync();
        var sessionId = _manager.CurrentSessionId!.Value;
        var planned = MakeMediaSegment(sessionId, SessionMediaRole.Camera, SessionMediaState.Planned);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.UpsertMediaSegmentAsync(Guid.NewGuid(), planned));
        await _manager.UpsertMediaSegmentAsync(sessionId, planned);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.UpsertMediaSegmentAsync(sessionId, planned with { FilePath = "other.mkv" }));
    }

    [Fact]
    public async Task FinalizedScreenMedia_UpdatesLegacyRecordingProjection()
    {
        await _manager.StartDesktopSessionAsync();
        var segment = MakeMediaSegment(
            _manager.CurrentSessionId!.Value,
            SessionMediaRole.Screen,
            SessionMediaState.Finalized);

        await _manager.UpsertMediaSegmentAsync(_manager.CurrentSessionId!.Value, segment);

        Assert.Equal(segment.FilePath, Assert.Single(_manager.CurrentSession!.RecordingPaths));
    }

    [Fact]
    public async Task UpsertProducerSegmentAsync_ExactDuplicateIsIdempotentAndFinalizedFieldsAreImmutable()
    {
        using var repository = new ControllableSessionRepository();
        var manager = CreateManager(repository);
        await manager.StartDesktopSessionAsync();
        var sessionId = manager.CurrentSessionId!.Value;
        var segment = MakeProducerSegment(sessionId);

        await manager.UpsertMediaSegmentAsync(sessionId, segment);
        var saveCount = repository.SaveCount;
        await manager.UpsertMediaSegmentAsync(sessionId, segment);

        Assert.Equal(saveCount, repository.SaveCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.UpsertMediaSegmentAsync(
                sessionId,
                segment with { Duration = segment.Duration!.Value + TimeSpan.FromMilliseconds(1) }));
        Assert.Equal(segment, Assert.Single(manager.CurrentSession!.MediaSegments));
    }

    [Fact]
    public async Task UpsertProducerSegmentAsync_FinalizesMatchingLegacyPlan()
    {
        await _manager.StartDesktopSessionAsync();
        var sessionId = _manager.CurrentSessionId!.Value;
        var planned = MakeMediaSegment(sessionId, SessionMediaRole.Screen, SessionMediaState.Planned);
        var finalized = planned with
        {
            State = SessionMediaState.Finalized,
            Duration = TimeSpan.FromSeconds(1),
            TimingProvenance = SessionTimingProvenance.RecorderStopwatchEstimate,
            ProducerMetadata = new SessionSegmentProducerMetadata(1, Guid.NewGuid(), Guid.NewGuid()),
        };

        await _manager.UpsertMediaSegmentAsync(sessionId, planned);
        await _manager.UpsertMediaSegmentAsync(sessionId, finalized);

        Assert.Equal(finalized, Assert.Single(_manager.CurrentSession!.MediaSegments));
    }

    [Fact]
    public async Task UpsertMediaSegmentAsync_LegacyFinalizedDurationUpdateRemainsCompatible()
    {
        await _manager.StartDesktopSessionAsync();
        var sessionId = _manager.CurrentSessionId!.Value;
        var legacy = MakeMediaSegment(sessionId, SessionMediaRole.Screen, SessionMediaState.Finalized);
        var corrected = legacy with { Duration = TimeSpan.FromSeconds(2) };

        await _manager.UpsertMediaSegmentAsync(sessionId, legacy);
        await _manager.UpsertMediaSegmentAsync(sessionId, corrected);

        Assert.Equal(corrected, Assert.Single(_manager.CurrentSession!.MediaSegments));
    }

    [Fact]
    public async Task UpsertProducerSegmentAsync_RejectsMalformedOrUnsupportedMetadata()
    {
        await _manager.StartDesktopSessionAsync();
        var sessionId = _manager.CurrentSessionId!.Value;
        var valid = MakeProducerSegment(sessionId);
        var metadata = valid.ProducerMetadata!;
        var malformed = new[]
        {
            valid with { ProducerMetadata = null },
            valid with { TimingProvenance = SessionTimingProvenance.BackendStopwatchEstimate },
            valid with { Role = SessionMediaRole.Camera },
            valid with { State = SessionMediaState.Planned, Duration = null },
            valid with { ProducerMetadata = metadata with { SchemaVersion = 0 } },
            valid with { ProducerMetadata = metadata with { OutputEpoch = Guid.Empty } },
            valid with { ProducerMetadata = metadata with { ProducerTimingEpoch = Guid.Empty } },
        };

        foreach (var segment in malformed)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                _manager.UpsertMediaSegmentAsync(sessionId, segment));
        }

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            _manager.UpsertMediaSegmentAsync(
                sessionId,
                valid with { ProducerMetadata = metadata with { SchemaVersion = 2 } }));
        Assert.Empty(_manager.CurrentSession!.MediaSegments);
    }

    [Fact]
    public async Task UpsertProducerSegmentAsync_RejectsConflictingOutputAndTimingIdentities()
    {
        await _manager.StartDesktopSessionAsync();
        var sessionId = _manager.CurrentSessionId!.Value;
        var first = MakeProducerSegment(sessionId);
        await _manager.UpsertMediaSegmentAsync(sessionId, first);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.UpsertMediaSegmentAsync(
                sessionId,
                MakeProducerSegment(
                    sessionId,
                    sequence: 1,
                    startOffset: TimeSpan.FromSeconds(2),
                    outputEpoch: first.ProducerMetadata!.OutputEpoch,
                    timingEpoch: first.ProducerMetadata.ProducerTimingEpoch,
                    captureRequestId: first.CaptureRequestId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.UpsertMediaSegmentAsync(
                sessionId,
                MakeProducerSegment(
                    sessionId,
                    sequence: first.Sequence,
                    startOffset: TimeSpan.FromSeconds(2),
                    timingEpoch: first.ProducerMetadata!.ProducerTimingEpoch,
                    captureRequestId: first.CaptureRequestId)));

        Assert.Equal(first, Assert.Single(_manager.CurrentSession!.MediaSegments));
    }

    [Fact]
    public async Task UpsertProducerSegmentAsync_OrdersOnlyWithinSameTimingEpoch()
    {
        await _manager.StartDesktopSessionAsync();
        var sessionId = _manager.CurrentSessionId!.Value;
        var requestId = Guid.NewGuid();
        var timingEpoch = Guid.NewGuid();
        var sequenceTwo = MakeProducerSegment(
            sessionId,
            sequence: 2,
            startOffset: TimeSpan.FromSeconds(4),
            timingEpoch: timingEpoch,
            captureRequestId: requestId);

        await _manager.UpsertMediaSegmentAsync(sessionId, sequenceTwo);
        await _manager.UpsertMediaSegmentAsync(
            sessionId,
            MakeProducerSegment(
                sessionId,
                sequence: 0,
                startOffset: TimeSpan.Zero,
                timingEpoch: timingEpoch,
                captureRequestId: requestId));
        await _manager.UpsertMediaSegmentAsync(
            sessionId,
            MakeProducerSegment(
                sessionId,
                sequence: 1,
                startOffset: TimeSpan.FromSeconds(2),
                timingEpoch: timingEpoch,
                captureRequestId: requestId));
        await _manager.UpsertMediaSegmentAsync(
            sessionId,
            MakeProducerSegment(
                sessionId,
                sequence: 0,
                startOffset: TimeSpan.FromMilliseconds(500),
                timingEpoch: Guid.NewGuid(),
                captureRequestId: requestId));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _manager.UpsertMediaSegmentAsync(
                sessionId,
                MakeProducerSegment(
                    sessionId,
                    sequence: 3,
                    startOffset: TimeSpan.FromMilliseconds(4500),
                    timingEpoch: timingEpoch,
                    captureRequestId: requestId)));
        Assert.Equal(4, _manager.CurrentSession!.MediaSegments.Count);
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
    public async Task RecoverOrphanedSessionsAsync_KeepsSessionsWhoseClipsAreUnknownWhileLibraryIsUnavailable()
    {
        var orphan = new GameSession
        {
            Id = Guid.NewGuid(),
            GameName = "CS2",
            GameExecutableName = "cs2.exe",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            Status = SessionStatus.InProgress,
        };
        await _repo.SaveAsync(orphan);
        var manager = new SessionManager(
            _repo,
            _clipStorage,
            _eventBus,
            NullLogger<SessionManager>.Instance,
            new UnavailableLibrary());

        await manager.RecoverOrphanedSessionsAsync();

        var recovered = await _repo.GetByIdAsync(orphan.Id);
        Assert.NotNull(recovered);
        Assert.Equal(SessionStatus.Crashed, recovered.Status);
    }

    [Fact]
    public async Task EndSessionAsync_KeepsSessionWhoseClipsAreUnknownWhileLibraryIsUnavailable()
    {
        var manager = new SessionManager(
            _repo,
            _clipStorage,
            _eventBus,
            NullLogger<SessionManager>.Instance,
            new UnavailableLibrary());
        var game = MakeGame();
        await manager.StartSessionAsync(game);
        var sessionId = manager.CurrentSessionId!.Value;

        await manager.EndSessionAsync(game);

        var ended = await _repo.GetByIdAsync(sessionId);
        Assert.NotNull(ended);
        Assert.Equal(SessionStatus.Completed, ended.Status);
    }

    private sealed class UnavailableLibrary : Core.Storage.IClipLibraryHealth
    {
        public Core.Storage.ClipLibraryStatus LibraryStatus { get; } =
            new(false, "The clip index and its backup are both damaged.", [], true);
    }

    [Fact]
    public async Task RecoverOrphanedSessionsAsync_DoesNotClaimResumeWithoutLifecycleIdentity()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var orphan = new GameSession
        {
            Id = Guid.NewGuid(),
            GameName = "Still running fixture",
            GameExecutableName = process.ProcessName + ".exe",
            StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            Status = SessionStatus.InProgress,
            RecordingPaths = ["/path/preserved-recording.mkv"],
        };
        await _repo.SaveAsync(orphan);

        await _manager.RecoverOrphanedSessionsAsync();

        var recovered = await _repo.GetByIdAsync(orphan.Id);
        Assert.Equal(SessionStatus.Crashed, recovered!.Status);
        Assert.Null(_manager.CurrentSession);
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

    [Fact]
    public async Task AddRecordingPathAsync_FailedSaveDoesNotMutateAndRetryIsIdempotent()
    {
        using var repository = new ControllableSessionRepository();
        var manager = CreateManager(repository);
        await manager.StartSessionAsync(MakeGame());
        repository.FailSaves = true;

        await Assert.ThrowsAsync<IOException>(() => manager.AddRecordingPathAsync("recording.mp4"));
        Assert.Empty(manager.CurrentSession!.RecordingPaths);

        repository.FailSaves = false;
        await manager.AddRecordingPathAsync("recording.mp4");
        await manager.AddRecordingPathAsync("recording.mp4");
        Assert.Equal("recording.mp4", Assert.Single(manager.CurrentSession!.RecordingPaths));
        Assert.Equal(
            "recording.mp4",
            Assert.Single((await repository.GetByIdAsync(manager.CurrentSessionId!.Value))!.RecordingPaths));
    }

    [Fact]
    public async Task UpdateMatchesAsync_FailedSaveDoesNotMutateCurrentSession()
    {
        using var repository = new ControllableSessionRepository();
        var manager = CreateManager(repository);
        await manager.StartSessionAsync(MakeGame());
        repository.FailSaves = true;
        var matches = new[]
        {
            new SessionMatch { MatchNumber = 1, StartedAt = DateTimeOffset.UtcNow },
        };

        await Assert.ThrowsAsync<IOException>(() => manager.UpdateMatchesAsync(matches));

        Assert.Empty(manager.CurrentSession!.Matches);
        Assert.Empty((await repository.GetByIdAsync(manager.CurrentSessionId!.Value))!.Matches);
    }

    [Fact]
    public async Task EndSessionAsync_FailedSaveRetainsActiveSession()
    {
        using var repository = new ControllableSessionRepository();
        var manager = CreateManager(repository);
        var game = MakeGame();
        await manager.StartSessionAsync(game);
        await manager.AddRecordingPathAsync("recording.mp4");
        repository.FailSaves = true;

        await Assert.ThrowsAsync<IOException>(() => manager.EndSessionAsync(game));

        Assert.NotNull(manager.CurrentSession);
        Assert.Equal(SessionStatus.InProgress, manager.CurrentSession!.Status);
    }

    [Fact]
    public async Task EndSessionAsync_FailedDeleteRetainsActiveEmptySession()
    {
        using var repository = new ControllableSessionRepository();
        var manager = CreateManager(repository);
        var game = MakeGame();
        await manager.StartSessionAsync(game);
        repository.FailDeletes = true;

        await Assert.ThrowsAsync<IOException>(() => manager.EndSessionAsync(game));

        Assert.NotNull(manager.CurrentSession);
        Assert.Equal(SessionStatus.InProgress, manager.CurrentSession!.Status);
    }

    private static SessionMediaSegment MakeMediaSegment(
        Guid sessionId,
        SessionMediaRole role,
        SessionMediaState state) => new()
        {
            MediaId = Guid.NewGuid(),
            SessionId = sessionId,
            CaptureRequestId = Guid.NewGuid(),
            CaptureGeneration = 1,
            Role = role,
            FilePath = $"{role}.mkv",
            Sequence = 0,
            State = state,
            StartOffset = TimeSpan.FromMilliseconds(25),
            Duration = state == SessionMediaState.Finalized ? TimeSpan.FromSeconds(1) : null,
        };

    private static SessionMediaSegment MakeProducerSegment(
        Guid sessionId,
        int sequence = 0,
        TimeSpan? startOffset = null,
        Guid? outputEpoch = null,
        Guid? timingEpoch = null,
        Guid? captureRequestId = null) => new()
        {
            MediaId = Guid.NewGuid(),
            SessionId = sessionId,
            CaptureRequestId = captureRequestId ?? Guid.NewGuid(),
            CaptureGeneration = 1,
            Role = SessionMediaRole.Screen,
            FilePath = $"screen-{Guid.NewGuid():N}.mkv",
            Sequence = sequence,
            State = SessionMediaState.Finalized,
            StartOffset = startOffset ?? TimeSpan.Zero,
            Duration = TimeSpan.FromSeconds(1),
            TimingProvenance = SessionTimingProvenance.RecorderStopwatchEstimate,
            ProducerMetadata = new SessionSegmentProducerMetadata(
                1,
                outputEpoch ?? Guid.NewGuid(),
                timingEpoch ?? Guid.NewGuid()),
        };

    private sealed class ControllableSessionRepository : ISessionRepository
    {
        private readonly Dictionary<Guid, GameSession> _sessions = [];

        public bool FailSaves { get; set; }
        public bool FailDeletes { get; set; }
        public int SaveCount { get; private set; }

        public Task<GameSession?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_sessions.GetValueOrDefault(id));

        public Task<IReadOnlyList<GameSession>> GetByGameAsync(
            string gameName,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameSession>>(
                _sessions.Values.Where(session => session.GameName == gameName).ToList());

        public Task<IReadOnlyList<GameSession>> GetRecentAsync(int count, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GameSession>>(
                _sessions.Values.OrderByDescending(session => session.StartedAt).Take(count).ToList());

        public Task SaveAsync(GameSession session, CancellationToken ct = default)
        {
            if (FailSaves)
                throw new IOException("Injected session save failure.");

            SaveCount++;
            _sessions[session.Id] = session;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(Guid id, CancellationToken ct = default)
        {
            if (FailDeletes)
                throw new IOException("Injected session delete failure.");

            _sessions.Remove(id);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }
}
