using System.Collections.ObjectModel;
using WatchDog.CaptureProtocol;

namespace WatchDog.CaptureIsolation;

/// <summary>
/// Test/profile budgets for the managed supervisor contract. There are intentionally no
/// production defaults and this type is not registered by the application.
/// </summary>
public sealed record SupervisorTestProfile
{
    public SupervisorTestProfile(
        TimeSpan gracefulStopBudget,
        TimeSpan reapObservationBudget,
        CaptureIsolationLimitSnapshot capabilityLimits)
    {
        if (gracefulStopBudget <= TimeSpan.Zero || gracefulStopBudget == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(gracefulStopBudget));
        if (reapObservationBudget <= TimeSpan.Zero || reapObservationBudget == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(reapObservationBudget));
        ArgumentNullException.ThrowIfNull(capabilityLimits);
        GracefulStopBudget = gracefulStopBudget;
        ReapObservationBudget = reapObservationBudget;
        CapabilityLimits = capabilityLimits;
    }

    public TimeSpan GracefulStopBudget { get; }
    public TimeSpan ReapObservationBudget { get; }
    public CaptureIsolationLimitSnapshot CapabilityLimits { get; }
}

public sealed record HelperLaunchRequest(CaptureCorrelation Correlation, string PackageManifestDigest)
{
    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Correlation);
        if (!IsolationValueValidation.IsCorrelationValid(Correlation) ||
            !IsolationValueValidation.IsSha256Digest(PackageManifestDigest))
        {
            throw new ArgumentException("A valid launch identity and package digest are required.");
        }
    }
}

/// <summary>
/// Opaque identity for a retained process handle owned by an implementation. It is not a PID,
/// executable name, or proof of process exit.
/// </summary>
public sealed record RetainedProcessIdentity(Guid OpaqueHandleIdentity, Guid HelperInstanceId)
{
    internal void Validate()
    {
        if (OpaqueHandleIdentity == Guid.Empty || HelperInstanceId == Guid.Empty)
            throw new ArgumentException("A retained process identity must be nonempty.");
    }
}

/// <summary>
/// Opaque identity for a retained job handle owned by an implementation. It is not proof that
/// the job is empty or configured as a Windows containment boundary.
/// </summary>
public sealed record RetainedJobIdentity(Guid OpaqueHandleIdentity, Guid HelperInstanceId)
{
    internal void Validate()
    {
        if (OpaqueHandleIdentity == Guid.Empty || HelperInstanceId == Guid.Empty)
            throw new ArgumentException("A retained job identity must be nonempty.");
    }
}

public interface IHelperProcessBoundary
{
    /// <summary>
    /// Atomically launches the exact helper suspended in the retained job. Any created process is
    /// returned with its retained identity even when post-create validation or cancellation fails.
    /// A NotCreated result guarantees that no process was created.
    /// </summary>
    ValueTask<HelperLaunchOwnershipResult> LaunchSuspendedInJobAsync(
        HelperLaunchRequest request,
        RetainedJobIdentity job,
        CancellationToken cancellationToken);

    ValueTask ResumeAsync(
        RetainedProcessIdentity process,
        CancellationToken cancellationToken);

    ValueTask TerminateAsync(
        RetainedProcessIdentity process,
        CancellationToken cancellationToken);
}

public enum HelperLaunchOwnershipStatus
{
    NotCreated = 1,
    CreatedSuspended = 2,
    CreatedRejected = 3,
}

/// <summary>
/// Neutral ownership result from the process boundary. The supervisor validates its exact job,
/// helper, status, process presence, and bounded failure before trusting the launch.
/// </summary>
public sealed record HelperLaunchOwnershipResult(
    HelperLaunchOwnershipStatus Status,
    RetainedJobIdentity Job,
    RetainedProcessIdentity? Process,
    string? FailureCode);

