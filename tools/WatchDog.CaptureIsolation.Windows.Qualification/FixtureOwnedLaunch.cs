using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using WatchDog.CaptureIsolation;
using WatchDog.CaptureIsolation.Windows;
using WatchDog.CaptureProtocol;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal sealed partial class FixtureOwnedLaunch : IAsyncDisposable
{
    private readonly WindowsJobBoundary _boundary;
    private readonly WindowsProcessLaunchApi _launchApi;
    private readonly WindowsContainmentApi _containmentApi;
    private readonly WindowsJobBoundary.AtomicLaunchReservation _reservation;
    private IHelperReaper? _reaper;
    private readonly WindowsBootstrapTransportBoundary? _bootstrap;
    private readonly SafeWaitHandle? _sentinel;
    private readonly CaptureIsolationLimitSnapshot? _bootstrapLimits;
    private bool _reaped;

    private FixtureOwnedLaunch(
        WindowsJobBoundary boundary,
        WindowsProcessLaunchApi launchApi,
        WindowsContainmentApi containmentApi,
        WindowsJobBoundary.AtomicLaunchReservation reservation,
        CaptureCorrelation correlation,
        WindowsBootstrapTransportBoundary? bootstrap = null,
        SafeWaitHandle? sentinel = null,
        CaptureIsolationLimitSnapshot? bootstrapLimits = null)
    {
        _boundary = boundary;
        _launchApi = launchApi;
        _containmentApi = containmentApi;
        _reservation = reservation;
        Correlation = correlation;
        _bootstrap = bootstrap;
        _sentinel = sentinel;
        _bootstrapLimits = bootstrapLimits;
    }

    internal CaptureCorrelation Correlation { get; }
    internal bool IsReaped => _reaped;
    internal RetainedJobIdentity Job => _reservation.Job.Identity;
    internal RetainedProcessIdentity Process => _reservation.Process.Identity;
    internal ProcessCoordinate Coordinate => new(
        _reservation.Process.Lease.ProcessId,
        _reservation.Process.Lease.CreationFileTime ??
            throw new QualificationAssertionException("fixture.process.metadata-missing"));

    internal static async Task<FixtureOwnedLaunch> CreateAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IEnumerable<SafeHandle>? inheritedChildHandles = null)
    {
        var helperId = NewNonemptyGuid();
        var correlation = new CaptureCorrelation(
            NewNonemptyGuid(),
            helperId,
            NewNonemptyGuid(),
            NewNonemptyGuid(),
            1,
            NewNonemptyGuid(),
            1,
            QualificationConstants.Digest);
        var request = new HelperLaunchRequest(correlation, QualificationConstants.Digest);
        var commandLine = BuildCommandLine(executablePath, arguments);
        var specification = new WindowsLaunchSpecification(
            request,
            executablePath,
            commandLine,
            inheritedChildHandles: inheritedChildHandles);
        var containmentApi = WindowsContainmentApi.Instance;
        var launchApi = WindowsProcessLaunchApi.Instance;
        var boundary = new WindowsJobBoundary();
        RetainedJobIdentity? job = null;
        WindowsJobBoundary.AtomicLaunchReservation? reservation = null;
        try
        {
            job = await boundary.CreateAsync(helperId, cancellationToken).ConfigureAwait(false);
            reservation = boundary.ReserveAtomicLaunch(job, helperId, cancellationToken);
            launchApi.CreateSuspendedInJob(
                specification,
                reservation.Job.Handle,
                reservation.Process.Lease,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var processId = launchApi.GetProcessId(reservation.Process.Lease.ProcessHandle);
            cancellationToken.ThrowIfCancellationRequested();
            var threadId = launchApi.GetThreadId(reservation.Process.Lease.PrimaryThreadHandle);
            cancellationToken.ThrowIfCancellationRequested();
            var creation = launchApi.GetProcessCreationFileTime(
                reservation.Process.Lease.ProcessHandle);
            if (!reservation.Process.Lease.TryVerifyMetadata(processId, threadId, creation))
                throw new QualificationAssertionException("fixture.process.identity-mismatch");
            return new(boundary, launchApi, containmentApi, reservation, correlation);
        }
        catch
        {
            if (reservation is not null && reservation.Process.Lease.IsCreated)
            {
                await CleanupFailedCreationAsync(boundary, launchApi, job!, reservation)
                    .ConfigureAwait(false);
            }
            else if (reservation is not null)
            {
                boundary.CancelAtomicLaunch(reservation);
                await CloseEmptyJobAsync(boundary, job!).ConfigureAwait(false);
            }
            else if (job is not null)
            {
                await CloseEmptyJobAsync(boundary, job).ConfigureAwait(false);
            }
            await boundary.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task<FixtureOwnedLaunch> CreateAuthenticatedAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool corruptBootstrapMaterial = false)
    {
        var helperId = NewNonemptyGuid();
        var correlation = new CaptureCorrelation(
            NewNonemptyGuid(), helperId, NewNonemptyGuid(), NewNonemptyGuid(), 1,
            NewNonemptyGuid(), 1, QualificationConstants.Digest);
        var request = new HelperLaunchRequest(correlation, QualificationConstants.Digest);
        var limits = new CaptureIsolationLimitSnapshot(4096, 2048, 8, 32);
        var boundary = new WindowsJobBoundary();
        WindowsBootstrapTransportBoundary? bootstrap = null;
        SafeWaitHandle? sentinel = null;
        RetainedJobIdentity? job = null;
        WindowsJobBoundary.AtomicLaunchReservation? reservation = null;
        try
        {
            bootstrap = WindowsBootstrapTransportBoundary.Create(
                boundary,
                new WindowsBootstrapExpectation(
                    request,
                    (int)CaptureIsolationSchemaVersion.V1,
                    (int)CaptureIsolationArchitecture.X64,
                    QualificationConstants.Digest,
                    QualificationConstants.Digest,
                    limits),
                QualificationRoles.BootstrapProfile());
            if (corruptBootstrapMaterial)
                bootstrap.CorruptBootstrapMaterialForFixture();
            sentinel = CreateInheritableSentinel();
            var childArguments = arguments.Concat(
            [
                "--bootstrap-section", bootstrap.ChildSectionHandle.ToInt64().ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "--bootstrap-parent", bootstrap.ChildParentProcessHandle.ToInt64().ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "--bootstrap-bytes", bootstrap.SectionBytes.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "--sentinel-handle", sentinel.DangerousGetHandle().ToInt64().ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
            ]).ToArray();
            var specification = new WindowsLaunchSpecification(
                request,
                executablePath,
                BuildCommandLine(executablePath, childArguments),
                inheritedChildHandles: bootstrap.InheritedChildHandles);
            var launchApi = WindowsProcessLaunchApi.Instance;
            var containmentApi = WindowsContainmentApi.Instance;
            job = await boundary.CreateAsync(helperId, cancellationToken).ConfigureAwait(false);
            reservation = boundary.ReserveAtomicLaunch(job, helperId, cancellationToken);
            launchApi.CreateSuspendedInJob(
                specification,
                reservation.Job.Handle,
                reservation.Process.Lease,
                cancellationToken);
            var processId = launchApi.GetProcessId(reservation.Process.Lease.ProcessHandle);
            var threadId = launchApi.GetThreadId(reservation.Process.Lease.PrimaryThreadHandle);
            var creation = launchApi.GetProcessCreationFileTime(reservation.Process.Lease.ProcessHandle);
            if (!reservation.Process.Lease.TryVerifyMetadata(processId, threadId, creation))
                throw new QualificationAssertionException("fixture.process.identity-mismatch");
            return new(
                boundary,
                launchApi,
                containmentApi,
                reservation,
                correlation,
                bootstrap,
                sentinel,
                limits);
        }
        catch
        {
            if (reservation is not null && reservation.Process.Lease.IsCreated)
                await CleanupFailedCreationAsync(boundary, WindowsProcessLaunchApi.Instance, job!, reservation)
                    .ConfigureAwait(false);
            else if (reservation is not null)
            {
                boundary.CancelAtomicLaunch(reservation);
                await CloseEmptyJobAsync(boundary, job!).ConfigureAwait(false);
            }
            else if (job is not null)
                await CloseEmptyJobAsync(boundary, job).ConfigureAwait(false);
            if (bootstrap is not null)
                await bootstrap.DisposeAsync().ConfigureAwait(false);
            sentinel?.Dispose();
            await boundary.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal Task<HelperBootstrapObservation> AuthenticateBootstrapAsync(
        CancellationToken cancellationToken)
    {
        if (_bootstrap is null || _bootstrapLimits is null)
            throw new InvalidOperationException("The fixture launch has no bootstrap owner.");
        return _bootstrap.AuthenticateAsync(
            Process,
            new HelperLaunchRequest(Correlation, QualificationConstants.Digest),
            _bootstrapLimits,
            cancellationToken).AsTask();
    }

    internal Task<FixtureOwnedLaunch> CreateUnintendedBootstrapPeerAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (_bootstrap is null || _sentinel is null)
            throw new InvalidOperationException("The fixture launch has no bootstrap owner.");
        var childArguments = arguments.Concat(
        [
            "--bootstrap-section", _bootstrap.ChildSectionHandle.ToInt64().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "--bootstrap-parent", _bootstrap.ChildParentProcessHandle.ToInt64().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "--bootstrap-bytes", _bootstrap.SectionBytes.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            "--sentinel-handle", _sentinel.DangerousGetHandle().ToInt64().ToString(
                System.Globalization.CultureInfo.InvariantCulture),
        ]).ToArray();
        return CreateAsync(
            executablePath,
            childArguments,
            cancellationToken,
            _bootstrap.InheritedChildHandles);
    }

    internal async Task ObserveSingletonAndResumeAsync(CancellationToken cancellationToken)
    {
        var observation = await _boundary.ObserveAssignmentAsync(
            Job,
            Process,
            Correlation,
            cancellationToken).ConfigureAwait(false);
        if (observation.Status != HelperJobMembershipStatus.Confirmed ||
            observation.AssignedProcessCount != 1 ||
            observation.MemberProcessCount != 1 ||
            observation.Policy != HelperJobContainmentPolicy.KillOnJobClose ||
            observation.Job != Job || observation.Process != Process)
        {
            throw new QualificationAssertionException("fixture.job.singleton-not-proven");
        }

        _boundary.UseOwnedProcess(
            Process,
            cancellationToken,
            owned =>
            {
                if (!owned.Lease.IsMetadataVerified || !owned.ContainmentObserved)
                    throw new QualificationAssertionException("fixture.process.resume-unverified");
                var previousCount = _launchApi.ResumePrimaryThread(owned.Lease.BeginResume());
                if (previousCount != 1)
                    throw new QualificationAssertionException("fixture.process.resume-count-invalid");
                return true;
            });
    }

    internal MembershipSnapshotRecord CaptureMembershipSnapshot(
        Guid nonce,
        string scenario,
        RetainedProcessProof descendant)
    {
        var observed = _containmentApi.QueryProcessIds(_reservation.Job.Handle);
        var expected = new[] { Coordinate.ProcessId, descendant.Coordinate.ProcessId };
        Array.Sort(expected);
        var actual = observed.ProcessIds.Order().ToArray();
        return new MembershipSnapshotRecord(
            nonce,
            "owner",
            scenario,
            Array.AsReadOnly(expected),
            observed.AssignedProcessCount,
            observed.MemberProcessCount,
            observed.Truncated,
            Array.AsReadOnly(actual),
            _containmentApi.QueryActiveProcessCount(_reservation.Job.Handle),
            _containmentApi.IsProcessInJob(
                _reservation.Process.Lease.ProcessHandle,
                _reservation.Job.Handle),
            descendant.IsMemberOfJob(_reservation.Job.Handle),
            _containmentApi.QueryContainmentPolicy(_reservation.Job.Handle).ToString());
    }

    internal MembershipEnumerationRecord CaptureDiagnosticMembership(
        Guid nonce,
        string scenario,
        RetainedProcessProof descendant,
        string fixtureExecutablePath) =>
        DiagnosticJobMembership.Capture(
            nonce,
            scenario,
            _reservation.Job.Handle,
            _reservation.Process.Lease.ProcessHandle,
            Coordinate,
            descendant,
            fixtureExecutablePath);

    internal static void AssertExactMembership(MembershipSnapshotRecord snapshot)
    {
        if (snapshot.Truncated)
            throw new QualificationAssertionException("fixture.job.membership-truncated");
        if (snapshot.AssignedProcessCount != 2 || snapshot.MemberProcessCount != 2 ||
            snapshot.ActiveProcessCount != 2 || snapshot.ActualProcessIds.Count != 2)
        {
            throw new QualificationAssertionException("fixture.job.membership-count-mismatch");
        }
        if (!snapshot.HelperIsMember)
            throw new QualificationAssertionException("fixture.job.membership-helper-not-member");
        if (!snapshot.DescendantIsMember)
        {
            throw new QualificationAssertionException(
                "fixture.job.membership-descendant-not-member");
        }
        if (!snapshot.ActualProcessIds.SequenceEqual(snapshot.ExpectedProcessIds))
            throw new QualificationAssertionException("fixture.job.membership-id-mismatch");
        if (!snapshot.ObservedPolicy.Equals(
                HelperJobContainmentPolicy.KillOnJobClose.ToString(),
                StringComparison.Ordinal))
        {
            throw new QualificationAssertionException("fixture.job.policy-mismatch");
        }
    }

    internal bool IsProcessSignaled() =>
        _containmentApi.WaitForProcess(_reservation.Process.Lease.ProcessHandle) ==
        WindowsWaitResult.Signaled;

    internal void TerminateProcessExact() =>
        _boundary.UseOwnedProcess(
            Process,
            CancellationToken.None,
            owned =>
            {
                _launchApi.TerminateProcess(owned.Lease.ProcessHandle);
                return true;
            });

    internal ValueTask TerminateJobExactAsync() =>
        _boundary.TerminateAsync(Job, CancellationToken.None);

    internal async Task ReapAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        _reaper ??= _boundary.CreateReaper(Job, Process);
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observation = await _reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None)
                .ConfigureAwait(false);
            if (observation.Process == Process && observation.Job == Job &&
                observation.ProcessExited && observation.JobEmpty)
            {
                await _reaper.DisposeAsync().ConfigureAwait(false);
                _reaped = true;
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
        throw new QualificationTimeoutException("fixture.reaper.timeout");
    }

    internal async Task ForceCleanupAsync(CancellationToken cancellationToken)
    {
        if (_reaped)
            return;

        Exception? requestFailure = null;
        try
        {
            _launchApi.TerminateProcess(_reservation.Process.Lease.ProcessHandle);
        }
        catch (Exception exception)
        {
            requestFailure = exception;
        }
        try
        {
            _containmentApi.TerminateJob(_reservation.Job.Handle);
        }
        catch (Exception exception)
        {
            requestFailure ??= exception;
        }

        try
        {
            await ReapAsync(QualificationConstants.CleanupTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw new QualificationCleanupException(
                requestFailure is null
                    ? "fixture.cleanup.reap-failed"
                    : "fixture.cleanup.request-and-reap-failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_reaped)
            throw new InvalidOperationException("Fixture ownership cannot be disposed before reaping.");
        if (_bootstrap is not null)
            await _bootstrap.DisposeAsync().ConfigureAwait(false);
        _sentinel?.Dispose();
        await _boundary.DisposeAsync().ConfigureAwait(false);
    }

    internal static string BuildCommandLine(string executablePath, IReadOnlyList<string> arguments)
    {
        var values = new List<string>(arguments.Count + 1) { executablePath };
        values.AddRange(arguments);
        return string.Join(' ', values.Select(QuoteWindowsArgument));
    }

    private static string QuoteWindowsArgument(string value)
    {
        if (value.Length == 0)
            return "\"\"";
        if (!value.Any(character => char.IsWhiteSpace(character) || character == '"'))
            return value;

        var result = new System.Text.StringBuilder(value.Length + 2).Append('"');
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\')
            {
                slashes++;
                continue;
            }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }

    private static Guid NewNonemptyGuid()
    {
        Guid value;
        do
        {
            value = Guid.NewGuid();
        }
        while (value == Guid.Empty);
        return value;
    }

    private static SafeWaitHandle CreateInheritableSentinel()
    {
        var handle = CreateEvent(nint.Zero, true, false, null);
        if (handle.IsInvalid || !SetHandleInformation(handle, 1, 1))
        {
            handle.Dispose();
            throw new QualificationAssertionException("fixture.sentinel.create-failed");
        }
        return handle;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeWaitHandle CreateEvent(
        nint security,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(SafeWaitHandle handle, uint mask, uint flags);

    private static async Task CleanupFailedCreationAsync(
        WindowsJobBoundary boundary,
        WindowsProcessLaunchApi launchApi,
        RetainedJobIdentity job,
        WindowsJobBoundary.AtomicLaunchReservation reservation)
    {
        try
        {
            boundary.UseOwnedProcess(
                reservation.Process.Identity,
                CancellationToken.None,
                owned =>
                {
                    launchApi.TerminateProcess(owned.Lease.ProcessHandle);
                    return true;
                });
        }
        catch
        {
            // The independent job request and exact reaper still run below.
        }
        try
        {
            await boundary.TerminateAsync(job, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Exact terminal proof below decides cleanup certainty.
        }
        var reaper = boundary.CreateReaper(job, reservation.Process.Identity);
        await ReapTemporaryAsync(reaper, reservation.Process.Identity, job).ConfigureAwait(false);
    }

    private static async Task CloseEmptyJobAsync(
        WindowsJobBoundary boundary,
        RetainedJobIdentity job)
    {
        var reaper = boundary.CreateReaper(job, null);
        await ReapTemporaryAsync(reaper, null, job).ConfigureAwait(false);
    }

    private static async Task ReapTemporaryAsync(
        IHelperReaper reaper,
        RetainedProcessIdentity? process,
        RetainedJobIdentity job)
    {
        var deadline = Stopwatch.GetTimestamp() +
            (long)(QualificationConstants.CleanupTimeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var observation = await reaper.ObserveAsync(TimeSpan.Zero, CancellationToken.None)
                .ConfigureAwait(false);
            if (observation.Process == process && observation.Job == job &&
                observation.ProcessExited && observation.JobEmpty)
            {
                await reaper.DisposeAsync().ConfigureAwait(false);
                return;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(25)).ConfigureAwait(false);
        }
        throw new QualificationCleanupException("fixture.cleanup.reap-timeout");
    }
}
