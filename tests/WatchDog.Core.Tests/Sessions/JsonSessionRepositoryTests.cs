using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Sessions;

namespace WatchDog.Core.Tests.Sessions;

public sealed class JsonSessionRepositoryTests : IDisposable
{
    private readonly string _tempDir;

    public JsonSessionRepositoryTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"WatchDog_Sessions_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private JsonSessionRepository CreateRepo() =>
        new(_tempDir, NullLogger<JsonSessionRepository>.Instance);

    private string IndexPath => Path.Combine(_tempDir, "sessions-index.json");

    private string BackupPath => Path.Combine(_tempDir, "sessions-index.json.bak");

    private static readonly JsonSerializerOptions IndexJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static Task WriteIndexAsync(
        string storagePath,
        IReadOnlyList<GameSession> sessions,
        int schemaVersion = 1)
    {
        Directory.CreateDirectory(storagePath);
        var json = JsonSerializer.Serialize(new { schemaVersion, sessions }, IndexJsonOptions);
        return File.WriteAllTextAsync(Path.Combine(storagePath, "sessions-index.json"), json);
    }

    private static async Task AssertPersistenceFailureAsync(Func<Task> action)
    {
        var error = await Record.ExceptionAsync(action);
        Assert.True(
            error is IOException or UnauthorizedAccessException,
            $"Expected an I/O or access failure, but got {error?.GetType().Name ?? "no exception"}.");
    }

    private static GameSession MakeSession(string gameName = "CS2", SessionStatus status = SessionStatus.InProgress) =>
        new()
        {
            Id = Guid.NewGuid(),
            GameName = gameName,
            GameExecutableName = "cs2.exe",
            StartedAt = DateTimeOffset.UtcNow,
            Status = status,
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

    private static SessionMediaSegment MakeLegacySegment(Guid sessionId, string filePath) => new()
    {
        MediaId = Guid.NewGuid(),
        SessionId = sessionId,
        CaptureRequestId = Guid.NewGuid(),
        CaptureGeneration = 1,
        Role = SessionMediaRole.Screen,
        FilePath = filePath,
        Sequence = 0,
        State = SessionMediaState.Finalized,
        StartOffset = TimeSpan.Zero,
        Duration = TimeSpan.FromSeconds(1),
    };

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var repo = CreateRepo();

        var result = await repo.GetByIdAsync(Guid.NewGuid());

        Assert.Null(result);
    }

    [Fact]
    public async Task SaveAsync_ThenGetByIdAsync_ReturnsSession()
    {
        var repo = CreateRepo();
        var session = MakeSession();

        await repo.SaveAsync(session);
        var result = await repo.GetByIdAsync(session.Id);

        Assert.NotNull(result);
        Assert.Equal(session.Id, result.Id);
        Assert.Equal(session.GameName, result.GameName);
    }

    [Fact]
    public async Task SaveAsync_UpdatesExistingSession()
    {
        var repo = CreateRepo();
        var session = MakeSession();
        await repo.SaveAsync(session);

        var updated = session with { Status = SessionStatus.Completed, EndedAt = DateTimeOffset.UtcNow };
        await repo.SaveAsync(updated);

        var result = await repo.GetByIdAsync(session.Id);
        Assert.Equal(SessionStatus.Completed, result!.Status);
        Assert.NotNull(result.EndedAt);
    }

    [Fact]
    public async Task DeleteAsync_RemovesSession()
    {
        var repo = CreateRepo();
        var session = MakeSession();
        await repo.SaveAsync(session);

        await repo.DeleteAsync(session.Id);
        var result = await repo.GetByIdAsync(session.Id);

        Assert.Null(result);
    }

    [Fact]
    public async Task DeleteAsync_IsNoOp_WhenSessionNotFound()
    {
        var repo = CreateRepo();
        // Should not throw
        await repo.DeleteAsync(Guid.NewGuid());
    }

