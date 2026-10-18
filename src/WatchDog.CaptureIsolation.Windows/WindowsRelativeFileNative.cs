using System.Runtime.InteropServices;

namespace WatchDog.CaptureIsolation.Windows;

internal sealed record WindowsRelativeFileSnapshot(
    uint FileAttributes,
    uint ReparseTag,
    ulong VolumeSerialNumber,
    ulong FileIdLow,
    ulong FileIdHigh);

internal sealed class WindowsRelativeFileNativeException : Exception
{
    internal WindowsRelativeFileNativeException(string failureCode)
        : base("A handle-relative media-file operation failed.")
    {
        FailureCode = failureCode;
    }

    internal string FailureCode { get; }
}

internal interface IWindowsRelativeFileApi : IWindowsHandleCloser
{
    SafeWindowsHandle CreateNew(SafeWindowsHandle rootDirectory, string singleComponentName);
    WindowsRelativeFileSnapshot Observe(SafeWindowsHandle file);
    void Write(SafeWindowsHandle file, ReadOnlySpan<byte> content);
    byte[] ReadAll(SafeWindowsHandle file, int maximumBytes);
    void Flush(SafeWindowsHandle file);
    bool MarkDeleteOnClose(SafeWindowsHandle file);
}

/// <summary>
/// Narrow Windows x64 Native API boundary for one-component opens relative to a retained
/// directory handle. It uses exported ntdll entry points and never constructs an absolute path.
/// </summary>
internal sealed partial class WindowsRelativeFileApi : IWindowsRelativeFileApi
{
    internal const uint ObjectCaseInsensitive = 0x00000040;
    internal const uint ObjectDontReparse = 0x00001000;
    internal const uint FileCreate = 0x00000002;
    internal const uint FileSynchronousIoNonAlert = 0x00000020;
    internal const uint FileNonDirectoryFile = 0x00000040;
    internal const uint FileOpenReparsePoint = 0x00200000;
    internal const uint FileReadData = 0x00000001;
    internal const uint FileWriteData = 0x00000002;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint FileWriteAttributes = 0x00000100;
    internal const uint Delete = 0x00010000;
    internal const uint Synchronize = 0x00100000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileAttributeNormal = 0x00000080;
    internal const int FileAttributeTagInfoClass = 9;
    internal const int FileIdInfoClass = 18;
    internal const int FileDispositionInfoClass = 4;

    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    private const int StatusReparsePointEncountered = unchecked((int)0xC000050B);
    private const int StatusStoppedOnSymlink = unchecked((int)0x8000002D);
    private const int StatusReparse = 0x00000104;
    private const int StatusReparseObject = 0x00000118;
    private const int StatusReparseGlobal = 0x00000368;
    private const int StatusIoReparseTagNotHandled = unchecked((int)0xC0000279);
    private const int StatusReparsePointNotResolved = unchecked((int)0xC0000280);
    private const int StatusDirectoryIsAReparsePoint = unchecked((int)0xC0000281);
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);

    internal static WindowsRelativeFileApi Instance { get; } = new();

    private WindowsRelativeFileApi()
    {
    }

    public unsafe SafeWindowsHandle CreateNew(
        SafeWindowsHandle rootDirectory,
        string singleComponentName)
    {
        ArgumentNullException.ThrowIfNull(rootDirectory);
        ArgumentNullException.ThrowIfNull(singleComponentName);
        var byteLength = checked(singleComponentName.Length * sizeof(char));
        if (byteLength == 0 || byteLength > ushort.MaxValue)
            throw new WindowsRelativeFileNativeException("media-file.name-invalid");

        var rootAddedRef = false;
        try
        {
            rootDirectory.DangerousAddRef(ref rootAddedRef);
            fixed (char* nameBuffer = singleComponentName)
            {
                var unicodeName = new UnicodeString
                {
                    Length = checked((ushort)byteLength),
                    MaximumLength = checked((ushort)byteLength),
                    Buffer = (nint)nameBuffer,
                };
                var attributes = new ObjectAttributes
                {
                    Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                    RootDirectory = rootDirectory.DangerousGetHandle(),
                    ObjectName = (nint)(&unicodeName),
                    Attributes = ObjectCaseInsensitive | ObjectDontReparse,
                };
                var status = NtCreateFile(
                    out var rawHandle,
                    FileReadData | FileWriteData | FileReadAttributes | FileWriteAttributes |
                        Delete | Synchronize,
                    ref attributes,
                    out _,
                    nint.Zero,
                    FileAttributeNormal,
                    FileShareRead,
                    FileCreate,
                    FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint,
                    nint.Zero,
                    0);
                if (!IsUsableCreateResult(status, rawHandle))
                {
                    if (rawHandle is not 0 and not -1)
                    {
                        using var unexpectedHandle = new SafeWindowsHandle(rawHandle, this);
                        _ = unexpectedHandle.CloseOnce();
                    }
                    throw new WindowsRelativeFileNativeException(FailureCodeForStatus(status));
                }
                return new(rawHandle, this);
            }
        }
        finally
        {
            if (rootAddedRef)
                rootDirectory.DangerousRelease();
        }
    }

    internal static bool IsUsableCreateResult(int status, nint rawHandle) =>
        status == 0 && rawHandle is not 0 and not -1;

    internal static string FailureCodeForStatus(int status) => status switch
    {
        StatusObjectNameCollision => "media-file.already-exists",
        StatusReparsePointEncountered or StatusStoppedOnSymlink or
            StatusReparse or StatusReparseObject or StatusReparseGlobal or
            StatusIoReparseTagNotHandled or StatusReparsePointNotResolved or
            StatusDirectoryIsAReparsePoint => "media-file.reparse-point",
        StatusAccessDenied => "media-file.access-denied",
        StatusObjectNameNotFound => "media-file.root-stale",
        _ => "media-file.create-unavailable",
    };

    public WindowsRelativeFileSnapshot Observe(SafeWindowsHandle file)
    {
        if (!GetFileInformationByHandleExAttribute(
                file,
                FileAttributeTagInfoClass,
                out var attributeTag,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
            !GetFileInformationByHandleExId(
                file,
                FileIdInfoClass,
                out var fileId,
                (uint)Marshal.SizeOf<FileIdInfo>()))
        {
            _ = Marshal.GetLastPInvokeError();
            throw new WindowsRelativeFileNativeException("media-file.identity-unavailable");
        }

        return new(
            attributeTag.FileAttributes,
            attributeTag.ReparseTag,
            fileId.VolumeSerialNumber,
            fileId.FileId.Low,
            fileId.FileId.High);
    }

    public unsafe void Write(SafeWindowsHandle file, ReadOnlySpan<byte> content)
    {
        fixed (byte* buffer = content)
        {
            var offset = 0;
            while (offset < content.Length)
            {
                var remaining = checked((uint)(content.Length - offset));
                if (!WriteFile(file, buffer + offset, remaining, out var written, nint.Zero) ||
                    written == 0)
                {
                    _ = Marshal.GetLastPInvokeError();
                    throw new WindowsRelativeFileNativeException("media-file.write-failed");
                }
                offset = checked(offset + (int)written);
            }
        }
    }

    public unsafe byte[] ReadAll(SafeWindowsHandle file, int maximumBytes)
    {
        if (!GetFileSizeEx(file, out var length) || length < 0 || length > maximumBytes)
        {
            _ = Marshal.GetLastPInvokeError();
            throw new WindowsRelativeFileNativeException("media-file.read-size-invalid");
        }
        if (!SetFilePointerEx(file, 0, out _, 0))
        {
            _ = Marshal.GetLastPInvokeError();
            throw new WindowsRelativeFileNativeException("media-file.seek-failed");
        }

        var result = new byte[checked((int)length)];
        fixed (byte* buffer = result)
        {
            var offset = 0;
            while (offset < result.Length)
            {
                var remaining = checked((uint)(result.Length - offset));
                if (!ReadFile(file, buffer + offset, remaining, out var read, nint.Zero) || read == 0)
                {
                    _ = Marshal.GetLastPInvokeError();
                    throw new WindowsRelativeFileNativeException("media-file.read-failed");
                }
                offset = checked(offset + (int)read);
            }
        }
        return result;
    }

    public void Flush(SafeWindowsHandle file)
    {
        if (!FlushFileBuffers(file))
        {
            _ = Marshal.GetLastPInvokeError();
            throw new WindowsRelativeFileNativeException("media-file.flush-failed");
        }
    }

    public bool MarkDeleteOnClose(SafeWindowsHandle file)
    {
        var disposition = new FileDispositionInfo { DeleteFile = 1 };
        return SetFileInformationByHandle(
            file,
            FileDispositionInfoClass,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInfo>());
    }

    public bool Close(nint handle) => CloseHandle(handle);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtCreateFile(
        out nint fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        nint allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        nint eaBuffer,
        uint eaLength);

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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WriteFile(
        SafeWindowsHandle file,
        byte* buffer,
        uint bytesToWrite,
        out uint bytesWritten,
        nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadFile(
        SafeWindowsHandle file,
        byte* buffer,
        uint bytesToRead,
        out uint bytesRead,
        nint overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushFileBuffers(SafeWindowsHandle file);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileSizeEx(SafeWindowsHandle file, out long fileSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFilePointerEx(
        SafeWindowsHandle file,
        long distanceToMove,
        out long newFilePointer,
        uint moveMethod);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeWindowsHandle file,
        int fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    internal struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ObjectAttributes
    {
        internal uint Length;
        internal nint RootDirectory;
        internal nint ObjectName;
        internal uint Attributes;
        internal nint SecurityDescriptor;
        internal nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IoStatusBlock
    {
        internal nint StatusOrPointer;
        internal nuint Information;
    }

#pragma warning disable CS0649 // Fields are populated by Win32 interop.
    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTagInfo
    {
        internal uint FileAttributes;
        internal uint ReparseTag;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileId128
    {
        internal ulong Low;
        internal ulong High;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo
    {
        internal ulong VolumeSerialNumber;
        internal FileId128 FileId;
    }
#pragma warning restore CS0649

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        internal int DeleteFile;
    }
}
