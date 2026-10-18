using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureArtifactOwnershipTests
{
    [Fact]
    public void AdmitsExactRootAndCreatesOnlyAllowlistedNewComponent()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue(FixtureArtifactCreateDisposition.Created, "scenario",
            Identity(2, FixtureArtifactKind.Directory));

        var scenario = authority.CreateNew(authority.Root, "scenario",
            FixtureArtifactKind.Directory, FixtureArtifactLifetime.RetainedEvidence,
            CancellationToken.None);

        Assert.Equal(FixtureArtifactKind.Directory, scenario.Identity.Kind);
        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "SCENARIO", FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.RetainedEvidence, CancellationToken.None));
        Assert.Equal(1, native.CreateCalls);
    }

    [Fact]
    public void UnexpectedCaseAliasIsRejectedBeforeFirstNativeCall()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "SCENARIO", FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        Assert.Equal(0, native.CreateCalls);
    }

    [Fact]
    public void RootOrParentIdentityChangeRejectsBeforeCreate()
    {
        var native = new FakeNative();
        native.ObservedOverride = new FixtureArtifactIdentity(7, 1, 0,
            FixtureArtifactKind.Directory, true);
        Assert.Throws<FixtureArtifactContractException>(() => NewAuthority(native));

        native.ObservedOverride = null;
        var authority = NewAuthority(native);
        native.ObservedOverride = new FixtureArtifactIdentity(7, 1, 0,
            FixtureArtifactKind.Directory, true);
        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "scenario", FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        native.ObservedOverride = null;
        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        Assert.Equal(0, native.CreateCalls);
    }

    [Fact]
    public void ParentObservationFailurePoisonsFutureCreates()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.ThrowNextObserveFor = "root";

        Assert.Throws<InvalidOperationException>(() => authority.CreateNew(
            authority.Root, "scenario", FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        Assert.Equal(0, native.CreateCalls);
    }

    [Fact]
    public void UndefinedKindOrLifetimeRejectsBeforeCreate()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "scenario", (FixtureArtifactKind)99,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "scenario", FixtureArtifactKind.Directory,
            (FixtureArtifactLifetime)99, CancellationToken.None));
        Assert.Equal(0, native.CreateCalls);
    }

    [Fact]
    public void RetainedEvidenceCannotBeCreatedInsideCleanupOwnedScratch()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue(FixtureArtifactCreateDisposition.Created, "directory",
            Identity(2, FixtureArtifactKind.Directory));
        var scratch = authority.CreateNew(authority.Root, "scenario",
            FixtureArtifactKind.Directory, FixtureArtifactLifetime.Scratch,
            CancellationToken.None);

        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            scratch, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.RetainedEvidence, CancellationToken.None));
        Assert.Equal(1, native.CreateCalls);
    }

    [Theory]
    [InlineData("../scenario")]
    [InlineData("scenario/child")]
    [InlineData("scenario:stream")]
    [InlineData("CON.txt")]
    [InlineData("scenario.")]
    [InlineData("other")]
    public void RejectsMalformedOrUnlistedNameBeforeNativeCreate(string name)
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);

        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, name, FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        Assert.Equal(0, native.CreateCalls);
    }

    [Theory]
    [InlineData((int)FixtureArtifactCreateDisposition.OpenedExisting, true)]
    [InlineData((int)FixtureArtifactCreateDisposition.NoCreate, false)]
    [InlineData((int)FixtureArtifactCreateDisposition.Ambiguous, false)]
    public void UnexpectedOpenResultClosesHandleWithoutDeletingForeignObject(
        int dispositionValue, bool cleanupConfirmed)
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue((FixtureArtifactCreateDisposition)dispositionValue,
            "foreign", Identity(2, FixtureArtifactKind.File));

        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();

        Assert.Equal(cleanupConfirmed, authority.TryFinish());
        Assert.Contains("close:foreign", native.Events);
        Assert.DoesNotContain("delete:foreign", native.Events);
        if (!cleanupConfirmed)
            Assert.DoesNotContain("close:root", native.Events);
    }

    [Fact]
    public void CreatedWithoutHandleLeavesCleanupUncertain()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue(FixtureArtifactCreateDisposition.Created, null, null);

        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();
        Assert.False(authority.TryFinish());
        Assert.DoesNotContain("close:root", native.Events);
    }

    [Fact]
    public void ReturnedAliasOfOwnedRootIsNeverClosedOrDeleted()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.QueueAlias(FixtureArtifactCreateDisposition.Created, native.Root);

        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();
        Assert.False(authority.TryFinish());
        Assert.DoesNotContain("close:root", native.Events);
        Assert.DoesNotContain("delete:root", native.Events);
    }

    [Fact]
    public void CancellationBeforeCreateDoesNotCallNative()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => authority.CreateNew(
            authority.Root, "scenario", FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.Scratch, cancellation.Token));
        Assert.Equal(0, native.CreateCalls);
        native.Queue(FixtureArtifactCreateDisposition.Created, "scenario",
            Identity(2, FixtureArtifactKind.Directory));
        authority.CreateNew(authority.Root, "scenario", FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.Scratch, CancellationToken.None);
        Assert.Equal(1, native.CreateCalls);
    }

    [Fact]
    public void NativeCreateThrowLeavesUncertainMutation()
    {
        var native = new FakeNative { ThrowOnCreate = true };
        var authority = NewAuthority(native);
        Assert.Throws<InvalidOperationException>(() => authority.CreateNew(
            authority.Root, "scenario", FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();
        Assert.False(authority.TryFinish());
    }

    [Theory]
    [InlineData(2UL, 0UL, false)]
    [InlineData(2UL, 7UL, true)]
    [InlineData(0UL, 7UL, false)]
    public void InvalidNewIdentityClosesOnlyAndLeavesCleanupUncertain(
        ulong fileId, ulong volume, bool reparse)
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue(FixtureArtifactCreateDisposition.Created, "bad",
            new FixtureArtifactIdentity(volume, fileId, 0,
                FixtureArtifactKind.File, reparse));

        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();
        Assert.False(authority.TryFinish());
        Assert.DoesNotContain("delete:bad", native.Events);
        Assert.Contains("close:bad", native.Events);
        Assert.DoesNotContain("close:root", native.Events);
    }

    [Fact]
    public void CancellationAfterCreateKeepsNewScratchHandleForCleanup()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        using var cancellation = new CancellationTokenSource();
        native.Queue(FixtureArtifactCreateDisposition.Created, "scratch",
            Identity(2, FixtureArtifactKind.Directory));
        native.AfterCreate = cancellation.Cancel;

        Assert.Throws<OperationCanceledException>(() => authority.CreateNew(
            authority.Root, "scenario", FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.Scratch, cancellation.Token));
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();
        Assert.True(authority.TryFinish());
        Assert.Contains("delete:scratch", native.Events);
    }

    [Fact]
    public void CleanupWaitsForDrainAndReapThenDeletesScratchBottomUpWithRetry()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue(FixtureArtifactCreateDisposition.Created, "directory",
            Identity(2, FixtureArtifactKind.Directory));
        var directory = authority.CreateNew(authority.Root, "scenario",
            FixtureArtifactKind.Directory, FixtureArtifactLifetime.Scratch,
            CancellationToken.None);
        native.Queue(FixtureArtifactCreateDisposition.Created, "file",
            Identity(3, FixtureArtifactKind.File));
        authority.CreateNew(directory, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None);
        native.FailNextDeleteFor = "file";

        Assert.False(authority.TryFinish());
        authority.ConfirmChildrenReaped();
        Assert.False(authority.TryFinish());
        authority.ConfirmObserversDrained();
        Assert.False(authority.TryFinish());
        Assert.DoesNotContain("close:directory", native.Events);
        Assert.True(authority.TryFinish());
        Assert.True(native.Events.IndexOf("close:file") <
            native.Events.IndexOf("delete:directory"));
        Assert.True(native.Events.IndexOf("close:directory") <
            native.Events.IndexOf("close:root"));
    }

    [Fact]
    public void UnknownScratchChildPreventsDeletionAndCleanupConfirmation()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue(FixtureArtifactCreateDisposition.Created, "directory",
            Identity(2, FixtureArtifactKind.Directory));
        authority.CreateNew(authority.Root, "scenario", FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.Scratch, CancellationToken.None);
        native.NonemptyDirectories.Add("directory");
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();

        Assert.False(authority.TryFinish());
        Assert.DoesNotContain("delete:directory", native.Events);
        Assert.DoesNotContain("close:root", native.Events);
    }

    [Fact]
    public void ChangedScratchIdentityBeforeCleanupIsNotDeleteMarked()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue(FixtureArtifactCreateDisposition.Created, "file",
            Identity(2, FixtureArtifactKind.File));
        authority.CreateNew(authority.Root, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None);
        native.ObservedOverrides["file"] = new FixtureArtifactIdentity(7, 2, 0,
            FixtureArtifactKind.File, true);
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();

        Assert.False(authority.TryFinish());
        Assert.DoesNotContain("delete:file", native.Events);
        Assert.DoesNotContain("close:root", native.Events);
        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "scenario", FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
    }

    [Fact]
    public void ScratchCloseNeedsExactTerminalDeletionBeforeParentCloses()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue(FixtureArtifactCreateDisposition.Created, "file",
            Identity(2, FixtureArtifactKind.File));
        authority.CreateNew(authority.Root, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None);
        native.DeletionStatus = FixtureArtifactDeletionStatus.Ambiguous;
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();

        Assert.False(authority.TryFinish());
        Assert.Contains("close:file", native.Events);
        Assert.DoesNotContain("close:root", native.Events);
        native.DeletionStatus = FixtureArtifactDeletionStatus.Confirmed;
        Assert.True(authority.TryFinish());
        Assert.Equal(1, native.Events.Count(value => value == "delete:file"));
        Assert.Equal(1, native.Events.Count(value => value == "close:file"));
    }

    [Fact]
    public void CleanupStartFreezesNewCreatesEvenBeforeReapAndDrain()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        Assert.False(authority.TryFinish());
        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "scenario", FixtureArtifactKind.Directory,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        Assert.Equal(0, native.CreateCalls);
    }

    [Fact]
    public void FailedCloseRetainsMarkedHandleAndRetriesWithoutRemarking()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue(FixtureArtifactCreateDisposition.Created, "file",
            Identity(2, FixtureArtifactKind.File));
        authority.CreateNew(authority.Root, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None);
        native.FailNextCloseFor = "file";
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();

        Assert.False(authority.TryFinish());
        Assert.True(authority.TryFinish());
        Assert.Equal(1, native.Events.Count(value => value == "delete:file"));
        Assert.Equal(2, native.Events.Count(value => value == "close:file"));
    }

    [Fact]
    public void AmbiguousCloseIsNeverRetriedOrReportedAsCleanup()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue(FixtureArtifactCreateDisposition.Created, "file",
            Identity(2, FixtureArtifactKind.File));
        authority.CreateNew(authority.Root, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None);
        native.AmbiguousCloseFor = "file";
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();

        Assert.False(authority.TryFinish());
        Assert.False(authority.TryFinish());
        Assert.Equal(1, native.Events.Count(value => value == "close:file"));
        Assert.DoesNotContain("close:root", native.Events);
    }

    [Fact]
    public void UnexpectedHandleCloseFailureRemainsPendingForRetry()
    {
        var native = new FakeNative();
        var authority = NewAuthority(native);
        native.Queue(FixtureArtifactCreateDisposition.OpenedExisting, "foreign",
            Identity(2, FixtureArtifactKind.File));
        Assert.Throws<FixtureArtifactContractException>(() => authority.CreateNew(
            authority.Root, "record.json", FixtureArtifactKind.File,
            FixtureArtifactLifetime.Scratch, CancellationToken.None));
        native.FailNextCloseFor = "foreign";
        authority.ConfirmChildrenReaped();
        authority.ConfirmObserversDrained();

        Assert.False(authority.TryFinish());
        Assert.DoesNotContain("close:root", native.Events);
        Assert.True(authority.TryFinish());
        Assert.DoesNotContain("delete:foreign", native.Events);
    }

    [Fact]
    public void CleanupUncertaintyOverridesRequestedExitCode()
    {
        Assert.Equal(QualificationExitCode.CleanupIncomplete,
            FixtureArtifactOwnership.ExitCodeAfterCleanup(
                QualificationExitCode.Pass, cleanupConfirmed: false));
        Assert.Equal(QualificationExitCode.AssertionFailedCleanupConfirmed,
            FixtureArtifactOwnership.ExitCodeAfterCleanup(
                QualificationExitCode.AssertionFailedCleanupConfirmed,
                cleanupConfirmed: true));
    }

    private static FixtureArtifactOwnership NewAuthority(FakeNative native) =>
        new(native, native.Root, Identity(1, FixtureArtifactKind.Directory),
            ["scenario", "record.json"]);

    private static FixtureArtifactIdentity Identity(
        ulong fileId, FixtureArtifactKind kind) => new(7, fileId, 0, kind, false);

    private sealed record FakeHandle(string Name, nint RawValue) : IFixtureArtifactHandle;

    private sealed class FakeNative : IFixtureArtifactNative
    {
        private readonly Queue<FixtureArtifactCreateResult> _results = new();
        private readonly Dictionary<IFixtureArtifactHandle, FixtureArtifactIdentity> _identities = new();
        private int _nextHandle = 2;

        internal FakeNative() => _identities.Add(Root, Identity(1, FixtureArtifactKind.Directory));

        internal FakeHandle Root { get; } = new("root", 1);
        internal List<string> Events { get; } = [];
        internal HashSet<string> NonemptyDirectories { get; } = [];
        internal Dictionary<string, FixtureArtifactIdentity> ObservedOverrides { get; } = [];
        internal FixtureArtifactDeletionStatus DeletionStatus { get; set; } =
            FixtureArtifactDeletionStatus.Confirmed;
        internal string? FailNextDeleteFor { get; set; }
        internal string? FailNextCloseFor { get; set; }
        internal string? AmbiguousCloseFor { get; set; }
        internal FixtureArtifactIdentity? ObservedOverride { get; set; }
        internal bool ThrowOnCreate { get; set; }
        internal string? ThrowNextObserveFor { get; set; }
        internal Action? AfterCreate { get; set; }
        internal int CreateCalls { get; private set; }

        internal void Queue(
            FixtureArtifactCreateDisposition disposition,
            string? handleName,
            FixtureArtifactIdentity? identity)
        {
            FakeHandle? handle = handleName is null ? null : new(handleName, _nextHandle++);
            if (handle is not null && identity is not null)
                _identities.Add(handle, identity);
            _results.Enqueue(new(disposition, handle));
        }

        internal void QueueAlias(
            FixtureArtifactCreateDisposition disposition, FakeHandle handle) =>
            _results.Enqueue(new(disposition, handle));

        public FixtureArtifactCreateResult CreateNew(
            IFixtureArtifactHandle parent, string singleComponentName, FixtureArtifactKind kind)
        {
            CreateCalls++;
            if (ThrowOnCreate)
                throw new InvalidOperationException("Unknown create outcome.");
            var result = _results.Dequeue();
            AfterCreate?.Invoke();
            return result;
        }

        public FixtureArtifactIdentity Observe(IFixtureArtifactHandle handle)
        {
            var name = ((FakeHandle)handle).Name;
            if (ThrowNextObserveFor == name)
            {
                ThrowNextObserveFor = null;
                throw new InvalidOperationException("Observation unavailable.");
            }
            return ObservedOverride ??
                (ObservedOverrides.TryGetValue(name, out var observed)
                    ? observed : _identities[handle]);
        }

        public bool IsEmptyDirectory(
            IFixtureArtifactHandle handle, FixtureArtifactIdentity expected) =>
            !NonemptyDirectories.Contains(((FakeHandle)handle).Name);

        public bool MarkDelete(
            IFixtureArtifactHandle handle, FixtureArtifactIdentity expected)
        {
            var name = ((FakeHandle)handle).Name;
            Events.Add($"delete:{name}");
            if (FailNextDeleteFor != name)
                return true;
            FailNextDeleteFor = null;
            return false;
        }

        public FixtureArtifactCloseStatus Close(IFixtureArtifactHandle handle)
        {
            var name = ((FakeHandle)handle).Name;
            Events.Add($"close:{name}");
            if (AmbiguousCloseFor == name)
                return FixtureArtifactCloseStatus.Ambiguous;
            if (FailNextCloseFor == name)
            {
                FailNextCloseFor = null;
                return FixtureArtifactCloseStatus.StillOpen;
            }
            return FixtureArtifactCloseStatus.Closed;
        }

        public FixtureArtifactDeletionStatus ConfirmTerminalDeletion(
            IFixtureArtifactHandle parent,
            FixtureArtifactIdentity expectedParent,
            string singleComponentName,
            FixtureArtifactIdentity expectedChild)
        {
            Events.Add($"terminal:{singleComponentName}");
            return DeletionStatus;
        }
    }
}
