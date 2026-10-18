using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static class RuntimeBoundaryReducerChecks
{
    internal static int Run()
    {
        var positive = new List<string>();
        var rejected = new List<string>();
        var incomplete = new List<string>();
        try
        {
            CheckPreparationAndExactBinding(positive, rejected, incomplete);
            CheckStopAndLocalAdmissionOrdering(positive, rejected, incomplete);
            CheckBlockedReturnAndIndependentReport(positive, incomplete);
            CheckRawReturnPublicationAndUnknownOverflow(positive, rejected, incomplete);
            CheckLeaseRequestCompletionDrainAndEligibility(positive, rejected, incomplete);
            CheckDelayedEntryPreservesLaterStage(positive, rejected);
            CheckOrdinaryFloodPreservesTerminalCredits(positive, rejected, incomplete);
            CheckAutomaticDuePolicy(rejected, incomplete);
            CheckPhaseCeilingsAndEquality(positive, rejected, incomplete);
            CheckImmutableReportAndPostCutoffSettlement(positive, rejected, incomplete);
            CheckLateClockReservedSettlement(positive, incomplete);
            CheckNearLimitDuePolicy(rejected);
            CheckInvalidPhaseKinds(rejected);
            CheckRejectedIdsCannotPoisonReservedEvidence(positive, rejected);
            CheckNullObservationBoundaries(rejected);
            CheckMalformedCorrelationCannotAdvanceClocks(rejected);
            CheckOwnerExitNeverRepresentsWriter(positive, incomplete);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "watchdog.ow-runtime-boundary-reducer-checks.v1",
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
                totalChecks = positive.Count + rejected.Count + incomplete.Count,
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
                schema = "watchdog.ow-runtime-boundary-reducer-checks.v1",
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
                totalChecks = positive.Count + rejected.Count + incomplete.Count,
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

    private static void CheckPreparationAndExactBinding(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var expected = ExpectedContext(Slots());
        RequireThrows<ArgumentException>(() => new RuntimeBoundaryReducer(expected, 0, PClockDomain,
            Policy.MonotonicFrequency), "Zero initial P clock was admitted.");
        RequireThrows<OverflowException>(() => new RuntimeBoundaryReducer(expected, long.MaxValue - 1,
            PClockDomain, Policy.MonotonicFrequency), "Unrepresentable initial P horizon was admitted.");
        var missingGeneration = Slots().Select(slot => slot.LogicalSlotId == OwnerSlotId
            ? slot with { ObjectIdentity = slot.ObjectIdentity with { Generation = null } }
            : slot).ToArray();
        RequireThrows<ArgumentException>(() => new RuntimeBoundaryReducer(ExpectedContext(missingGeneration),
            1, PClockDomain, Policy.MonotonicFrequency), "Missing frozen capability generation was admitted.");

        var fixture = NewFixture();
        var operation = FixtureGuid(100);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100);
        var before = fixture.Reducer.Snapshot().Operations.Single();
        var wrongContext = O(fixture, RuntimeBoundaryEventKind.OAdmission, 10, operation) with
        {
            ContextDigest = new string('f', 64),
        };
        Require(!fixture.Reducer.AdmitOwner(new(wrongContext, operation, OwnerSlot(fixture))).Accepted,
            "Wrong context admitted the owner token.");
        var wrongEpoch = O(fixture, RuntimeBoundaryEventKind.OAdmission, 10, operation) with
        {
            Epoch = fixture.Expected.Epoch + 1,
        };
        Require(!fixture.Reducer.AdmitOwner(new(wrongEpoch, operation, OwnerSlot(fixture))).Accepted,
            "Wrong epoch admitted the owner token.");
        var wrongActor = O(fixture, RuntimeBoundaryEventKind.OAdmission, 10, operation) with { Actor = Writer };
        Require(!fixture.Reducer.AdmitOwner(new(wrongActor, operation, OwnerSlot(fixture))).Accepted,
            "Wrong O actor admitted the owner token.");
        var wrongClock = O(fixture, RuntimeBoundaryEventKind.OAdmission, 10, operation) with
        {
            ClockDomain = PClockDomain,
        };
        Require(!fixture.Reducer.AdmitOwner(new(wrongClock, operation, OwnerSlot(fixture))).Accepted,
            "P clock was accepted as an O observation.");
        var wrongGeneration = OwnerSlot(fixture) with { Generation = OwnerSlot(fixture).Generation + 1 };
        Require(!fixture.Reducer.AdmitOwner(new(
                O(fixture, RuntimeBoundaryEventKind.OAdmission, 10, operation), operation, wrongGeneration)).Accepted,
            "Wrong capability generation admitted the owner token.");
        var emptyEvent = O(fixture, RuntimeBoundaryEventKind.OAdmission, 10, operation) with
        {
            EventId = Guid.Empty,
        };
        Require(!fixture.Reducer.AdmitOwner(new(emptyEvent, operation, OwnerSlot(fixture))).Accepted,
            "Empty event identity admitted the owner token.");
        var afterRejected = fixture.Reducer.Snapshot().Operations.Single();
        Require(!afterRejected.OwnerAdmitted && afterRejected.RemainingTerminalHolds ==
                before.RemainingTerminalHolds,
            "Malformed admission spent a protected operation terminal hold.");

        var admittedObservation = O(fixture, RuntimeBoundaryEventKind.OAdmission, 10, operation);
        Require(fixture.Reducer.AdmitOwner(new(admittedObservation, operation, OwnerSlot(fixture))).Accepted,
            "Exact owner admission was rejected.");
        var replay = admittedObservation with { EventId = fixture.NextEventId() };
        var replayed = fixture.Reducer.AdmitOwner(new(replay, operation, OwnerSlot(fixture)));
        Require(!replayed.Accepted && replayed.State.Operations.Single().RemainingTerminalHolds == 4,
            "Owner terminal replay spent a second terminal hold.");
        Require(replayed.State.Events[0].Ordinal == 1,
            "The first retained event ordinal did not start at one.");
        positive.Add("frozen-context-and-initial-authoritative-clock-prepare-exactly");
        rejected.Add("wrong-context-epoch-actor-clock-slot-generation-and-event-reject-before-terminal-mutation");
        rejected.Add("operation-terminal-replay-does-not-steal-a-hold");
        incomplete.Add("synthetic-binding-does-not-authenticate-or-admit-production");
    }

    private static void CheckStopAndLocalAdmissionOrdering(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var pOnly = NewFixture();
        var operation = FixtureGuid(200);
        Commit(pOnly, operation, SupervisionCallKind.AcquireMarker, 100);
        Require(pOnly.Reducer.ObservePStop(P(pOnly, RuntimeBoundaryEventKind.PStop, 200)).Accepted,
            "P-only stop was rejected.");
        var stopped = pOnly.Reducer.Snapshot();
        Require(stopped.LocalOClosure == RuntimeBoundaryLocalOClosure.Missing && stopped.PAdmissionClosed &&
                stopped.LeaseAdmissionClosed,
            "P-only stop invented remote O closure or left fresh admission open.");
        var deniedLease = pOnly.Reducer.AcquireLease(LeaseRequest(pOnly, operation, "late-lease", 201));
        Require(!deniedLease.Accepted, "A new lease was admitted after P stop.");
        var cutoff = stopped.OuterCutoffTicks;
        Require(cutoff is not null && pOnly.Reducer.FreezeReport(
                P(pOnly, RuntimeBoundaryEventKind.ReportFrozen, cutoff.Value), "p-only-stop").Accepted,
            "P report did not freeze independently at cutoff.");
        var frozen = pOnly.Reducer.Snapshot().Report;
        Require(frozen is not null && frozen.LocalOClosure == RuntimeBoundaryLocalOClosure.Missing &&
                !frozen.Operations.Single().OwnerAdmitted,
            "Frozen P-only report promoted missing O admission or closure.");
        Require(pOnly.Reducer.AdmitOwner(new(
                O(pOnly, RuntimeBoundaryEventKind.OAdmission, 10, operation), operation, OwnerSlot(pOnly))).Accepted,
            "A precommitted exact O token could not be observed after cutoff while O closure was Missing.");
        Require(pOnly.Reducer.ObserveEntry(
                O(pOnly, RuntimeBoundaryEventKind.EntryObservation, 11, operation),
                RuntimeBoundaryEntryDisposition.Missing).Accepted,
            "Explicit missing entry could not settle after cutoff.");
        var afterLateAdmission = pOnly.Reducer.Snapshot();
        Require(afterLateAdmission.Operations.Single().OwnerAdmitted &&
                afterLateAdmission.Operations.Single().Stage == RuntimeBoundaryOperationStage.Unresolved &&
                afterLateAdmission.Report is not null && !afterLateAdmission.Report.Operations.Single().OwnerAdmitted,
            "Late O admission rewrote the report or claimed a clean stage.");

        var before = NewFixture();
        var beforeOperation = FixtureGuid(210);
        Commit(before, beforeOperation, SupervisionCallKind.AcquireMarker, 100);
        Require(before.Reducer.ObserveLocalOStop(
                O(before, RuntimeBoundaryEventKind.LocalOStop, 10)).Accepted,
            "Local O stop before admission was rejected.");
        var blocked = before.Reducer.AdmitOwner(new(
            O(before, RuntimeBoundaryEventKind.OAdmission, 11, beforeOperation), beforeOperation,
            OwnerSlot(before)));
        Require(!blocked.Accepted && blocked.State.LocalOClosure ==
                RuntimeBoundaryLocalOClosure.ObservedBeforeAdmission,
            "Local O stop before admission failed to prevent token consumption.");

        var after = NewFixture();
        var afterOperation = FixtureGuid(220);
        Commit(after, afterOperation, SupervisionCallKind.AcquireMarker, 100);
        Admit(after, afterOperation, 10);
        Require(after.Reducer.ObserveLocalOStop(O(after, RuntimeBoundaryEventKind.LocalOStop, 11)).Accepted,
            "Local O stop after admission was rejected.");
        Require(after.Reducer.ObserveEntry(
                O(after, RuntimeBoundaryEventKind.EntryObservation, 12, afterOperation),
                RuntimeBoundaryEntryDisposition.Observed).Accepted &&
                after.Reducer.Snapshot().LocalOClosure == RuntimeBoundaryLocalOClosure.ObservedAfterAdmission,
            "Stop after admission falsely prevented or cancelled the admitted operation.");
        positive.Add("p-stop-freezes-report-with-remote-o-missing-and-late-precommit-admission-visible-only-live");
        rejected.Add("local-o-stop-before-admission-prevents-token-consumption");
        incomplete.Add("local-o-stop-after-admission-proves-no-prevention-or-cancellation");
    }

    private static void CheckBlockedReturnAndIndependentReport(
        List<string> positive,
        List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(300);
        Commit(fixture, operation, SupervisionCallKind.Walk, 100);
        Admit(fixture, operation, 10);
        Require(fixture.Reducer.ObserveEntry(
                O(fixture, RuntimeBoundaryEventKind.EntryObservation, 11, operation),
                RuntimeBoundaryEntryDisposition.Observed).Accepted,
            "Blocked-return fixture did not observe entry.");
        Require(fixture.Reducer.ObserveDeadline(
                P(fixture, RuntimeBoundaryEventKind.DeadlineObserved, 5_100, operation)).Accepted,
            "P deadline could not advance while return was blocked.");
        var cutoff = fixture.Reducer.Snapshot().OuterCutoffTicks;
        Require(cutoff is not null && fixture.Reducer.FreezeReport(
                P(fixture, RuntimeBoundaryEventKind.ReportFrozen, cutoff.Value), "blocked-return").Accepted,
            "Blocked return prevented cutoff report.");
        var first = fixture.Reducer.Snapshot();
        var second = fixture.Reducer.Snapshot();
        Require(first.Report is not null && first.Report.Operations.Single().FirstRawReturn is null &&
                !ReferenceEquals(first.Operations, second.Operations) &&
                !ReferenceEquals(first.Report.Operations, second.Report!.Operations),
            "Blocked report invented a return or exposed mutable snapshot collections.");
        positive.Add("deadline-and-report-progress-with-blocked-return-and-no-join");
        incomplete.Add("blocked-native-return-remains-synthetic-unresolved-custody");
    }

    private static void CheckRawReturnPublicationAndUnknownOverflow(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(400);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100);
        Admit(fixture, operation, 10);
        Require(fixture.Reducer.ObserveEntry(
                O(fixture, RuntimeBoundaryEventKind.EntryObservation, 11, operation),
                RuntimeBoundaryEntryDisposition.Observed).Accepted,
            "Raw-return fixture did not observe entry.");
        var coordinates = Enumerable.Range(0, RuntimeBoundaryReducer.MaximumUnknowns)
            .Select(index => 701L + index).ToArray();
        var raw = Raw(SupervisionCallKind.AcquireMarker, 5, coordinates, 12);
        Require(fixture.Reducer.RetainFirstRawReturn(
                O(fixture, RuntimeBoundaryEventKind.FirstRawReturn, 12, operation), raw).Accepted,
            "Positive error/multiple-coordinate first return was not retained.");
        var retained = fixture.Reducer.Snapshot();
        Require(retained.Unknowns.Count == RuntimeBoundaryReducer.MaximumUnknowns &&
                retained.Operations.Single().FirstRawReturn!.PositiveCoordinates.SequenceEqual(coordinates),
            "Direct status or a positive coordinate was dropped before publication.");

        var conflictCoordinates = Enumerable.Range(0, RuntimeBoundaryReducer.MaximumUnknowns)
            .Select(index => 801L + index).ToArray();
        var conflict = Raw(SupervisionCallKind.AcquireMarker, 6, conflictCoordinates, 13);
        var deniedConflict = fixture.Reducer.RetainFirstRawReturn(
            O(fixture, RuntimeBoundaryEventKind.FirstRawReturn, 13, operation), conflict);
        var conflictState = deniedConflict.State;
        Require(!deniedConflict.Accepted && conflictState.UnknownOverflow &&
                conflictState.Unknowns.Count == RuntimeBoundaryReducer.MaximumUnknowns &&
                conflictState.Operations.Single().FirstRawReturn!.ApiStatus == 5 &&
                conflictState.Operations.Single().FirstAdverseRawReturn!.ApiStatus == 6 &&
                conflictState.Operations.Single().FirstAdverseRawReturn!.PositiveCoordinates
                    .SequenceEqual(conflictCoordinates),
            "Unknown overflow evicted first evidence or dropped adverse raw coordinates/status.");
        Require(fixture.Reducer.RecordPublication(
                P(fixture, RuntimeBoundaryEventKind.Publication, 200, operation),
                RuntimeBoundaryPublicationDisposition.Lost).Accepted,
            "Lost publication could not consume its exact reserved terminal credit.");
        var lost = fixture.Reducer.Snapshot().Operations.Single();
        Require(lost.FirstRawReturn is not null && lost.Publication == RuntimeBoundaryPublicationDisposition.Lost &&
                lost.Delivery == RuntimeBoundaryDeliveryDisposition.Unobserved,
            "Lost publication erased returned-but-unpublished custody or invented delivery.");
        var undefined = (RuntimeBoundaryPublicationDisposition)999;
        Require(!NewPublicationFixture(undefined), "Undefined publication disposition was admitted.");
        positive.Add("first-raw-error-and-all-positive-coordinates-freeze-before-publication");
        rejected.Add("first-return-conflict-overflow-retains-first-and-bounded-adverse-raw-tuples");
        rejected.Add("undefined-publication-disposition-rejected-before-terminal-credit");
        incomplete.Add("lost-publication-retains-unpublished-custody-without-wire-proof");

        static bool NewPublicationFixture(RuntimeBoundaryPublicationDisposition disposition)
        {
            var local = NewFixture();
            var localOperation = FixtureGuid(409);
            Commit(local, localOperation, SupervisionCallKind.AcquireMarker, 100);
            Admit(local, localOperation, 10);
            _ = local.Reducer.RetainFirstRawReturn(
                O(local, RuntimeBoundaryEventKind.FirstRawReturn, 11, localOperation),
                Raw(SupervisionCallKind.AcquireMarker, ObserverResourceLifecycle.Success, [901], 11));
            return local.Reducer.RecordPublication(
                P(local, RuntimeBoundaryEventKind.Publication, 200, localOperation), disposition).Accepted;
        }
    }

    private static void CheckLeaseRequestCompletionDrainAndEligibility(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(500);
        Commit(fixture, operation, SupervisionCallKind.AcquireSnapshot, 100);
        var crossHolder = LeaseRequest(fixture, operation, "cross-holder", 109) with
        {
            BorrowerLane = RuntimeBoundaryLane.OwnerO,
            Borrower = Owner,
        };
        Require(!fixture.Reducer.AcquireLease(crossHolder).Accepted &&
                fixture.Reducer.Snapshot().Leases.Count == 0,
            "A separately valid O identity borrowed a P-held slot without an authorized transfer.");
        Require(fixture.Reducer.AcquireLease(LeaseRequest(fixture, operation, "observe-a", 110)).Accepted &&
                fixture.Reducer.AcquireLease(LeaseRequest(fixture, operation, "observe-b", 111)).Accepted,
            "Two bounded observation leases were not admitted before stop.");
        Admit(fixture, operation, 10);
        _ = fixture.Reducer.ObserveEntry(O(fixture, RuntimeBoundaryEventKind.EntryObservation, 11, operation),
            RuntimeBoundaryEntryDisposition.Observed);
        _ = fixture.Reducer.RetainFirstRawReturn(
            O(fixture, RuntimeBoundaryEventKind.FirstRawReturn, 12, operation),
            Raw(SupervisionCallKind.AcquireSnapshot, ObserverResourceLifecycle.Success, [1_001], 12));
        _ = fixture.Reducer.RecordPublication(P(fixture, RuntimeBoundaryEventKind.Publication, 120, operation),
            RuntimeBoundaryPublicationDisposition.Completed);
        _ = fixture.Reducer.ObserveDelivery(P(fixture, RuntimeBoundaryEventKind.Delivery, 130, operation));
        Require(!fixture.Reducer.Snapshot().Leases.Any(item => item.Drained) &&
                !fixture.Reducer.SyntheticUseEligible(OwnerSlot(fixture)),
            "Publication or delivery settled a borrowed-use lease.");
        Require(fixture.Reducer.ObservePStop(P(fixture, RuntimeBoundaryEventKind.PStop, 200)).Accepted,
            "Lease fixture stop was rejected.");
        Require(!fixture.Reducer.AcquireLease(LeaseRequest(fixture, operation, "observe-c", 201)).Accepted,
            "A new lease was admitted after stop.");
        Require(fixture.Reducer.RequestLeaseCancellation(
                P(fixture, RuntimeBoundaryEventKind.CancellationRequest, 202, operation, "observe-a")).Accepted &&
                fixture.Reducer.ObserveLeaseTimeout(
                    P(fixture, RuntimeBoundaryEventKind.Timeout, 203, operation, "observe-a")).Accepted,
            "Cancellation/timeout observations were rejected.");
        Require(!fixture.Reducer.SyntheticUseEligible(OwnerSlot(fixture)),
            "Cancellation or timeout authorized synthetic disposal eligibility.");
        Require(fixture.Reducer.ObserveUseCompletion(
                P(fixture, RuntimeBoundaryEventKind.UseCompletion, 204, operation, "observe-a")).Accepted &&
                fixture.Reducer.ObserveLeaseDrain(
                    P(fixture, RuntimeBoundaryEventKind.Drain, 205, operation, "observe-a")).Accepted,
            "First lease did not complete and drain distinctly.");
        Require(!fixture.Reducer.SyntheticUseEligible(OwnerSlot(fixture)),
            "One drained borrower hid a second outstanding borrower.");
        var earlyDrain = fixture.Reducer.ObserveLeaseDrain(
            P(fixture, RuntimeBoundaryEventKind.Drain, 206, operation, "observe-b"));
        Require(!earlyDrain.Accepted, "Drain before exact use completion was admitted.");
        Require(fixture.Reducer.ObserveUseCompletion(
                P(fixture, RuntimeBoundaryEventKind.UseCompletion, 207, operation, "observe-b")).Accepted &&
                fixture.Reducer.ObserveLeaseDrain(
                    P(fixture, RuntimeBoundaryEventKind.Drain, 208, operation, "observe-b")).Accepted &&
                fixture.Reducer.SyntheticUseEligible(OwnerSlot(fixture)),
            "All-borrower drain did not establish synthetic use eligibility.");
        Require(!fixture.Reducer.Snapshot().DescriptorReleaseSupported,
            "The pure reducer claimed descriptor release support.");
        positive.Add("publication-delivery-stop-cancellation-timeout-settle-no-lease");
        positive.Add("synthetic-use-eligibility-requires-every-exact-borrower-completed-and-drained");
        rejected.Add("lease-borrower-must-equal-exact-trusted-slot-holder");
        rejected.Add("drain-before-exact-use-completion-rejected");
        incomplete.Add("descriptor-release-remains-unsupported-without-lifecycle-composition");
    }

    private static void CheckDelayedEntryPreservesLaterStage(
        List<string> positive,
        List<string> rejected)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(550);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100);
        Admit(fixture, operation, 10);
        Require(fixture.Reducer.RetainFirstRawReturn(
                O(fixture, RuntimeBoundaryEventKind.FirstRawReturn, 11, operation),
                Raw(SupervisionCallKind.AcquireMarker, ObserverResourceLifecycle.Success, [1_050], 11)).Accepted,
            "Raw return before an entry observation was rejected.");
        Require(fixture.Reducer.RecordPublication(
                P(fixture, RuntimeBoundaryEventKind.Publication, 110, operation),
                RuntimeBoundaryPublicationDisposition.Completed).Accepted &&
                fixture.Reducer.ObserveDelivery(
                    P(fixture, RuntimeBoundaryEventKind.Delivery, 120, operation)).Accepted,
            "Delayed-entry fixture did not reach delivery.");
        var delayed = fixture.Reducer.ObserveEntry(
            O(fixture, RuntimeBoundaryEventKind.EntryObservation, 12, operation),
            RuntimeBoundaryEntryDisposition.Observed);
        Require(delayed.Accepted && delayed.State.Operations.Single().Entry ==
                RuntimeBoundaryEntryDisposition.Observed && delayed.State.Operations.Single().Stage ==
                RuntimeBoundaryOperationStage.Delivered &&
                delayed.State.Operations.Single().RemainingTerminalHolds == 0,
            "Delayed independent entry evidence regressed the delivered operation stage.");
        positive.Add("delayed-entry-populates-independent-field-without-regressing-delivered-stage");
        rejected.Add("delayed-entry-consumes-only-its-preallocated-terminal-hold");
    }

    private static void CheckOrdinaryFloodPreservesTerminalCredits(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(600);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100);
        Require(fixture.Reducer.AcquireLease(LeaseRequest(fixture, operation, "flood-lease", 110)).Accepted,
            "Flood fixture lease was not reserved before saturation.");
        for (var index = 0; index < RuntimeBoundaryReducer.MaximumOrdinaryRecords - 2; index++)
        {
            var candidateOperation = FixtureGuid(610 + index);
            var observation = P(fixture, RuntimeBoundaryEventKind.OperationCommit, 111, candidateOperation) with
            {
                ContextDigest = new string('f', 64),
            };
            _ = fixture.Reducer.CommitOperation(new(observation, candidateOperation,
                SupervisionCallKind.AcquireMarker, OwnerSlot(fixture)));
        }
        var saturated = fixture.Reducer.Snapshot();
        Require(saturated.OrdinaryRecords == RuntimeBoundaryReducer.MaximumOrdinaryRecords &&
                saturated.RemainingOperationTerminalHolds == RuntimeBoundaryReducer.OperationTerminalHolds &&
                saturated.RemainingLeaseTerminalHolds == RuntimeBoundaryReducer.LeaseTerminalHolds,
            "Malformed flood stole operation or lease terminal reservations.");
        var overflowOperation = FixtureGuid(699);
        var overflowObservation = P(fixture, RuntimeBoundaryEventKind.OperationCommit, 112, overflowOperation) with
        {
            ContextDigest = new string('f', 64),
        };
        _ = fixture.Reducer.CommitOperation(new(overflowObservation, overflowOperation,
            SupervisionCallKind.AcquireMarker, OwnerSlot(fixture)));
        Require(fixture.Reducer.Snapshot().PAdmissionClosed,
            "Ordinary rejection saturation did not close fresh admission.");

        Admit(fixture, operation, 10);
        _ = fixture.Reducer.ObserveEntry(O(fixture, RuntimeBoundaryEventKind.EntryObservation, 11, operation),
            RuntimeBoundaryEntryDisposition.Observed);
        _ = fixture.Reducer.RetainFirstRawReturn(
            O(fixture, RuntimeBoundaryEventKind.FirstRawReturn, 12, operation),
            Raw(SupervisionCallKind.AcquireMarker, ObserverResourceLifecycle.Success, [1_101], 12));
        _ = fixture.Reducer.RecordPublication(P(fixture, RuntimeBoundaryEventKind.Publication, 120, operation),
            RuntimeBoundaryPublicationDisposition.Completed);
        _ = fixture.Reducer.ObserveDelivery(P(fixture, RuntimeBoundaryEventKind.Delivery, 130, operation));
        _ = fixture.Reducer.ObserveUseCompletion(
            P(fixture, RuntimeBoundaryEventKind.UseCompletion, 131, operation, "flood-lease"));
        _ = fixture.Reducer.ObserveLeaseDrain(
            P(fixture, RuntimeBoundaryEventKind.Drain, 132, operation, "flood-lease"));
        var settled = fixture.Reducer.Snapshot();
        Require(settled.Operations.Single().Delivery == RuntimeBoundaryDeliveryDisposition.Delivered &&
                settled.Leases.Single().Drained && settled.RetainedEvents + settled.RemainingFixedHolds +
                settled.RemainingOperationTerminalHolds + settled.RemainingLeaseTerminalHolds <=
                RuntimeBoundaryReducer.MaximumEvents,
            "Reserved exact settlement failed after ordinary rejection saturation.");
        positive.Add("ordinary-flood-retains-preallocated-operation-and-lease-settlement");
        rejected.Add("sixty-four-ordinary-record-bound-closes-fresh-admission");
        incomplete.Add("metadata-ceilings-do-not-claim-physical-runtime-or-wire-bounds");
    }

    private static void CheckAutomaticDuePolicy(List<string> rejected, List<string> incomplete)
    {
        var skippedDeadline = NewFixture();
        var operation = FixtureGuid(700);
        Commit(skippedDeadline, operation, SupervisionCallKind.AcquireMarker, 100);
        var normalCutoff = skippedDeadline.Reducer.Snapshot().OuterCutoffTicks;
        if (normalCutoff is null) throw new InvalidOperationException("Automatic-policy fixture had no normal cutoff.");
        var leaseAtCutoff = skippedDeadline.Reducer.AcquireLease(
            LeaseRequest(skippedDeadline, operation, "cutoff-lease", normalCutoff.Value));
        Require(!leaseAtCutoff.Accepted && leaseAtCutoff.State.FirstAbortTicks == normalCutoff &&
                leaseAtCutoff.State.PAdmissionClosed && leaseAtCutoff.State.Leases.Count == 0,
            "Skipped explicit deadline allowed a fresh lease at F or backdated A.");

        var lateDelivery = NewFixture();
        var lateOperation = FixtureGuid(710);
        Commit(lateDelivery, lateOperation, SupervisionCallKind.AcquireMarker, 100);
        Admit(lateDelivery, lateOperation, 10);
        _ = lateDelivery.Reducer.ObserveEntry(
            O(lateDelivery, RuntimeBoundaryEventKind.EntryObservation, 11, lateOperation),
            RuntimeBoundaryEntryDisposition.Observed);
        _ = lateDelivery.Reducer.RetainFirstRawReturn(
            O(lateDelivery, RuntimeBoundaryEventKind.FirstRawReturn, 12, lateOperation),
            Raw(SupervisionCallKind.AcquireMarker, ObserverResourceLifecycle.Success, [1_201], 12));
        _ = lateDelivery.Reducer.RecordPublication(
            P(lateDelivery, RuntimeBoundaryEventKind.Publication, 200, lateOperation),
            RuntimeBoundaryPublicationDisposition.Completed);
        var deadline = lateDelivery.Reducer.Snapshot().Operations.Single().DeadlineTicks;
        var delivered = lateDelivery.Reducer.ObserveDelivery(
            P(lateDelivery, RuntimeBoundaryEventKind.Delivery, deadline, lateOperation));
        Require(delivered.Accepted && delivered.State.FirstAbortTicks == deadline &&
                delivered.State.Operations.Single().Delivery ==
                RuntimeBoundaryDeliveryDisposition.DeliveredAfterDeadline &&
                delivered.State.Operations.Single().Stage == RuntimeBoundaryOperationStage.Unresolved,
            "Late delivery did not first latch due policy from authoritative P time.");
        var fresh = FixtureGuid(711);
        var deniedCommit = lateDelivery.Reducer.CommitOperation(new(
            P(lateDelivery, RuntimeBoundaryEventKind.OperationCommit, deadline + 1, fresh), fresh,
            SupervisionCallKind.AcquireSnapshot, OwnerSlot(lateDelivery)));
        Require(!deniedCommit.Accepted && deniedCommit.State.Operations.Count == 1,
            "Late delivery left fresh P command admission open.");
        rejected.Add("lease-at-f-with-skipped-deadline-latches-current-p-abort-and-admits-no-work");
        rejected.Add("late-delivery-closes-fresh-command-before-next-commit");
        incomplete.Add("due-policy-is-supplied-p-observation-not-a-wall-clock-guarantee");
    }

    private static void CheckPhaseCeilingsAndEquality(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(800);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100);
        _ = fixture.Reducer.ObservePStop(P(fixture, RuntimeBoundaryEventKind.PStop, 200));
        Require(fixture.Reducer.BeginPhase(
                P(fixture, RuntimeBoundaryEventKind.OwnerPhaseStarted, 300),
                RuntimeBoundaryPhaseKind.Owner).Accepted &&
                fixture.Reducer.EndPhase(P(fixture, RuntimeBoundaryEventKind.OwnerPhaseEnded, 400),
                    RuntimeBoundaryPhaseKind.Owner, evidenceObserved: true).Accepted,
            "Early owner phase did not complete from supplied evidence.");
        Require(fixture.Reducer.BeginPhase(
                P(fixture, RuntimeBoundaryEventKind.WriterPhaseStarted, 500),
                RuntimeBoundaryPhaseKind.Writer).Accepted,
            "Writer phase did not begin after early owner completion.");
        var writer = fixture.Reducer.Snapshot().WriterPhase;
        Require(writer.ActualEndTicks == 10_500 && writer.AnchoredCeilingTicks == 25_200,
            "Writer phase was not capped to ten seconds and the anchored ceiling.");
        Require(fixture.Reducer.EndPhase(
                P(fixture, RuntimeBoundaryEventKind.WriterPhaseEnded, writer.ActualEndTicks!.Value),
                RuntimeBoundaryPhaseKind.Writer, evidenceObserved: true).Accepted &&
                fixture.Reducer.Snapshot().WriterPhase.Disposition == RuntimeBoundaryPhaseDisposition.Missing &&
                fixture.Reducer.Snapshot().WriterDisposition == RuntimeBoundaryWriterDisposition.Unrepresented,
            "Phase-end equality was treated as timely or represented W cleanup.");

        var delayed = NewFixture();
        var delayedOperation = FixtureGuid(810);
        Commit(delayed, delayedOperation, SupervisionCallKind.AcquireMarker, 100);
        _ = delayed.Reducer.ObservePStop(P(delayed, RuntimeBoundaryEventKind.PStop, 200));
        var ownerCeiling = delayed.Reducer.Snapshot().OwnerPhaseCeilingTicks;
        Require(ownerCeiling is not null && !delayed.Reducer.BeginPhase(
                P(delayed, RuntimeBoundaryEventKind.OwnerPhaseStarted, ownerCeiling.Value),
                RuntimeBoundaryPhaseKind.Owner).Accepted &&
                delayed.Reducer.Snapshot().OwnerPhase.Disposition ==
                RuntimeBoundaryPhaseDisposition.SkippedExpired,
            "Expired owner phase received a fresh ten-second window.");

        var lateAbort = NewFixture();
        var lateOperation = FixtureGuid(820);
        Commit(lateAbort, lateOperation, SupervisionCallKind.AcquireMarker, 100);
        _ = lateAbort.Reducer.ObservePStop(P(lateAbort, RuntimeBoundaryEventKind.PStop, 41_000));
        var lateState = lateAbort.Reducer.Snapshot();
        Require(lateState.ReportReservationTicks == 40_100 && lateState.FirstAbortTicks == 41_000 &&
                !lateAbort.Reducer.BeginPhase(
                    P(lateAbort, RuntimeBoundaryEventKind.OwnerPhaseStarted, 41_001),
                    RuntimeBoundaryPhaseKind.Owner).Accepted,
            "Late abort reopened an already elapsed R phase window.");
        positive.Add("early-owner-completion-allows-writer-phase-capped-to-ten-seconds");
        rejected.Add("phase-end-equality-delayed-phase-and-late-abort-do-not-renew-window");
        incomplete.Add("writer-phase-evidence-remains-unrepresented-and-unsupported");
    }

    private static void CheckImmutableReportAndPostCutoffSettlement(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(900);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100);
        Require(fixture.Reducer.AcquireLease(LeaseRequest(fixture, operation, "report-lease", 110)).Accepted,
            "Report fixture did not reserve its lease.");
        _ = fixture.Reducer.ObservePStop(P(fixture, RuntimeBoundaryEventKind.PStop, 200));
        var cutoff = fixture.Reducer.Snapshot().OuterCutoffTicks;
        if (cutoff is null) throw new InvalidOperationException("Report with outstanding lease did not freeze.");
        Require(fixture.Reducer.FreezeReport(
                P(fixture, RuntimeBoundaryEventKind.ReportFrozen, cutoff.Value), "outstanding-lease").Accepted,
            "Report with outstanding lease did not freeze.");
        var frozen = fixture.Reducer.Snapshot().Report;
        Require(frozen is not null && frozen.Leases.Single().Drained == false,
            "Report omitted its outstanding lease.");
        Require(fixture.Reducer.ObserveOutputFailure(
                P(fixture, RuntimeBoundaryEventKind.OutputFailure, cutoff.Value + 1)).Accepted,
            "Post-freeze output failure was not retained.");
        Require(fixture.Reducer.ObserveUseCompletion(
                P(fixture, RuntimeBoundaryEventKind.UseCompletion, cutoff.Value + 2, operation,
                    "report-lease")).Accepted &&
                fixture.Reducer.ObserveLeaseDrain(
                    P(fixture, RuntimeBoundaryEventKind.Drain, cutoff.Value + 3, operation,
                        "report-lease")).Accepted,
            "Exact post-cutoff lease settlement was rejected.");
        var settled = fixture.Reducer.Snapshot();
        Require(settled.OutputFailureObserved && settled.Leases.Single().Drained &&
                settled.Report is not null && !settled.Report.Leases.Single().Drained &&
                settled.Report.Reason == frozen!.Reason,
            "Output failure or post-cutoff settlement rewrote the frozen report.");
        Require(!fixture.Reducer.BeginPhase(
                P(fixture, RuntimeBoundaryEventKind.OwnerPhaseStarted, cutoff.Value + 4),
                RuntimeBoundaryPhaseKind.Owner).Accepted,
            "Post-cutoff settlement reopened a phase.");
        var beforeStopReport = fixture.Reducer.Snapshot().Report;
        Require(fixture.Reducer.ObserveLocalOStop(
                O(fixture, RuntimeBoundaryEventKind.LocalOStop, 10)).Accepted,
            "Reserved postreport local O-stop was rejected.");
        var lateAdmission = fixture.Reducer.AdmitOwner(new(
            O(fixture, RuntimeBoundaryEventKind.OAdmission, 11, operation), operation, OwnerSlot(fixture)));
        var afterStopReport = lateAdmission.State.Report;
        Require(!lateAdmission.Accepted && lateAdmission.State.LocalOClosure ==
                RuntimeBoundaryLocalOClosure.ObservedBeforeAdmission &&
                afterStopReport is not null && beforeStopReport is not null &&
                afterStopReport.FrozenTicks == beforeStopReport.FrozenTicks &&
                afterStopReport.Reason == beforeStopReport.Reason &&
                afterStopReport.LocalOClosure == RuntimeBoundaryLocalOClosure.Missing &&
                !afterStopReport.Operations.Single().OwnerAdmitted,
            "Postreport local O-stop failed to close live admission or rewrote the frozen report.");
        positive.Add("mailbox-freezes-before-output-failure-and-postcutoff-exact-settlement");
        positive.Add("reserved-postreport-local-o-stop-updates-live-gate-without-report-mutation");
        rejected.Add("postcutoff-settlement-opens-no-token-ticket-or-phase");
        rejected.Add("postreport-local-o-stop-rejects-later-precommitted-o-admission");
        incomplete.Add("output-and-durable-persistence-remain-independent-missing-runtime-evidence");
    }

    private static void CheckLateClockReservedSettlement(
        List<string> positive,
        List<string> incomplete)
    {
        var reportFixture = NewFixture();
        var reportOperation = FixtureGuid(925);
        Commit(reportFixture, reportOperation, SupervisionCallKind.AcquireMarker, 100);
        var report = reportFixture.Reducer.FreezeReport(
            P(reportFixture, RuntimeBoundaryEventKind.ReportFrozen, long.MaxValue), "late-report");
        Require(report.Accepted && report.State.Report is not null &&
                report.State.FirstAbortTicks == long.MaxValue,
            "A representable existing cutoff rejected its reserved report at a late authoritative P tick.");

        var rawFixture = NewFixture();
        var rawOperation = FixtureGuid(926);
        Commit(rawFixture, rawOperation, SupervisionCallKind.AcquireMarker, 100);
        Admit(rawFixture, rawOperation, 10);
        var raw = rawFixture.Reducer.RetainFirstRawReturn(
            O(rawFixture, RuntimeBoundaryEventKind.FirstRawReturn, long.MaxValue, rawOperation),
            Raw(SupervisionCallKind.AcquireMarker, ObserverResourceLifecycle.Success, [1_925], long.MaxValue));
        Require(raw.Accepted && raw.State.LastOTicks == long.MaxValue &&
                raw.State.Operations.Single().FirstRawReturn?.OwnerObservedTicks == long.MaxValue,
            "Independent late O raw settlement was rejected by an unused future-horizon calculation.");
        positive.Add("late-representable-p-report-and-o-raw-use-no-unused-future-horizon");
        incomplete.Add("independent-p-and-o-ticks-establish-no-cross-domain-time-equivalence");
    }

    private static void CheckMalformedCorrelationCannotAdvanceClocks(List<string> rejected)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(974);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100);
        Admit(fixture, operation, 10);
        Require(fixture.Reducer.RetainFirstRawReturn(
                O(fixture, RuntimeBoundaryEventKind.FirstRawReturn, 11, operation),
                Raw(SupervisionCallKind.AcquireMarker, 0, [1_974], 11)).Accepted,
            "Clock-correlation fixture did not retain its first raw result.");
        var wrongSlot = OwnerSlot(fixture) with { Generation = OwnerSlot(fixture).Generation + 1 };
        var otherOperation = FixtureGuid(975);
        Require(!fixture.Reducer.CommitOperation(new(
                    P(fixture, RuntimeBoundaryEventKind.OperationCommit, long.MaxValue, otherOperation),
                    otherOperation, SupervisionCallKind.AcquireMarker, wrongSlot)).Accepted &&
                !fixture.Reducer.RecordPublication(
                    P(fixture, RuntimeBoundaryEventKind.Publication, long.MaxValue, operation),
                    (RuntimeBoundaryPublicationDisposition)999).Accepted,
            "Malformed future P metadata gained stage authority.");
        var unmutated = fixture.Reducer.Snapshot();
        Require(unmutated.LastPTicks == 100 && unmutated.FirstAbortTicks is null &&
                fixture.Reducer.RecordPublication(P(fixture, RuntimeBoundaryEventKind.Publication, 200, operation),
                    RuntimeBoundaryPublicationDisposition.Completed).Accepted &&
                fixture.Reducer.ObserveDelivery(P(fixture, RuntimeBoundaryEventKind.Delivery, 300, operation)).Accepted,
            "Wrong generation or publication disposition poisoned P time or due policy.");

        var owner = NewFixture();
        var ownerOperation = FixtureGuid(976);
        Commit(owner, ownerOperation, SupervisionCallKind.AcquireMarker, 100);
        Admit(owner, ownerOperation, 10);
        Require(!owner.Reducer.ObserveEntry(
                    O(owner, RuntimeBoundaryEventKind.EntryObservation, long.MaxValue, ownerOperation),
                    (RuntimeBoundaryEntryDisposition)999).Accepted &&
                !owner.Reducer.RetainFirstRawReturn(
                    O(owner, RuntimeBoundaryEventKind.FirstRawReturn, long.MaxValue, FixtureGuid(977)),
                    Raw(SupervisionCallKind.AcquireMarker, 0, [1_977], long.MaxValue)).Accepted &&
                owner.Reducer.Snapshot().LastOTicks == 10 &&
                owner.Reducer.RetainFirstRawReturn(
                    O(owner, RuntimeBoundaryEventKind.FirstRawReturn, 11, ownerOperation),
                    Raw(SupervisionCallKind.AcquireMarker, 0, [1_976], 11)).Accepted,
            "Invalid O disposition or operation poisoned later exact raw return ordering.");
        rejected.Add("wrong-generation-and-invalid-publication-do-not-advance-p-time-or-policy");
        rejected.Add("invalid-owner-disposition-and-operation-do-not-poison-exact-raw-time");
    }

    private static void CheckNearLimitDuePolicy(List<string> rejected)
    {
        var policy = SupervisionPolicy.FromFrequency(1);
        var expected = (ExpectedContext(Slots()) with
        {
            ExpectedPolicy = policy,
            OwnerClockFrequency = 1,
        }).Freeze();
        var start = long.MaxValue - 45;
        var fixture = new Fixture(new RuntimeBoundaryReducer(expected, start, PClockDomain, 1), expected);
        var operation = FixtureGuid(970);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, start);
        var overdue = start + 30;
        var result = fixture.Reducer.AcquireLease(LeaseRequest(fixture, operation, "near-limit", overdue));
        Require(!result.Accepted && result.State.FirstAbortTicks == overdue &&
                result.State.OuterCutoffTicks == start + 45 && result.State.PAdmissionClosed &&
                result.State.LeaseAdmissionClosed && result.State.Leases.Count == 0 &&
                result.State.Operations.Single().RemainingTerminalHolds == 5,
            "Overflowing unclamped abort arithmetic bypassed overdue closure before existing F.");
        Require(fixture.Reducer.BeginPhase(P(fixture, RuntimeBoundaryEventKind.OwnerPhaseStarted, overdue + 6),
                    RuntimeBoundaryPhaseKind.Owner).Accepted &&
                fixture.Reducer.Snapshot().OwnerPhase.ActualEndTicks == start + 40,
            "Unused overflowing phase addition rejected a representable clamped phase.");
        Require(fixture.Reducer.FreezeReport(
                P(fixture, RuntimeBoundaryEventKind.ReportFrozen, start + 45), "near-limit").Accepted,
            "Clamped near-limit abort prevented the reserved report.");
        rejected.Add("near-limit-overdue-lease-before-f-closes-with-representable-clamped-horizons");
    }

    private static void CheckInvalidPhaseKinds(List<string> rejected)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(971);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100);
        Require(fixture.Reducer.ObservePStop(P(fixture, RuntimeBoundaryEventKind.PStop, 200)).Accepted &&
                fixture.Reducer.BeginPhase(P(fixture, RuntimeBoundaryEventKind.OwnerPhaseStarted, 5_200),
                    RuntimeBoundaryPhaseKind.Owner).Accepted &&
                fixture.Reducer.EndPhase(P(fixture, RuntimeBoundaryEventKind.OwnerPhaseEnded, 6_000),
                    RuntimeBoundaryPhaseKind.Owner, true).Accepted &&
                fixture.Reducer.BeginPhase(P(fixture, RuntimeBoundaryEventKind.WriterPhaseStarted, 6_100),
                    RuntimeBoundaryPhaseKind.Writer).Accepted,
            "Invalid-phase fixture did not reach an active writer phase.");
        var before = fixture.Reducer.Snapshot();
        var invalid = (RuntimeBoundaryPhaseKind)999;
        Require(!fixture.Reducer.BeginPhase(P(fixture, RuntimeBoundaryEventKind.WriterPhaseStarted, 6_200),
                    invalid).Accepted &&
                !fixture.Reducer.EndPhase(P(fixture, RuntimeBoundaryEventKind.WriterPhaseEnded, 6_200),
                    invalid, true).Accepted,
            "Undefined phase kind was accepted.");
        var after = fixture.Reducer.Snapshot();
        Require(after.WriterPhase == before.WriterPhase && after.RemainingFixedHolds == before.RemainingFixedHolds &&
                after.LastPTicks == before.LastPTicks &&
                fixture.Reducer.EndPhase(P(fixture, RuntimeBoundaryEventKind.WriterPhaseEnded, 6_200),
                    RuntimeBoundaryPhaseKind.Writer, true).Accepted,
            "Undefined phase consumed a control hold or altered the active writer phase.");
        rejected.Add("undefined-phase-start-and-end-preserve-clock-state-and-fixed-holds");
    }

    private static void CheckRejectedIdsCannotPoisonReservedEvidence(List<string> positive, List<string> rejected)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(972);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100);
        Require(fixture.Reducer.AcquireLease(LeaseRequest(fixture, operation, "poisoned-id", 110)).Accepted &&
                fixture.Reducer.ObservePStop(P(fixture, RuntimeBoundaryEventKind.PStop, 200)).Accepted,
            "Replay-authority fixture did not reserve its live ticket.");
        var cutoff = fixture.Reducer.Snapshot().OuterCutoffTicks ?? throw new InvalidOperationException("Missing F.");
        var report = P(fixture, RuntimeBoundaryEventKind.ReportFrozen, cutoff);
        var completion = P(fixture, RuntimeBoundaryEventKind.UseCompletion, cutoff + 1, operation, "poisoned-id");
        var drain = P(fixture, RuntimeBoundaryEventKind.Drain, cutoff + 2, operation, "poisoned-id");
        Require(!fixture.Reducer.FreezeReport(report with { Epoch = 2 }, "invalid-context").Accepted &&
                !fixture.Reducer.ObserveUseCompletion(completion with { Epoch = 2 }).Accepted &&
                !fixture.Reducer.ObserveLeaseDrain(drain with { Epoch = 2 }).Accepted,
            "Malformed context gained reserved settlement authority.");
        for (var index = 0; index < 80; index++)
            _ = fixture.Reducer.ObserveUseCompletion(
                P(fixture, RuntimeBoundaryEventKind.UseCompletion, cutoff + 1, operation, "poisoned-id") with { Epoch = 2 });
        Require(fixture.Reducer.Snapshot().OrdinaryRecords == RuntimeBoundaryReducer.MaximumOrdinaryRecords &&
                fixture.Reducer.FreezeReport(report, "reserved-report").Accepted &&
                fixture.Reducer.ObserveUseCompletion(completion).Accepted &&
                fixture.Reducer.ObserveLeaseDrain(drain).Accepted,
            "Rejected event IDs or flood saturation blocked exact reserved report/completion/drain.");
        var settled = fixture.Reducer.Snapshot();
        Require(settled.Leases.Single().Drained && settled.Report!.Leases.Single().Drained == false,
            "Reserved settlement altered the frozen custody report.");
        Require(!fixture.Reducer.ObserveUseCompletion(completion with { ObservedTicks = cutoff + 3 }).Accepted &&
                fixture.Reducer.Snapshot().RemainingLeaseTerminalHolds == settled.RemainingLeaseTerminalHolds,
            "Accepted event replay consumed another terminal hold.");
        positive.Add("rejected-ids-stay-diagnostic-and-cannot-poison-reserved-settlement-after-flood");
        rejected.Add("accepted-terminal-event-id-remains-one-use-replay-authority");
    }

    private static void CheckNullObservationBoundaries(List<string> rejected)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(973);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100);
        var before = fixture.Reducer.Snapshot();
        Require(!fixture.Reducer.ObserveDeadline(null!).Accepted &&
                !fixture.Reducer.ObserveEntry(null!, RuntimeBoundaryEntryDisposition.Missing).Accepted &&
                !fixture.Reducer.RetainFirstRawReturn(null!,
                    Raw(SupervisionCallKind.AcquireMarker, 0, [], 10)).Accepted &&
                !fixture.Reducer.RecordPublication(null!, RuntimeBoundaryPublicationDisposition.Completed).Accepted &&
                !fixture.Reducer.ObserveDelivery(null!).Accepted,
            "Null observations bypassed bounded rejection.");
        var after = fixture.Reducer.Snapshot();
        Require(after.RemainingOperationTerminalHolds == before.RemainingOperationTerminalHolds &&
                after.RemainingFixedHolds == before.RemainingFixedHolds && after.LastPTicks == before.LastPTicks &&
                after.Operations.Single().Stage == before.Operations.Single().Stage,
            "Null observations consumed protected evidence or mutated policy.");
        rejected.Add("null-observation-boundaries-reject-without-consuming-protected-state");
    }

    private static void CheckOwnerExitNeverRepresentsWriter(
        List<string> positive,
        List<string> incomplete)
    {
        var fixture = NewFixture();
        var operation = FixtureGuid(950);
        Commit(fixture, operation, SupervisionCallKind.AcquireMarker, 100);
        _ = fixture.Reducer.ObservePStop(P(fixture, RuntimeBoundaryEventKind.PStop, 200));
        Require(fixture.Reducer.ObserveOwnerExit(
                P(fixture, RuntimeBoundaryEventKind.OwnerExit, 1_000)).Accepted,
            "Supplied owner exit observation was rejected.");
        var state = fixture.Reducer.Snapshot();
        Require(state.OwnerExitObserved && state.WriterDisposition == RuntimeBoundaryWriterDisposition.Unrepresented &&
                !state.NativeCallsPerformed && !state.PeerAuthenticated &&
                !state.ProductionAdmissionGranted && !state.ReleaseApproved,
            "O exit represented W state or promoted a proof claim.");
        positive.Add("owner-exit-never-supplies-missing-writer-state");
        incomplete.Add("all-native-auth-authority-production-b1-and-release-claims-remain-false");
    }

    private static Fixture NewFixture()
    {
        var expected = ExpectedContext(Slots());
        return new(new RuntimeBoundaryReducer(expected, 1, PClockDomain, Policy.MonotonicFrequency), expected);
    }

    private static DiagnosticDecodeContext ExpectedContext(IReadOnlyList<SupervisionCapabilitySlot> slots) => new DiagnosticDecodeContext(
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
        OwnerSlotId,
        PClockDomain,
        OClockDomain,
        "trusted-parent",
        SdkDigest,
        "windows-x64-supplied",
        CustodyDigest,
        slots,
        Policy,
        Policy.MonotonicFrequency).Freeze();

    private static IReadOnlyList<SupervisionCapabilitySlot> Slots() =>
    [
        Slot(OwnerSlotId, Controller, Owner, 101, FixtureGuid(9_001), 1),
        Slot(WriterSlotId, Controller, Writer, 102, FixtureGuid(9_002), 1),
    ];

    private static SupervisionCapabilitySlot Slot(
        string id,
        InventoryProcessIdentity holder,
        InventoryProcessIdentity target,
        long coordinate,
        Guid leaseId,
        long generation) => new(
        id,
        holder,
        InventoryRole.Controller,
        null,
        target.Role,
        InventoryObjectClass.Process,
        InventoryCoordinateKind.Owned,
        coordinate,
        holder,
        new InventoryObjectIdentity(id, TargetProcessId: target.ProcessId,
            TargetCreationFileTime: target.CreationFileTime, LeaseId: leaseId, Generation: generation),
        0x001FFFFF,
        0x001FFFFF,
        false,
        InventoryMeasurementProvenance.Synthetic);

    private static RuntimeBoundarySlotReference OwnerSlot(Fixture fixture)
    {
        var slot = fixture.Expected.ExpectedCustody!.Single(item => item.LogicalSlotId == OwnerSlotId);
        return new(slot.LogicalSlotId, slot.ObjectIdentity.LeaseId!.Value,
            slot.ObjectIdentity.Generation!.Value, slot.Holder, slot.CoordinateOwner);
    }

    private static RuntimeBoundaryObservation P(
        Fixture fixture,
        RuntimeBoundaryEventKind kind,
        long ticks,
        Guid? operationId = null,
        string? ticketId = null) => new(
        RunId,
        SessionId,
        InventoryAnchor.B1,
        1,
        ContextDigest,
        1,
        fixture.NextEventId(),
        RuntimeBoundaryLane.ManagementP,
        Controller,
        ticks,
        PClockDomain,
        fixture.Expected.ExpectedPolicy!.MonotonicFrequency,
        kind,
        operationId,
        ticketId);

    private static RuntimeBoundaryObservation O(
        Fixture fixture,
        RuntimeBoundaryEventKind kind,
        long ticks,
        Guid? operationId = null,
        string? ticketId = null) => new(
        RunId,
        SessionId,
        InventoryAnchor.B1,
        1,
        ContextDigest,
        1,
        fixture.NextEventId(),
        RuntimeBoundaryLane.OwnerO,
        Owner,
        ticks,
        OClockDomain,
        Policy.MonotonicFrequency,
        kind,
        operationId,
        ticketId);

    private static void Commit(
        Fixture fixture,
        Guid operationId,
        SupervisionCallKind kind,
        long ticks)
    {
        var result = fixture.Reducer.CommitOperation(new(
            P(fixture, RuntimeBoundaryEventKind.OperationCommit, ticks, operationId),
            operationId,
            kind,
            OwnerSlot(fixture)));
        Require(result.Accepted, $"Could not commit {kind}: {result.Reason}");
    }

    private static void Admit(Fixture fixture, Guid operationId, long ticks)
    {
        var result = fixture.Reducer.AdmitOwner(new(
            O(fixture, RuntimeBoundaryEventKind.OAdmission, ticks, operationId),
            operationId,
            OwnerSlot(fixture)));
        Require(result.Accepted, $"Could not admit owner token: {result.Reason}");
    }

    private static RuntimeBoundaryLeaseRequest LeaseRequest(
        Fixture fixture,
        Guid operationId,
        string ticket,
        long ticks) => new(
        P(fixture, RuntimeBoundaryEventKind.LeaseAcquired, ticks, operationId, ticket),
        ticket,
        operationId,
        OwnerSlot(fixture),
        RuntimeBoundaryLane.ManagementP,
        Controller,
        RuntimeBoundaryLeasePurpose.OwnerObservation);

    private static DiagnosticFirstRawReturn Raw(
        SupervisionCallKind kind,
        uint status,
        IReadOnlyList<long> coordinates,
        long ownerTicks) => new(
        Owner,
        kind,
        status,
        coordinates,
        null,
        ownerTicks,
        OClockDomain,
        Policy.MonotonicFrequency,
        null,
        DiagnosticProofClaims.False);

    private static Guid FixtureGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        bytes[15] = 0x7B;
        return new Guid(bytes);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private sealed class Fixture(RuntimeBoundaryReducer reducer, DiagnosticDecodeContext expected)
    {
        private int _nextEvent = 20_000;
        internal RuntimeBoundaryReducer Reducer { get; } = reducer;
        internal DiagnosticDecodeContext Expected { get; } = expected;
        internal Guid NextEventId() => FixtureGuid(_nextEvent++);
    }

    private static readonly Guid RunId = Guid.Parse("51637e5e-33f2-4971-8df4-385c6f872114");
    private static readonly Guid SessionId = Guid.Parse("e10ea54f-697f-42ae-9fa5-f97868779483");
    private static readonly InventoryProcessIdentity Controller = new(InventoryRole.Controller, 7101, 40101);
    private static readonly InventoryProcessIdentity Owner = new(InventoryRole.Owner, 7102, 40102);
    private static readonly InventoryProcessIdentity Writer = new(InventoryRole.Writer, 7103, 40103);
    private static readonly SupervisionPolicy Policy = SupervisionPolicy.FromFrequency(1_000);
    private static readonly string ContextDigest = new('e', 64);
    private static readonly string SourceDigest = new('a', 64);
    private static readonly string BinaryDigest = new('b', 64);
    private static readonly string SdkDigest = new('c', 64);
    private static readonly string CustodyDigest = new('d', 64);
    private const string OwnerSlotId = "p:o-process-original";
    private const string WriterSlotId = "p:w-process-retained";
    private const string PClockDomain = "p-clock";
    private const string OClockDomain = "o-clock";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
