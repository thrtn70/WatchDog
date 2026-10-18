namespace WatchDog.Core.Runtime;

/// <summary>Holds an operating-system file handle that permits one writer process.</summary>
public sealed class ExclusiveFileLease : IDisposable
{
    private FileStream? _stream;

    private ExclusiveFileLease(FileStream stream)
    {
        _stream = stream;
    }

    public static ExclusiveFileLease? TryAcquire(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        try
        {
            var stream = new FileStream(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            return new ExclusiveFileLease(stream);
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33)
        {
            return null;
        }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _stream, null)?.Dispose();
    }
}
