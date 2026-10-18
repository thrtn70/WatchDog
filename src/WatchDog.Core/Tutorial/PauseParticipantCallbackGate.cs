namespace WatchDog.Core.Tutorial;

/// <summary>
/// Pure managed admission and drain boundary for copied pause participant evidence.
/// It owns no native callback, output, or release authority.
/// </summary>
public sealed class PauseParticipantCallbackGate
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeLeases;
    private bool _closed;
    private bool _copyReserved;
    private bool _hasInvalidEvidence;
    private PauseOutputAck? _acknowledgment;

    public PauseParticipantCallbackGate(PauseParticipantRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        Registration = registration;
    }

    public PauseParticipantRegistration Registration { get; }

    public bool IsClosed
    {
        get
        {
            lock (_sync)
                return _closed;
        }
    }

    public bool IsDrained
    {
        get
        {
            lock (_sync)
                return _closed && _activeLeases == 0;
        }
    }

    public bool HasInvalidEvidence
    {
        get
        {
            lock (_sync)
                return _hasInvalidEvidence;
        }
    }

    public bool TryEnter(
        PauseParticipantRegistration registration,
        out PauseParticipantCallbackLease? lease)
    {
        lock (_sync)
        {
            lease = null;
            if (!ReferenceEquals(registration, Registration) || _closed)
                return false;
            if (_activeLeases >= Registration.Streams.Count)
            {
                _hasInvalidEvidence = true;
                return false;
            }

            _activeLeases++;
            lease = new PauseParticipantCallbackLease(this);
            return true;
        }
    }

    public Task CloseAsync(CancellationToken waitCancellation = default)
    {
        var completeDrain = false;
        Task drain;
        lock (_sync)
        {
            _closed = true;
            completeDrain = _activeLeases == 0;
            drain = _drained.Task;
        }

        if (completeDrain)
            _drained.TrySetResult();

        return waitCancellation.CanBeCanceled
            ? drain.WaitAsync(waitCancellation)
            : drain;
    }

    public bool TryGetAcknowledgment(out PauseOutputAck? acknowledgment)
    {
        lock (_sync)
        {
            if (!_closed || _activeLeases != 0 || _hasInvalidEvidence || _acknowledgment is null)
            {
                acknowledgment = null;
                return false;
            }

            acknowledgment = _acknowledgment;
            return true;
        }
    }

    internal bool TryCopy(PauseParticipantCallbackLease lease, PauseOutputAck acknowledgment)
    {
        var completeDrain = false;
        var reserved = false;
        lock (_sync)
        {
            if (!lease.BeginCopy())
            {
                _hasInvalidEvidence = true;
                return false;
            }

            if (_copyReserved || _acknowledgment is not null)
            {
                _hasInvalidEvidence = true;
                if (lease.EndCopy())
                    completeDrain = ReleaseLeaseUnderLock();
            }
            else
            {
                _copyReserved = true;
                reserved = true;
            }
        }

        if (!reserved)
        {
            if (completeDrain)
                _drained.TrySetResult();
            return false;
        }

        PauseOutputAck? frozen = null;
        var freezeSucceeded = false;
        try
        {
            freezeSucceeded = TryFreeze(acknowledgment, out frozen);
        }
        catch
        {
            // Caller-owned callback storage is untrusted evidence. Publication below
            // converts every read failure into one latched invalid result.
        }

        var copied = false;
        lock (_sync)
        {
            _copyReserved = false;
            if (!freezeSucceeded || frozen is null || _hasInvalidEvidence || _acknowledgment is not null)
            {
                _hasInvalidEvidence = true;
            }
            else
            {
                _acknowledgment = frozen;
                copied = true;
            }

            if (lease.EndCopy())
                completeDrain = ReleaseLeaseUnderLock();
        }

        if (completeDrain)
            _drained.TrySetResult();
        return copied;
    }

    internal void Release(PauseParticipantCallbackLease lease)
    {
        var completeDrain = false;
        lock (_sync)
        {
            if (!lease.RequestDispose())
                return;
            if (!lease.CopyInProgress)
                completeDrain = ReleaseLeaseUnderLock();
        }

        if (completeDrain)
            _drained.TrySetResult();
    }

    private bool ReleaseLeaseUnderLock()
    {
        _activeLeases--;
        return _closed && _activeLeases == 0;
    }

    private bool TryFreeze(PauseOutputAck acknowledgment, out PauseOutputAck? frozen)
    {
        frozen = null;
        if (acknowledgment is null ||
            acknowledgment.OperationId != Registration.Command.OperationId ||
            acknowledgment.Sequence != Registration.Command.Sequence ||
            acknowledgment.Identity != Registration.Command.Identity ||
            acknowledgment.Action != Registration.Command.Action ||
            acknowledgment.Kind != Registration.Kind ||
            acknowledgment.OutputEpoch != Registration.OutputEpoch ||
            !Enum.IsDefined(acknowledgment.Effect) ||
            acknowledgment.Streams is null)
            return false;

        var expectedCount = Registration.Streams.Count;
        if (acknowledgment.Streams.Count != expectedCount)
            return false;

        var packets = new PauseStreamEffect[expectedCount];
        var matched = new bool[expectedCount];
        for (var packetIndex = 0; packetIndex < expectedCount; packetIndex++)
        {
            var packet = acknowledgment.Streams[packetIndex];
            if (packet?.Id is null)
                return false;

            var expectedIndex = -1;
            for (var streamIndex = 0; streamIndex < expectedCount; streamIndex++)
            {
                if (Registration.Streams[streamIndex].Id == packet.Id)
                {
                    expectedIndex = streamIndex;
                    break;
                }
            }

            if (expectedIndex < 0 || matched[expectedIndex] ||
                packet.TransformId != Registration.Streams[expectedIndex].InputClock.TransformId)
                return false;
            matched[expectedIndex] = true;
            packets[packetIndex] = new PauseStreamEffect(
                new PauseStreamId(packet.Id.Kind, packet.Id.Index),
                packet.TransformId, packet.InputBoundaryIndex);
        }

        frozen = new PauseOutputAck(
            acknowledgment.OperationId,
            acknowledgment.Sequence,
            acknowledgment.Identity,
            acknowledgment.Action,
            acknowledgment.Kind,
            acknowledgment.OutputEpoch,
            acknowledgment.Effect,
            acknowledgment.EffectiveBoundary,
            Array.AsReadOnly(packets));
        return true;
    }

    public override string ToString() => nameof(PauseParticipantCallbackGate);
}

public sealed class PauseParticipantCallbackLease : IDisposable
{
    private readonly PauseParticipantCallbackGate _gate;
    private bool _copyInProgress;
    private bool _disposeRequested;
    private bool _disposed;

    internal PauseParticipantCallbackLease(PauseParticipantCallbackGate gate)
    {
        _gate = gate;
    }

    public bool TryCopy(PauseOutputAck acknowledgment) =>
        _gate.TryCopy(this, acknowledgment);

    public void Dispose() => _gate.Release(this);

    internal bool CopyInProgress => _copyInProgress;

    internal bool BeginCopy()
    {
        if (_disposed || _copyInProgress)
            return false;
        _copyInProgress = true;
        return true;
    }

    internal bool EndCopy()
    {
        _copyInProgress = false;
        if (!_disposeRequested)
            return false;
        _disposed = true;
        return true;
    }

    internal bool RequestDispose()
    {
        if (_disposed || _disposeRequested)
            return false;
        _disposeRequested = true;
        if (!_copyInProgress)
            _disposed = true;
        return true;
    }

    public override string ToString() => nameof(PauseParticipantCallbackLease);
}