    [Fact]
    public async Task GetByGameAsync_FiltersCorrectly()
    {
        var repo = CreateRepo();
        await repo.SaveAsync(MakeSession("CS2"));
        await repo.SaveAsync(MakeSession("CS2"));
        await repo.SaveAsync(MakeSession("Valorant"));

        var cs2 = await repo.GetByGameAsync("CS2");
        var valorant = await repo.GetByGameAsync("Valorant");
        var ow = await repo.GetByGameAsync("Overwatch");

        Assert.Equal(2, cs2.Count);
        Assert.Single(valorant);
        Assert.Empty(ow);
    }

    [Fact]
    public async Task GetByGameAsync_IsCaseInsensitive()
    {
        var repo = CreateRepo();
        await repo.SaveAsync(MakeSession("CS2"));

        var result = await repo.GetByGameAsync("cs2");

        Assert.Single(result);
    }

    [Fact]
    public async Task GetRecentAsync_ReturnsNewestFirst()
    {
        var repo = CreateRepo();
        var older = new GameSession
        {
            Id = Guid.NewGuid(),
            GameName = "Game",
            GameExecutableName = "game.exe",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
        };
        var newer = new GameSession
        {
            Id = Guid.NewGuid(),
            GameName = "Game",
            GameExecutableName = "game.exe",
            StartedAt = DateTimeOffset.UtcNow,
        };

        await repo.SaveAsync(older);
        await repo.SaveAsync(newer);

        var recent = await repo.GetRecentAsync(2);

        Assert.Equal(newer.Id, recent[0].Id);
        Assert.Equal(older.Id, recent[1].Id);
    }

    [Fact]
    public async Task GetRecentAsync_RespectsCountLimit()
    {
        var repo = CreateRepo();
        for (var i = 0; i < 5; i++)
            await repo.SaveAsync(MakeSession());

        var result = await repo.GetRecentAsync(3);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task PersistedToDisk_ReloadedByNewInstance()
    {
        var session = MakeSession();

        var repo1 = CreateRepo();
        await repo1.SaveAsync(session);

        // New instance reads from same directory
        var repo2 = CreateRepo();
        var result = await repo2.GetByIdAsync(session.Id);

        Assert.NotNull(result);
        Assert.Equal(session.Id, result.Id);
    }

    [Fact]
    public async Task AtomicWrite_TempFileIsCleanedUp()
    {
        var repo = CreateRepo();
        await repo.SaveAsync(MakeSession());

        var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");

        Assert.Empty(tmpFiles);
    }

    [Fact]
    public async Task IndexFile_ContainsSchemaVersion()
    {
        var repo = CreateRepo();
        await repo.SaveAsync(MakeSession());

        var json = await File.ReadAllTextAsync(IndexPath);

        Assert.Contains("schemaVersion", json);
    }

    [Fact]
    public async Task ConcurrentSaves_AreSerializedAndAllPersisted()
    {
        using var repo = CreateRepo();
        var sessions = Enumerable.Range(0, 20)
            .Select(i => MakeSession($"Game {i}"))
            .ToList();

        await Task.WhenAll(sessions.Select(session => repo.SaveAsync(session)));

        using var reloaded = CreateRepo();
        var persisted = await reloaded.GetRecentAsync(sessions.Count);
        Assert.Equal(
            sessions.Select(session => session.Id).OrderBy(id => id),
            persisted.Select(session => session.Id).OrderBy(id => id));
    }

    [Fact]
    public async Task SaveAsync_WhenPersistenceFails_ThrowsAndRollsBackUpdate()
    {
        using var repo = CreateRepo();
        var session = MakeSession();
        await repo.SaveAsync(session);

        File.Move(IndexPath, IndexPath + ".blocked");
        Directory.CreateDirectory(IndexPath);
        var updated = session with { Status = SessionStatus.Completed, EndedAt = DateTimeOffset.UtcNow };

        await AssertPersistenceFailureAsync(() => repo.SaveAsync(updated));

        var retained = await repo.GetByIdAsync(session.Id);
        Assert.Equal(SessionStatus.InProgress, retained!.Status);
        Assert.Null(retained.EndedAt);
    }

    [Fact]
    public async Task DeleteAsync_WhenPersistenceFails_ThrowsAndRollsBackDelete()
    {
        using var repo = CreateRepo();
        var session = MakeSession();
        await repo.SaveAsync(session);

        File.Move(IndexPath, IndexPath + ".blocked");
        Directory.CreateDirectory(IndexPath);

        await AssertPersistenceFailureAsync(() => repo.DeleteAsync(session.Id));

        Assert.NotNull(await repo.GetByIdAsync(session.Id));
    }

    [Theory]
    [InlineData("session")]
    [InlineData("paths")]
    [InlineData("matches")]
    [InlineData("match")]
    [InlineData("clipIds")]
    public async Task StructurallyCorruptPrimary_UsesValidBackup(string damage)
    {
        var session = MakeSession("Recoverable");
        using (var repo = CreateRepo())
        {
            await repo.SaveAsync(session);
            await repo.SaveAsync(MakeSession("Later"));
        }
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(IndexPath))!;
        var sessions = node["sessions"]!.AsArray();
        if (damage == "session") sessions[0] = null;
        else if (damage == "paths") sessions[0]!["recordingPaths"] = null;
        else if (damage == "matches") sessions[0]!["matches"] = null;
        else if (damage == "match") sessions[0]!["matches"] = System.Text.Json.Nodes.JsonNode.Parse("[null]");
        else sessions[0]!["matches"] = System.Text.Json.Nodes.JsonNode.Parse(
            "[{\"matchNumber\":1,\"startedAt\":\"2026-09-09T00:00:00Z\",\"clipIds\":null}]");
        await File.WriteAllTextAsync(IndexPath, node.ToJsonString());
        using var recovered = CreateRepo();
        Assert.Equal(session.Id, Assert.Single(await recovered.GetRecentAsync(10)).Id);
    }

