using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1ReleaseCleanupModelTests
{
    private static readonly FixtureHandoffProcessIdentity Wrapper = new(701, 7_010);
    private static readonly FixtureHandoffProcessIdentity Launcher = new(702, 7_020);
    private static readonly FixtureHandoffProcessIdentity Runner = new(703, 7_030);

    [Fact]
    public void EightFreshClaimsKeepSlotOrderAndRequireCompleteWCloseReport()
    {
        var authority = new FakeAuthority();
        var model = NewModel(authority);

        foreach (var step in Enum.GetValues<FixtureB1ReleaseStep>())
            CompleteStep(model, step);

        Assert.Equal(8, authority.ReleaseChecks);
        Assert.False(model.CanClaimNextRelease);
        Assert.False(model.CanReportPass);

        model.StartCleanup();
        Assert.True(model.TryConfirmDrain(Guid.NewGuid()));
        RecordAllTerminals(model);
        foreach (var kind in Enum.GetValues<FixtureB1CloseKind>())
        {
            var claim = model.TryBeginClose(kind, Guid.NewGuid());
            Assert.NotNull(claim);
            Assert.True(model.TryConfirmClose(claim!, Guid.NewGuid()));
        }

        var report = model.Snapshot();
        Assert.True(report.CleanupConfirmed);
        Assert.False(report.AdapterRetainsOwnership);
        Assert.True(model.CanReportPass);
        Assert.False(model.NativeQualificationEstablished);
        Assert.False(model.NativeReleaseSynchronizationEstablished);
    }

    [Fact]
    public void OutOfOrderSlotAttemptPermanentlyClosesReleaseAdmission()
    {
        var model = NewModel();

        Assert.Null(model.TryClaimRelease(FixtureB1ReleaseStep.Slot2, Guid.NewGuid()));
        Assert.False(model.CanClaimNextRelease);
        Assert.Null(model.TryClaimRelease(FixtureB1ReleaseStep.Slot1, Guid.NewGuid()));
        Assert.False(model.CanReportPass);
    }

    [Fact]
    public void LauncherOwnedLeaseCannotSatisfyWrapperCloseInventory()
    {
        var custody = new FixtureB1ReleaseCustody(
            Guid.NewGuid(), Guid.NewGuid(), Wrapper, Launcher, Runner,
            Guid.NewGuid(), Guid.NewGuid(), 1);
        var leases = Enum.GetValues<FixtureB1CloseKind>()
            .Select(kind => new FixtureB1RetainedLease(
                kind,
                kind == FixtureB1CloseKind.SafetyJob
                    ? FixtureB1OperationOwner.Launcher
                    : FixtureB1OperationOwner.Wrapper,
                Guid.NewGuid(), 1));

        Assert.Throws<ArgumentException>(() =>
            new FixtureB1ReleaseCleanupModel(custody, leases, new FakeAuthority()));
    }

    [Fact]
    public void FreshReleaseProofFailureStopsBeforeNativeAction()
    {
        var authority = new FakeAuthority { ReleaseAdmitted = false };
        var model = NewModel(authority);
        var operationId = Guid.NewGuid();

        Assert.Null(model.TryClaimRelease(FixtureB1ReleaseStep.Slot1, operationId));
        Assert.False(model.CanClaimNextRelease);
        Assert.Equal(1, authority.ReleaseChecks);
        var operation = Assert.Single(model.Snapshot().Operations);
        Assert.Equal(operationId, operation.OperationId);
        Assert.True(operation.NativeReturned);
        Assert.True(operation.CallbackTerminal);
        Assert.False(operation.NativeStarted);
        Assert.Null(operation.NativeStartOrdinal);
        Assert.False(operation.Succeeded);
        Assert.False(model.CanReportPass);
    }

    [Fact]
    public void FailedCustodyAcknowledgmentBlocksOriginalNativeWork()
    {
        var authority = new FakeAuthority { CustodyAcknowledged = false };
        var model = NewModel(authority);

        Assert.False(model.RegisterOriginalOperation(
            Guid.NewGuid(), FixtureB1OperationOwner.Wrapper));
        Assert.Null(model.TryClaimRelease(
            FixtureB1ReleaseStep.Slot1, Guid.NewGuid()));
        Assert.False(model.CanReportPass);
    }

    [Fact]
    public void AbortOfAnotherOriginalOperationRevokesClaimedReleaseStart()
    {
        var model = NewModel();
        var originalId = Guid.NewGuid();
        Assert.True(model.RegisterOriginalOperation(
            originalId, FixtureB1OperationOwner.Wrapper));
        var claim = model.TryClaimRelease(
            FixtureB1ReleaseStep.Slot1, Guid.NewGuid());
        Assert.NotNull(claim);

        Assert.True(model.RequestAbort(originalId));
        Assert.True(model.CancelBeforeNativeStart(originalId));
        Assert.False(model.MarkReleaseNativeStarted(claim!));
        Assert.False(model.CanReportPass);
    }

    [Fact]
    public void ThrowingReleaseObserverAlsoFailsClosed()
    {
        var authority = new FakeAuthority { ThrowOnRelease = true };
        var model = NewModel(authority);

        Assert.Null(model.TryClaimRelease(FixtureB1ReleaseStep.Slot1, Guid.NewGuid()));
        Assert.False(model.CanClaimNextRelease);
        Assert.False(model.NativeReleaseSynchronizationEstablished);
    }

    [Fact]
    public void StopAfterClaimKeepsLateNativeReturnOwnedUntilCallbackDrains()
    {
        var model = NewModel();
        var operationId = Guid.NewGuid();
        var claim = model.TryClaimRelease(FixtureB1ReleaseStep.Slot1, operationId);
        Assert.NotNull(claim);
        Assert.True(model.MarkReleaseNativeStarted(claim!));

        model.StopAdmission();
        Assert.True(model.RequestAbort(operationId));
        model.StartCleanup();
        Assert.False(model.TryConfirmDrain(Guid.NewGuid()));
        Assert.True(model.RecordNativeReturn(operationId, succeeded: true));
        Assert.False(model.TryConfirmDrain(Guid.NewGuid()));
        Assert.True(model.RecordCallbackTerminal(operationId));
        Assert.True(model.TryConfirmDrain(Guid.NewGuid()));

        Assert.False(model.CanClaimNextRelease);
        Assert.False(model.CanReportPass);
        var operation = Assert.Single(model.Snapshot().Operations);
        Assert.True(operation.AbortRequested);
        Assert.True(operation.NativeReturned);
        Assert.True(operation.CallbackTerminal);
    }

    [Fact]
    public void ForgedReleaseGenerationCannotStartNativeAction()
    {
        var model = NewModel();
        var claim = model.TryClaimRelease(
            FixtureB1ReleaseStep.Slot1, Guid.NewGuid());
        Assert.NotNull(claim);

        Assert.False(model.MarkReleaseNativeStarted(
            claim! with { CustodyGeneration = claim.CustodyGeneration + 1 }));
        Assert.True(model.MarkReleaseNativeStarted(claim!));
    }

    [Fact]
    public void RegisteredOriginalOperationMustReturnAndDrainAfterAbort()
    {
        var model = NewModel();
        var operationId = Guid.NewGuid();
        Assert.True(model.RegisterOriginalOperation(
            operationId, FixtureB1OperationOwner.Wrapper));
        Assert.True(model.MarkNativeStarted(operationId));
        Assert.False(model.RegisterOriginalOperation(
            operationId, FixtureB1OperationOwner.Wrapper));
        model.StartCleanup();
        Assert.False(model.RegisterOriginalOperation(
            Guid.NewGuid(), FixtureB1OperationOwner.Launcher));
        Assert.True(model.RequestAbort(operationId));
        Assert.False(model.TryConfirmDrain(Guid.NewGuid()));
        Assert.True(model.RecordCallbackTerminal(operationId));
        Assert.False(model.TryConfirmDrain(Guid.NewGuid()));
        Assert.True(model.RecordNativeReturn(operationId, succeeded: true));
        var barrier = Guid.NewGuid();
        Assert.True(model.TryConfirmDrain(barrier));
        Assert.Equal(barrier, model.Snapshot().DrainBarrierId);
        Assert.False(model.TryConfirmDrain(Guid.NewGuid()));
    }

    [Fact]
    public void DeadlineExpiryIsStickyEvenAfterLateOriginalCompletionAndAllCloses()
    {
        var model = NewModel();
        var operationId = Guid.NewGuid();
        Assert.True(model.RegisterOriginalOperation(
            operationId, FixtureB1OperationOwner.Wrapper));
        Assert.True(model.MarkNativeStarted(operationId));
        model.ExpireCleanupDeadline();
        Assert.True(model.RecordNativeReturn(operationId, succeeded: true));
        Assert.True(model.RecordCallbackTerminal(operationId));
        Assert.True(model.TryConfirmDrain(Guid.NewGuid()));
        RecordAllTerminals(model);
        CloseAll(model);

        var report = model.Snapshot();
        Assert.True(report.AllLeasesClosed);
        Assert.False(report.AdapterRetainsOwnership);
        Assert.True(report.DeadlineExpired);
        Assert.False(report.CleanupConfirmed);
        Assert.False(model.CanReportPass);
    }

    [Fact]
    public void RunnerAndLauncherClosesRequireTheirOwnIndependentTerminalPairs()
    {
        var model = NewModel();
        model.StartCleanup();
        var barrier = Guid.NewGuid();
        Assert.True(model.TryConfirmDrain(barrier));
        Assert.Null(model.TryBeginClose(FixtureB1CloseKind.RunnerProcess, Guid.NewGuid()));
        Assert.Null(model.TryBeginClose(FixtureB1CloseKind.LauncherProcess, Guid.NewGuid()));

        Assert.True(model.TryRecordTerminal(
            FixtureB1TerminalKind.RunnerSignaled, Guid.NewGuid()));
        Assert.Null(model.TryBeginClose(FixtureB1CloseKind.RunnerProcess, Guid.NewGuid()));
        Assert.True(model.TryRecordTerminal(
            FixtureB1TerminalKind.RunnerJobEmpty, Guid.NewGuid()));
        var runnerClaim = model.TryBeginClose(
            FixtureB1CloseKind.RunnerProcess, Guid.NewGuid());
        Assert.NotNull(runnerClaim);
        Assert.Equal(barrier, runnerClaim!.DrainBarrierId);
        Assert.Equal(2, runnerClaim.RequiredTerminalObservations.Length);
        Assert.Null(model.TryBeginClose(FixtureB1CloseKind.LauncherProcess, Guid.NewGuid()));

        Assert.True(model.TryRecordTerminal(
            FixtureB1TerminalKind.LauncherSignaled, Guid.NewGuid()));
        Assert.Null(model.TryBeginClose(FixtureB1CloseKind.LauncherProcess, Guid.NewGuid()));
        Assert.True(model.TryRecordTerminal(
            FixtureB1TerminalKind.SafetyJobEmpty, Guid.NewGuid()));
        Assert.NotNull(model.TryBeginClose(
            FixtureB1CloseKind.LauncherProcess, Guid.NewGuid()));
    }

    [Fact]
    public void UnconfirmedCloseKeepsExclusiveClaimAndCannotCloseReusedNumber()
    {
        var authority = new FakeAuthority { CloseConfirmed = false };
        var model = NewModel(authority);
        model.StartCleanup();
        Assert.True(model.TryConfirmDrain(Guid.NewGuid()));
        RecordAllTerminals(model);
        var claim = model.TryBeginClose(FixtureB1CloseKind.RunnerProcess, Guid.NewGuid());
        Assert.NotNull(claim);

        Assert.False(model.TryConfirmClose(claim!, Guid.NewGuid()));
        Assert.Null(model.TryBeginClose(FixtureB1CloseKind.RunnerProcess, Guid.NewGuid()));
        var receipt = Assert.Single(model.Snapshot().Closes,
            close => close.Lease.Kind == FixtureB1CloseKind.RunnerProcess);
        Assert.Equal(FixtureB1CloseState.ClosePending, receipt.State);
        Assert.True(model.Snapshot().AdapterRetainsOwnership);

        authority.CloseConfirmed = true;
        Assert.True(model.TryConfirmClose(claim!, Guid.NewGuid()));
        Assert.False(model.TryConfirmClose(claim!, Guid.NewGuid()));
        Assert.Null(model.TryBeginClose(FixtureB1CloseKind.RunnerProcess, Guid.NewGuid()));
    }

    [Fact]
    public void ForgedCloseBarrierCannotConfirmTheOwnedAttempt()
    {
        var model = NewModel();
        model.StartCleanup();
        Assert.True(model.TryConfirmDrain(Guid.NewGuid()));
        RecordAllTerminals(model);
        var claim = model.TryBeginClose(FixtureB1CloseKind.RunnerJob, Guid.NewGuid());
        Assert.NotNull(claim);
        var forged = claim! with { DrainBarrierId = Guid.NewGuid() };

        Assert.False(model.TryConfirmClose(forged, Guid.NewGuid()));
        Assert.True(model.TryConfirmClose(claim!, Guid.NewGuid()));
    }

    [Fact]
    public void WrongTerminalObservationKeepsFinalReportAmbiguous()
    {
        var authority = new FakeAuthority { TerminalObserved = false };
        var model = NewModel(authority);
        model.StartCleanup();
        Assert.True(model.TryConfirmDrain(Guid.NewGuid()));
        Assert.False(model.TryRecordTerminal(
            FixtureB1TerminalKind.RunnerSignaled, Guid.NewGuid()));
        authority.TerminalObserved = true;
        RecordAllTerminals(model);
        CloseAll(model);

        Assert.True(model.Snapshot().AllLeasesClosed);
        Assert.True(model.Snapshot().Ambiguous);
        Assert.False(model.Snapshot().CleanupConfirmed);
    }

    [Fact]
    public void ConcurrentCloseClaimsAllowExactlyOnePhysicalCloser()
    {
        var model = NewModel();
        model.StartCleanup();
        Assert.True(model.TryConfirmDrain(Guid.NewGuid()));
        RecordAllTerminals(model);
        var count = 0;

        Parallel.For(0, 16, _ =>
        {
            if (model.TryBeginClose(
                    FixtureB1CloseKind.SafetyJob, Guid.NewGuid()) is not null)
                Interlocked.Increment(ref count);
        });

        Assert.Equal(1, count);
        Assert.Equal(FixtureB1CloseState.ClosePending,
            Assert.Single(model.Snapshot().Closes,
                close => close.Lease.Kind == FixtureB1CloseKind.SafetyJob).State);
    }

    [Fact]
    public void StopBeforeNativeStartDeniesStaleReleaseClaimAndDrainsCancellation()
    {
        var model = NewModel();
        var operationId = Guid.NewGuid();
        var claim = model.TryClaimRelease(FixtureB1ReleaseStep.Slot1, operationId);
        Assert.NotNull(claim);
        model.StopAdmission();

        Assert.False(model.MarkReleaseNativeStarted(claim!));
        Assert.True(model.RequestAbort(operationId));
        Assert.True(model.CancelBeforeNativeStart(operationId));
        model.StartCleanup();
        Assert.True(model.TryConfirmDrain(Guid.NewGuid()));
        var operation = Assert.Single(model.Snapshot().Operations);
        Assert.False(operation.NativeStarted);
        Assert.True(operation.AbortRequested);
        Assert.True(operation.NativeReturned);
        Assert.True(operation.CallbackTerminal);
        Assert.True(operation.RegisteredOrdinal < operation.AbortRequestOrdinal!.Value);
        Assert.True(operation.AbortRequestOrdinal!.Value <
            operation.NativeReturnOrdinal!.Value);
        Assert.False(model.CanReportPass);
    }

    private static FixtureB1ReleaseCleanupModel NewModel(FakeAuthority? authority = null)
    {
        var custody = new FixtureB1ReleaseCustody(
            Guid.NewGuid(), Guid.NewGuid(), Wrapper, Launcher, Runner,
            Guid.NewGuid(), Guid.NewGuid(), 1);
        var leases = Enum.GetValues<FixtureB1CloseKind>()
            .Select(kind => new FixtureB1RetainedLease(
                kind, FixtureB1OperationOwner.Wrapper, Guid.NewGuid(), 1));
        return new(custody, leases, authority ?? new());
    }

    private static void CompleteStep(
        FixtureB1ReleaseCleanupModel model,
        FixtureB1ReleaseStep step)
    {
        var id = Guid.NewGuid();
        var claim = model.TryClaimRelease(step, id);
        Assert.NotNull(claim);
        Assert.Equal(step, claim!.Step);
        Assert.True(model.MarkReleaseNativeStarted(claim!));
        Assert.True(model.RecordNativeReturn(id, succeeded: true));
        Assert.True(model.RecordCallbackTerminal(id));
    }

    private static void RecordAllTerminals(FixtureB1ReleaseCleanupModel model)
    {
        foreach (var kind in Enum.GetValues<FixtureB1TerminalKind>())
            Assert.True(model.TryRecordTerminal(kind, Guid.NewGuid()));
    }

    private static void CloseAll(FixtureB1ReleaseCleanupModel model)
    {
        foreach (var kind in Enum.GetValues<FixtureB1CloseKind>())
        {
            var claim = model.TryBeginClose(kind, Guid.NewGuid());
            Assert.NotNull(claim);
            Assert.True(model.TryConfirmClose(claim!, Guid.NewGuid()));
        }
    }

    private sealed class FakeAuthority : IFixtureB1ReleaseCleanupAuthority
    {
        internal bool CustodyAcknowledged { get; set; } = true;
        internal bool ReleaseAdmitted { get; set; } = true;
        internal bool ThrowOnRelease { get; set; }
        internal bool TerminalObserved { get; set; } = true;
        internal bool CloseConfirmed { get; set; } = true;
        internal int ReleaseChecks { get; private set; }

        public bool IsCustodyAcknowledged(FixtureB1ReleaseCustody custody) =>
            CustodyAcknowledged;

        public bool IsFreshReleaseAdmitted(
            FixtureB1ReleaseCustody custody,
            FixtureB1ReleaseStep step)
        {
            ReleaseChecks++;
            if (ThrowOnRelease)
                throw new InvalidOperationException("Fake release observer failed.");
            return ReleaseAdmitted;
        }

        public bool IsExactTerminalObserved(
            FixtureB1ReleaseCustody custody,
            FixtureB1TerminalKind kind,
            Guid observationId) => TerminalObserved;

        public bool IsExactCloseConfirmed(
            FixtureB1CloseClaim claim,
            Guid confirmationId) => CloseConfirmed;
    }
}
