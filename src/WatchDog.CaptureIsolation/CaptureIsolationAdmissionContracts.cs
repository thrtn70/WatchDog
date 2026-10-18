using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using WatchDog.CaptureProtocol;

[assembly: InternalsVisibleTo("WatchDog.Core.Tests")]

namespace WatchDog.CaptureIsolation;

public enum CaptureIsolationAdmissionOutcome
{
    Admitted = 1,
    Rejected = 2,
}

public enum CaptureIsolationAdmissionFailure
{
    None = 0,
    PolicyMissing,
    PolicyInvalid,
    BudgetMissing,
    BudgetMismatch,
    LimitMissing,
    LimitMismatch,
    LaunchMismatch,
    PackageUnverified,
    PackageMismatch,
    MediaRootUnverified,
    PriorOwnershipPending,
    ContainmentUnverified,
    PeerMismatch,
    BootstrapUnverified,
    ProtocolMismatch,
}

public enum CaptureIsolationPolicyRevision
{
    ManagedV1 = 1,
}

public enum CaptureIsolationSchemaVersion
{
    V1 = 1,
}

public enum CaptureIsolationArchitecture
{
    X64 = 1,
    Arm64 = 2,
}

public enum CaptureIsolationEvidenceProvenance
{
    TrustedPolicyBoundary = 1,
    TrustedPackageBoundary = 2,
    AdmittedMediaRootBoundary = 3,
    JobMembershipBoundary = 4,
    AuthenticatedTransportBoundary = 5,
}

public enum CaptureIsolationPublisherAuthenticity
{
    TrustedPublisherBoundary = 1,
}

public enum CaptureIsolationPayloadRole
{
    Parent = 1,
    Helper = 2,
}

public enum CaptureIsolationBootstrapStatus
{
    Authenticated = 1,
    Rejected = 2,
}

public sealed class CaptureIsolationBudgetSnapshot
{
    internal CaptureIsolationBudgetSnapshot(
        TimeSpan launchBudget,
        TimeSpan assignmentBudget,
        TimeSpan bootstrapBudget,
        TimeSpan fenceBudget,
        TimeSpan gracefulStopBudget,
        TimeSpan reapObservationBudget)
    {
        LaunchBudget = launchBudget;
        AssignmentBudget = assignmentBudget;
        BootstrapBudget = bootstrapBudget;
        FenceBudget = fenceBudget;
        GracefulStopBudget = gracefulStopBudget;
        ReapObservationBudget = reapObservationBudget;
    }

    public TimeSpan LaunchBudget { get; }
    public TimeSpan AssignmentBudget { get; }
    public TimeSpan BootstrapBudget { get; }
    public TimeSpan FenceBudget { get; }
    public TimeSpan GracefulStopBudget { get; }
    public TimeSpan ReapObservationBudget { get; }
}

public sealed class CaptureIsolationLimitSnapshot
{
    public CaptureIsolationLimitSnapshot(
        int maxControlFrameBytes,
        int maxPayloadBytes,
        int maxPendingCommands,
        int maxTelemetrySamples)
    {
        if (maxControlFrameBytes <= AuthenticatedProtocolSession.FrameOverheadBytes ||
            maxPayloadBytes <= 0 ||
            maxPayloadBytes > maxControlFrameBytes - AuthenticatedProtocolSession.FrameOverheadBytes ||
            maxPendingCommands <= 0 || maxPendingCommands > maxPayloadBytes ||
            maxTelemetrySamples <= 0 || maxTelemetrySamples > maxPayloadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxControlFrameBytes));
        }
        MaxControlFrameBytes = maxControlFrameBytes;
        MaxPayloadBytes = maxPayloadBytes;
        MaxPendingCommands = maxPendingCommands;
        MaxTelemetrySamples = maxTelemetrySamples;
    }

    public int MaxControlFrameBytes { get; }
    public int MaxPayloadBytes { get; }
    public int MaxPendingCommands { get; }
    public int MaxTelemetrySamples { get; }
}

