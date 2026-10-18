using System.Collections.Concurrent;
using WatchDog.CaptureIsolation;

namespace WatchDog.Core.Tests.Isolation;

public sealed class CommandLedgerTests
{
    [Fact]
    public void DuplicatePendingAndCompletedCommandsAreIdempotent()
    {
        var ledger = Ledger(2);
        var identity = Identity(1);
        var commandId = Guid.NewGuid();

        Assert.Equal(CommandLedgerOutcome.Accepted, ledger.Accept(identity, commandId, Digest('a')).Outcome);
        Assert.Equal(CommandLedgerOutcome.DuplicatePending, ledger.Accept(identity, commandId, Digest('a')).Outcome);
        Assert.True(ledger.TryComplete(identity, "done"));
        var duplicate = ledger.Accept(identity, commandId, Digest('a'));
        Assert.Equal(CommandLedgerOutcome.DuplicateCompleted, duplicate.Outcome);
        Assert.Equal("done", duplicate.Result);
    }

    [Fact]
    public void SameHelperAndSequenceWithChangedCommandIsConflict()
    {
        var ledger = Ledger(2);
        var identity = Identity(1);
        _ = ledger.Accept(identity, Guid.NewGuid(), Digest('a'));

        Assert.Equal(
            CommandLedgerOutcome.Conflict,
            ledger.Accept(identity, Guid.NewGuid(), Digest('b')).Outcome);
    }

    [Fact]
    public void PendingCommandsAreNeverEvicted()
    {
        var ledger = Ledger(2);
        _ = ledger.Accept(Identity(1), Guid.NewGuid(), Digest('a'));
        _ = ledger.Accept(Identity(2), Guid.NewGuid(), Digest('b'));

        Assert.Equal(
            CommandLedgerOutcome.CapacityExhausted,
            ledger.Accept(Identity(3), Guid.NewGuid(), Digest('c')).Outcome);
    }

    [Fact]
    public void EvictedCompletedCommandIsRejectedInsteadOfReexecuted()
    {
        var ledger = Ledger(2);
        var firstCommand = Guid.NewGuid();
        _ = ledger.Accept(Identity(1), firstCommand, Digest('a'));
        Assert.True(ledger.TryComplete(Identity(1), "one"));
        _ = ledger.Accept(Identity(2), Guid.NewGuid(), Digest('b'));
        Assert.True(ledger.TryComplete(Identity(2), "two"));
        Assert.Equal(CommandLedgerOutcome.Accepted, ledger.Accept(Identity(3), Guid.NewGuid(), Digest('c')).Outcome);

        Assert.Equal(
            CommandLedgerOutcome.Evicted,
            ledger.Accept(Identity(1), firstCommand, Digest('a')).Outcome);
    }

    [Fact]
    public void ConcurrentDuplicatesHaveExactlyOneAdmission()
    {
        var ledger = Ledger(2);
        var commandId = Guid.NewGuid();
        var outcomes = new ConcurrentBag<CommandLedgerOutcome>();

        Parallel.For(0, 64, _ =>
            outcomes.Add(ledger.Accept(Identity(1), commandId, Digest('a')).Outcome));

        Assert.Equal(1, outcomes.Count(outcome => outcome == CommandLedgerOutcome.Accepted));
        Assert.Equal(63, outcomes.Count(outcome => outcome == CommandLedgerOutcome.DuplicatePending));
    }

    [Fact]
    public void SequenceBelowObservedHighWaterIsNeverAdmittedLater()
    {
        var ledger = Ledger(2);
        Assert.Equal(
            CommandLedgerOutcome.Accepted,
            ledger.Accept(Identity(3), Guid.NewGuid(), Digest('c')).Outcome);

        Assert.Equal(
            CommandLedgerOutcome.Evicted,
            ledger.Accept(Identity(2), Guid.NewGuid(), Digest('b')).Outcome);
    }

    private static HelperCommandIdentity Identity(long sequence) => new(
        Guid.Parse("20000000-0000-0000-0000-000000000002"),
        sequence);

    private static BoundedCommandResultLedger<string> Ledger(int capacity) => new(
        Identity(1).HelperInstanceId,
        capacity);

    private static string Digest(char value) => new(value, 64);
}