/// <summary>
/// Trust seam for exact job ownership and neutral membership observations. The injected
/// implementation must retain the native ownership represented by every returned identity;
/// observations do not become trusted containment evidence until the internal admission gate
/// validates their identities, counts, status, and policy.
/// </summary>
public interface IHelperJobBoundary
{
    /// <summary>
    /// Returns the retained identity of the exact newly created job. A failed call must guarantee
    /// that it did not leave an unowned job alive. If closing an empty, unexposed job fails, the
    /// boundary must retain it, reject further creates and disposal completion, and keep reporting
    /// bounded cleanup failure until that exact handle is closed.
    /// </summary>
    ValueTask<RetainedJobIdentity> CreateAsync(
        Guid helperInstanceId,
        CancellationToken cancellationToken);

    ValueTask<HelperJobMembershipObservation> ObserveAssignmentAsync(
        RetainedJobIdentity job,
        RetainedProcessIdentity process,
        CaptureCorrelation correlation,
        CancellationToken cancellationToken);

    ValueTask TerminateAsync(
        RetainedJobIdentity job,
        CancellationToken cancellationToken);

    IHelperReaper CreateReaper(
        RetainedJobIdentity job,
        RetainedProcessIdentity? process);
}

public enum HelperJobMembershipStatus
{
    Confirmed = 1,
    NotMember = 2,
    Truncated = 3,
    ForeignMember = 4,
    Unavailable = 5,
    MultipleMembers = 6,
}

[Flags]
public enum HelperJobContainmentPolicy
{
    None = 0,
    KillOnJobClose = 1,
    BreakawayAllowed = 2,
    SilentBreakawayAllowed = 4,
    Unknown = 8,
}

/// <summary>
/// Neutral observation emitted by a job boundary. It is not trusted admission evidence; the
/// managed admission gate verifies the exact retained identities, singleton membership, and
/// containment policy before minting immutable containment evidence.
/// </summary>
public sealed record HelperJobMembershipObservation(
    RetainedJobIdentity Job,
    RetainedProcessIdentity Process,
    HelperJobMembershipStatus Status,
    uint AssignedProcessCount,
    uint MemberProcessCount,
    HelperJobContainmentPolicy Policy);

public enum HelperAuthenticationOutcome
{
    Unknown = 0,
    Authenticated = 1,
    PeerMismatch = 2,
    ProtocolMismatch = 3,
    PackageMismatch = 4,
    AuthenticationFailed = 5,
}

/// <summary>
/// Neutral peer claim observed across the authenticated transport. It is not package,
/// publisher, process, or admission evidence; the managed gate compares every field with the
/// already-admitted launch plan before minting trusted evidence.
/// </summary>
public sealed class HelperBootstrapPeerObservation
{
    public HelperBootstrapPeerObservation(
        CaptureIsolationPayloadRole role,
        Guid selfInstanceId,
        Guid peerInstanceId,
        int protocolVersion,
        CaptureIsolationSchemaVersion schemaVersion,
        CaptureIsolationArchitecture architecture,
        string packageManifestDigest,
        string payloadDigest)
    {
        Role = role;
        SelfInstanceId = selfInstanceId;
        PeerInstanceId = peerInstanceId;
        ProtocolVersion = protocolVersion;
        SchemaVersion = schemaVersion;
        Architecture = architecture;
        PackageManifestDigest = packageManifestDigest;
        PayloadDigest = payloadDigest;
    }

    public CaptureIsolationPayloadRole Role { get; }
    public Guid SelfInstanceId { get; }
    public Guid PeerInstanceId { get; }
    public int ProtocolVersion { get; }
    public CaptureIsolationSchemaVersion SchemaVersion { get; }
    public CaptureIsolationArchitecture Architecture { get; }
    public string PackageManifestDigest { get; }
    public string PayloadDigest { get; }
}

/// <summary>
/// Privacy-safe neutral result returned by a helper transport. It contains no endpoint,
/// descriptor, launch secret, raw operating-system handle, PID, or user identity. A successful
/// outcome remains untrusted until the internal admission gate binds it to the exact retained
/// process, correlation, plan, peers, protocol, architecture, package, and limits.
/// </summary>
public sealed class HelperBootstrapObservation
{
    private readonly ReadOnlyCollection<HelperBootstrapPeerObservation> _peers;

