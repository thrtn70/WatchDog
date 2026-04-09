namespace WatchDog.Core.Sessions;

public interface ISessionRepository : IDisposable
{
    Task<GameSession?> GetByIdAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<GameSession>> GetByGameAsync(string gameName, CancellationToken ct = default);
    Task<IReadOnlyList<GameSession>> GetRecentAsync(int count, CancellationToken ct = default);
    Task SaveAsync(GameSession session, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
