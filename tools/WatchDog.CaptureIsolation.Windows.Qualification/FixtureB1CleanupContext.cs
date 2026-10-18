namespace WatchDog.CaptureIsolation.Windows.Qualification;

/// <summary>
/// Lazy one-shot cleanup deadline shared by every cleanup stage for one retained launch.
/// The owner controls its lifetime; expiration remains sticky after the token is canceled.
/// </summary>
internal sealed class FixtureB1CleanupContext : IDisposable
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly TimeSpan _budget;
    private CancellationTokenSource? _source;
    private CancellationTokenRegistration _expirationRegistration;
    private bool _disposed;
    private int _expired;

    internal FixtureB1CleanupContext(TimeProvider clock, TimeSpan budget)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (budget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(budget));

        _clock = clock;
        _budget = budget;
    }

    internal CancellationToken Token
    {
        get
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_source is null)
                {
                    _source = new CancellationTokenSource(_budget, _clock);
                    _expirationRegistration = _source.Token.Register(
                        static state => Interlocked.Exchange(
                            ref ((FixtureB1CleanupContext)state!)._expired, 1),
                        this);
                }

                return _source.Token;
            }
        }
    }

    internal bool IsExpired
    {
        get
        {
            if (Volatile.Read(ref _expired) != 0)
                return true;
            lock (_gate)
                return _source?.IsCancellationRequested == true;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _expirationRegistration.Dispose();
            _source?.Dispose();
        }
    }
}