    public HelperBootstrapObservation(
        HelperAuthenticationOutcome outcome,
        RetainedProcessIdentity process,
        CaptureCorrelation correlation,
        string planDigest,
        CaptureIsolationLimitSnapshot effectiveLimits,
        IEnumerable<HelperBootstrapPeerObservation> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        Outcome = outcome;
        Process = process;
        Correlation = correlation;
        PlanDigest = planDigest;
        EffectiveLimits = effectiveLimits;
        _peers = Array.AsReadOnly(peers.ToArray());
    }

    public HelperAuthenticationOutcome Outcome { get; }
    public RetainedProcessIdentity Process { get; }
    public CaptureCorrelation Correlation { get; }
    public string PlanDigest { get; }
    public CaptureIsolationLimitSnapshot EffectiveLimits { get; }
    public IReadOnlyList<HelperBootstrapPeerObservation> Peers => _peers;
}

public enum HelperStopOutcome
{
    Completed,
    TimedOut,
    BrokenTransport,
    Rejected,
}

public interface IHelperTransportBoundary
{
    ValueTask<HelperBootstrapObservation> AuthenticateAsync(
        RetainedProcessIdentity process,
        HelperLaunchRequest request,
        CaptureIsolationLimitSnapshot effectiveLimits,
        CancellationToken cancellationToken);

    ValueTask FenceAsync(
        RetainedProcessIdentity process,
        CancellationToken cancellationToken);

    /// <summary>
    /// Must return with no unowned operation. Implementations may retain work only through the
    /// supervisor-owned reaper/process boundary.
    /// </summary>
    ValueTask<HelperStopOutcome> StopAsync(
        RetainedProcessIdentity process,
        TimeSpan gracefulBudget,
        CancellationToken cancellationToken);
}

public sealed record HelperReapObservation(
    RetainedProcessIdentity? Process,
    RetainedJobIdentity? Job,
    bool ProcessExited,
    bool JobEmpty);

public interface IHelperReaper : IAsyncDisposable
{
    Guid Identity { get; }

    ValueTask<HelperReapObservation> ObserveAsync(
        TimeSpan waitBudget,
        CancellationToken cancellationToken);
}

public sealed class HelperBoundaryException : Exception
{
    public HelperBoundaryException(string failureCode)
        : base("A managed helper boundary operation failed.")
    {
        if (!IsolationValueValidation.IsIdentifier(failureCode))
            throw new ArgumentException("A bounded failure code is required.", nameof(failureCode));
        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}

public enum SupervisorState
{
    Absent,
    Launching,
    Authenticating,
    Assigned,
    Fenced,
    Draining,
    Terminating,
    CleanupPending,
    Reaped,
}

public enum SupervisorStartOutcome
{
    Assigned,
    Rejected,
    FailedAndReaped,
    CleanupPending,
}

public sealed record SupervisorStartResult(
    SupervisorStartOutcome Outcome,
    SupervisorState State,
    string? FailureCode = null,
    string? CleanupFailureCode = null);

public enum SupervisorCleanupDisposition
{
    Completed,
    CleanupPending,
    Rejected,
}

public sealed record SupervisorCleanupResult(
    SupervisorCleanupDisposition Disposition,
    SupervisorState State,
    CaptureCleanupCompletion? Completion = null,
    string? FailureCode = null);

internal static class IsolationValueValidation
{
    public static bool IsIdentifier(string? value) =>
        value is { Length: >= 1 and <= 96 } && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    public static bool IsSha256Digest(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    public static bool IsCorrelationValid(CaptureCorrelation? correlation) =>
        correlation is not null &&
        correlation.ParentInstanceId != Guid.Empty &&
        correlation.HelperInstanceId != Guid.Empty &&
        correlation.ParentInstanceId != correlation.HelperInstanceId &&
        correlation.SessionId != Guid.Empty &&
        correlation.RequestId != Guid.Empty &&
        correlation.CommandId != Guid.Empty &&
        correlation.Generation > 0 &&
        correlation.CommandSequence > 0 &&
        IsSha256Digest(correlation.PlanDigest);
}
