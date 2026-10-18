using WatchDog.CaptureIsolation;
using WatchDog.CaptureProtocol;

namespace WatchDog.Core.Tests.Isolation;

public sealed class CaptureIsolationAdmissionTests
{
    private readonly CaptureIsolationAdmissionGate _gate =
        new(CaptureIsolationAdmissionFixture.Limits());

    [Fact]
    public void MissingPolicyFailsClosed()
    {
        var correlation = ProtocolFramingTests.Correlation();
        var valid = CaptureIsolationAdmissionFixture.Preflight(correlation);
        var evidence = new CaptureIsolationPreflightEvidence(
            null,
            valid.LaunchPlan,
            valid.Package,
            valid.MediaRoot);

        var decision = _gate.EvaluatePreflight(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            evidence,
            SupervisorState.Absent);

        Assert.Equal(CaptureIsolationAdmissionOutcome.Rejected, decision.Outcome);
        Assert.Equal(CaptureIsolationAdmissionFailure.PolicyMissing, decision.Failure);
        Assert.Equal("isolation.preflight.policy-missing", decision.FailureCode);
        Assert.Null(decision.Admission);
    }

    [Theory]
    [InlineData(CaptureIsolationEvidenceProvenance.TrustedPackageBoundary)]
    [InlineData((CaptureIsolationEvidenceProvenance)999)]
    public void InvalidOrUnverifiedPolicyFailsClosed(CaptureIsolationEvidenceProvenance provenance)
    {
        var correlation = ProtocolFramingTests.Correlation();
        var valid = CaptureIsolationAdmissionFixture.Preflight(correlation);
        var evidence = new CaptureIsolationPreflightEvidence(
            CaptureIsolationAdmissionFixture.Policy(provenance: provenance),
            valid.LaunchPlan,
            valid.Package,
            valid.MediaRoot);

        var decision = _gate.EvaluatePreflight(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            evidence,
            SupervisorState.Absent);

        Assert.Equal(CaptureIsolationAdmissionFailure.PolicyInvalid, decision.Failure);
    }

    [Fact]
    public void MissingExplicitBudgetsFailsClosed()
    {
        var correlation = ProtocolFramingTests.Correlation();
        var valid = CaptureIsolationAdmissionFixture.Preflight(correlation);
        var evidence = new CaptureIsolationPreflightEvidence(
            CaptureIsolationAdmissionFixture.PolicyWithoutBudgets(),
            valid.LaunchPlan,
            valid.Package,
            valid.MediaRoot);

        var decision = _gate.EvaluatePreflight(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            evidence,
            SupervisorState.Absent);

        Assert.Equal(CaptureIsolationAdmissionFailure.BudgetMissing, decision.Failure);
        Assert.Equal("isolation.preflight.budget-missing", decision.FailureCode);
    }

    [Fact]
    public void ForeignPreflightEvidenceCannotBeCombined()
    {
        var correlation = ProtocolFramingTests.Correlation();
        var foreign = correlation with { RequestId = Guid.NewGuid() };
        var valid = CaptureIsolationAdmissionFixture.Preflight(correlation);
        var launch = CaptureIsolationAdmissionFixture.Launch(correlation);

        var foreignLaunch = _gate.EvaluatePreflight(
            launch,
            new(
                valid.Policy,
                CaptureIsolationAdmissionFixture.Plan(foreign),
                valid.Package,
                valid.MediaRoot),
            SupervisorState.Absent);
        var foreignPackage = _gate.EvaluatePreflight(
            launch,
            new(
                valid.Policy,
                valid.LaunchPlan,
                CaptureIsolationAdmissionFixture.Package(foreign),
                valid.MediaRoot),
            SupervisorState.Absent);
        var foreignRoot = _gate.EvaluatePreflight(
            launch,
            new(
                valid.Policy,
                valid.LaunchPlan,
                valid.Package,
                CaptureIsolationAdmissionFixture.MediaRoot(foreign)),
            SupervisorState.Absent);

        Assert.Equal(CaptureIsolationAdmissionFailure.LaunchMismatch, foreignLaunch.Failure);
        Assert.Equal(CaptureIsolationAdmissionFailure.PackageMismatch, foreignPackage.Failure);
        Assert.Equal(CaptureIsolationAdmissionFailure.MediaRootUnverified, foreignRoot.Failure);
    }

