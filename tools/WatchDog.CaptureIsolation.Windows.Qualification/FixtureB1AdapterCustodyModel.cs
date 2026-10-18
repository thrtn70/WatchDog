using System.Collections.ObjectModel;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureB1AdapterDiagnosticCode
{
    None,
    InvalidRequest,
    ObservationRejected,
    ProviderFault,
    CallerCanceled,
    DeadlineExpired,
    CustodyAmbiguous,
}

internal enum FixtureB1AdapterTerminalState
{
    Confirmed,
    Rejected,
    Uncertain,
}

internal enum FixtureB1AdapterOperationKind
{
    PrepareTarget,
    DuplicateCoordinate,
    AcquireInspection,
    AwaitTerminal,
}

internal enum FixtureB1AdoptionDisposition
{
    Adopted,
    AlreadyOwned,
    ProviderRetainsNewResource,
}

internal sealed record FixtureB1AdapterLaunchCustody(
    Guid LaunchId,
    FixtureHandoffProcessIdentity ExactTarget,
    long ExactTargetLease,
    long OuterJobLease,
    long IssuerSourceLease,
    long ReapingLease);

internal sealed record FixtureB1AdapterTargetCoordinate(
    Guid LaunchId,
    FixtureHandoffProcessIdentity ExactTarget,
    string Label,
    long Coordinate);

internal sealed record FixtureB1AdapterInspectionLease(
    Guid LaunchId,
    FixtureHandoffProcessIdentity ExactTarget,
    long Lease);

internal sealed record FixtureB1AdapterFinalCloseReport(
    bool ExactTargetLeaseClosed,
    bool PrimaryThreadLeaseClosed,
    bool OuterJobLeaseClosed,
    bool IssuerSourceLeaseClosed,
    bool ReapingLeaseClosed,
    bool InspectionLeasesClosed,
    bool OperationsDrained,
    bool RetainedRootClosed,
    bool RegisteredOutputsClosed,
    bool AuthenticationMappingsClosed,
    bool LauncherProofClosed,
    bool TransportClosed,
    bool RejectedAdoptionsClosed,
    bool ProviderClosed)
{
    internal bool Complete => ExactTargetLeaseClosed && PrimaryThreadLeaseClosed &&
        OuterJobLeaseClosed &&
        IssuerSourceLeaseClosed && ReapingLeaseClosed && InspectionLeasesClosed &&
        OperationsDrained && RetainedRootClosed && RegisteredOutputsClosed &&
        AuthenticationMappingsClosed && LauncherProofClosed && TransportClosed &&
        RejectedAdoptionsClosed && ProviderClosed;
}

/// <summary>
/// Preallocated, nonthrowing adoption point registered before a provider operation starts.
/// Providers publish successful native side effects here before any fallible validation.
/// AlreadyOwned forbids provider close of the same object. ProviderRetainsNewResource
/// requires the provider to retain and reconcile that new object under its own terminal
/// proof, then attest closure in the final report. Both rejected cases latch uncertainty.
/// </summary>
internal interface IFixtureB1AdapterAdoptionSink
{
    int OperationId { get; }
    FixtureB1AdapterOperationKind Kind { get; }
    FixtureB1AdoptionDisposition TryAdoptTarget(FixtureB1AdapterLaunchCustody custody);
    FixtureB1AdoptionDisposition TryAdoptCoordinate(FixtureB1AdapterTargetCoordinate coordinate);
    FixtureB1AdoptionDisposition TryAdoptInspection(FixtureB1AdapterInspectionLease inspection);
}

internal interface IFixtureB1AdapterCustodyProvider
{
    // Once true, this property must never become false. The model also latches it locally.
    bool MutationStarted { get; }

    ValueTask PrepareTargetAsync(
        Guid runNonce,
        Guid launchId,
        IFixtureB1AdapterAdoptionSink adoption,
        CancellationToken cancellationToken);

    ValueTask DuplicateTargetCoordinateAsync(
        FixtureHandoffSlot slot,
        int slotIndex,
        IFixtureB1AdapterAdoptionSink adoption,
        CancellationToken cancellationToken);

    ValueTask AcquireInspectionAsync(
        FixtureB1AdapterTargetCoordinate coordinate,
        IFixtureB1AdapterAdoptionSink adoption,
        CancellationToken cancellationToken);

    ValueTask AwaitTerminalAsync(
        FixtureB1AdapterLaunchCustody custody,
        CancellationToken cancellationToken);

    ValueTask CloseInspectionAsync(
        FixtureB1AdapterInspectionLease inspection,
        CancellationToken cancellationToken);

    ValueTask CloseTargetCoordinateAsync(
        FixtureB1AdapterTargetCoordinate coordinate,
        CancellationToken cancellationToken);

    ValueTask CloseIssuerSourceAsync(
        FixtureB1AdapterLaunchCustody custody,
        CancellationToken cancellationToken);

