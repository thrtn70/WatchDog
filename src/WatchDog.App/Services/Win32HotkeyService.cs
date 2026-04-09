using System.Windows;
using System.Windows.Interop;
using Microsoft.Extensions.Logging;
using WatchDog.Native.Win32;

using WatchDog.Core.Hotkeys;

namespace WatchDog.App.Services;

public sealed class Win32HotkeyService : IGlobalHotkeyService
{
    private readonly ILogger<Win32HotkeyService> _logger;
    private readonly Dictionary<int, HotkeyConfig> _registeredHotkeys = new();
    private nint _hwnd;
    private HwndSource? _hwndSource;
    private bool _disposed;

    public event Action<int>? HotkeyPressed;

    public Win32HotkeyService(ILogger<Win32HotkeyService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Must be called from the UI thread to create the message-only window.
    /// </summary>
    public void Initialize()
    {
        var parameters = new HwndSourceParameters("WatchDogHotkeyWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0, // Hidden window
        };

        _hwndSource = new HwndSource(parameters);
        _hwnd = _hwndSource.Handle;
        _hwndSource.AddHook(WndProc);

        _logger.LogDebug("Hotkey message window created: {Handle}", _hwnd);
    }

    public bool Register(HotkeyConfig config)
    {
        if (_hwnd == nint.Zero)
        {
            _logger.LogError("Cannot register hotkey: message window not initialized");
            return false;
        }

        var result = User32.RegisterHotKey(
            _hwnd,
            config.Id,
            config.Modifiers | User32.MOD_NOREPEAT,
            config.VirtualKey);

        if (result)
        {
            _registeredHotkeys[config.Id] = config;
            _logger.LogInformation("Registered hotkey {Id}: {Description} (key=0x{Key:X}, mod=0x{Mod:X})",
                config.Id, config.Description, config.VirtualKey, config.Modifiers);
        }
        else
        {
            _logger.LogError("Failed to register hotkey {Id}: {Description}. Key may be in use by another application.",
                config.Id, config.Description);
        }

        return result;
    }

    public bool Unregister(int id)
    {
        if (_hwnd == nint.Zero)
            return false;

        var result = User32.UnregisterHotKey(_hwnd, id);
        _registeredHotkeys.Remove(id);

        if (result)
            _logger.LogDebug("Unregistered hotkey {Id}", id);

        return result;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == User32.WM_HOTKEY)
        {
            var id = wParam.ToInt32();
            _logger.LogDebug("Hotkey pressed: {Id}", id);
            HotkeyPressed?.Invoke(id);
            handled = true;
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var id in _registeredHotkeys.Keys.ToArray())
            Unregister(id);

        _hwndSource?.RemoveHook(WndProc);
        _hwndSource?.Dispose();
    }
}
