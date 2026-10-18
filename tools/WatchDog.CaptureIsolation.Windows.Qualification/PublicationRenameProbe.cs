using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

/// <summary>
/// Fixture-only Win32 rename experiment. This is not a production publication boundary.
/// </summary>
internal static partial class PublicationRenameProbe
{
    private const int FileRenameInfo = 3;
    private const uint DirectoryReparsePoint = 0x00000400;
    private const string FinalComponent = "published.bin";
    // A fresh, independently reviewed destination/cleanup packet must precede any
    // further native rename call. This is deliberately not a runtime opt-in flag.
    private static readonly bool NativeRenameAdmitted = false;
    // Fixture-owned handles are local to these probes. Every rename and explicit close of
    // a rename source/root must pass this gate; SafeHandle AddRef alone does not exclude
    // SafeWindowsHandle.CloseOnce(), which directly closes the native handle.
    private static readonly object RenameLifetimeGate = new();
    private static readonly FixturePendingFileCleanup PendingFileCleanup = new(
        RenameLifetimeGate, WindowsRelativeFileApi.Instance.MarkDeleteOnClose);
    private static readonly FixtureRelativeFileCloseOwner ObserverCloseOwner = new(
        RenameLifetimeGate);
    private static readonly FixturePendingRootClose PendingRootClose = new(
        RenameLifetimeGate);

    internal static object LifetimeGate => RenameLifetimeGate;