    ValueTask<FixtureB1AdapterFinalCloseReport> CloseLocalAsync(
        FixtureB1AdapterLaunchCustody? custody,
        IReadOnlyList<FixtureB1AdapterInspectionLease> inspections,
        // False forbids releasing the independent reaping lease. The provider must
        // retain authority to finish termination/reap even when cleanup is uncertain.
        bool mayReleaseReaper,
        CancellationToken cancellationToken);
}

internal interface IFixtureB1AdapterObservationProvider
{
    bool IsLaunchCustodyValid(FixtureB1AdapterLaunchCustody custody);
    bool IsTargetCoordinateValid(
        FixtureHandoffSlot expectedSlot,
        FixtureB1AdapterLaunchCustody custody,
        FixtureB1AdapterTargetCoordinate coordinate);
    bool IsInspectionValid(
        FixtureB1AdapterTargetCoordinate coordinate,
        FixtureB1AdapterInspectionLease inspection);
    bool IsExactTargetSignaled(FixtureB1AdapterLaunchCustody custody);
    bool IsOuterJobEmpty(FixtureB1AdapterLaunchCustody custody);
    bool IsTargetCoordinateClosed(FixtureB1AdapterTargetCoordinate coordinate);
    bool IsInspectionClosed(FixtureB1AdapterInspectionLease inspection);
    bool IsIssuerSourceClosed(FixtureB1AdapterLaunchCustody custody);
    bool IsReapingLeaseRetained(FixtureB1AdapterLaunchCustody custody);
    bool AreRetainedRootAndOutputsAdmitted(FixtureB1AdapterLaunchCustody custody);
}

internal interface IFixtureB1AdapterAbortProvider
{
    ValueTask AbortAsync(CancellationToken cancellationToken);
    // A successful drain guarantees every original operation and adoption callback is terminal.
    ValueTask DrainAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Immutable privacy-safe terminal snapshot. It contains no raw handles, paths, or provider
/// exception text and is custody evidence only, not native qualification or release evidence.
/// </summary>
internal sealed class FixtureB1AdapterCustodyResult
{
    internal FixtureB1AdapterCustodyResult(
        Guid runNonce,
        Guid launchId,
        FixtureHandoffProcessIdentity? exactTarget,
        bool mutationStarted,
        bool exactTargetSignaled,
        bool outerJobEmpty,
        int adoptedCoordinateCount,
        int confirmedCoordinateCloseCount,
        int adoptedInspectionCount,
        FixtureB1AdapterFinalCloseReport? finalClose,
        FixtureB1AdapterTerminalState terminalState,
        FixtureB1AdapterDiagnosticCode diagnosticCode)
    {
        RunNonce = runNonce;
        LaunchId = launchId;
        ExactTarget = exactTarget;
        MutationStarted = mutationStarted;
        ExactTargetSignaled = exactTargetSignaled;
        OuterJobEmpty = outerJobEmpty;
        AdoptedCoordinateCount = adoptedCoordinateCount;
        ConfirmedCoordinateCloseCount = confirmedCoordinateCloseCount;
        AdoptedInspectionCount = adoptedInspectionCount;
        FinalClose = finalClose;
        TerminalState = terminalState;
        DiagnosticCode = diagnosticCode;
    }

    internal Guid RunNonce { get; }
    internal Guid LaunchId { get; }
    internal FixtureHandoffProcessIdentity? ExactTarget { get; }
    internal bool MutationStarted { get; }
    internal bool ExactTargetSignaled { get; }
    internal bool OuterJobEmpty { get; }
    internal int AdoptedCoordinateCount { get; }
    internal int ConfirmedCoordinateCloseCount { get; }
    internal int AdoptedInspectionCount { get; }
    internal FixtureB1AdapterFinalCloseReport? FinalClose { get; }
    internal FixtureB1AdapterTerminalState TerminalState { get; }
    internal FixtureB1AdapterDiagnosticCode DiagnosticCode { get; }
}

/// <summary>
/// Pure, one-use custody ledger for a future B1 native adapter. It invokes only injected
/// providers and owns no process, filesystem, IPC, or P/Invoke implementation.
/// </summary>
internal sealed class FixtureB1AdapterCustodyModel
{
    private enum RunOutcome { Confirmed, Rejected, TimedOut, Infrastructure }
    private readonly IFixtureB1AdapterCustodyProvider _provider;
    private readonly IFixtureB1AdapterObservationProvider _observer;
    private readonly IFixtureB1AdapterAbortProvider _abort;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _readinessBudget;
    private readonly FixtureB1CleanupContext _cleanupContext;
    private readonly bool _ownsCleanupContext;
    private int _used;

    internal FixtureB1AdapterCustodyModel(
        IFixtureB1AdapterCustodyProvider provider,
        IFixtureB1AdapterObservationProvider observer,
        IFixtureB1AdapterAbortProvider abort,
        TimeProvider clock,
        TimeSpan? readinessBudget = null,
        TimeSpan? cleanupBudget = null)
        : this(
            provider,
            observer,
            abort,
            clock,
            CreateCleanupContext(clock, cleanupBudget),
            ownsCleanupContext: true,
            readinessBudget: readinessBudget)
    {
    }

