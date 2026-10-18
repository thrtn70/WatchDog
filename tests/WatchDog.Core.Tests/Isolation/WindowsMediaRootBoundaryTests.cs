using System.Runtime.InteropServices;
using WatchDog.CaptureIsolation;
using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.Core.Tests.Isolation;

public sealed partial class WindowsMediaRootBoundaryTests
{
    [Fact]
    public void Win64InteropLayoutsAndShareModeMatchMediaRootContract()
    {
        Assert.Equal(8, IntPtr.Size);
        Assert.Equal(8, Marshal.SizeOf<WindowsMediaRootApi.FileAttributeTagInfo>());
        Assert.Equal(24, Marshal.SizeOf<WindowsMediaRootApi.FileIdInfo>());
        Assert.Equal(0u, WindowsMediaRootApi.ShareReadWrite & 0x00000004u);
        Assert.Equal(0x02200000u,
            WindowsMediaRootApi.FileFlagBackupSemantics |
            WindowsMediaRootApi.FileFlagOpenReparsePoint);
    }

    [Theory]
    [InlineData("media", LocalMediaRootAdmissionStatus.InvalidPath)]
    [InlineData(@"\\server\share\media", LocalMediaRootAdmissionStatus.UnsupportedNamespace)]
    [InlineData(@"\\?\C:\media", LocalMediaRootAdmissionStatus.UnsupportedNamespace)]
    [InlineData(@"\\.\C:\media", LocalMediaRootAdmissionStatus.UnsupportedNamespace)]
    [InlineData(@"\??\C:\media", LocalMediaRootAdmissionStatus.UnsupportedNamespace)]
    [InlineData(@"C:\media:private", LocalMediaRootAdmissionStatus.AlternateDataStream)]
    public async Task LexicallyUnsafeRootsAreRejectedBeforeNativeAccess(
        string path,
        LocalMediaRootAdmissionStatus expectedStatus)
    {
        var api = new FakeWindowsMediaRootApi();
        await using var boundary = new WindowsMediaRootBoundary(api);

        var observation = await boundary.AdmitAsync(path, CancellationToken.None);

        Assert.Equal(expectedStatus, observation.Status);
        Assert.Null(observation.Identity);
        Assert.Null(observation.CanonicalPathDigest);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task CanceledAdmissionMakesNoNativeCall()
    {
        var api = new FakeWindowsMediaRootApi();
        await using var boundary = new WindowsMediaRootBoundary(api);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            boundary.AdmitAsync(@"C:\media", cancellation.Token).AsTask());

        Assert.Empty(api.Calls);
    }

    [Theory]
    [InlineData(4, LocalMediaRootAdmissionStatus.NetworkRoot)]
    [InlineData(2, LocalMediaRootAdmissionStatus.NonFixedRoot)]
    [InlineData(0, LocalMediaRootAdmissionStatus.NonFixedRoot)]
    public async Task OnlyFixedLocalDriveRootsAreAdmitted(
        int driveTypeValue,
        LocalMediaRootAdmissionStatus expectedStatus)
    {
        var api = new FakeWindowsMediaRootApi
        {
            DriveType = (WindowsMediaRootDriveType)driveTypeValue,
        };
        await using var boundary = new WindowsMediaRootBoundary(api);

        var observation = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);

