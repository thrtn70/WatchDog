using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal sealed record FixtureB1LauncherPreparation(
    FixtureBrokerTargetRegistration Registration,
    FixtureBrokerSnapshot Snapshot,
    byte[] SnapshotBytes,
    byte[] SnapshotMac,
    byte[] Secret);

internal sealed record FixtureB1LauncherConnection(
    long ConnectionReceipt,
    FixtureHandoffProcessIdentity ObservedTarget);

internal sealed class FixtureB1LauncherCleanupObservation
{
    internal FixtureB1LauncherCleanupObservation(
        FixtureHandoffProcessIdentity? exactTarget,
        bool exactTargetReaped,
        bool outerJobEmpty,
        IReadOnlyDictionary<nint, FixtureArtifactCloseStatus> transferCloses,
        bool issuerCustodyClosed,
        bool observersDrained,
        bool artifactOperationsDrained,
        bool retainedRootAndOutputsAdmitted)
    {
        ExactTarget = exactTarget;
        ExactTargetReaped = exactTargetReaped;
        OuterJobEmpty = outerJobEmpty;
        TransferCloses = new ReadOnlyDictionary<nint, FixtureArtifactCloseStatus>(
            new Dictionary<nint, FixtureArtifactCloseStatus>(transferCloses));
        IssuerCustodyClosed = issuerCustodyClosed;
        ObserversDrained = observersDrained;
        ArtifactOperationsDrained = artifactOperationsDrained;
        RetainedRootAndOutputsAdmitted = retainedRootAndOutputsAdmitted;
    }

    internal FixtureHandoffProcessIdentity? ExactTarget { get; }
    internal bool ExactTargetReaped { get; }
    internal bool OuterJobEmpty { get; }
    internal IReadOnlyDictionary<nint, FixtureArtifactCloseStatus> TransferCloses { get; }
    internal bool IssuerCustodyClosed { get; }
    internal bool ObserversDrained { get; }
    internal bool ArtifactOperationsDrained { get; }
    internal bool RetainedRootAndOutputsAdmitted { get; }
}

internal sealed record FixtureB1LauncherLocalCloseReport(
    bool ReapingLeaseClosed,
    bool RetainedRootClosed,
    bool OutputHandlesClosed,
    bool ProcessAndThreadHandlesClosed,
    bool OuterJobClosed,
    bool TransportClosed,
    bool RejectedAdoptionsClosed)
{
    internal bool Complete => ReapingLeaseClosed && RetainedRootClosed &&
        OutputHandlesClosed && ProcessAndThreadHandlesClosed && OuterJobClosed &&
        TransportClosed && RejectedAdoptionsClosed;
}

internal sealed class FixtureB1LauncherResult
{
    internal FixtureB1LauncherResult(
        Guid runNonce,
        Guid launchId,
        FixtureHandoffProcessIdentity launcher,
        FixtureHandoffProcessIdentity? target,
        bool activationAcknowledged,
        bool cleanupConfirmed,
        bool abortConfirmed,
        bool drainConfirmed,
        QualificationExitCode exitCode,
        FixtureB1LauncherCleanupObservation? cleanup,
        FixtureB1LauncherLocalCloseReport? localClose)
    {
        RunNonce = runNonce;
        LaunchId = launchId;
        Launcher = launcher;
        Target = target;
        ActivationAcknowledged = activationAcknowledged;
        CleanupConfirmed = cleanupConfirmed;
        AbortConfirmed = abortConfirmed;
        DrainConfirmed = drainConfirmed;
        ExitCode = exitCode;
        Cleanup = cleanup;
        LocalClose = localClose;
    }

    internal Guid RunNonce { get; }
    internal Guid LaunchId { get; }
    internal FixtureHandoffProcessIdentity Launcher { get; }
    internal FixtureHandoffProcessIdentity? Target { get; }
    internal bool ActivationAcknowledged { get; }
    internal bool CleanupConfirmed { get; }
    internal bool AbortConfirmed { get; }
    internal bool DrainConfirmed { get; }
    internal QualificationExitCode ExitCode { get; }
    internal FixtureB1LauncherCleanupObservation? Cleanup { get; }
    internal FixtureB1LauncherLocalCloseReport? LocalClose { get; }
}