    [Fact]
    public async Task CorruptPrimary_IsRecoveredFromPreviousGoodBackup()
    {
        var first = MakeSession("First");
        var second = MakeSession("Second");
        using (var repo = CreateRepo())
        {
            await repo.SaveAsync(first);
            await repo.SaveAsync(second);
        }

        Assert.True(File.Exists(BackupPath));
        await File.WriteAllTextAsync(IndexPath, "{ corrupt json");

        using var recovered = CreateRepo();
        Assert.NotNull(await recovered.GetByIdAsync(first.Id));
        Assert.Null(await recovered.GetByIdAsync(second.Id));

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(IndexPath));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task LegacyIndexes_AreMergedReadOnly_WithPrimaryTakingPrecedenceById()
    {
        var legacyPath = Path.Combine(_tempDir, "legacy");
        var sharedId = Guid.NewGuid();
        var legacyShared = MakeSession("Legacy Shared") with { Id = sharedId };
        var legacyOnly = MakeSession("Legacy Only");
        var primaryShared = MakeSession("Primary Shared") with { Id = sharedId };
        await WriteIndexAsync(legacyPath, [legacyShared, legacyOnly]);
        await WriteIndexAsync(_tempDir, [primaryShared]);
        var originalLegacyJson = await File.ReadAllTextAsync(Path.Combine(legacyPath, "sessions-index.json"));

        using var repo = new JsonSessionRepository(
            _tempDir,
            NullLogger<JsonSessionRepository>.Instance,
            [legacyPath]);

        Assert.Equal("Primary Shared", (await repo.GetByIdAsync(sharedId))!.GameName);
        Assert.NotNull(await repo.GetByIdAsync(legacyOnly.Id));

        await repo.SaveAsync(MakeSession("New Primary"));
        Assert.Equal(
            originalLegacyJson,
            await File.ReadAllTextAsync(Path.Combine(legacyPath, "sessions-index.json")));
    }

