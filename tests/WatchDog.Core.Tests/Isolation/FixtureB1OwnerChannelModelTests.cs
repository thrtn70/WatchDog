using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1OwnerChannelModelTests
{
    private static readonly Guid RunNonce =
        Guid.Parse("41000000-2222-3333-4444-555555555555");
    private static readonly Guid LaunchId =
        Guid.Parse("42000000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureHandoffProcessIdentity Wrapper = new(801, 8_010);
    private static readonly FixtureHandoffProcessIdentity Launcher = new(802, 8_020);
    private static readonly FixtureHandoffProcessIdentity Runner = new(803, 8_030);
    private static readonly Guid JobOperation =
        Guid.Parse("43000000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid ProcessOperation =
        Guid.Parse("44000000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid JobSuccessInstance =
        Guid.Parse("44100000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid ProcessSuccessInstance =
        Guid.Parse("44200000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid JobAckReceipt =
        Guid.Parse("45000000-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly Guid ProcessAckReceipt =
        Guid.Parse("46000000-bbbb-cccc-dddd-eeeeeeeeeeee");

    [Fact]
    public void ExactTwoTransferSequenceOpensDeliveryAndResumeOnlyAfterWrapperOwnsRunner()
    {
        var harness = new Harness();

        var jobAck = CompleteJobTransfer(harness);

        Assert.True(harness.Model.RegisterSuspendedRunner(RunnerRegistration()));
        Assert.False(harness.Model.CanDeliverRunnerSlots);
        Assert.False(harness.Model.CanResumeRunner);
        Assert.True(RegisterAndAdmit(
            harness.Model, ProcessDuplicateReceipt(), ProcessOffer()));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            harness.Model.TryAdopt(ProcessAdoption()));
        Assert.False(harness.Model.CanDeliverRunnerSlots);

        var processAck = harness.Model.CreateAck(ProcessOperation, ProcessAckReceipt);

        Assert.NotNull(processAck);
        Assert.True(harness.Model.AcceptAck(processAck!));
        Assert.False(harness.Model.CanDeliverRunnerSlots);
        Assert.False(harness.Model.CanResumeRunner);
        Assert.True(harness.Model.TryAuthorizeRunnerRelease());
        Assert.True(harness.Model.CanDeliverRunnerSlots);
        Assert.True(harness.Model.CanResumeRunner);
        var result = harness.Model.Snapshot();
        Assert.Equal(FixtureB1OwnerChannelState.Ready, result.State);
        Assert.Equal(FixtureB1OwnerChannelDiagnosticCode.None, result.DiagnosticCode);
        Assert.True(result.ExternalBootstrapAuthorityRequired);
        Assert.False(result.NativeQualificationEstablished);
        Assert.False(result.NativeReleaseSynchronizationEstablished);
        Assert.Equal(2, result.AcknowledgedTransferCount);
        Assert.Equal(2, result.JournaledRemoteObligationCount);
        Assert.Equal(0, result.UnconfirmedRemoteObligationCount);
        Assert.Equal(0, result.OutstandingProviderRetainedRejectCount);
        Assert.Equal(
            new[]
            {
                FixtureB1OwnerJournalEvent.RemoteCoordinateJournaled,
                FixtureB1OwnerJournalEvent.Adopted,
                FixtureB1OwnerJournalEvent.AckIssued,
                FixtureB1OwnerJournalEvent.Acknowledged,
                FixtureB1OwnerJournalEvent.RemoteCoordinateJournaled,
                FixtureB1OwnerJournalEvent.Adopted,
                FixtureB1OwnerJournalEvent.AckIssued,
                FixtureB1OwnerJournalEvent.Acknowledged,
                FixtureB1OwnerJournalEvent.ReleaseAuthorized,
            },
            harness.Journal.Entries.Select(entry => entry.Event));
        Assert.Equal(1, jobAck.Sequence);
        Assert.Equal(3, processAck!.Sequence);
    }

    [Fact]
    public void JobAckCannotBeIssuedBeforeLauncherAdoptsTheRemoteJobCoordinate()
    {
        var harness = new Harness();
        Assert.True(RegisterAndAdmit(
            harness.Model, JobDuplicateReceipt(), JobOffer()));

        var ack = harness.Model.CreateAck(JobOperation, JobAckReceipt);

        Assert.Null(ack);
        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.OrderViolation);
        Assert.False(harness.Model.CanDeliverRunnerSlots);
    }

    [Fact]
    public void InheritableOrWrongJobAdoptionIsProviderRetainedAndNeverAcknowledged()
    {
        var harness = new Harness();
        Assert.True(RegisterAndAdmit(
            harness.Model, JobDuplicateReceipt(), JobOffer()));
        var wrong = JobAdoption() with
        {
            ResourceReceipt = 9_999,
            NonInheritable = false,
        };

        var disposition = harness.Model.TryAdopt(wrong);

        Assert.Equal(FixtureB1OwnerAdoptionDisposition.ProviderRetainsNewResource,
            disposition);
        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.WrongObject);
        Assert.Contains(harness.Journal.Entries,
            entry => entry.Event ==
                FixtureB1OwnerJournalEvent.ProviderRetainedRejected);
        Assert.Equal(1,
            harness.Model.Snapshot().OutstandingProviderRetainedRejectCount);
    }

    [Fact]
    public void ProviderRetainedRejectRequiresExternalCloseProofAndDurableCloseReceipt()
    {
        var harness = new Harness();
        Assert.True(RegisterAndAdmit(
            harness.Model, JobDuplicateReceipt(), JobOffer()));
        var wrong = JobAdoption() with { ResourceReceipt = 9_999 };
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.ProviderRetainsNewResource,
            harness.Model.TryAdopt(wrong));
        var closeReceipt = Guid.Parse("47000000-bbbb-cccc-dddd-eeeeeeeeeeee");

        Assert.True(harness.Model.ConfirmProviderRetainedClose(wrong, closeReceipt));

        var result = harness.Model.Snapshot();
        Assert.Equal(1, result.ProviderRetainedRejectCount);
        Assert.Equal(0, result.OutstandingProviderRetainedRejectCount);
        Assert.Contains(harness.Journal.Entries,
            entry => entry.Event == FixtureB1OwnerJournalEvent.ProviderRetainedClosed &&
                entry.ReceiptId == closeReceipt);
    }

    [Fact]
    public void RunnerMustBeExactSuspendedInWrapperJobWithZeroInheritedHandles()
    {
        var invalidRegistrations = new[]
        {
            RunnerRegistration() with { ExactRunner = new(804, 8_040) },
            RunnerRegistration() with { Creator = Wrapper },
            RunnerRegistration() with { RunnerJobReceipt = 9_999 },
            RunnerRegistration() with { CreatedSuspended = false },
            RunnerRegistration() with { InheritedHandleCount = 1 },
        };

        foreach (var registration in invalidRegistrations)
        {
            var harness = new Harness();
            CompleteJobTransfer(harness);

            Assert.False(harness.Model.RegisterSuspendedRunner(registration));
            AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.WrongObject);
            Assert.False(harness.Model.CanResumeRunner);
        }
    }

    [Fact]
    public void RunnerProcessAdoptionMustBeExactAndRemainInTheWrapperCreatedJob()
    {
        var harness = ReadyForProcessTransfer();
        Assert.True(RegisterAndAdmit(
            harness.Model, ProcessDuplicateReceipt(), ProcessOffer()));
        var wrong = ProcessAdoption() with
        {
            ExactProcess = Launcher,
            ContainingJobReceipt = 9_999,
        };

        var disposition = harness.Model.TryAdopt(wrong);

        Assert.Equal(FixtureB1OwnerAdoptionDisposition.ProviderRetainsNewResource,
            disposition);
        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.WrongObject);
        Assert.False(harness.Model.CanDeliverRunnerSlots);
    }

    [Fact]
    public void ProcessTransferCannotSubstituteRunnerAfterRegistrationFreezesIdentity()
    {
        var harness = ReadyForProcessTransfer();
        var substituted = ProcessOffer() with
        {
            ExactRunner = new FixtureHandoffProcessIdentity(804, 8_040),
        };

        Assert.False(harness.Model.RegisterRemoteDuplicate(
            DuplicateReceiptFor(substituted)));

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.InvalidFrame);
        Assert.Equal(2, harness.Model.Snapshot().JournaledRemoteObligationCount);
    }

    [Fact]
    public void ProcessAckCannotDriftFromRunnerFrozenAtSuspendedRegistration()
    {
        var harness = ReadyForProcessTransfer();
        Assert.True(RegisterAndAdmit(
            harness.Model, ProcessDuplicateReceipt(), ProcessOffer()));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            harness.Model.TryAdopt(ProcessAdoption()));
        var ack = harness.Model.CreateAck(ProcessOperation, ProcessAckReceipt)!;
        var substituted = ack with
        {
            ExactRunner = new FixtureHandoffProcessIdentity(804, 8_040),
        };

        Assert.False(harness.Model.AcceptAck(substituted));

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.BindingMismatch);
    }

    [Fact]
    public void AliasingTheJobObjectAsTheRunnerProcessIsProviderRetained()
    {
        var harness = ReadyForProcessTransfer();
        var aliasedOffer = ProcessOffer() with { ResourceReceipt = 9_001 };
        Assert.True(RegisterAndAdmit(
            harness.Model, DuplicateReceiptFor(aliasedOffer), aliasedOffer));
        var aliasedAdoption = ProcessAdoption() with { ResourceReceipt = 9_001 };

        var disposition = harness.Model.TryAdopt(aliasedAdoption);

        Assert.Equal(FixtureB1OwnerAdoptionDisposition.ProviderRetainsNewResource,
            disposition);
        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.ResourceAlias);
    }

    [Fact]
    public void LostAckIsJournaledAndItsLateArrivalCannotAdvanceTheContract()
    {
        var harness = new Harness();
        Assert.True(RegisterAndAdmit(
            harness.Model, JobDuplicateReceipt(), JobOffer()));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            harness.Model.TryAdopt(JobAdoption()));
        var ack = harness.Model.CreateAck(JobOperation, JobAckReceipt)!;

        Assert.True(harness.Model.AbandonAck(JobOperation));
        Assert.False(harness.Model.AcceptAck(ack));

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.AckLost);
        Assert.Contains(harness.Journal.Entries,
            entry => entry.Event == FixtureB1OwnerJournalEvent.AckLost);
        Assert.False(harness.Model.RegisterSuspendedRunner(RunnerRegistration()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactLauncherDeathBeforeOrAfterJobAckFailsClosed(bool afterAck)
    {
        var harness = new Harness();
        FixtureB1OwnerChannelAck? ack = null;
        Assert.True(RegisterAndAdmit(
            harness.Model, JobDuplicateReceipt(), JobOffer()));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            harness.Model.TryAdopt(JobAdoption()));
        if (afterAck)
        {
            ack = harness.Model.CreateAck(JobOperation, JobAckReceipt);
            Assert.NotNull(ack);
            Assert.True(harness.Model.AcceptAck(ack!));
        }

        Assert.True(harness.Model.ReportPeerDeath(Launcher));

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.PeerDied);
        Assert.Contains(harness.Journal.Entries,
            entry => entry.Event == FixtureB1OwnerJournalEvent.PeerDeathObserved &&
                entry.Target == Launcher);
        Assert.False(harness.Model.RegisterSuspendedRunner(RunnerRegistration()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExactWrapperDeathBeforeOrAfterRunnerProcessAckFailsClosed(bool afterAck)
    {
        var harness = ReadyForProcessTransfer();
        Assert.True(RegisterAndAdmit(
            harness.Model, ProcessDuplicateReceipt(), ProcessOffer()));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            harness.Model.TryAdopt(ProcessAdoption()));
        if (afterAck)
        {
            var ack = harness.Model.CreateAck(ProcessOperation, ProcessAckReceipt);
            Assert.NotNull(ack);
            Assert.True(harness.Model.AcceptAck(ack!));
            Assert.False(harness.Model.CanResumeRunner);
        }

        Assert.True(harness.Model.ReportPeerDeath(Wrapper));

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.PeerDied);
        Assert.False(harness.Model.CanDeliverRunnerSlots);
    }

    [Fact]
    public void ReplayedAckLatchesFailureEvenAfterTheFirstAckWasAccepted()
    {
        var harness = new Harness();
        var ack = CompleteJobTransfer(harness);

        Assert.False(harness.Model.AcceptAck(ack));

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.AckReplay);
        Assert.False(harness.Model.RegisterSuspendedRunner(RunnerRegistration()));
    }

    [Fact]
    public void ExactAdoptionReplayRemainsAlreadyOwnedAfterAckAndLaterRejection()
    {
        var harness = new Harness();
        var ack = CompleteJobTransfer(harness);

        Assert.Equal(FixtureB1OwnerAdoptionDisposition.AlreadyOwned,
            harness.Model.TryAdopt(JobAdoption()));
        Assert.Equal(0, harness.Model.Snapshot().ProviderRetainedRejectCount);
        Assert.False(harness.Model.AcceptAck(ack));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.AlreadyOwned,
            harness.Model.TryAdopt(JobAdoption()));
        Assert.Equal(0, harness.Model.Snapshot().ProviderRetainedRejectCount);
    }

    [Fact]
    public void MetadataDriftOnKnownOwnedCoordinateNeverCreatesSecondCloseAuthority()
    {
        var harness = new Harness();
        CompleteJobTransfer(harness);
        var drifted = JobAdoption() with { ResourceReceipt = 9_999 };

        Assert.Equal(FixtureB1OwnerAdoptionDisposition.AlreadyOwned,
            harness.Model.TryAdopt(drifted));

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.ResourceAlias);
        Assert.Equal(0, harness.Model.Snapshot().ProviderRetainedRejectCount);
    }

    [Theory]
    [MemberData(nameof(UnboundJobOffers))]
    public void UnboundFrameIsRejectedBeforeAdoption(string caseId)
    {
        var (offer, expected) = caseId switch
        {
            "nonce" => (JobOffer() with
            {
                Binding = Binding() with { RunNonce = Guid.NewGuid() },
            }, FixtureB1OwnerChannelDiagnosticCode.BindingMismatch),
            "launch" => (JobOffer() with
            {
                Binding = Binding() with { LaunchId = Guid.NewGuid() },
            }, FixtureB1OwnerChannelDiagnosticCode.BindingMismatch),
            "connection" => (JobOffer() with
            {
                Binding = Binding() with { ConnectionReceipt = 7_002 },
            }, FixtureB1OwnerChannelDiagnosticCode.BindingMismatch),
            "peer" => (JobOffer() with
            {
                Binding = Binding() with
                {
                    ExactWrapper = new FixtureHandoffProcessIdentity(811, 8_110),
                },
            }, FixtureB1OwnerChannelDiagnosticCode.BindingMismatch),
            "direction" => (JobOffer() with { Source = Launcher, Target = Wrapper },
                FixtureB1OwnerChannelDiagnosticCode.InvalidFrame),
            "sequence" => (JobOffer() with { Sequence = 9 },
                FixtureB1OwnerChannelDiagnosticCode.SequenceMismatch),
            "operation" => (JobOffer() with { OperationId = Guid.Empty },
                FixtureB1OwnerChannelDiagnosticCode.InvalidFrame),
            _ => throw new ArgumentOutOfRangeException(nameof(caseId)),
        };
        var harness = new Harness();

        Assert.True(harness.Model.ReserveRemoteDuplicate(
            ReservationFor(JobDuplicateReceipt())));
        Assert.True(harness.Model.RegisterRemoteDuplicate(
            JobDuplicateReceipt()));
        Assert.False(harness.Model.AdmitFrame(offer));

        AssertRejected(harness, expected);
        Assert.DoesNotContain(harness.Journal.Entries,
            entry => entry.Event == FixtureB1OwnerJournalEvent.Adopted);
    }

    public static TheoryData<string> UnboundJobOffers => new()
    {
        "nonce",
        "launch",
        "connection",
        "peer",
        "direction",
        "sequence",
        "operation",
    };

    [Fact]
    public void WrongConnectionOrUnauthenticatedBootstrapCannotStartCustody()
    {
        var authority = new FakeAuthority { BootstrapAuthenticated = false };
        var harness = new Harness(authority);

        Assert.False(harness.Model.RegisterRemoteDuplicate(
            JobDuplicateReceipt()));

        AssertRejected(harness,
            FixtureB1OwnerChannelDiagnosticCode.BootstrapAuthorityRejected);
        Assert.Single(harness.Journal.Entries);
        Assert.Equal(FixtureB1OwnerJournalEvent.RemoteCoordinateJournaled,
            harness.Journal.Entries[0].Event);
    }

    [Fact]
    public void SyntacticallyValidWrapperJobReservationRequiresExternalProof()
    {
        var authority = new FakeAuthority { ReservationAuthenticated = false };
        var harness = new Harness(authority);

        var result = harness.Model.Snapshot();

        Assert.Equal(FixtureB1OwnerChannelState.Rejected, result.State);
        Assert.Equal(FixtureB1OwnerChannelDiagnosticCode.ReservationAuthorityRejected,
            result.DiagnosticCode);
        Assert.Empty(result.Journal);
        Assert.False(result.NativeQualificationEstablished);
    }

    [Fact]
    public void RemoteSuccessIsJournaledBeforeUnauthenticatedFrameFailsClosed()
    {
        var authority = new FakeAuthority { FrameAuthenticated = false };
        var harness = new Harness(authority);

        Assert.True(harness.Model.ReserveRemoteDuplicate(
            ReservationFor(JobDuplicateReceipt())));
        Assert.True(harness.Model.RegisterRemoteDuplicate(JobDuplicateReceipt()));
        Assert.False(harness.Model.AdmitFrame(JobOffer()));

        AssertRejected(harness,
            FixtureB1OwnerChannelDiagnosticCode.FrameAuthenticationRejected);
        Assert.Equal(FixtureB1OwnerJournalEvent.RemoteCoordinateJournaled,
            harness.Journal.Entries[0].Event);
        Assert.Equal(JobOperation, harness.Journal.Entries[0].OperationId);
        Assert.Equal(1, harness.Model.Snapshot().JournaledRemoteObligationCount);
        Assert.Equal(0, harness.Model.Snapshot().UnconfirmedRemoteObligationCount);
    }

    [Fact]
    public void RemoteSuccessRemainsJournaledWhenNoFrameCanBeConstructed()
    {
        var harness = new Harness();

        Assert.True(harness.Model.ReserveRemoteDuplicate(
            ReservationFor(JobDuplicateReceipt())));
        Assert.True(harness.Model.RegisterRemoteDuplicate(JobDuplicateReceipt()));
        var pending = harness.Model.Snapshot();
        Assert.Equal(FixtureB1OwnerChannelState.Pending, pending.State);
        Assert.Equal(1, pending.JournaledRemoteObligationCount);
        Assert.Single(pending.Journal);

        Assert.True(harness.Model.FailFrameAdmission(JobOperation));

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.InvalidFrame);
        Assert.Equal(FixtureB1OwnerJournalEvent.RemoteCoordinateJournaled,
            harness.Journal.Entries[0].Event);
        Assert.Contains(harness.Journal.Entries,
            entry => entry.Event == FixtureB1OwnerJournalEvent.ProtocolRejected);
    }

    [Fact]
    public void ExactNativeSuccessCallbackReplayDoesNotCreateSecondCloseDuty()
    {
        var harness = new Harness();
        var duplicate = JobDuplicateReceipt();
        Assert.True(harness.Model.ReserveRemoteDuplicate(ReservationFor(duplicate)));
        Assert.True(harness.Model.RegisterRemoteDuplicate(duplicate));

        Assert.True(harness.Model.RegisterRemoteDuplicate(duplicate));

        var result = harness.Model.Snapshot();
        Assert.Equal(1, result.JournaledRemoteObligationCount);
        var remoteEntries = result.Journal.Where(
            entry => entry.Event ==
                FixtureB1OwnerJournalEvent.RemoteCoordinateJournaled).ToArray();
        Assert.Single(remoteEntries);
        Assert.Equal(JobSuccessInstance, remoteEntries[0].SuccessInstanceId);
        Assert.True(harness.Model.AdmitFrame(JobOffer()));
    }

    [Fact]
    public void NewNativeSuccessAtReusedCoordinateIsSeparateCustodyAndFailsClosed()
    {
        var harness = new Harness();
        var first = JobDuplicateReceipt();
        Assert.True(harness.Model.ReserveRemoteDuplicate(ReservationFor(first)));
        Assert.True(harness.Model.RegisterRemoteDuplicate(first));
        var secondSuccess = Guid.Parse("44300000-bbbb-cccc-dddd-eeeeeeeeeeee");
        var second = first with { SuccessInstanceId = secondSuccess };

        Assert.False(harness.Model.RegisterRemoteDuplicate(second));

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.OrderViolation);
        var result = harness.Model.Snapshot();
        Assert.Equal(2, result.JournaledRemoteObligationCount);
        Assert.Contains(result.Journal,
            entry => entry.SuccessInstanceId == JobSuccessInstance);
        Assert.Contains(result.Journal,
            entry => entry.SuccessInstanceId == secondSuccess);
    }

    [Fact]
    public void UnauthenticatedAckFailsClosedWithoutAcknowledgingCustody()
    {
        var authority = new FakeAuthority();
        var harness = new Harness(authority);
        Assert.True(RegisterAndAdmit(
            harness.Model, JobDuplicateReceipt(), JobOffer()));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            harness.Model.TryAdopt(JobAdoption()));
        var ack = harness.Model.CreateAck(JobOperation, JobAckReceipt)!;
        authority.AckAuthenticated = false;

        Assert.False(harness.Model.AcceptAck(ack));

        AssertRejected(harness,
            FixtureB1OwnerChannelDiagnosticCode.AckAuthenticationRejected);
        Assert.Equal(0, harness.Model.Snapshot().AcknowledgedTransferCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AckConnectionOrPeerDriftCannotReuseAnAuthenticatedOffer(
        bool driftConnection)
    {
        var harness = new Harness();
        Assert.True(RegisterAndAdmit(
            harness.Model, JobDuplicateReceipt(), JobOffer()));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            harness.Model.TryAdopt(JobAdoption()));
        var ack = harness.Model.CreateAck(JobOperation, JobAckReceipt)!;
        var drifted = driftConnection
            ? ack with
            {
                Binding = ack.Binding with { ConnectionReceipt = 7_002 },
            }
            : ack with { Source = Wrapper, Target = Launcher };

        Assert.False(harness.Model.AcceptAck(drifted));

        AssertRejected(harness,
            FixtureB1OwnerChannelDiagnosticCode.BindingMismatch);
        Assert.Equal(0, harness.Model.Snapshot().AcknowledgedTransferCount);
    }

    [Fact]
    public void OperationAndAckReceiptsAreOneUseAcrossBothTransfers()
    {
        var harness = ReadyForProcessTransfer();

        var replayedOperation = ProcessOffer() with { OperationId = JobOperation };
        Assert.False(harness.Model.ReserveRemoteDuplicate(
            ReservationFor(DuplicateReceiptFor(replayedOperation))));
        AssertRejected(harness,
            FixtureB1OwnerChannelDiagnosticCode.OperationReplay);

        var second = ReadyForProcessTransfer();
        Assert.True(RegisterAndAdmit(
            second.Model, ProcessDuplicateReceipt(), ProcessOffer()));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            second.Model.TryAdopt(ProcessAdoption()));
        Assert.Null(second.Model.CreateAck(ProcessOperation, JobAckReceipt));
        AssertRejected(second,
            FixtureB1OwnerChannelDiagnosticCode.AckReplay);
    }

    [Fact]
    public void JournalFailureAfterAdoptionLeavesTheNewResourceWithProvider()
    {
        var harness = new Harness();
        Assert.True(RegisterAndAdmit(
            harness.Model, JobDuplicateReceipt(), JobOffer()));
        harness.Journal.FailEvent = FixtureB1OwnerJournalEvent.Adopted;

        var disposition = harness.Model.TryAdopt(JobAdoption());

        Assert.Equal(FixtureB1OwnerAdoptionDisposition.ProviderRetainsNewResource,
            disposition);
        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.JournalFailure);
        Assert.Equal(1,
            harness.Model.Snapshot().OutstandingProviderRetainedRejectCount);
        Assert.False(harness.Model.CanResumeRunner);
    }

    [Fact]
    public void RemoteJournalFailureIsUnconfirmedAndNeverClaimedAsCustody()
    {
        var harness = new Harness();
        Assert.True(harness.Model.ReserveRemoteDuplicate(
            ReservationFor(JobDuplicateReceipt())));
        harness.Journal.FailEvent =
            FixtureB1OwnerJournalEvent.RemoteCoordinateJournaled;

        Assert.False(harness.Model.RegisterRemoteDuplicate(
            JobDuplicateReceipt()));

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.JournalFailure);
        var result = harness.Model.Snapshot();
        Assert.Equal(0, result.JournaledRemoteObligationCount);
        Assert.Equal(1, result.UnconfirmedRemoteObligationCount);
        Assert.Empty(result.Journal);
    }

    [Fact]
    public void RequestingDeliveryBeforeRunnerProcessAckPermanentlyFailsClosed()
    {
        var harness = ReadyForProcessTransfer();
        Assert.True(RegisterAndAdmit(
            harness.Model, ProcessDuplicateReceipt(), ProcessOffer()));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            harness.Model.TryAdopt(ProcessAdoption()));

        Assert.False(harness.Model.TryAuthorizeRunnerRelease());

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.OrderViolation);
        Assert.Null(harness.Model.CreateAck(ProcessOperation, ProcessAckReceipt));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeadOrUnobservablePeerAtReleaseBoundaryFailsClosed(bool throws)
    {
        var harness = ReadyForRelease();
        harness.Authority.ReleaseBoundaryLive = false;
        harness.Authority.ThrowOnReleaseBoundary = throws;
        Assert.False(harness.Model.CanDeliverRunnerSlots);
        Assert.False(harness.Model.CanResumeRunner);

        Assert.False(harness.Model.TryAuthorizeRunnerRelease());

        AssertRejected(harness,
            FixtureB1OwnerChannelDiagnosticCode.ReleaseAuthorityRejected);
    }

    [Fact]
    public void ReleaseAuthorizationIsOneUseAndRepeatedRequestFailsClosed()
    {
        var harness = ReadyForRelease();

        Assert.True(harness.Model.TryAuthorizeRunnerRelease());
        Assert.True(harness.Model.CanDeliverRunnerSlots);
        Assert.True(harness.Model.CanResumeRunner);
        Assert.False(harness.Model.TryAuthorizeRunnerRelease());

        AssertRejected(harness, FixtureB1OwnerChannelDiagnosticCode.ReleaseReplay);
        Assert.False(harness.Model.CanDeliverRunnerSlots);
        Assert.False(harness.Model.CanResumeRunner);
    }

    private static FixtureB1OwnerChannelAck CompleteJobTransfer(Harness harness)
    {
        Assert.True(RegisterAndAdmit(
            harness.Model, JobDuplicateReceipt(), JobOffer()));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            harness.Model.TryAdopt(JobAdoption()));
        var ack = harness.Model.CreateAck(JobOperation, JobAckReceipt);
        Assert.NotNull(ack);
        Assert.True(harness.Model.AcceptAck(ack!));
        return ack!;
    }

    private static Harness ReadyForProcessTransfer()
    {
        var harness = new Harness();
        CompleteJobTransfer(harness);
        Assert.True(harness.Model.RegisterSuspendedRunner(RunnerRegistration()));
        return harness;
    }

    private static Harness ReadyForRelease()
    {
        var harness = ReadyForProcessTransfer();
        Assert.True(RegisterAndAdmit(
            harness.Model, ProcessDuplicateReceipt(), ProcessOffer()));
        Assert.Equal(FixtureB1OwnerAdoptionDisposition.Adopted,
            harness.Model.TryAdopt(ProcessAdoption()));
        var ack = harness.Model.CreateAck(ProcessOperation, ProcessAckReceipt);
        Assert.NotNull(ack);
        Assert.True(harness.Model.AcceptAck(ack!));
        Assert.False(harness.Model.CanDeliverRunnerSlots);
        Assert.False(harness.Model.CanResumeRunner);
        return harness;
    }

    private static FixtureB1OwnerChannelBinding Binding() => new(
        RunNonce, LaunchId, Wrapper, Launcher, 7_001);

    private static FixtureB1OwnerJobReservation Reservation() => new(
        Wrapper, 9_001, 9_002, NonInheritable: true,
        KillOnLastHandleClose: true, InitiallyEmpty: true);

    private static FixtureB1OwnerChannelFrame JobOffer() => new(
        Binding(), JobOperation, JobSuccessInstance, 0,
        FixtureB1OwnerChannelDirection.WrapperToLauncher,
        FixtureB1OwnerResourceKind.RunnerJob,
        Wrapper, Launcher, null, 8_001, 9_001, NonInheritable: true);

    private static FixtureB1OwnerRemoteDuplicateReceipt JobDuplicateReceipt() =>
        DuplicateReceiptFor(JobOffer());

    private static FixtureB1OwnerChannelAdoption JobAdoption() => new(
        JobOperation, FixtureB1OwnerResourceKind.RunnerJob,
        Launcher, 8_001, 9_001, null, null, NonInheritable: true);

    private static FixtureB1OwnerRunnerRegistration RunnerRegistration() => new(
        Runner, Launcher, 9_001, CreatedSuspended: true,
        InheritedHandleCount: 0);

    private static FixtureB1OwnerChannelFrame ProcessOffer() => new(
        Binding(), ProcessOperation, ProcessSuccessInstance, 2,
        FixtureB1OwnerChannelDirection.LauncherToWrapper,
        FixtureB1OwnerResourceKind.RunnerProcess,
        Launcher, Wrapper, Runner, 8_002, 9_101, NonInheritable: true);

    private static FixtureB1OwnerRemoteDuplicateReceipt ProcessDuplicateReceipt() =>
        DuplicateReceiptFor(ProcessOffer());

    private static FixtureB1OwnerRemoteDuplicateReceipt DuplicateReceiptFor(
        FixtureB1OwnerChannelFrame frame) => new(
            frame.OperationId,
            frame.SuccessInstanceId,
            frame.Direction,
            frame.ResourceKind,
            frame.Source,
            frame.Target,
            frame.ExactRunner,
            frame.TargetLocalCoordinate,
            frame.ResourceReceipt,
            frame.NonInheritable);

    private static FixtureB1OwnerRemoteDuplicateReservation ReservationFor(
        FixtureB1OwnerRemoteDuplicateReceipt duplicate) => new(
            duplicate.OperationId,
            duplicate.SuccessInstanceId,
            duplicate.Direction,
            duplicate.ResourceKind,
            duplicate.Source,
            duplicate.Target,
            duplicate.ExactRunner);

    private static bool RegisterAndAdmit(
        FixtureB1OwnerChannelModel model,
        FixtureB1OwnerRemoteDuplicateReceipt duplicate,
        FixtureB1OwnerChannelFrame frame) =>
        model.ReserveRemoteDuplicate(ReservationFor(duplicate)) &&
        model.RegisterRemoteDuplicate(duplicate) && model.AdmitFrame(frame);

    private static FixtureB1OwnerChannelAdoption ProcessAdoption() => new(
        ProcessOperation, FixtureB1OwnerResourceKind.RunnerProcess,
        Wrapper, 8_002, 9_101, Runner, 9_001, NonInheritable: true);

    private static void AssertRejected(
        Harness harness,
        FixtureB1OwnerChannelDiagnosticCode expected)
    {
        var result = harness.Model.Snapshot();
        Assert.Equal(FixtureB1OwnerChannelState.Rejected, result.State);
        Assert.Equal(expected, result.DiagnosticCode);
        Assert.False(result.CanDeliverRunnerSlots);
        Assert.False(result.CanResumeRunner);
        Assert.False(result.NativeQualificationEstablished);
    }

    private sealed class Harness
    {
        internal Harness(FakeAuthority? authority = null)
        {
            Authority = authority ?? new();
            Journal = new();
            Model = new(Binding(), Reservation(), Authority, Journal);
        }

        internal FakeAuthority Authority { get; }
        internal FakeJournal Journal { get; }
        internal FixtureB1OwnerChannelModel Model { get; }
    }

    private sealed class FakeJournal : IFixtureB1OwnerChannelJournal
    {
        internal List<FixtureB1OwnerJournalEntry> Entries { get; } = [];
        internal FixtureB1OwnerJournalEvent? FailEvent { get; set; }

        public bool TryAppend(FixtureB1OwnerJournalEntry entry)
        {
            if (entry.Event == FailEvent)
                return false;
            Entries.Add(entry);
            return true;
        }
    }

    private sealed class FakeAuthority : IFixtureB1OwnerChannelAuthority
    {
        internal bool BootstrapAuthenticated { get; set; } = true;
        internal bool ReservationAuthenticated { get; set; } = true;
        internal bool FrameAuthenticated { get; set; } = true;
        internal bool AckAuthenticated { get; set; } = true;
        internal bool CloseConfirmed { get; set; } = true;
        internal bool ReleaseBoundaryLive { get; set; } = true;
        internal bool ThrowOnReleaseBoundary { get; set; }

        public bool IsBootstrapAuthenticated(FixtureB1OwnerChannelBinding binding) =>
            BootstrapAuthenticated && binding == Binding();

        public bool IsExactWrapperOwnedJobReservation(
            FixtureB1OwnerChannelBinding binding,
            FixtureB1OwnerJobReservation reservation) =>
            ReservationAuthenticated && binding == Binding() &&
            reservation == Reservation();

        public bool IsFrameAuthenticated(FixtureB1OwnerChannelFrame frame) =>
            FrameAuthenticated;

        public bool IsAckAuthenticated(FixtureB1OwnerChannelAck ack) =>
            AckAuthenticated;

        public bool IsExactRunnerJob(
            FixtureB1OwnerChannelAdoption adoption,
            FixtureB1OwnerJobReservation reservation) =>
            adoption.ResourceReceipt == reservation.ResourceReceipt;

        public bool IsExactRunnerRegistration(
            FixtureB1OwnerRunnerRegistration registration,
            FixtureB1OwnerJobReservation reservation) =>
            registration.ExactRunner == Runner &&
            registration.RunnerJobReceipt == reservation.ResourceReceipt;

        public bool IsExactRunnerProcess(
            FixtureB1OwnerChannelAdoption adoption,
            FixtureB1OwnerRunnerRegistration registration,
            FixtureB1OwnerJobReservation reservation) =>
            adoption.ExactProcess == registration.ExactRunner &&
            adoption.ContainingJobReceipt == reservation.ResourceReceipt;

        public bool IsProviderRetainedResourceClosed(
            FixtureB1OwnerChannelAdoption adoption,
            Guid closeReceipt) => CloseConfirmed && closeReceipt != Guid.Empty;

        public bool IsExactPeerTerminated(
            FixtureHandoffProcessIdentity peer) => peer == Wrapper || peer == Launcher;

        public bool IsReleaseBoundaryLiveAndOwned(
            FixtureB1OwnerChannelBinding binding,
            FixtureB1OwnerRunnerRegistration runner)
        {
            if (ThrowOnReleaseBoundary)
                throw new IOException("release observation failed");
            return ReleaseBoundaryLive && binding == Binding() &&
                runner == RunnerRegistration();
        }
    }
}
