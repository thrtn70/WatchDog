namespace WatchDog.Core.Events;

public interface IEventBus
{
    void Publish<T>(T eventData) where T : class;
    IDisposable Subscribe<T>(Action<T> handler) where T : class;
}

public sealed class InMemoryEventBus : IEventBus
{
    private readonly Dictionary<Type, List<Delegate>> _handlers = new();
    private readonly object _lock = new();

    public void Publish<T>(T eventData) where T : class
    {
        List<Delegate> snapshot;
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var handlers))
                return;
            snapshot = [.. handlers];
        }

        foreach (var handler in snapshot)
        {
            if (handler is Action<T> typed)
                typed(eventData);
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : class
    {
        lock (_lock)
        {
            if (!_handlers.TryGetValue(typeof(T), out var handlers))
            {
                handlers = [];
                _handlers[typeof(T)] = handlers;
            }
            handlers.Add(handler);
        }

        return new Subscription(() =>
        {
            lock (_lock)
            {
                if (_handlers.TryGetValue(typeof(T), out var handlers))
                    handlers.Remove(handler);
            }
        });
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose()
        {
            Interlocked.Exchange(ref _onDispose, null)?.Invoke();
        }
    }
}
