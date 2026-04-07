using System.Runtime.InteropServices;

namespace TikrClipr.Native.Win32;

public static partial class Kernel32
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
}