    [Fact]
    public void UntrustedPackageCannotBorrowMatchingPeerReportedDigests()
    {
        var correlation = ProtocolFramingTests.Correlation();
        var valid = CaptureIsolationAdmissionFixture.Preflight(correlation);
        var package = CaptureIsolationAdmissionFixture.Package(
            correlation,
            publisherAuthenticity: (CaptureIsolationPublisherAuthenticity)999);

        var decision = _gate.EvaluatePreflight(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            new(valid.Policy, valid.LaunchPlan, package, valid.MediaRoot),
            SupervisorState.Absent);

        Assert.Equal(CaptureIsolationAdmissionFailure.PackageUnverified, decision.Failure);
    }

    [Fact]
    public void PriorOwnedCleanupFailsPreflight()
    {
        var correlation = ProtocolFramingTests.Correlation();

        var decision = _gate.EvaluatePreflight(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            CaptureIsolationAdmissionFixture.Preflight(correlation),
            SupervisorState.CleanupPending);

        Assert.Equal(CaptureIsolationAdmissionFailure.PriorOwnershipPending, decision.Failure);
    }

    [Fact]
    public void ForeignContainmentOrPeerEvidenceCannotBeCombined()
    {
        var correlation = ProtocolFramingTests.Correlation();
        var preflight = Assert.IsType<CaptureIsolationPreflightAdmission>(
            _gate.EvaluatePreflight(
                CaptureIsolationAdmissionFixture.Launch(correlation),
                CaptureIsolationAdmissionFixture.Preflight(correlation),
                SupervisorState.Absent).Admission);
        var process = CaptureIsolationAdmissionFixture.Process(correlation);
        var job = CaptureIsolationAdmissionFixture.Job(correlation);
        var peers = CaptureIsolationAdmissionFixture.ObservedPeers(correlation).ToArray();
        var helper = peers[1];
        peers[1] = new HelperBootstrapPeerObservation(
            helper.Role,
            helper.SelfInstanceId,
            Guid.NewGuid(),
            helper.ProtocolVersion,
            helper.SchemaVersion,
            helper.Architecture,
            helper.PackageManifestDigest,
            helper.PayloadDigest);

        var foreignContainment = _gate.EvaluateContainment(
            preflight,
            process,
            job,
            CaptureIsolationAdmissionFixture.Membership(
                process,
                job with { OpaqueHandleIdentity = Guid.NewGuid() }));
        var validContainment = _gate.EvaluateContainment(
            preflight,
            process,
            job,
            CaptureIsolationAdmissionFixture.Membership(process, job));
        var foreignPeers = _gate.EvaluateAssignment(
            Assert.IsType<CaptureIsolationContainmentAdmission>(validContainment.Admission),
            CaptureIsolationAdmissionFixture.BootstrapObservation(correlation, peers: peers));

        Assert.Equal(
            CaptureIsolationAdmissionFailure.ContainmentUnverified,
            foreignContainment.Failure);
        Assert.Equal(CaptureIsolationAdmissionFailure.PeerMismatch, foreignPeers.Failure);
    }

    [Theory]
    [InlineData(HelperJobMembershipStatus.NotMember, 1u, 1u,
        HelperJobContainmentPolicy.KillOnJobClose)]
    [InlineData(HelperJobMembershipStatus.Truncated, 2u, 1u,
        HelperJobContainmentPolicy.KillOnJobClose)]
    [InlineData(HelperJobMembershipStatus.ForeignMember, 1u, 1u,
        HelperJobContainmentPolicy.KillOnJobClose)]
    [InlineData(HelperJobMembershipStatus.Confirmed, 2u, 2u,
        HelperJobContainmentPolicy.KillOnJobClose)]
    [InlineData(HelperJobMembershipStatus.Confirmed, 1u, 1u,
        HelperJobContainmentPolicy.None)]
    [InlineData(HelperJobMembershipStatus.Confirmed, 1u, 1u,
        HelperJobContainmentPolicy.KillOnJobClose | HelperJobContainmentPolicy.BreakawayAllowed)]
    [InlineData(HelperJobMembershipStatus.Confirmed, 1u, 1u,
        HelperJobContainmentPolicy.KillOnJobClose |
        HelperJobContainmentPolicy.SilentBreakawayAllowed)]
    [InlineData(HelperJobMembershipStatus.Confirmed, 1u, 1u,
        HelperJobContainmentPolicy.KillOnJobClose | HelperJobContainmentPolicy.Unknown)]
    [InlineData((HelperJobMembershipStatus)999, 1u, 1u,
        HelperJobContainmentPolicy.KillOnJobClose)]
    public void NonExactMembershipOrPolicyFailsClosed(
        HelperJobMembershipStatus status,
        uint assignedCount,
        uint memberCount,
        HelperJobContainmentPolicy policy)
    {
        var correlation = ProtocolFramingTests.Correlation();
        var preflight = Assert.IsType<CaptureIsolationPreflightAdmission>(
            _gate.EvaluatePreflight(
                CaptureIsolationAdmissionFixture.Launch(correlation),
                CaptureIsolationAdmissionFixture.Preflight(correlation),
                SupervisorState.Absent).Admission);
        var process = CaptureIsolationAdmissionFixture.Process(correlation);
        var job = CaptureIsolationAdmissionFixture.Job(correlation);

        var decision = _gate.EvaluateContainment(
            preflight,
            process,
            job,
            CaptureIsolationAdmissionFixture.Membership(
                process,
                job,
                status,
                assignedCount,
                memberCount,
                policy));

        Assert.Equal(CaptureIsolationAdmissionOutcome.Rejected, decision.Outcome);
        Assert.Equal(CaptureIsolationAdmissionFailure.ContainmentUnverified, decision.Failure);
    }