public sealed class CaptureIsolationPolicyEvidence
{
    internal CaptureIsolationPolicyEvidence(
        CaptureIsolationEvidenceProvenance provenance,
        CaptureIsolationPolicyRevision revision,
        CaptureIsolationSchemaVersion schemaVersion,
        CaptureIsolationArchitecture architecture,
        CaptureIsolationBudgetSnapshot? budgets,
        CaptureIsolationLimitSnapshot? limits)
    {
        Provenance = provenance;
        Revision = revision;
        SchemaVersion = schemaVersion;
        Architecture = architecture;
        Budgets = budgets;
        Limits = limits;
    }

    public CaptureIsolationEvidenceProvenance Provenance { get; }
    public CaptureIsolationPolicyRevision Revision { get; }
    public CaptureIsolationSchemaVersion SchemaVersion { get; }
    public CaptureIsolationArchitecture Architecture { get; }
    public CaptureIsolationBudgetSnapshot? Budgets { get; }
    public CaptureIsolationLimitSnapshot? Limits { get; }
}

public sealed class CaptureIsolationLaunchPlan
{
    internal CaptureIsolationLaunchPlan(
        CaptureCorrelation correlation,
        int protocolVersion,
        CaptureIsolationSchemaVersion schemaVersion,
        CaptureIsolationArchitecture architecture,
        string packageManifestDigest,
        string parentPayloadDigest,
        string helperPayloadDigest,
        Guid mediaRootIdentity)
    {
        Correlation = correlation;
        ProtocolVersion = protocolVersion;
        SchemaVersion = schemaVersion;
        Architecture = architecture;
        PackageManifestDigest = packageManifestDigest;
        ParentPayloadDigest = parentPayloadDigest;
        HelperPayloadDigest = helperPayloadDigest;
        MediaRootIdentity = mediaRootIdentity;
    }

    public CaptureCorrelation Correlation { get; }
    public int ProtocolVersion { get; }
    public CaptureIsolationSchemaVersion SchemaVersion { get; }
    public CaptureIsolationArchitecture Architecture { get; }
    public string PackageManifestDigest { get; }
    public string ParentPayloadDigest { get; }
    public string HelperPayloadDigest { get; }
    public Guid MediaRootIdentity { get; }
}

public sealed class CaptureIsolationPayloadEvidence
{
    internal CaptureIsolationPayloadEvidence(
        CaptureIsolationPayloadRole role,
        Guid instanceId,
        int protocolVersion,
        CaptureIsolationSchemaVersion schemaVersion,
        CaptureIsolationArchitecture architecture,
        string payloadDigest)
    {
        Role = role;
        InstanceId = instanceId;
        ProtocolVersion = protocolVersion;
        SchemaVersion = schemaVersion;
        Architecture = architecture;
        PayloadDigest = payloadDigest;
    }

    public CaptureIsolationPayloadRole Role { get; }
    public Guid InstanceId { get; }
    public int ProtocolVersion { get; }
    public CaptureIsolationSchemaVersion SchemaVersion { get; }
    public CaptureIsolationArchitecture Architecture { get; }
    public string PayloadDigest { get; }
}

public sealed class CaptureIsolationPackageEvidence
{
    private readonly ReadOnlyCollection<CaptureIsolationPayloadEvidence> _payloads;

    internal CaptureIsolationPackageEvidence(
        CaptureIsolationEvidenceProvenance provenance,
        CaptureIsolationPublisherAuthenticity publisherAuthenticity,
        CaptureCorrelation correlation,
        string planDigest,
        string packageManifestDigest,
        IEnumerable<CaptureIsolationPayloadEvidence> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        Provenance = provenance;
        PublisherAuthenticity = publisherAuthenticity;
        Correlation = correlation;
        PlanDigest = planDigest;
        PackageManifestDigest = packageManifestDigest;
        _payloads = Array.AsReadOnly(payloads.ToArray());
    }

    public CaptureIsolationEvidenceProvenance Provenance { get; }
    public CaptureIsolationPublisherAuthenticity PublisherAuthenticity { get; }
    public CaptureCorrelation Correlation { get; }
    public string PlanDigest { get; }
    public string PackageManifestDigest { get; }
    public IReadOnlyList<CaptureIsolationPayloadEvidence> Payloads => _payloads;
}

public sealed class CaptureIsolationMediaRootEvidence
{
    internal CaptureIsolationMediaRootEvidence(
        CaptureIsolationEvidenceProvenance provenance,
        CaptureCorrelation correlation,
        Guid mediaRootIdentity,
        string planDigest,
        string packageManifestDigest)
    {
        Provenance = provenance;
        Correlation = correlation;
        MediaRootIdentity = mediaRootIdentity;
        PlanDigest = planDigest;
        PackageManifestDigest = packageManifestDigest;
    }

