namespace TikrClipr.Core.Buffer;

public interface IReplayBuffer : IDisposable
{
    bool IsActive { get; }
    BufferConfig Config { get; }

    bool Start();
    bool Stop();
    Task<string?> SaveAsync(CancellationToken ct = default);

    event Action<string>? Saved;
    event Action<string>? Error;
}