    [Fact]
    public void GateMintsAndRetainsExactImmutableContainmentEvidence()
    {
        var correlation = ProtocolFramingTests.Correlation();
        var preflight = Assert.IsType<CaptureIsolationPreflightAdmission>(
            _gate.EvaluatePreflight(
                CaptureIsolationAdmissionFixture.Launch(correlation),
                CaptureIsolationAdmissionFixture.Preflight(correlation),
                SupervisorState.Absent).Admission);
        var process = CaptureIsolationAdmissionFixture.Process(correlation);
        var job = CaptureIsolationAdmissionFixture.Job(correlation);

        var decision = _gate.EvaluateContainment(
            preflight,
            process,
            job,
            CaptureIsolationAdmissionFixture.Membership(process, job));

        var admitted = Assert.IsType<CaptureIsolationContainmentAdmission>(decision.Admission);
        Assert.Equal(CaptureIsolationEvidenceProvenance.JobMembershipBoundary,
            admitted.Containment.Provenance);
        Assert.Equal(correlation, admitted.Containment.Correlation);
        Assert.Equal(job, admitted.Containment.Job);
        Assert.Equal(process, Assert.Single(admitted.Containment.Members));
        Assert.Equal(1u, admitted.Containment.AssignedProcessCount);
        Assert.Equal(1u, admitted.Containment.MemberProcessCount);
        Assert.Equal(HelperJobContainmentPolicy.KillOnJobClose,
            admitted.Containment.Policy);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<RetainedProcessIdentity>)admitted.Containment.Members).Clear());
    }

    [Theory]
    [InlineData(999, CaptureIsolationSchemaVersion.V1)]
    [InlineData(AuthenticatedProtocolSession.ProtocolVersion, (CaptureIsolationSchemaVersion)999)]
    public void UnknownProtocolAndSchemaValuesFailClosed(
        int protocolVersion,
        CaptureIsolationSchemaVersion schemaVersion)
    {
        var correlation = ProtocolFramingTests.Correlation();
        var valid = CaptureIsolationAdmissionFixture.Preflight(correlation);
        var plan = new CaptureIsolationLaunchPlan(
            correlation,
            protocolVersion,
            schemaVersion,
            CaptureIsolationArchitecture.X64,
            CaptureIsolationAdmissionFixture.PackageDigest,
            CaptureIsolationAdmissionFixture.ParentPayloadDigest,
            CaptureIsolationAdmissionFixture.HelperPayloadDigest,
            CaptureIsolationAdmissionFixture.MediaRootIdentity);
        var unknownProtocol = new CaptureIsolationPreflightEvidence(
            valid.Policy,
            plan,
            valid.Package,
            valid.MediaRoot);

        var preflight = _gate.EvaluatePreflight(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            unknownProtocol,
            SupervisorState.Absent);

        Assert.Equal(CaptureIsolationAdmissionFailure.LaunchMismatch, preflight.Failure);
    }

    [Fact]
    public void ParentAndHelperIdentitiesMustBeDistinct()
    {
        var original = ProtocolFramingTests.Correlation();
        var collapsed = original with { HelperInstanceId = original.ParentInstanceId };

        var decision = _gate.EvaluatePreflight(
            CaptureIsolationAdmissionFixture.Launch(collapsed),
            CaptureIsolationAdmissionFixture.Preflight(collapsed),
            SupervisorState.Absent);

        Assert.Equal(CaptureIsolationAdmissionFailure.LaunchMismatch, decision.Failure);
    }

    [Fact]
    public void BootstrapMustAttestExactEffectiveLimits()
    {
        var correlation = ProtocolFramingTests.Correlation();
        var preflight = Assert.IsType<CaptureIsolationPreflightAdmission>(
            _gate.EvaluatePreflight(
                CaptureIsolationAdmissionFixture.Launch(correlation),
                CaptureIsolationAdmissionFixture.Preflight(correlation),
                SupervisorState.Absent).Admission);
        var process = CaptureIsolationAdmissionFixture.Process(correlation);
        var job = CaptureIsolationAdmissionFixture.Job(correlation);
        var containment = _gate.EvaluateContainment(
            preflight,
            process,
            job,
            CaptureIsolationAdmissionFixture.Membership(process, job));

        var decision = _gate.EvaluateAssignment(
            Assert.IsType<CaptureIsolationContainmentAdmission>(containment.Admission),
            CaptureIsolationAdmissionFixture.BootstrapObservation(
                correlation,
                effectiveLimits: new CaptureIsolationLimitSnapshot(4096, 2048, 7, 32)));

        Assert.Equal(CaptureIsolationAdmissionFailure.LimitMismatch, decision.Failure);
        Assert.Equal("isolation.admission.limit-mismatch", decision.FailureCode);
    }

    [Fact]
    public void BootstrapObservationMustBindRetainedProcessAndArchitecture()
    {
        var correlation = ProtocolFramingTests.Correlation();
        var preflight = Assert.IsType<CaptureIsolationPreflightAdmission>(
            _gate.EvaluatePreflight(
                CaptureIsolationAdmissionFixture.Launch(correlation),
                CaptureIsolationAdmissionFixture.Preflight(correlation),
                SupervisorState.Absent).Admission);
        var process = CaptureIsolationAdmissionFixture.Process(correlation);
        var job = CaptureIsolationAdmissionFixture.Job(correlation);
        var containment = Assert.IsType<CaptureIsolationContainmentAdmission>(
            _gate.EvaluateContainment(
                preflight,
                process,
                job,
                CaptureIsolationAdmissionFixture.Membership(process, job)).Admission);
        var peers = CaptureIsolationAdmissionFixture.ObservedPeers(correlation).ToArray();
        var helper = peers[1];
        peers[1] = new HelperBootstrapPeerObservation(
            helper.Role,
            helper.SelfInstanceId,
            helper.PeerInstanceId,
            helper.ProtocolVersion,
            helper.SchemaVersion,
            CaptureIsolationArchitecture.Arm64,
            helper.PackageManifestDigest,
            helper.PayloadDigest);

        var foreignProcess = _gate.EvaluateAssignment(
            containment,
            CaptureIsolationAdmissionFixture.BootstrapObservation(
                correlation,
                process: process with { OpaqueHandleIdentity = Guid.NewGuid() }));
        var foreignArchitecture = _gate.EvaluateAssignment(
            containment,
            CaptureIsolationAdmissionFixture.BootstrapObservation(
                correlation,
                process: process,
                peers: peers));

        Assert.Equal(CaptureIsolationAdmissionFailure.BootstrapUnverified, foreignProcess.Failure);
        Assert.Equal(CaptureIsolationAdmissionFailure.ProtocolMismatch, foreignArchitecture.Failure);
    }

    [Fact]
    public void GateMintsTrustedBootstrapEvidenceOnlyAfterNeutralObservationAdmission()
    {
        var correlation = ProtocolFramingTests.Correlation();
        var preflight = Assert.IsType<CaptureIsolationPreflightAdmission>(
            _gate.EvaluatePreflight(
                CaptureIsolationAdmissionFixture.Launch(correlation),
                CaptureIsolationAdmissionFixture.Preflight(correlation),
                SupervisorState.Absent).Admission);
        var process = CaptureIsolationAdmissionFixture.Process(correlation);
        var job = CaptureIsolationAdmissionFixture.Job(correlation);
        var containment = Assert.IsType<CaptureIsolationContainmentAdmission>(
            _gate.EvaluateContainment(
                preflight,
                process,
                job,
                CaptureIsolationAdmissionFixture.Membership(process, job)).Admission);

        var decision = _gate.EvaluateAssignment(
            containment,
            CaptureIsolationAdmissionFixture.BootstrapObservation(
                correlation,
                process: process));

        var admission = Assert.IsType<CaptureIsolationAssignmentAdmission>(decision.Admission);
        Assert.Equal(
            CaptureIsolationEvidenceProvenance.AuthenticatedTransportBoundary,
            admission.Bootstrap.Provenance);
        Assert.Equal(CaptureIsolationBootstrapStatus.Authenticated, admission.Bootstrap.Status);
        Assert.Equal(correlation, admission.Bootstrap.Correlation);
        Assert.Equal(2, admission.Bootstrap.Peers.Count);
    }

    [Fact]
    public void RelationValidButOversizedLimitsExceedRuntimeCapability()
    {
        var correlation = ProtocolFramingTests.Correlation();
        var valid = CaptureIsolationAdmissionFixture.Preflight(correlation);
        var oversizedPayload = int.MaxValue - AuthenticatedProtocolSession.FrameOverheadBytes;
        var policy = CaptureIsolationAdmissionFixture.Policy(
            limits: new CaptureIsolationLimitSnapshot(
                int.MaxValue,
                oversizedPayload,
                oversizedPayload,
                oversizedPayload));
        var evidence = new CaptureIsolationPreflightEvidence(
            policy,
            valid.LaunchPlan,
            valid.Package,
            valid.MediaRoot);

        var decision = _gate.EvaluatePreflight(
            CaptureIsolationAdmissionFixture.Launch(correlation),
            evidence,
            SupervisorState.Absent);

        Assert.Equal(CaptureIsolationAdmissionFailure.LimitMismatch, decision.Failure);
        Assert.Equal("isolation.admission.limit-mismatch", decision.FailureCode);
    }

    [Fact]
    public void FixtureCollectionsAreDefensivelyFrozen()
    {
        var correlation = ProtocolFramingTests.Correlation();
        var payloads = CaptureIsolationAdmissionFixture.Payloads(correlation).ToList();
        var peers = CaptureIsolationAdmissionFixture.ObservedPeers(correlation).ToList();
        var package = CaptureIsolationAdmissionFixture.Package(correlation, payloads: payloads);
        var bootstrap = CaptureIsolationAdmissionFixture.BootstrapObservation(correlation, peers: peers);

        payloads.Clear();
        peers.Clear();

        Assert.Equal(2, package.Payloads.Count);
        Assert.Equal(2, bootstrap.Peers.Count);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<CaptureIsolationPayloadEvidence>)package.Payloads).Clear());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<HelperBootstrapPeerObservation>)bootstrap.Peers).Clear());
    }
}