    public CaptureIsolationEvidenceProvenance Provenance { get; }
    public CaptureCorrelation Correlation { get; }
    public Guid MediaRootIdentity { get; }
    public string PlanDigest { get; }
    public string PackageManifestDigest { get; }
}

public sealed class CaptureIsolationPreflightEvidence
{
    internal CaptureIsolationPreflightEvidence(
        CaptureIsolationPolicyEvidence? policy,
        CaptureIsolationLaunchPlan? launchPlan,
        CaptureIsolationPackageEvidence? package,
        CaptureIsolationMediaRootEvidence? mediaRoot)
    {
        Policy = policy;
        LaunchPlan = launchPlan;
        Package = package;
        MediaRoot = mediaRoot;
    }

    public CaptureIsolationPolicyEvidence? Policy { get; }
    public CaptureIsolationLaunchPlan? LaunchPlan { get; }
    public CaptureIsolationPackageEvidence? Package { get; }
    public CaptureIsolationMediaRootEvidence? MediaRoot { get; }
}

public sealed class CaptureIsolationPreflightAdmission
{
    internal CaptureIsolationPreflightAdmission(
        CaptureIsolationPolicyEvidence policy,
        CaptureIsolationLaunchPlan launchPlan,
        CaptureIsolationPackageEvidence package,
        CaptureIsolationMediaRootEvidence mediaRoot)
    {
        Policy = policy;
        LaunchPlan = launchPlan;
        Package = package;
        MediaRoot = mediaRoot;
    }

    public CaptureIsolationPolicyEvidence Policy { get; }
    public CaptureIsolationLaunchPlan LaunchPlan { get; }
    public CaptureIsolationPackageEvidence Package { get; }
    public CaptureIsolationMediaRootEvidence MediaRoot { get; }
}

public sealed class CaptureIsolationPreflightDecision
{
    internal CaptureIsolationPreflightDecision(
        CaptureIsolationAdmissionOutcome outcome,
        CaptureIsolationAdmissionFailure failure,
        string? failureCode,
        CaptureIsolationPreflightAdmission? admission)
    {
        Outcome = outcome;
        Failure = failure;
        FailureCode = failureCode;
        Admission = admission;
    }

    public CaptureIsolationAdmissionOutcome Outcome { get; }
    public CaptureIsolationAdmissionFailure Failure { get; }
    public string? FailureCode { get; }
    public CaptureIsolationPreflightAdmission? Admission { get; }
}

public sealed class CaptureIsolationContainmentEvidence
{
    private readonly ReadOnlyCollection<RetainedProcessIdentity> _members;

    internal CaptureIsolationContainmentEvidence(
        CaptureIsolationEvidenceProvenance provenance,
        CaptureCorrelation correlation,
        RetainedJobIdentity job,
        IEnumerable<RetainedProcessIdentity> members,
        uint assignedProcessCount,
        uint memberProcessCount,
        HelperJobContainmentPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(members);
        Provenance = provenance;
        Correlation = correlation;
        Job = job;
        _members = Array.AsReadOnly(members.ToArray());
        AssignedProcessCount = assignedProcessCount;
        MemberProcessCount = memberProcessCount;
        Policy = policy;
    }

    public CaptureIsolationEvidenceProvenance Provenance { get; }
    public CaptureCorrelation Correlation { get; }
    public RetainedJobIdentity Job { get; }
    public IReadOnlyList<RetainedProcessIdentity> Members => _members;
    public uint AssignedProcessCount { get; }
    public uint MemberProcessCount { get; }
    public HelperJobContainmentPolicy Policy { get; }
}

public sealed class CaptureIsolationPeerEvidence
{
    internal CaptureIsolationPeerEvidence(
        CaptureIsolationPayloadRole role,
        Guid selfInstanceId,
        Guid peerInstanceId,
        int protocolVersion,
        CaptureIsolationSchemaVersion schemaVersion,
        string packageManifestDigest,
        string payloadDigest)
    {
        Role = role;
        SelfInstanceId = selfInstanceId;
        PeerInstanceId = peerInstanceId;
        ProtocolVersion = protocolVersion;
        SchemaVersion = schemaVersion;
        PackageManifestDigest = packageManifestDigest;
        PayloadDigest = payloadDigest;
    }

