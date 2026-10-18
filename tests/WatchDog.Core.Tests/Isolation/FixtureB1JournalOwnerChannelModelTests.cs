using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1JournalOwnerChannelModelTests
{
    private static readonly FixtureHandoffProcessIdentity Owner = new(901, 90_010);
    private static readonly FixtureHandoffProcessIdentity Writer = new(902, 90_020);
    private static readonly FixtureArtifactIdentity ParentIdentity =
        new(7, 10, 11, FixtureArtifactKind.Directory, IsReparse: false);
    private static readonly FixtureArtifactIdentity RootIdentity =
        new(7, 20, 21, FixtureArtifactKind.Directory, IsReparse: false);
    private static readonly FixtureArtifactIdentity FramesIdentity =
        new(7, 30, 31, FixtureArtifactKind.File, IsReparse: false);
    private static readonly FixtureArtifactIdentity AnchorIdentity =
        new(7, 40, 41, FixtureArtifactKind.File, IsReparse: false);

    [Fact]
    public void ExactParentAndThreeReadAdoptionsOpenOneUseWriteGate()
    {
        var harness = ReadyForRelease();

        Assert.True(harness.Writer.AcceptWriteRelease(harness.Release!));
        Assert.True(harness.Writer.TryStartWrite(Id(900)));
        Assert.True(harness.Writer.CompleteWrite(Id(900)));

        var owner = harness.Owner.Snapshot();
        var writer = harness.Writer.Snapshot();
        Assert.Equal(FixtureB1JournalEndpointState.Ready, owner.State);
        Assert.Equal(3, owner.AdoptedReadCount);
        Assert.Equal(3, owner.OutstandingLocalCloseCount);
        Assert.False(owner.NativeAuthorityProven);
        Assert.False(owner.NativeDurabilityProven);
        Assert.Equal(FixtureB1JournalEndpointState.WritableIdle, writer.State);
        Assert.Equal(3, writer.AcknowledgedReadCount);
        Assert.True(writer.CanWrite);
        Assert.False(writer.NativeAuthorityProven);
        Assert.False(writer.NativeDurabilityProven);
        Assert.Equal(FixtureB1JournalCustodyEvent.CustodyRecorded,
            harness.Store.Entries[^1].Event);
        foreach (var resource in ReadResources())
        {
            var successIndex = harness.Store.Entries.FindIndex(entry =>
                entry.ResourceKind == resource &&
                entry.Event == FixtureB1JournalCustodyEvent.TransferSuccessRecorded);
            var adoptionIndex = harness.Store.Entries.FindIndex(entry =>
                entry.ResourceKind == resource &&
                entry.Event == FixtureB1JournalCustodyEvent.ReceiverAdopted);
            var ackIndex = harness.Store.Entries.FindIndex(entry =>
                entry.ResourceKind == resource &&
                entry.Event == FixtureB1JournalCustodyEvent.AckIssued);
            Assert.True(successIndex >= 0 && successIndex < adoptionIndex);
            Assert.True(adoptionIndex < ackIndex);
        }
    }

    [Fact]
    public void WriterCannotCreateJournalBeforeOwnerAcceptsParentAck()
    {
        var harness = new Harness();
        Assert.True(harness.Owner.ReserveParentTransfer(ParentTransfer()));
        Assert.True(harness.Owner.RecordParentTransferSuccess(ParentSuccess()));
        var ack = harness.Writer.AdoptParent(
            ParentSuccess(), ParentObservation(), Id(20));
        Assert.NotNull(ack);

        Assert.False(harness.Writer.RegisterWriterSources(Sources()));

        AssertWriterRejected(harness, FixtureB1JournalDiagnosticCode.WrongObject);
    }

    [Theory]
    [InlineData((uint)FixtureB1NativeJournalHandleAccess.WriteDataOrAddFile)]
    [InlineData((uint)FixtureB1NativeJournalHandleAccess.Delete)]
    [InlineData((uint)FixtureB1NativeJournalHandleAccess.WriteDac)]
    [InlineData((uint)FixtureB1NativeJournalHandleAccess.WriteOwner)]
    public void ParentAdoptionRejectsEveryExcessRight(uint excessRight)
    {
        var harness = new Harness();
        Assert.True(harness.Owner.ReserveParentTransfer(ParentTransfer()));
        Assert.True(harness.Owner.RecordParentTransferSuccess(ParentSuccess()));
        var observation = ParentObservation() with
        {
            EffectiveGrantedAccess =
                FixtureB1JournalAccessPolicy.WriterParent | excessRight,
        };

        Assert.Null(harness.Writer.AdoptParent(
            ParentSuccess(), observation, Id(20)));

        AssertWriterRejected(harness, FixtureB1JournalDiagnosticCode.ExcessRights);
    }

    [Theory]
    [InlineData("wrong-owner")]
    [InlineData("wrong-writer")]
    [InlineData("wrong-owner-lease")]
    [InlineData("wrong-writer-lease")]
    [InlineData("wrong-parent-lease")]
    [InlineData("stale-connection")]
    public void ParentTransferRejectsBindingAndLeaseDrift(string caseId)
    {
        var harness = new Harness();
        var transfer = ParentTransfer();
        transfer = caseId switch
        {
            "wrong-owner" => transfer with { Source = Writer },
            "wrong-writer" => transfer with { Target = Owner },
            "wrong-owner-lease" => transfer with
            {
                SourceProcessLeaseGeneration =
                    transfer.SourceProcessLeaseGeneration + 1,
            },
            "wrong-writer-lease" => transfer with
            {
                TargetProcessLeaseGeneration =
                    transfer.TargetProcessLeaseGeneration + 1,
            },
            "wrong-parent-lease" => transfer with
            {
                SourceHandleLeaseGeneration =
                    transfer.SourceHandleLeaseGeneration + 1,
            },
            "stale-connection" => transfer with
            {
                Binding = transfer.Binding with
                {
                    ConnectionGeneration =
                        transfer.Binding.ConnectionGeneration + 1,
                },
            },
            _ => throw new InvalidOperationException(caseId),
        };

        Assert.False(harness.Owner.ReserveParentTransfer(transfer));

        AssertOwnerRejected(harness, FixtureB1JournalDiagnosticCode.InvalidTransfer);
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("security")]
    [InlineData("path")]
    public void ParentAdoptionRejectsIdentitySecurityAndPathSubstitution(string caseId)
    {
        var harness = new Harness();
        Assert.True(harness.Owner.ReserveParentTransfer(ParentTransfer()));
        Assert.True(harness.Owner.RecordParentTransferSuccess(ParentSuccess()));
        var observation = ParentObservation();
        observation = caseId switch
        {
            "identity" => observation with
            {
                Identity = ParentIdentity with { FileIdLow = 999 },
            },
            "security" => observation with { SecurityMatches = false },
            "path" => observation with
            {
                FinalPath = @"C:\ProgramData\WatchDog\Substituted",
            },
            _ => throw new InvalidOperationException(caseId),
        };

        Assert.Null(harness.Writer.AdoptParent(
            ParentSuccess(), observation, Id(20)));

        AssertWriterRejected(harness, FixtureB1JournalDiagnosticCode.WrongObject);
    }

    [Fact]
    public void AliasedFramesAndAnchorWriterOriginalsAreRejected()
    {
        var harness = ParentAcknowledged();
        var sources = Sources();
        sources[2] = sources[2] with
        {
            Observation = sources[2].Observation with
            {
                Identity = sources[1].Observation.Identity,
            },
        };

        Assert.False(harness.Writer.RegisterWriterSources(sources));

        AssertWriterRejected(harness, FixtureB1JournalDiagnosticCode.WrongObject);
    }

    [Fact]
    public void OwnerReadAdoptionRejectsWritableDuplicateAndNeverIssuesAck()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        var offer = Assert.IsType<FixtureB1JournalTransferDescriptor>(
            harness.Writer.CreateReadOffer(source.ResourceKind, Id(30), Id(31)));
        Assert.True(harness.Owner.ReserveReadTransfer(offer));
        var success = ReadSuccess(offer, 70_001, 80_001);
        Assert.True(harness.Owner.RecordReadTransferSuccess(success));
        var observation = ReadObservation(source) with
        {
            EffectiveGrantedAccess = FixtureB1JournalAccessPolicy.OwnerRootRead |
                (uint)FixtureB1NativeJournalHandleAccess.WriteDataOrAddFile,
        };

        Assert.Null(harness.Owner.AdoptRead(
            success, observation, source, Id(32)));

        AssertOwnerRejected(harness, FixtureB1JournalDiagnosticCode.ExcessRights);
        Assert.Equal(1, harness.Owner.Snapshot().OutstandingLocalCloseCount);
    }

    [Fact]
    public void SuccessfulDuplicateBeforeFailedCustodyAppendLeavesEmergencyCloseDuty()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        var offer = Assert.IsType<FixtureB1JournalTransferDescriptor>(
            harness.Writer.CreateReadOffer(source.ResourceKind, Id(40), Id(41)));
        Assert.True(harness.Owner.ReserveReadTransfer(offer));
        harness.Store.FailEvent = FixtureB1JournalCustodyEvent.TransferSuccessRecorded;

        Assert.False(harness.Owner.RecordReadTransferSuccess(
            ReadSuccess(offer, 70_002, 80_002)));

        var snapshot = harness.Owner.Snapshot();
        Assert.Equal(FixtureB1JournalEndpointState.Poisoned, snapshot.State);
        Assert.Equal(FixtureB1JournalDiagnosticCode.CustodyStoreFailure,
            snapshot.DiagnosticCode);
        Assert.Equal(1, snapshot.EmergencyObligationCount);
        Assert.Equal(1, snapshot.OutstandingLocalCloseCount);
        Assert.Null(harness.Owner.CreateWriteRelease(Id(42), Sources()));
    }

    [Fact]
    public void SourceCoordinateReuseWithSameFileIdentityPoisonsPinnedOffer()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        Assert.NotNull(harness.Writer.CreateReadOffer(
            source.ResourceKind, Id(50), Id(51)));

        Assert.False(harness.Writer.ObserveSourceLease(
            source.ResourceKind, source.SourceHandleCoordinate,
            source.SourceHandleLeaseGeneration + 1,
            source.Observation.Identity));

        AssertWriterRejected(harness, FixtureB1JournalDiagnosticCode.SourceLeaseDrift);
    }

    [Fact]
    public void LostParentAckPermanentlyClosesOwnerAdmission()
    {
        var harness = new Harness();
        Assert.True(harness.Owner.ReserveParentTransfer(ParentTransfer()));
        Assert.True(harness.Owner.RecordParentTransferSuccess(ParentSuccess()));
        Assert.NotNull(harness.Writer.AdoptParent(
            ParentSuccess(), ParentObservation(), Id(20)));

        Assert.False(harness.Owner.ReportParentAckLost(
            ParentTransfer().OperationId));

        AssertOwnerRejected(harness, FixtureB1JournalDiagnosticCode.AckLost);
        Assert.Contains(harness.Store.Entries, entry =>
            entry.Event == FixtureB1JournalCustodyEvent.AckLost);
    }

    [Fact]
    public void LostReadAckKeepsSourcePinnedAndPermanentlyClosesWriter()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        var offer = Assert.IsType<FixtureB1JournalTransferDescriptor>(
            harness.Writer.CreateReadOffer(source.ResourceKind, Id(55), Id(56)));
        Assert.True(harness.Owner.ReserveReadTransfer(offer));
        var success = ReadSuccess(offer, 70_055, 80_055);
        Assert.True(harness.Owner.RecordReadTransferSuccess(success));
        Assert.NotNull(harness.Owner.AdoptRead(
            success, ReadObservation(source), source, Id(57)));

        Assert.False(harness.Writer.ReportReadAckLost(offer.OperationId));

        AssertWriterRejected(harness, FixtureB1JournalDiagnosticCode.AckLost);
    }

    [Fact]
    public void PinnedSourceCannotCloseBeforeRemoteAdoptionTerminates()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        Assert.NotNull(harness.Writer.CreateReadOffer(
            source.ResourceKind, Id(60), Id(61)));

        Assert.False(harness.Writer.RequestSourceClose(source.ResourceKind));

        AssertWriterRejected(harness,
            FixtureB1JournalDiagnosticCode.SourceCloseBarrier);
    }

    [Fact]
    public void ReleaseArrivingAfterObservedStopCannotReopenWriter()
    {
        var harness = ReadyForRelease();

        Assert.False(harness.Writer.ObserveStop());
        Assert.False(harness.Writer.AcceptWriteRelease(harness.Release!));

        AssertWriterRejected(harness, FixtureB1JournalDiagnosticCode.StopObserved);
    }

    [Fact]
    public void StartedWriteCompletingAfterStopIsCleanupOnly()
    {
        var harness = ReadyForRelease();
        Assert.True(harness.Writer.AcceptWriteRelease(harness.Release!));
        var operation = Id(70);
        Assert.True(harness.Writer.TryStartWrite(operation));

        Assert.False(harness.Writer.ObserveStop());
        Assert.False(harness.Writer.CompleteWrite(operation));

        var snapshot = harness.Writer.Snapshot();
        Assert.Equal(FixtureB1JournalDiagnosticCode.StopObserved,
            snapshot.DiagnosticCode);
        Assert.False(snapshot.WriteInFlight);
        Assert.False(snapshot.CanWrite);
    }

    [Fact]
    public void WriterDeathAfterReadCustodyEnablesForensicBytesOnly()
    {
        var harness = ReadyForRelease();

        Assert.True(harness.Owner.ObserveWriterDeath());

        var snapshot = harness.Owner.Snapshot();
        Assert.Equal(FixtureB1JournalEndpointState.ForensicOnly, snapshot.State);
        Assert.True(snapshot.CanReadMetadata);
        Assert.True(snapshot.CanReadForensicBytes);
        Assert.Null(harness.Owner.CreateWriteRelease(Id(71), Sources()));
        Assert.False(snapshot.NativeAuthorityProven);
        Assert.False(snapshot.NativeDurabilityProven);
    }

    [Fact]
    public void WriterDeathBeforeAllReadAdoptionsCannotBeRecoveredByPath()
    {
        var harness = ParentReady();

        Assert.False(harness.Owner.ObserveWriterDeath());

        AssertOwnerRejected(harness,
            FixtureB1JournalDiagnosticCode.WriterDiedBeforeCustody);
        Assert.False(harness.Owner.Snapshot().CanReadForensicBytes);
    }

    [Fact]
    public void WriterDeathDuringTransferMakesLateSuccessCleanupOnly()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        var offer = Assert.IsType<FixtureB1JournalTransferDescriptor>(
            harness.Writer.CreateReadOffer(source.ResourceKind, Id(75), Id(76)));
        Assert.True(harness.Owner.ReserveReadTransfer(offer));
        Assert.False(harness.Owner.ObserveWriterDeath());

        Assert.False(harness.Owner.RecordReadTransferSuccess(
            ReadSuccess(offer, 70_075, 80_075)));

        AssertOwnerRejected(harness,
            FixtureB1JournalDiagnosticCode.WriterDiedBeforeCustody);
        Assert.Contains(harness.Store.Entries, entry =>
            entry.Event == FixtureB1JournalCustodyEvent.LateCompletionRecorded &&
            entry.OperationId == offer.OperationId);
        Assert.Equal(1, harness.Owner.Snapshot().OutstandingLocalCloseCount);
    }

    [Fact]
    public void OwnerDeathWhileReadSourceIsPinnedPermanentlyClosesWriter()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        Assert.NotNull(harness.Writer.CreateReadOffer(
            source.ResourceKind, Id(77), Id(78)));

        Assert.False(harness.Writer.ObserveOwnerDeath());

        AssertWriterRejected(harness, FixtureB1JournalDiagnosticCode.OwnerDied);
    }

    [Fact]
    public void SuccessCallbackAfterStopIsRecordedForCleanupButCannotBeAdopted()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        var offer = Assert.IsType<FixtureB1JournalTransferDescriptor>(
            harness.Writer.CreateReadOffer(source.ResourceKind, Id(80), Id(81)));
        Assert.True(harness.Owner.ReserveReadTransfer(offer));
        Assert.False(harness.Owner.ObserveStop());

        Assert.False(harness.Owner.RecordReadTransferSuccess(
            ReadSuccess(offer, 70_003, 80_003)));

        Assert.Contains(harness.Store.Entries, entry =>
            entry.Event == FixtureB1JournalCustodyEvent.LateCompletionRecorded &&
            entry.OperationId == offer.OperationId);
        Assert.Equal(1, harness.Owner.Snapshot().OutstandingLocalCloseCount);
    }

    [Fact]
    public void UncertainClosePreservesFirstFailureAndBlocksRelease()
    {
        var harness = ParentReady();
        CompleteRead(harness, 0);

        Assert.False(harness.Owner.ReportReadClose(
            FixtureB1JournalResourceKind.PrivateRootRead,
            FixtureB1JournalCloseDisposition.Uncertain, Id(90)));

        AssertOwnerRejected(harness, FixtureB1JournalDiagnosticCode.UncertainClose);
        Assert.Contains(harness.Store.Entries, entry =>
            entry.Event == FixtureB1JournalCustodyEvent.CloseUncertain);
    }

    [Fact]
    public void ReplayedWriteReleasePoisonsPreviouslyWritableEndpoint()
    {
        var harness = ReadyForRelease();
        Assert.True(harness.Writer.AcceptWriteRelease(harness.Release!));

        Assert.False(harness.Writer.AcceptWriteRelease(harness.Release!));

        AssertWriterRejected(harness, FixtureB1JournalDiagnosticCode.ReleaseReplay);
    }

    [Fact]
    public void SharedPositionByteReadRemainsClosedWhileWriterIsAlive()
    {
        var harness = ReadyForRelease();

        var snapshot = harness.Owner.Snapshot();

        Assert.True(snapshot.CanReadMetadata);
        Assert.False(snapshot.CanReadForensicBytes);
    }

    [Fact]
    public void MalformedReadSuccessWithWrongOperationRemainsUnconfirmed()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        var offer = Assert.IsType<FixtureB1JournalTransferDescriptor>(
            harness.Writer.CreateReadOffer(source.ResourceKind, Id(600), Id(601)));
        Assert.True(harness.Owner.ReserveReadTransfer(offer));
        var malformed = ReadSuccess(
            offer with { OperationId = Id(699) }, 76_000, 86_000);

        Assert.False(harness.Owner.RecordReadTransferSuccess(malformed));

        var captured = harness.Owner.Snapshot();
        Assert.Equal(FixtureB1JournalDiagnosticCode.InvalidTransfer,
            captured.DiagnosticCode);
        Assert.Equal(0, captured.OutstandingLocalCloseCount);
        Assert.Equal(1, captured.UnconfirmedSuccessCount);
        Assert.Equal(1, captured.EmergencyObligationCount);
        Assert.Empty(captured.UnresolvedCleanupHandles);
        Assert.False(harness.Owner.ReportCleanupClose(
            malformed, FixtureB1JournalCloseDisposition.Confirmed, Id(602)));
        Assert.Equal(0, harness.Owner.Snapshot().OutstandingLocalCloseCount);
        Assert.Equal(FixtureB1JournalDiagnosticCode.InvalidTransfer,
            harness.Owner.Snapshot().DiagnosticCode);
    }

    [Fact]
    public void MalformedSuccessAfterStopKeepsUnconfirmedEmergencyCustody()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        var offer = Assert.IsType<FixtureB1JournalTransferDescriptor>(
            harness.Writer.CreateReadOffer(source.ResourceKind, Id(610), Id(611)));
        Assert.True(harness.Owner.ReserveReadTransfer(offer));
        Assert.False(harness.Owner.ObserveStop());
        var malformed = ReadSuccess(offer, coordinate: 0, receipt: 0);

        Assert.False(harness.Owner.RecordReadTransferSuccess(malformed));

        var snapshot = harness.Owner.Snapshot();
        Assert.Equal(FixtureB1JournalDiagnosticCode.StopObserved,
            snapshot.DiagnosticCode);
        Assert.Equal(1, snapshot.UnconfirmedSuccessCount);
        Assert.Equal(1, snapshot.EmergencyObligationCount);
        Assert.Contains(snapshot.Custody, entry =>
            entry.Event == FixtureB1JournalCustodyEvent.LateCompletionRecorded &&
            entry.OperationId == offer.OperationId);
    }

    [Fact]
    public void MalformedParentSuccessAfterStopIsCapturedBeforeProtocolChecks()
    {
        var harness = new Harness();
        Assert.True(harness.Owner.ReserveParentTransfer(ParentTransfer()));
        Assert.False(harness.Owner.ObserveStop());
        var malformed = ParentSuccess() with
        {
            TargetLocalCoordinate = 0,
            TargetResourceReceipt = 0,
        };

        Assert.False(harness.Owner.RecordParentTransferSuccess(malformed));

        var snapshot = harness.Owner.Snapshot();
        Assert.Equal(FixtureB1JournalDiagnosticCode.StopObserved,
            snapshot.DiagnosticCode);
        Assert.Equal(1, snapshot.UnconfirmedSuccessCount);
        Assert.Equal(1, snapshot.EmergencyObligationCount);
        Assert.Contains(snapshot.Custody, entry =>
            entry.Event == FixtureB1JournalCustodyEvent.LateCompletionRecorded &&
            entry.ResourceKind == FixtureB1JournalResourceKind.TrustedParent);
    }

    [Fact]
    public void RejectedParentAdoptionRetainsExactlyOneWriterCloseDuty()
    {
        var harness = new Harness();
        Assert.True(harness.Owner.ReserveParentTransfer(ParentTransfer()));
        Assert.True(harness.Owner.RecordParentTransferSuccess(ParentSuccess()));
        var writable = ParentObservation() with
        {
            EffectiveGrantedAccess = FixtureB1JournalAccessPolicy.WriterParent |
                (uint)FixtureB1NativeJournalHandleAccess.WriteDataOrAddFile,
        };

        Assert.Null(harness.Writer.AdoptParent(
            ParentSuccess(), writable, Id(620)));
        Assert.Equal(1,
            harness.Writer.Snapshot().OutstandingRejectedParentCloseCount);
        Assert.True(harness.Writer.ReportRejectedParentClose(
            ParentSuccess(), FixtureB1JournalCloseDisposition.Confirmed, Id(621)));
        Assert.False(harness.Writer.ReportRejectedParentClose(
            ParentSuccess(), FixtureB1JournalCloseDisposition.Confirmed, Id(622)));
        Assert.Equal(0,
            harness.Writer.Snapshot().OutstandingRejectedParentCloseCount);
        Assert.Equal(FixtureB1JournalDiagnosticCode.ExcessRights,
            harness.Writer.Snapshot().DiagnosticCode);
    }

    [Fact]
    public void UncertainOwnerCloseRemainsUnresolvedAndCannotRetryRawHandle()
    {
        var harness = ParentReady();
        CompleteRead(harness, 0);

        Assert.False(harness.Owner.ReportReadClose(
            FixtureB1JournalResourceKind.PrivateRootRead,
            FixtureB1JournalCloseDisposition.Uncertain, Id(623)));
        var snapshot = harness.Owner.Snapshot();
        Assert.Equal(1, snapshot.OutstandingLocalCloseCount);
        Assert.Equal(1, snapshot.UncertainCloseCount);
        Assert.Single(snapshot.UnresolvedCleanupHandles);
        Assert.False(snapshot.CanReadMetadata);
        Assert.False(harness.Owner.ReportReadClose(
            FixtureB1JournalResourceKind.PrivateRootRead,
            FixtureB1JournalCloseDisposition.Confirmed, Id(624)));
        Assert.Equal(FixtureB1JournalDiagnosticCode.UncertainClose,
            harness.Owner.Snapshot().DiagnosticCode);
    }

    [Fact]
    public void UncertainRejectedParentCloseRemainsWriterCleanupDuty()
    {
        var harness = new Harness();
        Assert.True(harness.Owner.ReserveParentTransfer(ParentTransfer()));
        Assert.True(harness.Owner.RecordParentTransferSuccess(ParentSuccess()));
        var writable = ParentObservation() with
        {
            EffectiveGrantedAccess = FixtureB1JournalAccessPolicy.WriterParent |
                (uint)FixtureB1NativeJournalHandleAccess.WriteDataOrAddFile,
        };
        Assert.Null(harness.Writer.AdoptParent(
            ParentSuccess(), writable, Id(625)));

        Assert.False(harness.Writer.ReportRejectedParentClose(
            ParentSuccess(), FixtureB1JournalCloseDisposition.Uncertain, Id(626)));

        var snapshot = harness.Writer.Snapshot();
        Assert.Equal(1, snapshot.OutstandingRejectedParentCloseCount);
        Assert.Equal(1, snapshot.UncertainRejectedParentCloseCount);
        Assert.Single(snapshot.UnresolvedRejectedParentCleanupHandles);
        Assert.False(harness.Writer.ReportRejectedParentClose(
            ParentSuccess(), FixtureB1JournalCloseDisposition.Confirmed, Id(627)));
        Assert.Equal(FixtureB1JournalDiagnosticCode.ExcessRights,
            harness.Writer.Snapshot().DiagnosticCode);
    }

    [Fact]
    public void ConfirmedCloseSurvivesFailedCustodyAppendAsPhysicallyClosed()
    {
        var harness = ParentReady();
        CompleteRead(harness, 0);
        harness.Store.FailEvent = FixtureB1JournalCustodyEvent.CloseConfirmed;

        Assert.False(harness.Owner.ReportReadClose(
            FixtureB1JournalResourceKind.PrivateRootRead,
            FixtureB1JournalCloseDisposition.Confirmed, Id(628)));

        var snapshot = harness.Owner.Snapshot();
        Assert.Equal(0, snapshot.OutstandingLocalCloseCount);
        Assert.Empty(snapshot.UnresolvedCleanupHandles);
        Assert.False(snapshot.CanReadMetadata);
        Assert.False(snapshot.CanReadForensicBytes);
        Assert.Equal(1, snapshot.EmergencyObligationCount);
        Assert.Equal(FixtureB1JournalDiagnosticCode.OwnerCustodyLost,
            snapshot.DiagnosticCode);
    }

    [Fact]
    public void WrongValidParentTargetAfterStopIsUnconfirmedNotForeignCloseDuty()
    {
        var harness = new Harness();
        Assert.True(harness.Owner.ReserveParentTransfer(ParentTransfer()));
        Assert.False(harness.Owner.ObserveStop());
        var foreign = new FixtureHandoffProcessIdentity(999, 99_900);
        var malformed = ParentSuccess() with
        {
            Transfer = ParentTransfer() with
            {
                Target = foreign,
                TargetProcessLeaseGeneration = 999,
            },
        };

        Assert.False(harness.Owner.RecordParentTransferSuccess(malformed));

        var snapshot = harness.Owner.Snapshot();
        Assert.Equal(0, snapshot.OutstandingRemoteCloseCount);
        Assert.Equal(1, snapshot.UnconfirmedSuccessCount);
        Assert.Equal(1, snapshot.EmergencyObligationCount);
        Assert.Empty(snapshot.UnresolvedCleanupHandles);
        Assert.Equal(FixtureB1JournalDiagnosticCode.StopObserved,
            snapshot.DiagnosticCode);
    }

    [Fact]
    public void WrongValidReadTargetAfterStopIsUnconfirmedNotForeignCloseDuty()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        var offer = Assert.IsType<FixtureB1JournalTransferDescriptor>(
            harness.Writer.CreateReadOffer(source.ResourceKind, Id(629), Id(630)));
        Assert.True(harness.Owner.ReserveReadTransfer(offer));
        Assert.False(harness.Owner.ObserveStop());
        var foreign = new FixtureHandoffProcessIdentity(998, 99_800);
        var malformed = ReadSuccess(offer with
        {
            Target = foreign,
            TargetProcessLeaseGeneration = 998,
        }, 76_290, 86_290);

        Assert.False(harness.Owner.RecordReadTransferSuccess(malformed));

        var snapshot = harness.Owner.Snapshot();
        Assert.Equal(0, snapshot.OutstandingLocalCloseCount);
        Assert.Equal(1, snapshot.UnconfirmedSuccessCount);
        Assert.Equal(1, snapshot.EmergencyObligationCount);
        Assert.Empty(snapshot.UnresolvedCleanupHandles);
        Assert.Equal(FixtureB1JournalDiagnosticCode.StopObserved,
            snapshot.DiagnosticCode);
    }

    [Fact]
    public void WrongParentDirectionAfterStopCannotBecomeLocalCleanupDuty()
    {
        var harness = new Harness();
        Assert.True(harness.Owner.ReserveParentTransfer(ParentTransfer()));
        Assert.False(harness.Owner.ObserveStop());
        var malformed = ParentSuccess() with
        {
            Transfer = ParentTransfer() with
            {
                Direction = FixtureB1JournalChannelDirection.WriterToOwner,
            },
        };

        Assert.False(harness.Owner.RecordParentTransferSuccess(malformed));

        var snapshot = harness.Owner.Snapshot();
        Assert.Equal(0, snapshot.OutstandingLocalCloseCount);
        Assert.Equal(0, snapshot.OutstandingRemoteCloseCount);
        Assert.Equal(1, snapshot.UnconfirmedSuccessCount);
        Assert.Equal(1, snapshot.EmergencyObligationCount);
        Assert.Empty(snapshot.UnresolvedCleanupHandles);
        Assert.Equal(FixtureB1JournalDiagnosticCode.StopObserved,
            snapshot.DiagnosticCode);
    }

    [Fact]
    public void WrongReadDirectionAfterStopCannotBecomeRemoteCleanupDuty()
    {
        var harness = ParentReady();
        var source = Sources()[0];
        var offer = Assert.IsType<FixtureB1JournalTransferDescriptor>(
            harness.Writer.CreateReadOffer(source.ResourceKind, Id(632), Id(633)));
        Assert.True(harness.Owner.ReserveReadTransfer(offer));
        Assert.False(harness.Owner.ObserveStop());
        var malformed = ReadSuccess(offer with
        {
            Direction = FixtureB1JournalChannelDirection.OwnerToWriter,
        }, 76_320, 86_320);

        Assert.False(harness.Owner.RecordReadTransferSuccess(malformed));

        var snapshot = harness.Owner.Snapshot();
        Assert.Equal(0, snapshot.OutstandingLocalCloseCount);
        Assert.Equal(0, snapshot.OutstandingRemoteCloseCount);
        Assert.Equal(1, snapshot.UnconfirmedSuccessCount);
        Assert.Equal(1, snapshot.EmergencyObligationCount);
        Assert.Empty(snapshot.UnresolvedCleanupHandles);
        Assert.Equal(FixtureB1JournalDiagnosticCode.StopObserved,
            snapshot.DiagnosticCode);
    }

    [Fact]
    public void WriterRejectsWrongValidParentTargetAsUnconfirmedOnly()
    {
        var harness = new Harness();
        var foreign = new FixtureHandoffProcessIdentity(997, 99_700);
        var malformed = ParentSuccess() with
        {
            Transfer = ParentTransfer() with
            {
                Target = foreign,
                TargetProcessLeaseGeneration = 997,
            },
        };

        Assert.Null(harness.Writer.AdoptParent(
            malformed, ParentObservation(), Id(631)));

        var snapshot = harness.Writer.Snapshot();
        Assert.Equal(0, snapshot.OutstandingRejectedParentCloseCount);
        Assert.Equal(1, snapshot.UnconfirmedParentSuccessCount);
        Assert.Empty(snapshot.UnresolvedRejectedParentCleanupHandles);
        Assert.Equal(FixtureB1JournalDiagnosticCode.InvalidTransfer,
            snapshot.DiagnosticCode);
    }

    [Fact]
    public void ClosingRequiredReadBeforeReleasePermanentlyRevokesAdmission()
    {
        var harness = ParentReady();
        for (var index = 0; index < ReadResources().Length; index++)
            CompleteRead(harness, index);

        Assert.True(harness.Owner.ReportReadClose(
            FixtureB1JournalResourceKind.PrivateRootRead,
            FixtureB1JournalCloseDisposition.Confirmed, Id(630)));

        AssertOwnerRejected(harness, FixtureB1JournalDiagnosticCode.OwnerCustodyLost);
        Assert.Null(harness.Owner.CreateWriteRelease(Id(631), Sources()));
    }

    [Fact]
    public void CloseAfterReleaseBeforeWriterAcceptanceFailsFreshCustodyCheck()
    {
        var harness = ReadyForRelease();
        Assert.True(harness.Owner.ReportReadClose(
            FixtureB1JournalResourceKind.FramesRead,
            FixtureB1JournalCloseDisposition.Confirmed, Id(640)));
        harness.Authority.OwnerReadCustodyLive = false;

        Assert.False(harness.Writer.AcceptWriteRelease(harness.Release!));

        AssertWriterRejected(harness,
            FixtureB1JournalDiagnosticCode.OwnerCustodyLost);
    }

    [Fact]
    public void CloseBeforeWriterDeathRefusesForensicEligibility()
    {
        var harness = ReadyForRelease();
        Assert.True(harness.Owner.ReportReadClose(
            FixtureB1JournalResourceKind.AnchorRead,
            FixtureB1JournalCloseDisposition.Confirmed, Id(650)));
        harness.Authority.OwnerReadCustodyLive = false;

        Assert.False(harness.Owner.ObserveWriterDeath());

        var snapshot = harness.Owner.Snapshot();
        Assert.False(snapshot.CanReadForensicBytes);
        Assert.Equal(FixtureB1JournalDiagnosticCode.OwnerCustodyLost,
            snapshot.DiagnosticCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryWriteStartRechecksProviderBoundary(bool throws)
    {
        var harness = ReadyForRelease();
        Assert.True(harness.Writer.AcceptWriteRelease(harness.Release!));
        harness.Authority.WriteBoundaryLive = false;
        harness.Authority.ThrowOnWriteBoundary = throws;

        Assert.False(harness.Writer.TryStartWrite(Id(660)));

        AssertWriterRejected(harness,
            FixtureB1JournalDiagnosticCode.ReleaseAuthenticationRejected);
    }

    [Fact]
    public void ReadCustodyLossAfterReleaseAcceptanceBlocksNextWriteStart()
    {
        var harness = ReadyForRelease();
        Assert.True(harness.Writer.AcceptWriteRelease(harness.Release!));
        Assert.True(harness.Owner.ReportReadClose(
            FixtureB1JournalResourceKind.PrivateRootRead,
            FixtureB1JournalCloseDisposition.Confirmed, Id(665)));
        harness.Authority.OwnerReadCustodyLive = false;

        Assert.False(harness.Writer.TryStartWrite(Id(666)));

        AssertWriterRejected(harness,
            FixtureB1JournalDiagnosticCode.OwnerCustodyLost);
    }

    [Fact]
    public void IndependentCustodyStoreLossAfterEarlierAppendsBlocksRelease()
    {
        var harness = ParentReady();
        for (var index = 0; index < ReadResources().Length; index++)
            CompleteRead(harness, index);
        harness.Store.IndependentlyAdmitted = false;

        Assert.Null(harness.Owner.CreateWriteRelease(Id(670), Sources()));

        AssertOwnerRejected(harness,
            FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
    }

    [Fact]
    public void UnacknowledgedFinalCustodyAppendBlocksRelease()
    {
        var harness = ParentReady();
        for (var index = 0; index < ReadResources().Length; index++)
            CompleteRead(harness, index);
        harness.Store.FailEvent = FixtureB1JournalCustodyEvent.CustodyRecorded;

        Assert.Null(harness.Owner.CreateWriteRelease(Id(675), Sources()));

        AssertOwnerRejected(harness,
            FixtureB1JournalDiagnosticCode.CustodyStoreFailure);
    }

    [Theory]
    [InlineData("-leading")]
    [InlineData("wild*card")]
    [InlineData("trailing.")]
    [InlineData("contains space")]
    [InlineData("nonascii-\u00e9")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void BindingRejectsNamesOutsideNativeComponentGrammar(string invalidName)
    {
        var binding = Binding() with { PrivateRootName = invalidName };

        Assert.False(FixtureB1JournalChannelValidation.ValidBinding(binding));
    }

    [Fact]
    public void NativeComponentGrammarDoesNotAddDeviceNamePolicy()
    {
        var binding = Binding() with { PrivateRootName = "CON" };

        Assert.True(FixtureB1JournalChannelValidation.ValidBinding(binding));
    }

    private static Harness ReadyForRelease()
    {
        var harness = ParentReady();
        for (var index = 0; index < ReadResources().Length; index++)
            CompleteRead(harness, index);
        harness.Release = harness.Owner.CreateWriteRelease(Id(500), Sources());
        Assert.NotNull(harness.Release);
        return harness;
    }

    private static Harness ParentReady()
    {
        var harness = ParentAcknowledged();
        Assert.True(harness.Writer.RegisterWriterSources(Sources()));
        return harness;
    }

    private static Harness ParentAcknowledged()
    {
        var harness = new Harness();
        var transfer = ParentTransfer();
        var success = ParentSuccess();
        Assert.True(harness.Owner.ReserveParentTransfer(transfer));
        Assert.True(harness.Owner.RecordParentTransferSuccess(success));
        var ack = harness.Writer.AdoptParent(
            success, ParentObservation(), Id(20));
        Assert.NotNull(ack);
        var acceptance = harness.Owner.AcceptParentAck(ack!, Id(21));
        Assert.NotNull(acceptance);
        Assert.True(harness.Writer.AcceptParentAck(acceptance!));
        return harness;
    }

    private static void CompleteRead(Harness harness, int index)
    {
        var source = Sources()[index];
        var offer = Assert.IsType<FixtureB1JournalTransferDescriptor>(
            harness.Writer.CreateReadOffer(
                source.ResourceKind, Id(100 + index), Id(110 + index)));
        Assert.True(harness.Owner.ReserveReadTransfer(offer));
        var success = ReadSuccess(
            offer, 70_100 + index, 80_100 + index);
        Assert.True(harness.Owner.RecordReadTransferSuccess(success));
        var ack = harness.Owner.AdoptRead(
            success, ReadObservation(source), source, Id(120 + index));
        Assert.NotNull(ack);
        Assert.True(harness.Writer.AcceptReadAck(ack!));
    }

    private static FixtureB1JournalChannelBinding Binding() => new(
        ProtocolVersion: 1,
        RunNonce: Id(1),
        LaunchId: Id(2),
        WrapperInstance: Id(3),
        SessionEpoch: 4,
        ConnectionGeneration: 5,
        ExactOwner: Owner,
        OwnerProcessLeaseGeneration: 6,
        ExactWriter: Writer,
        WriterProcessLeaseGeneration: 7,
        TrustedParentLeaseGeneration: 8,
        TrustedParentIdentity: ParentIdentity,
        TrustedParentFinalPath: @"C:\ProgramData\WatchDog\Journal",
        PrivateRootName: "b1-private",
        FramesName: "frames.bin",
        AnchorName: "anchor.bin");

    private static FixtureB1JournalTransferDescriptor ParentTransfer() => new(
        Binding(), Id(10), Id(11), 0,
        FixtureB1JournalChannelDirection.OwnerToWriter,
        FixtureB1JournalResourceKind.TrustedParent,
        Owner, 6, Writer, 7, 60_001, 8,
        FixtureB1JournalAccessPolicy.WriterParent);

    private static FixtureB1JournalTransferSuccess ParentSuccess() =>
        new(ParentTransfer(), 70_000, 80_000);

    private static FixtureB1JournalObjectObservation ParentObservation() => new(
        ParentIdentity, null, "Journal", @"C:\ProgramData\WatchDog\Journal",
        FixtureB1JournalAccessPolicy.WriterParent,
        NonInheritable: true, SecurityMatches: true, NumberOfLinks: 1,
        DeletePending: false, HasUnexpectedStreams: false);

    private static FixtureB1JournalSourceLease[] Sources() =>
    [
        new(FixtureB1JournalResourceKind.PrivateRootRead,
            61_001, 101, 81_001,
            WriterObservation(RootIdentity, ParentIdentity, "b1-private",
                @"C:\ProgramData\WatchDog\Journal\b1-private")),
        new(FixtureB1JournalResourceKind.FramesRead,
            61_002, 102, 81_002,
            WriterObservation(FramesIdentity, RootIdentity, "frames.bin",
                @"C:\ProgramData\WatchDog\Journal\b1-private\frames.bin")),
        new(FixtureB1JournalResourceKind.AnchorRead,
            61_003, 103, 81_003,
            WriterObservation(AnchorIdentity, RootIdentity, "anchor.bin",
                @"C:\ProgramData\WatchDog\Journal\b1-private\anchor.bin")),
    ];

    private static FixtureB1JournalObjectObservation WriterObservation(
        FixtureArtifactIdentity identity,
        FixtureArtifactIdentity parent,
        string name,
        string path) => new(
        identity, parent, name, path,
        (uint)(FixtureB1NativeJournalHandleAccess.ReadDataOrListDirectory |
               FixtureB1NativeJournalHandleAccess.WriteDataOrAddFile |
               FixtureB1NativeJournalHandleAccess.AppendDataOrAddSubdirectory |
               FixtureB1NativeJournalHandleAccess.ReadAttributes |
               FixtureB1NativeJournalHandleAccess.ReadControl |
               FixtureB1NativeJournalHandleAccess.Synchronize),
        NonInheritable: true, SecurityMatches: true, NumberOfLinks: 1,
        DeletePending: false, HasUnexpectedStreams: false);

    private static FixtureB1JournalObjectObservation ReadObservation(
        FixtureB1JournalSourceLease source) => source.Observation with
        {
            EffectiveGrantedAccess =
            source.ResourceKind == FixtureB1JournalResourceKind.PrivateRootRead
                ? FixtureB1JournalAccessPolicy.OwnerRootRead
                : FixtureB1JournalAccessPolicy.OwnerFileRead,
        };

    private static FixtureB1JournalTransferSuccess ReadSuccess(
        FixtureB1JournalTransferDescriptor offer,
        long coordinate,
        long receipt) => new(offer, coordinate, receipt);

    private static FixtureB1JournalResourceKind[] ReadResources() =>
    [
        FixtureB1JournalResourceKind.PrivateRootRead,
        FixtureB1JournalResourceKind.FramesRead,
        FixtureB1JournalResourceKind.AnchorRead,
    ];

    private static Guid Id(int value) =>
        new(value, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);

    private static void AssertOwnerRejected(
        Harness harness,
        FixtureB1JournalDiagnosticCode diagnostic)
    {
        var snapshot = harness.Owner.Snapshot();
        Assert.Equal(FixtureB1JournalEndpointState.Poisoned, snapshot.State);
        Assert.Equal(diagnostic, snapshot.DiagnosticCode);
        Assert.False(snapshot.CanReadForensicBytes);
        Assert.False(snapshot.NativeAuthorityProven);
    }

    private static void AssertWriterRejected(
        Harness harness,
        FixtureB1JournalDiagnosticCode diagnostic)
    {
        var snapshot = harness.Writer.Snapshot();
        Assert.Equal(FixtureB1JournalEndpointState.Poisoned, snapshot.State);
        Assert.Equal(diagnostic, snapshot.DiagnosticCode);
        Assert.False(snapshot.CanWrite);
        Assert.False(snapshot.NativeAuthorityProven);
    }

    private sealed class Harness
    {
        internal Harness()
        {
            Authority = new();
            Store = new();
            Owner = new(Binding(), Authority, Store);
            Writer = new(Binding(), Authority);
        }

        internal FakeAuthority Authority { get; }
        internal FakeStore Store { get; }
        internal FixtureB1JournalOwnerEndpoint Owner { get; }
        internal FixtureB1JournalWriterEndpoint Writer { get; }
        internal FixtureB1JournalWriteRelease? Release { get; set; }
    }

    private sealed class FakeStore : IFixtureB1JournalOwnerCustodyStore
    {
        internal List<FixtureB1JournalCustodyEntry> Entries { get; } = [];
        internal FixtureB1JournalCustodyEvent? FailEvent { get; set; }
        internal bool IndependentlyAdmitted { get; set; } = true;

        public bool IsIndependentlyAdmitted(
            FixtureB1JournalChannelBinding binding) =>
            IndependentlyAdmitted && binding == Binding();

        public bool TryAppendAcknowledged(FixtureB1JournalCustodyEntry entry)
        {
            if (entry.Event == FailEvent)
                return false;
            Entries.Add(entry);
            return true;
        }
    }

    private sealed class FakeAuthority : IFixtureB1JournalOwnerChannelAuthority
    {
        internal bool BootstrapAuthenticated { get; set; } = true;
        internal bool ReleaseAuthenticated { get; set; } = true;
        internal bool WriteBoundaryLive { get; set; } = true;
        internal bool ThrowOnWriteBoundary { get; set; }
        internal bool OwnerReadCustodyLive { get; set; } = true;

        public bool IsBootstrapAuthenticated(FixtureB1JournalChannelBinding binding) =>
            BootstrapAuthenticated && binding == Binding();

        public bool IsTransferAuthenticated(
            FixtureB1JournalTransferDescriptor transfer) =>
            transfer.Binding == Binding();

        public bool IsExactParentAdoption(
            FixtureB1JournalTransferSuccess success,
            FixtureB1JournalObjectObservation observation) =>
            success == ParentSuccess() && observation.Identity == ParentIdentity;

        public bool IsExactReadAdoption(
            FixtureB1JournalTransferSuccess success,
            FixtureB1JournalObjectObservation observation,
            FixtureB1JournalSourceLease source) =>
            success.Transfer.ResourceKind == source.ResourceKind &&
            observation.Identity == source.Observation.Identity;

        public bool IsAckAuthenticated(FixtureB1JournalChannelAck ack) =>
            ack.Binding == Binding();

        public bool IsAckAcceptanceAuthenticated(
            FixtureB1JournalAckAcceptance acceptance) =>
            acceptance.Ack.Binding == Binding();

        public bool IsExactWriterSourceSet(
            FixtureB1JournalChannelBinding binding,
            IReadOnlyList<FixtureB1JournalSourceLease> sources) =>
            binding == Binding() && sources.Count == 3;

        public bool IsWriteReleaseAuthenticated(
            FixtureB1JournalWriteRelease release) =>
            ReleaseAuthenticated && release.Binding == Binding();

        public bool IsWriteBoundaryLiveAndOwned(
            FixtureB1JournalChannelBinding binding,
            IReadOnlyList<FixtureB1JournalSourceLease> sources)
        {
            if (ThrowOnWriteBoundary)
                throw new IOException("write-boundary observation failed");
            return WriteBoundaryLive && binding == Binding() && sources.Count == 3;
        }

        public bool IsOwnerReadCustodyLive(
            FixtureB1JournalChannelBinding binding,
            IReadOnlyList<FixtureB1JournalSourceLease> sources) =>
            OwnerReadCustodyLive && binding == Binding() && sources.Count == 3;

        public bool IsExactProcessTerminated(
            FixtureHandoffProcessIdentity process) =>
            process == Owner || process == Writer;
    }
}
