using WatchDog.CaptureIsolation;
using WatchDog.CaptureProtocol;

namespace WatchDog.Core.Tests.Isolation;

public sealed class ManagedSupervisorTests
{
    [Fact]
    public async Task SuccessfulStartAndExactReapCompletesStoredContinuation()
    {
        var boundary = new FakeBoundary();
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();
        var continuation = Continuation(correlation, CleanupOrigin.ExplicitStop);

        var started = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));
        var stopped = await supervisor.StopAsync(continuation);

        Assert.Equal(SupervisorStartOutcome.Assigned, started.Outcome);
        Assert.Equal(SupervisorCleanupDisposition.Completed, stopped.Disposition);
        Assert.Equal(SupervisorState.Reaped, supervisor.State);
        Assert.Equal(continuation, stopped.Completion?.Continuation);
        Assert.Equal(boundary.Process, stopped.Completion?.Process);
        Assert.Equal(boundary.Job, stopped.Completion?.Job);
        Assert.Equal(
            [
                "job.create",
                "process.launch-in-job",
                "job.observe-membership",
                "process.resume",
                "transport.authenticate",
            ],
            boundary.Calls.Take(5));
    }

    [Theory]
    [InlineData(HelperStopOutcome.TimedOut)]
    [InlineData(HelperStopOutcome.BrokenTransport)]
    public async Task StopFailureTerminatesAndRemainsPendingUntilExactReap(HelperStopOutcome stopOutcome)
    {
        var boundary = new FakeBoundary
        {
            StopOutcome = stopOutcome,
            ReapObservations = new Queue<HelperReapObservation>(
            [
                new(null, null, false, false),
                new(Process(), Job(), true, true),
            ]),
        };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();
        _ = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        var pending = await supervisor.StopAsync(Continuation(correlation, CleanupOrigin.LivenessSafety));

        Assert.Equal(SupervisorCleanupDisposition.CleanupPending, pending.Disposition);
        Assert.Equal(SupervisorState.CleanupPending, supervisor.State);
        Assert.Equal(1, boundary.JobTerminateCalls);
        Assert.Equal(0, boundary.ProcessTerminateCalls);
        Assert.NotNull(supervisor.OwnedReaperIdentity);
        var completed = await supervisor.TryCompleteCleanupAsync();
        Assert.Equal(SupervisorCleanupDisposition.Completed, completed.Disposition);
    }

    [Fact]
    public async Task ForeignReapEvidenceCannotReleaseOwnership()
    {
        var boundary = new FakeBoundary
        {
            ReapObservations = new Queue<HelperReapObservation>(
            [
                new(new RetainedProcessIdentity(Guid.NewGuid(), HelperId), null, true, true),
                new(Process(), Job(), true, true),
            ]),
        };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();
        _ = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        var pending = await supervisor.StopAsync(Continuation(correlation, CleanupOrigin.SourceSafety));
        var completed = await supervisor.TryCompleteCleanupAsync();

        Assert.Equal(SupervisorCleanupDisposition.CleanupPending, pending.Disposition);
        Assert.Equal(SupervisorCleanupDisposition.Completed, completed.Disposition);
    }

    [Theory]
    [InlineData(FakeFailure.Launch)]
    [InlineData(FakeFailure.Assignment)]
    [InlineData(FakeFailure.AuthenticationMismatch)]
    public async Task AdmissionFailuresNeverReportAssigned(FakeFailure failure)
    {
        var observations = failure == FakeFailure.Launch
            ? new Queue<HelperReapObservation>([new(null, Job(), true, true)])
            : new Queue<HelperReapObservation>([new(Process(), Job(), true, true)]);
        var boundary = new FakeBoundary { Failure = failure, ReapObservations = observations };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();

        var result = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        Assert.NotEqual(SupervisorStartOutcome.Assigned, result.Outcome);
        Assert.NotEqual(SupervisorState.Assigned, supervisor.State);
    }

    [Theory]
    [InlineData(CaptureIsolationAdmissionFailure.PolicyMissing)]
    [InlineData(CaptureIsolationAdmissionFailure.PolicyInvalid)]
    [InlineData(CaptureIsolationAdmissionFailure.BudgetMissing)]
    public async Task PreflightRejectionMakesNoBoundaryCalls(
        CaptureIsolationAdmissionFailure expectedFailure)
    {
        var boundary = new FakeBoundary();
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();
        var valid = CaptureIsolationAdmissionFixture.Preflight(correlation);
        var evidence = expectedFailure switch
        {
            CaptureIsolationAdmissionFailure.PolicyMissing =>
                new CaptureIsolationPreflightEvidence(
                    null,
                    valid.LaunchPlan,
                    valid.Package,
                    valid.MediaRoot),
            CaptureIsolationAdmissionFailure.PolicyInvalid =>
                new CaptureIsolationPreflightEvidence(
                    CaptureIsolationAdmissionFixture.Policy(
                        provenance: CaptureIsolationEvidenceProvenance.TrustedPackageBoundary),
                    valid.LaunchPlan,
                    valid.Package,
                    valid.MediaRoot),
            CaptureIsolationAdmissionFailure.BudgetMissing =>
                new CaptureIsolationPreflightEvidence(
                    CaptureIsolationAdmissionFixture.PolicyWithoutBudgets(),
                    valid.LaunchPlan,
                    valid.Package,
                    valid.MediaRoot),
            _ => throw new InvalidOperationException("Unexpected test case."),
        };

        var result = await supervisor.StartAsync(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            evidence,
            Continuation(correlation, CleanupOrigin.FailedStart));

        Assert.Equal(SupervisorStartOutcome.Rejected, result.Outcome);
        Assert.Equal(CaptureIsolationAdmissionGate.FailureCode(expectedFailure), result.FailureCode);
        Assert.Equal(SupervisorState.Absent, supervisor.State);
        Assert.Equal(0, boundary.TotalAdmissionBoundaryCalls);
    }

    [Theory]
    [InlineData(FakeFailure.ContainmentMismatch)]
    [InlineData(FakeFailure.ContainmentTruncated)]
    [InlineData(FakeFailure.ContainmentBreakaway)]
    public async Task ContainmentRejectionStaysSuspendedAndRetainsExactCleanupUntilTerminalEvidence(
        FakeFailure containmentFailure)
    {
        var boundary = new FakeBoundary
        {
            Failure = containmentFailure,
            ReapObservations = new Queue<HelperReapObservation>(
            [
                new(Process(), Job(), false, true),
                new(Process(), Job(), true, true),
            ]),
        };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();

        var pending = await supervisor.StartAsync(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        Assert.Equal(SupervisorStartOutcome.CleanupPending, pending.Outcome);
        Assert.Equal(
            "isolation.assignment.containment-unverified",
            pending.FailureCode);
        Assert.Equal(SupervisorState.CleanupPending, supervisor.State);
        Assert.NotNull(supervisor.OwnedReaperIdentity);
        Assert.Equal(0, boundary.ResumeCalls);
        Assert.Equal(0, boundary.AuthenticationCalls);
        Assert.Equal(0, boundary.FenceCalls);
        Assert.Equal(1, boundary.ProcessTerminateCalls);
        Assert.Equal(boundary.LastLaunchProcess, boundary.LastTerminatedProcess);
        Assert.Equal(1, boundary.JobTerminateCalls);
        Assert.Equal(boundary.Job, boundary.LastTerminatedJob);

        var completed = await supervisor.TryCompleteCleanupAsync();

        Assert.Equal(SupervisorCleanupDisposition.Completed, completed.Disposition);
        Assert.Equal(SupervisorState.Reaped, supervisor.State);
    }

    [Fact]
    public async Task AdmittedBudgetsAndLimitsAreBoundToBoundaryExecution()
    {
        var boundary = new FakeBoundary();
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();
        var evidence = CaptureIsolationAdmissionFixture.Preflight(correlation);

        var started = await supervisor.StartAsync(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            evidence,
            Continuation(correlation, CleanupOrigin.FailedStart));
        var stopped = await supervisor.StopAsync(
            Continuation(correlation, CleanupOrigin.ExplicitStop));

        Assert.Equal(SupervisorStartOutcome.Assigned, started.Outcome);
        Assert.Equal(SupervisorCleanupDisposition.Completed, stopped.Disposition);
        Assert.True(boundary.LastLaunchToken.CanBeCanceled);
        Assert.True(boundary.LastAssignmentToken.CanBeCanceled);
        Assert.True(boundary.LastBootstrapToken.CanBeCanceled);
        Assert.True(boundary.LastFenceToken.CanBeCanceled);
        Assert.Same(evidence.Policy!.Limits, boundary.LastEffectiveLimits);
        Assert.Equal(evidence.Policy.Budgets!.GracefulStopBudget, boundary.LastGracefulStopBudget);
        Assert.Equal(
            evidence.Policy.Budgets.ReapObservationBudget,
            boundary.LastReapObservationBudget);
    }

    [Fact]
    public async Task RuntimeBudgetMismatchRejectsBeforeBoundaryCalls()
    {
        var boundary = new FakeBoundary();
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();
        var valid = CaptureIsolationAdmissionFixture.Preflight(correlation);
        var policy = CaptureIsolationAdmissionFixture.Policy(
            budgets: new CaptureIsolationBudgetSnapshot(
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(6),
                TimeSpan.FromMilliseconds(5)));
        var evidence = new CaptureIsolationPreflightEvidence(
            policy,
            valid.LaunchPlan,
            valid.Package,
            valid.MediaRoot);

        var result = await supervisor.StartAsync(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            evidence,
            Continuation(correlation, CleanupOrigin.FailedStart));

        Assert.Equal(SupervisorStartOutcome.Rejected, result.Outcome);
        Assert.Equal("isolation.preflight.budget-mismatch", result.FailureCode);
        Assert.Equal(0, boundary.TotalAdmissionBoundaryCalls);
    }

    [Fact]
    public async Task CallerCancellationAfterLaunchAdmissionCannotAbandonRejectedAssignmentCleanup()
    {
        using var cancellation = new CancellationTokenSource();
        var boundary = new FakeBoundary
        {
            Failure = FakeFailure.PeerMismatch,
            CallerCancellation = cancellation,
        };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();

        var result = await supervisor.StartAsync(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart),
            cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(SupervisorStartOutcome.FailedAndReaped, result.Outcome);
        Assert.Equal("isolation.assignment.peer-mismatch", result.FailureCode);
        Assert.Equal(SupervisorState.Reaped, supervisor.State);
        Assert.False(boundary.LastCleanupToken.CanBeCanceled);
        Assert.Equal(1, boundary.FenceCalls);
        Assert.Equal(1, boundary.ProcessTerminateCalls);
        Assert.Equal(1, boundary.JobTerminateCalls);
    }

    [Fact]
    public async Task FenceFailureIsBoundedAndCannotPreventExactRejectedAssignmentCleanup()
    {
        var boundary = new FakeBoundary
        {
            Failure = FakeFailure.PeerMismatch,
            ThrowDuringFence = true,
        };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();

        var result = await supervisor.StartAsync(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        Assert.Equal(SupervisorStartOutcome.FailedAndReaped, result.Outcome);
        Assert.Equal("isolation.assignment.peer-mismatch", result.FailureCode);
        Assert.Equal("helper.cleanup.fence-failed", result.CleanupFailureCode);
        Assert.Equal(1, boundary.FenceCalls);
        Assert.Equal(1, boundary.ProcessTerminateCalls);
        Assert.Equal(1, boundary.JobTerminateCalls);
        Assert.Equal(SupervisorState.Reaped, supervisor.State);
    }

    [Theory]
    [InlineData(FakeFailure.Assignment, "helper.atomic-launch.failed")]
    [InlineData(FakeFailure.CancelAssignment, "helper.start.boundary-canceled")]
    public async Task AssignmentFailureTerminatesExactSuspendedProcessAndJob(
        FakeFailure failure,
        string expectedFailureCode)
    {
        var boundary = new FakeBoundary { Failure = failure };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();

        var result = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        Assert.Equal(SupervisorStartOutcome.FailedAndReaped, result.Outcome);
        Assert.Equal(expectedFailureCode, result.FailureCode);
        Assert.Equal(1, boundary.ProcessTerminateCalls);
        Assert.Equal(boundary.Process, boundary.LastTerminatedProcess);
        Assert.Equal(1, boundary.JobTerminateCalls);
        Assert.Equal(boundary.Job, boundary.LastTerminatedJob);
    }

    [Theory]
    [InlineData(FakeFailure.LaunchJobMismatch, "helper.launch.ownership-invalid")]
    [InlineData(FakeFailure.LaunchStatusMismatch, "helper.launch.ownership-invalid")]
    [InlineData(FakeFailure.LaunchHelperMismatch, "helper.launch.identity-mismatch")]
    [InlineData(FakeFailure.LaunchProcessIdentityInvalid, "helper.launch.ownership-invalid")]
    [InlineData(FakeFailure.LaunchFailureMismatch, "helper.launch.ownership-invalid")]
    public async Task AtomicLaunchOwnershipIsStoredBeforeNeutralResultIsRejected(
        FakeFailure failure,
        string expectedFailureCode)
    {
        var boundary = new FakeBoundary
        {
            Failure = failure,
            ReapObservations = new Queue<HelperReapObservation>(
                [new(null, null, false, false)]),
        };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();

        var result = await supervisor.StartAsync(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        Assert.Equal(SupervisorStartOutcome.CleanupPending, result.Outcome);
        Assert.Equal(expectedFailureCode, result.FailureCode);
        Assert.Equal(boundary.LastLaunchProcess, boundary.LastTerminatedProcess);
        Assert.Equal(boundary.Job, boundary.LastTerminatedJob);
        Assert.Equal(0, boundary.AssignmentObservationCalls);
        boundary.ReapObservations.Enqueue(new(
            boundary.LastLaunchProcess,
            boundary.Job,
            true,
            true));
        var completed = await supervisor.TryCompleteCleanupAsync();
        Assert.Equal(SupervisorCleanupDisposition.Completed, completed.Disposition);
        Assert.Equal(
            failure is FakeFailure.LaunchHelperMismatch or FakeFailure.LaunchProcessIdentityInvalid
                ? null
                : boundary.LastLaunchProcess,
            completed.Completion?.Process);
    }

    [Theory]
    [InlineData(FakeTerminationFailure.Process, "helper.cleanup.process-termination-failed")]
    [InlineData(FakeTerminationFailure.Job, "helper.cleanup.job-termination-failed")]
    [InlineData(FakeTerminationFailure.Both, "helper.cleanup.process-and-job-termination-failed")]
    public async Task AssignmentCleanupTerminationFailureRetainsOwnershipUntilExactReap(
        FakeTerminationFailure terminationFailure,
        string expectedCleanupFailureCode)
    {
        var boundary = new FakeBoundary
        {
            Failure = FakeFailure.Assignment,
            TerminationFailure = terminationFailure,
            ReapObservations = new Queue<HelperReapObservation>(
            [
                new(Process(), Job(), false, true),
                new(Process(), Job(), false, true),
                new(Process(), Job(), true, true),
            ]),
        };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();

        var pending = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        Assert.Equal(SupervisorStartOutcome.CleanupPending, pending.Outcome);
        Assert.Equal(expectedCleanupFailureCode, pending.CleanupFailureCode);
        Assert.Equal(SupervisorState.CleanupPending, supervisor.State);
        var ownedReaper = Assert.IsType<Guid>(supervisor.OwnedReaperIdentity);
        Assert.Equal(1, boundary.ProcessTerminateCalls);
        Assert.Equal(boundary.Process, boundary.LastTerminatedProcess);
        Assert.Equal(1, boundary.JobTerminateCalls);
        Assert.Equal(boundary.Job, boundary.LastTerminatedJob);

        var stillPending = await supervisor.TryCompleteCleanupAsync();

        Assert.Equal(SupervisorCleanupDisposition.CleanupPending, stillPending.Disposition);
        Assert.Equal(expectedCleanupFailureCode, stillPending.FailureCode);
        Assert.Equal(SupervisorState.CleanupPending, supervisor.State);
        Assert.Equal(ownedReaper, supervisor.OwnedReaperIdentity);

        var completed = await supervisor.TryCompleteCleanupAsync();

        Assert.Equal(SupervisorCleanupDisposition.Completed, completed.Disposition);
        Assert.Equal(SupervisorState.Reaped, supervisor.State);
    }

    [Fact]
    public async Task AcceptedCleanupIgnoresCallerCancellation()
    {
        var boundary = new FakeBoundary();
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();
        _ = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await supervisor.StopAsync(
            Continuation(correlation, CleanupOrigin.Shutdown),
            cancellation.Token);

        Assert.Equal(SupervisorCleanupDisposition.Completed, result.Disposition);
        Assert.False(boundary.LastCleanupToken.CanBeCanceled);
    }

    [Fact]
    public async Task HelperCrashDuringStopStillEntersOwnedReaping()
    {
        var boundary = new FakeBoundary { ThrowDuringStop = true };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();
        _ = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        var result = await supervisor.StopAsync(Continuation(correlation, CleanupOrigin.SourceSafety));

        Assert.Equal(SupervisorCleanupDisposition.Completed, result.Disposition);
        Assert.Equal("helper.transport.crashed", result.FailureCode);
        Assert.Equal(SupervisorState.Reaped, supervisor.State);
        Assert.NotNull(supervisor.OwnedReaperIdentity);
    }

    [Theory]
    [InlineData(FakeFailure.CancelLaunch)]
    [InlineData(FakeFailure.CancelAssignment)]
    [InlineData(FakeFailure.CancelResume)]
    [InlineData(FakeFailure.CancelAuthentication)]
    public async Task BoundaryCancellationAfterAdmissionRoutesThroughExactCleanup(FakeFailure failure)
    {
        var observations = failure == FakeFailure.CancelLaunch
            ? new Queue<HelperReapObservation>([new(null, Job(), true, true)])
            : new Queue<HelperReapObservation>([new(Process(), Job(), true, true)]);
        var boundary = new FakeBoundary { Failure = failure, ReapObservations = observations };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();

        var result = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        Assert.Equal(SupervisorStartOutcome.FailedAndReaped, result.Outcome);
        Assert.Equal("helper.start.boundary-canceled", result.FailureCode);
        Assert.Equal(SupervisorState.Reaped, supervisor.State);
        Assert.Equal(1, boundary.JobTerminateCalls);
        Assert.Equal(failure == FakeFailure.CancelLaunch ? 0 : 1, boundary.ProcessTerminateCalls);
    }

    [Fact]
    public async Task JobCreationCancellationUsesNoLeakContractAndReturnsAbsent()
    {
        var boundary = new FakeBoundary { Failure = FakeFailure.CancelCreate };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();

        var result = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        Assert.Equal(SupervisorStartOutcome.Rejected, result.Outcome);
        Assert.Equal("helper.start.boundary-canceled", result.FailureCode);
        Assert.Equal(SupervisorState.Absent, supervisor.State);
        Assert.Equal(0, boundary.JobTerminateCalls);
        Assert.Equal(0, boundary.ProcessTerminateCalls);
        Assert.Null(supervisor.OwnedReaperIdentity);
    }

    [Fact]
    public async Task DisposalWhileCleanupPendingRefusesToAbandonOwnedReaper()
    {
        var boundary = new FakeBoundary
        {
            StopOutcome = HelperStopOutcome.TimedOut,
            ReapObservations = new Queue<HelperReapObservation>(
            [
                new(null, null, false, false),
                new(Process(), Job(), true, true),
            ]),
        };
        var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();
        _ = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));
        _ = await supervisor.StopAsync(Continuation(correlation, CleanupOrigin.Shutdown));

        await Assert.ThrowsAsync<InvalidOperationException>(() => supervisor.DisposeAsync().AsTask());
        Assert.Equal(SupervisorState.CleanupPending, supervisor.State);
        Assert.Equal(0, boundary.ReaperDisposeCalls);

        Assert.Equal(
            SupervisorCleanupDisposition.Completed,
            (await supervisor.TryCompleteCleanupAsync()).Disposition);
        await supervisor.DisposeAsync();
        Assert.Equal(1, boundary.ReaperDisposeCalls);
    }

    [Theory]
    [InlineData(ReaperFault.CreateCancellation, "helper.reaper.boundary-canceled")]
    [InlineData(ReaperFault.CreateException, "helper.reaper.failed")]
    [InlineData(ReaperFault.ObserveCancellation, "helper.reaper.boundary-canceled")]
    [InlineData(ReaperFault.ObserveException, "helper.reaper.failed")]
    [InlineData(ReaperFault.InvalidIdentity, "helper.reaper.invalid-identity")]
    public async Task ReaperFailuresRemainOwnedAndRetryToExactCompletion(
        ReaperFault fault,
        string expectedFailureCode)
    {
        var boundary = new FakeBoundary
        {
            Failure = FakeFailure.AuthenticationMismatch,
            ReaperFaultMode = fault,
            ReapObservations = new Queue<HelperReapObservation>(
                [new(Process(), Job(), true, true)]),
        };
        await using var supervisor = CreateSupervisor(boundary);
        var correlation = ProtocolFramingTests.Correlation();

        var start = await supervisor.StartAsync(
            new HelperLaunchRequest(correlation, ProtocolFramingTests.Digest('c')),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            Continuation(correlation, CleanupOrigin.FailedStart));

        Assert.Equal(SupervisorStartOutcome.CleanupPending, start.Outcome);
        Assert.Equal(SupervisorState.CleanupPending, supervisor.State);
        Assert.Equal(expectedFailureCode, start.CleanupFailureCode);
        var completion = await supervisor.TryCompleteCleanupAsync();
        Assert.Equal(SupervisorCleanupDisposition.Completed, completion.Disposition);
        Assert.NotNull(completion.Completion);
        Assert.Equal(SupervisorState.Reaped, supervisor.State);
        Assert.Equal(expectedFailureCode, boundary.LastReaperFailureCode);
        if (fault == ReaperFault.InvalidIdentity)
            Assert.Equal(1, boundary.ReaperCreateCalls);
    }

    [Fact]
    public async Task DisposeIsIdempotentAndConcurrentSafeWhenNoOwnershipRemains()
    {
        var boundary = new FakeBoundary();
        var supervisor = CreateSupervisor(boundary);

        await supervisor.DisposeAsync();
        await supervisor.DisposeAsync();
        await Task.WhenAll(
            Enumerable.Range(0, 8)
                .Select(_ => supervisor.DisposeAsync().AsTask()));
    }

    private static ManagedCaptureSupervisor CreateSupervisor(FakeBoundary boundary) => new(
        boundary,
        boundary,
        boundary,
        new SupervisorTestProfile(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            CaptureIsolationAdmissionFixture.Limits()));

    private static CaptureCleanupContinuation Continuation(
        CaptureCorrelation correlation,
        CleanupOrigin origin) => new(
        correlation,
        origin,
        "capture.cleanup.test",
        CleanupTerminalStatus.Faulted,
        CleanupTerminalReadiness.Closed,
        CleanupTerminalHealth.Failed,
        CleanupContextDisposition.Retain,
        CleanupManualReservationDisposition.Preserve,
        CleanupBarrierDisposition.KeepClosed,
        CleanupBarrierDisposition.Preserve);

    private static readonly Guid HelperId = ProtocolFramingTests.Correlation().HelperInstanceId;

    private static RetainedProcessIdentity Process() => new(
        Guid.Parse("70000000-0000-0000-0000-000000000007"),
        HelperId);

    private static RetainedJobIdentity Job() => new(
        Guid.Parse("80000000-0000-0000-0000-000000000008"),
        HelperId);

    public enum FakeFailure
    {
        None,
        Launch,
        Assignment,
        ContainmentMismatch,
        ContainmentTruncated,
        ContainmentBreakaway,
        PeerMismatch,
        AuthenticationMismatch,
        CancelLaunch,
        CancelCreate,
        CancelAssignment,
        CancelResume,
        CancelAuthentication,
        LaunchJobMismatch,
        LaunchStatusMismatch,
        LaunchHelperMismatch,
        LaunchProcessIdentityInvalid,
        LaunchFailureMismatch,
    }

    public enum ReaperFault
    {
        None,
        CreateCancellation,
        CreateException,
        ObserveCancellation,
        ObserveException,
        InvalidIdentity,
    }

    [Flags]
    public enum FakeTerminationFailure
    {
        None = 0,
        Process = 1,
        Job = 2,
        Both = Process | Job,
    }

    private sealed class FakeBoundary : IHelperProcessBoundary, IHelperJobBoundary, IHelperTransportBoundary
    {
        public FakeFailure Failure { get; init; }
        public HelperStopOutcome StopOutcome { get; init; } = HelperStopOutcome.Completed;
        public bool ThrowDuringStop { get; init; }
        public bool ThrowDuringFence { get; init; }
        public ReaperFault ReaperFaultMode { get; init; }
        public FakeTerminationFailure TerminationFailure { get; init; }
        public CancellationTokenSource? CallerCancellation { get; init; }
        public Queue<HelperReapObservation> ReapObservations { get; init; } = new(
            [new(Process(), Job(), true, true)]);
        public RetainedProcessIdentity Process { get; } = Process();
        public RetainedProcessIdentity? LastLaunchProcess { get; private set; }
        public RetainedJobIdentity Job { get; } = Job();
        public int ProcessTerminateCalls { get; private set; }
        public int JobTerminateCalls { get; private set; }
        public RetainedProcessIdentity? LastTerminatedProcess { get; private set; }
        public RetainedJobIdentity? LastTerminatedJob { get; private set; }
        public int ReaperDisposeCalls { get; private set; }
        public int JobCreateCalls { get; private set; }
        public int ProcessLaunchCalls { get; private set; }
        public int AssignmentObservationCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int AuthenticationCalls { get; private set; }
        public int FenceCalls { get; private set; }
        public int ReaperCreateCalls { get; private set; }
        public int ReaperObserveCalls { get; private set; }
        public CancellationToken LastLaunchToken { get; private set; }
        public CancellationToken LastAssignmentToken { get; private set; }
        public CancellationToken LastBootstrapToken { get; private set; }
        public CancellationToken LastFenceToken { get; private set; }
        public CaptureIsolationLimitSnapshot? LastEffectiveLimits { get; private set; }
        public TimeSpan LastGracefulStopBudget { get; private set; }
        public TimeSpan LastReapObservationBudget { get; private set; }
        public string? LastReaperFailureCode { get; private set; }
        public List<string> Calls { get; } = [];
        private bool _reaperFaultInjected;
        public CancellationToken LastCleanupToken { get; private set; }
        public int TotalAdmissionBoundaryCalls =>
            JobCreateCalls + ProcessLaunchCalls + AssignmentObservationCalls +
            ResumeCalls + AuthenticationCalls + FenceCalls + ProcessTerminateCalls +
            JobTerminateCalls + ReaperCreateCalls + ReaperObserveCalls;

        public ValueTask<HelperLaunchOwnershipResult> LaunchSuspendedInJobAsync(
            HelperLaunchRequest request,
            RetainedJobIdentity job,
            CancellationToken cancellationToken)
        {
            ProcessLaunchCalls++;
            Calls.Add("process.launch-in-job");
            LastLaunchToken = cancellationToken;
            if (Failure == FakeFailure.Launch)
            {
                return ValueTask.FromResult(new HelperLaunchOwnershipResult(
                    HelperLaunchOwnershipStatus.NotCreated,
                    job,
                    null,
                    "helper.launch.failed"));
            }
            if (Failure == FakeFailure.CancelLaunch)
            {
                return ValueTask.FromResult(new HelperLaunchOwnershipResult(
                    HelperLaunchOwnershipStatus.NotCreated,
                    job,
                    null,
                    "helper.start.boundary-canceled"));
            }
            var returnedProcess = Failure == FakeFailure.LaunchHelperMismatch
                ? Process with { HelperInstanceId = Guid.NewGuid() }
                : Failure == FakeFailure.LaunchProcessIdentityInvalid
                    ? Process with { OpaqueHandleIdentity = Guid.Empty }
                    : Process;
            LastLaunchProcess = returnedProcess;
            return ValueTask.FromResult(new HelperLaunchOwnershipResult(
                Failure is FakeFailure.Assignment or FakeFailure.LaunchFailureMismatch
                    ? HelperLaunchOwnershipStatus.CreatedRejected
                    : Failure == FakeFailure.LaunchStatusMismatch
                        ? (HelperLaunchOwnershipStatus)999
                        : HelperLaunchOwnershipStatus.CreatedSuspended,
                Failure == FakeFailure.LaunchJobMismatch
                    ? job with { OpaqueHandleIdentity = Guid.NewGuid() }
                    : job,
                returnedProcess,
                Failure switch
                {
                    FakeFailure.Assignment => "helper.atomic-launch.failed",
                    FakeFailure.LaunchFailureMismatch => @"C:\private\helper.exe",
                    _ => null,
                }));
        }

        public ValueTask ResumeAsync(
            RetainedProcessIdentity process,
            CancellationToken cancellationToken)
        {
            ResumeCalls++;
            Calls.Add("process.resume");
            LastAssignmentToken = cancellationToken;
            if (Failure == FakeFailure.CancelResume)
                throw new OperationCanceledException("Injected boundary cancellation.");
            return ValueTask.CompletedTask;
        }

        ValueTask IHelperProcessBoundary.TerminateAsync(
            RetainedProcessIdentity process,
            CancellationToken cancellationToken)
        {
            LastCleanupToken = cancellationToken;
            ProcessTerminateCalls++;
            LastTerminatedProcess = process;
            if (TerminationFailure.HasFlag(FakeTerminationFailure.Process))
                throw new HelperBoundaryException("helper.process.terminate-failed");
            return ValueTask.CompletedTask;
        }

        public ValueTask<RetainedJobIdentity> CreateAsync(
            Guid helperInstanceId,
            CancellationToken cancellationToken)
        {
            JobCreateCalls++;
            Calls.Add("job.create");
            LastLaunchToken = cancellationToken;
            if (Failure == FakeFailure.CancelCreate)
                throw new OperationCanceledException("Injected boundary cancellation.");
            return ValueTask.FromResult(Job);
        }

        public ValueTask<HelperJobMembershipObservation> ObserveAssignmentAsync(
            RetainedJobIdentity job,
            RetainedProcessIdentity process,
            CaptureCorrelation correlation,
            CancellationToken cancellationToken)
        {
            AssignmentObservationCalls++;
            Calls.Add("job.observe-membership");
            LastAssignmentToken = cancellationToken;
            if (Failure == FakeFailure.CancelAssignment)
                throw new OperationCanceledException("Injected boundary cancellation.");
            CallerCancellation?.Cancel();
            var observedJob = Failure == FakeFailure.ContainmentMismatch
                ? job with { OpaqueHandleIdentity = Guid.NewGuid() }
                : job;
            return ValueTask.FromResult(
                CaptureIsolationAdmissionFixture.Membership(
                    process,
                    observedJob,
                    Failure == FakeFailure.ContainmentTruncated
                        ? HelperJobMembershipStatus.Truncated
                        : HelperJobMembershipStatus.Confirmed,
                    Failure == FakeFailure.ContainmentTruncated ? 2u : 1u,
                    1,
                    Failure == FakeFailure.ContainmentBreakaway
                        ? HelperJobContainmentPolicy.KillOnJobClose |
                            HelperJobContainmentPolicy.BreakawayAllowed
                        : HelperJobContainmentPolicy.KillOnJobClose));
        }

        public ValueTask TerminateAsync(
            RetainedJobIdentity job,
            CancellationToken cancellationToken)
        {
            LastCleanupToken = cancellationToken;
            JobTerminateCalls++;
            LastTerminatedJob = job;
            if (TerminationFailure.HasFlag(FakeTerminationFailure.Job))
                throw new HelperBoundaryException("helper.job.terminate-failed");
            return ValueTask.CompletedTask;
        }

        public IHelperReaper CreateReaper(
            RetainedJobIdentity job,
            RetainedProcessIdentity? process)
        {
            ReaperCreateCalls++;
            if (!_reaperFaultInjected &&
                ReaperFaultMode is ReaperFault.CreateCancellation or ReaperFault.CreateException)
            {
                _reaperFaultInjected = true;
                LastReaperFailureCode = ReaperFaultMode == ReaperFault.CreateCancellation
                    ? "helper.reaper.boundary-canceled"
                    : "helper.reaper.failed";
                if (ReaperFaultMode == ReaperFault.CreateCancellation)
                    throw new OperationCanceledException("Injected reaper cancellation.");
                throw new InvalidOperationException("Injected reaper failure.");
            }
            if (!_reaperFaultInjected && ReaperFaultMode == ReaperFault.InvalidIdentity)
            {
                _reaperFaultInjected = true;
                LastReaperFailureCode = "helper.reaper.invalid-identity";
                return new FakeReaper(this, Guid.Empty);
            }
            return new FakeReaper(this);
        }

        public ValueTask<HelperBootstrapObservation> AuthenticateAsync(
            RetainedProcessIdentity process,
            HelperLaunchRequest request,
            CaptureIsolationLimitSnapshot effectiveLimits,
            CancellationToken cancellationToken)
        {
            AuthenticationCalls++;
            Calls.Add("transport.authenticate");
            LastBootstrapToken = cancellationToken;
            LastEffectiveLimits = effectiveLimits;
            if (Failure == FakeFailure.CancelAuthentication)
                throw new OperationCanceledException("Injected boundary cancellation.");
            if (Failure is FakeFailure.AuthenticationMismatch or FakeFailure.PeerMismatch)
            {
                var peers = CaptureIsolationAdmissionFixture.ObservedPeers(request.Correlation).ToArray();
                var helper = peers[1];
                peers[1] = Failure == FakeFailure.PeerMismatch
                    ? new HelperBootstrapPeerObservation(
                        helper.Role,
                        helper.SelfInstanceId,
                        Guid.NewGuid(),
                        helper.ProtocolVersion,
                        helper.SchemaVersion,
                        helper.Architecture,
                        helper.PackageManifestDigest,
                        helper.PayloadDigest)
                    : new HelperBootstrapPeerObservation(
                        helper.Role,
                        helper.SelfInstanceId,
                        helper.PeerInstanceId,
                        helper.ProtocolVersion,
                        helper.SchemaVersion,
                        helper.Architecture,
                        ProtocolFramingTests.Digest('f'),
                        helper.PayloadDigest);
                return ValueTask.FromResult(
                    CaptureIsolationAdmissionFixture.BootstrapObservation(
                        request.Correlation,
                        process: process,
                        effectiveLimits: effectiveLimits,
                        peers: peers));
            }
            return ValueTask.FromResult(
                CaptureIsolationAdmissionFixture.BootstrapObservation(
                    request.Correlation,
                    process: process,
                    effectiveLimits: effectiveLimits));
        }

        public ValueTask FenceAsync(
            RetainedProcessIdentity process,
            CancellationToken cancellationToken)
        {
            FenceCalls++;
            LastFenceToken = cancellationToken;
            LastCleanupToken = cancellationToken;
            if (ThrowDuringFence)
                throw new HelperBoundaryException("helper.transport.fence-failed");
            return ValueTask.CompletedTask;
        }

        public ValueTask<HelperStopOutcome> StopAsync(
            RetainedProcessIdentity process,
            TimeSpan gracefulBudget,
            CancellationToken cancellationToken)
        {
            LastCleanupToken = cancellationToken;
            LastGracefulStopBudget = gracefulBudget;
            if (ThrowDuringStop)
                throw new HelperBoundaryException("helper.transport.crashed");
            return ValueTask.FromResult(StopOutcome);
        }

        private sealed class FakeReaper : IHelperReaper
        {
            private readonly FakeBoundary _owner;
            private bool _terminalObserved;

            public FakeReaper(FakeBoundary owner, Guid? identity = null)
            {
                _owner = owner;
                Identity = identity ?? Guid.NewGuid();
            }

            public Guid Identity { get; }

            public ValueTask<HelperReapObservation> ObserveAsync(
                TimeSpan waitBudget,
                CancellationToken cancellationToken)
            {
                _owner.ReaperObserveCalls++;
                _owner.LastReapObservationBudget = waitBudget;
                _owner.LastCleanupToken = cancellationToken;
                if (!_owner._reaperFaultInjected &&
                    _owner.ReaperFaultMode is ReaperFault.ObserveCancellation or ReaperFault.ObserveException)
                {
                    _owner._reaperFaultInjected = true;
                    _owner.LastReaperFailureCode = _owner.ReaperFaultMode == ReaperFault.ObserveCancellation
                        ? "helper.reaper.boundary-canceled"
                        : "helper.reaper.failed";
                    if (_owner.ReaperFaultMode == ReaperFault.ObserveCancellation)
                        throw new OperationCanceledException("Injected reaper cancellation.");
                    throw new InvalidOperationException("Injected reaper failure.");
                }
                var observation = _owner.ReapObservations.Count == 0
                    ? new HelperReapObservation(null, null, false, false)
                    : _owner.ReapObservations.Dequeue();
                _terminalObserved = observation.ProcessExited && observation.JobEmpty;
                return ValueTask.FromResult(observation);
            }

            public ValueTask DisposeAsync()
            {
                if (!_terminalObserved)
                    throw new HelperBoundaryException("helper.reaper.not-terminal");
                _owner.ReaperDisposeCalls++;
                return ValueTask.CompletedTask;
            }
        }
    }
}
