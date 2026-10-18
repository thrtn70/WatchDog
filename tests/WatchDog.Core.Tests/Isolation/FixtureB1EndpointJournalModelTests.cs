using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1EndpointJournalModelTests
{
    private static readonly Guid RunNonce =
        Guid.Parse("51000000-2222-3333-4444-555555555555");
    private static readonly Guid LaunchId =
        Guid.Parse("52000000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid JobOperation =
        Guid.Parse("53000000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid ProcessOperation =
        Guid.Parse("54000000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureHandoffProcessIdentity Wrapper = new(901, 9_010);
    private static readonly FixtureHandoffProcessIdentity Launcher = new(902, 9_020);
    private static readonly FixtureHandoffProcessIdentity Runner = new(903, 9_030);
    private static readonly FixtureB1SuccessGeneration JobSuccess = Success(
        "53100000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureB1SuccessGeneration ProcessSuccess = Success(
        "54100000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureB1LeaseGeneration WrapperJobLease = Lease(
        "53200000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureB1LeaseGeneration LauncherJobLease = Lease(
        "53300000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureB1LeaseGeneration LauncherRunnerLease = Lease(
        "54200000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureB1LeaseGeneration WrapperRunnerLease = Lease(
        "54300000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid JobAck =
        Guid.Parse("55000000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid ProcessAck =
        Guid.Parse("56000000-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void SeparateWrapperAndLauncherEndpointsCompleteJobTransfer()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var launcher = Harness(FixtureB1EndpointRole.Launcher);
        Assert.NotSame(wrapper.Model, launcher.Model);
        Assert.NotSame(wrapper.Journal, launcher.Journal);

        var success = RecordJobSuccess(wrapper);
        Assert.True(launcher.Model.ObservePeerIntent(JobIntent()));
        Assert.Equal(FixtureB1EndpointInboundDisposition.Accepted,
            launcher.Model.AcceptInboundSuccess(success));
        var ack = launcher.Model.CreateAck(JobSuccess, JobAck);
        Assert.NotNull(ack);
        Assert.True(wrapper.Model.AcceptAck(ack!));

        var wrapperResult = wrapper.Model.Snapshot();
        var launcherResult = launcher.Model.Snapshot();
        Assert.Equal(1, wrapperResult.CommittedIntentCount);
        Assert.Equal(1, wrapperResult.SuccessObligationCount);
        Assert.Equal(1, wrapperResult.AcceptedAckCount);
        Assert.Equal(0, wrapperResult.AcceptedInboundCount);
        Assert.Equal(1, launcherResult.AcceptedInboundCount);
        Assert.Equal(1, launcherResult.IssuedAckCount);
        Assert.Equal(0, launcherResult.SuccessObligationCount);
        Assert.False(wrapperResult.DurabilityProven);
        Assert.False(wrapperResult.NativeAuthorityProven);
    }

    [Fact]
    public void WrapperIsNativeDuplicateInvokerForLauncherToWrapperProcessTransfer()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var launcher = Harness(FixtureB1EndpointRole.Launcher);

        var success = RecordProcessSuccess(wrapper);
        Assert.Equal(FixtureB1EndpointInboundDisposition.Accepted,
            wrapper.Model.AcceptInboundSuccess(success));
        Assert.True(launcher.Model.ObservePeerIntent(ProcessIntent()));
        Assert.True(launcher.Model.ObserveSourceSuccess(success));
        var ack = wrapper.Model.CreateAck(ProcessSuccess, ProcessAck);
        Assert.NotNull(ack);
        Assert.True(launcher.Model.AcceptAck(ack!));

        Assert.Equal(1, wrapper.Model.Snapshot().SuccessObligationCount);
        Assert.Equal(1, wrapper.Model.Snapshot().AcceptedInboundCount);
        Assert.Equal(1, launcher.Model.Snapshot().AcceptedAckCount);
    }

    [Fact]
    public void LauncherCannotReserveNativeDuplicateInvocation()
    {
        var launcher = Harness(FixtureB1EndpointRole.Launcher);

        Assert.False(launcher.Model.ReserveNativeDuplicate(JobIntent()));

        AssertRejected(launcher, FixtureB1EndpointDiagnosticCode.InvalidIntent);
        Assert.Equal(0, launcher.Model.Snapshot().CommittedIntentCount);
    }

    [Fact]
    public void PreCallIntentMustBeDurableBeforeNativeWorkCanStart()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        wrapper.Journal.FailEvent = FixtureB1EndpointJournalEvent.IntentCommitted;

        Assert.False(wrapper.Model.ReserveNativeDuplicate(JobIntent()));

        AssertRejected(wrapper, FixtureB1EndpointDiagnosticCode.JournalFailure);
        Assert.Equal(0, wrapper.Model.Snapshot().CommittedIntentCount);
    }

    [Fact]
    public void NativeSuccessJournalFailureIsUnconfirmedAndFailsClosed()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        Assert.True(wrapper.Model.ReserveNativeDuplicate(JobIntent()));
        wrapper.Journal.FailEvent =
            FixtureB1EndpointJournalEvent.SuccessObligationCommitted;

        var disposition = wrapper.Model.RecordNativeSuccess(JobReceipt());

        Assert.Equal(FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected,
            disposition);
        AssertRejected(wrapper, FixtureB1EndpointDiagnosticCode.JournalFailure);
        Assert.Equal(0, wrapper.Model.Snapshot().SuccessObligationCount);
        Assert.Equal(1, wrapper.Model.Snapshot().UnconfirmedSuccessCount);
    }

    [Fact]
    public void EmptySuccessGenerationIsDurablyInventoriedBeforeRejection()
    {
        var journal = new FakeJournal
        {
            FailEvent = FixtureB1EndpointJournalEvent.ProtocolRejected,
        };
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);
        Assert.True(wrapper.Model.ReserveNativeDuplicate(JobIntent()));
        var malformed = JobReceipt() with
        {
            SuccessGeneration = default,
        };

        Assert.Equal(FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected,
            wrapper.Model.RecordNativeSuccess(malformed));
        Assert.Equal(1, wrapper.Model.Snapshot().SuccessObligationCount);

        journal.FailEvent = null;
        var recovered = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);
        Assert.Equal(1, recovered.Model.Snapshot().SuccessObligationCount);
        Assert.Equal(1, recovered.Model.Snapshot().UnconfirmedSuccessCount);
        AssertRejected(recovered,
            FixtureB1EndpointDiagnosticCode.NativeSuccessMismatch);
    }

    [Fact]
    public void RecoveryInventoriesMalformedCommittedNativeSuccessBeforeRejecting()
    {
        var journal = new FakeJournal
        {
            FailEvent = FixtureB1EndpointJournalEvent.ProtocolRejected,
        };
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);
        Assert.True(wrapper.Model.ReserveNativeDuplicate(JobIntent()));
        var malformed = JobReceipt() with { NonInheritable = false };

        Assert.Equal(FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected,
            wrapper.Model.RecordNativeSuccess(malformed));

        journal.FailEvent = null;
        var recovered = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);
        Assert.Equal(1, recovered.Model.Snapshot().SuccessObligationCount);
        Assert.Equal(malformed, Assert.Single(
            recovered.Model.Snapshot().Records,
            record => record.Event ==
                FixtureB1EndpointJournalEvent.SuccessObligationCommitted).Success);
        AssertRejected(recovered,
            FixtureB1EndpointDiagnosticCode.NativeSuccessMismatch);
    }

    [Fact]
    public void ExactNativeSuccessCallbackReplayIsOneUseAndIdempotent()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var success = RecordJobSuccess(wrapper);

        var replay = wrapper.Model.RecordNativeSuccess(success);

        Assert.Equal(FixtureB1EndpointSuccessDisposition.ReplayIgnored, replay);
        Assert.Equal(1, wrapper.Model.Snapshot().SuccessObligationCount);
        Assert.Single(wrapper.Model.Snapshot().Records, record =>
            record.Event == FixtureB1EndpointJournalEvent.SuccessObligationCommitted);
    }

    [Fact]
    public void DistinctNativeSuccessAtReusedCoordinateIsRetainedAndFailsClosed()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var first = RecordJobSuccess(wrapper);
        var second = first with
        {
            SuccessGeneration = Success("53400000-bbbb-cccc-dddd-eeeeeeeeeeee"),
            TargetLeaseGeneration = Lease("53500000-bbbb-cccc-dddd-eeeeeeeeeeee"),
        };

        var disposition = wrapper.Model.RecordNativeSuccess(second);

        Assert.Equal(FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected,
            disposition);
        AssertRejected(wrapper, FixtureB1EndpointDiagnosticCode.NativeSuccessMismatch);
        Assert.Equal(2, wrapper.Model.Snapshot().SuccessObligationCount);
        Assert.Equal(first.TargetLocalCoordinate, second.TargetLocalCoordinate);
    }

    [Fact]
    public void DistinctNativeSuccessAliasingSuccessGenerationIsStillJournaled()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var first = RecordJobSuccess(wrapper);
        var second = first with
        {
            TargetLeaseGeneration = Lease(
                "53600000-bbbb-cccc-dddd-eeeeeeeeeeee"),
            TargetLocalCoordinate = first.TargetLocalCoordinate + 1,
            ResourceReceipt = first.ResourceReceipt + 1,
        };

        var disposition = wrapper.Model.RecordNativeSuccess(second);

        Assert.Equal(FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected,
            disposition);
        AssertRejected(wrapper,
            FixtureB1EndpointDiagnosticCode.SuccessGenerationAlias);
        Assert.Equal(2, wrapper.Model.Snapshot().SuccessObligationCount);
        Assert.Equal(1, wrapper.Model.Snapshot().ValidatedSuccessCount);
    }

    [Fact]
    public void CrashAfterIntentIsAmbiguousUntilExactNativeReconciliation()
    {
        var journal = new FakeJournal();
        var first = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);
        Assert.True(first.Model.ReserveNativeDuplicate(JobIntent()));

        var recovered = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);

        Assert.Equal(1, recovered.Model.Snapshot().CommittedIntentCount);
        Assert.Equal(0, recovered.Model.Snapshot().SuccessObligationCount);
        AssertRejected(recovered,
            FixtureB1EndpointDiagnosticCode.RecoveryAmbiguous);
    }

    [Fact]
    public void CrashAfterSuccessRecoversObligationAndSuppressesCallbackReplay()
    {
        var journal = new FakeJournal();
        var first = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);
        var success = RecordJobSuccess(first);

        var recovered = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);
        var replay = recovered.Model.RecordNativeSuccess(success);

        Assert.Equal(FixtureB1EndpointSuccessDisposition.ReplayIgnored, replay);
        Assert.Equal(1, recovered.Model.Snapshot().SuccessObligationCount);
    }

    [Fact]
    public void CrashAfterObligationBeforeValidationRecoversFailClosed()
    {
        var journal = new FakeJournal
        {
            FailEvent = FixtureB1EndpointJournalEvent.SuccessValidated,
        };
        var first = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);
        Assert.True(first.Model.ReserveNativeDuplicate(JobIntent()));
        Assert.Equal(FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected,
            first.Model.RecordNativeSuccess(JobReceipt()));

        journal.FailEvent = null;
        var recovered = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);

        AssertRejected(recovered,
            FixtureB1EndpointDiagnosticCode.RecoveryAmbiguous);
        Assert.Equal(1, recovered.Model.Snapshot().SuccessObligationCount);
        Assert.Equal(0, recovered.Model.Snapshot().ValidatedSuccessCount);
        Assert.Equal(FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected,
            recovered.Model.RecordNativeSuccess(JobReceipt()));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("order")]
    [InlineData("binding")]
    [InlineData("conflict")]
    public void RecoveryRejectsMalformedOrConflictingJournalStream(string caseId)
    {
        var journal = new FakeJournal();
        var seed = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);
        Assert.True(seed.Model.ReserveNativeDuplicate(JobIntent()));
        var original = Assert.Single(journal.ReadCommitted(
            FixtureB1EndpointRole.Wrapper));
        var records = caseId switch
        {
            "gap" => new[] { original with { Ordinal = 2 } },
            "order" => new[]
            {
                original with { Ordinal = 2 },
                original with { Ordinal = 1 },
            },
            "binding" => new[]
            {
                original with
                {
                    Intent = original.Intent! with
                    {
                        Binding = Binding() with { ConnectionReceipt = 8_002 },
                    },
                },
            },
            "conflict" => new[]
            {
                original,
                original with { Ordinal = 2 },
            },
            _ => throw new ArgumentOutOfRangeException(nameof(caseId)),
        };
        journal.Replace(FixtureB1EndpointRole.Wrapper, records);

        var recovered = Harness(FixtureB1EndpointRole.Wrapper, journal: journal);

        AssertRejected(recovered, FixtureB1EndpointDiagnosticCode.RecoveryCorrupt);
    }

    [Fact]
    public void LostAckStaysRejectedAndLateAckCannotAdvanceSourceEndpoint()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var launcher = Harness(FixtureB1EndpointRole.Launcher);
        var success = RecordJobSuccess(wrapper);
        Assert.True(launcher.Model.ObservePeerIntent(JobIntent()));
        Assert.Equal(FixtureB1EndpointInboundDisposition.Accepted,
            launcher.Model.AcceptInboundSuccess(success));
        var ack = launcher.Model.CreateAck(JobSuccess, JobAck)!;

        Assert.True(wrapper.Model.ReportAckLost(success));
        Assert.False(wrapper.Model.AcceptAck(ack));

        AssertRejected(wrapper, FixtureB1EndpointDiagnosticCode.AckLost);
        Assert.Contains(wrapper.Model.Snapshot().Records,
            record => record.Event == FixtureB1EndpointJournalEvent.AckLost);
    }

    [Fact]
    public void ReplayedAckFailsClosedAfterFirstAcceptance()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var launcher = Harness(FixtureB1EndpointRole.Launcher);
        var success = RecordJobSuccess(wrapper);
        Assert.True(launcher.Model.ObservePeerIntent(JobIntent()));
        Assert.Equal(FixtureB1EndpointInboundDisposition.Accepted,
            launcher.Model.AcceptInboundSuccess(success));
        var ack = launcher.Model.CreateAck(JobSuccess, JobAck)!;
        Assert.True(wrapper.Model.AcceptAck(ack));

        Assert.False(wrapper.Model.AcceptAck(ack));

        AssertRejected(wrapper, FixtureB1EndpointDiagnosticCode.AckReplay);
    }

    [Fact]
    public void FabricatedAckWithoutLocalExactSuccessCannotAdvanceEndpoint()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var success = JobReceipt();
        var ack = AckFor(success, JobAck);

        Assert.False(wrapper.Model.AcceptAck(ack));

        AssertRejected(wrapper,
            FixtureB1EndpointDiagnosticCode.AckWithoutSuccess);
        Assert.Equal(0, wrapper.Model.Snapshot().AcceptedAckCount);
    }

    [Fact]
    public void WrapperCannotUseSourceObservationToBypassNativeSuccessJournal()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var success = JobReceipt();

        Assert.False(wrapper.Model.ObserveSourceSuccess(success));
        Assert.False(wrapper.Model.AcceptAck(AckFor(success, JobAck)));

        AssertRejected(wrapper, FixtureB1EndpointDiagnosticCode.PeerDrift);
        Assert.DoesNotContain(wrapper.Model.Snapshot().Records,
            record => record.Event ==
                FixtureB1EndpointJournalEvent.SourceSuccessObserved);
        Assert.Equal(0, wrapper.Model.Snapshot().AcceptedAckCount);
    }

    [Fact]
    public void SuccessPeerDriftIsProviderRetainedByReceivingEndpoint()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var launcher = Harness(FixtureB1EndpointRole.Launcher);
        var success = RecordJobSuccess(wrapper) with
        {
            Binding = Binding() with { ConnectionReceipt = 8_002 },
        };
        Assert.True(launcher.Model.ObservePeerIntent(JobIntent()));

        var disposition = launcher.Model.AcceptInboundSuccess(success);

        Assert.Equal(
            FixtureB1EndpointInboundDisposition.ProviderRetainsRejectedResource,
            disposition);
        AssertRejected(launcher,
            FixtureB1EndpointDiagnosticCode.InboundAuthorityRejected);
        Assert.Equal(1, launcher.Model.Snapshot().ProviderRetainedInboundCount);
    }

    [Fact]
    public void WrongKindOrSequenceInboundSuccessCannotBeAcknowledged()
    {
        var launcher = Harness(FixtureB1EndpointRole.Launcher);
        Assert.True(launcher.Model.ObservePeerIntent(JobIntent()));
        var malformed = JobReceipt() with
        {
            ResourceKind = FixtureB1OwnerResourceKind.RunnerProcess,
            ExactRunner = Runner,
            Sequence = 2,
        };

        var disposition = launcher.Model.AcceptInboundSuccess(malformed);

        Assert.Equal(
            FixtureB1EndpointInboundDisposition.ProviderRetainsRejectedResource,
            disposition);
        AssertRejected(launcher,
            FixtureB1EndpointDiagnosticCode.InboundAuthorityRejected);
        Assert.Null(launcher.Model.CreateAck(JobSuccess, JobAck));
    }

    [Fact]
    public void InboundSourceLeaseMustMatchAuthenticatedPeerIntent()
    {
        var launcher = Harness(FixtureB1EndpointRole.Launcher);
        Assert.True(launcher.Model.ObservePeerIntent(JobIntent()));
        var wrongLease = JobReceipt() with
        {
            SourceLeaseGeneration = Lease(
                "53700000-bbbb-cccc-dddd-eeeeeeeeeeee"),
        };

        var disposition = launcher.Model.AcceptInboundSuccess(wrongLease);

        Assert.Equal(
            FixtureB1EndpointInboundDisposition.ProviderRetainsRejectedResource,
            disposition);
        AssertRejected(launcher,
            FixtureB1EndpointDiagnosticCode.InboundAuthorityRejected);
        Assert.Null(launcher.Model.CreateAck(JobSuccess, JobAck));
    }

    [Fact]
    public void WrapperCannotAcknowledgeProcessWithoutOwnValidatedSuccess()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);

        var disposition = wrapper.Model.AcceptInboundSuccess(ProcessReceipt());

        Assert.Equal(
            FixtureB1EndpointInboundDisposition.ProviderRetainsRejectedResource,
            disposition);
        AssertRejected(wrapper,
            FixtureB1EndpointDiagnosticCode.InboundAuthorityRejected);
        Assert.Null(wrapper.Model.CreateAck(ProcessSuccess, ProcessAck));
    }

    [Fact]
    public void AckPeerDriftFailsClosed()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var launcher = Harness(FixtureB1EndpointRole.Launcher);
        var success = RecordJobSuccess(wrapper);
        Assert.True(launcher.Model.ObservePeerIntent(JobIntent()));
        Assert.Equal(FixtureB1EndpointInboundDisposition.Accepted,
            launcher.Model.AcceptInboundSuccess(success));
        var ack = (launcher.Model.CreateAck(JobSuccess, JobAck)!) with
        {
            Source = Launcher,
            Target = Wrapper,
        };

        Assert.False(wrapper.Model.AcceptAck(ack));

        AssertRejected(wrapper, FixtureB1EndpointDiagnosticCode.PeerDrift);
    }

    [Fact]
    public void LateSuccessAfterStopStillCreatesDurableObligation()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        Assert.True(wrapper.Model.ReserveNativeDuplicate(JobIntent()));
        Assert.True(wrapper.Model.Stop());

        var disposition = wrapper.Model.RecordNativeSuccess(JobReceipt());

        Assert.Equal(FixtureB1EndpointSuccessDisposition.ObligationRetainedRejected,
            disposition);
        Assert.Equal(1, wrapper.Model.Snapshot().SuccessObligationCount);
        AssertRejected(wrapper, FixtureB1EndpointDiagnosticCode.Stopped);
    }

    [Fact]
    public void EndpointSuccessProjectsIntoExistingJointOwnerOracle()
    {
        var wrapper = Harness(FixtureB1EndpointRole.Wrapper);
        var launcher = Harness(FixtureB1EndpointRole.Launcher);
        var intent = JobIntent();
        Assert.True(wrapper.Model.ReserveNativeDuplicate(intent));
        var success = JobReceipt();
        Assert.Equal(FixtureB1EndpointSuccessDisposition.Recorded,
            wrapper.Model.RecordNativeSuccess(success));
        Assert.True(launcher.Model.ObservePeerIntent(intent));
        Assert.Equal(FixtureB1EndpointInboundDisposition.Accepted,
            launcher.Model.AcceptInboundSuccess(success));

        var jointAuthority = new JointAuthority();
        var joint = new FixtureB1OwnerChannelModel(
            JointBinding(), JointReservation(), jointAuthority, new JointJournal());
        Assert.True(joint.ReserveRemoteDuplicate(new(
            intent.OperationId,
            intent.SuccessGeneration.Value,
            intent.Direction,
            intent.ResourceKind,
            intent.Source,
            intent.Target,
            intent.ExactRunner)));
        Assert.True(joint.RegisterRemoteDuplicate(new(
            success.OperationId,
            success.SuccessGeneration.Value,
            success.Direction,
            success.ResourceKind,
            success.Source,
            success.Target,
            success.ExactRunner,
            success.TargetLocalCoordinate,
            success.ResourceReceipt,
            success.NonInheritable)));
        Assert.True(joint.AdmitFrame(new(
            JointBinding(),
            success.OperationId,
            success.SuccessGeneration.Value,
            success.Sequence,
            success.Direction,
            success.ResourceKind,
            success.Source,
            success.Target,
            success.ExactRunner,
            success.TargetLocalCoordinate,
            success.ResourceReceipt,
            success.NonInheritable)));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            joint.TryAdopt(new(
                success.OperationId,
                success.ResourceKind,
                success.Target,
                success.TargetLocalCoordinate,
                success.ResourceReceipt,
                null,
                null,
                success.NonInheritable)));
        var jointAck = joint.CreateAck(success.OperationId, JobAck);
        Assert.NotNull(jointAck);
        Assert.True(joint.AcceptAck(jointAck!));
    }

    private static FixtureB1EndpointSuccessReceipt RecordJobSuccess(HarnessState harness)
    {
        Assert.True(harness.Model.ReserveNativeDuplicate(JobIntent()));
        var success = JobReceipt();
        Assert.Equal(FixtureB1EndpointSuccessDisposition.Recorded,
            harness.Model.RecordNativeSuccess(success));
        return success;
    }

    private static FixtureB1EndpointSuccessReceipt RecordProcessSuccess(
        HarnessState harness)
    {
        Assert.True(harness.Model.ReserveNativeDuplicate(ProcessIntent()));
        var success = ProcessReceipt();
        Assert.Equal(FixtureB1EndpointSuccessDisposition.Recorded,
            harness.Model.RecordNativeSuccess(success));
        return success;
    }

    private static FixtureB1EndpointBinding Binding() => new(
        RunNonce, LaunchId, Wrapper, Launcher, 8_001);

    private static FixtureB1EndpointDuplicateIntent JobIntent() => new(
        Binding(), JobOperation, JobSuccess, WrapperJobLease, LauncherJobLease,
        FixtureB1OwnerChannelDirection.WrapperToLauncher,
        FixtureB1OwnerResourceKind.RunnerJob,
        Wrapper, Launcher, null, 0);

    private static FixtureB1EndpointSuccessReceipt JobReceipt() => new(
        Binding(), JobOperation, JobSuccess, WrapperJobLease, LauncherJobLease,
        FixtureB1OwnerChannelDirection.WrapperToLauncher,
        FixtureB1OwnerResourceKind.RunnerJob,
        Wrapper, Launcher, null, 0, 8_101, 9_101, NonInheritable: true);

    private static FixtureB1EndpointDuplicateIntent ProcessIntent() => new(
        Binding(), ProcessOperation, ProcessSuccess,
        LauncherRunnerLease, WrapperRunnerLease,
        FixtureB1OwnerChannelDirection.LauncherToWrapper,
        FixtureB1OwnerResourceKind.RunnerProcess,
        Launcher, Wrapper, Runner, 2);

    private static FixtureB1EndpointSuccessReceipt ProcessReceipt() => new(
        Binding(), ProcessOperation, ProcessSuccess,
        LauncherRunnerLease, WrapperRunnerLease,
        FixtureB1OwnerChannelDirection.LauncherToWrapper,
        FixtureB1OwnerResourceKind.RunnerProcess,
        Launcher, Wrapper, Runner, 2, 8_102, 9_102, NonInheritable: true);

    private static FixtureB1EndpointAck AckFor(
        FixtureB1EndpointSuccessReceipt success,
        Guid ackReceiptId) => new(
            success.Binding,
            success.OperationId,
            success.SuccessGeneration,
            success.SourceLeaseGeneration,
            success.TargetLeaseGeneration,
            success.Direction,
            success.ResourceKind,
            success.Source,
            success.Target,
            success.ExactRunner,
            success.Sequence + 1,
            success.TargetLocalCoordinate,
            success.ResourceReceipt,
            ackReceiptId);

    private static FixtureB1LeaseGeneration Lease(string value) =>
        new(Guid.Parse(value));

    private static FixtureB1SuccessGeneration Success(string value) =>
        new(Guid.Parse(value));

    private static HarnessState Harness(
        FixtureB1EndpointRole role,
        FakeAuthority? authority = null,
        FakeJournal? journal = null)
    {
        authority ??= new();
        journal ??= new();
        return new(new(role, Binding(), authority, journal), authority, journal);
    }

    private static void AssertRejected(
        HarnessState harness,
        FixtureB1EndpointDiagnosticCode diagnostic)
    {
        var snapshot = harness.Model.Snapshot();
        Assert.Equal(FixtureB1EndpointState.Rejected, snapshot.State);
        Assert.Equal(diagnostic, snapshot.DiagnosticCode);
        Assert.False(snapshot.DurabilityProven);
        Assert.False(snapshot.NativeAuthorityProven);
    }

    private sealed record HarnessState(
        FixtureB1EndpointJournalModel Model,
        FakeAuthority Authority,
        FakeJournal Journal);

    private sealed class FakeJournal : IFixtureB1EndpointJournal
    {
        private readonly Dictionary<FixtureB1EndpointRole,
            List<FixtureB1EndpointJournalRecord>> _records = [];

        internal FixtureB1EndpointJournalEvent? FailEvent { get; set; }

        public IReadOnlyList<FixtureB1EndpointJournalRecord> ReadCommitted(
            FixtureB1EndpointRole owner) =>
            _records.TryGetValue(owner, out var records) ? [.. records] : [];

        public bool TryAppend(FixtureB1EndpointJournalRecord record)
        {
            if (record.Event == FailEvent)
                return false;
            if (!_records.TryGetValue(record.Owner, out var records))
            {
                records = [];
                _records.Add(record.Owner, records);
            }
            records.Add(record);
            return true;
        }

        internal void Replace(
            FixtureB1EndpointRole owner,
            IEnumerable<FixtureB1EndpointJournalRecord> records) =>
            _records[owner] = [.. records];
    }

    private sealed class FakeAuthority : IFixtureB1EndpointAuthority
    {
        internal bool BootstrapAuthenticated { get; set; } = true;
        internal bool IntentAuthorized { get; set; } = true;
        internal bool NativeSuccessExact { get; set; } = true;
        internal bool InboundAuthenticated { get; set; } = true;
        internal bool AckAuthenticated { get; set; } = true;

        public bool IsBootstrapAuthenticated(
            FixtureB1EndpointRole endpoint,
            FixtureB1EndpointBinding binding) =>
            BootstrapAuthenticated && binding == Binding();

        public bool IsNativeIntentAuthorized(
            FixtureB1EndpointDuplicateIntent intent) =>
            IntentAuthorized && intent.Binding == Binding();

        public bool IsNativeSuccessExact(
            FixtureB1EndpointDuplicateIntent intent,
            FixtureB1EndpointSuccessReceipt success) =>
            NativeSuccessExact && intent.OperationId == success.OperationId;

        public bool IsPeerIntentAuthenticated(
            FixtureB1EndpointRole endpoint,
            FixtureB1EndpointDuplicateIntent intent) =>
            InboundAuthenticated && intent.Binding == Binding();

        public bool IsInboundPeerAuthenticated(
            FixtureB1EndpointRole endpoint,
            FixtureB1EndpointSuccessReceipt success) =>
            InboundAuthenticated && success.Binding == Binding();

        public bool IsSourceReceiptAuthenticated(
            FixtureB1EndpointRole endpoint,
            FixtureB1EndpointSuccessReceipt success) =>
            InboundAuthenticated && success.Binding == Binding();

        public bool IsAckPeerAuthenticated(
            FixtureB1EndpointRole endpoint,
            FixtureB1EndpointAck ack) =>
            AckAuthenticated && ack.Binding == Binding();
    }

    private static FixtureB1OwnerChannelBinding JointBinding() => new(
        RunNonce, LaunchId, Wrapper, Launcher, 8_001);

    private static FixtureB1OwnerJobReservation JointReservation() => new(
        Wrapper, 9_101, 9_001, NonInheritable: true,
        KillOnLastHandleClose: true, InitiallyEmpty: true);

    private sealed class JointJournal : IFixtureB1OwnerChannelJournal
    {
        public bool TryAppend(FixtureB1OwnerJournalEntry entry) => true;
    }

    private sealed class JointAuthority : IFixtureB1OwnerChannelAuthority
    {
        public bool IsBootstrapAuthenticated(
            FixtureB1OwnerChannelBinding binding) => binding == JointBinding();

        public bool IsExactWrapperOwnedJobReservation(
            FixtureB1OwnerChannelBinding binding,
            FixtureB1OwnerJobReservation reservation) =>
            binding == JointBinding() && reservation == JointReservation();

        public bool IsFrameAuthenticated(FixtureB1OwnerChannelFrame frame) => true;
        public bool IsAckAuthenticated(FixtureB1OwnerChannelAck ack) => true;

        public bool IsExactRunnerJob(
            FixtureB1OwnerChannelAdoption adoption,
            FixtureB1OwnerJobReservation reservation) =>
            adoption.ResourceReceipt == reservation.ResourceReceipt;

        public bool IsExactRunnerRegistration(
            FixtureB1OwnerRunnerRegistration registration,
            FixtureB1OwnerJobReservation reservation) => true;

        public bool IsExactRunnerProcess(
            FixtureB1OwnerChannelAdoption adoption,
            FixtureB1OwnerRunnerRegistration registration,
            FixtureB1OwnerJobReservation reservation) => true;

        public bool IsProviderRetainedResourceClosed(
            FixtureB1OwnerChannelAdoption adoption,
            Guid closeReceipt) => true;

        public bool IsExactPeerTerminated(FixtureHandoffProcessIdentity peer) => true;

        public bool IsReleaseBoundaryLiveAndOwned(
            FixtureB1OwnerChannelBinding binding,
            FixtureB1OwnerRunnerRegistration runner) => true;
    }
}
