using System.Runtime.InteropServices;
using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureRelativeFileState
{
    Absent,
    Present,
}

internal sealed record FixtureRelativeFileObservation(
    FixtureRelativeFileState State,
    WindowsRelativeFileSnapshot? Identity,
    byte[]? Bytes);

/// <summary>
/// Qualification-only observation of fixture-owned literal names beneath an already retained
/// directory handle. The close owner must use the same gate as rename and explicit handle close.
/// </summary>
internal static partial class FixtureRelativeFileObserver
{
    private const uint FileOpen = 1;
    private const uint FileReadData = 0x00000001;
    private const uint FileReadAttributes = 0x00000080;
    private const uint Synchronize = 0x00100000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint ObjectDontReparse = 0x00001000;
    private const uint DirectoryAttribute = 0x00000010;
    private const uint ReparseAttribute = 0x00000400;
    private const int MaximumObservedBytes = 4096;

    internal static unsafe FixtureRelativeFileObservation ObserveExisting(
        SafeWindowsHandle retainedRoot,
        WindowsMediaRootHandleSnapshot admittedRoot,
        string literalName,
        FixtureRelativeFileCloseOwner closeOwner)
    {
        ArgumentNullException.ThrowIfNull(retainedRoot);
        ArgumentNullException.ThrowIfNull(admittedRoot);
        ArgumentNullException.ThrowIfNull(closeOwner);
        if (!IsAllowedName(literalName))
            throw new QualificationAssertionException("fixture.relative-observer.name-invalid");

        lock (closeOwner.LifetimeGate)
        {
            if (retainedRoot.IsClosed || retainedRoot.IsInvalid)
                throw new QualificationAssertionException("fixture.relative-observer.root-closed");
            VerifyRoot(retainedRoot, admittedRoot);

            SafeWindowsHandle? opened = null;
            var rootRef = false;
            try
            {
                retainedRoot.DangerousAddRef(ref rootRef);
                fixed (char* nameBuffer = literalName)
                {
                    var byteLength = checked((ushort)(literalName.Length * sizeof(char)));
                    var unicodeName = new WindowsRelativeFileApi.UnicodeString
                    {
                        Length = byteLength,
                        MaximumLength = byteLength,
                        Buffer = (nint)nameBuffer,
                    };
                    var attributes = new WindowsRelativeFileApi.ObjectAttributes
                    {
                        Length = checked((uint)Marshal.SizeOf<WindowsRelativeFileApi.ObjectAttributes>()),
                        RootDirectory = retainedRoot.DangerousGetHandle(),
                        ObjectName = (nint)(&unicodeName),
                        Attributes = ObjectCaseInsensitive | ObjectDontReparse,
                    };
                    nint rawHandle = 0;
                    WindowsRelativeFileApi.IoStatusBlock ioStatus = default;
                    var status = NtCreateFile(
                        &rawHandle,
                        FileReadData | FileReadAttributes | Synchronize,
                        &attributes,
                        &ioStatus,
                        nint.Zero,
                        0,
                        FileShareRead | FileShareWrite | FileShareDelete,
                        FileOpen,
                        FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint,
                        nint.Zero,
                        0);
                    if (rawHandle is not 0 and not -1)
                        opened = new SafeWindowsHandle(rawHandle, WindowsRelativeFileApi.Instance);

                    // An absent leaf is not proof of an intact retained root. Recheck it even
                    // on the only status accepted as absence, before returning the result.
                    VerifyRoot(retainedRoot, admittedRoot);
                    var disposition = FixtureRelativeOpenDecision.Classify(
                        status, ioStatus.Information, rawHandle);
                    if (disposition == FixtureRelativeOpenDisposition.Absent)
                        return new(FixtureRelativeFileState.Absent, null, null);
                    if (disposition != FixtureRelativeOpenDisposition.Present || opened is null)
                        throw new QualificationAssertionException("fixture.relative-observer.open-ambiguous");

                    var before = WindowsRelativeFileApi.Instance.Observe(opened);
                    if ((before.FileAttributes & (DirectoryAttribute | ReparseAttribute)) != 0 ||
                        before.ReparseTag != 0 ||
                        before.VolumeSerialNumber != admittedRoot.VolumeSerialNumber)
                    {
                        throw new QualificationAssertionException("fixture.relative-observer.identity-invalid");
                    }
                    var bytes = WindowsRelativeFileApi.Instance.ReadAll(opened, MaximumObservedBytes);
                    var after = WindowsRelativeFileApi.Instance.Observe(opened);
                    if (!PublicationRenameProbe.SameFile(before, after) ||
                        before.FileAttributes != after.FileAttributes ||
                        before.ReparseTag != after.ReparseTag)
                    {
                        throw new QualificationAssertionException("fixture.relative-observer.changed-during-read");
                    }
                    VerifyRoot(retainedRoot, admittedRoot);
                    return new(FixtureRelativeFileState.Present, after, bytes);
                }
            }
            finally
            {
                try
                {
                    if (opened is not null)
                    {
                        if (!closeOwner.TryClose(opened))
                            throw new QualificationCleanupException("fixture.relative-observer.close-unconfirmed");
                    }
                }
                finally
                {
                    if (rootRef)
                        retainedRoot.DangerousRelease();
                }
            }
        }
    }

    private static bool IsAllowedName(string? name) => name is
        "temporary-one.bin" or "temporary-two.bin" or "published.bin" or "fixture-canary.bin";

    private static void VerifyRoot(
        SafeWindowsHandle retainedRoot,
        WindowsMediaRootHandleSnapshot admittedRoot)
    {
        WindowsMediaRootHandleSnapshot actual;
        try
        {
            actual = WindowsMediaRootApi.Instance.Observe(retainedRoot);
        }
        catch (WindowsMediaRootNativeException)
        {
            throw new QualificationAssertionException("fixture.relative-observer.root-unavailable");
        }
        if ((actual.FileAttributes & DirectoryAttribute) == 0 ||
            (actual.FileAttributes & ReparseAttribute) != 0 || actual.ReparseTag != 0 ||
            !PublicationRenameProbe.SameRoot(admittedRoot, actual) ||
            !string.Equals(actual.FinalPath, admittedRoot.FinalPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new QualificationAssertionException("fixture.relative-observer.root-changed");
        }
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtCreateFile(
        nint* fileHandle,
        uint desiredAccess,
        WindowsRelativeFileApi.ObjectAttributes* objectAttributes,
        WindowsRelativeFileApi.IoStatusBlock* ioStatusBlock,
        nint allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        nint eaBuffer,
        uint eaLength);
}
