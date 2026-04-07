namespace TikrClipr.Core.GameDetection;

public interface IGameDetector : IDisposable
{
    event Action<GameInfo>? GameStarted;
    event Action<GameInfo>? GameStopped;

    void Start();
    void Stop();
    GameInfo? CurrentGame { get; }
}
