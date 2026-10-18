using System.Text;
using WatchDog.CaptureIsolation;

namespace WatchDog.Core.Tests.Isolation;

public sealed class RecoveryJournalTests
{
    [Fact]
    public async Task ReconciliationIsAtomicAndIdempotentAcrossOwnershipAndMediaStates()
    {
        var storage = new MemoryStorage();
        var journal = new CaptureRecoveryJournal(storage, TestLimits());
        var entry = Entry();
        await journal.UpsertAsync(entry);
        var observation = new RecoveryObservation(
            entry.Identity,
            RecoveryOwnershipState.CleanupPending,
            [new RecoveryMediaObservation(MediaId, RecoveryMediaState.Interrupted)]);

        Assert.True(await journal.ReconcileAsync(observation));
        var afterFirst = storage.Bytes!.ToArray();
        Assert.False(await journal.ReconcileAsync(observation));

        Assert.Equal(afterFirst, storage.Bytes);
        var snapshot = await journal.LoadAsync();
        var recovered = Assert.Single(snapshot.Entries);
        Assert.Equal(RecoveryOwnershipState.CleanupPending, recovered.OwnershipState);
        Assert.Equal(RecoveryMediaState.Interrupted, Assert.Single(recovered.Media).State);
    }

    [Fact]
    public async Task FailedAtomicWriteLeavesPreviousSnapshotReadable()
    {
        var storage = new MemoryStorage();
        var journal = new CaptureRecoveryJournal(storage, TestLimits());
        await journal.UpsertAsync(Entry());
        var before = storage.Bytes!.ToArray();
        storage.FailNextWrite = true;

        await Assert.ThrowsAsync<IOException>(
            () => journal.ReconcileAsync(new RecoveryObservation(
                Entry().Identity,
                RecoveryOwnershipState.CleanupPending,
                [])));

        Assert.Equal(before, storage.Bytes);
        Assert.Single((await journal.LoadAsync()).Entries);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"version\":99,\"entries\":[]}")]
    public async Task CorruptOrUnsupportedJournalFailsClosed(string content)
    {
        var storage = new MemoryStorage { Bytes = Encoding.UTF8.GetBytes(content) };
        var journal = new CaptureRecoveryJournal(storage, TestLimits());

        await Assert.ThrowsAsync<RecoveryJournalCorruptedException>(() => journal.LoadAsync());
    }

    [Theory]
    [InlineData("{\"version\":1,\"entries\":[null]}")]
    [InlineData("{\"version\":1,\"entries\":[{\"identity\":null,\"ownershipState\":0,\"media\":[]}]}")]
    [InlineData("{\"version\":1,\"entries\":[{\"identity\":{\"parentInstanceId\":\"10000000-0000-0000-0000-000000000001\",\"helperInstanceId\":\"20000000-0000-0000-0000-000000000002\",\"sessionId\":\"30000000-0000-0000-0000-000000000003\",\"requestId\":\"40000000-0000-0000-0000-000000000004\",\"generation\":5,\"planDigest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},\"ownershipState\":0,\"media\":null}]}")]
    [InlineData("{\"version\":1,\"entries\":[{\"identity\":{\"parentInstanceId\":\"10000000-0000-0000-0000-000000000001\",\"helperInstanceId\":\"20000000-0000-0000-0000-000000000002\",\"sessionId\":\"30000000-0000-0000-0000-000000000003\",\"requestId\":\"40000000-0000-0000-0000-000000000004\",\"generation\":5,\"planDigest\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"},\"ownershipState\":0,\"media\":[null]}]}")]
    public async Task NullMembersInRawJournalAreReportedAsCorruption(string content)
    {
        var storage = new MemoryStorage { Bytes = Encoding.UTF8.GetBytes(content) };
        var journal = new CaptureRecoveryJournal(storage, TestLimits());

        await Assert.ThrowsAsync<RecoveryJournalCorruptedException>(() => journal.LoadAsync());
    }

    [Fact]
    public async Task StateRegressionAndForeignMediaAreRejectedWithoutWrite()
    {
        var storage = new MemoryStorage();
        var journal = new CaptureRecoveryJournal(storage, TestLimits());
        var entry = Entry() with { OwnershipState = RecoveryOwnershipState.Assigned };
        await journal.UpsertAsync(entry);
        var writes = storage.WriteCount;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => journal.ReconcileAsync(new RecoveryObservation(
                entry.Identity,
                RecoveryOwnershipState.Launching,
                [])));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => journal.ReconcileAsync(new RecoveryObservation(
                entry.Identity,
                null,
                [new RecoveryMediaObservation(Guid.NewGuid(), RecoveryMediaState.Finalized)])));

        Assert.Equal(writes, storage.WriteCount);
    }

    private static readonly Guid MediaId = Guid.Parse("90000000-0000-0000-0000-000000000009");

    private static RecoveryJournalEntry Entry() => new(
        new RecoveryIdentity(
            Guid.Parse("10000000-0000-0000-0000-000000000001"),
            Guid.Parse("20000000-0000-0000-0000-000000000002"),
            Guid.Parse("30000000-0000-0000-0000-000000000003"),
            Guid.Parse("40000000-0000-0000-0000-000000000004"),
            5,
            ProtocolFramingTests.Digest('a')),
        RecoveryOwnershipState.Assigned,
        [new RecoveryMediaRecord(
            MediaId,
            Guid.Parse("a0000000-0000-0000-0000-00000000000a"),
            "stage-token",
            "final-token",
            RecoveryMediaState.Writing)]);

    private static RecoveryJournalLimits TestLimits() => new(16, 8, 16 * 1024, 128);

    private sealed class MemoryStorage : IRecoveryJournalStorage
    {
        public byte[]? Bytes { get; set; }
        public bool FailNextWrite { get; set; }
        public int WriteCount { get; private set; }

        public ValueTask<byte[]?> ReadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Bytes?.ToArray());

        public ValueTask WriteAtomicallyAsync(
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken = default)
        {
            WriteCount++;
            if (FailNextWrite)
            {
                FailNextWrite = false;
                throw new IOException("Injected write failure.");
            }

            Bytes = bytes.ToArray();
            return ValueTask.CompletedTask;
        }
    }
}
