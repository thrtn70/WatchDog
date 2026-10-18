using Microsoft.Win32.SafeHandles;

namespace WatchDog.CaptureIsolation.Windows;

internal interface IWindowsHandleCloser
{
    bool Close(nint handle);
}

internal sealed class SafeWindowsHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly object _closeSync = new();
    private readonly IWindowsHandleCloser _closer;
    private bool _explicitlyClosed;

    internal SafeWindowsHandle(nint handle, IWindowsHandleCloser closer)
        : base(ownsHandle: true)
    {
        _closer = closer ?? throw new ArgumentNullException(nameof(closer));
        SetHandle(handle);
    }

    internal bool CloseOnce()
    {
        lock (_closeSync)
        {
            if (_explicitlyClosed)
                return true;
            if (IsClosed || IsInvalid)
                return false;
            if (!TryCloseNative())
                return false;
            _explicitlyClosed = true;
            SetHandleAsInvalid();
            return true;
        }
    }

    protected override bool ReleaseHandle() => TryCloseNative();

    private bool TryCloseNative()
    {
        try
        {
            return _closer.Close(handle);
        }
        catch
        {
            return false;
        }
    }
}
