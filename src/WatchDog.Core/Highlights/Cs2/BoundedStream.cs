namespace WatchDog.Core.Highlights.Cs2;

internal sealed class BoundedStream : Stream
{
    private readonly Stream _inner;
    private readonly long _maxBytes;
    private long _bytesRead;

    public BoundedStream(Stream inner, long maxBytes)
    {
        _inner = inner;
        _maxBytes = maxBytes;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var remaining = _maxBytes - _bytesRead;
        if (remaining <= 0) return 0;

        var toRead = (int)Math.Min(count, remaining);
        var read = _inner.Read(buffer, offset, toRead);
        _bytesRead += read;
        return read;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var remaining = _maxBytes - _bytesRead;
        if (remaining <= 0) return 0;

        var toRead = (int)Math.Min(count, remaining);
        var read = await _inner.ReadAsync(buffer.AsMemory(offset, toRead), ct);
        _bytesRead += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var remaining = _maxBytes - _bytesRead;
        if (remaining <= 0) return 0;

        var toRead = (int)Math.Min(buffer.Length, remaining);
        var read = await _inner.ReadAsync(buffer[..toRead], ct);
        _bytesRead += read;
        return read;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