internal static class CaptureIsolationAdmissionFixture
{
    internal static readonly Guid MediaRootIdentity =
        Guid.Parse("90000000-0000-0000-0000-000000000009");

    internal static HelperLaunchRequest Launch(CaptureCorrelation correlation) =>
        new(correlation, PackageDigest);

    internal static CaptureIsolationPreflightEvidence Preflight(CaptureCorrelation correlation) =>
        new(
            Policy(),
            Plan(correlation),
            Package(correlation),
            MediaRoot(correlation));

    internal static CaptureIsolationPolicyEvidence Policy(
        CaptureIsolationEvidenceProvenance provenance =
            CaptureIsolationEvidenceProvenance.TrustedPolicyBoundary,
        CaptureIsolationBudgetSnapshot? budgets = default,
        CaptureIsolationLimitSnapshot? limits = default) =>
        new(
            provenance,
            CaptureIsolationPolicyRevision.ManagedV1,
            CaptureIsolationSchemaVersion.V1,
            CaptureIsolationArchitecture.X64,
            budgets ?? Budgets(),
            limits ?? Limits());

    internal static CaptureIsolationPolicyEvidence PolicyWithoutBudgets() =>
        new(
            CaptureIsolationEvidenceProvenance.TrustedPolicyBoundary,
            CaptureIsolationPolicyRevision.ManagedV1,
            CaptureIsolationSchemaVersion.V1,
            CaptureIsolationArchitecture.X64,
            null,
            Limits());

