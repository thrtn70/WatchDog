using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

/// <summary>
/// Retains fixture file handles until delete disposition and close are both confirmed.
/// A failed mark must not discard the only handle capable of deleting the file.
/// </summary>
internal sealed class FixturePendingFileCleanup(
    object lifetimeGate,
    Func<SafeWindowsHandle, bool> markDelete)
{
    private readonly List<SafeWindowsHandle> _pending = [];
    private readonly HashSet<SafeWindowsHandle> _completed = [];

    internal bool TryCleanup(SafeWindowsHandle? file)
    {
        lock (lifetimeGate)
        {
            if (file is not null && _completed.Contains(file))
                return TryDrainCore();
            var wasPending = file is not null && _pending.Contains(file);
            var pendingCleared = TryDrainCore();
            if (file is null || wasPending)
                return pendingCleared;

            if (TryMarkAndClose(file))
            {
                _completed.Add(file);
                return pendingCleared;
            }

            _pending.Add(file);
            return false;
        }
    }

    internal bool TryDrain()
    {
        lock (lifetimeGate)
            return TryDrainCore();
    }

    internal int PendingCount
    {
        get
        {
            lock (lifetimeGate)
                return _pending.Count;
        }
    }

    private bool TryDrainCore()
    {
        for (var index = _pending.Count - 1; index >= 0; index--)
        {
            if (TryMarkAndClose(_pending[index]))
            {
                _completed.Add(_pending[index]);
                _pending.RemoveAt(index);
            }
        }
        return _pending.Count == 0;
    }

    private bool TryMarkAndClose(SafeWindowsHandle file)
    {
        try
        {
            if (!markDelete(file) || !file.CloseOnce())
                return false;
        }
        catch
        {
            return false;
        }
        file.Dispose();
        return true;
    }
}

/// <summary>Retains directory handles when an explicit close cannot be confirmed.</summary>
internal sealed class FixturePendingRootClose(object lifetimeGate)
{
    private readonly List<SafeWindowsHandle> _pending = [];
    private readonly HashSet<SafeWindowsHandle> _completed = [];

    internal bool TryClose(SafeWindowsHandle? root)
    {
        lock (lifetimeGate)
        {
            if (root is not null && _completed.Contains(root))
                return TryDrainCore();
            var wasPending = root is not null && _pending.Contains(root);
            var pendingCleared = TryDrainCore();
            if (root is null || wasPending)
                return pendingCleared;
            if (TryCloseCore(root))
            {
                _completed.Add(root);
                return pendingCleared;
            }
            _pending.Add(root);
            return false;
        }
    }

    internal bool TryDrain()
    {
        lock (lifetimeGate)
            return TryDrainCore();
    }

    internal int PendingCount
    {
        get
        {
            lock (lifetimeGate)
                return _pending.Count;
        }
    }

    private bool TryDrainCore()
    {
        for (var index = _pending.Count - 1; index >= 0; index--)
        {
            if (TryCloseCore(_pending[index]))
            {
                _completed.Add(_pending[index]);
                _pending.RemoveAt(index);
            }
        }
        return _pending.Count == 0;
    }

    private static bool TryCloseCore(SafeWindowsHandle root)
    {
        if (!root.CloseOnce())
            return false;
        root.Dispose();
        return true;
    }
}

/// <summary>
/// Fixture-scoped owner for observer handles whose exact close was not confirmed.
/// The fixture retains this owner and retries before any further scratch work.
/// </summary>
internal sealed class FixtureRelativeFileCloseOwner(object lifetimeGate)
{
    private readonly FixturePendingRootClose _pendingClose = new(lifetimeGate);

    internal object LifetimeGate { get; } = lifetimeGate ??
        throw new ArgumentNullException(nameof(lifetimeGate));

    internal bool TryClose(SafeWindowsHandle handle) => _pendingClose.TryClose(handle);

    internal bool TryClosePending() => _pendingClose.TryDrain();

    internal int PendingCount => _pendingClose.PendingCount;
}
