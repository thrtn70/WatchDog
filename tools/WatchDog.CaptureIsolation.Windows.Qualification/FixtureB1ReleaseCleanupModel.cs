using System.Collections.Immutable;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureB1ReleaseStep
{
    Slot1,
    Slot2,
    Slot3,
    Slot4,
    Slot5,
    Slot6,
    Slot7,
    Resume,
}

internal enum FixtureB1OperationOwner
{
    Wrapper,
    Launcher,
}

internal enum FixtureB1TerminalKind
{
    RunnerSignaled,
    RunnerJobEmpty,
    LauncherSignaled,
    SafetyJobEmpty,
}

internal enum FixtureB1CloseKind
{
    RunnerProcess,
    RunnerJob,
    LauncherProcess,
    SafetyJob,
    OwnerChannel,
    StandardOutput,
    StandardError,
    RetainedBinaryStreams,
}

internal enum FixtureB1CloseState
{
    Owned,
    ClosePending,
    Closed,
}

internal sealed record FixtureB1ReleaseCustody(
    Guid RunNonce,
    Guid LaunchId,
    FixtureHandoffProcessIdentity ExactWrapper,
    FixtureHandoffProcessIdentity ExactLauncher,
    FixtureHandoffProcessIdentity ExactRunner,
    Guid JobAckReceipt,
    Guid ProcessAckReceipt,
    long CustodyGeneration);

internal sealed record FixtureB1ReleaseClaim(
    Guid OperationId,
    FixtureB1ReleaseStep Step,
    long Sequence,
    long CustodyGeneration);

internal sealed record FixtureB1RetainedLease(
    FixtureB1CloseKind Kind,
    FixtureB1OperationOwner Owner,
    Guid LeaseId,
    long Generation);

internal sealed record FixtureB1CloseClaim(
    FixtureB1RetainedLease Lease,
    Guid AttemptId,
    Guid DrainBarrierId,
    ImmutableArray<Guid> RequiredTerminalObservations);

internal sealed record FixtureB1OriginalOperationReceipt(
    Guid OperationId,
    FixtureB1OperationOwner Owner,
    FixtureB1ReleaseStep? ReleaseStep,
    long RegisteredOrdinal,
    long? NativeStartOrdinal,
    long? AbortRequestOrdinal,
    long? NativeReturnOrdinal,
    long? CallbackTerminalOrdinal,
    bool AbortRequested,
    bool NativeStarted,
    bool NativeReturned,
    bool CallbackTerminal,
    bool Succeeded);

internal sealed record FixtureB1CloseReceipt(
    FixtureB1RetainedLease Lease,
    FixtureB1CloseState State,
    FixtureB1CloseClaim? Claim,
    Guid? ConfirmationId);

internal sealed record FixtureB1WrapperTerminalCloseReport(
    bool RunnerSignaled,
    bool RunnerJobEmpty,
    bool LauncherSignaled,
    bool SafetyJobEmpty,
    bool OriginalOperationsDrained,
    Guid? DrainBarrierId,
    bool DeadlineExpired,
    bool Ambiguous,
    IReadOnlyList<FixtureB1OriginalOperationReceipt> Operations,
    IReadOnlyList<FixtureB1CloseReceipt> Closes)
{
    internal bool AllLeasesClosed => Closes.Count == Enum.GetValues<FixtureB1CloseKind>().Length &&
        Closes.All(close => close.State == FixtureB1CloseState.Closed);

    internal bool CleanupConfirmed => RunnerSignaled && RunnerJobEmpty &&
        LauncherSignaled && SafetyJobEmpty && OriginalOperationsDrained &&
        !DeadlineExpired && !Ambiguous && AllLeasesClosed;

    internal bool AdapterRetainsOwnership => !AllLeasesClosed;
}

/// <summary>
/// Pure external observations. A native adapter must prove exact retained objects,
/// current peer/custody generations, and a particular close attempt. A true fake
/// return does not establish any Windows behavior.
/// </summary>
internal interface IFixtureB1ReleaseCleanupAuthority
{
    bool IsCustodyAcknowledged(FixtureB1ReleaseCustody custody);
    bool IsFreshReleaseAdmitted(
        FixtureB1ReleaseCustody custody,
        FixtureB1ReleaseStep step);
    bool IsExactTerminalObserved(
        FixtureB1ReleaseCustody custody,
        FixtureB1TerminalKind kind,
        Guid observationId);
    bool IsExactCloseConfirmed(FixtureB1CloseClaim claim, Guid confirmationId);
}

