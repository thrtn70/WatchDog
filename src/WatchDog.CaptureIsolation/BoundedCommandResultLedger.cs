namespace WatchDog.CaptureIsolation;

public readonly record struct HelperCommandIdentity(Guid HelperInstanceId, long CommandSequence)
{
    internal void Validate()
    {
        if (HelperInstanceId == Guid.Empty || CommandSequence <= 0)
            throw new ArgumentException("A helper command identity must be nonempty and positive.");
    }
}

public enum CommandLedgerOutcome
{
    Accepted,
    DuplicatePending,
    DuplicateCompleted,
    Conflict,
    Evicted,
    CapacityExhausted,
}

public sealed record CommandLedgerAdmission<TResult>(
    CommandLedgerOutcome Outcome,
    TResult? Result = default);

/// <summary>
/// Bounded idempotency ledger. Pending commands are never evicted; completed commands are
/// evicted oldest-first, and their sequence high-water prevents later re-execution.
/// </summary>
public sealed class BoundedCommandResultLedger<TResult>
{
    private readonly object _gate = new();
    private readonly Guid _helperInstanceId;
    private readonly int _capacity;
    private readonly Dictionary<HelperCommandIdentity, Entry> _entries = [];
    private readonly LinkedList<HelperCommandIdentity> _completedOrder = [];
    private long _evictedHighWater;
    private long _observedHighWater;

    public BoundedCommandResultLedger(Guid helperInstanceId, int capacity)
    {
        if (helperInstanceId == Guid.Empty)
            throw new ArgumentException("A helper instance ID is required.", nameof(helperInstanceId));
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _helperInstanceId = helperInstanceId;
        _capacity = capacity;
    }

    public CommandLedgerAdmission<TResult> Accept(
        HelperCommandIdentity identity,
        Guid commandId,
        string payloadDigest)
    {
        identity.Validate();
        if (commandId == Guid.Empty)
            throw new ArgumentException("A command ID is required.", nameof(commandId));
        if (!IsolationValueValidation.IsSha256Digest(payloadDigest))
            throw new ArgumentException("A SHA-256 payload digest is required.", nameof(payloadDigest));

        lock (_gate)
        {
            if (identity.HelperInstanceId != _helperInstanceId)
                return new(CommandLedgerOutcome.Conflict);
            if (_entries.TryGetValue(identity, out var existing))
            {
                if (existing.CommandId != commandId ||
                    !string.Equals(existing.PayloadDigest, payloadDigest, StringComparison.Ordinal))
                {
                    return new(CommandLedgerOutcome.Conflict);
                }

                return existing.Completed
                    ? new(CommandLedgerOutcome.DuplicateCompleted, existing.Result)
                    : new(CommandLedgerOutcome.DuplicatePending);
            }

            if (identity.CommandSequence <= _evictedHighWater)
                return new(CommandLedgerOutcome.Evicted);
            if (identity.CommandSequence <= _observedHighWater)
                return new(CommandLedgerOutcome.Evicted);

            if (_entries.Count >= _capacity && !TryEvictOldestCompleted())
                return new(CommandLedgerOutcome.CapacityExhausted);

            _entries.Add(identity, new Entry(commandId, payloadDigest));
            _observedHighWater = identity.CommandSequence;
            return new(CommandLedgerOutcome.Accepted);
        }
    }

    public bool TryComplete(HelperCommandIdentity identity, TResult result)
    {
        identity.Validate();
        lock (_gate)
        {
            if (!_entries.TryGetValue(identity, out var entry) || entry.Completed)
                return false;
            entry.Completed = true;
            entry.Result = result;
            entry.CompletedNode = _completedOrder.AddLast(identity);
            return true;
        }
    }

    private bool TryEvictOldestCompleted()
    {
        var node = _completedOrder.First;
        if (node is null)
            return false;
        _completedOrder.RemoveFirst();
        var identity = node.Value;
        _entries.Remove(identity);
        _evictedHighWater = Math.Max(_evictedHighWater, identity.CommandSequence);
        return true;
    }

    private sealed class Entry(Guid commandId, string payloadDigest)
    {
        public Guid CommandId { get; } = commandId;
        public string PayloadDigest { get; } = payloadDigest;
        public bool Completed { get; set; }
        public TResult? Result { get; set; }
        public LinkedListNode<HelperCommandIdentity>? CompletedNode { get; set; }
    }
}
