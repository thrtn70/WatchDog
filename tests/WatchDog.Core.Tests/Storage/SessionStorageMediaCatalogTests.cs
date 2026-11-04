using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Sessions;
using WatchDog.Core.Storage;

namespace WatchDog.Core.Tests.Storage;

public sealed class SessionStorageMediaCatalogTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        "WatchDog_SessionCatalog_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SnapshotIncludesScreenCameraAndLegacyProjectionWithoutDoubleCounting()
    {
        Directory.CreateDirectory(_path);
        var screen = CreateMedia("screen.mkv", 100);
        var camera = CreateMedia("camera.mp4", 250);
        using var repository = new JsonSessionRepository(
            _path,
            NullLogger<JsonSessionRepository>.Instance);
        var sessionId = Guid.NewGuid();
        await repository.SaveAsync(new GameSession
        {
            Id = sessionId,
            GameName = "Tutorial",
            GameExecutableName = "tutorial.exe",
            StartedAt = DateTimeOffset.UtcNow,
            CountsTowardStorageLimit = true,
            RecordingPaths = [screen],
            MediaSegments =
            [
                Segment(sessionId, screen, SessionMediaRole.Screen),
                Segment(sessionId, camera, SessionMediaRole.Camera),
            ],
        });
        var catalog = new SessionStorageMediaCatalog(
            repository,
            new StorageConfig { BasePath = _path });

        var snapshot = await catalog.SnapshotAsync();

        Assert.Equal(2, snapshot.OwnedPaths.Count);
        Assert.Equal(2, snapshot.ExistingFiles.Count);
        Assert.Contains(Path.GetFullPath(screen), snapshot.OwnedPaths);
        Assert.Contains(Path.GetFullPath(camera), snapshot.OwnedPaths);
        Assert.Equal(350, snapshot.ExistingBytes);
        Assert.Empty(snapshot.Failures);
    }

    [Fact]
    public async Task OlderVersionSessionsStayOwnedButDoNotCountTowardTheQuota()
    {
        Directory.CreateDirectory(_path);
        var older = CreateMedia("older-session.mp4", 400);
        var olderSegment = CreateMedia("older-segment.mkv", 300);
        var recorded = CreateMedia("recorded-session.mkv", 100);
        File.WriteAllText(Path.Combine(_path, "sessions-index.json"), OlderVersionIndex(older));
        var olderSessionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        using (var repository = new JsonSessionRepository(_path, NullLogger<JsonSessionRepository>.Instance))
        {
            // What v1.4.0 and v1.4.1 wrote: media segments, no countsTowardStorageLimit.
            await repository.SaveAsync(new GameSession
            {
                Id = olderSessionId,
                GameName = "Game",
                GameExecutableName = "game.exe",
                StartedAt = DateTimeOffset.UtcNow.AddDays(-10),
                MediaSegments = [Segment(olderSessionId, olderSegment, SessionMediaRole.Screen)],
            });
            await repository.SaveAsync(new GameSession
            {
                Id = sessionId,
                GameName = "Game",
                GameExecutableName = "game.exe",
                StartedAt = DateTimeOffset.UtcNow,
                CountsTowardStorageLimit = true,
                MediaSegments = [Segment(sessionId, recorded, SessionMediaRole.Screen)],
            });
        }

        using var reopened = new JsonSessionRepository(_path, NullLogger<JsonSessionRepository>.Instance);
        var catalog = new SessionStorageMediaCatalog(reopened, new StorageConfig { BasePath = _path });
        var snapshot = await catalog.SnapshotAsync();

        Assert.Contains(Path.GetFullPath(older), snapshot.OwnedPaths);
        Assert.Contains(Path.GetFullPath(olderSegment), snapshot.OwnedPaths);
        Assert.Contains(Path.GetFullPath(recorded), snapshot.OwnedPaths);
        Assert.Equal(Path.GetFullPath(recorded), Assert.Single(snapshot.ExistingFiles).Key);
        Assert.Equal(100, snapshot.ExistingBytes);
        Assert.Empty(snapshot.Failures);
    }

    // The shape v1.2.x and v1.3.0 wrote: schema 1, no media segments, no countsTowardStorageLimit.
    private static string OlderVersionIndex(string recordingPath) => new JsonObject
    {
        ["schemaVersion"] = 1,
        ["sessions"] = new JsonArray(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(),
            ["gameName"] = "Desktop",
            ["gameExecutableName"] = "desktop",
            ["startedAt"] = DateTimeOffset.UtcNow.AddDays(-200),
            ["endedAt"] = DateTimeOffset.UtcNow.AddDays(-200),
            ["status"] = "Completed",
            ["matches"] = new JsonArray(),
            ["recordingPaths"] = new JsonArray(recordingPath),
            ["isAutoRecorded"] = false,
        }),
    }.ToJsonString();

    private string CreateMedia(string name, int bytes)
    {
        var path = Path.Combine(_path, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static SessionMediaSegment Segment(
        Guid sessionId,
        string path,
        SessionMediaRole role) => new()
        {
            MediaId = Guid.NewGuid(),
            SessionId = sessionId,
            CaptureRequestId = Guid.NewGuid(),
            CaptureGeneration = 1,
            Role = role,
            FilePath = path,
            Sequence = 0,
            State = SessionMediaState.Finalized,
            StartOffset = TimeSpan.Zero,
            Duration = TimeSpan.FromSeconds(1),
        };

    public void Dispose() => Helpers.TestFs.DeleteDirectoryWithRetry(_path);
}