/// <summary>
/// One retained-launch session. A native adapter must make AbortAsync finite and must not
/// complete DrainAsync until every original I/O task is terminal. If an arbitrary adapter
/// never unblocks an aborted operation, ExecuteAsync intentionally remains pending rather
/// than detaching work or returning a false cleanup receipt.
/// </summary>
internal interface IFixtureB1LauncherOwnedSession
{
    Guid LaunchId { get; }
    string ControlEndpoint { get; }
    FixtureHandoffProcessIdentity Launcher { get; }
    IFixtureBrokerAuthority BrokerAuthority { get; }
    TimeProvider Clock { get; }
    // The retained session owns this one lazy cleanup budget through any uncertain return.
    FixtureB1CleanupContext CleanupContext { get; }
    // Called before the first mutating provider operation. The session retains the ledger
    // until every adopted or provider-retained resource is reconciled or explicitly uncertain.
    void RetainCustodyLedger(FixtureB1AdapterCustodyModel.CustodyLedger ledger);
    // Monotonic: once any root, output, process, job, or transfer mutation starts,
    // this remains true through reconciliation and final local close.
    bool MutationStarted { get; }

    bool AreCommandClaimsAttributed(FixtureB1LauncherCommand command);

    FixtureHandoffProcessIdentity ObserveCreator();
    FixtureHandoffProcessIdentity ObserveIssuer();
    FixtureHandoffProcessIdentity? ObserveRetainedTarget();

    ValueTask<FixtureB1LauncherPreparation> PrepareTargetAsync(
        FixtureArtifactHandoffPlan plan,
        FixtureB1LauncherCommand command,
        IFixtureB1AdapterAdoptionSink adoption,
        CancellationToken cancellationToken);

    ValueTask<FixtureBrokerDeliveryReceipt> DuplicateNextSlotAsync(
        FixtureHandoffSlot slot,
        int index,
        IFixtureB1AdapterAdoptionSink adoption,
        CancellationToken cancellationToken);

    ValueTask ResumeTargetAsync(
        FixtureHandoffProcessIdentity exactTarget,
        CancellationToken cancellationToken);

    ValueTask<FixtureB1LauncherConnection> AcceptRunnerAsync(
        string controlEndpoint,
        CancellationToken cancellationToken);

    ValueTask SendDescriptorAsync(
        long connectionReceipt,
        FixtureBrokerDescriptor descriptor,
        CancellationToken cancellationToken);

    ValueTask<FixtureBrokerFrameObservation> ReceiveProofAsync(
        long connectionReceipt,
        CancellationToken cancellationToken);

    ValueTask SendGrantAsync(
        long connectionReceipt,
        ReadOnlyMemory<byte> grant,
        CancellationToken cancellationToken);

    ValueTask<FixtureBrokerFrameObservation> ReceiveGrantAckAsync(
        long connectionReceipt,
        CancellationToken cancellationToken);

    ValueTask SendActivationAsync(
        long connectionReceipt,
        ReadOnlyMemory<byte> activation,
        CancellationToken cancellationToken);

    ValueTask<FixtureBrokerFrameObservation> ReceiveActivationAckAsync(
        long connectionReceipt,
        CancellationToken cancellationToken);

    ValueTask AbortAsync(CancellationToken cancellationToken);
    ValueTask DrainAsync(CancellationToken cancellationToken);

    ValueTask<FixtureB1LauncherCleanupObservation> ReconcileAsync(
        FixtureHandoffProcessIdentity? exactTarget,
        IReadOnlyList<FixtureBrokerDeliveryReceipt> journaledTransfers,
        CancellationToken cancellationToken);

    // False forbids release of the independent reaping capability. It must stay with
    // the durable session owner until exact target and job terminal proof exists.
    ValueTask<FixtureB1LauncherLocalCloseReport> CloseLocalAsync(
        bool mayReleaseReaper,
        CancellationToken cancellationToken);

    // Reobserve after local close; a prior reconciliation receipt cannot prove that
    // the retained root and outputs stayed admitted while handles were closed.
    bool AreRetainedRootAndOutputsAdmitted();
}

