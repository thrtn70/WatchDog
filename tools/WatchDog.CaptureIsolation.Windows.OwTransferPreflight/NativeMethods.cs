using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static partial class NativeMethods
{
    internal const uint FileReadData = 0x00000001;
    internal const uint FileListDirectory = 0x00000001;
    internal const uint FileAddFile = 0x00000002;
    internal const uint FileWriteData = 0x00000002;
    internal const uint FileAddSubdirectory = 0x00000004;
    internal const uint FileAppendData = 0x00000004;
    internal const uint FileTraverse = 0x00000020;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint FileWriteAttributes = 0x00000100;
    internal const uint Delete = 0x00010000;
    internal const uint ReadControl = 0x00020000;
    internal const uint Synchronize = 0x00100000;

    internal const uint ParentTransferAccess = FileListDirectory | FileAddSubdirectory |
        FileTraverse | FileReadAttributes | ReadControl | Synchronize;
    internal const uint RootReadAccess = FileListDirectory | FileTraverse |
        FileReadAttributes | ReadControl | Synchronize;
    internal const uint FileReadAccess = FileReadData | FileReadAttributes |
        ReadControl | Synchronize;

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileAttributeReparsePoint = 0x00000400;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint ObjectDontReparse = 0x00001000;
    private const uint FileCreate = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint HandleFlagInherit = 0x00000001;
    private const int StatusSuccess = 0;
    private const int FileStandardInfoClass = 1;
    private const int FileAttributeTagInfoClass = 9;
    private const int FileIdInfoClass = 18;
    private const int ObjectBasicInformationClass = 0;

    internal static SafeFileHandle OpenTrustedParent(string path)
    {
        var handle = CreateFile(
            path,
            ParentTransferAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            nint.Zero,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint,
            nint.Zero);
        if (handle.IsInvalid)
            throw NewWin32("Could not retain the trusted parent directory");
        return handle;
    }

    internal static SafeFileHandle OpenDirectoryForObservation(string path) =>
        OpenTrustedParent(path);

    internal static long DuplicateToProcess(
        SafeFileHandle source,
        SafeHandle targetProcess,
        uint desiredAccess)
    {
        var sourceAdded = false;
        var targetAdded = false;
        try
        {
            source.DangerousAddRef(ref sourceAdded);
            targetProcess.DangerousAddRef(ref targetAdded);
            if (!DuplicateHandle(
                    GetCurrentProcess(), source.DangerousGetHandle(),
                    targetProcess.DangerousGetHandle(), out var remote,
                    desiredAccess, false, 0) || remote is 0 or -1)
                throw NewWin32("DuplicateHandle O-to-W failed");
            return remote.ToInt64();
        }
        finally
        {
            if (targetAdded)
                targetProcess.DangerousRelease();
            if (sourceAdded)
                source.DangerousRelease();
        }
    }

    internal static SafeFileHandle DuplicateFromProcess(
        SafeHandle sourceProcess,
        long sourceCoordinate,
        uint desiredAccess)
    {
        var adopted = new SafeFileHandle(nint.Zero, ownsHandle: true);
        var processAdded = false;
        try
        {
            sourceProcess.DangerousAddRef(ref processAdded);
            var succeeded = DuplicateHandle(
                    sourceProcess.DangerousGetHandle(), new nint(sourceCoordinate),
                    GetCurrentProcess(), out var local, desiredAccess, false, 0);
            Marshal.InitHandle(adopted, local);
            if (!succeeded || adopted.IsInvalid)
            {
                adopted.Dispose();
                throw NewWin32("DuplicateHandle W-to-O failed");
            }
            return adopted;
        }
        finally
        {
            if (processAdded)
                sourceProcess.DangerousRelease();
        }
    }

    internal static unsafe SafeFileHandle CreateDirectoryRelative(
        SafeFileHandle parent,
        string name,
        byte[] securityDescriptor) =>
        CreateRelative(
            parent,
            name,
            FileListDirectory | FileAddFile | FileTraverse | FileReadAttributes |
                ReadControl | Synchronize,
            FileAttributeDirectory,
            FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
            securityDescriptor);

    internal static unsafe SafeFileHandle CreateFileRelative(
        SafeFileHandle parent,
        string name,
        byte[] securityDescriptor) =>
        CreateRelative(
            parent,
            name,
            FileReadData | FileWriteData | FileAppendData | FileReadAttributes |
                FileWriteAttributes | ReadControl | Synchronize,
            FileAttributeNormal,
            FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint,
            securityDescriptor);

    private static unsafe SafeFileHandle CreateRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint attributes,
        uint options,
        byte[] securityDescriptor)
    {
        if (!IsSingleComponent(name) || securityDescriptor.Length == 0)
            throw new ArgumentException("A relative object name and security descriptor are required.");

        var parentAdded = false;
        try
        {
            parent.DangerousAddRef(ref parentAdded);
            var byteLength = checked(name.Length * sizeof(char));
            fixed (char* nameBuffer = name)
            fixed (byte* securityBuffer = securityDescriptor)
            {
                var unicodeName = new UnicodeString
                {
                    Length = checked((ushort)byteLength),
                    MaximumLength = checked((ushort)byteLength),
                    Buffer = (nint)nameBuffer,
                };
                var objectAttributes = new ObjectAttributes
                {
                    Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = (nint)(&unicodeName),
                    Attributes = ObjectCaseInsensitive | ObjectDontReparse,
                    SecurityDescriptor = (nint)securityBuffer,
                };
                var adopted = new SafeFileHandle(nint.Zero, ownsHandle: true);
                var status = NtCreateFile(
                    out var raw,
                    desiredAccess,
                    ref objectAttributes,
                    out _,
                    nint.Zero,
                    attributes,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    FileCreate,
                    options,
                    nint.Zero,
                    0);
                Marshal.InitHandle(adopted, raw);
                if (status != StatusSuccess || adopted.IsInvalid)
                {
                    adopted?.Dispose();
                    throw new InvalidOperationException($"NtCreateFile failed with NTSTATUS 0x{status:X8}.");
                }
                return adopted;
            }
        }
        finally
        {
            if (parentAdded)
                parent.DangerousRelease();
        }
    }

    internal static SafeFileHandle OpenRelativeForDelete(
        SafeFileHandle parent,
        string name,
        bool directory) =>
        OpenRelative(
            parent,
            name,
            Delete | FileReadAttributes | ReadControl | Synchronize |
                (directory ? FileListDirectory | FileTraverse : FileReadData),
            directory ? FileAttributeDirectory : FileAttributeNormal,
            (directory ? FileDirectoryFile : FileNonDirectoryFile) |
                FileSynchronousIoNonAlert | FileOpenReparsePoint);

    internal static SafeFileHandle ReopenFileRelative(SafeFileHandle parent, string name) =>
        OpenRelative(parent, name,
            FileReadData | FileWriteData | FileAppendData | FileReadAttributes |
                FileWriteAttributes | ReadControl | Synchronize,
            FileAttributeNormal, FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint);

    private static unsafe SafeFileHandle OpenRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        uint attributes,
        uint options)
    {
        if (!IsSingleComponent(name))
            throw new ArgumentException("A relative object name is required.");

        var parentAdded = false;
        try
        {
            parent.DangerousAddRef(ref parentAdded);
            var byteLength = checked(name.Length * sizeof(char));
            fixed (char* nameBuffer = name)
            {
                var unicodeName = new UnicodeString
                {
                    Length = checked((ushort)byteLength),
                    MaximumLength = checked((ushort)byteLength),
                    Buffer = (nint)nameBuffer,
                };
                var objectAttributes = new ObjectAttributes
                {
                    Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                    RootDirectory = parent.DangerousGetHandle(),
                    ObjectName = (nint)(&unicodeName),
                    Attributes = ObjectCaseInsensitive | ObjectDontReparse,
                };
                var adopted = new SafeFileHandle(nint.Zero, ownsHandle: true);
                var status = NtCreateFile(
                    out var raw,
                    desiredAccess,
                    ref objectAttributes,
                    out _,
                    nint.Zero,
                    attributes,
                    FileShareRead | FileShareWrite | FileShareDelete,
                    1,
                    options,
                    nint.Zero,
                    0);
                Marshal.InitHandle(adopted, raw);
                if (status != StatusSuccess || adopted.IsInvalid)
                {
                    adopted?.Dispose();
                    throw new InvalidOperationException(
                        $"Relative cleanup open failed with NTSTATUS 0x{status:X8}.");
                }
                return adopted;
            }
        }
        finally
        {
            if (parentAdded)
                parent.DangerousRelease();
        }
    }

    internal static KernelHandleObservation ObserveKernelHandle(SafeHandle handle)
    {
        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);
            var raw = handle.DangerousGetHandle();
            if (!GetHandleInformation(raw, out var flags))
                throw NewWin32("Could not observe kernel handle flags");
            var status = NtQueryObject(raw, ObjectBasicInformationClass, out var basic,
                (uint)Marshal.SizeOf<ObjectBasicInformation>(), out _);
            if (status != StatusSuccess)
                throw new InvalidOperationException($"Kernel handle rights query failed with NTSTATUS 0x{status:X8}.");
            return new(basic.GrantedAccess, (flags & HandleFlagInherit) != 0);
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }
    }

    internal static void MarkDeleteOnClose(SafeFileHandle handle, string resource)
    {
        var disposition = new FileDispositionInfo { DeleteFile = 1 };
        if (!SetFileInformationByHandle(
                handle.DangerousGetHandle(),
                4,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInfo>()))
            throw NewWin32($"Could not mark {resource} for deletion");
    }

    internal static HandleObservation Observe(
        SafeFileHandle handle,
        string resource)
    {
        if (handle.IsInvalid || handle.IsClosed)
            throw new InvalidOperationException($"{resource} handle is unavailable.");

        var raw = handle.DangerousGetHandle();
        if (!GetFileInformationByHandleExAttribute(
                raw, FileAttributeTagInfoClass, out var attributes,
                (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
            !GetFileInformationByHandleExId(
                raw, FileIdInfoClass, out var id,
                (uint)Marshal.SizeOf<FileIdInfo>()) ||
            !GetFileInformationByHandleExStandard(
                raw, FileStandardInfoClass, out var standard,
                (uint)Marshal.SizeOf<FileStandardInfo>()) ||
            !GetHandleInformation(raw, out var handleFlags))
            throw NewWin32($"Could not observe {resource}");

        var status = NtQueryObject(
            raw,
            ObjectBasicInformationClass,
            out var basic,
            (uint)Marshal.SizeOf<ObjectBasicInformation>(),
            out _);
        if (status != StatusSuccess)
            throw new InvalidOperationException(
                $"NtQueryObject failed for {resource} with NTSTATUS 0x{status:X8}.");

        var finalPath = GetFinalPath(raw) ??
            throw new InvalidOperationException($"Final path unavailable for {resource}.");
        return new(
            resource,
            basic.GrantedAccess,
            (handleFlags & HandleFlagInherit) != 0,
            standard.Directory != 0,
            (attributes.FileAttributes & FileAttributeReparsePoint) != 0,
            id.VolumeSerialNumber,
            $"{id.FileId.High:X16}{id.FileId.Low:X16}",
            standard.NumberOfLinks,
            standard.EndOfFile,
            finalPath);
    }

    internal static unsafe VolumeObservation ObserveVolume(SafeFileHandle handle)
    {
        Span<char> filesystemBuffer = stackalloc char[64];
        fixed (char* filesystem = filesystemBuffer)
        {
            if (!GetVolumeInformationByHandle(
                    handle.DangerousGetHandle(),
                    null,
                    0,
                    out var volumeSerial,
                    out _,
                    out _,
                    filesystem,
                    (uint)filesystemBuffer.Length))
                throw NewWin32("Could not observe the opened fixture volume");
            var filesystemLength = filesystemBuffer.IndexOf('\0');
            if (filesystemLength <= 0)
                throw new InvalidDataException("The opened fixture filesystem name is invalid.");

            var finalPath = GetFinalPath(handle.DangerousGetHandle()) ??
                throw new InvalidDataException("The opened fixture final path is unavailable.");
            Span<char> volumePathBuffer = stackalloc char[512];
            fixed (char* volumePath = volumePathBuffer)
            {
                if (!GetVolumePathName(
                        NormalizePath(finalPath), volumePath, (uint)volumePathBuffer.Length))
                    throw NewWin32("Could not resolve the opened fixture volume path");
                var volumePathLength = volumePathBuffer.IndexOf('\0');
                if (volumePathLength <= 0)
                    throw new InvalidDataException("The opened fixture volume path is invalid.");
                var observedVolumePath = new string(volumePathBuffer[..volumePathLength]);
                return new(
                    new string(filesystemBuffer[..filesystemLength]),
                    GetDriveType(observedVolumePath) == 3,
                    volumeSerial,
                    observedVolumePath);
            }
        }
    }

    internal static void RequireObservation(
        HandleObservation observation,
        uint exactAccess,
        bool directory,
        string expectedPath)
    {
        if (observation.GrantedAccess != exactAccess || observation.Inheritable ||
            observation.IsDirectory != directory || observation.IsReparsePoint ||
            !PathsEqual(observation.FinalPath, expectedPath))
            throw new InvalidDataException(
                $"{observation.Resource} observation did not match the fail-closed contract.");
        if (!directory && (observation.NumberOfLinks != 1 || observation.EndOfFile != 0))
            throw new InvalidDataException(
                $"{observation.Resource} was not a distinct empty single-link file.");
    }

    internal static void RequireSameObject(
        HandleObservation left,
        HandleObservation right)
    {
        if (left.VolumeSerialNumber != right.VolumeSerialNumber ||
            !StringComparer.Ordinal.Equals(left.FileId, right.FileId) ||
            left.IsDirectory != right.IsDirectory)
            throw new InvalidDataException($"{left.Resource} and {right.Resource} are not the same object.");
    }

    internal static void RequireSourceObservation(
        HandleObservation observation,
        bool directory,
        string expectedPath)
    {
        if (observation.Inheritable || observation.IsDirectory != directory ||
            observation.IsReparsePoint || !PathsEqual(observation.FinalPath, expectedPath))
            throw new InvalidDataException(
                $"{observation.Resource} source observation did not match the fail-closed contract.");
        if (!directory && (observation.NumberOfLinks != 1 || observation.EndOfFile != 0))
            throw new InvalidDataException(
                $"{observation.Resource} source was not a distinct empty single-link file.");
    }

    internal static bool IsSingleComponent(string value) =>
        !string.IsNullOrWhiteSpace(value) && value is not "." and not ".." &&
        value.IndexOfAny(['\\', '/', ':', '\0']) < 0;

    internal static bool PathsEqual(string observed, string expected) =>
        StringComparer.OrdinalIgnoreCase.Equals(NormalizePath(observed), NormalizePath(expected));

    private static string NormalizePath(string value)
    {
        var normalized = value.StartsWith(@"\\?\", StringComparison.Ordinal) ? value[4..] : value;
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(normalized));
    }

    private static unsafe string? GetFinalPath(nint handle)
    {
        Span<char> initial = stackalloc char[512];
        fixed (char* buffer = initial)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)initial.Length, 0);
            if (length == 0)
                return null;
            if (length < initial.Length)
                return new string(buffer, 0, checked((int)length));
            if (length > 32_767)
                return null;
            var expanded = new char[checked((int)length + 1)];
            fixed (char* expandedBuffer = expanded)
            {
                var expandedLength = GetFinalPathNameByHandle(
                    handle, expandedBuffer, (uint)expanded.Length, 0);
                return expandedLength == 0 || expandedLength >= expanded.Length ? null :
                    new string(expandedBuffer, 0, checked((int)expandedLength));
            }
        }
    }

    private static Win32Exception NewWin32(string message) =>
        new(Marshal.GetLastPInvokeError(), message);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        nint sourceProcess,
        nint sourceHandle,
        nint targetProcess,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(
        SafeHandle pipe,
        out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeServerProcessId(
        SafeHandle pipe,
        out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetHandleInformation(nint handle, out uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleExAttribute(
        nint handle,
        int informationClass,
        out FileAttributeTagInfo information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleExId(
        nint handle,
        int informationClass,
        out FileIdInfo information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleExStandard(
        nint handle,
        int informationClass,
        out FileStandardInfo information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(
        nint handle,
        char* filePath,
        uint filePathLength,
        uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumeInformationByHandleW",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumeInformationByHandle(
        nint file,
        char* volumeNameBuffer,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        char* fileSystemNameBuffer,
        uint fileSystemNameSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetVolumePathName(
        string fileName,
        char* volumePathName,
        uint bufferLength);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string rootPathName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        nint file,
        int fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryObject(
        nint handle,
        int objectInformationClass,
        out ObjectBasicInformation objectInformation,
        uint objectInformationLength,
        out uint returnLength);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        internal ushort Length;
        internal ushort MaximumLength;
        internal nint Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        internal uint Length;
        internal nint RootDirectory;
        internal nint ObjectName;
        internal uint Attributes;
        internal nint SecurityDescriptor;
        internal nint SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        internal nint StatusOrPointer;
        internal nuint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        internal byte DeleteFile;
    }

#pragma warning disable CS0649 // Populated by Windows native APIs.
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        internal long AllocationSize;
        internal long EndOfFile;
        internal uint NumberOfLinks;
        internal byte DeletePending;
        internal byte Directory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectBasicInformation
    {
        internal uint Attributes;
        internal uint GrantedAccess;
        internal uint HandleCount;
        internal uint PointerCount;
        internal uint PagedPoolCharge;
        internal uint NonPagedPoolCharge;
        internal uint Reserved1;
        internal uint Reserved2;
        internal uint Reserved3;
        internal uint NameInformationLength;
        internal uint TypeInformationLength;
        internal uint SecurityDescriptorLength;
        internal long CreationTime;
    }
#pragma warning restore CS0649
}

internal sealed record VolumeObservation(
    string FileSystem,
    bool IsFixed,
    uint VolumeSerialNumber,
    string VolumePath);
