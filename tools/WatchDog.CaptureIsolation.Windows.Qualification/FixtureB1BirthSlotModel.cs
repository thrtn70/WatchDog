namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureB1BirthHandleState
{
    Empty,
    RawOwned,
    AttachPending,
    SafeOwned,
    ClosePending,
    Closed,
}

internal enum FixtureB1BirthAdoptionDisposition
{
    Adopted,
    AlreadyOwned,
    ProviderRetainsNewResource,
    Invalid,
}

/// <summary>
/// Pure, preallocated ownership ledger for one successful CreateProcess-style return.
/// Raw values and shell IDs are fake local identities here, not native handles or proof
/// that a Windows process was created, terminated, or closed.
/// </summary>
internal sealed class FixtureB1BirthSlotModel
{
    private readonly object _gate = new();
    private readonly long _processShellId;
    private readonly long _threadShellId;
    private bool _armed;
    private bool _stopped;
    private bool _nativeSuccess;
    private bool _originalAndCallbacksTerminal;
    private bool _closeAdmitted;
    private bool _ambiguous;
    private long _processRaw;
    private long _threadRaw;
    private FixtureB1BirthHandleState _processState;
    private FixtureB1BirthHandleState _threadState;

    internal FixtureB1BirthSlotModel(
        Guid launchId,
        long processShellId,
        long threadShellId)
    {
        if (launchId == Guid.Empty)
            throw new ArgumentException("A launch ID is required.", nameof(launchId));
        if (processShellId <= 0 || threadShellId <= 0 ||
            processShellId == threadShellId)
            throw new ArgumentException("Distinct preallocated shells are required.");

        LaunchId = launchId;
        _processShellId = processShellId;
        _threadShellId = threadShellId;
    }

    internal Guid LaunchId { get; }
    internal bool NativeSuccess { get { lock (_gate) return _nativeSuccess; } }
    internal bool OriginalAndCallbacksTerminal
    {
        get { lock (_gate) return _originalAndCallbacksTerminal; }
    }
    internal bool Ambiguous { get { lock (_gate) return _ambiguous; } }
    internal FixtureB1BirthHandleState ProcessState
    {
        get { lock (_gate) return _processState; }
    }
    internal FixtureB1BirthHandleState ThreadState
    {
        get { lock (_gate) return _threadState; }
    }
    internal bool MayContinueToValidation
    {
        get
        {
            lock (_gate)
                return _nativeSuccess && _originalAndCallbacksTerminal &&
                    !_stopped && !_ambiguous && !_closeAdmitted &&
                    _processState == FixtureB1BirthHandleState.SafeOwned &&
                    _threadState == FixtureB1BirthHandleState.SafeOwned;
        }
    }
    internal bool CleanupConfirmed
    {
        get
        {
            lock (_gate)
                return _nativeSuccess && _originalAndCallbacksTerminal &&
                    _closeAdmitted && !_ambiguous &&
                    _processState == FixtureB1BirthHandleState.Closed &&
                    _threadState == FixtureB1BirthHandleState.Closed;
        }
    }

    // Reserve the two cells and owning shells before calling the native provider.
    internal bool Arm()
    {
        lock (_gate)
        {
            if (_armed || _stopped)
            {
                _ambiguous = true;
                return false;
            }
            _armed = true;
            return true;
        }
    }

    // Abort stops a new creation but must not reject a late success from an armed call.
    internal void Stop()
    {
        lock (_gate) _stopped = true;
    }

    internal bool MarkNativeSuccess()
    {
        lock (_gate)
        {
            if (!_armed || _nativeSuccess || _originalAndCallbacksTerminal)
            {
                _ambiguous = true;
                return false;
            }
            _nativeSuccess = true;
            return true;
        }
    }

    // The native provider calls these before metadata, cancellation, logging, or wrapper
    // construction. A rejected new value stays with the provider, never this slot.
    internal FixtureB1BirthAdoptionDisposition TryAdoptProcessRaw(long raw) =>
        TryAdoptRaw(raw, process: true);

    internal FixtureB1BirthAdoptionDisposition TryAdoptThreadRaw(long raw) =>
        TryAdoptRaw(raw, process: false);

    private FixtureB1BirthAdoptionDisposition TryAdoptRaw(long raw, bool process)
    {
        lock (_gate)
        {
            if (raw <= 0)
            {
                _ambiguous = true;
                return FixtureB1BirthAdoptionDisposition.Invalid;
            }

            var ownRaw = process ? _processRaw : _threadRaw;
            var otherRaw = process ? _threadRaw : _processRaw;
            var state = process ? _processState : _threadState;
            if (!_armed || !_nativeSuccess || _originalAndCallbacksTerminal ||
                state != FixtureB1BirthHandleState.Empty || raw == otherRaw)
            {
                _ambiguous = true;
                return raw == ownRaw || raw == otherRaw
                    ? FixtureB1BirthAdoptionDisposition.AlreadyOwned
                    : FixtureB1BirthAdoptionDisposition.ProviderRetainsNewResource;
            }

            if (process)
            {
                _processRaw = raw;
                _processState = FixtureB1BirthHandleState.RawOwned;
            }
            else
            {
                _threadRaw = raw;
                _threadState = FixtureB1BirthHandleState.RawOwned;
            }
            return FixtureB1BirthAdoptionDisposition.Adopted;
        }
    }