    public CaptureIsolationPayloadRole Role { get; }
    public Guid SelfInstanceId { get; }
    public Guid PeerInstanceId { get; }
    public int ProtocolVersion { get; }
    public CaptureIsolationSchemaVersion SchemaVersion { get; }
    public string PackageManifestDigest { get; }
    public string PayloadDigest { get; }
}

public sealed class CaptureIsolationBootstrapEvidence
{
    private readonly ReadOnlyCollection<CaptureIsolationPeerEvidence> _peers;

    internal CaptureIsolationBootstrapEvidence(
        CaptureIsolationEvidenceProvenance provenance,
        CaptureIsolationBootstrapStatus status,
        CaptureCorrelation correlation,
        string planDigest,
        CaptureIsolationLimitSnapshot effectiveLimits,
        IEnumerable<CaptureIsolationPeerEvidence> peers)
    {
        ArgumentNullException.ThrowIfNull(peers);
        Provenance = provenance;
        Status = status;
        Correlation = correlation;
        PlanDigest = planDigest;
        EffectiveLimits = effectiveLimits;
        _peers = Array.AsReadOnly(peers.ToArray());
    }

    public CaptureIsolationEvidenceProvenance Provenance { get; }
    public CaptureIsolationBootstrapStatus Status { get; }
    public CaptureCorrelation Correlation { get; }
    public string PlanDigest { get; }
    public CaptureIsolationLimitSnapshot EffectiveLimits { get; }
    public IReadOnlyList<CaptureIsolationPeerEvidence> Peers => _peers;
}

public sealed class CaptureIsolationContainmentAdmission
{
    internal CaptureIsolationContainmentAdmission(
        CaptureIsolationPreflightAdmission preflight,
        RetainedProcessIdentity process,
        RetainedJobIdentity job,
        CaptureIsolationContainmentEvidence containment)
    {
        Preflight = preflight;
        Process = process;
        Job = job;
        Containment = containment;
    }

    public CaptureIsolationPreflightAdmission Preflight { get; }
    public RetainedProcessIdentity Process { get; }
    public RetainedJobIdentity Job { get; }
    public CaptureIsolationContainmentEvidence Containment { get; }
}

public sealed class CaptureIsolationContainmentDecision
{
    internal CaptureIsolationContainmentDecision(
        CaptureIsolationAdmissionOutcome outcome,
        CaptureIsolationAdmissionFailure failure,
        string? failureCode,
        CaptureIsolationContainmentAdmission? admission)
    {
        Outcome = outcome;
        Failure = failure;
        FailureCode = failureCode;
        Admission = admission;
    }

    public CaptureIsolationAdmissionOutcome Outcome { get; }
    public CaptureIsolationAdmissionFailure Failure { get; }
    public string? FailureCode { get; }
    public CaptureIsolationContainmentAdmission? Admission { get; }
}

public sealed class CaptureIsolationAssignmentAdmission
{
    internal CaptureIsolationAssignmentAdmission(
        CaptureIsolationPreflightAdmission preflight,
        CaptureIsolationContainmentEvidence containment,
        CaptureIsolationBootstrapEvidence bootstrap)
    {
        Preflight = preflight;
        Containment = containment;
        Bootstrap = bootstrap;
    }

    public CaptureIsolationPreflightAdmission Preflight { get; }
    public CaptureIsolationContainmentEvidence Containment { get; }
    public CaptureIsolationBootstrapEvidence Bootstrap { get; }
}

public sealed class CaptureIsolationAssignmentDecision
{
    internal CaptureIsolationAssignmentDecision(
        CaptureIsolationAdmissionOutcome outcome,
        CaptureIsolationAdmissionFailure failure,
        string? failureCode,
        CaptureIsolationAssignmentAdmission? admission)
    {
        Outcome = outcome;
        Failure = failure;
        FailureCode = failureCode;
        Admission = admission;
    }

    public CaptureIsolationAdmissionOutcome Outcome { get; }
    public CaptureIsolationAdmissionFailure Failure { get; }
    public string? FailureCode { get; }
    public CaptureIsolationAssignmentAdmission? Admission { get; }
}
