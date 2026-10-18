using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WatchDog.CaptureProtocol;

/// <summary>
/// Direction- and channel-specific authenticated framing session. A session is single-lane:
/// sequences begin at one and advance only after a complete authenticated frame succeeds.
/// </summary>
public sealed class AuthenticatedProtocolSession : IDisposable
{
    public const int ProtocolVersion = 1;
    public const int HeaderBytes = 24;
    public const int AuthenticationTagBytes = 32;
    public const int FrameOverheadBytes = HeaderBytes + AuthenticationTagBytes;

    private static ReadOnlySpan<byte> Magic => "WDCP"u8;

    private readonly object _gate = new();
    private readonly byte[] _sessionKey;
    private readonly ProtocolChannel _channel;
    private readonly ProtocolDirection _direction;
    private readonly CaptureProtocolLimits _limits;
    private long _nextWriteSequence = 1;
    private long _nextReadSequence = 1;
    private bool _disposed;

    public AuthenticatedProtocolSession(
        ReadOnlySpan<byte> launchSecret,
        ProtocolChannel channel,
        ProtocolDirection direction,
        CaptureProtocolLimits limits)
    {
        if (launchSecret.Length < 32)
            throw new ArgumentException("A launch secret must contain at least 32 bytes.", nameof(launchSecret));
        if (!Enum.IsDefined(channel))
            throw new ArgumentOutOfRangeException(nameof(channel));
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));

        _limits = limits ?? throw new ArgumentNullException(nameof(limits));
        _channel = channel;
        _direction = direction;
        var separation = Encoding.ASCII.GetBytes(
            $"WatchDog/CaptureProtocol/v{ProtocolVersion}/{(byte)channel}/{(byte)direction}");
        _sessionKey = HMACSHA256.HashData(launchSecret, separation);
    }

    public byte[] Encode<T>(ProtocolMessageKind kind, T message)
        where T : IProtocolValidatable
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        lock (_gate)
        {
            ThrowIfDisposed();
            var payload = BoundedJsonCodec.Serialize(message, _limits);
            var frameLength = checked(FrameOverheadBytes + payload.Length);
            if (frameLength > _limits.MaxFrameBytes)
                throw ProtocolErrors.Create(ProtocolErrorCode.FrameTooLarge, _channel, _direction, kind);

            var frame = new byte[frameLength];
            WriteHeader(frame, kind, payload.Length, _nextWriteSequence);
            payload.CopyTo(frame.AsSpan(HeaderBytes));
            var authenticatedLength = HeaderBytes + payload.Length;
            var tag = HMACSHA256.HashData(_sessionKey, frame.AsSpan(0, authenticatedLength));
            tag.CopyTo(frame.AsSpan(authenticatedLength));
            _nextWriteSequence = checked(_nextWriteSequence + 1);
            return frame;
        }
    }

    public T Decode<T>(
        ReadOnlySpan<byte> frame,
        ProtocolMessageKind expectedKind,
        CaptureCorrelation? expectedCorrelation = null)
        where T : IProtocolValidatable
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (frame.Length > _limits.MaxFrameBytes)
                throw ProtocolErrors.Create(ProtocolErrorCode.FrameTooLarge, _channel, _direction);
            if (frame.Length < FrameOverheadBytes)
                throw ProtocolErrors.Create(ProtocolErrorCode.TruncatedFrame, _channel, _direction);
            if (!frame[..Magic.Length].SequenceEqual(Magic))
                throw ProtocolErrors.Create(ProtocolErrorCode.InvalidHeader, _channel, _direction);

            var version = BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(4, 2));
            if (version != ProtocolVersion)
                throw ProtocolErrors.Create(ProtocolErrorCode.UnsupportedVersion, _channel, _direction);

            var channel = (ProtocolChannel)frame[6];
            var direction = (ProtocolDirection)frame[7];
            if (channel != _channel || direction != _direction)
            {
                throw ProtocolErrors.Create(
                    ProtocolErrorCode.ChannelOrDirectionMismatch,
                    _channel,
                    _direction);
            }

            var kind = (ProtocolMessageKind)BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(8, 2));
            if (kind != expectedKind)
                throw ProtocolErrors.Create(ProtocolErrorCode.UnexpectedMessageKind, _channel, _direction, kind);
            if (BinaryPrimitives.ReadUInt16LittleEndian(frame.Slice(10, 2)) != 0)
                throw ProtocolErrors.Create(ProtocolErrorCode.InvalidHeader, _channel, _direction, kind);

            var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(frame.Slice(12, 4));
            if (payloadLength < 0 || payloadLength > _limits.MaxPayloadBytes)
                throw ProtocolErrors.Create(ProtocolErrorCode.PayloadTooLarge, _channel, _direction, kind);
            var expectedLength = checked(FrameOverheadBytes + payloadLength);
            if (frame.Length < expectedLength)
                throw ProtocolErrors.Create(ProtocolErrorCode.TruncatedFrame, _channel, _direction, kind);
            if (frame.Length > expectedLength)
                throw ProtocolErrors.Create(ProtocolErrorCode.TrailingFrameData, _channel, _direction, kind);

            var sequence = BinaryPrimitives.ReadInt64LittleEndian(frame.Slice(16, 8));
            var authenticatedLength = HeaderBytes + payloadLength;
            Span<byte> expectedTag = stackalloc byte[AuthenticationTagBytes];
            HMACSHA256.HashData(_sessionKey, frame[..authenticatedLength], expectedTag);
            if (!CryptographicOperations.FixedTimeEquals(
                    expectedTag,
                    frame.Slice(authenticatedLength, AuthenticationTagBytes)))
            {
                throw ProtocolErrors.Create(
                    ProtocolErrorCode.AuthenticationFailed,
                    _channel,
                    _direction,
                    kind,
                    sequence);
            }
            if (sequence != _nextReadSequence)
            {
                throw ProtocolErrors.Create(
                    ProtocolErrorCode.SequenceMismatch,
                    _channel,
                    _direction,
                    kind,
                    sequence);
            }

            var message = BoundedJsonCodec.Deserialize<T>(frame.Slice(HeaderBytes, payloadLength), _limits);
            if (message is ICorrelatedProtocolMessage correlated)
            {
                if (expectedCorrelation is null)
                    throw ProtocolErrors.Create(ProtocolErrorCode.CorrelationMismatch, _channel, _direction, kind, sequence);
                correlated.Correlation.EnsureExact(expectedCorrelation);
            }
            else if (expectedCorrelation is not null)
            {
                throw ProtocolErrors.Create(ProtocolErrorCode.CorrelationMismatch, _channel, _direction, kind, sequence);
            }

            _nextReadSequence = checked(_nextReadSequence + 1);
            return message;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            CryptographicOperations.ZeroMemory(_sessionKey);
            _disposed = true;
        }
    }

    private void WriteHeader(Span<byte> frame, ProtocolMessageKind kind, int payloadLength, long sequence)
    {
        Magic.CopyTo(frame);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(4, 2), ProtocolVersion);
        frame[6] = (byte)_channel;
        frame[7] = (byte)_direction;
        BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(8, 2), (ushort)kind);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.Slice(10, 2), 0);
        BinaryPrimitives.WriteInt32LittleEndian(frame.Slice(12, 4), payloadLength);
        BinaryPrimitives.WriteInt64LittleEndian(frame.Slice(16, 8), sequence);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