/// <summary>
/// Unregistered joint W/L test oracle for action admission, original-operation drain,
/// and W's typed terminal/close report. Real W and L must have separate registries
/// and exchange authenticated receipts; this model has no native synchronization.
/// </summary>
internal sealed class FixtureB1ReleaseCleanupModel
{
    private sealed class Operation(
        Guid id,
        FixtureB1OperationOwner owner,
        FixtureB1ReleaseStep? step,
        long registeredOrdinal)
    {
        internal Guid Id { get; } = id;
        internal FixtureB1OperationOwner Owner { get; } = owner;
        internal FixtureB1ReleaseStep? Step { get; } = step;
        internal FixtureB1ReleaseClaim? Claim { get; set; }
        internal long RegisteredOrdinal { get; } = registeredOrdinal;
        internal long? NativeStartOrdinal { get; set; }
        internal long? AbortRequestOrdinal { get; set; }
        internal long? NativeReturnOrdinal { get; set; }
        internal long? CallbackTerminalOrdinal { get; set; }
        internal bool AbortRequested { get; set; }
        internal bool NativeStarted { get; set; }
        internal bool NativeReturned { get; set; }
        internal bool CallbackTerminal { get; set; }
        internal bool Succeeded { get; set; }
        internal bool ReleaseApplied { get; set; }

        internal FixtureB1OriginalOperationReceipt Snapshot() => new(
            Id, Owner, Step, RegisteredOrdinal, NativeStartOrdinal,
            AbortRequestOrdinal, NativeReturnOrdinal, CallbackTerminalOrdinal,
            AbortRequested, NativeStarted, NativeReturned, CallbackTerminal,
            Succeeded);
    }

    private sealed class Lease(FixtureB1RetainedLease identity)
    {
        internal FixtureB1RetainedLease Identity { get; } = identity;
        internal FixtureB1CloseState State { get; set; }
        internal FixtureB1CloseClaim? Claim { get; set; }
        internal Guid? ConfirmationId { get; set; }

        internal FixtureB1CloseReceipt Snapshot() => new(
            Identity, State, Claim, ConfirmationId);
    }

    private readonly object _gate = new();
    private readonly FixtureB1ReleaseCustody _custody;
    private readonly IFixtureB1ReleaseCleanupAuthority _authority;
    private readonly Dictionary<Guid, Operation> _operations = [];
    private readonly Dictionary<FixtureB1CloseKind, Lease> _leases;
    private readonly HashSet<Guid> _usedCloseAttemptIds = [];
    private readonly HashSet<Guid> _usedConfirmationIds = [];
    private readonly HashSet<Guid> _terminalObservationIds = [];
    private readonly HashSet<FixtureB1TerminalKind> _terminal = [];
    private readonly Dictionary<FixtureB1TerminalKind, Guid> _terminalReceipts = [];
    private Guid? _activeReleaseOperationId;
    private Guid? _drainBarrierId;
    private long _nextEventOrdinal;
    private int _nextReleaseIndex;
    private bool _admissionStopped;
    private bool _aborted;
    private bool _cleanupStarted;
    private bool _deadlineExpired;
    private bool _drained;
    private bool _ambiguous;