        Assert.Equal(expectedStatus, observation.Status);
        Assert.Equal(["drive:C:\\"], api.Calls);
    }

    [Fact]
    public async Task VolumeRootIsRejectedWithoutOpeningAHandle()
    {
        var api = new FakeWindowsMediaRootApi();
        await using var boundary = new WindowsMediaRootBoundary(api);

        var observation = await boundary.AdmitAsync(@"C:\", CancellationToken.None);

        Assert.Equal(LocalMediaRootAdmissionStatus.VolumeRoot, observation.Status);
        Assert.Equal(["drive:C:\\"], api.Calls);
    }

    [Fact]
    public async Task AdmissionRetainsEveryComponentAndReturnsOnlyOpaqueIdentity()
    {
        var api = new FakeWindowsMediaRootApi();
        await using var boundary = new WindowsMediaRootBoundary(api);

        var observation = await boundary.AdmitAsync(
            @"C:\media\clips",
            CancellationToken.None);

        Assert.Equal(LocalMediaRootAdmissionStatus.Confirmed, observation.Status);
        Assert.NotNull(observation.Identity);
        Assert.NotEqual(Guid.Empty, observation.Identity!.OpaqueHandleIdentity);
        Assert.Equal(64, observation.CanonicalPathDigest!.Length);
        Assert.Equal([@"C:\", @"C:\media", @"C:\media\clips"], api.OpenedPaths);
        Assert.DoesNotContain(@"C:\media", observation.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(observation.CanonicalPathDigest, observation.ToString(), StringComparison.Ordinal);

        var reobserved = await boundary.ObserveAsync(
            observation.Identity,
            CancellationToken.None);
        Assert.Equal(LocalMediaRootAdmissionStatus.Confirmed, reobserved.Status);
        Assert.Equal(observation.Identity, reobserved.Identity);
        Assert.Equal(observation.CanonicalPathDigest, reobserved.CanonicalPathDigest);

        await boundary.ReleaseAsync(observation.Identity, CancellationToken.None);
        Assert.Equal(api.OpenedHandles.AsEnumerable().Reverse(), api.SuccessfullyClosedHandles);
    }

    [Fact]
    public async Task ReparseAncestorRejectsAndClosesRetainedPrefixInReverseOrder()
    {
        var api = new FakeWindowsMediaRootApi();
        api.OverrideForPath(@"C:\media", snapshot => snapshot with
        {
            FileAttributes = snapshot.FileAttributes |
                WindowsMediaRootApi.FileAttributeReparsePoint,
            ReparseTag = 0xA0000003,
        });
        await using var boundary = new WindowsMediaRootBoundary(api);

        var observation = await boundary.AdmitAsync(
            @"C:\media\clips",
            CancellationToken.None);

        Assert.Equal(LocalMediaRootAdmissionStatus.ReparsePoint, observation.Status);
        Assert.Equal([@"C:\", @"C:\media"], api.OpenedPaths);
        Assert.Equal(api.OpenedHandles.AsEnumerable().Reverse(), api.SuccessfullyClosedHandles);
    }

    [Fact]
    public async Task FinalPathMismatchRejectsSubstitutionAndClosesEveryHandle()
    {
        var api = new FakeWindowsMediaRootApi();
        api.OverrideForPath(@"C:\media", snapshot => snapshot with
        {
            FinalPath = @"\\?\C:\other",
        });
        await using var boundary = new WindowsMediaRootBoundary(api);

        var observation = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);

        Assert.Equal(LocalMediaRootAdmissionStatus.FinalPathMismatch, observation.Status);
        Assert.Equal(api.OpenedHandles.AsEnumerable().Reverse(), api.SuccessfullyClosedHandles);
    }

    [Fact]
    public async Task ChangedFileIdentityIsReportedAsStaleWithoutReleasingOwnership()
    {
        var api = new FakeWindowsMediaRootApi();
        await using var boundary = new WindowsMediaRootBoundary(api);
        var admitted = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);
        var finalHandle = api.OpenedHandles[^1];
        api.OverrideForHandle(finalHandle, snapshot => snapshot with
        {
            FileIdLow = snapshot.FileIdLow + 1,
        });

        var observation = await boundary.ObserveAsync(
            admitted.Identity!,
            CancellationToken.None);

        Assert.Equal(LocalMediaRootAdmissionStatus.Stale, observation.Status);
        Assert.Null(observation.Identity);
        Assert.Empty(api.SuccessfullyClosedHandles);
        await boundary.ReleaseAsync(admitted.Identity!, CancellationToken.None);
    }

    [Fact]
    public async Task ChangedFinalPathIsRejectedEvenWhenFileIdentityMatches()
    {
        var api = new FakeWindowsMediaRootApi();
        await using var boundary = new WindowsMediaRootBoundary(api);
        var admitted = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);
        var finalHandle = api.OpenedHandles[^1];
        api.OverrideForHandle(finalHandle, snapshot => snapshot with
        {
            FinalPath = @"\\?\C:\substituted",
        });

        var observation = await boundary.ObserveAsync(
            admitted.Identity!,
            CancellationToken.None);

        Assert.Equal(LocalMediaRootAdmissionStatus.FinalPathMismatch, observation.Status);
        await boundary.ReleaseAsync(admitted.Identity!, CancellationToken.None);
    }

    [Fact]
    public async Task FailedCloseRetainsExactLeaseAndCanBeRetried()
    {
        var api = new FakeWindowsMediaRootApi();
        await using var boundary = new WindowsMediaRootBoundary(api);
        var admitted = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);
        var finalHandle = api.OpenedHandles[^1];
        api.CloseFailures.Add(finalHandle);

        var exception = await Assert.ThrowsAsync<LocalMediaRootBoundaryException>(() =>
            boundary.ReleaseAsync(admitted.Identity!, CancellationToken.None).AsTask());

        Assert.Equal("media-root.close-failed", exception.FailureCode);
        var closing = await Assert.ThrowsAsync<LocalMediaRootBoundaryException>(() =>
            boundary.ObserveAsync(admitted.Identity!, CancellationToken.None).AsTask());
        Assert.Equal("media-root.closing", closing.FailureCode);
        api.CloseFailures.Remove(finalHandle);
        await boundary.ReleaseAsync(admitted.Identity!, CancellationToken.None);
        Assert.Equal(2, api.CloseAttempts.Count(handle => handle == finalHandle));
    }

    [Fact]
    public async Task FailedRejectionCleanupBlocksAdmissionUntilCloseSucceeds()
    {
        var api = new FakeWindowsMediaRootApi();
        api.OverrideForPath(@"C:\media", snapshot => snapshot with
        {
            FileAttributes = snapshot.FileAttributes |
                WindowsMediaRootApi.FileAttributeReparsePoint,
            ReparseTag = 0xA000000C,
        });
        api.FailAllCloses = true;
        var boundary = new WindowsMediaRootBoundary(api);

        var rejected = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);
        var blocked = await boundary.AdmitAsync(@"C:\other", CancellationToken.None);

        Assert.Equal(LocalMediaRootAdmissionStatus.ClosePending, rejected.Status);
        Assert.Equal(LocalMediaRootAdmissionStatus.ClosePending, blocked.Status);
        api.FailAllCloses = false;
        await boundary.DisposeAsync();
    }

    [Fact]
    public async Task DisposeClosesAllOwnedRootsAndIsIdempotent()
    {
        var api = new FakeWindowsMediaRootApi();
        var boundary = new WindowsMediaRootBoundary(api);
        var first = await boundary.AdmitAsync(@"C:\media", CancellationToken.None);
        var second = await boundary.AdmitAsync(@"C:\exports", CancellationToken.None);

        await boundary.DisposeAsync();
        await boundary.DisposeAsync();

        Assert.NotNull(first.Identity);
        Assert.NotNull(second.Identity);
        Assert.Equal(api.OpenedHandles.Count, api.SuccessfullyClosedHandles.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            boundary.ObserveAsync(first.Identity!, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task NativeBoundaryRetainsAndReobservesExistingFixedLocalDirectory()
    {
        var directory = Directory.CreateTempSubdirectory("watchdog-media-root-");
        try
        {
            await using var boundary = new WindowsMediaRootBoundary();

            var admitted = await boundary.AdmitAsync(directory.FullName, CancellationToken.None);

            Assert.Equal(LocalMediaRootAdmissionStatus.Confirmed, admitted.Status);
            Assert.NotNull(admitted.Identity);
            Assert.Equal(64, admitted.CanonicalPathDigest!.Length);

            var observed = await boundary.ObserveAsync(
                admitted.Identity,
                CancellationToken.None);
            Assert.Equal(LocalMediaRootAdmissionStatus.Confirmed, observed.Status);
            Assert.Equal(admitted.Identity, observed.Identity);
            Assert.Equal(admitted.CanonicalPathDigest, observed.CanonicalPathDigest);

            await boundary.ReleaseAsync(admitted.Identity, CancellationToken.None);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }


    private sealed class FakeWindowsMediaRootApi : IWindowsMediaRootApi
    {
        private readonly Dictionary<nint, WindowsMediaRootHandleSnapshot> _snapshots = [];
        private readonly Dictionary<string, Func<WindowsMediaRootHandleSnapshot,
            WindowsMediaRootHandleSnapshot>> _pathOverrides =
            new(StringComparer.OrdinalIgnoreCase);
        private long _nextHandle = 100;

        internal WindowsMediaRootDriveType DriveType { get; set; } =
            WindowsMediaRootDriveType.Fixed;
        internal List<string> Calls { get; } = [];
        internal List<string> OpenedPaths { get; } = [];
        internal List<nint> OpenedHandles { get; } = [];
        internal List<nint> CloseAttempts { get; } = [];
        internal List<nint> SuccessfullyClosedHandles { get; } = [];
        internal HashSet<nint> CloseFailures { get; } = [];
        internal bool FailNextClose { get; set; }
        internal bool FailAllCloses { get; set; }

        public WindowsMediaRootDriveType GetDriveType(string rootPath)
        {
            Calls.Add($"drive:{rootPath}");
            return DriveType;
        }

        public SafeWindowsHandle OpenDirectory(string path)
        {
            Calls.Add($"open:{path}");
            OpenedPaths.Add(path);
            var rawHandle = (nint)_nextHandle++;
            OpenedHandles.Add(rawHandle);
            var normalized = Path.TrimEndingDirectorySeparator(path);
            var finalPath = @"\\?\" + normalized;
            var snapshot = new WindowsMediaRootHandleSnapshot(
                WindowsMediaRootApi.FileAttributeDirectory,
                0,
                7,
                unchecked((ulong)rawHandle.ToInt64()),
                11,
                finalPath);
            if (_pathOverrides.TryGetValue(path, out var transform))
                snapshot = transform(snapshot);
            _snapshots.Add(rawHandle, snapshot);
            return new SafeWindowsHandle(rawHandle, this);
        }

        public WindowsMediaRootHandleSnapshot Observe(SafeWindowsHandle handle)
        {
            var rawHandle = handle.DangerousGetHandle();
            Calls.Add($"observe:{rawHandle}");
            return _snapshots[rawHandle];
        }

        public bool Close(nint handle)
        {
            Calls.Add($"close:{handle}");
            CloseAttempts.Add(handle);
            if (FailAllCloses)
                return false;
            if (FailNextClose)
            {
                FailNextClose = false;
                return false;
            }
            if (CloseFailures.Contains(handle))
                return false;
            SuccessfullyClosedHandles.Add(handle);
            return true;
        }

        internal void OverrideForPath(
            string path,
            Func<WindowsMediaRootHandleSnapshot, WindowsMediaRootHandleSnapshot> transform) =>
            _pathOverrides[path] = transform;

        internal void OverrideForHandle(
            nint handle,
            Func<WindowsMediaRootHandleSnapshot, WindowsMediaRootHandleSnapshot> transform) =>
            _snapshots[handle] = transform(_snapshots[handle]);
    }
}
