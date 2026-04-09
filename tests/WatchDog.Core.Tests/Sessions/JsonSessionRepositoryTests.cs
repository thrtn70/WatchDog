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

    private static GameSession MakeSession(string gameName = "CS2", SessionStatus status = SessionStatus.InProgress) =>
        new()
        {
            Id = Guid.NewGuid(),
            GameName = gameName,
            GameExecutableName = "cs2.exe",
            StartedAt = DateTimeOffset.UtcNow,
            Status = status,
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

        var json = await File.ReadAllTextAsync(Path.Combine(_tempDir, "sessions-index.json"));

        Assert.Contains("schemaVersion", json);
    }
}
