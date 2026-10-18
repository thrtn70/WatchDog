using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1NativeJournalObservationTests
{
    [Fact]
    public void AcquisitionRetainsReadObservationAfterWriterCustodyCloses()
    {
        var api = new FakeApi();
        var authority = new FakeAuthority();
        var result = Acquire(api, authority);

        var lease = Assert.IsType<FixtureB1NativeJournalObservationLease>(result.Lease);
        Assert.True(result.Succeeded);
        Assert.False(result.NativeAuthorityProven);
        Assert.False(result.NativeDurabilityProven);
        Assert.True(lease.AuthorizeLiveWriter().Admitted);
        Assert.True(lease.CloseWriterCustody().Complete);
        authority.Allowed = false;
        Assert.False(lease.AuthorizeLiveWriter().Admitted);
        var read = lease.ObserveRetainedStorageForRead();
        Assert.True(read.Admitted);
        Assert.Empty(read.FramesBytes!);
        Assert.Empty(read.AnchorBytes!);
        Assert.True(lease.Close().Complete);
        Assert.Equal(FixtureB1NativeJournalLeaseState.Closed, lease.State);
        Assert.Equal(api.CreatedHandleIds.Count, api.ClosedHandleIds.Count);
    }

    [Fact]
    public void LostSimulatedWriterAuthorityDoesNotEraseReadCustody()
    {
        var api = new FakeApi();
        var authority = new FakeAuthority();
        var lease = Assert.IsType<FixtureB1NativeJournalObservationLease>(
            Acquire(api, authority).Lease);

        authority.Allowed = false;
        Assert.False(lease.AuthorizeLiveWriter().Admitted);
        Assert.Equal(FixtureB1NativeJournalLeaseState.Poisoned, lease.State);
        Assert.False(lease.ObserveRetainedStorageForRead().Admitted);
        Assert.Equal(0, api.ReadAttempts);
        Assert.True(lease.CloseWriterCustody().Complete);
        Assert.True(lease.ObserveRetainedStorageForRead().Admitted);
        Assert.True(lease.Close().Complete);
    }

    [Fact]
    public void ByteReadFailureAfterMetadataPoisonsReadObservation()
    {
        var api = new FakeApi { FailRead = true };
        var lease = Assert.IsType<FixtureB1NativeJournalObservationLease>(
            Acquire(api, new FakeAuthority()).Lease);

        Assert.True(lease.CloseWriterCustody().Complete);
        var observation = lease.ObserveRetainedStorageForRead();
        Assert.False(observation.Admitted);
        Assert.Equal("fake-read-failed", observation.Failure!.Code);
        Assert.Equal(FixtureB1NativeJournalLeaseState.Poisoned, lease.State);
        Assert.True(lease.Close().Complete);
    }

    [Theory]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.BeforeOpenVolumeRoot)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.AfterOpenVolumeRoot)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.BeforeOpenAncestor)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.AfterOpenAncestor)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.BeforeCreatePrivateRoot)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.AfterCreatePrivateRoot)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.BeforeCreateFrames)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.AfterCreateFrames)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.BeforeCreateAnchor)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.AfterCreateAnchor)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.BeforeDuplicateFrames)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.AfterDuplicateFrames)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.BeforeDuplicateAnchor)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.AfterDuplicateAnchor)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.BeforeInitialObservation)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.BeforeInitialEnumeration)]
    [InlineData((int)FixtureB1NativeJournalFaultPoint.BeforeLeasePublication)]
    public void EveryAcquisitionFaultRetainsAndClosesCreatedHandles(
        int pointValue)
    {
        var api = new FakeApi();
        var point = (FixtureB1NativeJournalFaultPoint)pointValue;
        var result = FixtureB1NativeJournalObservation.Acquire(api, new FakeAuthority(),
            Session(), Options(), new SingleFault(point));

        Assert.False(result.Succeeded);
        Assert.Null(result.Lease);
        Assert.Equal(point, result.Failure!.FaultPoint);
        Assert.True(result.CleanupOwner.CloseAll().Complete);
        Assert.Equal(api.CreatedHandleIds.Count, api.ClosedHandleIds.Count);
    }

    [Fact]
    public void FailedCreateThatReturnsHandleKeepsUncertainCloseObligation()
    {
        var api = new FakeApi { FailCreateFramesWithHandle = true, FailClose = true };
        var result = Acquire(api, new FakeAuthority());

        Assert.False(result.Succeeded);
        Assert.Equal("native-journal.acquire-close-pending", result.Failure!.Code);
        Assert.NotEmpty(result.CleanupOwner.UncertainHandleIds);
        Assert.Contains(api.CreatedHandleIds.Last(), result.CleanupOwner.UncertainHandleIds);
        Assert.False(result.CleanupOwner.CloseAll().Complete);
        Assert.Equal(1, api.CloseAttempts[api.CreatedHandleIds.Last()]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectedReturnedHandleIdentityStillGetsClosed(bool emptyId)
    {
        var api = new FakeApi { BadDuplicateIdOnRead = true, EmptyDuplicateId = emptyId };
        var result = Acquire(api, new FakeAuthority());

        Assert.False(result.Succeeded);
        Assert.True(result.CleanupOwner.CloseAll().Complete);
        Assert.Equal(api.CreatedHandles.Count, api.ClosedHandles.Count);
        Assert.All(api.CreatedHandles,
            handle => Assert.Contains(api.ClosedHandles,
                closed => ReferenceEquals(closed, handle)));
    }

    [Theory]
    [InlineData("links")]
    [InlineData("reparse")]
    [InlineData("delete")]
    [InlineData("rights")]
    [InlineData("security")]
    [InlineData("stream")]
    [InlineData("no-stream")]
    [InlineData("duplicate-stream")]
    [InlineData("identity")]
    [InlineData("path")]
    [InlineData("extra-entry")]
    public void InitialObservationRejectsAdversarialStorage(string drift)
    {
        var api = new FakeApi { InitialDrift = drift };
        var result = Acquire(api, new FakeAuthority());

        Assert.False(result.Succeeded);
        Assert.Null(result.Lease);
        Assert.True(result.CleanupOwner.CloseAll().Complete);
        Assert.Equal(api.CreatedHandleIds.Count, api.ClosedHandleIds.Count);
    }

    [Fact]
    public void ReparseAncestorIsRejectedBeforePrivateRootCreation()
    {
        var api = new FakeApi { InitialDrift = "ancestor-reparse" };

        var result = Acquire(api, new FakeAuthority());

        Assert.False(result.Succeeded);
        Assert.Equal(0, api.PrivateRootCreates);
        Assert.Equal(api.CreatedHandles.Count, api.ClosedHandles.Count);
    }

    [Fact]
    public void PrivateRootReparseIsRejectedBeforeFileCreation()
    {
        var api = new FakeApi { InitialDrift = "private-root-reparse" };

        var result = Acquire(api, new FakeAuthority());

        Assert.False(result.Succeeded);
        Assert.Equal(1, api.PrivateRootCreates);
        Assert.Equal(0, api.FileCreates);
        Assert.Equal(api.CreatedHandles.Count, api.ClosedHandles.Count);
    }

    [Fact]
    public void ParentMoveDuringCreateRejectsAndRetainsCreatedRootCleanup()
    {
        var api = new FakeApi { MoveParentDuringCreate = true };

        var result = Acquire(api, new FakeAuthority());

        Assert.False(result.Succeeded);
        Assert.Equal("native-journal.final-path-mismatch", result.Failure?.Code);
        Assert.Equal(1, api.PrivateRootCreates);
        Assert.Equal(0, api.FileCreates);
        Assert.EndsWith(@"scratch-moved\b1", api.CreatedRootPath!,
            StringComparison.OrdinalIgnoreCase);
        Assert.True(result.CleanupOwner.CloseAll().Complete);
        Assert.Equal(api.CreatedHandles.Count, api.ClosedHandles.Count);
    }

    [Fact]
    public void LaterIdentityDriftPoisonsWriterAndReadObservation()
    {
        var api = new FakeApi();
        var lease = Assert.IsType<FixtureB1NativeJournalObservationLease>(
            Acquire(api, new FakeAuthority()).Lease);

        api.Frames!.FileIdLow++;
        Assert.False(lease.AuthorizeLiveWriter().Admitted);
        Assert.True(lease.CloseWriterCustody().Complete);
        Assert.False(lease.ObserveRetainedStorageForRead().Admitted);
        Assert.Equal(FixtureB1NativeJournalLeaseState.Poisoned, lease.State);
        Assert.True(lease.Close().Complete);
    }

    [Fact]
    public void ClosingWriterWithUncertainNativeResultPreventsReadAdmission()
    {
        var api = new FakeApi { FailClose = true };
        var lease = Assert.IsType<FixtureB1NativeJournalObservationLease>(
            Acquire(api, new FakeAuthority()).Lease);

        var close = lease.CloseWriterCustody();
        Assert.False(close.Complete);
        Assert.Equal(FixtureB1NativeJournalLeaseState.ClosePending, lease.State);
        Assert.False(lease.AuthorizeLiveWriter().Admitted);
        Assert.False(lease.ObserveRetainedStorageForRead().Admitted);
        Assert.False(lease.Close().Complete);
        Assert.NotEmpty(api.CloseAttempts);
        Assert.All(api.CloseAttempts.Values, count => Assert.Equal(1, count));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("name:stream")]
    [InlineData(".")]
    [InlineData("trailing.")]
    public void InvalidComponentIsRejectedBeforeNativeCall(string privateRoot)
    {
        var api = new FakeApi();
        var options = Options() with { PrivateRootName = privateRoot };
        var result = FixtureB1NativeJournalObservation.Acquire(
            api, new FakeAuthority(), Session(), options);

        Assert.False(result.Succeeded);
        Assert.Empty(api.CreatedHandleIds);
    }

    private static FixtureB1NativeJournalAcquisition Acquire(
        FakeApi api, FakeAuthority authority) =>
        FixtureB1NativeJournalObservation.Acquire(api, authority, Session(), Options());

    private static FixtureB1AuthoritySession Session() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);

    internal static FixtureB1NativeJournalAcquisitionOptions Options()
    {
        var ace = new FixtureB1NativeJournalAceObservation(
            FixtureB1NativeJournalAceKind.Allow, "S-1-5-18", 0x001F01FF, 0);
        var security = new FixtureB1NativeJournalSecurityPolicy(
            [1], "S-1-5-18", true, [ace]);
        return new(@"C:\", ["scratch"], "b1", "frames.bin", "anchor.bin",
            1024, 1024, security, security,
            new(0, (uint)FixtureB1NativeJournalHandleAccess.WriteDataOrAddFile,
                (uint)FixtureB1NativeJournalHandleAccess.ReadDataOrListDirectory,
                (uint)(FixtureB1NativeJournalHandleAccess.WriteDataOrAddFile |
                    FixtureB1NativeJournalHandleAccess.AppendDataOrAddSubdirectory |
                    FixtureB1NativeJournalHandleAccess.Delete |
                    FixtureB1NativeJournalHandleAccess.WriteDac |
                    FixtureB1NativeJournalHandleAccess.WriteOwner)));
    }

    private sealed class SingleFault(FixtureB1NativeJournalFaultPoint point)
        : IFixtureB1NativeJournalFaultInjector
    {
        public bool ShouldFail(FixtureB1NativeJournalFaultPoint candidate) =>
            candidate == point;
    }

    private sealed class FakeAuthority : IFixtureB1NativeJournalWriterAuthority
    {
        internal bool Allowed { get; set; } = true;
        public bool AuthorizeLiveWriter(FixtureB1AuthoritySession session, Guid leaseId) =>
            Allowed && leaseId != Guid.Empty && session.ConnectionGeneration == 1;
    }

    private sealed class FakeApi : IFixtureB1NativeJournalObservationApi
    {
        private ulong _nextFileId = 1;
        internal HashSet<Guid> CreatedHandleIds { get; } = [];
        internal HashSet<Guid> ClosedHandleIds { get; } = [];
        internal List<IFixtureB1NativeJournalHandle> CreatedHandles { get; } = [];
        internal List<IFixtureB1NativeJournalHandle> ClosedHandles { get; } = [];
        internal Dictionary<Guid, int> CloseAttempts { get; } = [];
        internal bool FailCreateFramesWithHandle { get; set; }
        internal bool FailClose { get; set; }
        internal bool FailRead { get; set; }
        internal int ReadAttempts { get; private set; }
        internal bool BadDuplicateIdOnRead { get; set; }
        internal bool EmptyDuplicateId { get; set; }
        internal string? InitialDrift { get; set; }
        internal StorageObject? Frames { get; private set; }
        internal int PrivateRootCreates { get; private set; }
        internal int FileCreates { get; private set; }
        internal bool MoveParentDuringCreate { get; set; }
        internal string? CreatedRootPath { get; private set; }

        public FixtureB1NativeJournalHandleResult OpenVolumeRoot(string canonicalDriveRoot) =>
            New(new(canonicalDriveRoot, true, _nextFileId++), false);

        public FixtureB1NativeJournalHandleResult OpenDirectoryRelative(
            IFixtureB1NativeJournalHandle parent, string singleComponentName) =>
            New(new(ChildPath(parent, singleComponentName), true, _nextFileId++), false);

        public FixtureB1NativeJournalHandleResult CreatePrivateDirectoryRelative(
            IFixtureB1NativeJournalHandle trustedParent, string singleComponentName,
            byte[] selfRelativeSecurityDescriptor)
        {
            PrivateRootCreates++;
            if (MoveParentDuringCreate)
                ((Handle)trustedParent).Object.Path += "-moved";
            CreatedRootPath = ChildPath(trustedParent, singleComponentName);
            return New(new(CreatedRootPath, true, _nextFileId++), false);
        }

        public FixtureB1NativeJournalHandleResult CreateFileRelative(
            IFixtureB1NativeJournalHandle root, string singleComponentName,
            byte[] selfRelativeSecurityDescriptor)
        {
            FileCreates++;
            var item = new StorageObject(ChildPath(root, singleComponentName), false,
                _nextFileId++);
            if (singleComponentName == "frames.bin")
                Frames = item;
            var result = New(item, false);
            return FailCreateFramesWithHandle && singleComponentName == "frames.bin"
                ? result with { FailureCode = "fake-create-failed-after-handle" }
                : result;
        }

        public FixtureB1NativeJournalHandleResult DuplicateReadOnly(
            IFixtureB1NativeJournalHandle source) => New(((Handle)source).Object, true,
                BadDuplicateIdOnRead ? EmptyDuplicateId ? Guid.Empty :
                    source.LocalHandleId : null);

        public FixtureB1NativeJournalObservationResult Observe(
            IFixtureB1NativeJournalHandle handle)
        {
            var item = ((Handle)handle).Object;
            var readOnly = ((Handle)handle).ReadOnly;
            var isFrame = item == Frames;
            var attributes = item.Directory ? 0x10u : 0x80u;
            var links = 1u;
            var pending = false;
            var access = readOnly ? 1u : item.Directory ? 1u : 3u;
            var owner = "S-1-5-18";
            var streams = new List<string> { "::$DATA" };
            var fileId = item.FileIdLow;
            var path = item.Path;
            if (InitialDrift == "ancestor-reparse" && path.EndsWith("\\scratch",
                    StringComparison.OrdinalIgnoreCase))
                attributes |= 0x400;
            if (InitialDrift == "private-root-reparse" && path.EndsWith("\\b1",
                    StringComparison.OrdinalIgnoreCase))
                attributes |= 0x400;
            if (isFrame)
            {
                switch (InitialDrift)
                {
                    case "links": links = 2; break;
                    case "reparse": attributes |= 0x400; break;
                    case "delete": pending = true; break;
                    case "rights": if (readOnly) access = 3; break;
                    case "security": owner = "S-1-1-0"; break;
                    case "stream": streams.Add(":hidden:$DATA"); break;
                    case "no-stream": streams.Clear(); break;
                    case "duplicate-stream": streams.Add("::$DATA"); break;
                    case "identity": if (readOnly) fileId++; break;
                    case "path": path += "-other"; break;
                }
            }
            var security = new FixtureB1NativeJournalSecurityObservation(owner, true,
                [new(FixtureB1NativeJournalAceKind.Allow, "S-1-5-18", 0x001F01FF, 0)]);
            return new(new(item.Directory ? FixtureB1NativeJournalObjectKind.Directory :
                    FixtureB1NativeJournalObjectKind.File, attributes, 0, 7, fileId, 0,
                    links, pending, 0, 0, access, path, security, streams), null);
        }

        public FixtureB1NativeJournalDirectoryEntriesResult EnumerateDirectoryEntries(
            IFixtureB1NativeJournalHandle directory) =>
            new(InitialDrift == "extra-entry"
                ? ["frames.bin", "anchor.bin", "extra"]
                : ["frames.bin", "anchor.bin"], null);

        public FixtureB1NativeJournalBytesResult ReadExact(
            IFixtureB1NativeJournalHandle file, int expectedLength)
        {
            ReadAttempts++;
            return FailRead ? new(null, "fake-read-failed") :
                new(new byte[expectedLength], null);
        }

        public FixtureB1NativeJournalCloseResult Close(IFixtureB1NativeJournalHandle handle)
        {
            var id = handle.LocalHandleId;
            CloseAttempts[id] = CloseAttempts.GetValueOrDefault(id) + 1;
            if (FailClose)
                return new(FixtureB1NativeJournalCloseDisposition.Uncertain,
                    "fake-close-uncertain");
            ClosedHandleIds.Add(id);
            ClosedHandles.Add(handle);
            return new(FixtureB1NativeJournalCloseDisposition.Closed, null);
        }

        private FixtureB1NativeJournalHandleResult New(
            StorageObject item, bool readOnly, Guid? localIdOverride = null)
        {
            var handle = new Handle(localIdOverride ?? Guid.NewGuid(), item, readOnly);
            CreatedHandleIds.Add(handle.LocalHandleId);
            CreatedHandles.Add(handle);
            return new(handle, null);
        }

        private static string ChildPath(IFixtureB1NativeJournalHandle parent, string name) =>
            ((Handle)parent).Object.Path.TrimEnd('\\') + "\\" + name;

        private sealed record Handle(Guid LocalHandleId, StorageObject Object, bool ReadOnly)
            : IFixtureB1NativeJournalHandle;

        internal sealed class StorageObject(string path, bool directory, ulong fileIdLow)
        {
            internal string Path { get; set; } = path;
            internal bool Directory { get; } = directory;
            internal ulong FileIdLow { get; set; } = fileIdLow;
        }
    }
}
