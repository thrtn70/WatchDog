using WatchDog.CaptureProtocol;

namespace WatchDog.CaptureIsolation;

/// <summary>
/// Managed supervisor state machine over injected process/job/transport boundaries. This class
/// makes no claim that the injected job is an OS containment boundary and has no production DI.
/// </summary>
public sealed class ManagedCaptureSupervisor : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IHelperProcessBoundary _processBoundary;
    private readonly IHelperJobBoundary _jobBoundary;
    private readonly IHelperTransportBoundary _transportBoundary;
    private readonly SupervisorTestProfile _profile;
    private readonly CaptureIsolationAdmissionGate _admissionGate;

    private HelperLaunchRequest? _launch;
    private RetainedProcessIdentity? _process;
    private RetainedJobIdentity? _job;
    private IHelperReaper? _reaper;
    private CaptureCleanupContinuation? _continuation;
    private CaptureIsolationPreflightAdmission? _admission;
    private bool _fenceFailed;
    private bool _processTerminationFailed;
    private bool _jobTerminationFailed;
    private string? _reaperFailureCode;
    private bool _disposed;

    public ManagedCaptureSupervisor(
        IHelperProcessBoundary processBoundary,
        IHelperJobBoundary jobBoundary,
        IHelperTransportBoundary transportBoundary,
        SupervisorTestProfile profile)
    {
        _processBoundary = processBoundary ?? throw new ArgumentNullException(nameof(processBoundary));
        _jobBoundary = jobBoundary ?? throw new ArgumentNullException(nameof(jobBoundary));
        _transportBoundary = transportBoundary ?? throw new ArgumentNullException(nameof(transportBoundary));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _admissionGate = new(profile.CapabilityLimits);
    }

    public SupervisorState State { get; private set; } = SupervisorState.Absent;
    public Guid? OwnedReaperIdentity => _reaper?.Identity;

    public async Task<SupervisorStartResult> StartAsync(
        HelperLaunchRequest request,
        CaptureIsolationPreflightEvidence? preflightEvidence,
        CaptureCleanupContinuation failedStartContinuation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(failedStartContinuation);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            var preflight = _admissionGate.EvaluatePreflight(request, preflightEvidence, State);
            if (preflight.Outcome != CaptureIsolationAdmissionOutcome.Admitted)
                return new(SupervisorStartOutcome.Rejected, State, preflight.FailureCode);
            request.Validate();
            if (request.Correlation != failedStartContinuation.Correlation)
            {
                return new(
                    SupervisorStartOutcome.Rejected,
                    State,
                    CaptureIsolationAdmissionGate.FailureCode(
                        CaptureIsolationAdmissionFailure.LaunchMismatch));
            }
            var budgets = preflight.Admission!.Policy.Budgets!;
            if (budgets.GracefulStopBudget != _profile.GracefulStopBudget ||
                budgets.ReapObservationBudget != _profile.ReapObservationBudget)
            {
                return new(
                    SupervisorStartOutcome.Rejected,
                    State,
                    CaptureIsolationAdmissionGate.FailureCode(
                        CaptureIsolationAdmissionFailure.BudgetMismatch));
            }

            await ResetReaperAsync().ConfigureAwait(false);
            _launch = request;
            _admission = preflight.Admission;
            _continuation = null;
            _process = null;
            _job = null;
            _fenceFailed = false;
            _processTerminationFailed = false;
            _jobTerminationFailed = false;
            _reaperFailureCode = null;
            State = SupervisorState.Launching;
            var assignmentAdmissionConfirmed = false;
            var bootstrapReached = false;

            try
            {
                using var launchTimeout = new CancellationTokenSource(budgets.LaunchBudget);
                _job = await _jobBoundary.CreateAsync(
                    request.Correlation.HelperInstanceId,
                    launchTimeout.Token).ConfigureAwait(false);
                _job.Validate();
                var launchResult = await _processBoundary.LaunchSuspendedInJobAsync(
                    request,
                    _job,
                    launchTimeout.Token).ConfigureAwait(false);
                // Retain a created process before validating any neutral boundary claim. A
                // malformed status, job, helper, or failure must still route this exact identity
                // through uncancellable termination and reaping.
                _process = launchResult?.Process;
                ValidateLaunchOwnership(launchResult);
                var retainedProcess = _process!;
                using var assignmentTimeout = new CancellationTokenSource(budgets.AssignmentBudget);
                var containment = await _jobBoundary.ObserveAssignmentAsync(
                    _job,
                    retainedProcess,
                    request.Correlation,
                    assignmentTimeout.Token).ConfigureAwait(false);
                var containmentDecision = _admissionGate.EvaluateContainment(
                    preflight.Admission,
                    retainedProcess,
                    _job,
                    containment);
                if (containmentDecision.Outcome != CaptureIsolationAdmissionOutcome.Admitted)
                    throw new HelperBoundaryException(containmentDecision.FailureCode!);
                await _processBoundary.ResumeAsync(retainedProcess, assignmentTimeout.Token)
                    .ConfigureAwait(false);
                State = SupervisorState.Authenticating;
                using var bootstrapTimeout = new CancellationTokenSource(budgets.BootstrapBudget);
                bootstrapReached = true;
                var bootstrap = await _transportBoundary.AuthenticateAsync(
                    retainedProcess,
                    request,
                    preflight.Admission.Policy.Limits!,
                    bootstrapTimeout.Token).ConfigureAwait(false);
                var assignment = _admissionGate.EvaluateAssignment(
                    containmentDecision.Admission!,
                    bootstrap);
                if (assignment.Outcome != CaptureIsolationAdmissionOutcome.Admitted)
                    throw new HelperBoundaryException(assignment.FailureCode!);
                assignmentAdmissionConfirmed = true;

                State = SupervisorState.Assigned;
                return new(SupervisorStartOutcome.Assigned, State);
            }
            catch (Exception exception)
            {
                var failureCode = exception is HelperBoundaryException boundary
                    ? boundary.FailureCode
                    : exception is OperationCanceledException
                        ? "helper.start.boundary-canceled"
                        : "helper.start.failed";
                if (_job is null)
                {
                    // CreateAsync guarantees a throwing call leaves no unreturned job alive.
                    State = SupervisorState.Absent;
                    return new(SupervisorStartOutcome.Rejected, State, failureCode);
                }

                if (bootstrapReached && !assignmentAdmissionConfirmed && _process is not null)
                {
                    State = SupervisorState.Fenced;
                    try
                    {
                        using var fenceTimeout = new CancellationTokenSource(budgets.FenceBudget);
                        await _transportBoundary.FenceAsync(_process, fenceTimeout.Token)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        _fenceFailed = true;
                        // Failed admission cannot trust the transport. Exact termination/reaping
                        // below remains the ownership boundary even when fencing is unavailable.
                    }
                }

                var cleanup = await BeginCleanupAsync(
                    failedStartContinuation,
                    terminate: true,
                    terminateProcess: _process is not null &&
                        !assignmentAdmissionConfirmed).ConfigureAwait(false);
                return cleanup.Disposition == SupervisorCleanupDisposition.Completed
                    ? new(
                        SupervisorStartOutcome.FailedAndReaped,
                        State,
                        failureCode,
                        CleanupFailureCode())
                    : new(
                        SupervisorStartOutcome.CleanupPending,
                        State,
                        failureCode,
                        cleanup.FailureCode);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisorCleanupResult> StopAsync(
        CaptureCleanupContinuation continuation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State != SupervisorState.Assigned || _process is null || _job is null || _launch is null)
                return new(SupervisorCleanupDisposition.Rejected, State, null, "helper.stop.invalid-state");
            _launch.Correlation.EnsureExact(continuation.Correlation);
            var budgets = _admission!.Policy.Budgets!;

            State = SupervisorState.Fenced;
            try
            {
                using var fenceTimeout = new CancellationTokenSource(budgets.FenceBudget);
                await _transportBoundary.FenceAsync(_process, fenceTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                _fenceFailed = true;
                throw;
            }
            State = SupervisorState.Draining;
            var stop = await _transportBoundary.StopAsync(
                _process,
                budgets.GracefulStopBudget,
                CancellationToken.None).ConfigureAwait(false);
            var terminate = stop != HelperStopOutcome.Completed;
            return await BeginCleanupAsync(continuation, terminate).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var failureCode = exception is HelperBoundaryException boundary
                ? boundary.FailureCode
                : "helper.stop.failed";
            if (_process is null || _job is null)
                return new(SupervisorCleanupDisposition.Rejected, State, null, failureCode);
            var cleanup = await BeginCleanupAsync(continuation, terminate: true).ConfigureAwait(false);
            return cleanup.FailureCode is null
                ? cleanup with { FailureCode = failureCode }
                : cleanup;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SupervisorCleanupResult> TryCompleteCleanupAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (State != SupervisorState.CleanupPending || _continuation is null || _job is null)
                return new(SupervisorCleanupDisposition.Rejected, State, null, "helper.cleanup.invalid-state");
            return await ContinueReapingAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
                return;
            if (State is not (SupervisorState.Absent or SupervisorState.Reaped))
            {
                throw new InvalidOperationException(
                    "The supervisor still owns helper cleanup and cannot be disposed.");
            }
            await ResetReaperAsync().ConfigureAwait(false);
            _disposed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<SupervisorCleanupResult> BeginCleanupAsync(
        CaptureCleanupContinuation continuation,
        bool terminate,
        bool terminateProcess = false)
    {
        if (_job is null)
            return new(SupervisorCleanupDisposition.Rejected, State, null, "helper.cleanup.missing-ownership");
        _launch!.Correlation.EnsureExact(continuation.Correlation);
        _continuation = continuation;

        if (terminate)
        {
            State = SupervisorState.Terminating;
            if (terminateProcess && _process is not null)
            {
                try
                {
                    await _processBoundary.TerminateAsync(_process, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    _processTerminationFailed = true;
                    // Atomic launch rejection still retains an exact suspended process. Preserve
                    // its identity and continue both termination requests into exact reaping.
                }
            }
            try
            {
                await _jobBoundary.TerminateAsync(_job, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                _jobTerminationFailed = true;
                // A termination request is not ownership evidence. Continue into the retained
                // reaper and remain fail-visible if exact exit cannot be confirmed.
            }
        }

        return await ContinueReapingAsync().ConfigureAwait(false);
    }

    private async Task<SupervisorCleanupResult> ContinueReapingAsync()
    {
        try
        {
            if (_reaper is null)
            {
                var candidate = _jobBoundary.CreateReaper(_job!, _process);
                // Retain ownership before validating the public diagnostic identity. A malformed
                // identity must not make an otherwise owning reaper unreachable.
                _reaper = candidate;
                if (candidate.Identity == Guid.Empty)
                    throw new HelperBoundaryException("helper.reaper.invalid-identity");
            }
        }
        catch (Exception exception)
        {
            _reaperFailureCode ??= ReaperFailureCode(exception);
            State = SupervisorState.CleanupPending;
            return new(
                SupervisorCleanupDisposition.CleanupPending,
                State,
                null,
                CleanupFailureCode());
        }

        HelperReapObservation observation;
        try
        {
            observation = await _reaper.ObserveAsync(
                _admission!.Policy.Budgets!.ReapObservationBudget,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _reaperFailureCode ??= ReaperFailureCode(exception);
            State = SupervisorState.CleanupPending;
            return new(
                SupervisorCleanupDisposition.CleanupPending,
                State,
                null,
                CleanupFailureCode());
        }

        if (!IsExactTerminalObservation(observation))
        {
            State = SupervisorState.CleanupPending;
            return new(
                SupervisorCleanupDisposition.CleanupPending,
                State,
                null,
                CleanupFailureCode());
        }

        State = SupervisorState.Reaped;
        var completionCorrelation = _launch!.Correlation;
        var hasExactCompletionCorrelation =
            IsolationValueValidation.IsCorrelationValid(completionCorrelation) &&
            _process is not null &&
            _process.OpaqueHandleIdentity != Guid.Empty &&
            _process.HelperInstanceId == completionCorrelation.HelperInstanceId &&
            _job is not null &&
            _job.OpaqueHandleIdentity != Guid.Empty &&
            _job.HelperInstanceId == completionCorrelation.HelperInstanceId &&
            _continuation is not null &&
            _continuation.Correlation == completionCorrelation;
        var completion = !hasExactCompletionCorrelation
            ? null
            : new CaptureCleanupCompletion(
                _launch!.Correlation,
                _process!,
                _job!,
                _continuation!);
        return new(SupervisorCleanupDisposition.Completed, State, completion);
    }

    private bool IsExactTerminalObservation(HelperReapObservation observation)
    {
        var processMatches = _process is null
            ? observation.Process is null && observation.ProcessExited
            : observation.Process == _process && observation.ProcessExited;
        return processMatches && observation.JobEmpty && observation.Job == _job;
    }

    private void ValidateLaunchOwnership(HelperLaunchOwnershipResult? result)
    {
        if (result is null || !Enum.IsDefined(result.Status) || result.Job != _job)
            throw new HelperBoundaryException("helper.launch.ownership-invalid");

        var helper = _launch!.Correlation.HelperInstanceId;
        if (result.Job.HelperInstanceId != helper)
            throw new HelperBoundaryException("helper.launch.identity-mismatch");

        var hasBoundedFailure = IsBoundedFailureCode(result.FailureCode);
        switch (result.Status)
        {
            case HelperLaunchOwnershipStatus.NotCreated
                when result.Process is null && hasBoundedFailure:
                throw new HelperBoundaryException(result.FailureCode!);
            case HelperLaunchOwnershipStatus.CreatedSuspended
                when result.Process is not null && result.FailureCode is null:
                if (result.Process.OpaqueHandleIdentity == Guid.Empty ||
                    result.Process.HelperInstanceId == Guid.Empty)
                {
                    throw new HelperBoundaryException("helper.launch.ownership-invalid");
                }
                if (result.Process.HelperInstanceId != helper)
                    throw new HelperBoundaryException("helper.launch.identity-mismatch");
                return;
            case HelperLaunchOwnershipStatus.CreatedRejected
                when result.Process is not null && hasBoundedFailure:
                if (result.Process.OpaqueHandleIdentity == Guid.Empty ||
                    result.Process.HelperInstanceId == Guid.Empty)
                {
                    throw new HelperBoundaryException("helper.launch.ownership-invalid");
                }
                if (result.Process.HelperInstanceId != helper)
                    throw new HelperBoundaryException("helper.launch.identity-mismatch");
                throw new HelperBoundaryException(result.FailureCode!);
            default:
                throw new HelperBoundaryException("helper.launch.ownership-invalid");
        }
    }

    private static bool IsBoundedFailureCode(string? code) =>
        !string.IsNullOrWhiteSpace(code) &&
        code.Length <= 128 &&
        code.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');

    private async ValueTask ResetReaperAsync()
    {
        if (_reaper is null)
            return;
        await _reaper.DisposeAsync().ConfigureAwait(false);
        _reaper = null;
    }

    private static string ReaperFailureCode(Exception exception) => exception switch
    {
        HelperBoundaryException boundary => boundary.FailureCode,
        OperationCanceledException => "helper.reaper.boundary-canceled",
        _ => "helper.reaper.failed",
    };

    private string? CleanupFailureCode()
    {
        var terminationFailureCode = (_processTerminationFailed, _jobTerminationFailed) switch
        {
            (true, true) => "helper.cleanup.process-and-job-termination-failed",
            (true, false) => "helper.cleanup.process-termination-failed",
            (false, true) => "helper.cleanup.job-termination-failed",
            _ => null,
        };
        if (_reaperFailureCode is not null)
        {
            return terminationFailureCode is null && !_fenceFailed
                ? _reaperFailureCode
                : "helper.cleanup.multiple-failures";
        }
        if (!_fenceFailed)
            return terminationFailureCode;
        return terminationFailureCode is null
            ? "helper.cleanup.fence-failed"
            : "helper.cleanup.fence-and-termination-failed";
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
