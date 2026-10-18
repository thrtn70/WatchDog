namespace WatchDog.CaptureProtocol;

/// <summary>
/// Persistent parent-side bootstrap transcript. Directional sessions retain sequence state for
/// the entire helper-hello, parent-hello, helper-ack exchange; callers cannot reorder phases.
/// </summary>
public sealed class ParentBootstrapProtocol : IDisposable
{
    private readonly AuthenticatedProtocolSession _reader;
    private readonly AuthenticatedProtocolSession _writer;
    private CaptureCorrelation? _correlation;
    private ParentBootstrapPhase _phase = ParentBootstrapPhase.AwaitingHelperHello;
    private bool _disposed;

    public ParentBootstrapProtocol(
        ReadOnlySpan<byte> launchSecret,
        CaptureProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _reader = new(
            launchSecret,
            ProtocolChannel.Control,
            ProtocolDirection.HelperToParent,
            limits);
        _writer = new(
            launchSecret,
            ProtocolChannel.Control,
            ProtocolDirection.ParentToHelper,
            limits);
    }

    public HelperBootstrapHello AcceptHelperHello(
        ReadOnlySpan<byte> frame,
        CaptureCorrelation expectedCorrelation)
    {
        EnsurePhase(ParentBootstrapPhase.AwaitingHelperHello);
        var result = _reader.Decode<HelperBootstrapHello>(
            frame,
            ProtocolMessageKind.HelperHello,
            expectedCorrelation);
        _correlation = result.Correlation;
        _phase = ParentBootstrapPhase.ReadyForParentHello;
        return result;
    }

    public byte[] CreateParentHello(ParentBootstrapHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        EnsurePhase(ParentBootstrapPhase.ReadyForParentHello);
        hello.Correlation.EnsureExact(_correlation!);
        var result = _writer.Encode(ProtocolMessageKind.ParentHello, hello);
        _phase = ParentBootstrapPhase.AwaitingHelperAck;
        return result;
    }

    public HelperBootstrapAck AcceptHelperAck(
        ReadOnlySpan<byte> frame,
        CaptureCorrelation expectedCorrelation)
    {
        EnsurePhase(ParentBootstrapPhase.AwaitingHelperAck);
        _correlation!.EnsureExact(expectedCorrelation);
        var result = _reader.Decode<HelperBootstrapAck>(
            frame,
            ProtocolMessageKind.HelperAck,
            expectedCorrelation);
        _phase = ParentBootstrapPhase.Complete;
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _reader.Dispose();
        _writer.Dispose();
        _disposed = true;
    }

    private void EnsurePhase(ParentBootstrapPhase expected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_phase != expected)
            throw new InvalidOperationException("The bootstrap transcript phase is invalid.");
    }

    private enum ParentBootstrapPhase
    {
        AwaitingHelperHello,
        ReadyForParentHello,
        AwaitingHelperAck,
        Complete,
    }
}

/// <summary>
/// Persistent helper-side bootstrap transcript paired with ParentBootstrapProtocol.
/// </summary>
public sealed class HelperBootstrapProtocol : IDisposable
{
    private readonly AuthenticatedProtocolSession _writer;
    private readonly AuthenticatedProtocolSession _reader;
    private CaptureCorrelation? _correlation;
    private HelperBootstrapPhase _phase = HelperBootstrapPhase.ReadyForHelperHello;
    private bool _disposed;

    public HelperBootstrapProtocol(
        ReadOnlySpan<byte> launchSecret,
        CaptureProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _writer = new(
            launchSecret,
            ProtocolChannel.Control,
            ProtocolDirection.HelperToParent,
            limits);
        _reader = new(
            launchSecret,
            ProtocolChannel.Control,
            ProtocolDirection.ParentToHelper,
            limits);
    }

    public byte[] CreateHelperHello(HelperBootstrapHello hello)
    {
        ArgumentNullException.ThrowIfNull(hello);
        EnsurePhase(HelperBootstrapPhase.ReadyForHelperHello);
        var result = _writer.Encode(ProtocolMessageKind.HelperHello, hello);
        _correlation = hello.Correlation;
        _phase = HelperBootstrapPhase.AwaitingParentHello;
        return result;
    }

    public ParentBootstrapHello AcceptParentHello(
        ReadOnlySpan<byte> frame,
        CaptureCorrelation expectedCorrelation)
    {
        EnsurePhase(HelperBootstrapPhase.AwaitingParentHello);
        _correlation!.EnsureExact(expectedCorrelation);
        var result = _reader.Decode<ParentBootstrapHello>(
            frame,
            ProtocolMessageKind.ParentHello,
            expectedCorrelation);
        _phase = HelperBootstrapPhase.ReadyForHelperAck;
        return result;
    }

    public byte[] CreateHelperAck(HelperBootstrapAck ack)
    {
        ArgumentNullException.ThrowIfNull(ack);
        EnsurePhase(HelperBootstrapPhase.ReadyForHelperAck);
        ack.Correlation.EnsureExact(_correlation!);
        var result = _writer.Encode(ProtocolMessageKind.HelperAck, ack);
        _phase = HelperBootstrapPhase.Complete;
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _writer.Dispose();
        _reader.Dispose();
        _disposed = true;
    }

    private void EnsurePhase(HelperBootstrapPhase expected)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_phase != expected)
            throw new InvalidOperationException("The bootstrap transcript phase is invalid.");
    }

    private enum HelperBootstrapPhase
    {
        ReadyForHelperHello,
        AwaitingParentHello,
        ReadyForHelperAck,
        Complete,
    }
}
