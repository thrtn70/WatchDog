using System.Runtime.InteropServices;
using System.Text;
using WatchDog.CaptureIsolation;
using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.Core.Tests.Isolation;

public sealed class WindowsMediaRootConsumerTests
{
    [Fact]
    public void X64NativeLayoutsAndCreateFlagsMatchRelativeOpenContract()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(16, Marshal.SizeOf<WindowsRelativeFileApi.UnicodeString>());
        Assert.Equal(48, Marshal.SizeOf<WindowsRelativeFileApi.ObjectAttributes>());
        Assert.Equal(16, Marshal.SizeOf<WindowsRelativeFileApi.IoStatusBlock>());
        Assert.Equal(0x00001040u,
            WindowsRelativeFileApi.ObjectCaseInsensitive |
            WindowsRelativeFileApi.ObjectDontReparse);
        Assert.Equal(0x00200060u,
            WindowsRelativeFileApi.FileSynchronousIoNonAlert |
            WindowsRelativeFileApi.FileNonDirectoryFile |
            WindowsRelativeFileApi.FileOpenReparsePoint);
        Assert.Equal(2u, WindowsRelativeFileApi.FileCreate);
    }

    [Theory]
    [InlineData(0x00000104)]
    [InlineData(0x00000118)]
    [InlineData(0x00000368)]
    [InlineData(unchecked((int)0x8000002D))]
    [InlineData(unchecked((int)0xC0000279))]
    [InlineData(unchecked((int)0xC0000280))]
    [InlineData(unchecked((int)0xC0000281))]
    [InlineData(unchecked((int)0xC000050B))]
    public void NativeReparseStatusesFailClosedAsReparsePoint(int status)
    {
        Assert.Equal(
            "media-file.reparse-point",
            WindowsRelativeFileApi.FailureCodeForStatus(status));
    }

    [Fact]
    public void PositiveReparseStatusWithValidHandleIsNotAUsableCreateResult()
    {
        Assert.False(WindowsRelativeFileApi.IsUsableCreateResult(0x00000104, (nint)123));
        Assert.True(WindowsRelativeFileApi.IsUsableCreateResult(0, (nint)123));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../probe.bin")]
    [InlineData("folder/probe.bin")]
    [InlineData("folder\\probe.bin")]
    [InlineData("probe.bin:stream")]
    [InlineData("*.bin")]
    [InlineData("probe.")]
    [InlineData("probe ")]
    [InlineData("CON")]
    [InlineData("lpt1.txt")]
    [InlineData("snowman-☃.bin")]
    public async Task UnsafeNamesAreRejectedBeforeRelativeNativeAccess(string name)
    {
        var rootApi = new FakeRootApi();
        var relativeApi = new FakeRelativeFileApi();
        await using var boundary = new WindowsMediaRootBoundary(rootApi, relativeApi);
        var root = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);

        var result = await boundary.CreateNewFileAsync(
            root.Identity!,
            name,
            CancellationToken.None);

        Assert.Equal(HandleRelativeMediaFileStatus.InvalidName, result.Status);
        Assert.Empty(relativeApi.CreatedRootHandles);
        await boundary.ReleaseAsync(root.Identity!, CancellationToken.None);
    }

    [Fact]
    public async Task FakeConsumerUsesExactRetainedRootAndOwnsReadWriteFlushLifecycle()
    {
        var rootApi = new FakeRootApi();
        var relativeApi = new FakeRelativeFileApi();
        await using var boundary = new WindowsMediaRootBoundary(rootApi, relativeApi);
        var root = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);
        var expectedRootHandle = rootApi.OpenedHandles[^1];

        var created = await boundary.CreateNewFileAsync(
            root.Identity!,
            "probe.bin",
            CancellationToken.None);
        var bytes = Encoding.UTF8.GetBytes("handle-relative");
        await boundary.WriteFileAsync(created.Identity!, bytes, CancellationToken.None);
        await boundary.FlushFileAsync(created.Identity!, CancellationToken.None);
        var read = await boundary.ReadFileAsync(created.Identity!, 1024, CancellationToken.None);

        Assert.Equal(HandleRelativeMediaFileStatus.Created, created.Status);
        Assert.Equal([expectedRootHandle], relativeApi.CreatedRootHandles);
        Assert.Equal(bytes, read);
        Assert.Equal(1, relativeApi.FlushCalls);
        Assert.DoesNotContain("probe.bin", created.ToString(), StringComparison.Ordinal);

        await boundary.ReleaseFileAsync(created.Identity!, CancellationToken.None);
        await boundary.ReleaseAsync(root.Identity!, CancellationToken.None);
    }

    [Fact]
    public async Task CancellationAfterNativeCreateDeletesAndClosesOwnedFile()
    {
        var rootApi = new FakeRootApi();
        using var cancellation = new CancellationTokenSource();
        var relativeApi = new FakeRelativeFileApi { OnCreate = cancellation.Cancel };
        await using var boundary = new WindowsMediaRootBoundary(rootApi, relativeApi);
        var root = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);

        var result = await boundary.CreateNewFileAsync(
            root.Identity!,
            "cancelled.bin",
            cancellation.Token);

        Assert.Equal(HandleRelativeMediaFileStatus.Cancelled, result.Status);
        Assert.Single(relativeApi.MarkDeleteCalls);
        Assert.Single(relativeApi.ClosedHandles);
        await boundary.ReleaseAsync(root.Identity!, CancellationToken.None);
    }

    [Fact]
    public async Task RejectedChildDeleteFailureRemainsOwnedAndBlocksUntilCleanupSucceeds()
    {
        var rootApi = new FakeRootApi();
        using var cancellation = new CancellationTokenSource();
        var relativeApi = new FakeRelativeFileApi();
        relativeApi.OnCreate = () =>
        {
            if (relativeApi.CreatedHandles.Count != 1)
                return;
            relativeApi.MarkDeleteFailures.Add(relativeApi.CreatedHandles[0]);
            cancellation.Cancel();
        };
        await using var boundary = new WindowsMediaRootBoundary(rootApi, relativeApi);
        var root = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);

        var rejected = await boundary.CreateNewFileAsync(
            root.Identity!,
            "cleanup-retry.bin",
            cancellation.Token);
        Assert.Equal(HandleRelativeMediaFileStatus.ClosePending, rejected.Status);
        var blocked = await boundary.CreateNewFileAsync(
            root.Identity!,
            "blocked.bin",
            CancellationToken.None);
        Assert.Equal(HandleRelativeMediaFileStatus.ClosePending, blocked.Status);
        Assert.Single(relativeApi.CreatedHandles);

        relativeApi.MarkDeleteFailures.Clear();
        var created = await boundary.CreateNewFileAsync(
            root.Identity!,
            "after-cleanup.bin",
            CancellationToken.None);
        Assert.Equal(HandleRelativeMediaFileStatus.Created, created.Status);
        await boundary.ReleaseFileAsync(created.Identity!, CancellationToken.None);
        await boundary.ReleaseAsync(root.Identity!, CancellationToken.None);
    }

    [Fact]
    public async Task RejectedChildCloseFailureRemainsOwnedAndBlocksUntilCleanupSucceeds()
    {
        var rootApi = new FakeRootApi();
        using var cancellation = new CancellationTokenSource();
        var relativeApi = new FakeRelativeFileApi();
        relativeApi.OnCreate = () =>
        {
            if (relativeApi.CreatedHandles.Count != 1)
                return;
            relativeApi.CloseFailures.Add(relativeApi.CreatedHandles[0]);
            cancellation.Cancel();
        };
        await using var boundary = new WindowsMediaRootBoundary(rootApi, relativeApi);
        var root = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);

        var rejected = await boundary.CreateNewFileAsync(
            root.Identity!,
            "cleanup-retry.bin",
            cancellation.Token);
        Assert.Equal(HandleRelativeMediaFileStatus.ClosePending, rejected.Status);
        var blocked = await boundary.CreateNewFileAsync(
            root.Identity!,
            "blocked.bin",
            CancellationToken.None);
        Assert.Equal(HandleRelativeMediaFileStatus.ClosePending, blocked.Status);
        Assert.Single(relativeApi.CreatedHandles);

        relativeApi.CloseFailures.Clear();
        var created = await boundary.CreateNewFileAsync(
            root.Identity!,
            "after-cleanup.bin",
            CancellationToken.None);
        Assert.Equal(HandleRelativeMediaFileStatus.Created, created.Status);
        await boundary.ReleaseFileAsync(created.Identity!, CancellationToken.None);
        await boundary.ReleaseAsync(root.Identity!, CancellationToken.None);
    }

    [Fact]
    public async Task MutationDuringCreateRejectsAndDeletesOwnedFile()
    {
        var rootApi = new FakeRootApi();
        var relativeApi = new FakeRelativeFileApi
        {
            OnCreate = () => rootApi.MarkReparse(@"C:\media"),
        };
        await using var boundary = new WindowsMediaRootBoundary(rootApi, relativeApi);
        var root = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);

        var result = await boundary.CreateNewFileAsync(
            root.Identity!,
            "mutated.bin",
            CancellationToken.None);

        Assert.Equal(HandleRelativeMediaFileStatus.ReparsePoint, result.Status);
        Assert.Single(relativeApi.MarkDeleteCalls);
        Assert.Single(relativeApi.ClosedHandles);
        rootApi.ClearReparse(@"C:\media");
        var reactivated = await Assert.ThrowsAsync<LocalMediaRootBoundaryException>(() =>
            boundary.CreateNewFileAsync(
                root.Identity!,
                "must-not-reactivate.bin",
                CancellationToken.None).AsTask());
        Assert.Equal("media-root.closing", reactivated.FailureCode);
        await boundary.ReleaseAsync(root.Identity!, CancellationToken.None);
    }

    [Fact]
    public async Task MutationDetectedBeforeCreateIrreversiblyRetiresRootLease()
    {
        var rootApi = new FakeRootApi();
        var relativeApi = new FakeRelativeFileApi();
        await using var boundary = new WindowsMediaRootBoundary(rootApi, relativeApi);
        var root = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);
        rootApi.MarkReparse(@"C:\media");

        var rejected = await boundary.CreateNewFileAsync(
            root.Identity!,
            "rejected.bin",
            CancellationToken.None);
        Assert.Equal(HandleRelativeMediaFileStatus.ReparsePoint, rejected.Status);
        Assert.Empty(relativeApi.CreatedHandles);

        rootApi.ClearReparse(@"C:\media");
        var reactivated = await Assert.ThrowsAsync<LocalMediaRootBoundaryException>(() =>
            boundary.CreateNewFileAsync(
                root.Identity!,
                "must-not-reactivate.bin",
                CancellationToken.None).AsTask());
        Assert.Equal("media-root.closing", reactivated.FailureCode);
        await boundary.ReleaseAsync(root.Identity!, CancellationToken.None);
    }

    [Fact]
    public async Task RootReleaseBecomesIrreversiblyClosingUntilChildAndRootCleanupFinish()
    {
        var rootApi = new FakeRootApi();
        var relativeApi = new FakeRelativeFileApi();
        await using var boundary = new WindowsMediaRootBoundary(rootApi, relativeApi);
        var root = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);
        var created = await boundary.CreateNewFileAsync(
            root.Identity!,
            "owned.bin",
            CancellationToken.None);

        var release = await Assert.ThrowsAsync<LocalMediaRootBoundaryException>(() =>
            boundary.ReleaseAsync(root.Identity!, CancellationToken.None).AsTask());
        Assert.Equal("media-root.files-open", release.FailureCode);
        var write = await Assert.ThrowsAsync<LocalMediaRootBoundaryException>(() =>
            boundary.WriteFileAsync(
                created.Identity!,
                new byte[] { 1 },
                CancellationToken.None).AsTask());
        Assert.Equal("media-root.closing", write.FailureCode);

        await boundary.ReleaseFileAsync(created.Identity!, CancellationToken.None);
        await boundary.ReleaseAsync(root.Identity!, CancellationToken.None);
    }

    [Fact]
    public async Task FailedChildCloseRemainsOwnedAndRetryable()
    {
        var rootApi = new FakeRootApi();
        var relativeApi = new FakeRelativeFileApi();
        await using var boundary = new WindowsMediaRootBoundary(rootApi, relativeApi);
        var root = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);
        var created = await boundary.CreateNewFileAsync(
            root.Identity!,
            "retry.bin",
            CancellationToken.None);
        var handle = relativeApi.CreatedHandles.Single();
        relativeApi.CloseFailures.Add(handle);

        var first = await Assert.ThrowsAsync<LocalMediaRootBoundaryException>(() =>
            boundary.ReleaseFileAsync(created.Identity!, CancellationToken.None).AsTask());
        Assert.Equal("media-file.close-failed", first.FailureCode);
        var closing = await Assert.ThrowsAsync<LocalMediaRootBoundaryException>(() =>
            boundary.ReadFileAsync(created.Identity!, 16, CancellationToken.None).AsTask());
        Assert.Equal("media-file.closing", closing.FailureCode);

        relativeApi.CloseFailures.Remove(handle);
        await boundary.ReleaseFileAsync(created.Identity!, CancellationToken.None);
        await boundary.ReleaseAsync(root.Identity!, CancellationToken.None);
        Assert.Equal(2, relativeApi.CloseAttempts.Count(value => value == handle));
    }

    [Fact]
    public async Task NativeConsumerCreatesReadsAndNeverOverwritesExactRootChild()
    {
        var directory = Directory.CreateTempSubdirectory("watchdog-relative-root-");
        var path = Path.Combine(directory.FullName, "probe.bin");
        var bytes = Encoding.UTF8.GetBytes("native-handle-relative");
        try
        {
            await using var boundary = new WindowsMediaRootBoundary();
            var root = await boundary.AdmitAsync(directory.FullName, CancellationToken.None);
            Assert.Equal(LocalMediaRootAdmissionStatus.Confirmed, root.Status);

            var created = await boundary.CreateNewFileAsync(
                root.Identity!,
                "probe.bin",
                CancellationToken.None);
            Assert.Equal(HandleRelativeMediaFileStatus.Created, created.Status);
            await boundary.WriteFileAsync(created.Identity!, bytes, CancellationToken.None);
            await boundary.FlushFileAsync(created.Identity!, CancellationToken.None);
            Assert.Equal(
                bytes,
                await boundary.ReadFileAsync(created.Identity!, 1024, CancellationToken.None));
            await boundary.ReleaseFileAsync(created.Identity!, CancellationToken.None);

            var collision = await boundary.CreateNewFileAsync(
                root.Identity!,
                "probe.bin",
                CancellationToken.None);
            Assert.Equal(HandleRelativeMediaFileStatus.AlreadyExists, collision.Status);
            await boundary.ReleaseAsync(root.Identity!, CancellationToken.None);

            Assert.Equal(bytes, File.ReadAllBytes(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FakeRootApi : IWindowsMediaRootApi
    {
        private readonly Dictionary<nint, WindowsMediaRootHandleSnapshot> _snapshots = [];
        private readonly Dictionary<string, nint> _handlesByPath =
            new(StringComparer.OrdinalIgnoreCase);
        private long _nextHandle = 100;

        internal List<nint> OpenedHandles { get; } = [];

        public WindowsMediaRootDriveType GetDriveType(string rootPath) =>
            WindowsMediaRootDriveType.Fixed;

        public SafeWindowsHandle OpenDirectory(string path)
        {
            var handle = (nint)_nextHandle++;
            var snapshot = new WindowsMediaRootHandleSnapshot(
                WindowsMediaRootApi.FileAttributeDirectory,
                0,
                7,
                unchecked((ulong)handle.ToInt64()),
                11,
                @"\\?\" + Path.TrimEndingDirectorySeparator(path));
            _snapshots.Add(handle, snapshot);
            _handlesByPath[path] = handle;
            OpenedHandles.Add(handle);
            return new(handle, this);
        }

        public WindowsMediaRootHandleSnapshot Observe(SafeWindowsHandle handle) =>
            _snapshots[handle.DangerousGetHandle()];

        public bool Close(nint handle) => true;

        internal void MarkReparse(string path)
        {
            var handle = _handlesByPath[path];
            _snapshots[handle] = _snapshots[handle] with
            {
                FileAttributes = _snapshots[handle].FileAttributes |
                    WindowsMediaRootApi.FileAttributeReparsePoint,
                ReparseTag = 0xA0000003,
            };
        }

        internal void ClearReparse(string path)
        {
            var handle = _handlesByPath[path];
            _snapshots[handle] = _snapshots[handle] with
            {
                FileAttributes = WindowsMediaRootApi.FileAttributeDirectory,
                ReparseTag = 0,
            };
        }
    }

    private sealed class FakeRelativeFileApi : IWindowsRelativeFileApi
    {
        private readonly Dictionary<nint, MemoryStream> _contents = [];
        private readonly Dictionary<nint, WindowsRelativeFileSnapshot> _snapshots = [];
        private long _nextHandle = 1000;

        internal Action? OnCreate { get; set; }
        internal List<nint> CreatedRootHandles { get; } = [];
        internal List<nint> CreatedHandles { get; } = [];
        internal List<nint> MarkDeleteCalls { get; } = [];
        internal List<nint> CloseAttempts { get; } = [];
        internal List<nint> ClosedHandles { get; } = [];
        internal HashSet<nint> MarkDeleteFailures { get; } = [];
        internal HashSet<nint> CloseFailures { get; } = [];
        internal int FlushCalls { get; private set; }

        public SafeWindowsHandle CreateNew(
            SafeWindowsHandle rootDirectory,
            string singleComponentName)
        {
            CreatedRootHandles.Add(rootDirectory.DangerousGetHandle());
            var handle = (nint)_nextHandle++;
            CreatedHandles.Add(handle);
            _contents.Add(handle, new MemoryStream());
            _snapshots.Add(handle, new(
                0,
                0,
                7,
                unchecked((ulong)handle.ToInt64()),
                19));
            OnCreate?.Invoke();
            return new(handle, this);
        }

        public WindowsRelativeFileSnapshot Observe(SafeWindowsHandle file) =>
            _snapshots[file.DangerousGetHandle()];

        public void Write(SafeWindowsHandle file, ReadOnlySpan<byte> content) =>
            _contents[file.DangerousGetHandle()].Write(content);

        public byte[] ReadAll(SafeWindowsHandle file, int maximumBytes)
        {
            var bytes = _contents[file.DangerousGetHandle()].ToArray();
            if (bytes.Length > maximumBytes)
                throw new WindowsRelativeFileNativeException("media-file.read-size-invalid");
            return bytes;
        }

        public void Flush(SafeWindowsHandle file) => FlushCalls++;

        public bool MarkDeleteOnClose(SafeWindowsHandle file)
        {
            MarkDeleteCalls.Add(file.DangerousGetHandle());
            return !MarkDeleteFailures.Contains(file.DangerousGetHandle());
        }

        public bool Close(nint handle)
        {
            CloseAttempts.Add(handle);
            if (CloseFailures.Contains(handle))
                return false;
            ClosedHandles.Add(handle);
            _contents.Remove(handle);
            _snapshots.Remove(handle);
            return true;
        }
    }
}
