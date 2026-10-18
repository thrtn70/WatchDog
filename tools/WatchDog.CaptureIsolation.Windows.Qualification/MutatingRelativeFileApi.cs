using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal sealed class MutatingRelativeFileApi(
    IWindowsRelativeFileApi inner,
    Action mutation) : IWindowsRelativeFileApi
{
    private bool _mutated;

    public SafeWindowsHandle CreateNew(
        SafeWindowsHandle rootDirectory,
        string singleComponentName)
    {
        if (!_mutated)
        {
            mutation();
            _mutated = true;
        }
        return inner.CreateNew(rootDirectory, singleComponentName);
    }

    public WindowsRelativeFileSnapshot Observe(SafeWindowsHandle file) => inner.Observe(file);

    public void Write(SafeWindowsHandle file, ReadOnlySpan<byte> content) =>
        inner.Write(file, content);

    public byte[] ReadAll(SafeWindowsHandle file, int maximumBytes) =>
        inner.ReadAll(file, maximumBytes);

    public void Flush(SafeWindowsHandle file) => inner.Flush(file);

    public bool MarkDeleteOnClose(SafeWindowsHandle file) => inner.MarkDeleteOnClose(file);

    public bool Close(nint handle) => inner.Close(handle);
}
