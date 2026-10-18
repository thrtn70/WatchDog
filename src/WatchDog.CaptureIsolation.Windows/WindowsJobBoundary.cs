using System.Diagnostics;
using WatchDog.CaptureProtocol;

namespace WatchDog.CaptureIsolation.Windows;

/// <summary>
/// Unregistered Windows Job Object owner shared with the internal atomic process facade. The
/// facade reserves ownership here before CreateProcessW associates the child through
/// PROC_THREAD_ATTRIBUTE_JOB_LIST.
/// </summary>
public sealed class WindowsJobBoundary : IHelperJobBoundary, IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly IWindowsContainmentApi _api;
    private readonly IWindowsReapDelay _reapDelay;
    private readonly Dictionary<Guid, JobLease> _jobs = [];
    private readonly Dictionary<Guid, AdoptedProcessLease> _processes = [];
    private readonly Dictionary<Guid, WindowsJobReaper> _reapers = [];
    private readonly List<SafeWindowsHandle> _emptyJobsPendingClose = [];
    private bool _disposed;

    public WindowsJobBoundary()
        : this(WindowsContainmentApi.Instance, SystemWindowsReapDelay.Instance)
    {
    }

    internal WindowsJobBoundary(
        IWindowsContainmentApi api,
        IWindowsReapDelay? reapDelay = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _reapDelay = reapDelay ?? SystemWindowsReapDelay.Instance;
    }

    public ValueTask<RetainedJobIdentity> CreateAsync(
        Guid helperInstanceId,
        CancellationToken cancellationToken)
    {
        if (helperInstanceId == Guid.Empty)
            throw new ArgumentException("A helper identity is required.", nameof(helperInstanceId));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (_emptyJobsPendingClose.Count != 0)
                throw new HelperBoundaryException("helper.windows.job.close-pending");
            var handle = _api.CreateUnnamedJob();
            try
            {
                // Once the native handle exists, cancellation cannot discard ownership.
                _api.ConfigureKillOnJobClose(handle);
                var observedPolicy = _api.QueryContainmentPolicy(handle);
                if (observedPolicy != HelperJobContainmentPolicy.KillOnJobClose)
                    throw new HelperBoundaryException("helper.windows.job.policy-mismatch");

                var identity = NewJobIdentity(helperInstanceId);
                _jobs.Add(identity.OpaqueHandleIdentity, new(identity, handle));
                return ValueTask.FromResult(identity);
            }
            catch
            {
                // The new job is still empty. Closing it here cannot abandon a child.
                if (!handle.CloseOnce())
                {
                    _emptyJobsPendingClose.Add(handle);
                    throw new HelperBoundaryException("helper.windows.job.close-failed");
                }
                throw;
            }
        }
    }

    internal AtomicLaunchReservation ReserveAtomicLaunch(
        RetainedJobIdentity job,
        Guid helperInstanceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (helperInstanceId == Guid.Empty)
            throw new ArgumentException("A helper identity is required.", nameof(helperInstanceId));
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var jobLease = FindJob(job);
            if (job.HelperInstanceId != helperInstanceId)
                throw new HelperBoundaryException("helper.windows.job.identity-mismatch");
            if (jobLease.ReservedProcess is not null)
                throw new HelperBoundaryException("helper.windows.job.launch-reserved");

            var identity = NewProcessIdentity(helperInstanceId);
            var processLease = new WindowsProcessLease();
            var ownedProcess = new AdoptedProcessLease(identity, processLease);
            _processes.Add(identity.OpaqueHandleIdentity, ownedProcess);
            jobLease.ReservedProcess = identity;
            return new(jobLease, ownedProcess);
        }
    }

    internal void CancelAtomicLaunch(AtomicLaunchReservation reservation)
    {
        ArgumentNullException.ThrowIfNull(reservation);

        lock (_sync)
        {
            ThrowIfDisposed();
            if (reservation.Process.Lease.IsCreated ||
                !_jobs.TryGetValue(reservation.Job.Identity.OpaqueHandleIdentity, out var job) ||
                !ReferenceEquals(job, reservation.Job) ||
                job.ReservedProcess != reservation.Process.Identity ||
                !_processes.TryGetValue(
                    reservation.Process.Identity.OpaqueHandleIdentity,
                    out var process) ||
                !ReferenceEquals(process, reservation.Process))
            {
                throw new HelperBoundaryException("helper.windows.launch.reservation-invalid");
            }
            _processes.Remove(reservation.Process.Identity.OpaqueHandleIdentity);
            job.ReservedProcess = null;
            reservation.Process.Lease.Dispose();
        }
    }

    internal T UseOwnedProcess<T>(
        RetainedProcessIdentity process,
        CancellationToken cancellationToken,
        Func<AdoptedProcessLease, T> operation)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            return operation(FindProcess(process));
        }
    }

    internal WindowsOwnedProcessCoordinate GetOwnedProcessCoordinate(
        RetainedProcessIdentity process,
        CancellationToken cancellationToken) =>
        UseOwnedProcess(
            process,
            cancellationToken,
            owned =>
            {
                if (!owned.Lease.IsMetadataVerified ||
                    owned.Lease.CreationFileTime is not { } creation || creation <= 0)
                {
                    throw new HelperBoundaryException(
                        "helper.windows.process.identity-unverified");
                }
                if (_api.WaitForProcess(owned.Lease.ProcessHandle) != WindowsWaitResult.Timeout)
                    throw new HelperBoundaryException("helper.windows.process.exited");
                return new WindowsOwnedProcessCoordinate(
                    owned.Lease.ProcessId,
                    creation);
            });

    internal async ValueTask<bool> WaitForOwnedProcessExitAsync(
        RetainedProcessIdentity process,
        TimeSpan budget,
        CancellationToken cancellationToken)
    {
        if (budget <= TimeSpan.Zero || budget == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(budget));
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var exited = UseOwnedProcess(
                process,
                cancellationToken,
                owned => _api.WaitForProcess(owned.Lease.ProcessHandle) == WindowsWaitResult.Signaled);
            if (exited)
                return true;
            var elapsed = Stopwatch.GetElapsedTime(started);
            if (elapsed >= budget)
                return false;
            var delay = TimeSpan.FromMilliseconds(Math.Min(20, (budget - elapsed).TotalMilliseconds));
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
    }

    public ValueTask<HelperJobMembershipObservation> ObserveAssignmentAsync(
        RetainedJobIdentity job,
        RetainedProcessIdentity process,
        CaptureCorrelation correlation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(correlation);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var jobLease = FindJob(job);
            var processLease = FindProcess(process);
            EnsureSameHelper(job, process);
            if (correlation.HelperInstanceId != job.HelperInstanceId)
                throw new HelperBoundaryException("helper.windows.job.identity-mismatch");
            if (jobLease.ReservedProcess != process)
                throw new HelperBoundaryException("helper.windows.job.assignment-mismatch");

            var policy = _api.QueryContainmentPolicy(jobLease.Handle);
            var isMember = _api.IsProcessInJob(
                processLease.Lease.ProcessHandle,
                jobLease.Handle);
            var processIds = _api.QueryProcessIds(jobLease.Handle);
            var status = MembershipStatus(isMember, processLease.Lease.ProcessId, processIds);
            processLease.ContainmentObserved =
                status == HelperJobMembershipStatus.Confirmed &&
                policy == HelperJobContainmentPolicy.KillOnJobClose;
            return ValueTask.FromResult(new HelperJobMembershipObservation(
                job,
                process,
                status,
                processIds.AssignedProcessCount,
                processIds.MemberProcessCount,
                policy));
        }
    }

    public ValueTask TerminateAsync(
        RetainedJobIdentity job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_sync)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            var lease = FindJob(job);
            _api.TerminateJob(lease.Handle);
            return ValueTask.CompletedTask;
        }
    }

    public IHelperReaper CreateReaper(
        RetainedJobIdentity job,
        RetainedProcessIdentity? process)
    {
        ArgumentNullException.ThrowIfNull(job);
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_reapers.TryGetValue(job.OpaqueHandleIdentity, out var existing))
            {
                if (existing.ProcessIdentity != process || existing.JobIdentity != job)
                    throw new HelperBoundaryException("helper.windows.reaper.identity-mismatch");
                if (!existing.IsActive)
                    throw new HelperBoundaryException("helper.windows.reaper.unavailable");
                return existing;
            }

            var jobLease = FindJob(job);
            AdoptedProcessLease? processLease = null;
            if (process is not null)
            {
                processLease = FindProcess(process);
                EnsureSameHelper(job, process);
                if (jobLease.ReservedProcess is not null && jobLease.ReservedProcess != process)
                    throw new HelperBoundaryException("helper.windows.reaper.identity-mismatch");
            }
            else if (jobLease.ReservedProcess is not null)
            {
                throw new HelperBoundaryException("helper.windows.reaper.process-required");
            }

            var reaper = new WindowsJobReaper(
                _api,
                _reapDelay,
                jobLease,
                processLease,
                OnReaperDisposed);
            _reapers.Add(job.OpaqueHandleIdentity, reaper);
            _jobs.Remove(job.OpaqueHandleIdentity);
            if (process is not null)
                _processes.Remove(process.OpaqueHandleIdentity);
            return reaper;
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync)
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            for (var index = _emptyJobsPendingClose.Count - 1; index >= 0; index--)
            {
                if (_emptyJobsPendingClose[index].CloseOnce())
                    _emptyJobsPendingClose.RemoveAt(index);
            }
            if (_emptyJobsPendingClose.Count != 0)
                throw new HelperBoundaryException("helper.windows.job.close-failed");
            if (_jobs.Count != 0 || _processes.Count != 0 || _reapers.Count != 0)
            {
                throw new InvalidOperationException(
                    "The Windows containment boundary still owns native resources.");
            }
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static HelperJobMembershipStatus MembershipStatus(
        bool isMember,
        uint expectedProcessId,
        WindowsProcessIdList processIds)
    {
        if (!isMember)
            return HelperJobMembershipStatus.NotMember;
        if (processIds.Truncated || processIds.MemberProcessCount < processIds.AssignedProcessCount)
            return HelperJobMembershipStatus.Truncated;
        if (processIds.ProcessIds.Count != processIds.MemberProcessCount ||
            !processIds.ProcessIds.Contains(expectedProcessId))
        {
            return HelperJobMembershipStatus.ForeignMember;
        }
        if (processIds.AssignedProcessCount != 1 || processIds.MemberProcessCount != 1)
            return HelperJobMembershipStatus.MultipleMembers;
        return HelperJobMembershipStatus.Confirmed;
    }

    private JobLease FindJob(RetainedJobIdentity identity)
    {
        if (identity.OpaqueHandleIdentity == Guid.Empty || identity.HelperInstanceId == Guid.Empty)
            throw new HelperBoundaryException("helper.windows.job.identity-mismatch");
        if (!_jobs.TryGetValue(identity.OpaqueHandleIdentity, out var lease) ||
            lease.Identity != identity)
        {
            throw new HelperBoundaryException("helper.windows.job.identity-mismatch");
        }
        return lease;
    }

    private AdoptedProcessLease FindProcess(RetainedProcessIdentity identity)
    {
        if (identity.OpaqueHandleIdentity == Guid.Empty || identity.HelperInstanceId == Guid.Empty)
            throw new HelperBoundaryException("helper.windows.process.identity-mismatch");
        if (!_processes.TryGetValue(identity.OpaqueHandleIdentity, out var lease) ||
            lease.Identity != identity)
        {
            throw new HelperBoundaryException("helper.windows.process.identity-mismatch");
        }
        return lease;
    }

    private static void EnsureSameHelper(
        RetainedJobIdentity job,
        RetainedProcessIdentity process)
    {
        if (job.HelperInstanceId != process.HelperInstanceId)
            throw new HelperBoundaryException("helper.windows.job.identity-mismatch");
    }

    private void OnReaperDisposed(Guid jobIdentity)
    {
        lock (_sync)
        {
            _reapers.Remove(jobIdentity);
        }
    }

    private RetainedJobIdentity NewJobIdentity(Guid helperInstanceId)
    {
        Guid identity;
        do
        {
            identity = NewOpaqueIdentity();
        }
        while (_jobs.ContainsKey(identity) || _reapers.ContainsKey(identity));
        return new(identity, helperInstanceId);
    }

    private RetainedProcessIdentity NewProcessIdentity(Guid helperInstanceId)
    {
        Guid identity;
        do
        {
            identity = NewOpaqueIdentity();
        }
        while (_processes.ContainsKey(identity) ||
            _reapers.Values.Any(reaper =>
                reaper.ProcessIdentity?.OpaqueHandleIdentity == identity));
        return new(identity, helperInstanceId);
    }

    private static Guid NewOpaqueIdentity()
    {
        Guid identity;
        do
        {
            identity = Guid.NewGuid();
        }
        while (identity == Guid.Empty);
        return identity;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal sealed class JobLease(
        RetainedJobIdentity identity,
        SafeWindowsHandle handle)
    {
        internal RetainedJobIdentity Identity { get; } = identity;
        internal SafeWindowsHandle Handle { get; } = handle;
        internal RetainedProcessIdentity? ReservedProcess { get; set; }
    }

    internal sealed class AdoptedProcessLease(
        RetainedProcessIdentity identity,
        WindowsProcessLease lease)
    {
        internal RetainedProcessIdentity Identity { get; } = identity;
        internal WindowsProcessLease Lease { get; } = lease;
        internal bool ContainmentObserved { get; set; }
    }

    internal sealed class AtomicLaunchReservation(
        JobLease job,
        AdoptedProcessLease process)
    {
        internal JobLease Job { get; } = job;
        internal AdoptedProcessLease Process { get; } = process;
    }
}

internal sealed record WindowsOwnedProcessCoordinate(uint ProcessId, long CreationFileTime);
