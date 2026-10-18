using System.Runtime.InteropServices;

namespace WatchDog.CaptureIsolation.Windows;

internal enum WindowsMediaRootDriveType : uint
{
    Unknown = 0,
    NoRootDirectory = 1,
    Removable = 2,
    Fixed = 3,
    Remote = 4,
    CdRom = 5,
    RamDisk = 6,
}

internal sealed record WindowsMediaRootHandleSnapshot(
    uint FileAttributes,
    uint ReparseTag,
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh,
    string FinalPath);

internal sealed class WindowsMediaRootNativeException : Exception
{
    internal WindowsMediaRootNativeException(string failureCode)
        : base("A Windows media-root operation failed.")
    {
        FailureCode = failureCode;
    }

    internal string FailureCode { get; }
}

internal interface IWindowsMediaRootApi : IWindowsHandleCloser
{
    WindowsMediaRootDriveType GetDriveType(string rootPath);
    SafeWindowsHandle OpenDirectory(string path);
    WindowsMediaRootHandleSnapshot Observe(SafeWindowsHandle handle);
}

internal sealed partial class WindowsMediaRootApi : IWindowsMediaRootApi
{
    internal const uint FileAttributeDirectory = 0x00000010;
    internal const uint FileAttributeReparsePoint = 0x00000400;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint ShareReadWrite = 0x00000001 | 0x00000002;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const int FileAttributeTagInfoClass = 9;
    internal const int FileIdInfoClass = 18;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorDirectory = 267;

    internal static WindowsMediaRootApi Instance { get; } = new();

    private WindowsMediaRootApi()
    {
    }

    public WindowsMediaRootDriveType GetDriveType(string rootPath) =>
        (WindowsMediaRootDriveType)GetDriveTypeNative(rootPath);

    public SafeWindowsHandle OpenDirectory(string path)
    {
        var rawHandle = CreateFile(
            path,
            FileReadAttributes,
            ShareReadWrite,
            nint.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            nint.Zero);
        if (rawHandle is 0 or -1)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new WindowsMediaRootNativeException(error switch
            {
                ErrorFileNotFound or ErrorPathNotFound => "media-root.not-found",
                ErrorAccessDenied => "media-root.access-denied",
                ErrorDirectory => "media-root.not-directory",
                _ => "media-root.open-unavailable",
            });
        }
        return new(rawHandle, this);
    }

    public WindowsMediaRootHandleSnapshot Observe(SafeWindowsHandle handle)
    {
        if (!GetFileInformationByHandleExAttribute(
                handle,
                FileAttributeTagInfoClass,
                out var attributeTag,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
            !GetFileInformationByHandleExId(
                handle,
                FileIdInfoClass,
                out var fileId,
                (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            _ = Marshal.GetLastPInvokeError();
            throw new WindowsMediaRootNativeException("media-root.identity-unavailable");
        }

        return new(
            attributeTag.FileAttributes,
            attributeTag.ReparseTag,
            fileId.VolumeSerialNumber,
            fileId.FileId.Low,
            fileId.FileId.High,
            GetFinalPath(handle));
    }

    public bool Close(nint handle) => CloseHandle(handle);

    private static unsafe string GetFinalPath(SafeWindowsHandle handle)
    {
        Span<char> initial = stackalloc char[512];
        fixed (char* buffer = initial)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)initial.Length, 0);
            if (length == 0)
            {
                _ = Marshal.GetLastPInvokeError();
                throw new WindowsMediaRootNativeException("media-root.final-path-unavailable");
            }
            if (length < initial.Length)
                return new string(buffer, 0, checked((int)length));

            var expanded = new char[checked((int)length + 1)];
            fixed (char* expandedBuffer = expanded)
            {
                var expandedLength = GetFinalPathNameByHandle(
                    handle,
                    expandedBuffer,
                    (uint)expanded.Length,
                    0);
                if (expandedLength == 0 || expandedLength >= expanded.Length)
                {
                    _ = Marshal.GetLastPInvokeError();
                    throw new WindowsMediaRootNativeException("media-root.final-path-unavailable");
                }
                return new string(expandedBuffer, 0, checked((int)expandedLength));
            }
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeNative(string rootPathName);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleExAttribute(
        SafeWindowsHandle handle,
        int fileInformationClass,
        out FileAttributeTagInfo fileInformation,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleExId(
        SafeWindowsHandle handle,
        int fileInformationClass,
        out FileIdInfo fileInformation,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(
        SafeWindowsHandle handle,
        char* filePath,
        uint filePathLength,
        uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

#pragma warning disable CS0649 // Fields are populated by Win32 interop.
    [StructLayout(LayoutKind.Sequential)]
    internal struct FileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileId128
    {
        internal ulong Low;
        internal ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileIdInfo
    {
        internal ulong VolumeSerialNumber;
        internal FileId128 FileId;
    }
#pragma warning restore CS0649
}