    internal static Task<ScenarioResultRecord> RunAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        AssertNativeRenameAdmitted();
        var scratch = Path.GetFullPath(Path.Combine(
            options.ArtifactRoot, $"publication-scratch-{Guid.NewGuid():N}"));
        var rootPath = Path.GetFullPath(Path.Combine(scratch, "retained-root"));
        if (!Path.GetDirectoryName(scratch)!.Equals(
                Path.GetFullPath(options.ArtifactRoot), StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(rootPath)!.Equals(scratch, StringComparison.OrdinalIgnoreCase))
        {
            throw new QualificationAssertionException("fixture.publication.scratch-invalid");
        }

        if ((File.GetAttributes(options.ArtifactRoot) & FileAttributes.ReparsePoint) != 0)
            throw new QualificationAssertionException("fixture.publication.artifact-root-reparse");
        SafeWindowsHandle? root = null;
        SafeWindowsHandle? workingDirectoryRoot = null;
        SafeWindowsHandle? published = null;
        SafeWindowsHandle? collisionSource = null;
        WindowsMediaRootHandleSnapshot? admittedRoot = null;
        WindowsMediaRootHandleSnapshot? workingDirectoryIdentity = null;
        var observerCloseOwner = ObserverCloseOwner;
        if (!observerCloseOwner.TryClosePending())
            throw new QualificationCleanupException("fixture.relative-observer.close-pending");
        if (!PendingRootClose.TryDrain())
            throw new QualificationCleanupException("fixture.publication.root-close-pending");
        if (!PendingFileCleanup.TryDrain())
            throw new QualificationCleanupException("fixture.publication.file-cleanup-pending");
        var cleanupConfirmed = true;
        var scratchCreationAttempted = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssertRenameLayout();
            scratchCreationAttempted = true;
            Directory.CreateDirectory(rootPath);
            if ((File.GetAttributes(scratch) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
            {
                throw new QualificationCleanupException("fixture.publication.scratch-reparse");
            }
            root = WindowsMediaRootApi.Instance.OpenDirectory(rootPath);
            admittedRoot = WindowsMediaRootApi.Instance.Observe(root);
            if ((admittedRoot.FileAttributes & DirectoryReparsePoint) != 0)
                throw new QualificationAssertionException("fixture.publication.root-reparse");

            var expected = Encoding.UTF8.GetBytes("watchdog-publication-probe-v1");
            published = WindowsRelativeFileApi.Instance.CreateNew(root, "temporary-one.bin");
            WindowsRelativeFileApi.Instance.Write(published, expected);
            WindowsRelativeFileApi.Instance.Flush(published);
            if (!CryptographicOperations.FixedTimeEquals(
                    expected, WindowsRelativeFileApi.Instance.ReadAll(published, 4096)))
            {
                throw new QualificationAssertionException("fixture.publication.seal-mismatch");
            }
            var before = WindowsRelativeFileApi.Instance.Observe(published);
            cancellationToken.ThrowIfCancellationRequested();
            AssertContainedWorkingDirectory(options.ArtifactRoot);
            workingDirectoryRoot = WindowsMediaRootApi.Instance.OpenDirectory(
                Environment.CurrentDirectory);
            workingDirectoryIdentity = WindowsMediaRootApi.Instance.Observe(workingDirectoryRoot);
            RequireAbsent(workingDirectoryRoot, workingDirectoryIdentity,
                "published.bin", observerCloseOwner);
            var first = TryRename(published, root, "published.bin");
            AssertContainedWorkingDirectory(options.ArtifactRoot);
            if (!first.Success)
            {
                artifacts.Log("publication-rename-call-failed", FailureCategory(first.Error));
                throw new QualificationAssertionException("fixture.publication.rename-unavailable");
            }
            var after = WindowsRelativeFileApi.Instance.Observe(published);
            if (!SameFile(before, after) ||
                !EntryAbsent(root, admittedRoot, "temporary-one.bin", observerCloseOwner) ||
                !EntryMatches(root, admittedRoot, "published.bin", before,
                    expected, observerCloseOwner) ||
                !EntryAbsent(workingDirectoryRoot, workingDirectoryIdentity,
                    "published.bin", observerCloseOwner))
            {
                throw new QualificationAssertionException("fixture.publication.identity-or-bytes");
            }
            artifacts.Log("publication-nonnull-root-rename-observed");

            cancellationToken.ThrowIfCancellationRequested();
            var losingBytes = Encoding.UTF8.GetBytes("must-not-replace-existing-published-file");
            collisionSource = WindowsRelativeFileApi.Instance.CreateNew(root, "temporary-two.bin");
            WindowsRelativeFileApi.Instance.Write(collisionSource, losingBytes);
            WindowsRelativeFileApi.Instance.Flush(collisionSource);
            var loserBefore = WindowsRelativeFileApi.Instance.Observe(collisionSource);
            AssertContainedWorkingDirectory(options.ArtifactRoot);
            var collision = TryRename(
                collisionSource, root, "published.bin");
            AssertContainedWorkingDirectory(options.ArtifactRoot);
            if (collision.Success || collision.Error is not (80 or 183) ||
                !SameFile(loserBefore, WindowsRelativeFileApi.Instance.Observe(collisionSource)) ||
                !EntryMatches(root, admittedRoot, "published.bin", before,
                    expected, observerCloseOwner) ||
                !CryptographicOperations.FixedTimeEquals(
                    losingBytes, WindowsRelativeFileApi.Instance.ReadAll(collisionSource, 4096)) ||
                !EntryMatches(root, admittedRoot, "temporary-two.bin", loserBefore,
                    losingBytes, observerCloseOwner) ||
                !EntryAbsent(workingDirectoryRoot, workingDirectoryIdentity,
                    "published.bin", observerCloseOwner))
            {
                throw new QualificationAssertionException("fixture.publication.collision-unsafe");
            }
            artifacts.Log("publication-no-replace-confirmed", FailureCategory(collision.Error));
            if (!SameRoot(admittedRoot, WindowsMediaRootApi.Instance.Observe(root)))
                throw new QualificationAssertionException("fixture.publication.root-changed");

            return Task.FromResult(new ScenarioResultRecord(
                options.Scenario!.Value.ToString(), true, "passed", 0, true));
        }
        finally
        {
            var handlesClosed = CleanupFile(collisionSource) & CleanupFile(published);
            handlesClosed &= observerCloseOwner.TryClosePending();
            handlesClosed &= CloseRoot(workingDirectoryRoot);
            handlesClosed &= CloseRoot(root);
            cleanupConfirmed &= handlesClosed;
            if (cleanupConfirmed)
                cleanupConfirmed = admittedRoot is null
                    ? !scratchCreationAttempted
                    : DeleteEmptyScratch(scratch, rootPath, admittedRoot);
            if (!cleanupConfirmed)
                throw new QualificationCleanupException("fixture.publication.cleanup-uncertain");
        }
    }

    internal static bool CleanupFile(SafeWindowsHandle? file)
        => PendingFileCleanup.TryCleanup(file);

    internal static bool CloseRoot(SafeWindowsHandle? root)
    {
        var closed = PendingRootClose.TryClose(root);
        return closed & PendingFileCleanup.TryDrain();
    }

    internal static bool RetryPendingRootClose() => PendingRootClose.TryDrain();

    internal static bool RetryPendingFileCleanup() => PendingFileCleanup.TryDrain();

    internal static void AssertNativeRenameAdmitted()
    {
        if (!NativeRenameAdmitted)
            throw new QualificationAssertionException("fixture.publication.native-not-admitted");
    }

    private static bool DeleteEmptyScratch(
        string scratch,
        string rootPath,
        WindowsMediaRootHandleSnapshot admittedRoot)
    {
        try
        {
            if ((File.GetAttributes(scratch) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }
            var observedRoot = WindowsMediaRootApi.Instance.OpenDirectory(rootPath);
            bool matches;
            bool closed;
            try
            {
                matches = SameRoot(admittedRoot, WindowsMediaRootApi.Instance.Observe(observedRoot));
            }
            finally
            {
                closed = CloseRoot(observedRoot);
            }
            if (!matches || !closed)
            {
                return false;
            }
            Directory.Delete(rootPath, recursive: false);
            Directory.Delete(scratch, recursive: false);
            return !Directory.Exists(scratch);
        }
        catch
        {
            return false;
        }
    }

    internal static bool SameRoot(
        WindowsMediaRootHandleSnapshot left,
        WindowsMediaRootHandleSnapshot right) =>
        left.VolumeSerialNumber == right.VolumeSerialNumber &&
        left.FileIdLow == right.FileIdLow && left.FileIdHigh == right.FileIdHigh &&
        (right.FileAttributes & DirectoryReparsePoint) == 0;

    internal static bool SameFile(
        WindowsRelativeFileSnapshot left,
        WindowsRelativeFileSnapshot right) =>
        left.VolumeSerialNumber == right.VolumeSerialNumber &&
        left.FileIdLow == right.FileIdLow && left.FileIdHigh == right.FileIdHigh &&
        (right.FileAttributes & DirectoryReparsePoint) == 0;

    private static void RequireAbsent(
        SafeWindowsHandle root,
        WindowsMediaRootHandleSnapshot admittedRoot,
        string name,
        FixtureRelativeFileCloseOwner observerCloseOwner)
    {
        if (!EntryAbsent(root, admittedRoot, name, observerCloseOwner))
            throw new QualificationAssertionException("fixture.publication.unexpected-entry");
    }

    private static bool EntryAbsent(
        SafeWindowsHandle root,
        WindowsMediaRootHandleSnapshot admittedRoot,
        string name,
        FixtureRelativeFileCloseOwner observerCloseOwner)
    {
        var observed = FixtureRelativeFileObserver.ObserveExisting(
            root, admittedRoot, name, observerCloseOwner);
        return observed.State == FixtureRelativeFileState.Absent &&
            observed.Identity is null && observed.Bytes is null;
    }

    private static bool EntryMatches(
        SafeWindowsHandle root,
        WindowsMediaRootHandleSnapshot admittedRoot,
        string name,
        WindowsRelativeFileSnapshot expectedIdentity,
        byte[] expectedBytes,
        FixtureRelativeFileCloseOwner observerCloseOwner)
    {
        var observed = FixtureRelativeFileObserver.ObserveExisting(
            root, admittedRoot, name, observerCloseOwner);
        return observed.State == FixtureRelativeFileState.Present &&
            observed.Identity is not null && observed.Bytes is not null &&
            SameFile(expectedIdentity, observed.Identity) &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, observed.Bytes);
    }

    internal static string FailureCategory(int error) => error switch
    {
        0 => "fixture.publication.no-error",
        5 => "fixture.publication.access-denied",
        80 or 183 => "fixture.publication.destination-exists",
        32 => "fixture.publication.share-violation",
        _ when error is >= 1 and <= ushort.MaxValue => $"fixture.publication.win32-{error}",
        _ => "fixture.publication.native-rejected",
    };

    internal static void AssertRenameLayout()
    {
        if (IntPtr.Size != 8 || Marshal.SizeOf<FileRenameInfoLayout>() != 24 ||
            Marshal.OffsetOf<FileRenameInfoLayout>(nameof(FileRenameInfoLayout.Flags)).ToInt32() != 0 ||
            Marshal.OffsetOf<FileRenameInfoLayout>(nameof(FileRenameInfoLayout.RootDirectory)).ToInt32() != 8 ||
            Marshal.OffsetOf<FileRenameInfoLayout>(nameof(FileRenameInfoLayout.FileNameLength)).ToInt32() != 16 ||
            Marshal.OffsetOf<FileRenameInfoLayout>(nameof(FileRenameInfoLayout.FileName)).ToInt32() != 20)
        {
            throw new QualificationAssertionException("fixture.publication.abi-mismatch");
        }
    }

    internal static void AssertContainedWorkingDirectory(string artifactRoot)
    {
        var scenarioRoot = Path.GetFullPath(artifactRoot);
        var expected = Path.GetDirectoryName(scenarioRoot) ??
            throw new QualificationAssertionException("fixture.publication.working-directory-invalid");
        var actual = Path.GetFullPath(Environment.CurrentDirectory);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(expected) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(scenarioRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new QualificationAssertionException("fixture.publication.working-directory-invalid");
        }
    }

    internal static unsafe (bool Success, int Error) TryRename(
        SafeWindowsHandle source,
        SafeWindowsHandle root,
        string finalComponent)
    {
        if (!string.Equals(finalComponent, FinalComponent, StringComparison.Ordinal))
            throw new QualificationAssertionException("fixture.publication.name-invalid");
        AssertNativeRenameAdmitted();
        var name = Encoding.Unicode.GetBytes(finalComponent);
        var bufferSize = checked(Marshal.SizeOf<FileRenameInfoLayout>() + name.Length);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var rootRef = false;
        try
        {
            lock (RenameLifetimeGate)
            {
                if (source.IsClosed || source.IsInvalid || root.IsClosed || root.IsInvalid)
                    throw new QualificationAssertionException("fixture.publication.handle-closed");
                new Span<byte>((void*)buffer, bufferSize).Clear();
                root.DangerousAddRef(ref rootRef);
                try
                {
                    *(nint*)(buffer + 8) = root.DangerousGetHandle();
                    *(uint*)(buffer + 16) = checked((uint)name.Length);
                    Marshal.Copy(name, 0, buffer + 20, name.Length);
                    var success = SetFileInformationByHandle(
                        source, FileRenameInfo, buffer, checked((uint)bufferSize));
                    return (success, success ? 0 : Marshal.GetLastPInvokeError());
                }
                finally
                {
                    if (rootRef)
                        root.DangerousRelease();
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileRenameInfoLayout
    {
        public uint Flags;
        public nint RootDirectory;
        public uint FileNameLength;
        public char FileName;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeWindowsHandle file,
        int informationClass,
        nint information,
        uint bufferSize);
}
