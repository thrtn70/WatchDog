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