    [Fact]
    public async Task FutureSchemaPrimary_IsRejectedBeforeAnyWrite()
    {
        await WriteIndexAsync(_tempDir, [MakeSession()], schemaVersion: 3);
        var originalJson = await File.ReadAllTextAsync(IndexPath);

        var error = Assert.Throws<NotSupportedException>(() => CreateRepo());

        Assert.Contains("schema version 3", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(originalJson, await File.ReadAllTextAsync(IndexPath));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public async Task CorruptPrimaryAndBackup_FailConstructionAndPreserveBothFiles()
    {
        var primaryBytes = "{ corrupt primary"u8.ToArray();
        var backupBytes = "{ corrupt backup"u8.ToArray();
        await File.WriteAllBytesAsync(IndexPath, primaryBytes);
        await File.WriteAllBytesAsync(BackupPath, backupBytes);

        var error = Assert.Throws<InvalidDataException>(() => CreateRepo());

        Assert.Contains("could not be recovered", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(primaryBytes, await File.ReadAllBytesAsync(IndexPath));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(BackupPath));
    }

    [Fact]
    public async Task DeleteLegacySession_PersistsTombstoneAcrossReopen()
    {
        var legacyPath = Path.Combine(_tempDir, "legacy");
        var legacySession = MakeSession("Legacy");
        await WriteIndexAsync(legacyPath, [legacySession]);

        using (var repo = new JsonSessionRepository(
            _tempDir,
            NullLogger<JsonSessionRepository>.Instance,
            [legacyPath]))
        {
            await repo.DeleteAsync(legacySession.Id);
            Assert.Null(await repo.GetByIdAsync(legacySession.Id));
        }

        using var reopened = new JsonSessionRepository(
            _tempDir,
            NullLogger<JsonSessionRepository>.Instance,
            [legacyPath]);
        Assert.Null(await reopened.GetByIdAsync(legacySession.Id));
    }

    [Fact]
    public async Task FailedLegacyDelete_RollsBackSessionAndTombstone()
    {
        var legacyPath = Path.Combine(_tempDir, "legacy");
        var legacySession = MakeSession("Legacy");
        await WriteIndexAsync(legacyPath, [legacySession]);
        using var repo = new JsonSessionRepository(
            _tempDir,
            NullLogger<JsonSessionRepository>.Instance,
            [legacyPath]);
        Directory.CreateDirectory(IndexPath);

        await AssertPersistenceFailureAsync(() => repo.DeleteAsync(legacySession.Id));
        Assert.NotNull(await repo.GetByIdAsync(legacySession.Id));

        Directory.Delete(IndexPath);
        await repo.SaveAsync(MakeSession("Primary"));
        using var reopened = new JsonSessionRepository(
            _tempDir,
            NullLogger<JsonSessionRepository>.Instance,
            [legacyPath]);
        Assert.NotNull(await reopened.GetByIdAsync(legacySession.Id));
    }

    [Fact]
    public async Task SaveSameId_ClearsLegacyDeletionTombstone()
    {
        var legacyPath = Path.Combine(_tempDir, "legacy");
        var legacySession = MakeSession("Legacy");
        await WriteIndexAsync(legacyPath, [legacySession]);
        using (var repo = new JsonSessionRepository(
            _tempDir,
            NullLogger<JsonSessionRepository>.Instance,
            [legacyPath]))
        {
            await repo.DeleteAsync(legacySession.Id);
            await repo.SaveAsync(legacySession with { GameName = "Restored" });
        }

        using var reopened = new JsonSessionRepository(
            _tempDir,
            NullLogger<JsonSessionRepository>.Instance,
            [legacyPath]);
        Assert.Equal("Restored", (await reopened.GetByIdAsync(legacySession.Id))!.GameName);
    }

    [Fact]
    public async Task SaveAndRead_IsolateMutableSessionCollections()
    {
        var recordingPaths = new List<string> { "original.mp4" };
        var clipIds = new List<string> { "clip-1" };
        var matches = new List<SessionMatch>
        {
            new() { MatchNumber = 1, StartedAt = DateTimeOffset.UtcNow, ClipIds = clipIds },
        };
        var session = MakeSession() with { RecordingPaths = recordingPaths, Matches = matches };
        using var repo = CreateRepo();

        await repo.SaveAsync(session);
        recordingPaths[0] = "mutated.mp4";
        clipIds[0] = "mutated-clip";
        matches.Clear();

        var firstRead = await repo.GetByIdAsync(session.Id);
        Assert.Equal("original.mp4", Assert.Single(firstRead!.RecordingPaths));
        Assert.Equal("clip-1", Assert.Single(Assert.Single(firstRead.Matches).ClipIds));

        Assert.IsType<string[]>(firstRead.RecordingPaths)[0] = "reader-mutated.mp4";
        Assert.IsType<string[]>(Assert.Single(firstRead.Matches).ClipIds)[0] = "reader-mutated-clip";
        var secondRead = await repo.GetByIdAsync(session.Id);
        Assert.Equal("original.mp4", Assert.Single(secondRead!.RecordingPaths));
        Assert.Equal("clip-1", Assert.Single(Assert.Single(secondRead.Matches).ClipIds));
    }

    [Fact]
    public async Task VersionOneSessionWithoutMediaSegments_LoadsAsEmptyAndUpgradesOnSave()
    {
        var session = MakeSession();
        await WriteIndexAsync(_tempDir, [session], schemaVersion: 1);

        using (var repo = CreateRepo())
        {
            var loaded = await repo.GetByIdAsync(session.Id);
            Assert.Empty(loaded!.MediaSegments);
            await repo.SaveAsync(loaded);
        }

        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(IndexPath));
        Assert.Equal(2, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public async Task MediaSegments_AreClonedAndMalformedGraphUsesBackup()
    {
        var session = MakeSession();
        var media = new List<SessionMediaSegment>
        {
            new()
            {
                MediaId = Guid.NewGuid(),
                SessionId = session.Id,
                CaptureRequestId = Guid.NewGuid(),
                CaptureGeneration = 1,
                Role = SessionMediaRole.Camera,
                FilePath = "camera.mkv",
                Sequence = 0,
                State = SessionMediaState.Planned,
                StartOffset = TimeSpan.Zero,
            },
        };
        session = session with { MediaSegments = media };
        using (var repo = CreateRepo())
        {
            await repo.SaveAsync(session);
            await repo.SaveAsync(MakeSession("Later"));
        }
        media.Clear();

        using (var clean = CreateRepo())
            Assert.Single((await clean.GetByIdAsync(session.Id))!.MediaSegments);

        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(IndexPath))!;
        node["sessions"]![0]!["mediaSegments"]![0]!["filePath"] = "";
        await File.WriteAllTextAsync(IndexPath, node.ToJsonString());
        using var recovered = CreateRepo();
        Assert.Equal(session.Id, Assert.Single(await recovered.GetRecentAsync(10)).Id);
    }

    [Fact]
    public async Task ProducerMetadata_RoundTripsWithoutChangingLegacySegments()
    {
        var session = MakeSession();
        var legacy = new SessionMediaSegment
        {
            MediaId = Guid.NewGuid(),
            SessionId = session.Id,
            CaptureRequestId = Guid.NewGuid(),
            CaptureGeneration = 1,
            Role = SessionMediaRole.Screen,
            FilePath = "legacy.mkv",
            Sequence = 0,
            State = SessionMediaState.Finalized,
            StartOffset = TimeSpan.FromMilliseconds(25),
            Duration = TimeSpan.FromSeconds(1),
        };
        var producer = MakeProducerSegment(
            session.Id,
            sequence: 4,
            startOffset: TimeSpan.FromMilliseconds(525));
        session = session with { MediaSegments = [legacy, producer] };

        using (var repo = CreateRepo())
            await repo.SaveAsync(session);
        using var reopened = CreateRepo();
        var loaded = (await reopened.GetByIdAsync(session.Id))!.MediaSegments;

        var loadedLegacy = Assert.Single(loaded, item => item.MediaId == legacy.MediaId);
        Assert.Null(loadedLegacy.ProducerMetadata);
        Assert.Equal(SessionTimingProvenance.BackendStopwatchEstimate, loadedLegacy.TimingProvenance);
        var loadedProducer = Assert.Single(loaded, item => item.MediaId == producer.MediaId);
        Assert.Equal(producer.StartOffset, loadedProducer.StartOffset);
        Assert.Equal(producer.Duration, loadedProducer.Duration);
        Assert.Equal(SessionTimingProvenance.RecorderStopwatchEstimate, loadedProducer.TimingProvenance);
        Assert.Equal(producer.ProducerMetadata, loadedProducer.ProducerMetadata);
    }

    [Fact]
    public async Task SaveAsync_InvalidProducerMetadataFailsBeforeMutationOrDiskWrite()
    {
        var session = MakeSession();
        var valid = MakeProducerSegment(session.Id);
        session = session with { MediaSegments = [valid] };
        using var repo = CreateRepo();
        await repo.SaveAsync(session);
        var originalBytes = await File.ReadAllBytesAsync(IndexPath);
        var invalid = valid with
        {
            ProducerMetadata = valid.ProducerMetadata! with { OutputEpoch = Guid.Empty },
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.SaveAsync(session with { MediaSegments = [invalid] }));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(IndexPath));
        Assert.Equal(valid, Assert.Single((await repo.GetByIdAsync(session.Id))!.MediaSegments));
    }

    [Fact]
    public async Task SaveAsync_FutureProducerMetadataFailsBeforeMutationOrDiskWrite()
    {
        var session = MakeSession();
        using var repo = CreateRepo();
        await repo.SaveAsync(session);
        var originalBytes = await File.ReadAllBytesAsync(IndexPath);
        var future = MakeProducerSegment(session.Id) with
        {
            ProducerMetadata = new SessionSegmentProducerMetadata(2, Guid.NewGuid(), Guid.NewGuid()),
        };

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            repo.SaveAsync(session with { MediaSegments = [future] }));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(IndexPath));
        Assert.Empty((await repo.GetByIdAsync(session.Id))!.MediaSegments);
    }

    [Fact]
    public async Task SaveAsync_PersistedProducerReceiptCannotBeChangedOrRemoved()
    {
        var session = MakeSession();
        var producer = MakeProducerSegment(session.Id);
        session = session with { MediaSegments = [producer] };
        using var repo = CreateRepo();
        await repo.SaveAsync(session);
        var originalBytes = await File.ReadAllBytesAsync(IndexPath);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.SaveAsync(session with
            {
                MediaSegments =
                [
                    producer with
                    {
                        Duration = producer.Duration!.Value + TimeSpan.FromMilliseconds(1),
                    },
                ],
            }));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            repo.SaveAsync(session with { MediaSegments = [] }));

        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(IndexPath));
        Assert.Equal(producer, Assert.Single((await repo.GetByIdAsync(session.Id))!.MediaSegments));
    }

    [Fact]
    public async Task MalformedProducerMetadataUsesPreviousGoodBackup()
    {
        var session = MakeSession();
        var producer = MakeProducerSegment(session.Id);
        session = session with { MediaSegments = [producer] };
        using (var repo = CreateRepo())
        {
            await repo.SaveAsync(session);
            await repo.SaveAsync(MakeSession("Later"));
        }
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(IndexPath))!;
        node["sessions"]![0]!["mediaSegments"]![0]!["producerMetadata"]!["outputEpoch"] = Guid.Empty;
        await File.WriteAllTextAsync(IndexPath, node.ToJsonString());

        using var recovered = CreateRepo();
        Assert.Equal(producer.ProducerMetadata, Assert.Single(
            (await recovered.GetByIdAsync(session.Id))!.MediaSegments).ProducerMetadata);
    }

    [Fact]
    public async Task FutureProducerMetadataDoesNotFallBackToBackupOrRewritePrimary()
    {
        var session = MakeSession();
        session = session with { MediaSegments = [MakeProducerSegment(session.Id)] };
        using (var repo = CreateRepo())
        {
            await repo.SaveAsync(session);
            await repo.SaveAsync(MakeSession("Later"));
        }
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(IndexPath))!;
        node["sessions"]![0]!["mediaSegments"]![0]!["producerMetadata"]!["schemaVersion"] = 2;
        await File.WriteAllTextAsync(IndexPath, node.ToJsonString());
        var futureBytes = await File.ReadAllBytesAsync(IndexPath);
        var backupBytes = await File.ReadAllBytesAsync(BackupPath);

        Assert.Throws<NotSupportedException>(() => CreateRepo());

        Assert.Equal(futureBytes, await File.ReadAllBytesAsync(IndexPath));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(BackupPath));
    }

    [Fact]
    public async Task FutureProducerMetadataAfterMalformedV1DoesNotFallBackOrRewritePrimary()
    {
        var first = MakeSession("Malformed V1");
        first = first with { MediaSegments = [MakeProducerSegment(first.Id)] };
        var second = MakeSession("Future V2");
        second = second with { MediaSegments = [MakeProducerSegment(second.Id)] };
        using (var repo = CreateRepo())
        {
            await repo.SaveAsync(first);
            await repo.SaveAsync(second);
        }
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(IndexPath))!;
        node["sessions"]![0]!["mediaSegments"]![0]!["producerMetadata"]!["outputEpoch"] = Guid.Empty;
        node["sessions"]![1]!["mediaSegments"]![0]!["producerMetadata"]!["schemaVersion"] = 2;
        await File.WriteAllTextAsync(IndexPath, node.ToJsonString());
        var futureBytes = await File.ReadAllBytesAsync(IndexPath);
        var backupBytes = await File.ReadAllBytesAsync(BackupPath);

        Assert.Throws<NotSupportedException>(() => CreateRepo());

        Assert.Equal(futureBytes, await File.ReadAllBytesAsync(IndexPath));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(BackupPath));
    }

    [Fact]
    public async Task FutureProducerMetadataWithUnreadableFutureShapeDoesNotFallBackOrRewritePrimary()
    {
        var session = MakeSession();
        session = session with { MediaSegments = [MakeProducerSegment(session.Id)] };
        using (var repo = CreateRepo())
        {
            await repo.SaveAsync(session);
            await repo.SaveAsync(MakeSession("Later"));
        }
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(IndexPath))!;
        var segment = node["sessions"]![0]!["mediaSegments"]![0]!;
        segment["timingProvenance"] = "FutureRecorderClock";
        segment["producerMetadata"]!["schemaVersion"] = 2;
        segment["producerMetadata"]!["outputEpoch"] =
            System.Text.Json.Nodes.JsonNode.Parse("{\"futureIdentity\":true}");
        await File.WriteAllTextAsync(IndexPath, node.ToJsonString());
        var futureBytes = await File.ReadAllBytesAsync(IndexPath);
        var backupBytes = await File.ReadAllBytesAsync(BackupPath);

        Assert.Throws<NotSupportedException>(() => CreateRepo());

        Assert.Equal(futureBytes, await File.ReadAllBytesAsync(IndexPath));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(BackupPath));
    }

    [Fact]
    public async Task MalformedProducerMetadataWithoutBackupFailsAndPreservesPrimary()
    {
        var session = MakeSession();
        var producer = MakeProducerSegment(session.Id);
        session = session with { MediaSegments = [producer] };
        await WriteIndexAsync(_tempDir, [session], schemaVersion: 2);
        var node = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(IndexPath))!;
        node["sessions"]![0]!["mediaSegments"]![0]!["producerMetadata"]!["producerTimingEpoch"] = Guid.Empty;
        await File.WriteAllTextAsync(IndexPath, node.ToJsonString());
        var invalidBytes = await File.ReadAllBytesAsync(IndexPath);

        Assert.Throws<InvalidDataException>(() => CreateRepo());

        Assert.Equal(invalidBytes, await File.ReadAllBytesAsync(IndexPath));
        Assert.False(File.Exists(BackupPath));
    }

    [Fact]
    public async Task SaveAsync_RejectsProducerIdentityConflictsAndSameEpochOverlap()
    {
        var session = MakeSession();
        var requestId = Guid.NewGuid();
        var timingEpoch = Guid.NewGuid();
        var first = MakeProducerSegment(
            session.Id,
            timingEpoch: timingEpoch,
            captureRequestId: requestId);
        var invalidPeers = new[]
        {
            MakeProducerSegment(
                session.Id,
                sequence: 1,
                startOffset: TimeSpan.FromSeconds(2),
                outputEpoch: first.ProducerMetadata!.OutputEpoch,
                timingEpoch: timingEpoch,
                captureRequestId: requestId),
            MakeProducerSegment(
                session.Id,
                sequence: 0,
                startOffset: TimeSpan.FromSeconds(2),
                timingEpoch: timingEpoch,
                captureRequestId: requestId),
            MakeProducerSegment(
                session.Id,
                sequence: 1,
                startOffset: TimeSpan.FromMilliseconds(500),
                timingEpoch: timingEpoch,
                captureRequestId: requestId),
        };

        using var repo = CreateRepo();
        foreach (var peer in invalidPeers)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.SaveAsync(session with { MediaSegments = [first, peer] }));
        }

        Assert.False(File.Exists(IndexPath));
    }

    [Fact]
    public async Task SaveAsync_ProducerPathCollisionFailsBeforeMutationOrDiskWrite()
    {
        var session = MakeSession();
        using var repo = CreateRepo();
        await repo.SaveAsync(session);
        var originalBytes = await File.ReadAllBytesAsync(IndexPath);
        var producer = MakeProducerSegment(session.Id) with { FilePath = "Shared-Output.MKV" };
        var collisions = new IReadOnlyList<SessionMediaSegment>[]
        {
            [producer, MakeProducerSegment(session.Id) with { FilePath = "shared-output.mkv" }],
            [producer, MakeLegacySegment(session.Id, "SHARED-OUTPUT.mkv")],
        };

        foreach (var mediaSegments in collisions)
        {
            await Assert.ThrowsAsync<ArgumentException>(() =>
                repo.SaveAsync(session with { MediaSegments = mediaSegments }));
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(IndexPath));
            Assert.Empty((await repo.GetByIdAsync(session.Id))!.MediaSegments);
        }
    }

    [Fact]
    public async Task Load_ProducerPathCollisionFailsAndPreservesMalformedPrimary()
    {
        var session = MakeSession();
        var producer = MakeProducerSegment(session.Id) with { FilePath = "Shared-Output.MKV" };
        var collisions = new IReadOnlyList<SessionMediaSegment>[]
        {
            [producer, MakeProducerSegment(session.Id) with { FilePath = "shared-output.mkv" }],
            [producer, MakeLegacySegment(session.Id, "SHARED-OUTPUT.mkv")],
        };

        foreach (var mediaSegments in collisions)
        {
            await WriteIndexAsync(_tempDir, [session with { MediaSegments = mediaSegments }], schemaVersion: 2);
            var malformedBytes = await File.ReadAllBytesAsync(IndexPath);

            Assert.Throws<InvalidDataException>(() => CreateRepo());

            Assert.Equal(malformedBytes, await File.ReadAllBytesAsync(IndexPath));
            Assert.False(File.Exists(BackupPath));
        }
    }

    [Fact]
    public async Task SaveAsync_DoesNotCompareIndependentProducerTimingEpochs()
    {
        var session = MakeSession();
        var requestId = Guid.NewGuid();
        session = session with
        {
            MediaSegments =
            [
                MakeProducerSegment(session.Id, captureRequestId: requestId),
                MakeProducerSegment(
                    session.Id,
                    startOffset: TimeSpan.FromMilliseconds(500),
                    captureRequestId: requestId),
            ],
        };
        using var repo = CreateRepo();

        await repo.SaveAsync(session);

        Assert.Equal(2, (await repo.GetByIdAsync(session.Id))!.MediaSegments.Count);
    }
}
