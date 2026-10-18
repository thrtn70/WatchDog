using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

/// <summary>
/// Windows implementation for the unregistered B1 observation fixture. The implementation
/// returns point-in-time handle evidence only. It performs no journal writes or B1 effects.
/// </summary>
internal sealed partial class FixtureB1NativeJournalObservationNative :
    IFixtureB1NativeJournalObservationApi
{
    private const uint FileReadData = 0x00000001;
    private const uint FileListDirectory = 0x00000001;
    private const uint FileWriteData = 0x00000002;
    private const uint FileAppendData = 0x00000004;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileWriteAttributes = 0x00000100;
    private const uint ReadControl = 0x00020000;
    private const uint Synchronize = 0x00100000;

    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint OpenExisting = 3;
    private const uint DriveFixed = 3;

    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint ObjectDontReparse = 0x00001000;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint FileOpenReparsePoint = 0x00200000;

    private const int FileAttributeTagInfoClass = 9;
    private const int FileIdInfoClass = 18;
    private const int FileStandardInfoClass = 1;
    private const int FileStreamInfoClass = 7;
    private const int ObjectBasicInformationClass = 0;
    private const int FileNamesInformationClass = 12;

    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const int SeFileObject = 1;

    private const int StatusSuccess = 0;
    private const int StatusNoMoreFiles = unchecked((int)0x80000006);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    private const int StatusReparsePointEncountered = unchecked((int)0xC000050B);
    private const int StatusStoppedOnSymlink = unchecked((int)0x8000002D);
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);

    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const int MaximumDirectoryEntries = 1_024;
    private const int DirectoryBufferBytes = 64 * 1024;

    internal static FixtureB1NativeJournalObservationNative Instance { get; } = new();

    private FixtureB1NativeJournalObservationNative()
    {
    }

    public FixtureB1NativeJournalHandleResult OpenVolumeRoot(string canonicalDriveRoot)
    {
        if (!OperatingSystem.IsWindows() || !IsCanonicalDriveRoot(canonicalDriveRoot))
            return Failure("native-journal.volume-root-invalid");
        var raw = nint.Zero;
        try
        {
            if (GetDriveType(canonicalDriveRoot) != DriveFixed)
                return Failure("native-journal.volume-not-fixed");
            raw = CreateFile(
                canonicalDriveRoot,
                FileListDirectory | FileReadAttributes | ReadControl | Synchronize,
                FileShareRead | FileShareWrite | FileShareDelete,
                nint.Zero,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                nint.Zero);
            if (raw is 0 or -1)
                return Failure(FailureCodeForWin32(Marshal.GetLastPInvokeError(),
                    "native-journal.volume-open-failed"));
            if (!TryGetFilesystemName(raw, out var filesystem))
                return RetainedFailure(raw, "native-journal.filesystem-unavailable");
            if (!IsSupportedFilesystem(filesystem))
                return RetainedFailure(raw, "native-journal.filesystem-unsupported");
            return Success(new NativeHandle(raw));
        }
        catch
        {
            return RetainedFailure(raw, "native-journal.volume-open-threw");
        }
    }

    public FixtureB1NativeJournalHandleResult OpenDirectoryRelative(
        IFixtureB1NativeJournalHandle parent,
        string singleComponentName)
    {
        if (!TryHandle(parent, out var native, out var failure))
            return Failure(failure);
        return CreateRelative(native, singleComponentName,
            FileListDirectory | FileReadAttributes | ReadControl | Synchronize,
            FileShareRead | FileShareWrite | FileShareDelete,
            FileOpen, FileAttributeDirectory,
            FileSynchronousIoNonAlert | FileDirectoryFile | FileOpenReparsePoint,
            null);
    }

    public FixtureB1NativeJournalHandleResult CreatePrivateDirectoryRelative(
        IFixtureB1NativeJournalHandle trustedParent,
        string singleComponentName,
        byte[] selfRelativeSecurityDescriptor)
    {
        if (!TryHandle(trustedParent, out var native, out var failure))
            return Failure(failure);
        return CreateRelative(native, singleComponentName,
            FileListDirectory | FileReadAttributes | ReadControl | Synchronize,
            FileShareRead, FileCreate, FileAttributeDirectory,
            FileSynchronousIoNonAlert | FileDirectoryFile | FileOpenReparsePoint,
            selfRelativeSecurityDescriptor);
    }

    public FixtureB1NativeJournalHandleResult CreateFileRelative(
        IFixtureB1NativeJournalHandle root,
        string singleComponentName,
        byte[] selfRelativeSecurityDescriptor)
    {
        if (!TryHandle(root, out var native, out var failure))
            return Failure(failure);
        return CreateRelative(native, singleComponentName,
            FileReadData | FileWriteData | FileAppendData | FileReadAttributes |
                FileWriteAttributes | ReadControl | Synchronize,
            FileShareRead, FileCreate, FileAttributeNormal,
            FileSynchronousIoNonAlert | FileNonDirectoryFile | FileOpenReparsePoint,
            selfRelativeSecurityDescriptor);
    }

    public FixtureB1NativeJournalHandleResult DuplicateReadOnly(
        IFixtureB1NativeJournalHandle source)
    {
        if (!TryHandle(source, out var native, out var failure))
            return Failure(failure);
        var duplicated = nint.Zero;
        try
        {
            var process = GetCurrentProcess();
            var ok = DuplicateHandle(
                process,
                native.Raw,
                process,
                out duplicated,
                FileReadData | FileReadAttributes | ReadControl | Synchronize,
                false,
                0);
            if (!ok)
            {
                var code = FailureCodeForWin32(Marshal.GetLastPInvokeError(),
                    "native-journal.duplicate-failed");
                return RetainedFailure(duplicated, code);
            }
            if (duplicated is 0 or -1)
                return Failure("native-journal.duplicate-invalid");
            return Success(new NativeHandle(duplicated));
        }
        catch
        {
            return RetainedFailure(duplicated, "native-journal.duplicate-threw");
        }
    }

    public FixtureB1NativeJournalObservationResult Observe(
        IFixtureB1NativeJournalHandle handle)
    {
        if (!TryHandle(handle, out var native, out var failure))
            return new(null, failure);
        try
        {
            if (!GetFileInformationByHandleExAttribute(
                    native.Raw, FileAttributeTagInfoClass, out var attributes,
                    (uint)Marshal.SizeOf<FileAttributeTagInfo>()) ||
                !GetFileInformationByHandleExId(
                    native.Raw, FileIdInfoClass, out var id,
                    (uint)Marshal.SizeOf<FileIdInfo>()) ||
                !GetFileInformationByHandleExStandard(
                    native.Raw, FileStandardInfoClass, out var standard,
                    (uint)Marshal.SizeOf<FileStandardInfo>()))
                return new(null, FailureCodeForWin32(Marshal.GetLastPInvokeError(),
                    "native-journal.identity-observation-failed"));

            var objectStatus = NtQueryObject(
                native.Raw,
                ObjectBasicInformationClass,
                out var basic,
                (uint)Marshal.SizeOf<ObjectBasicInformation>(),
                out _);
            if (objectStatus != StatusSuccess)
                return new(null, "native-journal.granted-access-unavailable");

            var security = ObserveSecurity(native.Raw);
            if (!security.Succeeded)
                return new(null, security.FailureCode);
            var finalPath = GetFinalPath(native.Raw);
            if (finalPath is null)
                return new(null, "native-journal.final-path-unavailable");
            var kind = standard.Directory != 0 ? FixtureB1NativeJournalObjectKind.Directory :
                FixtureB1NativeJournalObjectKind.File;
            var streams = kind == FixtureB1NativeJournalObjectKind.File ?
                ObserveStreams(native.Raw) : new FixtureB1NativeJournalStreamsResult([], null);
            if (!streams.Succeeded)
                return new(null, streams.FailureCode);

            return new(new(
                kind,
                attributes.FileAttributes,
                attributes.ReparseTag,
                id.VolumeSerialNumber,
                id.FileId.Low,
                id.FileId.High,
                standard.NumberOfLinks,
                standard.DeletePending != 0,
                standard.EndOfFile,
                standard.AllocationSize,
                basic.GrantedAccess,
                finalPath,
                security.Observation!,
                streams.StreamNames!), null);
        }
        catch
        {
            return new(null, "native-journal.observation-threw");
        }
    }

    public FixtureB1NativeJournalDirectoryEntriesResult EnumerateDirectoryEntries(
        IFixtureB1NativeJournalHandle directory)
    {
        if (!TryHandle(directory, out var native, out var failure))
            return new(null, failure);
        var buffer = Marshal.AllocHGlobal(DirectoryBufferBytes);
        try
        {
            var names = new List<string>();
            var restart = true;
            while (true)
            {
                var status = NtQueryDirectoryFile(
                    native.Raw,
                    nint.Zero,
                    nint.Zero,
                    nint.Zero,
                    out var io,
                    buffer,
                    DirectoryBufferBytes,
                    FileNamesInformationClass,
                    false,
                    nint.Zero,
                    restart);
                restart = false;
                if (status == StatusNoMoreFiles)
                    break;
                if (status is not StatusSuccess and not StatusBufferOverflow ||
                    io.Information == 0 || io.Information > DirectoryBufferBytes)
                    return new(null, "native-journal.directory-enumeration-failed");
                ParseDirectoryNames(buffer, checked((int)io.Information), names);
                if (names.Count > MaximumDirectoryEntries)
                    return new(null, "native-journal.directory-entry-bound");
            }
            return new(names, null);
        }
        catch
        {
            return new(null, "native-journal.directory-enumeration-threw");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public unsafe FixtureB1NativeJournalBytesResult ReadExact(
        IFixtureB1NativeJournalHandle file, int expectedLength)
    {
        if (!TryHandle(file, out var native, out var failure))
            return new(null, failure);
        if (expectedLength < 0)
            return new(null, "native-journal.read-length-invalid");
        try
        {
            if (!SetFilePointerEx(native.Raw, 0, out _, 0))
                return new(null, "native-journal.read-seek-failed");
            var bytes = new byte[expectedLength];
            if (expectedLength == 0)
            {
                byte probe = 0;
                if (!ReadFile(native.Raw, &probe, 1, out var atEnd, nint.Zero) ||
                    atEnd != 0)
                    return new(null, "native-journal.read-empty-failed");
                return new(bytes, null);
            }
            fixed (byte* buffer = bytes)
            {
                var offset = 0;
                while (offset < bytes.Length)
                {
                    if (!ReadFile(native.Raw, buffer + offset,
                            checked((uint)(bytes.Length - offset)), out var read,
                            nint.Zero) || read == 0 || read > bytes.Length - offset)
                        return new(null, "native-journal.read-incomplete");
                    offset = checked(offset + (int)read);
                }
            }
            return new(bytes, null);
        }
        catch
        {
            return new(null, "native-journal.read-threw");
        }
    }

    public FixtureB1NativeJournalCloseResult Close(IFixtureB1NativeJournalHandle handle)
    {
        if (handle is not NativeHandle native)
            return new(FixtureB1NativeJournalCloseDisposition.Uncertain,
                "native-journal.handle-foreign");
        if (!native.TryBeginClose())
            return new(FixtureB1NativeJournalCloseDisposition.Uncertain,
                "native-journal.close-already-attempted");
        try
        {
            if (CloseHandle(native.Raw))
                return new(FixtureB1NativeJournalCloseDisposition.Closed, null);
            return new(FixtureB1NativeJournalCloseDisposition.Uncertain,
                FailureCodeForWin32(Marshal.GetLastPInvokeError(),
                    "native-journal.close-uncertain"));
        }
        catch
        {
            return new(FixtureB1NativeJournalCloseDisposition.Uncertain,
                "native-journal.close-threw");
        }
    }

    private static unsafe FixtureB1NativeJournalHandleResult CreateRelative(
        NativeHandle parent,
        string singleComponentName,
        uint desiredAccess,
        uint shareAccess,
        uint disposition,
        uint attributes,
        uint options,
        byte[]? securityDescriptor)
    {
        try
        {
            FixtureB1NativeJournalObservation.ValidateComponent(singleComponentName);
        }
        catch
        {
            return Failure("native-journal.component-invalid");
        }
        if (securityDescriptor is { Length: 0 })
            return Failure("native-journal.security-descriptor-invalid");
        var raw = nint.Zero;
        try
        {
            var byteLength = checked(singleComponentName.Length * sizeof(char));
            fixed (char* nameBuffer = singleComponentName)
            fixed (byte* securityBuffer = securityDescriptor)
            {
                var name = new UnicodeString
                {
                    Length = checked((ushort)byteLength),
                    MaximumLength = checked((ushort)byteLength),
                    Buffer = (nint)nameBuffer,
                };
                var objectAttributes = new ObjectAttributes
                {
                    Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                    RootDirectory = parent.Raw,
                    ObjectName = (nint)(&name),
                    Attributes = ObjectCaseInsensitive | ObjectDontReparse,
                    SecurityDescriptor = securityDescriptor is null ? nint.Zero :
                        (nint)securityBuffer,
                };
                var status = NtCreateFile(
                    out raw,
                    desiredAccess,
                    ref objectAttributes,
                    out _,
                    nint.Zero,
                    attributes,
                    shareAccess,
                    disposition,
                    options,
                    nint.Zero,
                    0);
                if (status != StatusSuccess || raw is 0 or -1)
                    return RetainedFailure(raw, FailureCodeForStatus(status));
                return Success(new NativeHandle(raw));
            }
        }
        catch
        {
            return RetainedFailure(raw, "native-journal.relative-create-threw");
        }
    }

    private static FixtureB1NativeJournalSecurityResult ObserveSecurity(nint handle)
    {
        var error = GetSecurityInfo(
            handle,
            SeFileObject,
            OwnerSecurityInformation | DaclSecurityInformation,
            out var owner,
            out _,
            out _,
            out _,
            out var descriptor);
        if (error != 0 || descriptor == nint.Zero || owner == nint.Zero)
        {
            if (descriptor != nint.Zero)
                _ = LocalFree(descriptor);
            return new(null, "native-journal.security-observation-failed");
        }
        try
        {
            var length = GetSecurityDescriptorLength(descriptor);
            if (length == 0 || length > 64 * 1024)
                return new(null, "native-journal.security-descriptor-invalid");
            var bytes = new byte[checked((int)length)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var raw = new RawSecurityDescriptor(bytes, 0);
            var ownerSid = new SecurityIdentifier(owner).Value;
            var dacl = new List<FixtureB1NativeJournalAceObservation>();
            if (raw.DiscretionaryAcl is null)
                return new(null, "native-journal.dacl-missing");
            foreach (GenericAce ace in raw.DiscretionaryAcl)
            {
                if (ace is not CommonAce common || common.SecurityIdentifier is null ||
                    common.IsCallback || common.OpaqueLength != 0)
                    return new(null, "native-journal.dacl-ace-unsupported");
                var kind = common.AceQualifier switch
                {
                    AceQualifier.AccessAllowed => FixtureB1NativeJournalAceKind.Allow,
                    AceQualifier.AccessDenied => FixtureB1NativeJournalAceKind.Deny,
                    _ => (FixtureB1NativeJournalAceKind?)null,
                };
                if (kind is null)
                    return new(null, "native-journal.dacl-ace-unsupported");
                dacl.Add(new(kind.Value, common.SecurityIdentifier.Value,
                    unchecked((uint)common.AccessMask), (byte)common.AceFlags));
            }
            return new(new(ownerSid,
                (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0,
                dacl), null);
        }
        catch
        {
            return new(null, "native-journal.security-parse-failed");
        }
        finally
        {
            _ = LocalFree(descriptor);
        }
    }

    private static FixtureB1NativeJournalStreamsResult ObserveStreams(nint handle)
    {
        var size = 1024;
        while (size <= 64 * 1024)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (GetFileInformationByHandleExBuffer(
                        handle, FileStreamInfoClass, buffer, (uint)size))
                    return new(ParseStreams(buffer, size), null);
                var error = Marshal.GetLastPInvokeError();
                if (error is not ErrorInsufficientBuffer and not ErrorMoreData)
                    return new(null, "native-journal.stream-observation-failed");
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            size *= 2;
        }
        return new(null, "native-journal.stream-observation-bound");
    }

    private static IReadOnlyList<string> ParseStreams(nint buffer, int bufferLength)
    {
        var streams = new List<string>();
        var offset = 0;
        while (true)
        {
            if (offset < 0 || offset > bufferLength - 24)
                throw new InvalidDataException();
            var entry = nint.Add(buffer, offset);
            var next = Marshal.ReadInt32(entry, 0);
            var nameBytes = Marshal.ReadInt32(entry, 4);
            if (nameBytes < 0 || (nameBytes & 1) != 0 || nameBytes > bufferLength - offset - 24)
                throw new InvalidDataException();
            var name = Marshal.PtrToStringUni(nint.Add(entry, 24), nameBytes / 2) ?? string.Empty;
            streams.Add(name);
            if (next == 0)
                break;
            if (next < 24 || next > bufferLength - offset)
                throw new InvalidDataException();
            offset = checked(offset + next);
        }
        return streams;
    }

    private static void ParseDirectoryNames(nint buffer, int bufferLength, List<string> names)
    {
        var offset = 0;
        while (true)
        {
            if (offset < 0 || offset > bufferLength - 12)
                throw new InvalidDataException();
            var entry = nint.Add(buffer, offset);
            var next = Marshal.ReadInt32(entry, 0);
            var nameBytes = Marshal.ReadInt32(entry, 8);
            if (nameBytes < 0 || (nameBytes & 1) != 0 || nameBytes > bufferLength - offset - 12)
                throw new InvalidDataException();
            var name = Marshal.PtrToStringUni(nint.Add(entry, 12), nameBytes / 2) ?? string.Empty;
            if (name is not "." and not "..")
                names.Add(name);
            if (next == 0)
                break;
            if (next < 12 || next > bufferLength - offset)
                throw new InvalidDataException();
            offset = checked(offset + next);
        }
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

    private static bool TryHandle(
        IFixtureB1NativeJournalHandle handle,
        out NativeHandle native,
        out string failure)
    {
        if (handle is NativeHandle candidate && candidate.IsOpen)
        {
            native = candidate;
            failure = string.Empty;
            return true;
        }
        native = null!;
        failure = handle is NativeHandle ? "native-journal.handle-closed" :
            "native-journal.handle-foreign";
        return false;
    }

    private static bool IsCanonicalDriveRoot(string value) =>
        value.Length == 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' &&
        value[2] == '\\';

    internal static bool IsSupportedFilesystem(string? filesystem) =>
        StringComparer.OrdinalIgnoreCase.Equals(filesystem, "NTFS");

    private static unsafe bool TryGetFilesystemName(nint volumeRoot, out string filesystem)
    {
        Span<char> name = stackalloc char[64];
        fixed (char* buffer = name)
        {
            if (!GetVolumeInformationByHandle(volumeRoot, null, 0, out _, out _,
                    out _, buffer, (uint)name.Length))
            {
                filesystem = string.Empty;
                return false;
            }
        }
        var terminator = name.IndexOf('\0');
        if (terminator <= 0)
        {
            filesystem = string.Empty;
            return false;
        }
        filesystem = new string(name[..terminator]);
        return true;
    }

    private static FixtureB1NativeJournalHandleResult Success(NativeHandle handle) =>
        new(handle, null);

    private static FixtureB1NativeJournalHandleResult Failure(string code) =>
        new(null, code);

    private static FixtureB1NativeJournalHandleResult RetainedFailure(nint raw, string code) =>
        raw is 0 or -1 ? Failure(code) : new(new NativeHandle(raw), code);

    private static string FailureCodeForStatus(int status) => status switch
    {
        StatusObjectNameCollision => "native-journal.already-exists",
        StatusReparsePointEncountered or StatusStoppedOnSymlink =>
            "native-journal.reparse-rejected",
        StatusAccessDenied => "native-journal.access-denied",
        StatusObjectNameNotFound => "native-journal.parent-stale",
        _ => "native-journal.relative-create-failed",
    };

    private static string FailureCodeForWin32(int error, string fallback) => error switch
    {
        2 or 3 => "native-journal.object-not-found",
        5 => "native-journal.access-denied",
        6 => "native-journal.handle-invalid",
        _ => fallback,
    };

    private sealed class NativeHandle(nint raw) : IFixtureB1NativeJournalHandle
    {
        private int _closeStarted;

        internal nint Raw { get; } = raw;
        public Guid LocalHandleId { get; } = Guid.NewGuid();
        internal bool IsOpen => Volatile.Read(ref _closeStarted) == 0;
        internal bool TryBeginClose() => Interlocked.CompareExchange(ref _closeStarted, 1, 0) == 0;
    }

    private sealed record FixtureB1NativeJournalSecurityResult(
        FixtureB1NativeJournalSecurityObservation? Observation,
        string? FailureCode)
    {
        internal bool Succeeded => Observation is not null && FailureCode is null;
    }

    private sealed record FixtureB1NativeJournalStreamsResult(
        IReadOnlyList<string>? StreamNames,
        string? FailureCode)
    {
        internal bool Succeeded => StreamNames is not null && FailureCode is null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetDriveTypeW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveType(string rootPathName);

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

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

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
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFilePointerEx(
        nint file, long distanceToMove, out long newFilePointer, uint moveMethod);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadFile(
        nint file, byte* buffer, uint numberOfBytesToRead, out uint numberOfBytesRead,
        nint overlapped);

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

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleExBuffer(
        nint handle,
        int informationClass,
        nint information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(
        nint handle,
        char* filePath,
        uint filePathLength,
        uint flags);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(
        nint handle,
        int objectType,
        uint securityInformation,
        out nint owner,
        out nint group,
        out nint dacl,
        out nint sacl,
        out nint securityDescriptor);

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityDescriptorLength(nint securityDescriptor);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

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

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryDirectoryFile(
        nint fileHandle,
        nint eventHandle,
        nint apcRoutine,
        nint apcContext,
        out IoStatusBlock ioStatusBlock,
        nint fileInformation,
        uint length,
        int fileInformationClass,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry,
        nint fileName,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);

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
