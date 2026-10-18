using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static class InMemorySupervisionDriverChecks
{
    internal static int Run()
    {
        var positive = new List<string>();
        var rejected = new List<string>();
        var incomplete = new List<string>();
        try
        {
            CheckFullSuccessJourney(positive);
            CheckStopDeadlinePendingAndMailbox(positive, rejected, incomplete);
            CheckStopBeforeCommitAndHorizon(rejected, incomplete);
            CheckInterledgerCommitFailure(rejected, incomplete);
            CheckPostAbortCleanupGrace(positive, rejected, incomplete);
            CheckMalformedHintIsolationAndLaterExactDelivery(positive, rejected, incomplete);
            CheckStoppedTimelyAcquisitionAndCutoffEquality(rejected, incomplete);
            CheckNormalSampleCutoffProjectionGuards(rejected, incomplete);
            CheckWireCorrelationReplayAndClaims(rejected, incomplete);
            CheckUnexpectedPositiveCoordinates(rejected, incomplete);
            CheckOversizedAnticipatedPayloads(rejected, incomplete);
            CheckInspectionExhaustion(rejected, incomplete);
            CheckQueueAndAttemptBounds(positive, rejected, incomplete);
            CheckUnknownOverflowRetainsPending(positive, incomplete);
            CheckPendingProjectionCapacityGuard(rejected, incomplete);
            CheckKnownKindUnknownDeduplication(positive, incomplete);
            CheckStartupRawEvidenceAndMailboxStates(positive, rejected, incomplete);
            CheckPersistentUnknownSnapshots(positive, incomplete);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "watchdog.ow-in-memory-supervision-driver-checks.v1",
                succeeded = true,
                syntheticOnly = true,
                nativeCallsPerformed = false,
                nativeCapturePerformed = false,
                peerAuthenticated = false,
                nativeAuthorityProven = false,
                nativeDurabilityProven = false,
                completeHandleInventoryProven = false,
                zeroExtraInheritanceProven = false,
                productionAdmissionGranted = false,
                b1WriteReleaseIssued = false,
                b1JournalWritesPerformed = false,
                releaseApproved = false,
                positiveChecks = positive.Count,
                rejectionChecks = rejected.Count,
                incompleteChecks = incomplete.Count,
                positiveCases = positive,
                rejectionCases = rejected,
                incompleteCases = incomplete,
            }, JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "watchdog.ow-in-memory-supervision-driver-checks.v1",
                succeeded = false,
                syntheticOnly = true,
                nativeCallsPerformed = false,
                nativeCapturePerformed = false,
                peerAuthenticated = false,
                nativeAuthorityProven = false,
                nativeDurabilityProven = false,
                completeHandleInventoryProven = false,
                zeroExtraInheritanceProven = false,
                productionAdmissionGranted = false,
                b1WriteReleaseIssued = false,
                b1JournalWritesPerformed = false,
                releaseApproved = false,
                positiveChecks = positive.Count,
                rejectionChecks = rejected.Count,
                incompleteChecks = incomplete.Count,
                positiveCases = positive,
                rejectionCases = rejected,
                incompleteCases = incomplete,
                failure = $"{exception.GetType().Name}: {exception.Message}",
            }, JsonOptions));
            return 1;
        }
    }

    private static void CheckFullSuccessJourney(List<string> positive)
    {
        var fixture = NewFixture();
        var markerOperation = FixtureGuid(100);
        Commit(fixture, markerOperation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        Complete(fixture, markerOperation, SupervisionCallKind.AcquireMarker, 200, 201,
            ObserverResourceLifecycle.Success, [701]);

        var snapshotOperation = FixtureGuid(110);
        Commit(fixture, snapshotOperation, SupervisionCallKind.AcquireSnapshot, 300, Payload(0, 0, 2));
        Complete(fixture, snapshotOperation, SupervisionCallKind.AcquireSnapshot, 400, 401,
            ObserverResourceLifecycle.Success, [702]);

        var walkOperation = FixtureGuid(120);
        Commit(fixture, walkOperation, SupervisionCallKind.Walk, 500, Payload(2, 16, 3));
        SendReturn(fixture, walkOperation, SupervisionCallKind.Walk, ObserverResourceLifecycle.Success,
            [], null, 550, ownerTicks: 50);
        SendRows(fixture, walkOperation, 0, true,
            [new DiagnosticValueRow(0, "alpha"), new DiagnosticValueRow(1, "beta")], 560);
        SendDelivery(fixture, walkOperation, SupervisionCallKind.Walk, 570);

        var exhaustedOperation = FixtureGuid(130);
        Commit(fixture, exhaustedOperation, SupervisionCallKind.Walk, 600, Payload(0, 0, 2));
        Complete(fixture, exhaustedOperation, SupervisionCallKind.Walk, 650, 660,
            ObserverResourceLifecycle.NoMoreItems, []);

        var marker = fixture.Driver.Snapshot().Supervision.Observer.Marker;
        Require(marker is not null, "Successful marker acquisition did not retain a descriptor.");
        var releaseMarker = FixtureGuid(140);
        Commit(fixture, releaseMarker, SupervisionCallKind.ReleaseMarker, 700,
            SupervisionPayloadBudget.Empty, marker);
        Complete(fixture, releaseMarker, SupervisionCallKind.ReleaseMarker, 710, 720,
            ObserverResourceLifecycle.Success, [marker!.Coordinate], marker);

        var snapshot = fixture.Driver.Snapshot().Supervision.Observer.Snapshot;
        Require(snapshot is not null, "Successful snapshot acquisition did not retain a descriptor.");
        var releaseSnapshot = FixtureGuid(150);
        Commit(fixture, releaseSnapshot, SupervisionCallKind.ReleaseSnapshot, 800,
            SupervisionPayloadBudget.Empty, snapshot);
        Complete(fixture, releaseSnapshot, SupervisionCallKind.ReleaseSnapshot, 810, 820,
            ObserverResourceLifecycle.Success, [snapshot!.Coordinate], snapshot);

        var state = fixture.Driver.Snapshot();
        Require(state.Operations.Count == 6 &&
                state.Operations.All(item => item.Stage == InMemorySupervisionOperationStage.Completed) &&
                state.Supervision.Observer.WalkExhausted &&
                state.Supervision.Observer.MarkerState == ObserverResourceState.Released &&
                state.Supervision.Observer.SnapshotState == ObserverResourceState.Released &&
                state.Accounting.Rows == 2 && state.Accounting.CopiedBytes == 9,
            "The complete acquire/walk/exhaust/free journey did not close exactly.");
        Require(!state.NativeCallsPerformed && !state.PeerAuthenticated &&
                !state.ProductionAdmissionGranted && !state.ReleaseApproved,
            "A synthetic journey promoted a proof flag.");
        positive.Add("full-acquire-walk-rows-exhaust-and-free");
        positive.Add("single-driver-accountant-retains-actual-row-and-wire-evidence");
    }

    private static void CheckStopDeadlinePendingAndMailbox(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(200);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        Require(fixture.Driver.RequestStop(FixtureGuid(201), 200).Accepted,
            "Stop after commitment was not accepted.");
        Require(fixture.Driver.ObserveDeadline(operation, FixtureGuid(202), 5_100).Accepted,
            "The supplied equality deadline was not accepted.");
        SendReturn(fixture, operation, SupervisionCallKind.AcquireMarker,
            ObserverResourceLifecycle.Success, [801], null, 5_150, ownerTicks: 25);
        var delivered = SendDelivery(fixture, operation, SupervisionCallKind.AcquireMarker, 5_160);
        Require(delivered.Accepted &&
                delivered.State.Operations.Single(item => item.OperationId == operation).Stage ==
                InMemorySupervisionOperationStage.CompletionPending,
            "A P-late O-early return was not retained as pending.");
        var pendingHeader = delivered.State.Operations.Single(item => item.OperationId == operation)
            .OriginalReturnHeader;
        var pendingReturn = delivered.State.Operations.Single(item => item.OperationId == operation)
            .OriginalReturn;
        Require(pendingHeader is not null && pendingHeader.Sequence == 3 &&
                pendingReturn is not null && pendingReturn.OwnerObservedTicks == 25 &&
                pendingReturn.OwnerClockDomain == OClockDomain,
            "The original wire identity or O-clock tuple was rewritten by auxiliary projection.");
        Require(fixture.Driver.DeliverPending(operation, FixtureGuid(203), 5_170).Accepted,
            "A separately supplied strictly later pending delivery was rejected.");
        var repeated = fixture.Driver.DeliverPending(operation, FixtureGuid(204), 5_171);
        Require(!repeated.Accepted, "A pending completion was delivered twice.");
        rejected.Add("pending-delivery-is-one-use-and-requires-a-separate-event");

        var report = fixture.Driver.PublishReport(FixtureGuid(205), FixtureGuid(206), 30_200,
            "supplied-cutoff", destinationLength: 1);
        Require(report.Accepted && report.State.Mailbox is { WireSerializationSucceeded: false } &&
                report.PublishedFrame is null,
            "A bounded mailbox did not survive final-frame serialization failure.");
        var firstSnapshot = fixture.Driver.Snapshot();
        var secondSnapshot = fixture.Driver.Snapshot();
        Require(firstSnapshot.Mailbox is not null && secondSnapshot.Mailbox is not null &&
                !ReferenceEquals(firstSnapshot.Mailbox.Unknowns, secondSnapshot.Mailbox.Unknowns) &&
                !ReferenceEquals(firstSnapshot.Mailbox.Tickets, secondSnapshot.Mailbox.Tickets) &&
                !ReferenceEquals(firstSnapshot.Mailbox.OutstandingOperations,
                    secondSnapshot.Mailbox.OutstandingOperations) &&
                !ReferenceEquals(firstSnapshot.Mailbox.UndeliveredRawReturns,
                    secondSnapshot.Mailbox.UndeliveredRawReturns) &&
                !ReferenceEquals(firstSnapshot.Mailbox.RawUncertainties,
                    secondSnapshot.Mailbox.RawUncertainties),
            "Mailbox snapshots exposed mutable collection aliases.");
        Require(!fixture.Driver.PublishReport(FixtureGuid(207), FixtureGuid(208), 30_201,
                "rewrite-forbidden").Accepted,
            "An available mailbox was overwritten.");
        positive.Add("deadline-equality-and-o-early-p-late-retain-original-evidence");
        positive.Add("pending-completion-requires-strictly-later-supplied-p-event");
        incomplete.Add("report-mailbox-survives-final-frame-serialization-failure");
        rejected.Add("single-immutable-mailbox-rejects-rewrite");
    }

    private static void CheckStopBeforeCommitAndHorizon(List<string> rejected, List<string> incomplete)
    {
        var stopped = NewFixture();
        Require(stopped.Driver.RequestStop(FixtureGuid(300), 10).Accepted,
            "Stop before commitment was rejected.");
        var stoppedState = stopped.Driver.Snapshot();
        Require(stoppedState.SampleStartedTicks is null && stoppedState.OuterReportCutoffTicks == 30_010,
            "Stop before commitment invented a sample start or lost the abort+30 cutoff.");
        Require(!stopped.Driver.CommitCommand(FixtureGuid(301), FixtureGuid(302),
                SupervisionCallKind.AcquireMarker, Payload(0, 0, 2), 11).Accepted,
            "Stop before commitment admitted an ordinary command.");
        var unsampledReport = stopped.Driver.PublishReport(FixtureGuid(303), FixtureGuid(304), 30_010,
            "unsampled-stop-cutoff");
        Require(unsampledReport.Accepted && unsampledReport.State.Mailbox is not null,
            "The supplied abort+30 cutoff could not finalize an unsampled stop.");

        var overflow = NewFixture();
        var before = overflow.Driver.Snapshot();
        var horizon = overflow.Driver.CommitCommand(FixtureGuid(305), FixtureGuid(306),
            SupervisionCallKind.AcquireMarker, Payload(0, 0, 2), long.MaxValue - 1);
        var after = overflow.Driver.Snapshot();
        Require(!horizon.Accepted && after.SampleStartedTicks is null &&
                after.Operations.Count == before.Operations.Count &&
                after.LastSuppliedPTicks == before.LastSuppliedPTicks,
            "A checked horizon overflow mutated command state.");
        rejected.Add("stop-before-commitment-closes-ordinary-admission");
        rejected.Add("checked-horizon-overflow-precedes-mutation");
        incomplete.Add("unsampled-stop-report-remains-synthetic-and-unqualified");
    }

    private static void CheckInterledgerCommitFailure(List<string> rejected, List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(400);
        var failed = fixture.Driver.CommitCommand(operation, FixtureGuid(401),
            SupervisionCallKind.AcquireMarker, Payload(0, 0, 2), 100, destinationLength: 1);
        var state = fixture.Driver.Snapshot();
        Require(!failed.Accepted && state.Operations.Single().Stage ==
                InMemorySupervisionOperationStage.Unresolved &&
                state.PublicationTranscript.Calls == 1 && state.ValidationTranscript.Calls == 0 &&
                state.Supervision.State == CollectorSupervisionState.CallInFlight,
            "A publication/model/validation partial failure was rolled back or hidden.");
        Require(!fixture.Driver.CommitCommand(operation, FixtureGuid(402),
                SupervisionCallKind.AcquireMarker, Payload(0, 0, 2), 101).Accepted,
            "An irreversibly committed operation token was retried.");
        rejected.Add("post-commit-encoding-failure-is-sticky-without-refund");
        incomplete.Add("interledger-partial-failure-retains-ticket-and-inflight-model");
    }

    private static void CheckPostAbortCleanupGrace(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var within = NewFixtureWithResourcesAndExhaustion();
        var beforeStop = within.Driver.Snapshot();
        var marker = beforeStop.Supervision.Observer.Marker;
        Require(marker is not null, "Cleanup fixture did not retain the marker descriptor.");
        Require(within.Driver.RequestStop(FixtureGuid(500), 1_000).Accepted,
            "Cleanup fixture stop was rejected.");
        var release = FixtureGuid(501);
        var beforeRelease = within.Driver.Snapshot().Accounting;
        Commit(within, release, SupervisionCallKind.ReleaseMarker, 1_100,
            SupervisionPayloadBudget.Empty, marker);
        SendReturn(within, release, SupervisionCallKind.ReleaseMarker,
            ObserverResourceLifecycle.Success, [marker!.Coordinate], marker, 1_200, ownerTicks: 100);
        var delivered = SendDelivery(within, release, SupervisionCallKind.ReleaseMarker, 1_300);
        Require(delivered.Accepted && delivered.State.Accounting.FreeActualEvents == 2 &&
                delivered.State.Accounting.OrdinaryActualEvents == beforeRelease.OrdinaryActualEvents + 1 &&
                delivered.State.Accounting.FinalActualEvents == 0,
            "Post-abort free return/delivery did not consume its protected holds exactly once.");
        positive.Add("post-abort-exact-free-return-and-delivery-use-protected-holds");

        var equality = NewFixtureWithResourcesAndExhaustion();
        var equalityMarker = equality.Driver.Snapshot().Supervision.Observer.Marker;
        Require(equalityMarker is not null && equality.Driver.RequestStop(FixtureGuid(510), 1_000).Accepted,
            "Grace equality fixture setup failed.");
        var denied = equality.Driver.CommitCommand(FixtureGuid(511), FixtureGuid(512),
            SupervisionCallKind.ReleaseMarker, SupervisionPayloadBudget.Empty, 6_000, equalityMarker);
        Require(!denied.Accepted && denied.Reason == "cleanup-dispatch-at-or-after-cutoff",
            "Cleanup dispatch at grace equality was admitted.");
        rejected.Add("cleanup-dispatch-at-grace-equality-is-late");

        var escalated = NewFixture();
        var acquisition = FixtureGuid(520);
        Commit(escalated, acquisition, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        Require(escalated.Driver.RequestStop(FixtureGuid(521), 200).Accepted &&
                escalated.Driver.ObserveDeadline(acquisition, FixtureGuid(522), 5_100).Accepted,
            "Escalation fixture did not reach sticky abort.");
        var escalation = escalated.Driver.EscalateOwnerTermination(FixtureGuid(523), FixtureGuid(524),
            5_200, 0);
        Require(escalation.Accepted || escalation.State.AdmissionClosed,
            "Escalation did not close dispatch even when the pure model rejected it.");
        Require(!escalated.Driver.CommitCommand(FixtureGuid(525), FixtureGuid(526),
                SupervisionCallKind.ReleaseMarker, SupervisionPayloadBudget.Empty, 5_201,
                new ObserverDescriptor(ObserverResourceKind.Marker, 1, Owner, acquisition, true)).Accepted,
            "Dispatch reopened after escalation.");
        incomplete.Add("o-only-escalation-never-represents-w-cleanup");
        rejected.Add("no-dispatch-after-escalation");
    }

    private static void CheckMalformedHintIsolationAndLaterExactDelivery(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(550);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        var before = fixture.Driver.Snapshot().Operations.Single(item => item.OperationId == operation);
        var malformed = new byte[sizeof(uint)];
        Require(fixture.Driver.Enqueue(new InMemorySuppliedFrame(malformed, operation)).Accepted,
            "Malformed hint-spoof frame did not enter the bounded queue.");
        var denied = fixture.Driver.DrainNext(200);
        var afterMalformed = denied.State.Operations.Single(item => item.OperationId == operation);
        Require(!denied.Accepted && afterMalformed.ReturnReservationHeld == before.ReturnReservationHeld &&
                afterMalformed.DeliveryReservationHeld == before.DeliveryReservationHeld &&
                afterMalformed.Stage == InMemorySupervisionOperationStage.Committed,
            "Malformed traffic spent a completion or delivery hold using an untrusted hint.");
        Require(fixture.Driver.ObserveDeadline(operation, FixtureGuid(551), 5_100).Accepted,
            "The exact operation could not advance to its supplied deadline after malformed traffic.");
        SendReturn(fixture, operation, SupervisionCallKind.AcquireMarker,
            ObserverResourceLifecycle.Success, [3_501], null, 5_150, ownerTicks: 15);
        var delivered = SendDelivery(fixture, operation, SupervisionCallKind.AcquireMarker, 5_160);
        Require(delivered.Accepted &&
                delivered.State.Operations.Single(item => item.OperationId == operation).Stage ==
                InMemorySupervisionOperationStage.CompletionPending,
            "Exact later return/delivery could not use its still-held credits.");
        positive.Add("strict-exact-return-transfers-precommitted-hold-after-independent-parse");
        rejected.Add("malformed-operation-hint-cannot-spend-completion-or-delivery-credit");
        incomplete.Add("malformed-frame-latches-abort-without-proving-drain");
    }

    private static void CheckStoppedTimelyAcquisitionAndCutoffEquality(
        List<string> rejected,
        List<string> incomplete)
    {
        var stopped = NewFixture();
        var operation = FixtureGuid(570);
        Commit(stopped, operation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        Require(stopped.Driver.RequestStop(FixtureGuid(571), 200).Accepted,
            "Stopped timely-acquisition fixture did not accept stop.");
        SendReturn(stopped, operation, SupervisionCallKind.AcquireMarker,
            ObserverResourceLifecycle.Success, [3_701], null, 300, ownerTicks: 30);
        var delivery = SendDelivery(stopped, operation, SupervisionCallKind.AcquireMarker, 400);
        Require(!delivery.Accepted &&
                delivery.Reason == "stopped-nonrelease-delivery-without-model-timeout" &&
                delivery.State.Supervision.State == CollectorSupervisionState.CallInFlight &&
                delivery.State.Supervision.Observer.Marker is null,
            "Wrapper stop projected a timely ordinary acquisition into a non-aborted pure model.");
        var stoppedReport = stopped.Driver.PublishReport(FixtureGuid(572), FixtureGuid(573), 30_200,
            "stopped-raw-delivery-cutoff");
        Require(stoppedReport.Accepted && stoppedReport.State.Mailbox is not null &&
                stoppedReport.State.Mailbox.UndeliveredRawReturns.Any(item =>
                    item.OperationId == operation &&
                    item.OriginalReturn.PositiveCoordinates.Contains(3_701)),
            "Stopped raw delivery omitted its unresolved original first-return tuple from the mailbox.");
        rejected.Add("stopped-nonrelease-delivery-remains-raw-without-independent-model-timeout");

        var sampleEquality = NewFixture();
        var inFlight = FixtureGuid(580);
        Commit(sampleEquality, inFlight, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        var accountingBefore = sampleEquality.Driver.Snapshot().Accounting;
        var dispatchAtEquality = sampleEquality.Driver.CommitCommand(FixtureGuid(581), FixtureGuid(582),
            SupervisionCallKind.AcquireSnapshot, Payload(0, 0, 2), 15_100);
        Require(!dispatchAtEquality.Accepted && dispatchAtEquality.Reason ==
                "dispatch-at-or-after-sample-cutoff" &&
                dispatchAtEquality.State.Operations.Count == 1 &&
                dispatchAtEquality.State.Accounting == accountingBefore,
            "Dispatch at sample-cutoff equality mutated reservations.");
        rejected.Add("dispatch-at-sample-cutoff-equality-is-guarded-before-reservation");

        var projectionEquality = NewFixture();
        var late = FixtureGuid(590);
        Commit(projectionEquality, late, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        Require(projectionEquality.Driver.RequestStop(FixtureGuid(591), 200).Accepted &&
                projectionEquality.Driver.ObserveDeadline(late, FixtureGuid(592), 5_100).Accepted,
            "Projection equality fixture did not reach independent model timeout.");
        SendReturn(projectionEquality, late, SupervisionCallKind.AcquireMarker,
            ObserverResourceLifecycle.Success, [3_901], null, 5_150, ownerTicks: 35);
        var atGrace = SendDelivery(projectionEquality, late, SupervisionCallKind.AcquireMarker, 5_200);
        Require(!atGrace.Accepted && atGrace.Reason == "delivery-projection-closed-or-outside-horizon" &&
                atGrace.State.Supervision.PendingCompletion is null,
            "Delivery at cleanup-grace equality mutated the pure pending slot.");
        rejected.Add("result-projection-at-grace-equality-is-late");
        incomplete.Add("stopped-and-cutoff-deliveries-remain-outstanding-raw-evidence");
    }

    private static void CheckNormalSampleCutoffProjectionGuards(
        List<string> rejected,
        List<string> incomplete)
    {
        var firstReturn = NewFixture();
        var acquisition = FixtureGuid(593);
        Commit(firstReturn, acquisition, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        var firstReturnCutoff = firstReturn.Driver.Snapshot().OuterReportCutoffTicks;
        if (firstReturnCutoff is null)
            throw new InvalidOperationException("First-return fixture did not expose its sample cutoff.");
        var lateReturn = Send(firstReturn, FirstReturnEnvelope(firstReturn, acquisition,
            SupervisionCallKind.AcquireMarker, ObserverResourceLifecycle.Success, [3_930], null, 40),
            firstReturnCutoff.Value, acquisition);
        var lateReturnOperation = lateReturn.State.Operations.Single(item => item.OperationId == acquisition);
        Require(!lateReturn.Accepted && lateReturn.Reason == "first-return-outside-projection-horizon" &&
                lateReturnOperation.Stage == InMemorySupervisionOperationStage.Unresolved &&
                lateReturnOperation.OriginalReturn is not null &&
                lateReturn.State.Supervision.Observer.Marker is null &&
                lateReturn.State.Supervision.State == CollectorSupervisionState.CallInFlight,
            "A first return at the normal sample cutoff was projected or its raw tuple was lost.");
        rejected.Add("first-return-at-normal-sample-cutoff-is-unresolved-before-projection");

        var rows = NewFixtureWithResources();
        var walk = FixtureGuid(594);
        Commit(rows, walk, SupervisionCallKind.Walk, 300, Payload(2, 9, 3));
        SendReturn(rows, walk, SupervisionCallKind.Walk, ObserverResourceLifecycle.Success,
            [], null, 400, ownerTicks: 41);
        var rowsCutoff = rows.Driver.Snapshot().OuterReportCutoffTicks;
        if (rowsCutoff is null)
            throw new InvalidOperationException("Rows fixture did not expose its sample cutoff.");
        var lateRowsEnvelope = DiagnosticEnvelope.Create(
            Header(rows, DiagnosticEnvelopeKind.ValueRowBatch, walk, EventGuid(walk, 3)),
            new DiagnosticValueRowBatch(Owner, 0, true,
                [new DiagnosticValueRow(0, "alpha"), new DiagnosticValueRow(1, "beta")],
                DiagnosticProofClaims.False));
        var lateRows = Send(rows, lateRowsEnvelope, rowsCutoff.Value, walk, anticipatedRows: 2,
            anticipatedCopiedBytes: 9);
        var lateRowsOperation = lateRows.State.Operations.Single(item => item.OperationId == walk);
        Require(!lateRows.Accepted && lateRows.Reason == "rows-outside-projection-horizon" &&
                lateRowsOperation.Stage == InMemorySupervisionOperationStage.Unresolved &&
                lateRowsOperation.OriginalReturn is not null && lateRowsOperation.Rows == 0 &&
                lateRows.State.Supervision.State == CollectorSupervisionState.CallInFlight,
            "Rows at the normal sample cutoff mutated the retained payload or pure model.");
        rejected.Add("rows-at-normal-sample-cutoff-are-unresolved-before-projection");

        var release = NewFixtureWithResourcesAndExhaustion();
        var marker = release.Driver.Snapshot().Supervision.Observer.Marker;
        Require(marker is not null, "Sample-cutoff release fixture did not acquire its marker.");
        var releaseOperation = FixtureGuid(596);
        Commit(release, releaseOperation, SupervisionCallKind.ReleaseMarker, 500,
            SupervisionPayloadBudget.Empty, marker);
        SendReturn(release, releaseOperation, SupervisionCallKind.ReleaseMarker,
            ObserverResourceLifecycle.Success, [marker!.Coordinate], marker, 600, ownerTicks: 42);
        var releaseCutoff = release.Driver.Snapshot().OuterReportCutoffTicks;
        if (releaseCutoff is null)
            throw new InvalidOperationException("Release fixture did not expose its sample cutoff.");
        var lateDelivery = SendDelivery(release, releaseOperation, SupervisionCallKind.ReleaseMarker,
            releaseCutoff.Value);
        var lateReleaseOperation = lateDelivery.State.Operations.Single(item => item.OperationId == releaseOperation);
        Require(!lateDelivery.Accepted &&
                lateDelivery.Reason == "delivery-projection-closed-or-outside-horizon" &&
                lateReleaseOperation.Stage == InMemorySupervisionOperationStage.Unresolved &&
                lateReleaseOperation.OriginalReturn is not null &&
                lateReleaseOperation.OriginalDeliveryHeader is not null &&
                lateDelivery.State.Supervision.Observer.MarkerState != ObserverResourceState.Released &&
                lateDelivery.State.Supervision.Observer.Marker?.Coordinate == marker.Coordinate,
            "A free delivery at the normal sample cutoff projected release or lost raw evidence.");
        rejected.Add("free-delivery-at-normal-sample-cutoff-is-unresolved-before-projection");
        incomplete.Add("normal-cutoff-first-return-rows-and-free-delivery-retain-unresolved-raw-state");
    }

    private static void CheckWireCorrelationReplayAndClaims(List<string> rejected, List<string> incomplete)
    {
        var drift = NewFixture();
        var operation = FixtureGuid(600);
        Commit(drift, operation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        var wrongContext = FirstReturnEnvelope(drift, operation, SupervisionCallKind.AcquireMarker,
            ObserverResourceLifecycle.Success, [901], null, 50) with
        {
            Header = Header(drift, DiagnosticEnvelopeKind.FirstRawReturn, operation, FixtureGuid(601)) with
            {
                ContextDigest = new string('f', 64),
            },
        };
        var rejectedDrift = Send(drift, wrongContext, 200, operation);
        Require(!rejectedDrift.Accepted && rejectedDrift.State.NextPublicationAuxiliarySequence == 1 &&
                rejectedDrift.State.Operations.Single().OriginalReturnHeader is null,
            "Context drift reached auxiliary resequencing or mutated original evidence.");
        rejected.Add("strict-original-wire-context-precedes-auxiliary-resequence");

        var reusedEvent = NewFixture();
        var replayOperation = FixtureGuid(605);
        Commit(reusedEvent, replayOperation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        SendReturn(reusedEvent, replayOperation, SupervisionCallKind.AcquireMarker,
            ObserverResourceLifecycle.Success, [905], null, 200, ownerTicks: 9);
        var auxiliaryBeforeReplay = reusedEvent.Driver.Snapshot().NextPublicationAuxiliarySequence;
        var replayedDelivery = DiagnosticEnvelope.Create(
            Header(reusedEvent, DiagnosticEnvelopeKind.DeliveryObservation, replayOperation,
                EventGuid(replayOperation, 2)),
            new DiagnosticDeliveryObservation(Controller, SupervisionCallKind.AcquireMarker, true, 201,
                PClockDomain, DiagnosticProofClaims.False));
        var replayed = Send(reusedEvent, replayedDelivery, 201, replayOperation);
        Require(!replayed.Accepted &&
                replayed.State.NextPublicationAuxiliarySequence == auxiliaryBeforeReplay &&
                replayed.State.Operations.Single().OriginalDeliveryHeader is null,
            "Auxiliary sequencing repaired a reused original wire event ID.");
        rejected.Add("reused-original-wire-event-id-rejected-before-auxiliary-bookkeeping");

        var multiple = NewFixture();
        var multipleOperation = FixtureGuid(607);
        Commit(multiple, multipleOperation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        var multipleReturn = FirstReturnEnvelope(multiple, multipleOperation,
            SupervisionCallKind.AcquireMarker, ObserverResourceLifecycle.Success, [907, 908], null, 7);
        var multipleResult = Send(multiple, multipleReturn, 200, multipleOperation);
        Require(!multipleResult.Accepted && multipleResult.Reason == "unsafe-acquisition-coordinate-set" &&
                multipleResult.State.Unknowns.Any(item => item.Coordinate == 907) &&
                multipleResult.State.Unknowns.Any(item => item.Coordinate == 908) &&
                multipleResult.State.Supervision.Observer.Marker is null,
            "A non-singleton acquisition selected a first coordinate or dropped additional uncertainty.");
        rejected.Add("nonsingleton-acquisition-preserves-all-coordinates-without-first-only-success");

        var conflicts = NewFixture();
        var conflictOperation = FixtureGuid(610);
        Commit(conflicts, conflictOperation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        SendReturn(conflicts, conflictOperation, SupervisionCallKind.AcquireMarker,
            ObserverResourceLifecycle.Success, [910], null, 200, ownerTicks: 10);
        var firstConflict = FirstReturnEnvelope(conflicts, conflictOperation,
            SupervisionCallKind.AcquireMarker, 5, [911], null, 11);
        Require(!Send(conflicts, firstConflict, 201, conflictOperation).Accepted,
            "A conflicting positive return was admitted.");
        var secondConflict = FirstReturnEnvelope(conflicts, conflictOperation,
            SupervisionCallKind.AcquireMarker, 6, [912], null, 12);
        Require(!Send(conflicts, secondConflict, 202, conflictOperation).Accepted,
            "A second conflicting positive return was admitted.");
        var conflictState = conflicts.Driver.Snapshot();
        Require(conflictState.Unknowns.Any(item => item.Coordinate == 911) &&
                conflictState.Unknowns.Any(item => item.Coordinate == 912),
            "Different positive coordinates on one operation were incorrectly deduplicated.");
        Require(!conflicts.Driver.CommitCommand(conflictOperation, FixtureGuid(611),
                SupervisionCallKind.AcquireMarker, Payload(0, 0, 2), 203).Accepted,
            "A reused operation token was admitted.");
        rejected.Add("conflicting-positive-returns-preserve-distinct-uncertainty");
        rejected.Add("reused-operation-token-rejected");

        var claims = NewFixture(acknowledge: false);
        var validAck = EncodeFixture(CustodyEnvelope(claims));
        var forgedAck = ReplaceBoolean(validAck, "nativeCallsPerformed", value: true);
        Require(claims.Driver.Enqueue(new InMemorySuppliedFrame(forgedAck)).Accepted,
            "Forged-claim frame could not enter the bounded queue.");
        var forged = claims.Driver.DrainNext(1);
        Require(!forged.Accepted && !forged.State.CustodyAcknowledged &&
                !forged.State.NativeCallsPerformed && !forged.State.ProductionAdmissionGranted,
            "A positive proof claim was admitted or promoted.");
        rejected.Add("authority-promoting-proof-claims-rejected");
        incomplete.Add("malformed-context-replay-never-proves-drain-or-custody");
    }

    private static void CheckUnexpectedPositiveCoordinates(List<string> rejected, List<string> incomplete)
    {
        var failedAcquisition = NewFixture();
        var failedOperation = FixtureGuid(620);
        Commit(failedAcquisition, failedOperation, SupervisionCallKind.AcquireMarker, 100,
            Payload(0, 0, 2));
        var failedReturn = Send(failedAcquisition, FirstReturnEnvelope(failedAcquisition,
            failedOperation, SupervisionCallKind.AcquireMarker, 5, [701, 702], null, 30),
            200, failedOperation);
        Require(!failedReturn.Accepted &&
                failedReturn.State.Operations.Single().Stage == InMemorySupervisionOperationStage.Unresolved &&
                failedReturn.State.Unknowns.Any(item => item.Coordinate == 701) &&
                failedReturn.State.Unknowns.Any(item => item.Coordinate == 702) &&
                failedReturn.State.Supervision.Observer.Marker is null,
            "A failed multi-coordinate acquisition selected a scalar descriptor or dropped a coordinate.");
        rejected.Add("failed-acquisition-multiple-positive-coordinates-remain-distinct-unknowns");

        var walk = NewFixtureWithResources();
        var walkOperation = FixtureGuid(621);
        Commit(walk, walkOperation, SupervisionCallKind.Walk, 300, Payload(0, 0, 2));
        var walkReturn = Send(walk, FirstReturnEnvelope(walk, walkOperation,
            SupervisionCallKind.Walk, ObserverResourceLifecycle.Success, [711], null, 31),
            400, walkOperation);
        Require(!walkReturn.Accepted &&
                walkReturn.State.Operations.Single(item => item.OperationId == walkOperation).Stage ==
                InMemorySupervisionOperationStage.Unresolved &&
                walkReturn.State.Unknowns.Any(item => item.OperationId == walkOperation &&
                    item.Coordinate == 711) &&
                !walkReturn.State.Supervision.Observer.WalkExhausted,
            "A Walk return with a positive coordinate projected or lost the unexpected coordinate.");
        rejected.Add("walk-positive-coordinate-is-preserved-and-never-projected");

        var release = NewFixtureWithResourcesAndExhaustion();
        var marker = release.Driver.Snapshot().Supervision.Observer.Marker;
        Require(marker is not null, "Release-coordinate fixture did not acquire its marker.");
        var releaseOperation = FixtureGuid(623);
        Commit(release, releaseOperation, SupervisionCallKind.ReleaseMarker, 500,
            SupervisionPayloadBudget.Empty, marker);
        var mismatchedRelease = Send(release, FirstReturnEnvelope(release, releaseOperation,
            SupervisionCallKind.ReleaseMarker, ObserverResourceLifecycle.Success,
            [marker!.Coordinate + 1], marker, 32), 600, releaseOperation);
        Require(!mismatchedRelease.Accepted &&
                mismatchedRelease.State.Operations.Single(item => item.OperationId == releaseOperation).Stage ==
                InMemorySupervisionOperationStage.Unresolved &&
                mismatchedRelease.State.Unknowns.Any(item => item.OperationId == releaseOperation &&
                    item.Coordinate == marker.Coordinate + 1) &&
                mismatchedRelease.State.Supervision.Observer.MarkerState != ObserverResourceState.Released,
            "A release return projected a coordinate different from its frozen descriptor.");
        rejected.Add("release-coordinate-mismatch-is-preserved-and-never-projected");
        incomplete.Add("unexpected-positive-coordinate-returns-remain-unresolved-custody");
    }

    private static void CheckOversizedAnticipatedPayloads(List<string> rejected, List<string> incomplete)
    {
        CheckOversizedAnticipation(int.MaxValue, 9, "anticipated-row-maximum");
        CheckOversizedAnticipation(2, long.MaxValue, "anticipated-copy-maximum");
        rejected.Add("anticipated-intmax-rows-rejected-before-hold-or-parse-mutation");
        rejected.Add("anticipated-longmax-copy-rejected-before-hold-or-parse-mutation");
        incomplete.Add("untrusted-anticipated-extents-cannot-wrap-remaining-capacity");

        static void CheckOversizedAnticipation(int anticipatedRows, long anticipatedCopiedBytes, string label)
        {
            var fixture = NewFixtureWithResources();
            var operation = anticipatedRows == int.MaxValue ? FixtureGuid(630) : FixtureGuid(631);
            Commit(fixture, operation, SupervisionCallKind.Walk, 300, Payload(2, 9, 4));
            SendReturn(fixture, operation, SupervisionCallKind.Walk, ObserverResourceLifecycle.Success,
                [], null, 400, ownerTicks: 35);
            SendRows(fixture, operation, 0, false,
                [new DiagnosticValueRow(0, "alpha"), new DiagnosticValueRow(1, "beta")], 401);
            var rows = DiagnosticEnvelope.Create(
                Header(fixture, DiagnosticEnvelopeKind.ValueRowBatch, operation, EventGuid(operation, 5)),
                new DiagnosticValueRowBatch(Owner, 1, true, [], DiagnosticProofClaims.False));
            var frame = EncodeFixture(rows);
            Require(fixture.Driver.Enqueue(new InMemorySuppliedFrame(frame, operation, anticipatedRows,
                    anticipatedCopiedBytes)).Accepted,
                $"{label} frame did not enter the bounded queue.");
            var before = fixture.Driver.Snapshot();
            Require(before.Accounting.Rows == 2 && before.Accounting.CopiedBytes == 9,
                $"{label} did not establish nonzero actual payload before overflow inspection.");
            var denied = fixture.Driver.DrainNext(402);
            var retained = denied.State.Operations.Single(item => item.OperationId == operation);
            Require(!denied.Accepted && denied.Reason == "driver-ordinary-capacity-exhausted" &&
                    denied.State.Accounting == before.Accounting &&
                    denied.State.ValidationTranscript.Calls == before.ValidationTranscript.Calls &&
                    denied.State.ValidationTranscript.OrdinaryEvents ==
                    before.ValidationTranscript.OrdinaryEvents &&
                    denied.State.ValidationTranscript.ActualEncodedBytes ==
                    before.ValidationTranscript.ActualEncodedBytes &&
                    denied.State.ValidationTranscript.UnknownObligations.Count ==
                    before.ValidationTranscript.UnknownObligations.Count &&
                    denied.State.NextWireSequence == before.NextWireSequence &&
                    retained.Stage == InMemorySupervisionOperationStage.Rows && retained.Rows == 2,
                $"{label} mutated a hold, parse ledger, wire sequence, or operation payload.");
        }
    }

    private static void CheckInspectionExhaustion(List<string> rejected, List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(700);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        Require(fixture.Driver.RequestStop(FixtureGuid(701), 200).Accepted,
            "Inspection fixture stop was rejected.");
        var malformed = new byte[sizeof(uint)];
        for (var index = 0; index < InMemorySupervisionDriver.MaximumInspectionFrames; index++)
        {
            Require(fixture.Driver.Enqueue(new InMemorySuppliedFrame(malformed, operation)).Accepted,
                "A funded inspection frame could not be queued.");
            Require(!fixture.Driver.DrainNext(201 + index).Accepted,
                "Malformed held inspection traffic was admitted.");
        }
        var beforeNinth = fixture.Driver.Snapshot();
        Require(beforeNinth.Accounting.InspectionFramesAvailable == 0 &&
                beforeNinth.Operations.Single().ReturnReservationHeld &&
                beforeNinth.Accounting.FreeHeldEvents == 0 && beforeNinth.Accounting.FinalActualEvents == 0,
            "Inspection rejection consumed the held result or a protected partition.");
        Require(fixture.Driver.Enqueue(new InMemorySuppliedFrame(malformed, operation)).Accepted,
            "The ninth opaque frame could not enter the bounded queue.");
        var ninth = fixture.Driver.DrainNext(210);
        Require(!ninth.Accepted && ninth.Reason == "funded-held-inspection-unavailable" &&
                ninth.State.ValidationTranscript.ActualEncodedBytes ==
                beforeNinth.ValidationTranscript.ActualEncodedBytes,
            "An unfunded ninth inspection parsed or charged a payload body.");
        rejected.Add("eight-funded-held-inspections-exhaust-without-ninth-parse");
        incomplete.Add("inspection-exhaustion-preserves-protected-cleanup-and-final-budgets");
    }

    private static void CheckQueueAndAttemptBounds(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var fixture = NewFixture();
        var malformed = new byte[sizeof(uint)];
        for (var index = 0; index < InMemorySupervisionDriver.MaximumQueuedFrames; index++)
            Require(fixture.Driver.Enqueue(new InMemorySuppliedFrame(malformed)).Accepted,
                "The bounded queue rejected an in-range frame.");
        var full = fixture.Driver.Snapshot();
        var rejectedSource = new byte[DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes];
        var overflow = fixture.Driver.Enqueue(new InMemorySuppliedFrame(rejectedSource));
        rejectedSource[0] = 0x7F;
        Require(!overflow.Accepted && overflow.Reason == "bounded-queue-overflow" &&
                overflow.State.QueuedFrames == InMemorySupervisionDriver.MaximumQueuedFrames &&
                overflow.State.QueuedBytes == full.QueuedBytes,
            "Queue capacity was checked after copy or changed retained extents.");
        rejected.Add("queue-capacity-and-aggregate-bytes-checked-before-copy");

        long ticks = 2;
        while (fixture.Driver.Snapshot().QueuedFrames > 0 &&
               fixture.Driver.Snapshot().Attempts.Count < InMemorySupervisionDriver.MaximumAttemptRecords)
        {
            _ = fixture.Driver.DrainNext(ticks++);
        }
        var attempts = fixture.Driver.Snapshot();
        _ = fixture.Driver.Enqueue(new InMemorySuppliedFrame(malformed));
        var exhausted = fixture.Driver.Snapshot();
        Require(exhausted.Attempts.Count <= InMemorySupervisionDriver.MaximumAttemptRecords &&
                (exhausted.AttemptOverflow || attempts.Attempts.Count ==
                    InMemorySupervisionDriver.MaximumAttemptRecords),
            "Attempt records exceeded their fixed bound.");
        positive.Add("queue-retains-at-most-128-complete-frames-and-8mib");
        incomplete.Add("ordinary-attempt-exhaustion-is-sticky-and-finite");
    }

    private static void CheckUnknownOverflowRetainsPending(List<string> positive, List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(800);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        Require(fixture.Driver.ObserveDeadline(operation, FixtureGuid(801), 5_100).Accepted,
            "Pending-overflow fixture did not reach its deadline.");
        SendReturn(fixture, operation, SupervisionCallKind.AcquireMarker,
            ObserverResourceLifecycle.Success, [1_001], null, 5_110, ownerTicks: 10);
        SendDelivery(fixture, operation, SupervisionCallKind.AcquireMarker, 5_120);
        Require(fixture.Driver.Snapshot().Supervision.PendingCompletion is not null,
            "Pending-overflow fixture did not retain its pending tuple.");
        for (var index = 0; index < SupervisionPolicy.MaximumUnknownReturns + 1; index++)
        {
            _ = fixture.Driver.RecordOpaqueObligation(FixtureGuid(810 + index), FixtureGuid(830 + index),
                5_130 + index, ObserverResourceKind.Marker, 2_000 + index, $"opaque-{index}");
        }
        var state = fixture.Driver.Snapshot();
        Require(state.UnknownOverflow && state.Unknowns.Count == SupervisionPolicy.MaximumUnknownReturns &&
                state.Supervision.PendingCompletion is not null &&
                state.Operations.Single(item => item.OperationId == operation).Stage ==
                InMemorySupervisionOperationStage.CompletionPending,
            "Unknown overflow evicted the separate pending tuple or serialized a ninth aggregate.");
        positive.Add("aggregate-unknowns-merge-to-eight-with-exact-distinct-coordinates");
        incomplete.Add("unknown-overflow-retains-separate-immutable-pending-tuple");
    }

    private static void CheckPendingProjectionCapacityGuard(List<string> rejected, List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(820);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        Require(fixture.Driver.ObserveDeadline(operation, FixtureGuid(821), 5_100).Accepted,
            "Pending-capacity fixture did not reach its supplied deadline.");
        SendReturn(fixture, operation, SupervisionCallKind.AcquireMarker, 5, [8_201], null,
            5_150, ownerTicks: 45);
        _ = SendDelivery(fixture, operation, SupervisionCallKind.AcquireMarker, 5_160);
        var pendingBefore = fixture.Driver.Snapshot().Supervision.PendingCompletion;
        Require(pendingBefore is { OperationId: var pendingOperation, ApiStatus: 5, Coordinate: 8_201 } &&
                pendingOperation == operation,
            "Pending-capacity fixture did not retain its failed positive acquisition.");

        for (var index = 0; index < SupervisionPolicy.MaximumUnknownReturns; index++)
        {
            var retained = fixture.Driver.RecordOpaqueObligation(FixtureGuid(830 + index),
                FixtureGuid(850 + index), 5_161 + index, ObserverResourceKind.Snapshot,
                9_000 + index, $"pending-capacity-{index}", (uint)(30 + index));
            Require(retained.Accepted, "An in-bound opaque unknown could not fill the aggregate ledger.");
        }
        var full = fixture.Driver.Snapshot();
        Require(full.Unknowns.Count == SupervisionPolicy.MaximumUnknownReturns && !full.UnknownOverflow,
            "The aggregate unknown ledger did not reach exactly eight entries before projection.");
        var denied = fixture.Driver.DeliverPending(operation, FixtureGuid(870), 5_169);
        var pendingAfter = denied.State.Supervision.PendingCompletion;
        Require(!denied.Accepted && denied.Reason == "unknown-return-capacity-exhausted-before-projection" &&
                denied.State.UnknownOverflow && denied.State.AdmissionClosed &&
                denied.State.Unknowns.Count == SupervisionPolicy.MaximumUnknownReturns &&
                pendingAfter is { OperationId: var retainedOperation, ApiStatus: 5, Coordinate: 8_201 } &&
                retainedOperation == operation &&
                denied.State.Operations.Single(item => item.OperationId == operation).Stage ==
                InMemorySupervisionOperationStage.Unresolved,
            "Pending projection at aggregate capacity evicted the tuple or projected a ninth unknown.");
        rejected.Add("pending-error-acquisition-projection-is-guarded-at-eight-unknowns");
        incomplete.Add("pending-capacity-rejection-preserves-tuple-and-closes-projection");
    }

    private static void CheckKnownKindUnknownDeduplication(List<string> positive, List<string> incomplete)
    {
        var opaque = NewFixture();
        var markerOperation = FixtureGuid(875);
        Commit(opaque, markerOperation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        var rejectedMarker = FirstReturnEnvelope(opaque, markerOperation,
            SupervisionCallKind.AcquireMarker, 5, [701], null, 201) with
        {
            Header = Header(opaque, DiagnosticEnvelopeKind.FirstRawReturn, markerOperation,
                FixtureGuid(876)) with
            { ContextDigest = new string('f', 64) },
        };
        Require(!Send(opaque, rejectedMarker, 200, markerOperation).Accepted,
            "Kind-resolution marker fixture did not reject its context-drifted return.");
        Require(opaque.Driver.RecordOpaqueObligation(markerOperation, FixtureGuid(877), 201,
                ObserverResourceKind.Marker, 701, "same-known-marker", 5).Accepted,
            "Known Marker obligation was not retained after the codec rejection.");
        var opaqueState = opaque.Driver.Snapshot();
        var markerMatches = opaqueState.Unknowns.Where(item => item.OperationId == markerOperation &&
            item.ApiStatus == 5 && item.Coordinate == 701).ToArray();
        Require(markerMatches is [{ Kind: ObserverResourceKind.Marker, ObservedMonotonicTicks: 201 }] &&
                !opaqueState.Tickets.Contains($"clock:{markerOperation:N}", StringComparer.Ordinal),
            "Codec Kind=null and exact known Marker evidence duplicated or changed first evidence.");

        var model = NewFixtureWithMarker();
        var snapshotOperation = FixtureGuid(878);
        Commit(model, snapshotOperation, SupervisionCallKind.AcquireSnapshot, 300, Payload(0, 0, 2));
        var rejectedSnapshot = FirstReturnEnvelope(model, snapshotOperation,
            SupervisionCallKind.AcquireSnapshot, 5, [702], null, 50) with
        {
            Header = Header(model, DiagnosticEnvelopeKind.FirstRawReturn, snapshotOperation,
                FixtureGuid(879)) with
            { ContextDigest = new string('f', 64) },
        };
        Require(!Send(model, rejectedSnapshot, 400, snapshotOperation).Accepted,
            "Kind-resolution snapshot fixture did not reject its context-drifted return.");
        Require(model.Driver.ObserveDeadline(snapshotOperation, FixtureGuid(880), 5_300).Accepted,
            "Kind-resolution snapshot fixture did not reach its supplied deadline.");
        SendReturn(model, snapshotOperation, SupervisionCallKind.AcquireSnapshot, 5, [702], null,
            5_350, ownerTicks: 50);
        _ = SendDelivery(model, snapshotOperation, SupervisionCallKind.AcquireSnapshot, 5_360);
        _ = model.Driver.DeliverPending(snapshotOperation, FixtureGuid(881), 5_370);
        var modelState = model.Driver.Snapshot();
        var snapshotMatches = modelState.Unknowns.Where(item => item.OperationId == snapshotOperation &&
            item.ApiStatus == 5 && item.Coordinate == 702).ToArray();
        Require(snapshotMatches is [{ Kind: ObserverResourceKind.Snapshot, ObservedMonotonicTicks: 50 }] &&
                !modelState.Tickets.Contains($"clock:{snapshotOperation:N}", StringComparer.Ordinal),
            "Derived model P-clock evidence duplicated codec evidence or raised a false clock conflict.");
        positive.Add("codec-null-kind-dedupes-with-known-acquisition-kind-and-preserves-first-time");
        positive.Add("derived-model-clock-alias-does-not-create-a-false-conflict-ticket");
        var derivedOnly = NewFixture();
        var derivedOperation = FixtureGuid(882);
        Commit(derivedOnly, derivedOperation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        SendReturn(derivedOnly, derivedOperation, SupervisionCallKind.AcquireMarker, 5, [703], null,
            200, ownerTicks: 51);
        _ = SendDelivery(derivedOnly, derivedOperation, SupervisionCallKind.AcquireMarker, 201);
        var derivedState = derivedOnly.Driver.Snapshot();
        Require(derivedState.Unknowns is [{ Kind: ObserverResourceKind.Marker, ObservedMonotonicTicks: 51 }] &&
                !derivedState.Tickets.Contains($"clock:{derivedOperation:N}", StringComparer.Ordinal),
            "A model-first unknown lost the matching immutable O raw observation clock.");
        positive.Add("model-first-unknown-uses-exact-matching-immutable-raw-clock");
        incomplete.Add("codec-rejected-acquisition-evidence-remains-diagnostic-only");
    }

    private static void CheckStartupRawEvidenceAndMailboxStates(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var missingClock = NewFixture(acknowledge: false);
        var invalidExtent = missingClock.Driver.Enqueue(new InMemorySuppliedFrame([],
            FixtureGuid(840)));
        Require(!invalidExtent.Accepted && invalidExtent.State.AdmissionClosed &&
                invalidExtent.State.FirstAbortTicks is null &&
                invalidExtent.State.RawUncertainties.Any(item => item.RawObservedTicks == 0),
            "Tick-zero queue failure invented an observed P abort or lost raw missing-clock evidence.");
        Require(missingClock.Driver.RequestStop(FixtureGuid(841), 1).Accepted,
            "A later supplied P stop could not establish the management observation.");
        var unsupported = missingClock.Driver.PublishReport(FixtureGuid(842), FixtureGuid(843), 30_001,
            "unsupported-startup-failure");
        Require(unsupported.Accepted && unsupported.State.Mailbox is
        { State: CollectorSupervisionState.Unsupported } &&
                unsupported.State.Mailbox.RawUncertainties.Any(item => item.RawObservedTicks == 0),
            "Mailbox did not survive a startup failure under Unsupported state.");
        rejected.Add("startup-invalid-extent-closes-admission-with-unobserved-abort-time");

        var prefix = NewFixture(acknowledge: false);
        Require(prefix.Driver.Enqueue(new InMemorySuppliedFrame(new byte[sizeof(uint)])).Accepted,
            "Startup prefix fixture did not enter the bounded queue.");
        var prefixFailure = prefix.Driver.DrainNext(1);
        Require(!prefixFailure.Accepted && prefixFailure.State.AdmissionClosed &&
                prefixFailure.State.FirstAbortTicks == 1,
            "Startup prefix failure did not fail closed at supplied P receipt.");
        var prefixReport = prefix.Driver.PublishReport(FixtureGuid(844), FixtureGuid(845), 30_001,
            "startup-prefix-failure");
        Require(prefixReport.Accepted && prefixReport.State.Mailbox is
        { State: CollectorSupervisionState.Unsupported },
            "Prefix-error mailbox was unavailable under Unsupported state.");
        rejected.Add("startup-prefix-error-fails-closed-before-next-operation");

        var prepared = NewFixture();
        var overlong = new string('r', DiagnosticValidation.MaximumFinalReasonBytes + 1);
        _ = prepared.Driver.RecordOpaqueObligation(FixtureGuid(846), FixtureGuid(847), 2,
            ObserverResourceKind.Marker, 0, overlong, 17);
        _ = prepared.Driver.RecordOpaqueObligation(FixtureGuid(848), FixtureGuid(849), 3,
            ObserverResourceKind.Marker, -5, "negative-coordinate", 18);
        var preparedReport = prepared.Driver.PublishReport(FixtureGuid(850), FixtureGuid(851), 30_002,
            "prepared-opaque-cutoff");
        Require(preparedReport.Accepted && preparedReport.State.Mailbox is
        { State: CollectorSupervisionState.Prepared } &&
                preparedReport.State.Mailbox.RawUncertainties.Any(item => item.Coordinate == 0) &&
                preparedReport.State.Mailbox.RawUncertainties.Any(item => item.Coordinate == -5) &&
                preparedReport.State.Mailbox.Unknowns.All(item => item.Coordinate is null or > 0) &&
                preparedReport.State.Mailbox.Unknowns.All(item =>
                    Encoding.UTF8.GetByteCount(item.MissingResultReason) <=
                    DiagnosticValidation.MaximumFinalReasonBytes),
            "Opaque zero/negative/129-byte evidence was not normalized safely under Prepared state.");
        positive.Add("raw-zero-negative-and-overlong-evidence-remains-bounded-and-separate");

        var inFlight = NewFixture();
        var operation = FixtureGuid(852);
        Commit(inFlight, operation, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        Require(inFlight.Driver.RequestStop(FixtureGuid(853), 200).Accepted,
            "Undelivered-return mailbox fixture did not accept stop.");
        SendReturn(inFlight, operation, SupervisionCallKind.AcquireMarker,
            ObserverResourceLifecycle.Success, [4_001], null, 300, ownerTicks: 40);
        var inFlightReport = inFlight.Driver.PublishReport(FixtureGuid(854), FixtureGuid(855), 30_200,
            "call-inflight-undelivered-return", destinationLength: 1);
        Require(inFlightReport.Accepted && inFlightReport.State.Mailbox is
        { State: CollectorSupervisionState.CallInFlight, WireSerializationSucceeded: false } &&
                inFlightReport.State.Mailbox.OutstandingOperations.Any(item => item.OperationId == operation) &&
                inFlightReport.State.Mailbox.UndeliveredRawReturns.Any(item =>
                    item.OperationId == operation && item.OriginalReturn.PositiveCoordinates.Contains(4_001)),
            "Mailbox-first serialization failure omitted outstanding/undelivered raw evidence.");
        positive.Add("mailbox-freezes-before-fallible-wire-with-outstanding-and-undelivered-raw-tuples");
        incomplete.Add("unsupported-prepared-and-callinflight-mailboxes-remain-unqualified");
    }

    private static void CheckPersistentUnknownSnapshots(List<string> positive, List<string> incomplete)
    {
        var fixture = NewFixture();
        var firstOperation = FixtureGuid(870);
        _ = fixture.Driver.RecordOpaqueObligation(firstOperation, FixtureGuid(871), 2,
            ObserverResourceKind.Marker, 5_001, "first", 5);
        var first = fixture.Driver.Snapshot();
        _ = fixture.Driver.RecordOpaqueObligation(firstOperation, FixtureGuid(872), 3,
            ObserverResourceKind.Marker, 5_001, "same-key-later-clock", 5);
        var repeated = fixture.Driver.Snapshot();
        Require(first.Unknowns.Count == 1 && repeated.Unknowns.Count == 1 &&
                repeated.Unknowns[0].ObservedMonotonicTicks == 2 &&
                repeated.Tickets.Any(item => item == $"clock:{firstOperation:N}"),
            "Repeated snapshots rebuilt, duplicated, or replaced first unknown evidence.");
        _ = fixture.Driver.RecordOpaqueObligation(firstOperation, FixtureGuid(873), 4,
            ObserverResourceKind.Marker, 5_001, "different-status", 6);
        for (var index = 0; index < SupervisionPolicy.MaximumUnknownReturns; index++)
        {
            _ = fixture.Driver.RecordOpaqueObligation(FixtureGuid(880 + index), FixtureGuid(900 + index),
                5 + index, ObserverResourceKind.Snapshot, 6_000 + index, $"fill-{index}",
                (uint)(20 + index));
        }
        var overflow = fixture.Driver.Snapshot();
        var afterRepeat = fixture.Driver.Snapshot();
        Require(overflow.UnknownOverflow && overflow.Unknowns.Count == SupervisionPolicy.MaximumUnknownReturns &&
                afterRepeat.Unknowns.SequenceEqual(overflow.Unknowns) &&
                afterRepeat.Unknowns[0].ObservedMonotonicTicks == 2,
            "Unknown overflow or repeated projections evicted persistent first evidence.");
        positive.Add("persistent-unknown-ledger-preserves-first-evidence-across-snapshots");
        incomplete.Add("unknown-overflow-closes-policy-projection-without-eviction");
    }

    private static Fixture NewFixture(bool acknowledge = true)
    {
        var slots = Slots();
        var binding = new CollectorSupervisionBinding(
            RunId,
            SessionId,
            InventoryAnchor.B1,
            1,
            Controller,
            Owner,
            Writer,
            Owner,
            SupervisionTargetContext.OwnerCurrentProcess,
            "o:self-current-process",
            "trusted-parent",
            slots,
            Policy);
        var expected = new DiagnosticDecodeContext(
            ContextDigest,
            1,
            RunId,
            SessionId,
            1,
            InventoryAnchor.B1,
            SourceDigest,
            BinaryDigest,
            Controller,
            Owner,
            Writer,
            Owner,
            SupervisionTargetContext.OwnerCurrentProcess,
            "o:self-current-process",
            PClockDomain,
            OClockDomain,
            "trusted-parent",
            SdkDigest,
            "windows-x64-supplied",
            CustodyDigest,
            slots,
            Policy,
            Policy.MonotonicFrequency).Freeze();
        var fixture = new Fixture(new InMemorySupervisionDriver(expected, binding), expected, binding);
        if (acknowledge)
        {
            var result = Send(fixture, CustodyEnvelope(fixture), 1);
            Require(result.Accepted && result.State.CustodyAcknowledged,
                "Canonical complete custody ACK did not prepare the driver.");
        }
        return fixture;
    }

    private static Fixture NewFixtureWithMarker()
    {
        var fixture = NewFixture();
        var marker = FixtureGuid(900);
        Commit(fixture, marker, SupervisionCallKind.AcquireMarker, 100, Payload(0, 0, 2));
        Complete(fixture, marker, SupervisionCallKind.AcquireMarker, 150, 160,
            ObserverResourceLifecycle.Success, [3_001]);
        return fixture;
    }

    private static Fixture NewFixtureWithResources()
    {
        var fixture = NewFixtureWithMarker();
        var snapshot = FixtureGuid(901);
        Commit(fixture, snapshot, SupervisionCallKind.AcquireSnapshot, 200, Payload(0, 0, 2));
        Complete(fixture, snapshot, SupervisionCallKind.AcquireSnapshot, 250, 260,
            ObserverResourceLifecycle.Success, [3_002]);
        return fixture;
    }

    private static Fixture NewFixtureWithResourcesAndExhaustion()
    {
        var fixture = NewFixtureWithResources();
        var exhausted = FixtureGuid(902);
        Commit(fixture, exhausted, SupervisionCallKind.Walk, 300, Payload(0, 0, 2));
        Complete(fixture, exhausted, SupervisionCallKind.Walk, 350, 360,
            ObserverResourceLifecycle.NoMoreItems, []);
        return fixture;
    }

    private static void Commit(
        Fixture fixture,
        Guid operationId,
        SupervisionCallKind kind,
        long ticks,
        SupervisionPayloadBudget payload,
        ObserverDescriptor? descriptor = null)
    {
        var result = fixture.Driver.CommitCommand(operationId, EventGuid(operationId, 1), kind, payload, ticks,
            descriptor);
        Require(result.Accepted && result.PublishedFrame is not null,
            $"Could not commit {kind}: {result.Reason}");
    }

    private static InMemorySupervisionDriverTransition Complete(
        Fixture fixture,
        Guid operationId,
        SupervisionCallKind kind,
        long returnTicks,
        long deliveryTicks,
        uint status,
        IReadOnlyList<long> coordinates,
        ObserverDescriptor? descriptor = null)
    {
        SendReturn(fixture, operationId, kind, status, coordinates, descriptor, returnTicks,
            ownerTicks: returnTicks / 2);
        return SendDelivery(fixture, operationId, kind, deliveryTicks);
    }

    private static InMemorySupervisionDriverTransition SendReturn(
        Fixture fixture,
        Guid operationId,
        SupervisionCallKind kind,
        uint status,
        IReadOnlyList<long> coordinates,
        ObserverDescriptor? descriptor,
        long pTicks,
        long ownerTicks)
    {
        var envelope = FirstReturnEnvelope(fixture, operationId, kind, status, coordinates, descriptor, ownerTicks);
        var result = Send(fixture, envelope, pTicks, operationId);
        Require(result.Accepted, $"Could not retain {kind} first return: {result.Reason}");
        return result;
    }

    private static InMemorySupervisionDriverTransition SendRows(
        Fixture fixture,
        Guid operationId,
        int batchIndex,
        bool final,
        IReadOnlyList<DiagnosticValueRow> rows,
        long pTicks)
    {
        var envelope = DiagnosticEnvelope.Create(
            Header(fixture, DiagnosticEnvelopeKind.ValueRowBatch, operationId,
                EventGuid(operationId, checked((byte)(3 + batchIndex)))),
            new DiagnosticValueRowBatch(Owner, batchIndex, final, rows, DiagnosticProofClaims.False));
        var copied = rows.Sum(row => (long)Encoding.UTF8.GetByteCount(row.Value));
        var result = Send(fixture, envelope, pTicks, operationId, rows.Count, copied);
        Require(result.Accepted, $"Could not retain row batch: {result.Reason}");
        return result;
    }

    private static InMemorySupervisionDriverTransition SendDelivery(
        Fixture fixture,
        Guid operationId,
        SupervisionCallKind kind,
        long pTicks)
    {
        var envelope = DiagnosticEnvelope.Create(
            Header(fixture, DiagnosticEnvelopeKind.DeliveryObservation, operationId,
                EventGuid(operationId, 4)),
            new DiagnosticDeliveryObservation(Controller, kind, true, pTicks, PClockDomain,
                DiagnosticProofClaims.False));
        return Send(fixture, envelope, pTicks, operationId);
    }

    private static DiagnosticEnvelope FirstReturnEnvelope(
        Fixture fixture,
        Guid operationId,
        SupervisionCallKind kind,
        uint status,
        IReadOnlyList<long> coordinates,
        ObserverDescriptor? descriptor,
        long ownerTicks) => DiagnosticEnvelope.Create(
        Header(fixture, DiagnosticEnvelopeKind.FirstRawReturn, operationId, EventGuid(operationId, 2)),
        new DiagnosticFirstRawReturn(Owner, kind, status, coordinates, descriptor, ownerTicks,
            OClockDomain, Policy.MonotonicFrequency, null, DiagnosticProofClaims.False));

    private static DiagnosticEnvelope CustodyEnvelope(Fixture fixture) => DiagnosticEnvelope.Create(
        Header(fixture, DiagnosticEnvelopeKind.CustodyAcknowledgment, FixtureGuid(9_900), FixtureGuid(9_901)),
        new DiagnosticCustodyAcknowledgment(Controller, CustodyDigest, fixture.Binding.RequiredCustody,
            DiagnosticProofClaims.False));

    private static DiagnosticEnvelopeHeader Header(
        Fixture fixture,
        DiagnosticEnvelopeKind kind,
        Guid operationId,
        Guid eventId) => new(
        DiagnosticEnvelopeCodec.Schema,
        DiagnosticEnvelopeCodec.Version,
        kind,
        fixture.Expected.ContextDigest,
        fixture.Expected.Epoch,
        fixture.Driver.Snapshot().NextWireSequence,
        eventId,
        operationId);

    private static InMemorySupervisionDriverTransition Send(
        Fixture fixture,
        DiagnosticEnvelope envelope,
        long pTicks,
        Guid? expectedOperation = null,
        int anticipatedRows = 0,
        long anticipatedCopiedBytes = 0)
    {
        var frame = EncodeFixture(envelope);
        var queued = fixture.Driver.Enqueue(new InMemorySuppliedFrame(frame, expectedOperation,
            anticipatedRows, anticipatedCopiedBytes));
        Require(queued.Accepted, $"Could not queue fixture frame: {queued.Reason}");
        return fixture.Driver.DrainNext(pTicks);
    }

    private static byte[] EncodeFixture(DiagnosticEnvelope envelope)
    {
        var peer = new DiagnosticTranscript();
        Require(peer.TryReserveOrdinary(envelope, out var reservation, out var reserveError) && reservation is not null,
            $"Could not reserve fake-peer frame: {reserveError}");
        var destination = new byte[DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes];
        Require(DiagnosticEnvelopeCodec.TryEncode(envelope, reservation!, destination, out var written,
                out var encodeError),
            $"Could not encode fake-peer frame: {encodeError}");
        return destination.AsSpan(0, written).ToArray();
    }

    private static byte[] ReplaceBoolean(byte[] frame, string property, bool value)
    {
        var json = Encoding.UTF8.GetString(frame.AsSpan(sizeof(uint)));
        var source = $"\"{property}\":{(!value).ToString().ToLowerInvariant()}";
        var targetValue = value ? "true " : "false";
        var target = $"\"{property}\":{targetValue}";
        Require(Encoding.UTF8.GetByteCount(source) == Encoding.UTF8.GetByteCount(target) &&
                json.Contains(source, StringComparison.Ordinal),
            "Could not locate a fixed-width proof-claim fixture token.");
        var payload = Encoding.UTF8.GetBytes(json.Replace(source, target, StringComparison.Ordinal));
        var result = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(result, (uint)payload.Length);
        payload.CopyTo(result.AsSpan(sizeof(uint)));
        return result;
    }

    private static SupervisionPayloadBudget Payload(int rows, long copiedBytes, int frames) => new(
        rows,
        copiedBytes,
        frames * (long)DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes,
        DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes);

    private static IReadOnlyList<SupervisionCapabilitySlot> Slots()
    {
        var slots = new List<SupervisionCapabilitySlot>();
        long coordinate = 100;
        void Add(
            string id,
            InventoryProcessIdentity holder,
            InventoryRole creator,
            InventoryRole? invoking,
            InventoryRole? target,
            InventoryObjectClass objectClass,
            InventoryObjectIdentity objectIdentity,
            uint access) => slots.Add(new(id, holder, creator, invoking, target, objectClass,
            InventoryCoordinateKind.Owned, coordinate++, holder, objectIdentity, access, access, false,
            InventoryMeasurementProvenance.Synthetic));

        Add("p:o-process-original", Controller, InventoryRole.Controller, null, InventoryRole.Owner,
            InventoryObjectClass.Process, ProcessObject("o-process", Owner), 0x001FFFFF);
        Add("p:o-primary-thread-original", Controller, InventoryRole.Controller, null, InventoryRole.Owner,
            InventoryObjectClass.Thread, ProcessObject("o-thread", Owner) with { ThreadId = 42 }, 0x001FFFFF);
        Add("p:outer-job", Controller, InventoryRole.Controller, null, InventoryRole.Owner,
            InventoryObjectClass.Job, new("p-outer-job", JobSlot: "p-outer-job"), 0x001F001F);
        Add("p:w-process-retained", Controller, InventoryRole.Owner, InventoryRole.Controller,
            InventoryRole.Controller, InventoryObjectClass.Process, ProcessObject("w-process", Writer),
            InventoryContract.RetainedWriterProcessAccess);
        AddCleanup("p:fixture-base");
        AddCleanup("p:run-root");
        AddCleanup("p:trusted-parent");
        return slots;

        void AddCleanup(string id) => Add(id, Controller, InventoryRole.Owner, InventoryRole.Controller,
            InventoryRole.Controller, InventoryObjectClass.Directory,
            new(id, VolumeSerialNumber: 7, FileId: $"file-{id}", IsDirectory: true, IsReparsePoint: false),
            InventoryContract.WritableDirectoryAccess);
    }

    private static InventoryObjectIdentity ProcessObject(string id, InventoryProcessIdentity process) =>
        new(id, TargetProcessId: process.ProcessId, TargetCreationFileTime: process.CreationFileTime);

    private static Guid EventGuid(Guid operationId, byte discriminator)
    {
        var bytes = operationId.ToByteArray();
        bytes[^1] ^= discriminator;
        return new Guid(bytes);
    }

    private static Guid FixtureGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        bytes[15] = 0x6D;
        return new Guid(bytes);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Fixture(
        InMemorySupervisionDriver Driver,
        DiagnosticDecodeContext Expected,
        CollectorSupervisionBinding Binding);

    private static readonly Guid RunId = Guid.Parse("17d95559-07db-450c-804b-e81e74100cbd");
    private static readonly Guid SessionId = Guid.Parse("00e7d447-c049-452f-9239-1b8cb62b7b34");
    private static readonly InventoryProcessIdentity Controller = new(InventoryRole.Controller, 6101, 30101);
    private static readonly InventoryProcessIdentity Owner = new(InventoryRole.Owner, 6102, 30102);
    private static readonly InventoryProcessIdentity Writer = new(InventoryRole.Writer, 6103, 30103);
    private static readonly SupervisionPolicy Policy = SupervisionPolicy.FromFrequency(1_000);
    private static readonly string ContextDigest = new('e', 64);
    private static readonly string SourceDigest = new('a', 64);
    private static readonly string BinaryDigest = new('b', 64);
    private static readonly string SdkDigest = new('c', 64);
    private static readonly string CustodyDigest = new('d', 64);
    private const string PClockDomain = "p-clock";
    private const string OClockDomain = "o-clock";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