/// <summary>
/// Pure, unregistered launcher-side B1 orchestration. It issues no native call and grants no
/// lasting broker, session, content, or write authority. PASS requires the full authenticated
/// exchange, broker reconciliation, and final local/reaping-lease close.
/// </summary>
internal sealed class FixtureB1LauncherModel
{
    private readonly IFixtureB1LauncherOwnedSession _session;
    private readonly TimeSpan _readinessBudget;
    private int _used;

    internal FixtureB1LauncherModel(
        IFixtureB1LauncherOwnedSession session,
        TimeSpan? readinessBudget = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
        _readinessBudget = readinessBudget ?? QualificationConstants.ReadinessTimeout;
        if (_readinessBudget <= TimeSpan.Zero ||
            _readinessBudget > QualificationConstants.ReadinessTimeout)
            throw new ArgumentOutOfRangeException(nameof(readinessBudget));
    }

    internal async Task<FixtureB1LauncherResult> ExecuteAsync(
        FixtureB1LauncherCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Interlocked.CompareExchange(ref _used, 1, 0) != 0)
            throw new InvalidOperationException("fixture.b1.launcher.one-use-rejected");

        using var deadline = new CancellationTokenSource(_readinessBudget, _session.Clock);
        using var active = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token);
        var operation = new ActiveOperation(_session, active.Token, deadline.Token);
        FixtureArtifactHandoffPlan? plan = null;
        FixtureBrokerState? broker = null;
        FixtureB1AdapterCustodyModel.CustodyLedger? custodyLedger = null;
        FixtureHandoffProcessIdentity? target = null;
        var journal = new List<FixtureBrokerDeliveryReceipt>();
        var deliveryCustodyUncertain = false;
        var activated = false;
        var requested = QualificationExitCode.UsageOrPrerequisite;

        try
        {
            active.Token.ThrowIfCancellationRequested();
            if (command.RunNonce == Guid.Empty || _session.LaunchId == Guid.Empty ||
                command.RunNonce == _session.LaunchId ||
                string.IsNullOrWhiteSpace(_session.ControlEndpoint) ||
                !_session.AreCommandClaimsAttributed(command))
                throw new FixtureB1LauncherException(
                    FixtureBrokerFailureKind.Prerequisite,
                    QualificationExitCode.UsageOrPrerequisite);

            var creator = _session.ObserveCreator();
            var issuer = _session.ObserveIssuer();
            if (!creator.IsValid || !issuer.IsValid || creator != issuer ||
                creator != _session.Launcher)
                throw Assertion();

            plan = FixtureArtifactHandoffPlan.Create(
                FixtureHandoffEdge.LauncherToRunner,
                null,
                command.RunNonce,
                _session.LaunchId);
            if (plan.Slots.Count != 7)
                throw Assertion();

            broker = FixtureBrokerState.Reserve(
                plan,
                creator,
                issuer,
                FixtureBrokerDeliveryMode.LocalLauncher,
                _session.BrokerAuthority,
                _session.Clock,
                _readinessBudget,
                active.Token);

            custodyLedger = new(plan.Slots.Count, includeInspections: false);
            _session.RetainCustodyLedger(custodyLedger);
            var preparationSink = custodyLedger.Register(
                FixtureB1AdapterOperationKind.PrepareTarget, 0);
            custodyLedger.LatchMutation(true);

            FixtureB1LauncherPreparation preparation;
            try
            {
                preparation = await operation.RunAsync(
                    token => _session.PrepareTargetAsync(
                        plan, command, preparationSink, token),
                    static abandoned =>
                    {
                        if (abandoned?.Secret is not null)
                            CryptographicOperations.ZeroMemory(abandoned.Secret);
                    }).ConfigureAwait(false);
            }
            finally
            {
                preparationSink.Complete();
                custodyLedger.MarkMissingAdoption(preparationSink);
            }
            var secret = preparation?.Secret;
            var adoptedTarget = preparationSink.Target;
            var adoptedTargetValid = adoptedTarget is not null &&
                FixtureB1AdapterCustodyModel.ValidCustodyShape(
                    adoptedTarget, _session.LaunchId);
            if (!adoptedTargetValid ||
                (preparation?.Registration is not null &&
                    adoptedTarget!.ExactTarget != preparation.Registration.Target))
                custodyLedger.MarkAmbiguous();
            try
            {
                if (preparation is null || preparation.Registration is null ||
                    preparation.Snapshot is null || preparation.SnapshotBytes is null ||
                    preparation.SnapshotMac is null || secret is null ||
                    !adoptedTargetValid ||
                    adoptedTarget!.ExactTarget != preparation.Registration.Target)
                    throw Assertion();
                target = preparation.Registration.Target;
                broker.RegisterTarget(
                    preparation.Registration,
                    preparation.Snapshot,
                    preparation.SnapshotBytes,
                    preparation.SnapshotMac,
                    secret);
            }
            finally
            {
                if (secret is not null)
                    CryptographicOperations.ZeroMemory(secret);
            }

            for (var index = 0; index < plan.Slots.Count; index++)
            {
                var duplicateSink = custodyLedger.Register(
                    FixtureB1AdapterOperationKind.DuplicateCoordinate, index);
                FixtureBrokerDeliveryReceipt receipt;
                var lateReceiptJournaled = false;
                var duplicationStarted = false;
                custodyLedger.LatchMutation(true);
                try
                {
                    receipt = await operation.RunAsync(token =>
                    {
                        duplicationStarted = true;
                        return _session.DuplicateNextSlotAsync(
                            plan.Slots[index], index, duplicateSink, token);
                    },
                        lateReceipt =>
                        {
                            journal.Add(lateReceipt);
                            lateReceiptJournaled = true;
                            if (!MatchesAdoptedCoordinate(
                                    duplicateSink.Coordinate, lateReceipt,
                                    target, plan.Slots[index].Label))
                            {
                                deliveryCustodyUncertain = true;
                                custodyLedger.MarkAmbiguous();
                            }
                        }).ConfigureAwait(false);
                }
                catch
                {
                    // A fault may follow successful native duplication without returning
                    // its target coordinate. No journal can prove that transfer closed.
                    if (duplicationStarted && !lateReceiptJournaled)
                        deliveryCustodyUncertain = true;
                    throw;
                }
                finally
                {
                    duplicateSink.Complete();
                    custodyLedger.MarkMissingAdoption(duplicateSink);
                }
                var adoptedCoordinate = duplicateSink.Coordinate;
                if (!MatchesAdoptedCoordinate(
                        adoptedCoordinate, receipt, target, plan.Slots[index].Label))
                {
                    deliveryCustodyUncertain = true;
                    custodyLedger.MarkAmbiguous();
                    throw Assertion();
                }
                // Journal immediately: no later duplication can begin before broker custody.
                journal.Add(receipt);
                broker.RecordDelivery(receipt);
            }

            var descriptor = broker.SealDelivery();
            await operation.RunAsync(token =>
                _session.ResumeTargetAsync(target, token)).ConfigureAwait(false);
            broker.MarkResumed();

            var connection = await operation.RunAsync(token =>
                _session.AcceptRunnerAsync(_session.ControlEndpoint, token))
                .ConfigureAwait(false);
            active.Token.ThrowIfCancellationRequested();
            if (connection.ConnectionReceipt <= 0 || connection.ObservedTarget != target ||
                _session.BrokerAuthority.ObserveConnectedPeer(
                    connection.ConnectionReceipt) != target)
                throw Assertion();

            RequirePeer(connection.ConnectionReceipt, target);
            await operation.RunAsync(token => _session.SendDescriptorAsync(
                connection.ConnectionReceipt, descriptor, token)).ConfigureAwait(false);

            var proof = await operation.RunAsync(token => _session.ReceiveProofAsync(
                connection.ConnectionReceipt, token)).ConfigureAwait(false);
            RequireFrame(proof, connection.ConnectionReceipt, target);
            var grant = broker.AcceptChildProof(proof);
            try
            {
                RequirePeer(connection.ConnectionReceipt, target);
                await operation.RunAsync(token => _session.SendGrantAsync(
                    connection.ConnectionReceipt, grant, token)).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(grant);
            }

            var grantAck = await operation.RunAsync(token =>
                _session.ReceiveGrantAckAsync(connection.ConnectionReceipt, token))
                .ConfigureAwait(false);
            RequireFrame(grantAck, connection.ConnectionReceipt, target);
            broker.AcceptGrantAck(grantAck);

            var activation = broker.IssueActivation();
            try
            {
                RequirePeer(connection.ConnectionReceipt, target);
                await operation.RunAsync(token => _session.SendActivationAsync(
                    connection.ConnectionReceipt, activation, token)).ConfigureAwait(false);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(activation);
            }

            var activationAck = await operation.RunAsync(token =>
                _session.ReceiveActivationAckAsync(connection.ConnectionReceipt, token))
                .ConfigureAwait(false);
            RequireFrame(activationAck, connection.ConnectionReceipt, target);
            broker.ConfirmChildActive(activationAck);
            activated = true;
            requested = QualificationExitCode.Pass;
        }
        catch (FixtureB1OperationStoppedException stopped)
        {
            requested = stopped.DeadlineExpired
                ? QualificationExitCode.TimeoutCleanupConfirmed
                : QualificationExitCode.AssertionFailedCleanupConfirmed;
            SafeFail(broker, stopped.DeadlineExpired
                ? FixtureBrokerFailureKind.Deadline
                : FixtureBrokerFailureKind.Assertion);
        }
        catch (FixtureB1LauncherException exception)
        {
            requested = exception.Requested;
            SafeFail(broker, exception.Failure);
        }
        catch (FixtureBrokerContractException)
        {
            requested = QualificationExitCode.AssertionFailedCleanupConfirmed;
            SafeFail(broker, FixtureBrokerFailureKind.Assertion);
        }
        catch (OperationCanceledException)
        {
            requested = deadline.IsCancellationRequested
                ? QualificationExitCode.TimeoutCleanupConfirmed
                : QualificationExitCode.AssertionFailedCleanupConfirmed;
            SafeFail(broker, deadline.IsCancellationRequested
                ? FixtureBrokerFailureKind.Deadline
                : FixtureBrokerFailureKind.Assertion);
            await operation.AbortAndDrainAsync(Task.CompletedTask).ConfigureAwait(false);
        }
        catch
        {
            requested = QualificationExitCode.InfrastructureExceptionCleanupConfirmed;
            SafeFail(broker, FixtureBrokerFailureKind.Infrastructure);
            await operation.AbortAndDrainAsync(Task.CompletedTask).ConfigureAwait(false);
        }

        FixtureB1LauncherCleanupObservation? cleanup = null;
        FixtureB1LauncherLocalCloseReport? localClose = null;
        custodyLedger?.Stop();
        var cleanupUncertain = false;
        var drained = false;
        QualificationExitCode exitCode;
        var cleanupToken = operation.CleanupToken;
        try
        {
            await operation.DrainForCleanupAsync(cleanupToken).ConfigureAwait(false);
            drained = true;
        }
        catch
        {
            cleanupUncertain = true;
            SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);
        }
        if (drained)
        {
            try
            {
                target ??= _session.ObserveRetainedTarget();
            }
            catch
            {
                cleanupUncertain = true;
                SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);
            }
            try
            {
                cleanup = await _session.ReconcileAsync(
                    target, journal.AsReadOnly(), cleanupToken).ConfigureAwait(false);
            }
            catch
            {
                cleanupUncertain = true;
                SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);
            }
        }
        if (cleanup is not null)
            ApplyCleanup(broker, target, journal, cleanup);
        else
            SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);

        if (deliveryCustodyUncertain)
        {
            cleanupUncertain = true;
            SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);
        }
        if (custodyLedger?.Ambiguous == true)
        {
            cleanupUncertain = true;
            SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            requested = QualificationExitCode.AssertionFailedCleanupConfirmed;
            SafeFail(broker, FixtureBrokerFailureKind.Assertion);
        }

        // ExitAfterCleanup is called exactly once, after every reconciliation attempt.
        if (broker is null)
            exitCode = cleanupUncertain
                ? QualificationExitCode.CleanupIncomplete
                : requested;
        else
        {
            try
            {
                exitCode = broker.ExitAfterCleanup(cleanupUncertain
                    ? QualificationExitCode.CleanupIncomplete
                    : requested);
            }
            catch
            {
                exitCode = QualificationExitCode.CleanupIncomplete;
            }
        }

        bool mutationStarted;
        var mutationKnown = true;
        try
        {
            mutationStarted = _session.MutationStarted ||
                custodyLedger?.MutationStarted == true;
        }
        catch
        {
            mutationStarted = true;
            mutationKnown = false;
        }

        if (drained)
        {
            try
            {
                var retainedCustody = custodyLedger?.Target;
                var mayReleaseReaper = !mutationStarted ||
                    (target is not null && cleanup is not null &&
                        retainedCustody is not null &&
                        cleanup.ExactTarget == target &&
                        retainedCustody.ExactTarget == target &&
                        FixtureB1AdapterCustodyModel.ValidCustodyShape(
                            retainedCustody, _session.LaunchId) &&
                        cleanup.ExactTargetReaped && cleanup.OuterJobEmpty);
                localClose = await _session.CloseLocalAsync(
                    mayReleaseReaper, cleanupToken).ConfigureAwait(false);
                if (!localClose.Complete ||
                    (!mayReleaseReaper && localClose.ReapingLeaseClosed))
                    exitCode = QualificationExitCode.CleanupIncomplete;
            }
            catch
            {
                exitCode = QualificationExitCode.CleanupIncomplete;
            }
        }
        else
            exitCode = QualificationExitCode.CleanupIncomplete;

        if (drained)
        {
            try
            {
                if (!_session.AreRetainedRootAndOutputsAdmitted())
                    exitCode = QualificationExitCode.CleanupIncomplete;
            }
            catch
            {
                exitCode = QualificationExitCode.CleanupIncomplete;
            }
        }

        // An adapter can complete successfully after ignoring an expired cleanup token.
        // Elapsed budget cannot be converted into a cleanup-confirmed result.
        if (_session.CleanupContext.IsExpired)
            exitCode = QualificationExitCode.CleanupIncomplete;
        if (custodyLedger?.Ambiguous == true)
            exitCode = QualificationExitCode.CleanupIncomplete;

        var cleanupConfirmed = exitCode != QualificationExitCode.CleanupIncomplete &&
            CleanupReportComplete(cleanup, target, journal) &&
            localClose?.Complete == true &&
            (broker?.CleanupConfirmed ?? true) && mutationKnown &&
            (!mutationStarted || target is not null);

        if (!cleanupConfirmed)
            exitCode = QualificationExitCode.CleanupIncomplete;
        if (!operation.AbortConfirmed || !operation.DrainConfirmed)
        {
            cleanupConfirmed = false;
            exitCode = QualificationExitCode.CleanupIncomplete;
        }
        if (cancellationToken.IsCancellationRequested &&
            exitCode == QualificationExitCode.Pass)
            exitCode = QualificationExitCode.AssertionFailedCleanupConfirmed;

        return new FixtureB1LauncherResult(
            command.RunNonce,
            _session.LaunchId,
            _session.Launcher,
            target,
            activated,
            cleanupConfirmed,
            operation.AbortConfirmed,
            operation.DrainConfirmed,
            exitCode,
            cleanup,
            localClose);
    }

    private static void ApplyCleanup(
        FixtureBrokerState? broker,
        FixtureHandoffProcessIdentity? target,
        IReadOnlyList<FixtureBrokerDeliveryReceipt> journal,
        FixtureB1LauncherCleanupObservation cleanup)
    {
        ArgumentNullException.ThrowIfNull(cleanup);
        if (broker is null || !broker.TargetCustodyAdopted)
            return;

        foreach (var receipt in journal)
        {
            if (cleanup.TransferCloses.TryGetValue(receipt.TargetHandle, out var status) &&
                status == FixtureArtifactCloseStatus.Closed)
            {
                if (broker.OwnsPendingTransfer(receipt.TargetHandle))
                    TryCleanupConfirmation(
                        broker, () => broker.ConfirmTransferClosed(receipt.TargetHandle));
            }
            else
                SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);
        }
        if (cleanup.IssuerCustodyClosed)
            TryCleanupConfirmation(broker, broker.ConfirmIssuerCustodyClosed);
        else
            SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);
        if (target is not null && cleanup.ExactTarget == target &&
            cleanup.ExactTargetReaped && cleanup.OuterJobEmpty)
            TryCleanupConfirmation(broker, () => broker.ConfirmExactTargetReaped(target));
        else
            SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);
        if (cleanup.ObserversDrained)
            TryCleanupConfirmation(broker, broker.ConfirmObserversDrained);
        else
            SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);
        if (cleanup.ArtifactOperationsDrained)
            TryCleanupConfirmation(broker, broker.ConfirmArtifactOperationsDrained);
        else
            SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);
        if (!cleanup.RetainedRootAndOutputsAdmitted ||
            cleanup.TransferCloses.Count != journal.Count)
            SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous);
    }

    private static void TryCleanupConfirmation(
        FixtureBrokerState broker,
        Action confirmation)
    {
        try { confirmation(); }
        catch { SafeFail(broker, FixtureBrokerFailureKind.CleanupAmbiguous); }
    }

    private static bool CleanupReportComplete(
        FixtureB1LauncherCleanupObservation? cleanup,
        FixtureHandoffProcessIdentity? target,
        IReadOnlyList<FixtureBrokerDeliveryReceipt> journal)
    {
        if (cleanup is null || !cleanup.OuterJobEmpty ||
            !cleanup.ObserversDrained || !cleanup.ArtifactOperationsDrained ||
            !cleanup.RetainedRootAndOutputsAdmitted)
            return false;

        if (target is not null &&
            (cleanup.ExactTarget != target || !cleanup.ExactTargetReaped ||
                !cleanup.IssuerCustodyClosed))
            return false;
        if (target is null && cleanup.ExactTarget is not null)
            return false;

        var expected = journal.Select(receipt => receipt.TargetHandle).ToHashSet();
        return cleanup.TransferCloses.Count == expected.Count &&
            cleanup.TransferCloses.All(pair => expected.Contains(pair.Key) &&
                pair.Value == FixtureArtifactCloseStatus.Closed);
    }

    private bool MatchesAdoptedCoordinate(
        FixtureB1AdapterTargetCoordinate? adopted,
        FixtureBrokerDeliveryReceipt receipt,
        FixtureHandoffProcessIdentity? target,
        string label) =>
        adopted is not null && target is not null &&
        adopted.LaunchId == _session.LaunchId &&
        adopted.ExactTarget == target &&
        adopted.Label == label &&
        adopted.Coordinate == (long)receipt.TargetHandle;

    private static void SafeFail(FixtureBrokerState? broker, FixtureBrokerFailureKind kind)
    {
        if (broker is null)
            return;
        try { broker.Fail(kind); }
        catch { }
    }

    private static FixtureB1LauncherException Assertion() =>
        new(FixtureBrokerFailureKind.Assertion,
            QualificationExitCode.AssertionFailedCleanupConfirmed);

    private void RequirePeer(
        long connectionReceipt,
        FixtureHandoffProcessIdentity target)
    {
        if (connectionReceipt <= 0 ||
            _session.BrokerAuthority.ObserveConnectedPeer(connectionReceipt) != target)
            throw Assertion();
    }

    private void RequireFrame(
        FixtureBrokerFrameObservation observation,
        long connectionReceipt,
        FixtureHandoffProcessIdentity target)
    {
        if (observation is null || observation.Frame is null ||
            observation.ConnectionReceipt != connectionReceipt)
            throw Assertion();
        RequirePeer(connectionReceipt, target);
    }

    private sealed class ActiveOperation(
        IFixtureB1LauncherOwnedSession session,
        CancellationToken activeToken,
        CancellationToken deadlineToken)
    {
        private int _abortStarted;
        internal bool AbortConfirmed { get; private set; } = true;
        internal bool DrainConfirmed { get; private set; }

        internal CancellationToken CleanupToken => session.CleanupContext.Token;

        internal async Task<T> RunAsync<T>(
            Func<CancellationToken, ValueTask<T>> start,
            Action<T>? discard = null)
        {
            activeToken.ThrowIfCancellationRequested();
            Task<T> original;
            try { original = start(activeToken).AsTask(); }
            catch
            {
                await AbortAndDrainAsync(Task.CompletedTask).ConfigureAwait(false);
                throw;
            }

            try
            {
                var cancellation = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = activeToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(),
                    cancellation);
                if (await Task.WhenAny(original, cancellation.Task).ConfigureAwait(false) !=
                    original)
                {
                    await AbortAndDrainAsync(original).ConfigureAwait(false);
                    if (original.Status == TaskStatus.RanToCompletion)
                        discard?.Invoke(original.Result);
                    throw new FixtureB1OperationStoppedException(
                        deadlineToken.IsCancellationRequested);
                }
                var result = await original.ConfigureAwait(false);
                if (activeToken.IsCancellationRequested)
                {
                    discard?.Invoke(result);
                    throw new OperationCanceledException(activeToken);
                }
                return result;
            }
            catch
            {
                await AbortAndDrainAsync(original).ConfigureAwait(false);
                if (activeToken.IsCancellationRequested)
                    throw new FixtureB1OperationStoppedException(
                        deadlineToken.IsCancellationRequested);
                throw;
            }
        }

        internal async Task RunAsync(Func<CancellationToken, ValueTask> start)
        {
            activeToken.ThrowIfCancellationRequested();
            Task original;
            try { original = start(activeToken).AsTask(); }
            catch
            {
                await AbortAndDrainAsync(Task.CompletedTask).ConfigureAwait(false);
                throw;
            }

            try
            {
                var cancellation = new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = activeToken.Register(
                    static state => ((TaskCompletionSource)state!).TrySetResult(),
                    cancellation);
                if (await Task.WhenAny(original, cancellation.Task).ConfigureAwait(false) !=
                    original)
                {
                    await AbortAndDrainAsync(original).ConfigureAwait(false);
                    throw new FixtureB1OperationStoppedException(
                        deadlineToken.IsCancellationRequested);
                }
                await original.ConfigureAwait(false);
                activeToken.ThrowIfCancellationRequested();
            }
            catch
            {
                await AbortAndDrainAsync(original).ConfigureAwait(false);
                if (activeToken.IsCancellationRequested)
                    throw new FixtureB1OperationStoppedException(
                        deadlineToken.IsCancellationRequested);
                throw;
            }
        }

        internal async Task AbortAndDrainAsync(Task original)
        {
            var cleanupToken = CleanupToken;
            if (Interlocked.CompareExchange(ref _abortStarted, 1, 0) != 0)
            {
                await ObserveAsync(original).ConfigureAwait(false);
                return;
            }

            try
            {
                var abort = session.AbortAsync(cleanupToken).AsTask();
                await abort.ConfigureAwait(false);
            }
            catch { AbortConfirmed = false; }

            // Never abandon the original task. The adapter's abort contract must make it
            // terminal; otherwise this method remains pending and cannot claim cleanup.
            await ObserveAsync(original).ConfigureAwait(false);

            try
            {
                var drain = session.DrainAsync(cleanupToken).AsTask();
                await drain.ConfigureAwait(false);
                DrainConfirmed = true;
            }
            catch { DrainConfirmed = false; }
        }

        internal async Task DrainForCleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                var drain = session.DrainAsync(cancellationToken).AsTask();
                await drain.ConfigureAwait(false);
                DrainConfirmed = true;
            }
            catch
            {
                DrainConfirmed = false;
                throw;
            }
        }

        private static async Task ObserveAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch { }
        }
    }

    private sealed class FixtureB1OperationStoppedException(bool deadlineExpired)
        : Exception
    {
        internal bool DeadlineExpired { get; } = deadlineExpired;
    }

    private sealed class FixtureB1LauncherException(
        FixtureBrokerFailureKind failure,
        QualificationExitCode requested) : Exception
    {
        internal FixtureBrokerFailureKind Failure { get; } = failure;
        internal QualificationExitCode Requested { get; } = requested;
    }
}