    internal FixtureB1AdapterCustodyModel(
        IFixtureB1AdapterCustodyProvider provider,
        IFixtureB1AdapterObservationProvider observer,
        IFixtureB1AdapterAbortProvider abort,
        TimeProvider clock,
        FixtureB1CleanupContext cleanupContext,
        TimeSpan? readinessBudget = null)
        : this(
            provider,
            observer,
            abort,
            clock,
            cleanupContext,
            ownsCleanupContext: false,
            readinessBudget: readinessBudget)
    {
    }

    private FixtureB1AdapterCustodyModel(
        IFixtureB1AdapterCustodyProvider provider,
        IFixtureB1AdapterObservationProvider observer,
        IFixtureB1AdapterAbortProvider abort,
        TimeProvider clock,
        FixtureB1CleanupContext cleanupContext,
        bool ownsCleanupContext,
        TimeSpan? readinessBudget)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(observer);
        ArgumentNullException.ThrowIfNull(abort);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(cleanupContext);
        _provider = provider;
        _observer = observer;
        _abort = abort;
        _clock = clock;
        _cleanupContext = cleanupContext;
        _ownsCleanupContext = ownsCleanupContext;
        _readinessBudget = readinessBudget ?? QualificationConstants.ReadinessTimeout;
        if (_readinessBudget <= TimeSpan.Zero ||
            _readinessBudget > QualificationConstants.ReadinessTimeout)
            throw new ArgumentOutOfRangeException(nameof(readinessBudget));
    }