    // Claim raw-close authority before physically attaching to the owning shell.
    // An interrupted attach stays pending: neither raw fallback nor a second attach
    // is licensed until independent proof resolves the first attempt.
    internal bool TryBeginProcessAttach() => TryBeginAttach(process: true);
    internal bool TryBeginThreadAttach() => TryBeginAttach(process: false);

    private bool TryBeginAttach(bool process)
    {
        lock (_gate)
        {
            if (!_nativeSuccess || _originalAndCallbacksTerminal ||
                (process ? _processState : _threadState) !=
                    FixtureB1BirthHandleState.RawOwned)
                return false;
            if (process)
                _processState = FixtureB1BirthHandleState.AttachPending;
            else
                _threadState = FixtureB1BirthHandleState.AttachPending;
            return true;
        }
    }

    // Only an independent observation that the original raw cell still owns the
    // same value can restore raw-close authority after an attach attempt failed.
    internal bool ConfirmProcessAttachFailed(bool rawCellStillOwnsValue) =>
        ConfirmAttachFailed(rawCellStillOwnsValue, process: true);

    internal bool ConfirmThreadAttachFailed(bool rawCellStillOwnsValue) =>
        ConfirmAttachFailed(rawCellStillOwnsValue, process: false);

    private bool ConfirmAttachFailed(bool rawCellStillOwnsValue, bool process)
    {
        lock (_gate)
        {
            if ((process ? _processState : _threadState) !=
                FixtureB1BirthHandleState.AttachPending)
                return false;
            if (!rawCellStillOwnsValue)
                return false;
            if (process)
                _processState = FixtureB1BirthHandleState.RawOwned;
            else
                _threadState = FixtureB1BirthHandleState.RawOwned;
            return true;
        }
    }

    // The provider reports the physical transfer to the exact preallocated shell.
    internal bool ConfirmProcessAttached(long shellId) => ConfirmAttached(shellId, process: true);
    internal bool ConfirmThreadAttached(long shellId) => ConfirmAttached(shellId, process: false);

    private bool ConfirmAttached(long shellId, bool process)
    {
        lock (_gate)
        {
            if (!_nativeSuccess || _originalAndCallbacksTerminal ||
                shellId != (process ? _processShellId : _threadShellId) ||
                (process ? _processState : _threadState) !=
                    FixtureB1BirthHandleState.AttachPending)
            {
                _ambiguous = true;
                return false;
            }
            if (process)
                _processState = FixtureB1BirthHandleState.SafeOwned;
            else
                _threadState = FixtureB1BirthHandleState.SafeOwned;
            return true;
        }
    }

    internal void CompleteOriginalAndCallbacks()
    {
        lock (_gate)
        {
            if (!_armed || _originalAndCallbacksTerminal)
                _ambiguous = true;
            if (_nativeSuccess &&
                (_processState == FixtureB1BirthHandleState.Empty ||
                    _threadState == FixtureB1BirthHandleState.Empty))
                _ambiguous = true;
            _originalAndCallbacksTerminal = true;
        }
    }

    // Terminal proof is supplied by an independent observer in a real adapter.
    // A partial proof or live callback never licenses either close.
    internal bool AdmitClose(
        bool exactProcessSignaled,
        bool outerJobEmpty,
        bool observersDrained)
    {
        lock (_gate)
        {
            if (!_nativeSuccess || !_originalAndCallbacksTerminal ||
                !exactProcessSignaled || !outerJobEmpty || !observersDrained ||
                (_processState is not (FixtureB1BirthHandleState.RawOwned or
                    FixtureB1BirthHandleState.SafeOwned) &&
                 _threadState is not (FixtureB1BirthHandleState.RawOwned or
                    FixtureB1BirthHandleState.SafeOwned)))
                return false;
            _closeAdmitted = true;
            return true;
        }
    }

    internal bool ConfirmProcessClosed(bool independentlyObservedClosed) =>
        ConfirmClosed(independentlyObservedClosed, process: true);

    internal bool ConfirmThreadClosed(bool independentlyObservedClosed) =>
        ConfirmClosed(independentlyObservedClosed, process: false);

    // The one-use claim precedes the physical close. An unconfirmed attempt stays
    // pending and cannot issue another close against a potentially reused value.
    internal bool TryBeginProcessClose() => TryBeginClose(process: true);
    internal bool TryBeginThreadClose() => TryBeginClose(process: false);

    private bool TryBeginClose(bool process)
    {
        lock (_gate)
        {
            var state = process ? _processState : _threadState;
            if (!_closeAdmitted ||
                state is not (FixtureB1BirthHandleState.RawOwned or
                    FixtureB1BirthHandleState.SafeOwned))
                return false;
            if (process)
                _processState = FixtureB1BirthHandleState.ClosePending;
            else
                _threadState = FixtureB1BirthHandleState.ClosePending;
            return true;
        }
    }

    private bool ConfirmClosed(bool independentlyObservedClosed, bool process)
    {
        lock (_gate)
        {
            var state = process ? _processState : _threadState;
            if (!_closeAdmitted || state != FixtureB1BirthHandleState.ClosePending)
            {
                _ambiguous = true;
                return false;
            }
            // Absence of a close observation is not proof that the original
            // handle value remains safe to close a second time.
            if (!independentlyObservedClosed)
                return false;
            if (process)
                _processState = FixtureB1BirthHandleState.Closed;
            else
                _threadState = FixtureB1BirthHandleState.Closed;
            return true;
        }
    }
}
