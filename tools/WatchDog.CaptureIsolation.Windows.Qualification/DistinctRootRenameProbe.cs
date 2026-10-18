using System.Security.Cryptography;
using System.Text;
using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

/// <summary>
/// Fixture-only, two-directory test of Win32 FILE_RENAME_INFO.RootDirectory.
/// This does not admit a production publication primitive.
/// </summary>
internal static partial class DistinctRootRenameProbe
{
    private const uint DirectoryReparsePoint = 0x00000400;
    private static readonly FixtureRelativeFileCloseOwner ObserverCloseOwner = new(
        PublicationRenameProbe.LifetimeGate);
    internal static Task<ScenarioResultRecord> RunAsync(
        QualificationOptions options,
        QualificationArtifacts artifacts,
        CancellationToken cancellationToken)
    {
        PublicationRenameProbe.AssertNativeRenameAdmitted();
        var artifactRoot = Path.GetFullPath(options.ArtifactRoot);
        var scratch = Path.GetFullPath(Path.Combine(
            artifactRoot, $"distinct-root-scratch-{Guid.NewGuid():N}"));
        var sourcePath = Path.GetFullPath(Path.Combine(scratch, "source-root"));
        var targetPath = Path.GetFullPath(Path.Combine(scratch, "destination-root"));
        if (!Path.GetDirectoryName(scratch)!.Equals(artifactRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(sourcePath)!.Equals(scratch, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetDirectoryName(targetPath)!.Equals(scratch, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(artifactRoot) & FileAttributes.ReparsePoint) != 0)
        {
            throw new QualificationAssertionException("fixture.distinct-root.scratch-invalid");
        }

        SafeWindowsHandle? scratchHandle = null;
        SafeWindowsHandle? sourceRoot = null;
        SafeWindowsHandle? targetRoot = null;
        SafeWindowsHandle? sourceFile = null;
        SafeWindowsHandle? outsideCanary = null;
        SafeWindowsHandle? sourceCanary = null;
        SafeWindowsHandle? targetCanary = null;
        SafeWindowsHandle? workingDirectoryRoot = null;
        var observerCloseOwner = ObserverCloseOwner;
        if (!observerCloseOwner.TryClosePending())
            throw new QualificationCleanupException("fixture.relative-observer.close-pending");
        if (!PublicationRenameProbe.RetryPendingRootClose())
            throw new QualificationCleanupException("fixture.publication.root-close-pending");
        if (!PublicationRenameProbe.RetryPendingFileCleanup())
            throw new QualificationCleanupException("fixture.publication.file-cleanup-pending");
        WindowsMediaRootHandleSnapshot? scratchIdentity = null;
        WindowsMediaRootHandleSnapshot? sourceIdentity = null;
        WindowsMediaRootHandleSnapshot? targetIdentity = null;
        WindowsMediaRootHandleSnapshot? workingDirectoryIdentity = null;
        var sourceCreationAttempted = false;
        var targetCreationAttempted = false;
        var cleanupConfirmed = true;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PublicationRenameProbe.AssertRenameLayout();
            sourceCreationAttempted = true;
            Directory.CreateDirectory(sourcePath);
            targetCreationAttempted = true;
            Directory.CreateDirectory(targetPath);
            if (IsReparse(scratch) || IsReparse(sourcePath) || IsReparse(targetPath))
                throw new QualificationAssertionException("fixture.distinct-root.reparse");

            scratchHandle = WindowsMediaRootApi.Instance.OpenDirectory(scratch);
            sourceRoot = WindowsMediaRootApi.Instance.OpenDirectory(sourcePath);
            targetRoot = WindowsMediaRootApi.Instance.OpenDirectory(targetPath);
            scratchIdentity = WindowsMediaRootApi.Instance.Observe(scratchHandle);
            sourceIdentity = WindowsMediaRootApi.Instance.Observe(sourceRoot);
            targetIdentity = WindowsMediaRootApi.Instance.Observe(targetRoot);
            if (HasReparse(scratchIdentity) || HasReparse(sourceIdentity) || HasReparse(targetIdentity) ||
                sourceIdentity.VolumeSerialNumber != targetIdentity.VolumeSerialNumber ||
                PublicationRenameProbe.SameRoot(sourceIdentity, targetIdentity))
            {
                throw new QualificationAssertionException("fixture.distinct-root.identity-invalid");
            }

            var expected = Encoding.UTF8.GetBytes("watchdog-distinct-root-rename-v1");
            var outsideCanaryBytes = Encoding.UTF8.GetBytes("watchdog-outside-canary-v1");
            var sourceCanaryBytes = Encoding.UTF8.GetBytes("watchdog-source-canary-v1");
            var targetCanaryBytes = Encoding.UTF8.GetBytes("watchdog-target-canary-v1");
            RequireAbsent(sourceRoot, sourceIdentity, "temporary-one.bin", observerCloseOwner,
                "fixture.distinct-root.unexpected-entry");
            RequireAbsent(sourceRoot, sourceIdentity, "published.bin", observerCloseOwner,
                "fixture.distinct-root.unexpected-entry");
            RequireAbsent(targetRoot, targetIdentity, "published.bin", observerCloseOwner,
                "fixture.distinct-root.unexpected-entry");
            RequireAbsent(scratchHandle, scratchIdentity, "published.bin", observerCloseOwner,
                "fixture.distinct-root.unexpected-entry");
            outsideCanary = WindowsRelativeFileApi.Instance.CreateNew(
                scratchHandle, "fixture-canary.bin");
            WindowsRelativeFileApi.Instance.Write(outsideCanary, outsideCanaryBytes);
            WindowsRelativeFileApi.Instance.Flush(outsideCanary);
            var outsideCanaryBefore = WindowsRelativeFileApi.Instance.Observe(outsideCanary);
            sourceCanary = WindowsRelativeFileApi.Instance.CreateNew(sourceRoot, "fixture-canary.bin");
            WindowsRelativeFileApi.Instance.Write(sourceCanary, sourceCanaryBytes);
            WindowsRelativeFileApi.Instance.Flush(sourceCanary);
            var sourceCanaryBefore = WindowsRelativeFileApi.Instance.Observe(sourceCanary);
            targetCanary = WindowsRelativeFileApi.Instance.CreateNew(targetRoot, "fixture-canary.bin");
            WindowsRelativeFileApi.Instance.Write(targetCanary, targetCanaryBytes);
            WindowsRelativeFileApi.Instance.Flush(targetCanary);
            var targetCanaryBefore = WindowsRelativeFileApi.Instance.Observe(targetCanary);
            sourceFile = WindowsRelativeFileApi.Instance.CreateNew(sourceRoot, "temporary-one.bin");
            WindowsRelativeFileApi.Instance.Write(sourceFile, expected);
            WindowsRelativeFileApi.Instance.Flush(sourceFile);
            if (!CryptographicOperations.FixedTimeEquals(
                    expected, WindowsRelativeFileApi.Instance.ReadAll(sourceFile, 4096)))
            {
                throw new QualificationAssertionException("fixture.distinct-root.seal-mismatch");
            }
            var before = WindowsRelativeFileApi.Instance.Observe(sourceFile);
            cancellationToken.ThrowIfCancellationRequested();
            PublicationRenameProbe.AssertContainedWorkingDirectory(options.ArtifactRoot);
            workingDirectoryRoot = WindowsMediaRootApi.Instance.OpenDirectory(
                Environment.CurrentDirectory);
            workingDirectoryIdentity = WindowsMediaRootApi.Instance.Observe(workingDirectoryRoot);
            RequireAbsent(workingDirectoryRoot, workingDirectoryIdentity, "published.bin",
                observerCloseOwner, "fixture.distinct-root.working-directory-target-present");
            var rooted = PublicationRenameProbe.TryRename(
                sourceFile, targetRoot, "published.bin");
            PublicationRenameProbe.AssertContainedWorkingDirectory(options.ArtifactRoot);
            if (!rooted.Success)
            {
                artifacts.Log("distinct-root-rename-rejected",
                    PublicationRenameProbe.FailureCategory(rooted.Error));
                RequireUnchangedAfterFailure(
                    sourceFile, sourceRoot, targetRoot, sourceIdentity, targetIdentity,
                    before, expected, observerCloseOwner);
                RequireCanariesUnchanged(sourceRoot, sourceIdentity, sourceCanary,
                    sourceCanaryBefore, sourceCanaryBytes, targetRoot, targetIdentity,
                    targetCanary, targetCanaryBefore, targetCanaryBytes, observerCloseOwner);
                RequireOutsideUnchanged(scratchHandle, scratchIdentity, outsideCanary,
                    outsideCanaryBefore, outsideCanaryBytes, observerCloseOwner);
                RequireAbsent(workingDirectoryRoot, workingDirectoryIdentity, "published.bin",
                    observerCloseOwner, "fixture.distinct-root.working-directory-target-present");
                throw new QualificationAssertionException("fixture.distinct-root.rename-unavailable");
            }

            RequireAbsent(workingDirectoryRoot, workingDirectoryIdentity, "published.bin",
                observerCloseOwner, "fixture.distinct-root.working-directory-target-present");
            RequireAbsent(sourceRoot, sourceIdentity, "temporary-one.bin", observerCloseOwner,
                "fixture.distinct-root.destination-invalid");
            RequireAbsent(sourceRoot, sourceIdentity, "published.bin", observerCloseOwner,
                "fixture.distinct-root.destination-invalid");
            RequireOutsideUnchanged(scratchHandle, scratchIdentity, outsideCanary,
                outsideCanaryBefore, outsideCanaryBytes, observerCloseOwner);
            RequireMatchingEntry(targetRoot, targetIdentity, "published.bin", before, expected,
                observerCloseOwner, "fixture.distinct-root.destination-invalid");
            if (
                !PublicationRenameProbe.SameFile(
                    before, WindowsRelativeFileApi.Instance.Observe(sourceFile)) ||
                !PublicationRenameProbe.SameRoot(
                    sourceIdentity, WindowsMediaRootApi.Instance.Observe(sourceRoot)) ||
                !PublicationRenameProbe.SameRoot(
                    targetIdentity, WindowsMediaRootApi.Instance.Observe(targetRoot)))
            {
                throw new QualificationAssertionException("fixture.distinct-root.destination-invalid");
            }
            RequireCanariesUnchanged(sourceRoot, sourceIdentity, sourceCanary,
                sourceCanaryBefore, sourceCanaryBytes, targetRoot, targetIdentity,
                targetCanary, targetCanaryBefore, targetCanaryBytes, observerCloseOwner);
            RequireOutsideUnchanged(scratchHandle, scratchIdentity, outsideCanary,
                outsideCanaryBefore, outsideCanaryBytes, observerCloseOwner);
            artifacts.Log("distinct-root-resolution-confirmed");
            return Task.FromResult(new ScenarioResultRecord(
                options.Scenario!.Value.ToString(), true, "passed", 0, true));
        }
        finally
        {
            var handlesClosed = PublicationRenameProbe.CleanupFile(sourceFile);
            handlesClosed &= PublicationRenameProbe.CleanupFile(targetCanary);
            handlesClosed &= PublicationRenameProbe.CleanupFile(sourceCanary);
            handlesClosed &= PublicationRenameProbe.CleanupFile(outsideCanary);
            handlesClosed &= observerCloseOwner.TryClosePending();
            handlesClosed &= CloseRoot(workingDirectoryRoot);
            handlesClosed &= CloseRoot(targetRoot);
            handlesClosed &= CloseRoot(sourceRoot);
            handlesClosed &= CloseRoot(scratchHandle);
            cleanupConfirmed &= handlesClosed;
            if (cleanupConfirmed)
            {
                cleanupConfirmed = !sourceCreationAttempted ||
                    DeleteEmptyScratch(
                        scratch, sourcePath, targetPath, targetCreationAttempted,
                        scratchIdentity, sourceIdentity, targetIdentity);
            }
            if (!cleanupConfirmed)
                throw new QualificationCleanupException("fixture.distinct-root.cleanup-uncertain");
        }
    }

    private static void RequireCanariesUnchanged(
        SafeWindowsHandle sourceRoot,
        WindowsMediaRootHandleSnapshot sourceIdentity,
        SafeWindowsHandle sourceCanary,
        WindowsRelativeFileSnapshot sourceBefore,
        byte[] sourceBytes,
        SafeWindowsHandle targetRoot,
        WindowsMediaRootHandleSnapshot targetIdentity,
        SafeWindowsHandle targetCanary,
        WindowsRelativeFileSnapshot targetBefore,
        byte[] targetBytes,
        FixtureRelativeFileCloseOwner observerCloseOwner)
    {
        RequireMatchingEntry(sourceRoot, sourceIdentity, "fixture-canary.bin",
            sourceBefore, sourceBytes, observerCloseOwner, "fixture.distinct-root.canary-changed");
        RequireMatchingEntry(targetRoot, targetIdentity, "fixture-canary.bin",
            targetBefore, targetBytes, observerCloseOwner, "fixture.distinct-root.canary-changed");
        if (!PublicationRenameProbe.SameFile(
                sourceBefore, WindowsRelativeFileApi.Instance.Observe(sourceCanary)) ||
            !CryptographicOperations.FixedTimeEquals(
                sourceBytes, WindowsRelativeFileApi.Instance.ReadAll(sourceCanary, 4096)) ||
            !PublicationRenameProbe.SameFile(
                targetBefore, WindowsRelativeFileApi.Instance.Observe(targetCanary)) ||
            !CryptographicOperations.FixedTimeEquals(
                targetBytes, WindowsRelativeFileApi.Instance.ReadAll(targetCanary, 4096)))
        {
            throw new QualificationAssertionException("fixture.distinct-root.canary-changed");
        }
    }

    private static void RequireOutsideUnchanged(
        SafeWindowsHandle outsideRoot,
        WindowsMediaRootHandleSnapshot outsideIdentity,
        SafeWindowsHandle outsideCanary,
        WindowsRelativeFileSnapshot outsideBefore,
        byte[] outsideBytes,
        FixtureRelativeFileCloseOwner observerCloseOwner)
    {
        RequireAbsent(outsideRoot, outsideIdentity, "published.bin", observerCloseOwner,
            "fixture.distinct-root.outside-changed");
        RequireMatchingEntry(outsideRoot, outsideIdentity, "fixture-canary.bin",
            outsideBefore, outsideBytes, observerCloseOwner,
            "fixture.distinct-root.outside-changed");
        if (!PublicationRenameProbe.SameFile(
                outsideBefore, WindowsRelativeFileApi.Instance.Observe(outsideCanary)) ||
            !CryptographicOperations.FixedTimeEquals(
                outsideBytes, WindowsRelativeFileApi.Instance.ReadAll(outsideCanary, 4096)))
        {
            throw new QualificationAssertionException("fixture.distinct-root.outside-changed");
        }
    }

    private static void RequireAbsent(
        SafeWindowsHandle root,
        WindowsMediaRootHandleSnapshot admittedRoot,
        string name,
        FixtureRelativeFileCloseOwner observerCloseOwner,
        string failureCode)
    {
        var observed = FixtureRelativeFileObserver.ObserveExisting(
            root, admittedRoot, name, observerCloseOwner);
        if (observed.State != FixtureRelativeFileState.Absent ||
            observed.Identity is not null || observed.Bytes is not null)
            throw new QualificationAssertionException(failureCode);
    }

    private static void RequireMatchingEntry(
        SafeWindowsHandle root,
        WindowsMediaRootHandleSnapshot admittedRoot,
        string name,
        WindowsRelativeFileSnapshot expectedIdentity,
        byte[] expectedBytes,
        FixtureRelativeFileCloseOwner observerCloseOwner,
        string failureCode)
    {
        var observed = FixtureRelativeFileObserver.ObserveExisting(
            root, admittedRoot, name, observerCloseOwner);
        if (observed.State != FixtureRelativeFileState.Present ||
            observed.Identity is null || observed.Bytes is null ||
            !PublicationRenameProbe.SameFile(expectedIdentity, observed.Identity) ||
            !CryptographicOperations.FixedTimeEquals(expectedBytes, observed.Bytes))
            throw new QualificationAssertionException(failureCode);
    }

    private static void RequireUnchangedAfterFailure(
        SafeWindowsHandle sourceFile,
        SafeWindowsHandle sourceRoot,
        SafeWindowsHandle targetRoot,
        WindowsMediaRootHandleSnapshot sourceIdentity,
        WindowsMediaRootHandleSnapshot targetIdentity,
        WindowsRelativeFileSnapshot before,
        byte[] expected,
        FixtureRelativeFileCloseOwner observerCloseOwner)
    {
        RequireMatchingEntry(sourceRoot, sourceIdentity, "temporary-one.bin", before, expected,
            observerCloseOwner, "fixture.distinct-root.failed-call-ambiguous");
        RequireAbsent(sourceRoot, sourceIdentity, "published.bin", observerCloseOwner,
            "fixture.distinct-root.failed-call-ambiguous");
        RequireAbsent(targetRoot, targetIdentity, "published.bin", observerCloseOwner,
            "fixture.distinct-root.failed-call-ambiguous");
        if (!PublicationRenameProbe.SameFile(
                before, WindowsRelativeFileApi.Instance.Observe(sourceFile)) ||
            !CryptographicOperations.FixedTimeEquals(
                expected, WindowsRelativeFileApi.Instance.ReadAll(sourceFile, 4096)) ||
            !PublicationRenameProbe.SameRoot(
                sourceIdentity, WindowsMediaRootApi.Instance.Observe(sourceRoot)) ||
            !PublicationRenameProbe.SameRoot(
                targetIdentity, WindowsMediaRootApi.Instance.Observe(targetRoot)))
        {
            throw new QualificationAssertionException("fixture.distinct-root.failed-call-ambiguous");
        }
    }

    private static bool CloseRoot(SafeWindowsHandle? handle) =>
        PublicationRenameProbe.CloseRoot(handle);

    private static bool DeleteEmptyScratch(
        string scratch,
        string sourcePath,
        string targetPath,
        bool targetCreationAttempted,
        WindowsMediaRootHandleSnapshot? scratchIdentity,
        WindowsMediaRootHandleSnapshot? sourceIdentity,
        WindowsMediaRootHandleSnapshot? targetIdentity)
    {
        try
        {
            if (scratchIdentity is null || sourceIdentity is null ||
                targetCreationAttempted && targetIdentity is null)
                return false;
            if (!Directory.Exists(scratch) || IsReparse(scratch) ||
                !Directory.Exists(sourcePath) || IsReparse(sourcePath) ||
                targetCreationAttempted && (!Directory.Exists(targetPath) || IsReparse(targetPath)))
            {
                return false;
            }
            if (!MatchesAdmitted(scratch, scratchIdentity) ||
                !MatchesAdmitted(sourcePath, sourceIdentity) ||
                targetCreationAttempted && !MatchesAdmitted(targetPath, targetIdentity))
            {
                return false;
            }
            if (targetCreationAttempted)
                Directory.Delete(targetPath, recursive: false);
            Directory.Delete(sourcePath, recursive: false);
            Directory.Delete(scratch, recursive: false);
            return !Directory.Exists(scratch);
        }
        catch
        {
            return false;
        }
    }

    private static bool MatchesAdmitted(
        string path,
        WindowsMediaRootHandleSnapshot? admitted)
    {
        if (admitted is null)
            return false;
        var reopened = WindowsMediaRootApi.Instance.OpenDirectory(path);
        bool matches;
        bool closed;
        try
        {
            matches = PublicationRenameProbe.SameRoot(
                admitted, WindowsMediaRootApi.Instance.Observe(reopened));
        }
        finally
        {
            closed = CloseRoot(reopened);
        }
        return matches && closed;
    }

    private static bool IsReparse(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool HasReparse(WindowsMediaRootHandleSnapshot snapshot) =>
        (snapshot.FileAttributes & DirectoryReparsePoint) != 0;

}