    internal static CaptureIsolationBudgetSnapshot Budgets() =>
        new(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(5));

    internal static CaptureIsolationLimitSnapshot Limits() => new(4096, 2048, 8, 32);

    internal static CaptureIsolationLaunchPlan Plan(
        CaptureCorrelation correlation,
        int protocolVersion = AuthenticatedProtocolSession.ProtocolVersion) =>
        new(
            correlation,
            protocolVersion,
            CaptureIsolationSchemaVersion.V1,
            CaptureIsolationArchitecture.X64,
            PackageDigest,
            ParentPayloadDigest,
            HelperPayloadDigest,
            MediaRootIdentity);

    internal static CaptureIsolationPackageEvidence Package(
        CaptureCorrelation correlation,
        CaptureIsolationEvidenceProvenance provenance =
            CaptureIsolationEvidenceProvenance.TrustedPackageBoundary,
        CaptureIsolationPublisherAuthenticity publisherAuthenticity =
            CaptureIsolationPublisherAuthenticity.TrustedPublisherBoundary,
        IEnumerable<CaptureIsolationPayloadEvidence>? payloads = null) =>
        new(
            provenance,
            publisherAuthenticity,
            correlation,
            correlation.PlanDigest,
            PackageDigest,
            payloads ?? Payloads(correlation));

