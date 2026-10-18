using WatchDog.CaptureProtocol;

namespace WatchDog.CaptureIsolation;

/// <summary>
/// Managed-only admission validation over immutable evidence emitted by typed boundaries. This
/// gate performs no native, filesystem, package-signature, or media-root verification itself.
/// </summary>
internal sealed class CaptureIsolationAdmissionGate
{
    private readonly CaptureIsolationLimitSnapshot _capabilityLimits;

    internal CaptureIsolationAdmissionGate(CaptureIsolationLimitSnapshot capabilityLimits)
    {
        _capabilityLimits = capabilityLimits ??
            throw new ArgumentNullException(nameof(capabilityLimits));
    }

    internal CaptureIsolationPreflightDecision EvaluatePreflight(
        HelperLaunchRequest request,
        CaptureIsolationPreflightEvidence? evidence,
        SupervisorState currentState)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (currentState is not (SupervisorState.Absent or SupervisorState.Reaped))
            return RejectPreflight(CaptureIsolationAdmissionFailure.PriorOwnershipPending);
        if (evidence?.Policy is null)
            return RejectPreflight(CaptureIsolationAdmissionFailure.PolicyMissing);
        if (!IsValidPolicy(evidence.Policy))
            return RejectPreflight(CaptureIsolationAdmissionFailure.PolicyInvalid);
        if (!HasValidBudgets(evidence.Policy.Budgets))
            return RejectPreflight(CaptureIsolationAdmissionFailure.BudgetMissing);
        if (!HasValidLimits(evidence.Policy.Limits))
            return RejectPreflight(CaptureIsolationAdmissionFailure.LimitMissing);
        if (!FitsRuntimeCapabilities(evidence.Policy.Limits!))
            return RejectPreflight(CaptureIsolationAdmissionFailure.LimitMismatch);
        if (!IsExactLaunch(request, evidence.LaunchPlan, evidence.Policy))
            return RejectPreflight(CaptureIsolationAdmissionFailure.LaunchMismatch);
        if (!IsTrustedPackage(evidence.Package))
            return RejectPreflight(CaptureIsolationAdmissionFailure.PackageUnverified);
        if (!IsExactPackage(evidence.Package!, evidence.LaunchPlan!))
            return RejectPreflight(CaptureIsolationAdmissionFailure.PackageMismatch);
        if (!IsExactMediaRoot(evidence.MediaRoot, evidence.LaunchPlan!))
            return RejectPreflight(CaptureIsolationAdmissionFailure.MediaRootUnverified);

