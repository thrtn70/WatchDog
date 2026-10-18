using System.Collections.ObjectModel;
using System.Runtime.InteropServices;

namespace WatchDog.CaptureIsolation.Windows;

/// <summary>
/// Frozen allowlist consumed by the CreateProcessW boundary. The job handle is used only by
/// PROC_THREAD_ATTRIBUTE_JOB_LIST and is never inherited. An empty child-handle list preserves
/// the legacy one-attribute, inherit-disabled launch.
/// </summary>
internal sealed class WindowsLaunchAttributePlan
{
    internal const int RequiredBootstrapHandleCount = 2;

    private readonly ReadOnlyCollection<SafeHandle> _inheritedChildHandles;

    internal WindowsLaunchAttributePlan(
        SafeWindowsHandle jobHandle,
        IEnumerable<SafeHandle>? inheritedChildHandles = null)
    {
        JobHandle = jobHandle ?? throw new ArgumentNullException(nameof(jobHandle));
        if (jobHandle.IsInvalid || jobHandle.IsClosed)
            throw new HelperBoundaryException("helper.windows.launch.job-handle-invalid");

        var handles = inheritedChildHandles?.ToArray() ?? [];
        if (handles.Length is not (0 or RequiredBootstrapHandleCount))
            throw new HelperBoundaryException("helper.windows.launch.handle-list-invalid");
        var rawHandles = new HashSet<nint>();
        var jobValue = jobHandle.DangerousGetHandle();
        foreach (var handle in handles)
        {
            if (handle is null || handle.IsInvalid || handle.IsClosed)
                throw new HelperBoundaryException("helper.windows.launch.handle-list-invalid");
            var value = handle.DangerousGetHandle();
            if (value == jobValue || !rawHandles.Add(value))
                throw new HelperBoundaryException("helper.windows.launch.handle-list-invalid");
        }
        _inheritedChildHandles = Array.AsReadOnly(handles);
    }

    internal SafeWindowsHandle JobHandle { get; }
    internal IReadOnlyList<SafeHandle> InheritedChildHandles => _inheritedChildHandles;
    internal uint AttributeCount => InheritHandles ? 2u : 1u;
    internal bool InheritHandles => _inheritedChildHandles.Count != 0;

    internal WindowsInheritedHandleLease AcquireInheritedHandles() =>
        new(_inheritedChildHandles);
}

/// <summary>
/// Pins SafeHandle lifetimes across attribute construction and CreateProcessW. It does not own
/// or close the underlying handles.
/// </summary>
internal sealed class WindowsInheritedHandleLease : IDisposable
{
    private readonly SafeHandle[] _handles;
    private int _acquiredCount;
    private bool _disposed;

    internal WindowsInheritedHandleLease(IReadOnlyList<SafeHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        _handles = handles.ToArray();
        RawHandles = new nint[_handles.Length];
        try
        {
            for (var index = 0; index < _handles.Length; index++)
            {
                var added = false;
                _handles[index].DangerousAddRef(ref added);
                if (!added)
                    throw new HelperBoundaryException("helper.windows.launch.handle-retain-failed");
                _acquiredCount++;
                RawHandles[index] = _handles[index].DangerousGetHandle();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal nint[] RawHandles { get; }

    public void Dispose()
    {
        if (_disposed)
            return;
        for (var index = _acquiredCount - 1; index >= 0; index--)
            _handles[index].DangerousRelease();
        Array.Clear(RawHandles);
        _acquiredCount = 0;
        _disposed = true;
    }
}
