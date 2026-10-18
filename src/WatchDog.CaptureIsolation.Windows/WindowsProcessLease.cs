namespace WatchDog.CaptureIsolation.Windows;

/// <summary>
/// Nonthrowing ownership sink used immediately after CreateProcessW succeeds. The exact process
/// and primary-thread handles are retained before cancellation, identity queries, or validation.
/// </summary>
internal interface IWindowsProcessOwnershipSink
{
    void CaptureCreatedProcess(
        SafeWindowsHandle processHandle,
        SafeWindowsHandle primaryThreadHandle,
        uint processId,
        uint primaryThreadId);
}

internal readonly record struct WindowsProcessCloseResult(
    bool ProcessClosed,
    bool PrimaryThreadClosed);

internal sealed class WindowsProcessLease : IDisposable, IWindowsProcessOwnershipSink
{
    private readonly object _sync = new();
    private SafeWindowsHandle? _processHandle;
    private SafeWindowsHandle? _primaryThreadHandle;
    private uint _processId;
    private uint _primaryThreadId;
    private long? _creationFileTime;
    private bool _created;
    private bool _metadataVerified;
    private bool _resumeAttempted;
    private bool _processClosed;
    private bool _primaryThreadClosed;
    private bool _released;

    internal bool IsCreated
    {
        get
        {
            lock (_sync)
                return _created;
        }
    }

    internal bool IsMetadataVerified
    {
        get
        {
            lock (_sync)
                return _metadataVerified;
        }
    }

    internal uint ProcessId
    {
        get
        {
            lock (_sync)
                return _processId;
        }
    }

    internal uint PrimaryThreadId
    {
        get
        {
            lock (_sync)
                return _primaryThreadId;
        }
    }

    internal long? CreationFileTime
    {
        get
        {
            lock (_sync)
                return _creationFileTime;
        }
    }

    internal SafeWindowsHandle ProcessHandle
    {
        get
        {
            lock (_sync)
                return _processHandle ?? throw new InvalidOperationException(
                    "The process lease does not own a created process.");
        }
    }

    internal SafeWindowsHandle PrimaryThreadHandle
    {
        get
        {
            lock (_sync)
                return _primaryThreadHandle ?? throw new InvalidOperationException(
                    "The process lease does not own a primary thread.");
        }
    }

    public void CaptureCreatedProcess(
        SafeWindowsHandle processHandle,
        SafeWindowsHandle primaryThreadHandle,
        uint processId,
        uint primaryThreadId)
    {
        ArgumentNullException.ThrowIfNull(processHandle);
        ArgumentNullException.ThrowIfNull(primaryThreadHandle);
        lock (_sync)
        {
            if (_created || _released)
            {
                throw new InvalidOperationException("The process ownership sink is unavailable.");
            }
            _processHandle = processHandle;
            _primaryThreadHandle = primaryThreadHandle;
            _processId = processId;
            _primaryThreadId = primaryThreadId;
            _created = true;
        }
    }

    internal bool TryVerifyMetadata(
        uint observedProcessId,
        uint observedThreadId,
        long observedCreationFileTime)
    {
        lock (_sync)
        {
            if (!_created || _released || _metadataVerified || observedProcessId == 0 ||
                observedThreadId == 0 || observedCreationFileTime <= 0 ||
                observedProcessId != _processId || observedThreadId != _primaryThreadId)
            {
                return false;
            }
            _creationFileTime = observedCreationFileTime;
            _metadataVerified = true;
            return true;
        }
    }

    internal SafeWindowsHandle BeginResume()
    {
        lock (_sync)
        {
            if (!_created || _released || _resumeAttempted || _primaryThreadHandle is null)
                throw new HelperBoundaryException("helper.windows.process.resume-unavailable");
            _resumeAttempted = true;
            return _primaryThreadHandle;
        }
    }

    internal WindowsProcessCloseResult ReleaseAfterTerminal()
    {
        lock (_sync)
        {
            if (!_created)
                return new(false, false);
            if (_released)
                return new(_processClosed, _primaryThreadClosed);
        }

        var threadClosed = _primaryThreadClosed || PrimaryThreadHandle.CloseOnce();
        var processClosed = _processClosed || ProcessHandle.CloseOnce();
        lock (_sync)
        {
            _primaryThreadClosed = threadClosed;
            _processClosed = processClosed;
            _released = threadClosed && processClosed;
            return new(processClosed, threadClosed);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_created && !_released)
            {
                throw new InvalidOperationException(
                    "A created process lease can only be released after terminal observation.");
            }
            _released = true;
        }
    }
}
