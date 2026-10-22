namespace WatchDog.Core.Tests.Updates;

/// <summary>
/// A <see cref="TimeProvider"/> whose time moves only when a test calls <see cref="Advance"/>. One-shot timers
/// (all a <see cref="CancellationTokenSource"/> timeout uses) fire synchronously inside the call that makes them due.
/// </summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly HashSet<ManualTimer> _timers = [];
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward, then runs every timer that became due (outside the lock).</summary>
    public void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        ManualTimer[] due;
        lock (_gate)
        {
            _now += elapsed;
            due = [.. _timers.Where(timer => timer.DueAt <= _now)];
            foreach (var timer in due)
                timer.DueAt = null;
        }

        foreach (var timer in due)
            timer.Fire();
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;

        /// <summary>When the timer fires next; null while disarmed. Guarded by the clock's lock.</summary>
        public DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
                throw new NotSupportedException("ManualClock only runs one-shot timers.");

            lock (clock._gate)
            {
                if (_disposed) return false;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                clock._timers.Add(this);
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                _disposed = true;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