        return new(
            CaptureIsolationAdmissionOutcome.Admitted,
            CaptureIsolationAdmissionFailure.None,
            null,
            new(
                evidence.Policy,
                evidence.LaunchPlan!,
                evidence.Package!,
                evidence.MediaRoot!));
    }

    internal CaptureIsolationContainmentDecision EvaluateContainment(
        CaptureIsolationPreflightAdmission preflight,
        RetainedProcessIdentity process,
        RetainedJobIdentity job,
        HelperJobMembershipObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(job);

        if (!IsExactContainment(observation, preflight.LaunchPlan, process, job))
        {
            return new(
                CaptureIsolationAdmissionOutcome.Rejected,
                CaptureIsolationAdmissionFailure.ContainmentUnverified,
                FailureCode(CaptureIsolationAdmissionFailure.ContainmentUnverified),
                null);
        }
        return new(
            CaptureIsolationAdmissionOutcome.Admitted,
            CaptureIsolationAdmissionFailure.None,
            null,
            new(
                preflight,
                process,
                job,
                new CaptureIsolationContainmentEvidence(
                    CaptureIsolationEvidenceProvenance.JobMembershipBoundary,
                    preflight.LaunchPlan.Correlation,
                    job,
                    [process],
                    observation!.AssignedProcessCount,
                    observation.MemberProcessCount,
                    observation.Policy)));
    }

    internal CaptureIsolationAssignmentDecision EvaluateAssignment(
        CaptureIsolationContainmentAdmission containment,
        HelperBootstrapObservation? observation)
    {
        ArgumentNullException.ThrowIfNull(containment);
        var preflight = containment.Preflight;
        if (!IsAuthenticatedBootstrap(observation, containment.Process, preflight.LaunchPlan))
            return RejectAssignment(CaptureIsolationAdmissionFailure.BootstrapUnverified);
        if (!HasExactPeers(observation!, preflight.LaunchPlan))
            return RejectAssignment(CaptureIsolationAdmissionFailure.PeerMismatch);
        if (!HasExactProtocol(observation!, preflight.LaunchPlan))
            return RejectAssignment(CaptureIsolationAdmissionFailure.ProtocolMismatch);
        if (!HasExactReportedPackage(observation!, preflight.LaunchPlan))
            return RejectAssignment(CaptureIsolationAdmissionFailure.PackageMismatch);
        if (!HasExactEffectiveLimits(observation!, preflight.Policy.Limits!))
            return RejectAssignment(CaptureIsolationAdmissionFailure.LimitMismatch);

        var bootstrap = MintBootstrapEvidence(observation!);

        return new(
            CaptureIsolationAdmissionOutcome.Admitted,
            CaptureIsolationAdmissionFailure.None,
            null,
            new(preflight, containment.Containment, bootstrap!));
    }

    private static bool IsValidPolicy(CaptureIsolationPolicyEvidence policy) =>
        policy.Provenance == CaptureIsolationEvidenceProvenance.TrustedPolicyBoundary &&
        policy.Revision == CaptureIsolationPolicyRevision.ManagedV1 &&
        Enum.IsDefined(policy.SchemaVersion) &&
        Enum.IsDefined(policy.Architecture);

    private static bool HasValidBudgets(CaptureIsolationBudgetSnapshot? budgets) =>
        budgets is not null &&
        IsFinitePositive(budgets.LaunchBudget) &&
        IsFinitePositive(budgets.AssignmentBudget) &&
        IsFinitePositive(budgets.BootstrapBudget) &&
        IsFinitePositive(budgets.FenceBudget) &&
        IsFinitePositive(budgets.GracefulStopBudget) &&
        IsFinitePositive(budgets.ReapObservationBudget);

    private static bool HasValidLimits(CaptureIsolationLimitSnapshot? limits) =>
        limits is not null &&
        limits.MaxControlFrameBytes > AuthenticatedProtocolSession.FrameOverheadBytes &&
        limits.MaxPayloadBytes > 0 &&
        limits.MaxPayloadBytes <=
            limits.MaxControlFrameBytes - AuthenticatedProtocolSession.FrameOverheadBytes &&
        limits.MaxPendingCommands > 0 &&
        limits.MaxPendingCommands <= limits.MaxPayloadBytes &&
        limits.MaxTelemetrySamples > 0 &&
        limits.MaxTelemetrySamples <= limits.MaxPayloadBytes;

    private bool FitsRuntimeCapabilities(CaptureIsolationLimitSnapshot limits) =>
        limits.MaxControlFrameBytes <= _capabilityLimits.MaxControlFrameBytes &&
        limits.MaxPayloadBytes <= _capabilityLimits.MaxPayloadBytes &&
        limits.MaxPendingCommands <= _capabilityLimits.MaxPendingCommands &&
        limits.MaxTelemetrySamples <= _capabilityLimits.MaxTelemetrySamples;

    private static bool IsExactLaunch(
        HelperLaunchRequest request,
        CaptureIsolationLaunchPlan? plan,
        CaptureIsolationPolicyEvidence policy) =>
        plan is not null &&
        plan.Correlation is not null &&
        IsolationValueValidation.IsCorrelationValid(request.Correlation) &&
        request.Correlation.ParentInstanceId != request.Correlation.HelperInstanceId &&
        request.Correlation == plan.Correlation &&
        string.Equals(request.Correlation.PlanDigest, plan.Correlation.PlanDigest, StringComparison.Ordinal) &&
        plan.ProtocolVersion == AuthenticatedProtocolSession.ProtocolVersion &&
        plan.SchemaVersion == policy.SchemaVersion &&
        Enum.IsDefined(plan.SchemaVersion) &&
        plan.Architecture == policy.Architecture &&
        Enum.IsDefined(plan.Architecture) &&
        plan.MediaRootIdentity != Guid.Empty &&
        IsolationValueValidation.IsSha256Digest(request.PackageManifestDigest) &&
        string.Equals(request.PackageManifestDigest, plan.PackageManifestDigest, StringComparison.Ordinal) &&
        IsolationValueValidation.IsSha256Digest(plan.ParentPayloadDigest) &&
        IsolationValueValidation.IsSha256Digest(plan.HelperPayloadDigest);

    private static bool IsTrustedPackage(CaptureIsolationPackageEvidence? package) =>
        package is not null &&
        package.Provenance == CaptureIsolationEvidenceProvenance.TrustedPackageBoundary &&
        package.PublisherAuthenticity ==
            CaptureIsolationPublisherAuthenticity.TrustedPublisherBoundary;

    private static bool IsExactPackage(
        CaptureIsolationPackageEvidence package,
        CaptureIsolationLaunchPlan plan)
    {
        if (package.Correlation != plan.Correlation ||
            !string.Equals(package.PlanDigest, plan.Correlation.PlanDigest, StringComparison.Ordinal) ||
            !string.Equals(
                package.PackageManifestDigest,
                plan.PackageManifestDigest,
                StringComparison.Ordinal) ||
            package.Payloads.Count != 2)
        {
            return false;
        }

        CaptureIsolationPayloadEvidence? parent = null;
        CaptureIsolationPayloadEvidence? helper = null;
        foreach (var payload in package.Payloads)
        {
            if (payload is null || !Enum.IsDefined(payload.Role))
                return false;
            if (payload.Role == CaptureIsolationPayloadRole.Parent && parent is null)
                parent = payload;
            else if (payload.Role == CaptureIsolationPayloadRole.Helper && helper is null)
                helper = payload;
            else
                return false;
        }

        return IsExactPayload(
                parent,
                CaptureIsolationPayloadRole.Parent,
                plan.Correlation.ParentInstanceId,
                plan.ParentPayloadDigest,
                plan) &&
            IsExactPayload(
                helper,
                CaptureIsolationPayloadRole.Helper,
                plan.Correlation.HelperInstanceId,
                plan.HelperPayloadDigest,
                plan);
    }

    private static bool IsExactPayload(
        CaptureIsolationPayloadEvidence? payload,
        CaptureIsolationPayloadRole role,
        Guid instanceId,
        string digest,
        CaptureIsolationLaunchPlan plan) =>
        payload is not null &&
        payload.Role == role &&
        payload.InstanceId == instanceId &&
        payload.ProtocolVersion == plan.ProtocolVersion &&
        payload.SchemaVersion == plan.SchemaVersion &&
        payload.Architecture == plan.Architecture &&
        string.Equals(payload.PayloadDigest, digest, StringComparison.Ordinal) &&
        IsolationValueValidation.IsSha256Digest(payload.PayloadDigest);

    private static bool IsExactMediaRoot(
        CaptureIsolationMediaRootEvidence? mediaRoot,
        CaptureIsolationLaunchPlan plan) =>
        mediaRoot is not null &&
        mediaRoot.Provenance == CaptureIsolationEvidenceProvenance.AdmittedMediaRootBoundary &&
        mediaRoot.Correlation == plan.Correlation &&
        mediaRoot.MediaRootIdentity == plan.MediaRootIdentity &&
        mediaRoot.MediaRootIdentity != Guid.Empty &&
        string.Equals(mediaRoot.PlanDigest, plan.Correlation.PlanDigest, StringComparison.Ordinal) &&
        string.Equals(
            mediaRoot.PackageManifestDigest,
            plan.PackageManifestDigest,
            StringComparison.Ordinal);

    private static bool IsExactContainment(
        HelperJobMembershipObservation? observation,
        CaptureIsolationLaunchPlan plan,
        RetainedProcessIdentity process,
        RetainedJobIdentity job) =>
        observation is not null &&
        Enum.IsDefined(observation.Status) &&
        Enum.IsDefined(observation.Policy) &&
        observation.Status == HelperJobMembershipStatus.Confirmed &&
        observation.Job == job &&
        observation.Process == process &&
        observation.AssignedProcessCount == 1 &&
        observation.MemberProcessCount == 1 &&
        observation.Policy == HelperJobContainmentPolicy.KillOnJobClose &&
        job.OpaqueHandleIdentity != Guid.Empty &&
        job.HelperInstanceId == plan.Correlation.HelperInstanceId &&
        process.OpaqueHandleIdentity != Guid.Empty &&
        process.HelperInstanceId == plan.Correlation.HelperInstanceId;

    private static bool IsAuthenticatedBootstrap(
        HelperBootstrapObservation? observation,
        RetainedProcessIdentity process,
        CaptureIsolationLaunchPlan plan) =>
        observation is not null &&
        observation.Outcome == HelperAuthenticationOutcome.Authenticated &&
        observation.Process == process &&
        process.OpaqueHandleIdentity != Guid.Empty &&
        process.HelperInstanceId == plan.Correlation.HelperInstanceId &&
        observation.Correlation == plan.Correlation &&
        string.Equals(observation.PlanDigest, plan.Correlation.PlanDigest, StringComparison.Ordinal) &&
        observation.Peers.Count == 2;

    private static bool HasExactPeers(
        HelperBootstrapObservation observation,
        CaptureIsolationLaunchPlan plan)
    {
        HelperBootstrapPeerObservation? parent = null;
        HelperBootstrapPeerObservation? helper = null;
        foreach (var peer in observation.Peers)
        {
            if (peer is null || !Enum.IsDefined(peer.Role))
                return false;
            if (peer.Role == CaptureIsolationPayloadRole.Parent && parent is null)
                parent = peer;
            else if (peer.Role == CaptureIsolationPayloadRole.Helper && helper is null)
                helper = peer;
            else
                return false;
        }

        return parent is not null && helper is not null &&
            plan.Correlation.ParentInstanceId != plan.Correlation.HelperInstanceId &&
            parent.SelfInstanceId == plan.Correlation.ParentInstanceId &&
            parent.PeerInstanceId == plan.Correlation.HelperInstanceId &&
            helper.SelfInstanceId == plan.Correlation.HelperInstanceId &&
            helper.PeerInstanceId == plan.Correlation.ParentInstanceId;
    }

    private static bool HasExactProtocol(
        HelperBootstrapObservation observation,
        CaptureIsolationLaunchPlan plan) =>
        observation.Peers.All(peer =>
            peer.ProtocolVersion == AuthenticatedProtocolSession.ProtocolVersion &&
            peer.ProtocolVersion == plan.ProtocolVersion &&
            peer.SchemaVersion == plan.SchemaVersion &&
            Enum.IsDefined(peer.SchemaVersion) &&
            peer.Architecture == plan.Architecture &&
            Enum.IsDefined(peer.Architecture));

    private static bool HasExactReportedPackage(
        HelperBootstrapObservation observation,
        CaptureIsolationLaunchPlan plan)
    {
        var parent = observation.Peers.Single(peer => peer.Role == CaptureIsolationPayloadRole.Parent);
        var helper = observation.Peers.Single(peer => peer.Role == CaptureIsolationPayloadRole.Helper);
        return string.Equals(
                parent.PackageManifestDigest,
                plan.PackageManifestDigest,
                StringComparison.Ordinal) &&
            string.Equals(
                helper.PackageManifestDigest,
                plan.PackageManifestDigest,
                StringComparison.Ordinal) &&
            string.Equals(parent.PayloadDigest, plan.ParentPayloadDigest, StringComparison.Ordinal) &&
            string.Equals(helper.PayloadDigest, plan.HelperPayloadDigest, StringComparison.Ordinal);
    }

    private static bool HasExactEffectiveLimits(
        HelperBootstrapObservation observation,
        CaptureIsolationLimitSnapshot expected)
    {
        var actual = observation.EffectiveLimits;
        return actual is not null &&
            actual.MaxControlFrameBytes == expected.MaxControlFrameBytes &&
            actual.MaxPayloadBytes == expected.MaxPayloadBytes &&
            actual.MaxPendingCommands == expected.MaxPendingCommands &&
            actual.MaxTelemetrySamples == expected.MaxTelemetrySamples;
    }

    private static CaptureIsolationBootstrapEvidence MintBootstrapEvidence(
        HelperBootstrapObservation observation) =>
        new(
            CaptureIsolationEvidenceProvenance.AuthenticatedTransportBoundary,
            CaptureIsolationBootstrapStatus.Authenticated,
            observation.Correlation,
            observation.PlanDigest,
            observation.EffectiveLimits,
            observation.Peers.Select(static peer => new CaptureIsolationPeerEvidence(
                peer.Role,
                peer.SelfInstanceId,
                peer.PeerInstanceId,
                peer.ProtocolVersion,
                peer.SchemaVersion,
                peer.PackageManifestDigest,
                peer.PayloadDigest)));

    private static CaptureIsolationPreflightDecision RejectPreflight(
        CaptureIsolationAdmissionFailure failure) =>
        new(CaptureIsolationAdmissionOutcome.Rejected, failure, FailureCode(failure), null);

    private static CaptureIsolationAssignmentDecision RejectAssignment(
        CaptureIsolationAdmissionFailure failure) =>
        new(CaptureIsolationAdmissionOutcome.Rejected, failure, FailureCode(failure), null);

    private static bool IsFinitePositive(TimeSpan value) =>
        value > TimeSpan.Zero &&
        value != Timeout.InfiniteTimeSpan &&
        value.TotalMilliseconds <= uint.MaxValue - 1;

    internal static string FailureCode(CaptureIsolationAdmissionFailure failure) => failure switch
    {
        CaptureIsolationAdmissionFailure.PolicyMissing => "isolation.preflight.policy-missing",
        CaptureIsolationAdmissionFailure.PolicyInvalid => "isolation.preflight.policy-invalid",
        CaptureIsolationAdmissionFailure.BudgetMissing => "isolation.preflight.budget-missing",
        CaptureIsolationAdmissionFailure.BudgetMismatch => "isolation.preflight.budget-mismatch",
        CaptureIsolationAdmissionFailure.LimitMissing => "isolation.preflight.limit-missing",
        CaptureIsolationAdmissionFailure.LimitMismatch => "isolation.admission.limit-mismatch",
        CaptureIsolationAdmissionFailure.LaunchMismatch => "isolation.preflight.launch-mismatch",
        CaptureIsolationAdmissionFailure.PackageUnverified => "isolation.preflight.package-unverified",
        CaptureIsolationAdmissionFailure.PackageMismatch => "isolation.admission.package-mismatch",
        CaptureIsolationAdmissionFailure.MediaRootUnverified =>
            "isolation.preflight.media-root-unverified",
        CaptureIsolationAdmissionFailure.PriorOwnershipPending =>
            "isolation.preflight.prior-ownership-pending",
        CaptureIsolationAdmissionFailure.ContainmentUnverified =>
            "isolation.assignment.containment-unverified",
        CaptureIsolationAdmissionFailure.PeerMismatch => "isolation.assignment.peer-mismatch",
        CaptureIsolationAdmissionFailure.BootstrapUnverified =>
            "isolation.assignment.bootstrap-unverified",
        CaptureIsolationAdmissionFailure.ProtocolMismatch =>
            "isolation.assignment.protocol-mismatch",
        _ => "isolation.admission.rejected",
    };
}
