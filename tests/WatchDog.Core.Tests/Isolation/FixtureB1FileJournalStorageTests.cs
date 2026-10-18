using System.Security.AccessControl;
using System.Security.Principal;
using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1FileJournalStorageTests
{
    [Fact]
    public void ReopenPreservesFlushedFramesAndAnchor()
    {
        using var root = new IsolatedRoot();
        var session = NewSession();
        var journalId = Guid.NewGuid();
        FixtureB1JournalStorageLease lease;
        using (var storage = FixtureB1FileJournalStorage.Create(
                   root.Path, session, journalId, out lease))
        {
            Assert.True(storage.IsAdmitted(session, lease));
            Assert.True(storage.TryAppendAndFlush([1, 2, 3, 4]));
            Assert.True(storage.TryAdvanceAndFlushAnchor(
                new FixtureB1JournalAnchor(1, Enumerable.Repeat((byte)7, 32).ToArray())));
        }

        using var reopened = FixtureB1FileJournalStorage.Open(root.Path, session, lease);
        Assert.True(reopened.IsAdmitted(session, lease));
        Assert.True(reopened.IsReadOnly);
        Assert.False(reopened.TryAppendAndFlush([5]));
        Assert.Equal([1, 2, 3, 4], reopened.ReadFrames());
        var anchor = Assert.IsType<FixtureB1JournalAnchor>(reopened.ReadAnchor());
        Assert.Equal(1, anchor.Ordinal);
        Assert.Equal(Enumerable.Repeat((byte)7, 32), anchor.Digest);
        Assert.False(reopened.IsPoisoned);
    }

    [Fact]
    public void HeldLeaseDeniesAnotherWriter()
    {
        using var root = new IsolatedRoot();
        var session = NewSession();
        using var storage = FixtureB1FileJournalStorage.Create(
            root.Path, session, Guid.NewGuid(), out var lease);

        Assert.ThrowsAny<IOException>(() => FixtureB1FileJournalStorage.Open(
            root.Path, session, lease));
        Assert.ThrowsAny<IOException>(() => new FileStream(
            System.IO.Path.Combine(root.Path, "frames.bin"), FileMode.Open,
            FileAccess.ReadWrite, FileShare.None));
        Assert.True(storage.IsAdmitted(session, lease));
    }

    [Fact]
    public void OversizedFramePoisonsWriterWithoutChangingForensicBytes()
    {
        using var root = new IsolatedRoot();
        var session = NewSession();
        using var storage = FixtureB1FileJournalStorage.Create(
            root.Path, session, Guid.NewGuid(), out var lease);
        Assert.False(storage.TryAppendAndFlush(new byte[64 * 1024]));
        Assert.True(storage.IsPoisoned);
        Assert.False(storage.IsAdmitted(session, lease));
        Assert.Empty(storage.ReadFrames());
        Assert.Null(storage.ReadAnchor());
        Assert.False(storage.TryAppendAndFlush([1]));
    }

    [Theory]
    [InlineData(FixtureB1FileJournalFault.TornAppend, false)]
    [InlineData(FixtureB1FileJournalFault.WrittenButFalse, true)]
    public void AmbiguousAppendPreservesObservedBytesAndStopsWriter(
        FixtureB1FileJournalFault fault, bool complete)
    {
        using var root = new IsolatedRoot();
        var session = NewSession();
        var payload = Enumerable.Range(0, 80).Select(i => (byte)i).ToArray();
        FixtureB1JournalStorageLease lease;
        using (var storage = FixtureB1FileJournalStorage.Create(
                   root.Path, session, Guid.NewGuid(), out lease))
        {
            storage.NextFault = fault;
            Assert.False(storage.TryAppendAndFlush(payload));
            Assert.True(storage.IsPoisoned);
            Assert.Equal(complete ? payload : payload[..40], storage.ReadFrames());
            Assert.Null(storage.ReadAnchor());
            Assert.False(storage.TryAppendAndFlush([9]));
        }

        using var reopened = FixtureB1FileJournalStorage.Open(root.Path, session, lease);
        Assert.Equal(complete ? payload : payload[..40], reopened.ReadFrames());
    }

    [Theory]
    [InlineData(FixtureB1FileJournalFault.RefuseAnchor, false)]
    [InlineData(FixtureB1FileJournalFault.CommittedAnchorButFalse, true)]
    public void AmbiguousAnchorPreservesFramesAndAnyWrittenAnchor(
        FixtureB1FileJournalFault fault, bool anchorWritten)
    {
        using var root = new IsolatedRoot();
        var session = NewSession();
        FixtureB1JournalStorageLease lease;
        using (var storage = FixtureB1FileJournalStorage.Create(
                   root.Path, session, Guid.NewGuid(), out lease))
        {
            Assert.True(storage.TryAppendAndFlush([1, 2, 3]));
            storage.NextFault = fault;
            Assert.False(storage.TryAdvanceAndFlushAnchor(
                new FixtureB1JournalAnchor(1, new byte[32])));
            Assert.True(storage.IsPoisoned);
            Assert.Equal([1, 2, 3], storage.ReadFrames());
            Assert.Equal(anchorWritten, storage.ReadAnchor() is not null);
        }

        using var reopened = FixtureB1FileJournalStorage.Open(root.Path, session, lease);
        Assert.Equal([1, 2, 3], reopened.ReadFrames());
        Assert.Equal(anchorWritten, reopened.ReadAnchor() is not null);
    }

    [Fact]
    public void BroadAclChangeRevokesAdmissionWithoutDiscardingFiles()
    {
        using var root = new IsolatedRoot();
        var session = NewSession();
        using var storage = FixtureB1FileJournalStorage.Create(
            root.Path, session, Guid.NewGuid(), out var lease);
        Assert.True(storage.TryAppendAndFlush([1, 2, 3]));

        var directory = new DirectoryInfo(root.Path);
        var security = directory.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData, AccessControlType.Allow));
        directory.SetAccessControl(security);

        Assert.False(storage.IsAdmitted(session, lease));
        Assert.False(storage.TryAppendAndFlush([4]));
        Assert.True(storage.IsPoisoned);
        Assert.Equal(3, new FileInfo(System.IO.Path.Combine(root.Path, "frames.bin")).Length);
    }

    [Fact]
    public void CanonicalIntentReplaysAsCleanupOnlyAfterReopen()
    {
        using var root = new IsolatedRoot();
        var session = NewSession();
        var registry = new FixtureB1AuthorityRegistry(session, new AcceptObjectAuthority());
        FixtureB1JournalStorageLease lease;
        using (var storage = FixtureB1FileJournalStorage.Create(
                   root.Path, session, Guid.NewGuid(), out lease))
        {
            var journal = new FixtureB1FramedJournalContract(
                registry, lease, storage, new AcceptPeerAuthority(), storage);
            var token = registry.Register(new FixtureB1ExactObjectObservation(
                FixtureB1AuthorityObjectKind.RunnerJob,
                FixtureB1EndpointRole.Wrapper,
                FixtureB1EndpointRole.Wrapper,
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
                Retained: true, NonInheritable: true));
            var invocation = registry.BeginOriginal(token!, session);
            Assert.True(journal.TryRecordIntent(invocation!));
        }

        using var reopened = FixtureB1FileJournalStorage.Open(root.Path, session, lease);
        var inspection = reopened.Inspect(
            registry, session, lease, new AcceptPeerAuthority());
        Assert.Equal(FixtureB1JournalInspectionState.CleanupOnly, inspection.State);
        Assert.Single(inspection.VerifiedRecords);
        Assert.False(inspection.NativeAuthorityProven);
        Assert.False(inspection.NativeDurabilityProven);
    }

    [Fact]
    public void TornAnchorCannotMasqueradeAsEmptyOnReopen()
    {
        using var root = new IsolatedRoot();
        var session = NewSession();
        FixtureB1JournalStorageLease lease;
        using (var storage = FixtureB1FileJournalStorage.Create(
                   root.Path, session, Guid.NewGuid(), out lease))
        {
            Assert.True(storage.TryAppendAndFlush([1, 2, 3]));
            storage.NextFault = FixtureB1FileJournalFault.TornAnchor;
            Assert.False(storage.TryAdvanceAndFlushAnchor(
                new FixtureB1JournalAnchor(1, new byte[32])));
            Assert.Throws<IOException>(() => storage.ReadAnchor());
        }

        using var reopened = FixtureB1FileJournalStorage.Open(root.Path, session, lease);
        Assert.True(reopened.IsReadOnly);
        Assert.Throws<IOException>(() => reopened.ReadAnchor());
        Assert.Equal(FixtureB1JournalInspectionState.Corrupt,
            reopened.Inspect(null, session, lease, new AcceptPeerAuthority()).State);
        Assert.False(reopened.TryAppendAndFlush([4]));
        Assert.Equal(20, new FileInfo(System.IO.Path.Combine(root.Path, "anchor.bin")).Length);
    }

    [Theory]
    [InlineData("frames.bin")]
    [InlineData("anchor.bin")]
    public void ChildFileAclBroadeningBlocksReopen(string name)
    {
        using var root = new IsolatedRoot();
        var session = NewSession();
        FixtureB1JournalStorageLease lease;
        using (FixtureB1FileJournalStorage.Create(
                   root.Path, session, Guid.NewGuid(), out lease)) { }

        var file = new FileInfo(System.IO.Path.Combine(root.Path, name));
        var security = file.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.ReadData, AccessControlType.Allow));
        file.SetAccessControl(security);

        Assert.Throws<IOException>(() => FixtureB1FileJournalStorage.Open(
            root.Path, session, lease));
    }

    [Fact]
    public void SameSizeCorruptFrameReopensOnlyForForensicReads()
    {
        using var root = new IsolatedRoot();
        var session = NewSession();
        FixtureB1JournalStorageLease lease;
        using (var storage = FixtureB1FileJournalStorage.Create(
                   root.Path, session, Guid.NewGuid(), out lease))
        {
            Assert.True(storage.TryAppendAndFlush([1, 2, 3, 4]));
            Assert.True(storage.TryAdvanceAndFlushAnchor(
                new FixtureB1JournalAnchor(1, new byte[32])));
        }

        var framesPath = System.IO.Path.Combine(root.Path, "frames.bin");
        using (var file = new FileStream(framesPath, FileMode.Open, FileAccess.Write))
            file.WriteByte(9);
        using var reopened = FixtureB1FileJournalStorage.Open(root.Path, session, lease);
        Assert.Equal([9, 2, 3, 4], reopened.ReadFrames());
        Assert.False(reopened.TryAppendAndFlush([5]));
        Assert.Equal(4, new FileInfo(framesPath).Length);
    }

    [Fact]
    public void RootIdentityMetadataDriftStopsWritesAndKeepsBytes()
    {
        using var root = new IsolatedRoot();
        var session = NewSession();
        using var storage = FixtureB1FileJournalStorage.Create(
            root.Path, session, Guid.NewGuid(), out var lease);
        Assert.True(storage.TryAppendAndFlush([1, 2, 3]));

        Directory.SetCreationTimeUtc(root.Path, DateTime.UtcNow.AddDays(-7));

        Assert.False(storage.IsAdmitted(session, lease));
        Assert.False(storage.TryAppendAndFlush([4]));
        Assert.True(storage.IsPoisoned);
        Assert.Equal(3, new FileInfo(System.IO.Path.Combine(root.Path, "frames.bin")).Length);
    }

    private sealed class AcceptObjectAuthority : IFixtureB1ExactObjectAuthority
    {
        public bool IsExactRetainedObject(
            FixtureB1AuthoritySession session,
            FixtureB1ExactObjectObservation observation) => true;
    }

    private sealed class AcceptPeerAuthority : IFixtureB1JournalPeerAuthority
    {
        public bool IsExactAckAuthenticated(
            FixtureB1AuthoritySession session, Guid invocationId,
            Guid completionId, Guid sourceTokenId,
            FixtureB1AuthorityObjectKind sourceKind,
            FixtureB1EndpointRole origin) => true;
    }

    private static FixtureB1AuthoritySession NewSession() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);

    private sealed class IsolatedRoot : IDisposable
    {
        private static readonly string Parent = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "WatchDog-B1-file-tests");

        internal string Path { get; } = System.IO.Path.Combine(Parent, Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            var expectedParent = System.IO.Path.GetFullPath(Parent)
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test root escaped its private parent.");
            if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        }
    }
}