    internal FixtureB1ReleaseCleanupModel(
        FixtureB1ReleaseCustody custody,
        IEnumerable<FixtureB1RetainedLease> retainedLeases,
        IFixtureB1ReleaseCleanupAuthority authority)
    {
        ArgumentNullException.ThrowIfNull(custody);
        ArgumentNullException.ThrowIfNull(retainedLeases);
        ArgumentNullException.ThrowIfNull(authority);
        if (custody.RunNonce == Guid.Empty || custody.LaunchId == Guid.Empty ||
            custody.RunNonce == custody.LaunchId ||
            !custody.ExactWrapper.IsValid || !custody.ExactLauncher.IsValid ||
            !custody.ExactRunner.IsValid ||
            custody.ExactWrapper == custody.ExactLauncher ||
            custody.ExactWrapper == custody.ExactRunner ||
            custody.ExactLauncher == custody.ExactRunner ||
            custody.JobAckReceipt == Guid.Empty ||
            custody.ProcessAckReceipt == Guid.Empty ||
            custody.JobAckReceipt == custody.ProcessAckReceipt ||
            custody.CustodyGeneration <= 0)
            throw new ArgumentException("Exact acknowledged custody is required.", nameof(custody));

        var leases = retainedLeases.ToArray();
        if (leases.Length != Enum.GetValues<FixtureB1CloseKind>().Length ||
            leases.Any(lease => lease is null ||
                !Enum.IsDefined(lease.Kind) ||
                lease.Owner != FixtureB1OperationOwner.Wrapper ||
                lease.LeaseId == Guid.Empty || lease.Generation <= 0) ||
            leases.Select(lease => lease.Kind).Distinct().Count() != leases.Length ||
            leases.Select(lease => lease.LeaseId).Distinct().Count() != leases.Length)
            throw new ArgumentException("One distinct retained lease per close kind is required.",
                nameof(retainedLeases));

        _custody = custody;
        _authority = authority;
        _leases = leases.ToDictionary(lease => lease.Kind, lease => new Lease(lease));
        if (!Observe(() => authority.IsCustodyAcknowledged(custody)))
            _aborted = true;
    }

    internal bool CanClaimNextRelease
    {
        get
        {
            lock (_gate)
                return CanClaimNextReleaseCore;
        }
    }

    private bool CanClaimNextReleaseCore =>
        !_aborted && !_admissionStopped && !_cleanupStarted &&
        _activeReleaseOperationId is null &&
        _nextReleaseIndex < Enum.GetValues<FixtureB1ReleaseStep>().Length;

    internal bool RegisterOriginalOperation(
        Guid operationId,
        FixtureB1OperationOwner owner)
    {
        lock (_gate)
        {
            if (operationId == Guid.Empty || !Enum.IsDefined(owner) ||
                _aborted || _admissionStopped || _cleanupStarted ||
                _operations.ContainsKey(operationId))
                return false;
            _operations.Add(operationId,
                new(operationId, owner, null, ++_nextEventOrdinal));
            return true;
        }
    }

    internal FixtureB1ReleaseClaim? TryClaimRelease(
        FixtureB1ReleaseStep step,
        Guid operationId)
    {
        lock (_gate)
        {
            if (operationId == Guid.Empty || _operations.ContainsKey(operationId) ||
                !CanClaimNextReleaseCore)
                return null;
            if ((int)step != _nextReleaseIndex)
            {
                _aborted = true;
                _admissionStopped = true;
                return null;
            }

            // Register before the external observation or later native action. A failed
            // observation has returned and is terminal; a granted action stays live.
            var operation = new Operation(
                operationId, FixtureB1OperationOwner.Launcher, step,
                ++_nextEventOrdinal);
            _operations.Add(operationId, operation);
            if (!Observe(() => _authority.IsFreshReleaseAdmitted(_custody, step)))
            {
                operation.NativeReturned = true;
                operation.CallbackTerminal = true;
                operation.NativeReturnOrdinal = ++_nextEventOrdinal;
                operation.CallbackTerminalOrdinal = ++_nextEventOrdinal;
                _aborted = true;
                _admissionStopped = true;
                return null;
            }

            _activeReleaseOperationId = operationId;
            var claim = new FixtureB1ReleaseClaim(
                operationId, step, _nextReleaseIndex, _custody.CustodyGeneration);
            operation.Claim = claim;
            return claim;
        }
    }

    internal void StopAdmission()
    {
        lock (_gate)
        {
            _admissionStopped = true;
            _aborted = true;
        }
    }

    internal void StartCleanup()
    {
        lock (_gate)
        {
            _cleanupStarted = true;
            _admissionStopped = true;
        }
    }

    internal void ExpireCleanupDeadline()
    {
        lock (_gate)
        {
            _deadlineExpired = true;
            _cleanupStarted = true;
            _admissionStopped = true;
        }
    }

