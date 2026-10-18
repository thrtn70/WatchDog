using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static class CollectorSupervisionProposalChecks
{
    internal static int Run()
    {
        var positive = new List<string>();
        var rejected = new List<string>();
        var incomplete = new List<string>();
        try
        {
            CheckContextAndCustody(positive, rejected);
            CheckDeadlineAndUnknownCustody(positive, rejected);
            CheckAllCallTimeoutKinds(rejected);
            CheckBudgets(positive, rejected, incomplete);
            CheckTerminationAndExit(positive, rejected, incomplete);
            CheckPostReleaseLiveness(positive, rejected);
            CheckReviewRegressions(positive, rejected, incomplete);
            CheckFinalReviewRegressions(rejected);
            CheckLegacyInventoryCollectorRule(rejected);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "watchdog.ow-collector-supervision-proposal-checks.v1",
                succeeded = true,
                syntheticOnly = true,
                suppliedCountersOnly = true,
                nativeCallsPerformed = false,
                nativeCapturePerformed = false,
                nativeCaptureProven = false,
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
                schema = "watchdog.ow-collector-supervision-proposal-checks.v1",
                succeeded = false,
                syntheticOnly = true,
                suppliedCountersOnly = true,
                nativeCallsPerformed = false,
                nativeCapturePerformed = false,
                nativeCaptureProven = false,
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

    private static void CheckContextAndCustody(List<string> positive, List<string> rejected)
    {
        var mutable = Slots(InventoryAnchor.B1, "trusted-parent", targetWriter: false).ToList();
        var trustedCopy = mutable.Select(CopySlot).ToArray();
        var model = new CollectorSupervisionProposal(Binding(mutable));
        mutable[0] = mutable[0] with { Coordinate = mutable[0].Coordinate + 500 };
        var ack = trustedCopy.Select(CopySlot).ToArray();
        Require(model.AcknowledgeCustody(ack).Accepted, "Copied trusted custody did not prepare.");
        ack[0] = ack[0] with { Coordinate = ack[0].Coordinate + 700 };
        Require(model.Snapshot().ExpectedCustody[0].Coordinate != mutable[0].Coordinate &&
                model.Snapshot().AcknowledgedCustody[0].Coordinate != ack[0].Coordinate,
            "Expected or acknowledged custody remained caller-mutable.");
        positive.Add("trusted-and-candidate-custody-snapshotted-independently");

        var missingAck = NewHarness(acknowledge: false);
        Require(!missingAck.Model.AcknowledgeCustody(null).Accepted &&
                !missingAck.Model.Snapshot().CustodyAcknowledged,
            "Missing cleanup custody prepared the proposal.");
        rejected.Add("missing-supervisor-cleanup-custody-rejected");

        var wrongAck = Slots(InventoryAnchor.B1, "trusted-parent", targetWriter: false).Select(CopySlot).ToArray();
        wrongAck[0] = wrongAck[0] with { GrantedAccess = wrongAck[0].GrantedAccess!.Value + 1 };
        var wrongModel = new CollectorSupervisionProposal(Binding(
            Slots(InventoryAnchor.B1, "trusted-parent", targetWriter: false)));
        Require(!wrongModel.AcknowledgeCustody(wrongAck).Accepted &&
                !wrongModel.Snapshot().CustodyAcknowledged,
            "Wrong access ACK prepared the proposal.");
        rejected.Add("exact-slot-object-access-owner-provenance-required");

        var b0 = NewHarness(anchor: InventoryAnchor.B0, acknowledge: false);
        Require(!b0.Model.AcknowledgeCustody(b0.Slots).Accepted, "B0 admitted unavailable independent W custody.");
        rejected.Add("b0-w-custody-unavailable");

        var noCleanup = Slots(InventoryAnchor.B1, "trusted-parent", targetWriter: false)
            .Where(item => item.LogicalSlotId is not ("p:fixture-base" or "p:run-root" or "p:trusted-parent"))
            .ToArray();
        RequireThrows<ArgumentException>(() => new CollectorSupervisionProposal(Binding(noCleanup)),
            "Binding without stage cleanup objects was accepted.");
        rejected.Add("stage-derived-cleanup-slots-required");

        var noSupervisor = Slots(InventoryAnchor.B1, "trusted-parent", targetWriter: false)
            .Where(item => item.LogicalSlotId != "p:outer-job")
            .ToArray();
        RequireThrows<ArgumentException>(() => new CollectorSupervisionProposal(Binding(noSupervisor)),
            "Binding without exact P outer-job custody was accepted.");
        rejected.Add("missing-independent-supervisor-capability-rejected");

        RequireThrows<ArgumentException>(() => new CollectorSupervisionProposal(Binding(null!)),
            "Null expected custody was accepted.");
        RequireThrows<OverflowException>(() => SupervisionPolicy.FromFrequency(long.MaxValue),
            "Monotonic policy arithmetic overflowed open.");
        rejected.Add("null-context-and-checked-clock-arithmetic-fail-closed");

        var writerSlots = Slots(InventoryAnchor.B1, "trusted-parent", targetWriter: true);
        var writerBinding = Binding(writerSlots, targetWriter: true) with
        {
            Target = Writer with { CreationFileTime = Writer.CreationFileTime + 1 },
        };
        RequireThrows<ArgumentException>(() => new CollectorSupervisionProposal(writerBinding),
            "Same-role wrong-creation target was accepted.");
        rejected.Add("wrong-target-birth-and-slot-rejected");
    }

    private static void CheckDeadlineAndUnknownCustody(List<string> positive, List<string> rejected)
    {
        var exact = NewHarness();
        var operation = exact.Begin(SupervisionCallKind.AcquireMarker);
        var deadline = exact.Model.Snapshot().InFlightDeadlineMonotonicTicks!.Value;
        var atDeadline = exact.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker,
            exact.Event(operation, Owner, deadline),
            ObserverResourceLifecycle.Success,
            111,
            SupervisionPayloadBudget.Empty);
        Require(atDeadline.Accepted && atDeadline.Reason == "completion-at-or-after-deadline-deferred" &&
                atDeadline.State.OriginalPendingCompletionMonotonicTicks == deadline &&
                atDeadline.State.Observer.InFlightOperationId == operation &&
                atDeadline.State.Observer.Marker is null,
            "Exactly-at-deadline completion was adjusted or prematurely delivered.");
        var delivered = exact.Model.DeliverPendingCompletion(exact.Event(operation, Controller, deadline + 1));
        Require(delivered.Accepted && delivered.State.State == CollectorSupervisionState.Aborted &&
                delivered.State.Observer.Marker?.CleanupOnly == true && delivered.State.InFlightOperationId is null,
            "Explicit later delivery did not remain cleanup-only.");
        positive.Add("exact-deadline-preserves-raw-time-and-requires-later-delivery");

        var malformed = NewHarness();
        var malformedOperation = malformed.Begin(SupervisionCallKind.AcquireMarker);
        var ordinaryBefore = malformed.Model.Snapshot().OrdinaryEvents;
        for (var index = 0; index < SupervisionPolicy.MaximumUnknownReturns + 1; index++)
        {
            var eventIdentity = malformed.PeekEvent(malformedOperation, Writer);
            var result = malformed.Model.CompleteAcquisition(
                SupervisionCallKind.AcquireMarker,
                eventIdentity,
                5,
                500 + index,
                SupervisionPayloadBudget.Empty);
            Require(!result.Accepted, "Malformed positive-coordinate completion passed.");
        }
        var malformedState = malformed.Model.Snapshot();
        Require(malformedState.UnknownObligations.Count == SupervisionPolicy.MaximumUnknownReturns &&
                malformedState.UnknownReturnOverflow && malformedState.OrdinaryEvents == ordinaryBefore,
            "Malformed returns were dropped or consumed cleanup reserves.");
        rejected.Add("malformed-positive-returns-retained-with-sticky-overflow");

        var errorPositive = NewHarness();
        var errorOperation = errorPositive.Begin(SupervisionCallKind.AcquireMarker);
        var errorReturn = errorPositive.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker,
            errorPositive.Event(errorOperation, Owner),
            5,
            450,
            SupervisionPayloadBudget.Empty);
        Require(!errorReturn.Accepted && errorReturn.State.Observer.UnknownReturns.Any(item => item.Coordinate == 450),
            "Trusted error-plus-positive return was not retained by the observer ledger.");
        rejected.Add("error-positive-coordinate-is-unknown-custody");

        var nullEvent = NewHarness();
        var nullOperation = nullEvent.Begin(SupervisionCallKind.AcquireMarker);
        var nullReturn = nullEvent.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker, null!, 0, 451, SupervisionPayloadBudget.Empty);
        Require(!nullReturn.Accepted && nullReturn.State.UnknownObligations.Any(item => item.Coordinate == 451) &&
                nullReturn.State.InFlightOperationId == nullOperation,
            "Null completion discarded returned custody or cleared the in-flight call.");
        rejected.Add("null-completion-preserves-positive-return-custody");

        var unpublished = NewHarness();
        var unpublishedOperation = unpublished.Begin(SupervisionCallKind.AcquireMarker);
        var transport = unpublished.Model.RecordTransportLoss(
            unpublished.Event(unpublishedOperation, Controller), ObserverResourceKind.Marker, true);
        Require(transport.Accepted && transport.State.UnknownObligations.Single().Coordinate is null &&
                transport.State.Observer.InFlightOperationId == unpublishedOperation,
            "Transport loss did not preserve possible unpublished custody.");
        rejected.Add("transport-loss-preserves-unpublished-descriptor-obligation");

        var replay = NewHarness();
        var replayOperation = Guid.NewGuid();
        var replayEvent = replay.PeekEvent(replayOperation, Owner);
        Require(replay.Model.BeginCall(SupervisionCallKind.AcquireMarker, replayEvent, SupervisionPayloadBudget.Empty).Accepted,
            "Replay setup failed.");
        Require(!replay.Model.BeginCall(SupervisionCallKind.AcquireMarker, replayEvent, SupervisionPayloadBudget.Empty).Accepted,
            "Operation/event replay passed.");
        var operationReplay = NewHarness();
        var firstOperation = operationReplay.Begin(SupervisionCallKind.AcquireMarker);
        Require(operationReplay.Model.CompleteAcquisition(
                SupervisionCallKind.AcquireMarker,
                operationReplay.Event(firstOperation, Owner),
                0,
                475,
                SupervisionPayloadBudget.Empty).Accepted,
            "Operation replay setup failed.");
        var replayedOperation = operationReplay.Model.BeginCall(
            SupervisionCallKind.AcquireSnapshot,
            operationReplay.Event(firstOperation, Owner),
            SupervisionPayloadBudget.Empty);
        Require(!replayedOperation.Accepted && replayedOperation.State.State == CollectorSupervisionState.Aborted,
            "Completed operation identifier was reused for a new call.");
        rejected.Add("one-call-event-and-completed-operation-replay-rejected");

        var wrongRelease = PreparedFor(SupervisionCallKind.ReleaseMarker);
        var retained = wrongRelease.Model.Snapshot().Observer.Marker!;
        var releaseOperation = wrongRelease.Begin(SupervisionCallKind.ReleaseMarker);
        var releaseResult = wrongRelease.Model.CompleteRelease(
            SupervisionCallKind.ReleaseMarker,
            wrongRelease.Event(releaseOperation, Owner),
            retained with { Coordinate = retained.Coordinate + 1 },
            ObserverResourceLifecycle.Success);
        Require(!releaseResult.Accepted && releaseResult.State.Observer.MarkerState == ObserverResourceState.UnknownCustody,
            "Wrong release coordinate was accepted as a successful free.");
        rejected.Add("release-requires-exact-descriptor-coordinate-and-acquisition");
    }

    private static void CheckAllCallTimeoutKinds(List<string> rejected)
    {
        foreach (var kind in Enum.GetValues<SupervisionCallKind>())
        {
            var harness = PreparedFor(kind);
            var operation = harness.Begin(kind);
            var deadline = harness.Model.Snapshot().InFlightDeadlineMonotonicTicks!.Value;
            var timeout = harness.Model.ObserveDeadline(harness.Event(operation, Controller, deadline));
            Require(timeout.Accepted && timeout.State.State == CollectorSupervisionState.Aborted &&
                    timeout.State.Observer.TimeoutObserved && timeout.State.InFlightOperationId == operation,
                $"{kind} timeout did not remain sticky and in flight.");
        }
        rejected.Add("all-five-operation-kinds-timeout-sticky");
    }

    private static void CheckBudgets(List<string> positive, List<string> rejected, List<string> incomplete)
    {
        var full = NewHarness();
        Acquire(full, ObserverResourceKind.Marker, 201);
        Acquire(full, ObserverResourceKind.Snapshot, 202);
        for (var index = 0; index < 55; index++) Walk(full, ObserverResourceLifecycle.Success, rows: 1);
        Walk(full, ObserverResourceLifecycle.NoMoreItems);
        Release(full, ObserverResourceKind.Marker);
        Release(full, ObserverResourceKind.Snapshot);
        var fullState = full.Model.Snapshot();
        Require(fullState.BegunCalls == 60 && fullState.OrdinaryEvents == 116 && fullState.ReleaseEvents == 4 &&
                fullState.Observer.MarkerState == ObserverResourceState.Released &&
                fullState.Observer.SnapshotState == ObserverResourceState.Released,
            "Last admitted terminal walk and both frees did not fit exact reserves.");
        positive.Add("total60calls-128event-partitions-preserve-both-frees");

        var nonterminal = NewHarness();
        Acquire(nonterminal, ObserverResourceKind.Marker, 301);
        Acquire(nonterminal, ObserverResourceKind.Snapshot, 302);
        for (var index = 0; index < 55; index++) Walk(nonterminal, ObserverResourceLifecycle.Success, rows: 1);
        var finalOperation = nonterminal.Begin(SupervisionCallKind.Walk, new(1, 1, 1, 1));
        var final = nonterminal.Model.CompleteWalk(
            nonterminal.Event(finalOperation, Owner),
            ObserverResourceLifecycle.Success,
            new(1, 1, 1, 1));
        Require(final.Accepted && final.Reason == "final-nonterminal-walk-deferred-for-cleanup" &&
                final.State.State == CollectorSupervisionState.Aborted && final.State.InFlightOperationId == finalOperation,
            "Final nonterminal walk did not defer into cleanup-safe abort.");
        Require(nonterminal.Model.DeliverPendingCompletion(nonterminal.Event(finalOperation, Controller)).Accepted,
            "Final nonterminal walk delivery failed.");
        Release(nonterminal, ObserverResourceKind.Marker);
        Release(nonterminal, ObserverResourceKind.Snapshot);
        Require(nonterminal.Model.Snapshot().Observer.SnapshotState == ObserverResourceState.Released &&
                nonterminal.Model.Snapshot().State == CollectorSupervisionState.Aborted,
            "Cleanup releases erased final-walk abort or failed to fit reserves.");
        incomplete.Add("nonterminal-final-budget-walk-remains-incomplete-after-cleanup");

        var flood = NewHarness();
        for (var index = 0; index < 100; index++)
        {
            var invalid = flood.PeekEvent(Guid.NewGuid(), Writer);
            Require(!flood.Model.BeginCall(SupervisionCallKind.AcquireMarker, invalid, SupervisionPayloadBudget.Empty).Accepted,
                "Rejected-event flood admitted a call.");
        }
        Require(flood.Model.Snapshot().OrdinaryEvents == 0 && flood.Model.Snapshot().ReleaseEvents == 0 &&
                flood.Model.Snapshot().AbortAndFinalEvents == 0,
            "Rejected-event flood consumed reserved records.");
        rejected.Add("rejected-event-flood-preserves-release-and-final-reserves");

        var bytes = NewHarness();
        Require(!bytes.Model.BeginCall(
                SupervisionCallKind.AcquireMarker,
                bytes.Event(Guid.NewGuid(), Owner),
                new(0, 0, SupervisionPolicy.MaximumEncodedBytes + 1, 1)).Accepted,
            "Oversized supplied encoded counter passed.");
        var rows = NewHarness();
        Require(!rows.Model.BeginCall(
                SupervisionCallKind.AcquireMarker,
                rows.Event(Guid.NewGuid(), Owner),
                new(SupervisionPolicy.MaximumRows + 1, 0, 0, 1)).Accepted,
            "Oversized supplied row counter passed.");
        var copied = NewHarness();
        Require(!copied.Model.BeginCall(
                SupervisionCallKind.AcquireMarker,
                copied.Event(Guid.NewGuid(), Owner),
                new(0, SupervisionPolicy.MaximumCopiedBytes + 1, 0, 1)).Accepted,
            "Oversized supplied copied-byte counter passed.");
        var frame = NewHarness();
        Require(!frame.Model.BeginCall(
                SupervisionCallKind.AcquireMarker,
                frame.Event(Guid.NewGuid(), Owner),
                new(0, 0, 0, SupervisionPolicy.MaximumFrameBytes + 1)).Accepted,
            "Oversized supplied frame counter passed.");
        rejected.Add("synthetic-row-byte-packet-frame-bounds-fail-closed");
    }

    private static void CheckTerminationAndExit(List<string> positive, List<string> rejected, List<string> incomplete)
    {
        var pending = NewHarness();
        var operation = pending.Begin(SupervisionCallKind.AcquireMarker);
        var deadline = pending.Model.Snapshot().InFlightDeadlineMonotonicTicks!.Value;
        pending.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker,
            pending.Event(operation, Owner, deadline),
            ObserverResourceLifecycle.Success,
            401,
            SupervisionPayloadBudget.Empty);
        var graceEnd = deadline + pending.Policy.LateCompletionGraceTicks;
        var terminate = pending.Model.RequestTermination(
            pending.Event(operation, Controller, graceEnd), Owner, 0);
        Require(terminate.Accepted && terminate.State.OriginalPendingCompletionMonotonicTicks == deadline &&
                terminate.State.UnknownObligations.Any(item => item.Coordinate == 401),
            "P could not escalate after grace while retaining pending raw custody.");
        Require(!pending.Model.DeliverPendingCompletion(pending.PeekEvent(operation, Controller)).Accepted,
            "Pending completion delivered after termination began.");
        var reaped = pending.Model.RecordExactExit(
            pending.Event(operation, Controller, graceEnd + 2), Owner, true, 77, 90001,
            SupervisionJobObservation.UnknownAfterLastHandleClose);
        Require(reaped.Accepted && reaped.State.State == CollectorSupervisionState.ReapedWithUnknownResources &&
                reaped.State.Observer.MarkerState != ObserverResourceState.Released,
            "Exact reap or unknown job was rewritten as descriptor release.");
        incomplete.Add("pending-late-return-termination-reap-retains-unresolved-resources");

        var noExit = TimedOutAcquire();
        var noExitOperation = noExit.Model.Snapshot().InFlightOperationId!.Value;
        Require(noExit.Model.RequestTermination(noExit.Event(noExitOperation, Controller), Owner, 0).Accepted,
            "Termination request setup failed.");
        var notSignaled = noExit.Model.RecordExactExit(
            noExit.Event(noExitOperation, Controller), Owner, false, 77, 90002, SupervisionJobObservation.EmptyWhileOwned);
        Require(!notSignaled.Accepted && notSignaled.State.State == CollectorSupervisionState.CleanupUnresolved,
            "Successful kill request without signaled exact exit passed.");
        rejected.Add("kill-request-is-not-signaled-exit");

        var wrongBirth = TimedOutAcquire();
        var wrongBirthOperation = wrongBirth.Model.Snapshot().InFlightOperationId!.Value;
        wrongBirth.Model.RequestTermination(wrongBirth.Event(wrongBirthOperation, Controller), Owner, 0);
        var samePidWrongCreation = Owner with { CreationFileTime = Owner.CreationFileTime + 1 };
        var wrongExit = wrongBirth.Model.RecordExactExit(
            wrongBirth.Event(wrongBirthOperation, Controller), samePidWrongCreation, true, 88, 90003,
            SupervisionJobObservation.EmptyWhileOwned);
        Require(!wrongExit.Accepted && wrongExit.State.State == CollectorSupervisionState.CleanupUnresolved,
            "Same PID with wrong creation identity passed exact exit.");
        rejected.Add("exact-exit-requires-pid-and-creation-identity");

        positive.Add("termination-request-exit-job-and-release-evidence-remain-separate");
    }

    private static void CheckPostReleaseLiveness(List<string> positive, List<string> rejected)
    {
        var harness = NewHarness();
        Acquire(harness, ObserverResourceKind.Marker, 601);
        Acquire(harness, ObserverResourceKind.Snapshot, 602);
        Walk(harness, ObserverResourceLifecycle.NoMoreItems);
        Release(harness, ObserverResourceKind.Marker);
        Release(harness, ObserverResourceKind.Snapshot);
        var operation = Guid.NewGuid();
        var gate = harness.PeekEvent(operation, Controller);
        var stale = harness.Model.EvaluatePostReleaseLiveness(
            gate,
            new(Controller, true, gate.MonotonicTicks),
            new(Owner, true, 1),
            new(Writer, true, gate.MonotonicTicks));
        Require(!stale.Accepted && !stale.State.ProductionAdmissionGranted,
            "Stale role liveness promoted the proposal.");
        rejected.Add("stale-post-release-liveness-rejected");

        var fresh = NewHarness();
        Acquire(fresh, ObserverResourceKind.Marker, 701);
        Acquire(fresh, ObserverResourceKind.Snapshot, 702);
        Walk(fresh, ObserverResourceLifecycle.NoMoreItems);
        Release(fresh, ObserverResourceKind.Marker);
        Release(fresh, ObserverResourceKind.Snapshot);
        var freshEvent = fresh.PeekEvent(Guid.NewGuid(), Controller);
        var result = fresh.Model.EvaluatePostReleaseLiveness(
            freshEvent,
            new(Controller, true, freshEvent.MonotonicTicks),
            new(Owner, true, freshEvent.MonotonicTicks),
            new(Writer, true, freshEvent.MonotonicTicks));
        Require(result.Accepted && result.Reason == "synthetic-cleanup-liveness-only-no-admission" &&
                !result.State.NativeAuthorityProven && !result.State.B1WriteReleaseIssued &&
                !result.State.ReleaseApproved,
            "Fresh synthetic liveness changed a forbidden authority/admission flag.");
        positive.Add("fresh-synthetic-liveness-never-promotes-authority-or-admission");
    }

    private static void CheckLegacyInventoryCollectorRule(List<string> rejected)
    {
        var processes = new Dictionary<InventoryRole, InventoryProcessIdentity>
        {
            [InventoryRole.Controller] = Controller,
            [InventoryRole.Owner] = Owner,
            [InventoryRole.Writer] = Writer,
        };
        var threads = new Dictionary<InventoryRole, uint>
        {
            [InventoryRole.Controller] = 1,
            [InventoryRole.Owner] = 2,
            [InventoryRole.Writer] = 3,
        };
        var resources = new[] { "fixture-base", "run-root", "trusted-parent", "private-root", "frames", "anchor" };
        var fixtures = resources.Select((resource, index) =>
        {
            var leased = resource is "private-root" or "frames" or "anchor";
            var directory = resource is "fixture-base" or "run-root" or "trusted-parent" or "private-root";
            return new InventoryFixtureIdentity(resource, $"name-{index}", $"path-{index}",
                1, $"id-{index}", directory, false, leased ? Guid.NewGuid() : null, leased ? 1 : null);
        }).ToDictionary(item => item.Resource, StringComparer.Ordinal);
        var context = new InventoryTrustedContext(
            RunId, SessionId, "synthetic", "trusted-parent", InventoryAnchor.B1, 1, Owner,
            InventoryRole.Owner, "B1-prearm-accepted-suspended", false, processes, threads, fixtures);
        RequireThrows<InvalidDataException>(() => InventoryContract.DeriveRequiredSlots(context),
            "Accepted v1 inventory context allowed O as actual collector.");
        rejected.Add("legacy-v1-still-rejects-o-actual-collector");
    }

    private static void CheckReviewRegressions(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var pending = NewHarness();
        var pendingOperation = pending.Begin(SupervisionCallKind.AcquireMarker);
        var pendingDeadline = pending.Model.Snapshot().InFlightDeadlineMonotonicTicks!.Value;
        pending.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker,
            pending.Event(pendingOperation, Owner, pendingDeadline),
            0,
            901,
            SupervisionPayloadBudget.Empty);
        var firstPending = pending.Model.Snapshot().PendingCompletion;
        Require(firstPending is { Kind: SupervisionCallKind.AcquireMarker, ApiStatus: 0, Coordinate: 901 } &&
                firstPending.OperationId == pendingOperation &&
                firstPending.OriginalMonotonicTicks == pendingDeadline,
            "Pending raw completion tuple was not exposed before delivery.");
        var pendingJson = JsonSerializer.Serialize(firstPending, JsonOptions);
        Require(pendingJson.Contains("901", StringComparison.Ordinal) &&
                pendingJson.Contains(pendingDeadline.ToString(), StringComparison.Ordinal),
            "Pending raw completion tuple was not serializable before delivery.");
        pending.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker,
            pending.PeekEvent(pendingOperation, Owner),
            5,
            902,
            SupervisionPayloadBudget.Empty);
        Require(pending.Model.Snapshot().PendingCompletion == firstPending &&
                pending.Model.Snapshot().UnknownObligations.Any(item => item.Coordinate == 902),
            "Second completion overwrote the first pending tuple or was dropped.");
        var backward = new CollectorSupervisionEvent(
            RunId, SessionId, InventoryAnchor.B1, 1, 3, 0, Guid.NewGuid(), pendingOperation, Owner, Controller);
        pending.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker, backward, 5, 909, SupervisionPayloadBudget.Empty);
        Require(pending.Model.RequestTermination(
                pending.Event(pendingOperation, Controller,
                    pendingDeadline + pending.Policy.LateCompletionGraceTicks), Owner, 0).Accepted,
            "Untrusted backward completion timestamp shortened the trusted grace window.");
        positive.Add("first-pending-completion-tuple-is-immutable-and-serializable");

        var payload = NewHarness();
        var payloadOperation = payload.Begin(SupervisionCallKind.AcquireMarker);
        var payloadRejected = payload.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker,
            payload.Event(payloadOperation, Owner),
            0,
            903,
            new(0, 0, 1, 1));
        Require(!payloadRejected.Accepted &&
                payloadRejected.State.UnknownObligations.Any(item => item.Coordinate == 903),
            "Payload-over-reservation completion dropped a positive native-style return.");
        rejected.Add("payload-rejection-preserves-positive-return-custody");

        var acquired = NewHarness();
        Acquire(acquired, ObserverResourceKind.Marker, 904);
        Acquire(acquired, ObserverResourceKind.Snapshot, 905);
        var failedWalkOperation = acquired.Begin(SupervisionCallKind.Walk);
        Require(!acquired.Model.CompleteWalk(
                acquired.Event(failedWalkOperation, Owner), 5, SupervisionPayloadBudget.Empty).Accepted,
            "Invalid walk terminal unexpectedly passed.");
        var acquiredOperation = Guid.NewGuid();
        Require(acquired.Model.RequestTermination(acquired.Event(acquiredOperation, Controller), Owner, 0).Accepted,
            "Acquired-resource termination setup failed.");
        var acquiredExit = acquired.Model.RecordExactExit(
            acquired.Event(acquiredOperation, Controller), Owner, true, 1, 91000,
            SupervisionJobObservation.EmptyWhileOwned);
        Require(acquiredExit.Accepted && acquiredExit.State.State == CollectorSupervisionState.ReapedWithUnknownResources &&
                acquiredExit.State.Observer.MarkerState == ObserverResourceState.Acquired,
            "Exact exit converted unreleased acquired descriptors into a clean reap.");
        incomplete.Add("exact-exit-with-acquired-descriptors-remains-unresolved");

        var terminal = TimedOutAcquire();
        var terminalOperation = terminal.Model.Snapshot().InFlightOperationId!.Value;
        terminal.Model.RequestTermination(terminal.Event(terminalOperation, Controller), Owner, 0);
        var stateBeforeDeadline = terminal.Model.Snapshot().State;
        Require(!terminal.Model.ObserveDeadline(terminal.PeekEvent(terminalOperation, Controller)).Accepted &&
                terminal.Model.Snapshot().State == stateBeforeDeadline,
            "Deadline observation reopened a termination phase.");
        Require(!terminal.Model.RecordTransportLoss(
                terminal.PeekEvent(terminalOperation, Controller), ObserverResourceKind.Marker, true).Accepted &&
                terminal.Model.Snapshot().State == stateBeforeDeadline,
            "Transport loss reopened a termination phase.");
        terminal.Model.RecordExactExit(
            terminal.Event(terminalOperation, Controller), Owner, true, 2, 92000,
            SupervisionJobObservation.UnknownAfterLastHandleClose);
        var reapedState = terminal.Model.Snapshot().State;
        Require(!terminal.Model.ObserveDeadline(terminal.PeekEvent(terminalOperation, Controller)).Accepted &&
                terminal.Model.Snapshot().State == reapedState,
            "Deadline observation reopened a reaped terminal phase.");
        Require(!terminal.Model.RecordTransportLoss(
                terminal.PeekEvent(terminalOperation, Controller), ObserverResourceKind.Marker, true).Accepted &&
                terminal.Model.Snapshot().State == reapedState,
            "Transport loss reopened a reaped terminal phase.");
        var terminalCompletion = terminal.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker,
            terminal.PeekEvent(terminalOperation, Owner),
            0,
            999,
            SupervisionPayloadBudget.Empty);
        Require(!terminalCompletion.Accepted && terminalCompletion.State.State == reapedState &&
                terminalCompletion.State.UnknownObligations.Any(item => item.Coordinate == 999),
            "Post-exit completion reopened state or discarded a positive obligation.");
        rejected.Add("escalation-and-exit-phases-are-absorbing");

        var zeroRogue = NewHarness();
        var zeroOperation = zeroRogue.Begin(SupervisionCallKind.AcquireMarker);
        zeroRogue.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker,
            zeroRogue.PeekEvent(zeroOperation, Writer),
            5,
            0,
            SupervisionPayloadBudget.Empty);
        var validAfterRogue = zeroRogue.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker,
            zeroRogue.Event(zeroOperation, Owner),
            0,
            906,
            SupervisionPayloadBudget.Empty);
        Require(validAfterRogue.Accepted && validAfterRogue.State.State == CollectorSupervisionState.Aborted &&
                validAfterRogue.State.PendingCompletion?.Coordinate == 906,
            "Zero-coordinate rogue completion did not latch abort before a later valid return.");
        rejected.Add("zero-coordinate-rejected-completion-stays-aborted");

        var nearOverflow = NewHarness();
        var overflowEvent = new CollectorSupervisionEvent(
            RunId, SessionId, InventoryAnchor.B1, 1, 1,
            long.MaxValue - 1, Guid.NewGuid(), Guid.NewGuid(), Owner, Controller);
        Require(!nearOverflow.Model.BeginCall(
                SupervisionCallKind.AcquireMarker, overflowEvent, SupervisionPayloadBudget.Empty).Accepted &&
                nearOverflow.Model.Snapshot().BegunCalls == 0,
            "Unrepresentable clock horizon mutated call state.");
        rejected.Add("accepted-clock-horizons-are-preflighted-with-checked-arithmetic");

        var encoded = NewHarness();
        Require(!encoded.Model.BeginCall(
                SupervisionCallKind.AcquireMarker,
                encoded.Event(Guid.NewGuid(), Owner),
                new(0, 0, SupervisionPolicy.MaximumOrdinaryEncodedBytes + 1, 1)).Accepted,
            "Ordinary payload consumed reserved cleanup/final encoded capacity.");
        rejected.Add("encoded-capacity-reserves-cleanup-and-final-metadata");

        var aliased = Slots(InventoryAnchor.B1, "trusted-parent", targetWriter: false).Select(CopySlot).ToArray();
        aliased[1] = aliased[1] with { Coordinate = aliased[0].Coordinate };
        RequireThrows<ArgumentException>(() => new CollectorSupervisionProposal(Binding(aliased)),
            "Distinct owned slots aliased one owner coordinate.");
        var weakMask = Slots(InventoryAnchor.B1, "trusted-parent", targetWriter: false).Select(CopySlot).ToArray();
        weakMask[0] = weakMask[0] with { RequestedAccess = 1, GrantedAccess = 1 };
        RequireThrows<ArgumentException>(() => new CollectorSupervisionProposal(Binding(weakMask)),
            "P retained O process custody omitted wait/query supervision rights.");
        rejected.Add("canonical-owned-coordinates-and-supervision-masks-required");

        var outerAbort = NewHarness();
        Acquire(outerAbort, ObserverResourceKind.Marker, 907);
        Acquire(outerAbort, ObserverResourceKind.Snapshot, 908);
        Walk(outerAbort, ObserverResourceLifecycle.NoMoreItems);
        Release(outerAbort, ObserverResourceKind.Marker);
        Release(outerAbort, ObserverResourceKind.Snapshot);
        outerAbort.Model.CompleteAcquisition(
            SupervisionCallKind.AcquireMarker,
            outerAbort.PeekEvent(Guid.NewGuid(), Writer),
            5,
            0,
            SupervisionPayloadBudget.Empty);
        var vetoEvent = outerAbort.PeekEvent(Guid.NewGuid(), Controller);
        var veto = outerAbort.Model.EvaluatePostReleaseLiveness(
            vetoEvent,
            new(Controller, true, vetoEvent.MonotonicTicks),
            new(Owner, true, vetoEvent.MonotonicTicks),
            new(Writer, true, vetoEvent.MonotonicTicks));
        Require(!veto.Accepted && !veto.State.ProductionAdmissionGranted,
            "A clean inner observer gate bypassed the sticky outer abort.");
        rejected.Add("outer-abort-vetoes-clean-inner-liveness");
    }

    private static Harness PreparedFor(SupervisionCallKind kind)
    {
        var harness = NewHarness();
        if (kind == SupervisionCallKind.AcquireMarker) return harness;
        Acquire(harness, ObserverResourceKind.Marker, 801);
        if (kind == SupervisionCallKind.AcquireSnapshot) return harness;
        Acquire(harness, ObserverResourceKind.Snapshot, 802);
        if (kind == SupervisionCallKind.Walk) return harness;
        Walk(harness, ObserverResourceLifecycle.NoMoreItems);
        if (kind == SupervisionCallKind.ReleaseMarker) return harness;
        Release(harness, ObserverResourceKind.Marker);
        return harness;
    }

    private static void CheckFinalReviewRegressions(List<string> rejected)
    {
        var beforeExit = TimedOutAcquire();
        var operation = beforeExit.Model.Snapshot().InFlightOperationId!.Value;
        Require(beforeExit.Model.RequestTermination(beforeExit.Event(operation, Controller), Owner, 0).Accepted,
            "Transport-loss exit-evidence setup failed.");
        beforeExit.Model.RecordTransportLoss(beforeExit.PeekEvent(operation, Controller), ObserverResourceKind.Marker, true);
        Require(beforeExit.Model.Snapshot().UnknownObligations.All(item => !item.OwnerConfirmedExited),
            "Termination request fabricated confirmed-exited owner evidence.");
        Require(beforeExit.Model.RecordExactExit(beforeExit.Event(operation, Controller), Owner, true, 2, 93000,
                SupervisionJobObservation.UnknownAfterLastHandleClose).Accepted,
            "Exact signaled-exit setup failed.");
        beforeExit.Model.RecordTransportLoss(beforeExit.PeekEvent(operation, Controller), ObserverResourceKind.Marker, true);
        Require(beforeExit.Model.Snapshot().UnknownObligations[^1].OwnerConfirmedExited,
            "Exact signaled exit was not distinguished from a termination request.");

        foreach (var (observed, signaled) in new[] { (Owner, false), (Owner with { CreationFileTime = Owner.CreationFileTime + 1 }, true) })
        {
            var unresolved = TimedOutAcquire();
            var unresolvedOperation = unresolved.Model.Snapshot().InFlightOperationId!.Value;
            unresolved.Model.RequestTermination(unresolved.Event(unresolvedOperation, Controller), Owner, 0);
            Require(!unresolved.Model.RecordExactExit(unresolved.Event(unresolvedOperation, Controller), observed,
                    signaled, 2, 94000, SupervisionJobObservation.EmptyWhileOwned).Accepted,
                "Unsignaled or wrong-birth exit was accepted.");
            unresolved.Model.RecordTransportLoss(unresolved.PeekEvent(unresolvedOperation, Controller), ObserverResourceKind.Marker, true);
            Require(unresolved.Model.Snapshot().UnknownObligations.All(item => !item.OwnerConfirmedExited),
                "Unresolved cleanup fabricated confirmed-exited owner evidence.");
        }
        rejected.Add("transport-loss-owner-exit-requires-exact-signaled-evidence");

        var exhausted = TimedOutAcquire();
        var exhaustedOperation = exhausted.Model.Snapshot().InFlightOperationId!.Value;
        for (var index = exhausted.Model.Snapshot().AbortAndFinalEvents;
             index < SupervisionPolicy.ReservedAbortAndFinalEvents - 1; index++)
        {
            Require(!exhausted.Model.RequestTermination(exhausted.Event(exhaustedOperation, Controller), Writer, 0).Accepted,
                "Wrong-target termination request passed.");
        }
        Require(exhausted.Model.RequestTermination(exhausted.Event(exhaustedOperation, Controller), Owner, 0).Accepted &&
                exhausted.Model.Snapshot().AbortAndFinalEvents == SupervisionPolicy.ReservedAbortAndFinalEvents,
            "Final-event capacity setup did not consume the exact reserve.");
        var overflowExit = exhausted.Model.RecordExactExit(exhausted.Event(exhaustedOperation, Controller), Owner,
            true, 2, 95000, SupervisionJobObservation.EmptyWhileOwned);
        Require(!overflowExit.Accepted && overflowExit.Reason == "event-budget-exhausted" &&
                overflowExit.State.State == CollectorSupervisionState.TerminationRequested &&
                overflowExit.State.Termination?.ProcessSignaled == false,
            "Exhausted exit observation reopened local cleanup or fabricated exit evidence.");
        var afterTermination = exhausted.Model.BeginCall(SupervisionCallKind.ReleaseMarker,
            exhausted.PeekEvent(Guid.NewGuid(), Owner), SupervisionPayloadBudget.Empty);
        Require(!afterTermination.Accepted && afterTermination.Reason == "call-after-termination-request",
            "Release was admitted after termination escalation and budget exhaustion.");
        rejected.Add("exhausted-final-event-budget-cannot-reopen-termination");

        var aggregate = NewHarness();
        var aggregateOperation = aggregate.Begin(SupervisionCallKind.AcquireMarker);
        for (var index = 0; index < SupervisionPolicy.MaximumUnknownReturns; index++)
        {
            aggregate.Model.CompleteAcquisition(SupervisionCallKind.AcquireMarker,
                aggregate.PeekEvent(aggregateOperation, Writer), 5, 1000 + index, SupervisionPayloadBudget.Empty);
        }
        var pending = aggregate.Model.CompleteAcquisition(SupervisionCallKind.AcquireMarker,
            aggregate.Event(aggregateOperation, Owner), 5, 1100, SupervisionPayloadBudget.Empty);
        Require(pending.State.PendingCompletion?.Coordinate == 1100,
            "Matching positive return was lost before bounded projection.");
        var aggregateDelivery = aggregate.Model.DeliverPendingCompletion(aggregate.Event(aggregateOperation, Controller));
        Require(!aggregateDelivery.Accepted && aggregateDelivery.State.UnknownReturnOverflow &&
                aggregateDelivery.State.UnknownObligations.Count + aggregateDelivery.State.Observer.UnknownReturns.Count ==
                    SupervisionPolicy.MaximumUnknownReturns &&
                aggregateDelivery.State.PendingCompletion is { Coordinate: 1100, ApiStatus: 5 } &&
                aggregateDelivery.State.InFlightOperationId == aggregateOperation &&
                aggregateDelivery.State.State == CollectorSupervisionState.Aborted,
            "Observer projection exceeded the aggregate unknown bound or lost the separately reserved pending tuple.");
        rejected.Add("aggregate-unknown-ledger-cap-preserves-pending-overflow-result");
    }

    private static Harness TimedOutAcquire()
    {
        var harness = NewHarness();
        var operation = harness.Begin(SupervisionCallKind.AcquireMarker);
        var deadline = harness.Model.Snapshot().InFlightDeadlineMonotonicTicks!.Value;
        Require(harness.Model.ObserveDeadline(harness.Event(operation, Controller, deadline)).Accepted,
            "Timeout setup failed.");
        return harness;
    }

    private static void Acquire(Harness harness, ObserverResourceKind kind, long coordinate)
    {
        var callKind = kind == ObserverResourceKind.Marker
            ? SupervisionCallKind.AcquireMarker
            : SupervisionCallKind.AcquireSnapshot;
        var operation = harness.Begin(callKind);
        Require(harness.Model.CompleteAcquisition(
                callKind,
                harness.Event(operation, Owner),
                ObserverResourceLifecycle.Success,
                coordinate,
                SupervisionPayloadBudget.Empty).Accepted,
            $"Could not complete {kind} acquisition.");
    }

    private static void Walk(Harness harness, uint status, int rows = 0)
    {
        var payload = new SupervisionPayloadBudget(rows, rows, rows, rows);
        var operation = harness.Begin(SupervisionCallKind.Walk, payload);
        Require(harness.Model.CompleteWalk(harness.Event(operation, Owner), status, payload).Accepted,
            "Could not complete walk.");
    }

    private static void Release(Harness harness, ObserverResourceKind kind)
    {
        var callKind = kind == ObserverResourceKind.Marker
            ? SupervisionCallKind.ReleaseMarker
            : SupervisionCallKind.ReleaseSnapshot;
        var descriptor = kind == ObserverResourceKind.Marker
            ? harness.Model.Snapshot().Observer.Marker
            : harness.Model.Snapshot().Observer.Snapshot;
        Require(descriptor is not null, $"{kind} descriptor was absent before release.");
        var operation = harness.Begin(callKind);
        Require(harness.Model.CompleteRelease(
                callKind,
                harness.Event(operation, Owner),
                descriptor!,
                ObserverResourceLifecycle.Success).Accepted,
            $"Could not complete {kind} release.");
    }

    private static Harness NewHarness(
        InventoryAnchor anchor = InventoryAnchor.B1,
        bool acknowledge = true,
        string selectedResource = "trusted-parent")
    {
        var slots = Slots(anchor, selectedResource, targetWriter: false);
        var harness = new Harness(new CollectorSupervisionProposal(Binding(slots, anchor: anchor,
            selectedResource: selectedResource)), slots, Policy);
        if (acknowledge)
            Require(harness.Model.AcknowledgeCustody(slots.Select(CopySlot).ToArray()).Accepted,
                "Harness custody ACK failed.");
        return harness;
    }

    private static CollectorSupervisionBinding Binding(
        IReadOnlyList<SupervisionCapabilitySlot> slots,
        InventoryAnchor anchor = InventoryAnchor.B1,
        string selectedResource = "trusted-parent",
        bool targetWriter = false) => new(
            RunId,
            SessionId,
            anchor,
            1,
            Controller,
            Owner,
            Writer,
            targetWriter ? Writer : Owner,
            targetWriter ? SupervisionTargetContext.OwnerOriginalWriterSlot : SupervisionTargetContext.OwnerCurrentProcess,
            targetWriter ? "o:w-process-original" : "o:self-current-process",
            selectedResource,
            slots,
            Policy);

    private static IReadOnlyList<SupervisionCapabilitySlot> Slots(
        InventoryAnchor anchor,
        string selectedResource,
        bool targetWriter)
    {
        var result = new List<SupervisionCapabilitySlot>();
        long coordinate = 100;
        void Add(
            string id,
            InventoryProcessIdentity holder,
            InventoryRole creator,
            InventoryRole? invoking,
            InventoryRole? target,
            InventoryObjectClass objectClass,
            InventoryObjectIdentity objectIdentity,
            uint access) => result.Add(new(id, holder, creator, invoking, target, objectClass,
                InventoryCoordinateKind.Owned, coordinate++, holder, objectIdentity, access, access, false,
                InventoryMeasurementProvenance.Synthetic));

        Add("p:o-process-original", Controller, InventoryRole.Controller, null, InventoryRole.Owner,
            InventoryObjectClass.Process, ProcessObject("o-process", Owner), 0x001FFFFF);
        Add("p:o-primary-thread-original", Controller, InventoryRole.Controller, null, InventoryRole.Owner,
            InventoryObjectClass.Thread, ProcessObject("o-thread", Owner) with { ThreadId = 42 }, 0x001FFFFF);
        Add("p:outer-job", Controller, InventoryRole.Controller, null, InventoryRole.Owner,
            InventoryObjectClass.Job, new("p-outer-job", JobSlot: "p-outer-job"), 0x001F001F);
        Add("p:w-process-retained", Controller, InventoryRole.Owner, InventoryRole.Controller, InventoryRole.Controller,
            InventoryObjectClass.Process, ProcessObject("w-process", Writer), InventoryContract.RetainedWriterProcessAccess);
        AddCleanup("p:fixture-base", InventoryRole.Owner, true, InventoryContract.WritableDirectoryAccess);
        AddCleanup("p:run-root", InventoryRole.Owner, true, InventoryContract.WritableDirectoryAccess);
        AddCleanup("p:trusted-parent", InventoryRole.Owner, true, InventoryContract.WritableDirectoryAccess);
        if (anchor == InventoryAnchor.B2 && selectedResource != "trusted-parent")
        {
            AddCleanup("p:private-root", InventoryRole.Writer, true, InventoryContract.RootReadAccess);
            AddCleanup("p:frames", InventoryRole.Writer, false, InventoryContract.FileReadAccess);
            AddCleanup("p:anchor", InventoryRole.Writer, false, InventoryContract.FileReadAccess);
        }
        if (targetWriter)
            Add("o:w-process-original", Owner, InventoryRole.Owner, null, InventoryRole.Writer,
                InventoryObjectClass.Process, ProcessObject("w-process", Writer), 0x001FFFFF);
        return result;

        void AddCleanup(string id, InventoryRole creator, bool directory, uint access) => Add(
            id,
            Controller,
            creator,
            InventoryRole.Controller,
            InventoryRole.Controller,
            directory ? InventoryObjectClass.Directory : InventoryObjectClass.File,
            new(id, VolumeSerialNumber: 7, FileId: $"file-{id}", IsDirectory: directory, IsReparsePoint: false),
            access);
    }

    private static InventoryObjectIdentity ProcessObject(string id, InventoryProcessIdentity process) =>
        new(id, TargetProcessId: process.ProcessId, TargetCreationFileTime: process.CreationFileTime);

    private static SupervisionCapabilitySlot CopySlot(SupervisionCapabilitySlot slot) => slot with
    {
        Holder = slot.Holder with { },
        CoordinateOwner = slot.CoordinateOwner with { },
        ObjectIdentity = slot.ObjectIdentity with { },
    };

    private static void RequireThrows<TException>(Action action, string message)
        where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Harness
    {
        private long _sequence = 1;
        private long _ticks = 1;

        internal Harness(
            CollectorSupervisionProposal model,
            IReadOnlyList<SupervisionCapabilitySlot> slots,
            SupervisionPolicy policy)
        {
            Model = model;
            Slots = slots;
            Policy = policy;
        }

        internal CollectorSupervisionProposal Model { get; }
        internal IReadOnlyList<SupervisionCapabilitySlot> Slots { get; }
        internal SupervisionPolicy Policy { get; }

        internal Guid Begin(SupervisionCallKind kind, SupervisionPayloadBudget? payload = null)
        {
            var operation = Guid.NewGuid();
            var result = Model.BeginCall(kind, Event(operation, Owner), payload ?? SupervisionPayloadBudget.Empty);
            Require(result.Accepted, $"Could not begin {kind}: {result.Reason}");
            return operation;
        }

        internal CollectorSupervisionEvent Event(
            Guid operation,
            InventoryProcessIdentity actor,
            long? ticks = null)
        {
            var currentTicks = ticks ?? _ticks;
            var value = new CollectorSupervisionEvent(
                RunId, SessionId, Model.Snapshot().Binding.Anchor, 1, _sequence, currentTicks,
                Guid.NewGuid(), operation, actor, Controller);
            _sequence++;
            _ticks = checked(currentTicks + 1);
            return value;
        }

        internal CollectorSupervisionEvent PeekEvent(Guid operation, InventoryProcessIdentity actor) => new(
            RunId, SessionId, Model.Snapshot().Binding.Anchor, 1, _sequence, _ticks,
            Guid.NewGuid(), operation, actor, Controller);
    }

    private static readonly Guid RunId = Guid.Parse("185acbbb-bdc5-4e43-a325-f63efaba9d85");
    private static readonly Guid SessionId = Guid.Parse("4d9ff225-8946-4a6e-a45f-e3bd95242d02");
    private static readonly InventoryProcessIdentity Controller = new(InventoryRole.Controller, 5101, 20101);
    private static readonly InventoryProcessIdentity Owner = new(InventoryRole.Owner, 5102, 20102);
    private static readonly InventoryProcessIdentity Writer = new(InventoryRole.Writer, 5103, 20103);
    private static readonly SupervisionPolicy Policy = SupervisionPolicy.FromFrequency(1_000);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