    internal static IEnumerable<CaptureIsolationPayloadEvidence> Payloads(
        CaptureCorrelation correlation) =>
    [
        new(
            CaptureIsolationPayloadRole.Parent,
            correlation.ParentInstanceId,
            AuthenticatedProtocolSession.ProtocolVersion,
            CaptureIsolationSchemaVersion.V1,
            CaptureIsolationArchitecture.X64,
            ParentPayloadDigest),
        new(
            CaptureIsolationPayloadRole.Helper,
            correlation.HelperInstanceId,
            AuthenticatedProtocolSession.ProtocolVersion,
            CaptureIsolationSchemaVersion.V1,
            CaptureIsolationArchitecture.X64,
            HelperPayloadDigest),
    ];

    internal static CaptureIsolationMediaRootEvidence MediaRoot(
        CaptureCorrelation correlation) =>
        new(
            CaptureIsolationEvidenceProvenance.AdmittedMediaRootBoundary,
            correlation,
            MediaRootIdentity,
            correlation.PlanDigest,
            PackageDigest);

    internal static RetainedProcessIdentity Process(CaptureCorrelation correlation) =>
        new(Guid.Parse("70000000-0000-0000-0000-000000000007"), correlation.HelperInstanceId);

    internal static RetainedJobIdentity Job(CaptureCorrelation correlation) =>
        new(Guid.Parse("80000000-0000-0000-0000-000000000008"), correlation.HelperInstanceId);

    internal static HelperJobMembershipObservation Membership(
        RetainedProcessIdentity process,
        RetainedJobIdentity job,
        HelperJobMembershipStatus status = HelperJobMembershipStatus.Confirmed,
        uint assignedProcessCount = 1,
        uint memberProcessCount = 1,
        HelperJobContainmentPolicy policy = HelperJobContainmentPolicy.KillOnJobClose) =>
        new(
            job,
            process,
            status,
            assignedProcessCount,
            memberProcessCount,
            policy);

    internal static HelperBootstrapObservation BootstrapObservation(
        CaptureCorrelation correlation,
        HelperAuthenticationOutcome outcome = HelperAuthenticationOutcome.Authenticated,
        RetainedProcessIdentity? process = null,
        CaptureIsolationLimitSnapshot? effectiveLimits = null,
        IEnumerable<HelperBootstrapPeerObservation>? peers = null) =>
        new(
            outcome,
            process ?? Process(correlation),
            correlation,
            correlation.PlanDigest,
            effectiveLimits ?? Limits(),
            peers ?? ObservedPeers(correlation));

    internal static IEnumerable<HelperBootstrapPeerObservation> ObservedPeers(
        CaptureCorrelation correlation) =>
    [
        new(
            CaptureIsolationPayloadRole.Parent,
            correlation.ParentInstanceId,
            correlation.HelperInstanceId,
            AuthenticatedProtocolSession.ProtocolVersion,
            CaptureIsolationSchemaVersion.V1,
            CaptureIsolationArchitecture.X64,
            PackageDigest,
            ParentPayloadDigest),
        new(
            CaptureIsolationPayloadRole.Helper,
            correlation.HelperInstanceId,
            correlation.ParentInstanceId,
            AuthenticatedProtocolSession.ProtocolVersion,
            CaptureIsolationSchemaVersion.V1,
            CaptureIsolationArchitecture.X64,
            PackageDigest,
            HelperPayloadDigest),
    ];

    internal static string PackageDigest => ProtocolFramingTests.Digest('c');
    internal static string ParentPayloadDigest => ProtocolFramingTests.Digest('d');
    internal static string HelperPayloadDigest => ProtocolFramingTests.Digest('e');
}
