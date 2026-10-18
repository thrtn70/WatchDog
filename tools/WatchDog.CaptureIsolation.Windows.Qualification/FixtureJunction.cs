using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal static partial class FixtureJunction
{
    private const uint GenericWrite = 0x40000000;
    private const uint ShareAll = 0x00000001 | 0x00000002 | 0x00000004;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint SetReparsePoint = 0x000900A4;
    private const uint DeleteReparsePoint = 0x000900AC;
    private const uint MountPointTag = 0xA0000003;

    internal static void Set(string directoryPath, string targetPath)
    {
        var substitute = @"\??\" + Path.TrimEndingDirectorySeparator(targetPath);
        var print = Path.TrimEndingDirectorySeparator(targetPath);
        var substituteBytes = Encoding.Unicode.GetBytes(substitute);
        var printBytes = Encoding.Unicode.GetBytes(print);
        var pathBytes = checked(substituteBytes.Length + 2 + printBytes.Length + 2);
        var dataLength = checked(8 + pathBytes);
        var buffer = new byte[checked(8 + dataLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), MountPointTag);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(4, 2),
            checked((ushort)dataLength));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(10, 2),
            checked((ushort)substituteBytes.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(12, 2),
            checked((ushort)(substituteBytes.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(
            buffer.AsSpan(14, 2),
            checked((ushort)printBytes.Length));
        substituteBytes.CopyTo(buffer, 16);
        printBytes.CopyTo(buffer, checked(16 + substituteBytes.Length + 2));

        using var handle = Open(directoryPath);
        if (!DeviceIoControl(
                handle,
                SetReparsePoint,
                buffer,
                checked((uint)buffer.Length),
                nint.Zero,
                0,
                out _,
                nint.Zero))
        {
            throw new QualificationAssertionException("fixture.media-root.reparse-set-unavailable");
        }
    }

    internal static void Delete(string directoryPath)
    {
        var buffer = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), MountPointTag);
        using var handle = Open(directoryPath);
        if (!DeviceIoControl(
                handle,
                DeleteReparsePoint,
                buffer,
                checked((uint)buffer.Length),
                nint.Zero,
                0,
                out _,
                nint.Zero))
        {
            throw new QualificationCleanupException("fixture.media-root.reparse-delete-failed");
        }
    }

    private static SafeFileHandle Open(string path)
    {
        var handle = CreateFile(
            path,
            GenericWrite,
            ShareAll,
            nint.Zero,
            OpenExisting,
            BackupSemantics | OpenReparsePoint,
            nint.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new QualificationAssertionException("fixture.media-root.reparse-open-unavailable");
        }
        return handle;
    }

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
    private static partial bool DeviceIoControl(
        SafeFileHandle device,
        uint controlCode,
        byte[] inputBuffer,
        uint inputBufferSize,
        nint outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        nint overlapped);
}