    internal async Task<FixtureB1AdapterCustodyResult> ExecuteAsync(
        Guid runNonce,
        Guid launchId,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _used, 1, 0) != 0)
            throw new InvalidOperationException("fixture.b1.adapter.one-use-rejected");

        using var ownedCleanupContext = _ownsCleanupContext ? _cleanupContext : null;

        var plan = TryCreatePlan(runNonce, launchId);
        if (plan is null)
            return new(runNonce, launchId, null, false, false, false, 0, 0, 0,
                null, FixtureB1AdapterTerminalState.Rejected,
                FixtureB1AdapterDiagnosticCode.InvalidRequest);

        var ledger = new CustodyLedger(plan.Slots.Count);
        using var deadline = new CancellationTokenSource(_readinessBudget, _clock);
        using var active = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);
        var operations = new OperationRunner(
            ledger, _provider, _abort, _cleanupContext, active.Token, deadline.Token);
        var runOutcome = RunOutcome.Confirmed;
        var diagnostic = FixtureB1AdapterDiagnosticCode.None;

        try
        {
            var prepare = ledger.Register(FixtureB1AdapterOperationKind.PrepareTarget, 0);
            await operations.RunAsync(
                prepare,
                token => _provider.PrepareTargetAsync(
                    runNonce, launchId, prepare, token)).ConfigureAwait(false);
            LatchMutation(ledger);
            var custody = prepare.Target ?? throw Reject();
            if (!ValidCustodyShape(custody, launchId) ||
                !Observe(() => _observer.IsLaunchCustodyValid(custody)))
                throw Reject();

            for (var index = 0; index < plan.Slots.Count; index++)
            {
                var duplicate = ledger.Register(
                    FixtureB1AdapterOperationKind.DuplicateCoordinate, index);
                await operations.RunAsync(
                    duplicate,
                    token => _provider.DuplicateTargetCoordinateAsync(
                        plan.Slots[index], index, duplicate, token)).ConfigureAwait(false);
                LatchMutation(ledger);
                var coordinate = duplicate.Coordinate ?? throw Reject();
                if (!ledger.AcceptCoordinate(coordinate, custody, plan.Slots[index]) ||
                    !Observe(() => _observer.IsTargetCoordinateValid(
                        plan.Slots[index], custody, coordinate)))
                    throw Reject();

                var inspection = ledger.Register(
                    FixtureB1AdapterOperationKind.AcquireInspection, index);
                await operations.RunAsync(
                    inspection,
                    token => _provider.AcquireInspectionAsync(
                        coordinate, inspection, token)).ConfigureAwait(false);
                LatchMutation(ledger);
                var inspectionLease = inspection.Inspection ?? throw Reject();
                if (!ledger.AcceptInspection(inspectionLease, custody) ||
                    !Observe(() => _observer.IsInspectionValid(
                        coordinate, inspectionLease)))
                    throw Reject();
            }

            var terminal = ledger.Register(FixtureB1AdapterOperationKind.AwaitTerminal, 0);
            await operations.RunAsync(
                terminal,
                token => _provider.AwaitTerminalAsync(custody, token))
                .ConfigureAwait(false);
            LatchMutation(ledger);
        }
        catch (OperationStoppedException stopped)
        {
            runOutcome = stopped.DeadlineExpired
                ? RunOutcome.TimedOut
                : RunOutcome.Rejected;
            diagnostic = stopped.DeadlineExpired
                ? FixtureB1AdapterDiagnosticCode.DeadlineExpired
                : FixtureB1AdapterDiagnosticCode.CallerCanceled;
            await operations.StopAbortAndDrainAsync(Task.CompletedTask).ConfigureAwait(false);
        }
        catch (ObservationRejectedException)
        {
            runOutcome = RunOutcome.Rejected;
            diagnostic = FixtureB1AdapterDiagnosticCode.ObservationRejected;
            await operations.StopAbortAndDrainAsync(Task.CompletedTask).ConfigureAwait(false);
        }
        catch
        {
            runOutcome = RunOutcome.Infrastructure;
            diagnostic = FixtureB1AdapterDiagnosticCode.ProviderFault;
            await operations.StopAbortAndDrainAsync(Task.CompletedTask).ConfigureAwait(false);
        }

        LatchMutation(ledger);
        ledger.Stop();
        var exactTargetSignaled = false;
        var outerJobEmpty = false;
        var confirmedCoordinateCloses = 0;
        var cleanupUncertain = false;

        var cleanupToken = _cleanupContext.Token;
        var drained = false;
        try
        {
            await operations.DrainForCleanupAsync(cleanupToken).ConfigureAwait(false);
            drained = true;
        }
        catch { cleanupUncertain = true; }

        if (!drained)
        {
            // The provider still owns every lease and live callback. Closing any dependent
            // object here could race an observer. No local-close receipt is claimed.
            return new(runNonce, launchId, ledger.Target?.ExactTarget,
                ledger.MutationStarted, false, false, ledger.Coordinates.Count, 0,
                ledger.Inspections.Count, null, FixtureB1AdapterTerminalState.Uncertain,
                FixtureB1AdapterDiagnosticCode.CustodyAmbiguous);
        }

        var custodySnapshot = ledger.Target;
        var coordinates = ledger.Coordinates;
        var inspections = ledger.Inspections;
        cleanupUncertain |= ledger.Ambiguous ||
            (ledger.MutationStarted && custodySnapshot is null);

        if (custodySnapshot is not null)
        {
            exactTargetSignaled = ObserveCleanup(
                () => _observer.IsExactTargetSignaled(custodySnapshot),
                ref cleanupUncertain);
            outerJobEmpty = ObserveCleanup(
                () => _observer.IsOuterJobEmpty(custodySnapshot),
                ref cleanupUncertain);
        }

        foreach (var inspection in inspections)
        {
            try
            {
                await _provider.CloseInspectionAsync(inspection, cleanupToken)
                    .ConfigureAwait(false);
                if (!ObserveCleanup(
                        () => _observer.IsInspectionClosed(inspection),
                        ref cleanupUncertain))
                    cleanupUncertain = true;
            }
            catch { cleanupUncertain = true; }
        }

        // A target-local transfer is confirmed closed only after both independent terminal
        // conditions hold. One terminal condition can never substitute for the other.
        if (exactTargetSignaled && outerJobEmpty)
        {
            foreach (var coordinate in coordinates)
            {
                // Terminal evidence belongs only to the retained exact target.
                if (custodySnapshot is null ||
                    coordinate.LaunchId != custodySnapshot.LaunchId ||
                    coordinate.ExactTarget != custodySnapshot.ExactTarget)
                {
                    cleanupUncertain = true;
                    continue;
                }
                try
                {
                    await _provider.CloseTargetCoordinateAsync(coordinate, cleanupToken)
                        .ConfigureAwait(false);
                    if (ObserveCleanup(
                            () => _observer.IsTargetCoordinateClosed(coordinate),
                            ref cleanupUncertain))
                        confirmedCoordinateCloses++;
                    else
                        cleanupUncertain = true;
                }
                catch { cleanupUncertain = true; }
            }
        }
        else if (coordinates.Count != 0)
        {
            cleanupUncertain = true;
        }

        if (custodySnapshot is not null)
        {
            try
            {
                await _provider.CloseIssuerSourceAsync(custodySnapshot, cleanupToken)
                    .ConfigureAwait(false);
                if (!ObserveCleanup(
                        () => _observer.IsIssuerSourceClosed(custodySnapshot),
                        ref cleanupUncertain) ||
                    !ObserveCleanup(
                        () => _observer.IsReapingLeaseRetained(custodySnapshot),
                        ref cleanupUncertain))
                    cleanupUncertain = true;
            }
            catch { cleanupUncertain = true; }
        }

        if (custodySnapshot is not null &&
            !ObserveCleanup(
                () => _observer.AreRetainedRootAndOutputsAdmitted(custodySnapshot),
                ref cleanupUncertain))
            cleanupUncertain = true;

        FixtureB1AdapterFinalCloseReport? finalClose = null;
        try
        {
            finalClose = await _provider.CloseLocalAsync(
                custodySnapshot, inspections,
                exactTargetSignaled && outerJobEmpty, cleanupToken).ConfigureAwait(false);
            if (finalClose is null || !finalClose.Complete)
                cleanupUncertain = true;
        }
        catch { cleanupUncertain = true; }

        // The final close provider must preserve the admitted artifact identity through
        // its own close report; a changed or unobservable identity is uncertainty.
        if (custodySnapshot is not null &&
            !ObserveCleanup(
                () => _observer.AreRetainedRootAndOutputsAdmitted(custodySnapshot),
                ref cleanupUncertain))
            cleanupUncertain = true;

        if (custodySnapshot is not null &&
            (!exactTargetSignaled || !outerJobEmpty))
            cleanupUncertain = true;
        if (confirmedCoordinateCloses != coordinates.Count || _cleanupContext.IsExpired)
            cleanupUncertain = true;
        cleanupUncertain |= ledger.Ambiguous;
        if (cancellationToken.IsCancellationRequested &&
            runOutcome == RunOutcome.Confirmed)
        {
            runOutcome = RunOutcome.Rejected;
            diagnostic = FixtureB1AdapterDiagnosticCode.CallerCanceled;
        }
        if (cleanupUncertain || !operations.AbortConfirmed || !operations.DrainConfirmed)
        {
            diagnostic = FixtureB1AdapterDiagnosticCode.CustodyAmbiguous;
        }

        var terminalState = cleanupUncertain || !operations.AbortConfirmed ||
            !operations.DrainConfirmed
            ? FixtureB1AdapterTerminalState.Uncertain
            : runOutcome switch
            {
                RunOutcome.Confirmed => FixtureB1AdapterTerminalState.Confirmed,
                _ => FixtureB1AdapterTerminalState.Rejected,
            };
        return new(
            runNonce,
            launchId,
            custodySnapshot?.ExactTarget,
            ledger.MutationStarted,
            exactTargetSignaled,
            outerJobEmpty,
            coordinates.Count,
            confirmedCoordinateCloses,
            inspections.Count,
            finalClose,
            terminalState,
            diagnostic);

        void LatchMutation(CustodyLedger target) =>
            target.LatchProviderMutation(_provider);
    }

    private static FixtureArtifactHandoffPlan? TryCreatePlan(Guid runNonce, Guid launchId)
    {
        try
        {
            return FixtureArtifactHandoffPlan.Create(
                FixtureHandoffEdge.LauncherToRunner, null, runNonce, launchId);
        }
        catch { return null; }
    }

    private static FixtureB1CleanupContext CreateCleanupContext(
        TimeProvider clock,
        TimeSpan? cleanupBudget)
    {
        ArgumentNullException.ThrowIfNull(clock);
        var budget = cleanupBudget ?? QualificationConstants.CleanupTimeout;
        if (budget <= TimeSpan.Zero || budget > QualificationConstants.CleanupTimeout)
            throw new ArgumentOutOfRangeException(nameof(cleanupBudget));
        return new FixtureB1CleanupContext(clock, budget);
    }

    internal static bool ValidCustodyShape(
        FixtureB1AdapterLaunchCustody custody,
        Guid launchId) => custody is not null && custody.LaunchId == launchId &&
        custody.ExactTarget is not null && custody.ExactTarget.IsValid &&
        custody.ExactTargetLease > 0 && custody.OuterJobLease > 0 &&
        custody.IssuerSourceLease > 0 && custody.ReapingLease > 0 &&
        custody.ExactTargetLease != custody.OuterJobLease &&
        custody.ExactTargetLease != custody.IssuerSourceLease &&
        custody.OuterJobLease != custody.IssuerSourceLease &&
        custody.ReapingLease != custody.ExactTargetLease &&
        custody.ReapingLease != custody.OuterJobLease &&
        custody.ReapingLease != custody.IssuerSourceLease;

    private static bool Observe(Func<bool> observation)
    {
        try { return observation(); }
        catch (Exception exception) { throw Reject(exception); }
    }

    private static bool ObserveCleanup(Func<bool> observation, ref bool uncertain)
    {
        try { return observation(); }
        catch
        {
            uncertain = true;
            return false;
        }
    }

    private static ObservationRejectedException Reject(Exception? inner = null) => new(inner);

    private sealed class ObservationRejectedException(Exception? inner) : Exception(null, inner);

    private sealed class OperationStoppedException(bool deadlineExpired) : Exception
    {
        internal bool DeadlineExpired { get; } = deadlineExpired;
    }

    private sealed class OperationRunner(
        CustodyLedger ledger,
        IFixtureB1AdapterCustodyProvider provider,
        IFixtureB1AdapterAbortProvider abort,
        FixtureB1CleanupContext cleanupContext,
        CancellationToken activeToken,
        CancellationToken deadlineToken)
    {
        private int _abortStarted;
        internal bool AbortConfirmed { get; private set; } = true;
        internal bool DrainConfirmed { get; private set; }

        internal async Task RunAsync(
            CustodyLedger.AdoptionSink sink,
            Func<CancellationToken, ValueTask> start)
        {
            if (activeToken.IsCancellationRequested)
                throw new OperationStoppedException(deadlineToken.IsCancellationRequested);
            if (sink.Kind is FixtureB1AdapterOperationKind.PrepareTarget or
                FixtureB1AdapterOperationKind.DuplicateCoordinate or
                FixtureB1AdapterOperationKind.AcquireInspection)
                ledger.LatchMutation(true);
            Task original;
            try { original = start(activeToken).AsTask(); }
            catch
            {
                sink.Complete();
                ledger.LatchProviderMutation(provider);
                ledger.MarkMissingAdoption(sink);
                await StopAbortAndDrainAsync(Task.CompletedTask).ConfigureAwait(false);
                throw;
            }

            try
            {
                var stopped = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = activeToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(), stopped);
                if (await Task.WhenAny(original, stopped.Task).ConfigureAwait(false) != original)
                {
                    await StopAbortAndDrainAsync(original).ConfigureAwait(false);
                    sink.Complete();
                    ledger.LatchProviderMutation(provider);
                    ledger.MarkMissingAdoption(sink);
                    throw new OperationStoppedException(deadlineToken.IsCancellationRequested);
                }
                await original.ConfigureAwait(false);
                sink.Complete();
                ledger.LatchProviderMutation(provider);
                ledger.MarkMissingAdoption(sink);
                activeToken.ThrowIfCancellationRequested();
            }
            catch
            {
                sink.Complete();
                ledger.LatchProviderMutation(provider);
                ledger.MarkMissingAdoption(sink);
                await StopAbortAndDrainAsync(original).ConfigureAwait(false);
                if (activeToken.IsCancellationRequested)
                    throw new OperationStoppedException(deadlineToken.IsCancellationRequested);
                throw;
            }
        }

        internal async Task StopAbortAndDrainAsync(Task original)
        {
            ledger.Stop();
            if (Interlocked.CompareExchange(ref _abortStarted, 1, 0) != 0)
            {
                await ObserveTaskAsync(original).ConfigureAwait(false);
                return;
            }

            try { await abort.AbortAsync(cleanupContext.Token).ConfigureAwait(false); }
            catch { AbortConfirmed = false; }

            // The original task remains owned. Abort must make it terminal; otherwise the
            // model intentionally stays pending instead of returning a detached operation.
            await ObserveTaskAsync(original).ConfigureAwait(false);
            try
            {
                await abort.DrainAsync(cleanupContext.Token).ConfigureAwait(false);
                DrainConfirmed = true;
            }
            catch { DrainConfirmed = false; }
        }

        internal async Task DrainForCleanupAsync(CancellationToken cancellationToken)
        {
            await abort.DrainAsync(cancellationToken).ConfigureAwait(false);
            DrainConfirmed = true;
        }

        private static async Task ObserveTaskAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch { }
        }
    }

    internal sealed class CustodyLedger
    {
        private readonly object _gate = new();
        private readonly AdoptionSink[] _operations;
        private readonly FixtureB1AdapterTargetCoordinate?[] _coordinates;
        private readonly FixtureB1AdapterInspectionLease?[] _inspections;
        private int _operationCount;
        private bool _stopping;
        private bool _ambiguous;
        private bool _mutationStarted;
        private FixtureB1AdapterLaunchCustody? _target;

        internal CustodyLedger(int slotCount, bool includeInspections = true)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(slotCount);
            var operationsPerSlot = includeInspections ? 2 : 1;
            _operations = new AdoptionSink[checked(2 + (slotCount * operationsPerSlot))];
            _coordinates = new FixtureB1AdapterTargetCoordinate?[slotCount];
            _inspections = new FixtureB1AdapterInspectionLease?[slotCount];
            var operation = 0;
            _operations[operation] = new AdoptionSink(
                this, operation++, FixtureB1AdapterOperationKind.PrepareTarget, 0);
            for (var index = 0; index < slotCount; index++)
            {
                _operations[operation] = new AdoptionSink(
                    this, operation++, FixtureB1AdapterOperationKind.DuplicateCoordinate, index);
                if (includeInspections)
                    _operations[operation] = new AdoptionSink(
                        this, operation++, FixtureB1AdapterOperationKind.AcquireInspection, index);
            }
            _operations[operation] = new AdoptionSink(
                this, operation, FixtureB1AdapterOperationKind.AwaitTerminal, 0);
        }

        internal bool Ambiguous { get { lock (_gate) return _ambiguous; } }
        internal bool MutationStarted { get { lock (_gate) return _mutationStarted; } }
        internal FixtureB1AdapterLaunchCustody? Target { get { lock (_gate) return _target; } }
        internal IReadOnlyList<FixtureB1AdapterTargetCoordinate> Coordinates
        {
            get
            {
                lock (_gate)
                    return new ReadOnlyCollection<FixtureB1AdapterTargetCoordinate>(
                        _coordinates.Where(value => value is not null).Select(value => value!).ToArray());
            }
        }
        internal IReadOnlyList<FixtureB1AdapterInspectionLease> Inspections
        {
            get
            {
                lock (_gate)
                    return new ReadOnlyCollection<FixtureB1AdapterInspectionLease>(
                        _inspections.Where(value => value is not null).Select(value => value!).ToArray());
            }
        }

        internal AdoptionSink Register(FixtureB1AdapterOperationKind kind, int slotIndex)
        {
            lock (_gate)
            {
                if (_stopping || _operationCount >= _operations.Length)
                    throw new InvalidOperationException("fixture.b1.adapter.operation-stopped");
                var sink = _operations[_operationCount++];
                if (sink.Kind != kind || sink.SlotIndex != slotIndex || !sink.Register())
                {
                    _ambiguous = true;
                    throw new InvalidOperationException("fixture.b1.adapter.operation-order");
                }
                return sink;
            }
        }

        internal void Stop()
        {
            lock (_gate) _stopping = true;
        }

        internal void LatchMutation(bool providerMutation)
        {
            lock (_gate) _mutationStarted |= providerMutation;
        }

        internal void LatchProviderMutation(IFixtureB1AdapterCustodyProvider provider)
        {
            try { LatchMutation(provider.MutationStarted); }
            catch
            {
                lock (_gate)
                {
                    _mutationStarted = true;
                    _ambiguous = true;
                }
            }
        }

        internal void MarkAmbiguous()
        {
            lock (_gate) _ambiguous = true;
        }

        internal void MarkMissingAdoption(AdoptionSink sink)
        {
            if (sink.RequiresAdoption && !sink.HasAdoption)
                MarkAmbiguous();
        }

        internal bool AcceptCoordinate(
            FixtureB1AdapterTargetCoordinate coordinate,
            FixtureB1AdapterLaunchCustody custody,
            FixtureHandoffSlot slot)
        {
            lock (_gate)
                return coordinate.LaunchId == custody.LaunchId &&
                    coordinate.ExactTarget == custody.ExactTarget &&
                    coordinate.Label == slot.Label;
        }

        internal bool AcceptInspection(
            FixtureB1AdapterInspectionLease inspection,
            FixtureB1AdapterLaunchCustody custody)
        {
            lock (_gate)
                return inspection.LaunchId == custody.LaunchId &&
                    inspection.ExactTarget == custody.ExactTarget;
        }

        private bool AdoptTarget(FixtureB1AdapterLaunchCustody custody)
        {
            lock (_gate)
            {
                _mutationStarted = true;
                if (custody is null || _target is not null)
                    return !(_ambiguous = true);
                _target = custody;
                return true;
            }
        }

        private bool AdoptCoordinate(int slotIndex, FixtureB1AdapterTargetCoordinate coordinate)
        {
            lock (_gate)
            {
                _mutationStarted = true;
                if (coordinate is null || slotIndex < 0 || slotIndex >= _coordinates.Length ||
                    _coordinates[slotIndex] is not null || coordinate.Coordinate <= 0 ||
                    CoordinateKnown(coordinate))
                    return !(_ambiguous = true);
                _coordinates[slotIndex] = coordinate;
                return true;
            }
        }

        private bool AdoptInspection(int slotIndex, FixtureB1AdapterInspectionLease inspection)
        {
            lock (_gate)
            {
                _mutationStarted = true;
                if (inspection is null || slotIndex < 0 || slotIndex >= _inspections.Length ||
                    _inspections[slotIndex] is not null || inspection.Lease <= 0 ||
                    InspectionKnown(inspection))
                    return !(_ambiguous = true);
                _inspections[slotIndex] = inspection;
                return true;
            }
        }

        private bool CoordinateKnown(FixtureB1AdapterTargetCoordinate coordinate)
        {
            foreach (var existing in _coordinates)
                if (existing is not null && existing.LaunchId == coordinate.LaunchId &&
                    existing.ExactTarget == coordinate.ExactTarget &&
                    existing.Coordinate == coordinate.Coordinate)
                    return true;
            return false;
        }

        internal bool KnowsTarget(FixtureB1AdapterLaunchCustody custody)
        {
            lock (_gate) return custody is not null && _target == custody;
        }

        internal bool KnowsCoordinate(FixtureB1AdapterTargetCoordinate coordinate)
        {
            lock (_gate) return coordinate is not null && CoordinateKnown(coordinate);
        }

        internal bool KnowsInspection(FixtureB1AdapterInspectionLease inspection)
        {
            lock (_gate) return inspection is not null && InspectionKnown(inspection);
        }

        private bool InspectionKnown(FixtureB1AdapterInspectionLease inspection)
        {
            foreach (var existing in _inspections)
                // This is a local lease: untrusted launch/target metadata cannot make
                // the same handle into a second independently closable resource.
                if (existing is not null && existing.Lease == inspection.Lease)
                    return true;
            return false;
        }

        internal sealed class AdoptionSink(
            CustodyLedger owner,
            int operationId,
            FixtureB1AdapterOperationKind kind,
            int slotIndex) : IFixtureB1AdapterAdoptionSink
        {
            private int _completed;
            private int _adopted;
            private int _registered;
            internal FixtureB1AdapterLaunchCustody? Target { get; private set; }
            internal FixtureB1AdapterTargetCoordinate? Coordinate { get; private set; }
            internal FixtureB1AdapterInspectionLease? Inspection { get; private set; }
            public int OperationId { get; } = operationId;
            public FixtureB1AdapterOperationKind Kind { get; } = kind;
            internal int SlotIndex { get; } = slotIndex;
            internal bool RequiresAdoption => Kind is
                FixtureB1AdapterOperationKind.PrepareTarget or
                FixtureB1AdapterOperationKind.DuplicateCoordinate or
                FixtureB1AdapterOperationKind.AcquireInspection;
            internal bool HasAdoption => Volatile.Read(ref _adopted) != 0;
            internal bool Register() =>
                Interlocked.CompareExchange(ref _registered, 1, 0) == 0;

            public FixtureB1AdoptionDisposition TryAdoptTarget(
                FixtureB1AdapterLaunchCustody custody)
            {
                if (Kind != FixtureB1AdapterOperationKind.PrepareTarget ||
                    Volatile.Read(ref _registered) == 0 ||
                    Volatile.Read(ref _completed) != 0 ||
                    Interlocked.CompareExchange(ref _adopted, 1, 0) != 0)
                {
                    owner.MarkAmbiguous();
                    return owner.KnowsTarget(custody)
                        ? FixtureB1AdoptionDisposition.AlreadyOwned
                        : FixtureB1AdoptionDisposition.ProviderRetainsNewResource;
                }
                Target = custody;
                try
                {
                    if (owner.AdoptTarget(custody))
                        return FixtureB1AdoptionDisposition.Adopted;
                }
                catch { owner.MarkAmbiguous(); }
                return owner.KnowsTarget(custody)
                    ? FixtureB1AdoptionDisposition.AlreadyOwned
                    : FixtureB1AdoptionDisposition.ProviderRetainsNewResource;
            }

            public FixtureB1AdoptionDisposition TryAdoptCoordinate(
                FixtureB1AdapterTargetCoordinate coordinate)
            {
                if (Kind != FixtureB1AdapterOperationKind.DuplicateCoordinate ||
                    Volatile.Read(ref _registered) == 0 ||
                    Volatile.Read(ref _completed) != 0 ||
                    Interlocked.CompareExchange(ref _adopted, 1, 0) != 0)
                {
                    owner.MarkAmbiguous();
                    return owner.KnowsCoordinate(coordinate)
                        ? FixtureB1AdoptionDisposition.AlreadyOwned
                        : FixtureB1AdoptionDisposition.ProviderRetainsNewResource;
                }
                Coordinate = coordinate;
                try
                {
                    if (owner.AdoptCoordinate(SlotIndex, coordinate))
                        return FixtureB1AdoptionDisposition.Adopted;
                }
                catch { owner.MarkAmbiguous(); }
                return owner.KnowsCoordinate(coordinate)
                    ? FixtureB1AdoptionDisposition.AlreadyOwned
                    : FixtureB1AdoptionDisposition.ProviderRetainsNewResource;
            }

            public FixtureB1AdoptionDisposition TryAdoptInspection(
                FixtureB1AdapterInspectionLease inspection)
            {
                if (Kind != FixtureB1AdapterOperationKind.AcquireInspection ||
                    Volatile.Read(ref _registered) == 0 ||
                    Volatile.Read(ref _completed) != 0 ||
                    Interlocked.CompareExchange(ref _adopted, 1, 0) != 0)
                {
                    owner.MarkAmbiguous();
                    return owner.KnowsInspection(inspection)
                        ? FixtureB1AdoptionDisposition.AlreadyOwned
                        : FixtureB1AdoptionDisposition.ProviderRetainsNewResource;
                }
                Inspection = inspection;
                try
                {
                    if (owner.AdoptInspection(SlotIndex, inspection))
                        return FixtureB1AdoptionDisposition.Adopted;
                }
                catch { owner.MarkAmbiguous(); }
                return owner.KnowsInspection(inspection)
                    ? FixtureB1AdoptionDisposition.AlreadyOwned
                    : FixtureB1AdoptionDisposition.ProviderRetainsNewResource;
            }

            internal void Complete() => Interlocked.Exchange(ref _completed, 1);
        }
    }
}
