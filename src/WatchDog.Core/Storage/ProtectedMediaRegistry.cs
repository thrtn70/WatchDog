namespace WatchDog.Core.Storage;

public interface IProtectedMediaRegistry
{
    IDisposable Protect(string mediaPath);
    bool IsProtected(string mediaPath);
    IDisposable? TryReserveDeletion(string mediaPath);
}

public sealed class ProtectedMediaRegistry : IProtectedMediaRegistry
{
    private readonly Dictionary<string, int> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deletionReservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public IDisposable Protect(string mediaPath)
    {
        var canonicalPath = StoragePath.Canonicalize(mediaPath);
        lock (_lock)
        {
            if (_deletionReservations.Contains(canonicalPath))
                throw new InvalidOperationException("Media cannot be protected after deletion has been reserved.");
            _leases.TryGetValue(canonicalPath, out var count);
            _leases[canonicalPath] = checked(count + 1);
        }

        return new ProtectionLease(this, canonicalPath);
    }

    public bool IsProtected(string mediaPath)
    {
        var canonicalPath = StoragePath.Canonicalize(mediaPath);
        lock (_lock)
            return _leases.ContainsKey(canonicalPath);
    }

    public IDisposable? TryReserveDeletion(string mediaPath)
    {
        var canonicalPath = StoragePath.Canonicalize(mediaPath);
        lock (_lock)
        {
            if (_leases.ContainsKey(canonicalPath) || !_deletionReservations.Add(canonicalPath))
                return null;
        }

        return new DeletionReservation(this, canonicalPath);
    }

    private void Release(string canonicalPath)
    {
        lock (_lock)
        {
            if (!_leases.TryGetValue(canonicalPath, out var count))
                return;
            if (count == 1)
                _leases.Remove(canonicalPath);
            else
                _leases[canonicalPath] = count - 1;
        }
    }

    private void ReleaseDeletion(string canonicalPath)
    {
        lock (_lock)
            _deletionReservations.Remove(canonicalPath);
    }

    private sealed class ProtectionLease(
        ProtectedMediaRegistry owner,
        string canonicalPath) : IDisposable
    {
        private ProtectedMediaRegistry? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.Release(canonicalPath);
    }

    private sealed class DeletionReservation(
        ProtectedMediaRegistry owner,
        string canonicalPath) : IDisposable
    {
        private ProtectedMediaRegistry? _owner = owner;

        public void Dispose() =>
            Interlocked.Exchange(ref _owner, null)?.ReleaseDeletion(canonicalPath);
    }
}

internal static class StoragePath
{
    public static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
