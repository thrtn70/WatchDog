using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WatchDog.Native.Win32;

/// <summary>
/// Enumerates visible top-level windows for manual capture selection.
/// </summary>
public static partial class WindowEnumerator
{
    /// <summary>Information about a visible top-level window.</summary>
    public sealed record WindowInfo(
        IntPtr Handle,
        string Title,
        string ExecutableName,
        int ProcessId,
        string? ClassName);

    /// <summary>
    /// Returns all visible top-level windows with non-empty titles,
    /// excluding system windows and the calling application.
    /// </summary>
    public static IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var windows = new List<WindowInfo>();
        var currentPid = Environment.ProcessId;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd))
                return true; // continue enumeration

            // Skip windows with empty/whitespace titles
            var title = GetWindowTitle(hWnd);
            if (string.IsNullOrWhiteSpace(title))
                return true;

            // Skip cloaked windows (UWP suspended apps, virtual desktops)
            if (IsWindowCloaked(hWnd))
                return true;

            // Get process info
            GetWindowThreadProcessId(hWnd, out var processId);

            // Skip our own windows
            if (processId == currentPid)
                return true;

            // Get executable name
            var exeName = GetProcessExecutableName(processId);
            if (exeName is null)
                return true;

            // Skip known system/shell processes
            if (IsSystemWindow(exeName))
                return true;

            // Get window class name
            var className = GetWindowClassName(hWnd);

            windows.Add(new WindowInfo(hWnd, title, exeName, processId, className));

            return true; // continue enumeration
        }, IntPtr.Zero);

        return windows;
    }

    // ── P/Invoke declarations ────────────────────────────────────────────

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true)]
    private static unsafe partial int GetWindowText(IntPtr hWnd, char* lpString, int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    private static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    private static unsafe partial int GetClassName(IntPtr hWnd, char* lpClassName, int nMaxCount);

    // DwmGetWindowAttribute — detect cloaked windows
    private const int DWMWA_CLOAKED = 14;

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    // ── Helpers ──────────────────────────────────────────────────────────

    private static unsafe string? GetWindowTitle(IntPtr hWnd)
    {
        var length = GetWindowTextLength(hWnd);
        if (length <= 0) return null;

        const int maxStackAlloc = 512;
        var bufLen = length + 1;
        if (bufLen > maxStackAlloc)
            bufLen = maxStackAlloc;

        char* buffer = stackalloc char[bufLen];
        var result = GetWindowText(hWnd, buffer, bufLen);
        return result > 0 ? new string(buffer, 0, result) : null;
    }

    private static unsafe string? GetWindowClassName(IntPtr hWnd)
    {
        const int maxLen = 256;
        char* buffer = stackalloc char[maxLen];
        var result = GetClassName(hWnd, buffer, maxLen);
        return result > 0 ? new string(buffer, 0, result) : null;
    }

    private static bool IsWindowCloaked(IntPtr hWnd)
    {
        var hr = DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out var cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    private static string? GetProcessExecutableName(int processId)
    {
        try
        {
            using var proc = Process.GetProcessById(processId);
            return proc.ProcessName + ".exe";
        }
        catch
        {
            return null; // Access denied or process exited
        }
    }

    /// <summary>
    /// Filters out shell/system processes that shouldn't appear in the window picker.
    /// </summary>
    private static bool IsSystemWindow(string executableName)
    {
        var normalized = executableName.ToLowerInvariant();
        return normalized is
            "explorer.exe" or
            "shellexperiencehost.exe" or
            "startmenuexperiencehost.exe" or
            "searchhost.exe" or
            "applicationframehost.exe" or
            "systemsettings.exe" or
            "textinputhost.exe" or
            "lockapp.exe" or
            "dwm.exe";
    }
}