    internal bool RequestAbort(Guid operationId)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation) ||
                operation.AbortRequested ||
                (operation.NativeReturned && operation.CallbackTerminal))
                return false;
            operation.AbortRequested = true;
            operation.AbortRequestOrdinal = ++_nextEventOrdinal;
            return true;
        }
    }

    // A native adapter must hold its release barrier through this transition and
    // native entry. These logical ordinals do not establish a physical time bound.
    internal bool MarkNativeStarted(Guid operationId)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation) ||
                operation.Step is not null ||
                operation.NativeStarted || operation.NativeReturned ||
                operation.AbortRequested || _aborted || _admissionStopped ||
                _cleanupStarted)
                return false;
            operation.NativeStarted = true;
            operation.NativeStartOrdinal = ++_nextEventOrdinal;
            return true;
        }
    }

    internal bool MarkReleaseNativeStarted(FixtureB1ReleaseClaim claim)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            if (!_operations.TryGetValue(claim.OperationId, out var operation) ||
                operation.Claim != claim ||
                _activeReleaseOperationId != claim.OperationId ||
                claim.Sequence != _nextReleaseIndex ||
                claim.CustodyGeneration != _custody.CustodyGeneration ||
                operation.NativeStarted || operation.NativeReturned ||
                operation.AbortRequested || _aborted || _admissionStopped ||
                _cleanupStarted)
                return false;
            operation.NativeStarted = true;
            operation.NativeStartOrdinal = ++_nextEventOrdinal;
            return true;
        }
    }

    internal bool CancelBeforeNativeStart(Guid operationId)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation) ||
                operation.NativeStarted || operation.NativeReturned ||
                !operation.AbortRequested)
                return false;
            operation.NativeReturned = true;
            operation.CallbackTerminal = true;
            operation.NativeReturnOrdinal = ++_nextEventOrdinal;
            operation.CallbackTerminalOrdinal = ++_nextEventOrdinal;
            _activeReleaseOperationId = operation.Step is null
                ? _activeReleaseOperationId
                : null;
            _aborted = true;
            TryAdvanceRelease(operation);
            return true;
        }
    }

    internal bool RecordNativeReturn(Guid operationId, bool succeeded)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation) ||
                !operation.NativeStarted || operation.NativeReturned)
                return false;
            operation.NativeReturned = true;
            operation.NativeReturnOrdinal = ++_nextEventOrdinal;
            operation.Succeeded = succeeded;
            if (!succeeded)
            {
                _aborted = true;
                _admissionStopped = true;
            }
            TryAdvanceRelease(operation);
            return true;
        }
    }

    internal bool RecordCallbackTerminal(Guid operationId)
    {
        lock (_gate)
        {
            if (!_operations.TryGetValue(operationId, out var operation) ||
                !operation.NativeStarted || operation.CallbackTerminal)
                return false;
            operation.CallbackTerminal = true;
            operation.CallbackTerminalOrdinal = ++_nextEventOrdinal;
            TryAdvanceRelease(operation);
            return true;
        }
    }

    private void TryAdvanceRelease(Operation operation)
    {
        if (operation.Step is null || operation.ReleaseApplied ||
            !operation.NativeReturned || !operation.CallbackTerminal)
            return;
        operation.ReleaseApplied = true;
        _activeReleaseOperationId = null;
        if (operation.Succeeded && !_aborted && !_cleanupStarted)
            _nextReleaseIndex++;
    }

    internal bool TryConfirmDrain(Guid barrierId)
    {
        lock (_gate)
        {
            if (barrierId == Guid.Empty || !_cleanupStarted || _drained ||
                _operations.Values.Any(operation =>
                    !operation.NativeReturned || !operation.CallbackTerminal))
                return false;
            _drained = true;
            _drainBarrierId = barrierId;
            return true;
        }
    }

    internal bool TryRecordTerminal(
        FixtureB1TerminalKind kind,
        Guid observationId)
    {
        lock (_gate)
        {
            if (!_drained || !Enum.IsDefined(kind) || observationId == Guid.Empty ||
                _terminalObservationIds.Contains(observationId) ||
                _terminal.Contains(kind))
                return false;
            if (!Observe(() => _authority.IsExactTerminalObserved(
                    _custody, kind, observationId)))
            {
                _ambiguous = true;
                return false;
            }
            _terminalObservationIds.Add(observationId);
            _terminal.Add(kind);
            _terminalReceipts.Add(kind, observationId);
            return true;
        }
    }

    internal FixtureB1CloseClaim? TryBeginClose(
        FixtureB1CloseKind kind,
        Guid attemptId)
    {
        lock (_gate)
        {
            if (!_drained || attemptId == Guid.Empty ||
                _usedCloseAttemptIds.Contains(attemptId) ||
                !_leases.TryGetValue(kind, out var lease) ||
                lease.State != FixtureB1CloseState.Owned ||
                !TerminalAllowsClose(kind))
                return null;

            _usedCloseAttemptIds.Add(attemptId);
            var claim = new FixtureB1CloseClaim(
                lease.Identity, attemptId, _drainBarrierId!.Value,
                RequiredTerminalReceipts(kind));
            lease.State = FixtureB1CloseState.ClosePending;
            lease.Claim = claim;
            return claim;
        }
    }

    internal bool TryConfirmClose(FixtureB1CloseClaim claim, Guid confirmationId)
    {
        ArgumentNullException.ThrowIfNull(claim);
        lock (_gate)
        {
            if (claim.Lease is null ||
                claim.RequiredTerminalObservations.IsDefault ||
                confirmationId == Guid.Empty ||
                _usedConfirmationIds.Contains(confirmationId) ||
                !_leases.TryGetValue(claim.Lease.Kind, out var lease) ||
                lease.Identity != claim.Lease ||
                lease.State != FixtureB1CloseState.ClosePending ||
                lease.Claim is null ||
                lease.Claim.Lease != claim.Lease ||
                lease.Claim.AttemptId != claim.AttemptId ||
                lease.Claim.DrainBarrierId != claim.DrainBarrierId ||
                !lease.Claim.RequiredTerminalObservations.SequenceEqual(
                    claim.RequiredTerminalObservations))
                return false;
            if (!Observe(() => _authority.IsExactCloseConfirmed(claim, confirmationId)))
                return false;
            _usedConfirmationIds.Add(confirmationId);
            lease.State = FixtureB1CloseState.Closed;
            lease.ConfirmationId = confirmationId;
            return true;
        }
    }

    internal FixtureB1WrapperTerminalCloseReport Snapshot()
    {
        lock (_gate)
            return new(
                _terminal.Contains(FixtureB1TerminalKind.RunnerSignaled),
                _terminal.Contains(FixtureB1TerminalKind.RunnerJobEmpty),
                _terminal.Contains(FixtureB1TerminalKind.LauncherSignaled),
                _terminal.Contains(FixtureB1TerminalKind.SafetyJobEmpty),
                _drained,
                _drainBarrierId,
                _deadlineExpired,
                _ambiguous,
                _operations.Values.Select(operation => operation.Snapshot()).ToArray(),
                _leases.Values.Select(lease => lease.Snapshot()).ToArray());
    }

    internal bool CanReportPass
    {
        get
        {
            lock (_gate)
                return !_aborted && !_deadlineExpired &&
                    _nextReleaseIndex == Enum.GetValues<FixtureB1ReleaseStep>().Length &&
                    Snapshot().CleanupConfirmed;
        }
    }

    internal bool NativeQualificationEstablished => false;
    internal bool NativeReleaseSynchronizationEstablished => false;

    private bool TerminalAllowsClose(FixtureB1CloseKind kind) => kind switch
    {
        FixtureB1CloseKind.RunnerProcess or FixtureB1CloseKind.RunnerJob =>
            _terminal.Contains(FixtureB1TerminalKind.RunnerSignaled) &&
            _terminal.Contains(FixtureB1TerminalKind.RunnerJobEmpty),
        FixtureB1CloseKind.LauncherProcess or FixtureB1CloseKind.SafetyJob =>
            _terminal.Contains(FixtureB1TerminalKind.LauncherSignaled) &&
            _terminal.Contains(FixtureB1TerminalKind.SafetyJobEmpty),
        _ => true,
    };

    private ImmutableArray<Guid> RequiredTerminalReceipts(FixtureB1CloseKind kind) => kind switch
    {
        FixtureB1CloseKind.RunnerProcess or FixtureB1CloseKind.RunnerJob =>
        [
            _terminalReceipts[FixtureB1TerminalKind.RunnerSignaled],
            _terminalReceipts[FixtureB1TerminalKind.RunnerJobEmpty],
        ],
        FixtureB1CloseKind.LauncherProcess or FixtureB1CloseKind.SafetyJob =>
        [
            _terminalReceipts[FixtureB1TerminalKind.LauncherSignaled],
            _terminalReceipts[FixtureB1TerminalKind.SafetyJobEmpty],
        ],
        _ => [],
    };

    private static bool Observe(Func<bool> observation)
    {
        try { return observation(); }
        catch { return false; }
    }
}
