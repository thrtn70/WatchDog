namespace WatchDog.CaptureIsolation.Windows;

/// <summary>
/// Internal, unregistered process facade over the shared WindowsJobBoundary ownership registry.
/// </summary>
internal sealed class WindowsProcessBoundary : IHelperProcessBoundary
{
    private readonly WindowsJobBoundary _jobs;
    private readonly IWindowsProcessLaunchApi _api;
    private readonly WindowsLaunchSpecification _specification;

    internal WindowsProcessBoundary(
        WindowsJobBoundary jobs,
        IWindowsProcessLaunchApi api,
        WindowsLaunchSpecification specification)
    {
        _jobs = jobs ?? throw new ArgumentNullException(nameof(jobs));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _specification = specification ?? throw new ArgumentNullException(nameof(specification));
    }

    public ValueTask<HelperLaunchOwnershipResult> LaunchSuspendedInJobAsync(
        HelperLaunchRequest request,
        RetainedJobIdentity job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(job);
        if (request != _specification.Request)
        {
            return ValueTask.FromResult(new HelperLaunchOwnershipResult(
                HelperLaunchOwnershipStatus.NotCreated,
                job,
                null,
                "helper.windows.launch.specification-mismatch"));
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(new HelperLaunchOwnershipResult(
                HelperLaunchOwnershipStatus.NotCreated,
                job,
                null,
                "helper.windows.launch.boundary-canceled"));
        }

        WindowsJobBoundary.AtomicLaunchReservation reservation;
        try
        {
            reservation = _jobs.ReserveAtomicLaunch(
                job,
                request.Correlation.HelperInstanceId,
                cancellationToken);
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(new HelperLaunchOwnershipResult(
                HelperLaunchOwnershipStatus.NotCreated,
                job,
                null,
                FailureCode(exception)));
        }

        try
        {
            _api.CreateSuspendedInJob(
                _specification,
                reservation.Job.Handle,
                reservation.Process.Lease,
                cancellationToken);
        }
        catch (Exception exception)
        {
            if (reservation.Process.Lease.IsCreated)
                return ValueTask.FromResult(Rejected(job, reservation, FailureCode(exception)));

            _jobs.CancelAtomicLaunch(reservation);
            return ValueTask.FromResult(new HelperLaunchOwnershipResult(
                HelperLaunchOwnershipStatus.NotCreated,
                job,
                null,
                FailureCode(exception)));
        }

        if (!reservation.Process.Lease.IsCreated)
        {
            _jobs.CancelAtomicLaunch(reservation);
            return ValueTask.FromResult(new HelperLaunchOwnershipResult(
                HelperLaunchOwnershipStatus.NotCreated,
                job,
                null,
                "helper.windows.launch.no-ownership"));
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromResult(Rejected(
                job,
                reservation,
                "helper.windows.launch.boundary-canceled"));
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observedProcessId = _api.GetProcessId(reservation.Process.Lease.ProcessHandle);
            cancellationToken.ThrowIfCancellationRequested();
            var observedThreadId = _api.GetThreadId(
                reservation.Process.Lease.PrimaryThreadHandle);
            cancellationToken.ThrowIfCancellationRequested();
            var observedCreationTime = _api.GetProcessCreationFileTime(
                reservation.Process.Lease.ProcessHandle);
            cancellationToken.ThrowIfCancellationRequested();
            if (!reservation.Process.Lease.TryVerifyMetadata(
                    observedProcessId,
                    observedThreadId,
                    observedCreationTime))
            {
                return ValueTask.FromResult(Rejected(
                    job,
                    reservation,
                    "helper.windows.process.identity-mismatch"));
            }
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(Rejected(job, reservation, FailureCode(exception)));
        }

        return ValueTask.FromResult(new HelperLaunchOwnershipResult(
            HelperLaunchOwnershipStatus.CreatedSuspended,
            job,
            reservation.Process.Identity,
            null));
    }

    public ValueTask ResumeAsync(
        RetainedProcessIdentity process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        _jobs.UseOwnedProcess(
            process,
            cancellationToken,
            owned =>
            {
                if (!owned.Lease.IsMetadataVerified || !owned.ContainmentObserved)
                    throw new HelperBoundaryException("helper.windows.process.resume-unverified");
                var thread = owned.Lease.BeginResume();
                var previousSuspendCount = _api.ResumePrimaryThread(thread);
                if (previousSuspendCount != 1)
                {
                    throw new HelperBoundaryException(
                        "helper.windows.process.resume-count-invalid");
                }
                return true;
            });
        return ValueTask.CompletedTask;
    }

    public ValueTask TerminateAsync(
        RetainedProcessIdentity process,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        _jobs.UseOwnedProcess(
            process,
            cancellationToken,
            owned =>
            {
                _api.TerminateProcess(owned.Lease.ProcessHandle);
                return true;
            });
        return ValueTask.CompletedTask;
    }

    private static HelperLaunchOwnershipResult Rejected(
        RetainedJobIdentity job,
        WindowsJobBoundary.AtomicLaunchReservation reservation,
        string failureCode) =>
        new(
            HelperLaunchOwnershipStatus.CreatedRejected,
            job,
            reservation.Process.Identity,
            failureCode);

    private static string FailureCode(Exception exception) => exception switch
    {
        HelperBoundaryException boundary when IsBoundedFailureCode(boundary.FailureCode) =>
            boundary.FailureCode,
        OperationCanceledException => "helper.windows.launch.boundary-canceled",
        _ => "helper.windows.launch.failed",
    };

    private static bool IsBoundedFailureCode(string code) =>
        !string.IsNullOrWhiteSpace(code) &&
        code.Length <= 128 &&
        code.All(character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
}
