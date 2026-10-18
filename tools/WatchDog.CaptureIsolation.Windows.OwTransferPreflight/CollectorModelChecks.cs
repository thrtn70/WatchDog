using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static class CollectorModelChecks
{
    internal static int Run()
    {
        var positiveCases = new List<string>();
        var rejectionCases = new List<string>();
        var incompleteCases = new List<string>();
        try
        {
            CheckLiteralLayout(positiveCases);
            CheckDecoder(positiveCases, rejectionCases, incompleteCases);
            CheckLifecycle(positiveCases, rejectionCases);

            var receipt = new
            {
                schema = "watchdog.ow-observer-collector-model-checks.v1",
                succeeded = true,
                syntheticOnly = true,
                nativeCallsPerformed = false,
                nativeCapturePerformed = false,
                nativeCaptureProven = false,
                nativeAuthorityProven = false,
                nativeDurabilityProven = false,
                completeHandleInventoryProven = false,
                admissionGranted = false,
                positiveChecks = positiveCases.Count,
                rejectionChecks = rejectionCases.Count,
                incompleteChecks = incompleteCases.Count,
                positiveCases,
                rejectionCases,
                incompleteCases,
            };
            Console.WriteLine(JsonSerializer.Serialize(receipt, JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            var receipt = new
            {
                schema = "watchdog.ow-observer-collector-model-checks.v1",
                succeeded = false,
                syntheticOnly = true,
                nativeCallsPerformed = false,
                nativeCapturePerformed = false,
                nativeCaptureProven = false,
                nativeAuthorityProven = false,
                admissionGranted = false,
                positiveChecks = positiveCases.Count,
                rejectionChecks = rejectionCases.Count,
                incompleteChecks = incompleteCases.Count,
                positiveCases,
                rejectionCases,
                incompleteCases,
                failure = $"{exception.GetType().Name}: {exception.Message}",
            };
            Console.WriteLine(JsonSerializer.Serialize(receipt, JsonOptions));
            return 1;
        }
    }

    private static void CheckLiteralLayout(List<string> positive)
    {
        Require(PssHandleEntryDecoder.EntrySize == 136, "PSS entry size drifted.");
        Require(PssHandleEntryDecoder.UnionOffset == 88 && PssHandleEntryDecoder.UnionSize == 48,
            "PSS union layout drifted.");

        var bytes = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.Basic | SyntheticPssHandleFlags.TypeSpecific,
            SyntheticPssObjectType.Process);
        U64(bytes, 0, 0x1122334455667788);
        U64(bytes, 16, 0x0102030405060708);
        U32(bytes, 24, 0x10);
        U32(bytes, 28, 0x120187);
        U32(bytes, 32, 3);
        U32(bytes, 36, 4);
        U32(bytes, 40, 5);
        U32(bytes, 44, 6);
        U64(bytes, 48, 0x8877665544332211);
        U32(bytes, 88, 259);
        U64(bytes, 96, 0x1000);
        U64(bytes, 104, 0x2000);
        I32(bytes, 112, 8);
        U32(bytes, 116, 701);
        U32(bytes, 120, 700);
        U32(bytes, 124, 9);

        var decoded = PssHandleEntryDecoder.Decode(bytes);
        Require(decoded.Status == PssHandleDecodeStatus.Decoded &&
                decoded.Handle == 0x1122334455667788 &&
                decoded.CaptureTime == 0x0102030405060708 &&
                decoded.ReservedCreationTime == 0x8877665544332211,
            "Literal outer offsets were not decoded exactly.");
        Require(decoded.RawBasicFields is { GrantedAccess: 0x120187, Attributes: 0x10 } &&
                decoded.TypeSpecificInformation is PssProcessInformation
                {
                    ExitStatus: 259,
                    PebBaseAddress: 0x1000,
                    AffinityMask: 0x2000,
                    BasePriority: 8,
                    ProcessId: 701,
                    ParentProcessId: 700,
                    Flags: 9,
                }, "Literal process union offsets were not decoded exactly.");
        Require(!decoded.InheritableKnown && !decoded.TargetCreationTimeKnown &&
                !decoded.NativeAuthorityProven && !decoded.ProductionAdmissionGranted,
            "Decoder manufactured proof from raw bytes.");
        positive.Add("literal-x64-pack8-136-offsets-and-process-union");

        var thread = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.TypeSpecific,
            SyntheticPssObjectType.Thread);
        U32(thread, 88, 259);
        U64(thread, 96, 0x8877665544332211);
        U32(thread, 104, 703);
        U32(thread, 108, 704);
        U64(thread, 112, 0x123456789abcdef0);
        I32(thread, 120, -5);
        I32(thread, 124, 7);
        U64(thread, 128, 0xfedcba9876543210);
        Require(PssHandleEntryDecoder.Decode(thread).TypeSpecificInformation is PssThreadInformation
        {
            ExitStatus: 259,
            TebBaseAddress: 0x8877665544332211,
            ProcessId: 703,
            ThreadId: 704,
            AffinityMask: 0x123456789abcdef0,
            Priority: -5,
            BasePriority: 7,
            StartAddress: 0xfedcba9876543210,
        }, "Literal thread union offsets or signed fields drifted.");
        positive.Add("literal-thread-union-offsets-and-signed-priority");

        var mutant = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.TypeSpecific,
            SyntheticPssObjectType.Mutant);
        I32(mutant, 88, -3);
        I32(mutant, 92, 2);
        U32(mutant, 96, 705);
        U32(mutant, 100, 706);
        Require(PssHandleEntryDecoder.Decode(mutant).TypeSpecificInformation is PssMutantInformation
        {
            CurrentCount: -3,
            Abandoned: true,
            OwnerProcessId: 705,
            OwnerThreadId: 706,
        }, "Literal mutant union fields drifted.");
        positive.Add("literal-mutant-union-signed-count-and-bool");

        var eventEntry = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.TypeSpecific,
            SyntheticPssObjectType.Event);
        I32(eventEntry, 88, 0);
        I32(eventEntry, 92, -1);
        Require(PssHandleEntryDecoder.Decode(eventEntry).TypeSpecificInformation is PssEventInformation
        { ManualReset: false, Signaled: true }, "Literal event union BOOL fields drifted.");
        positive.Add("literal-event-union-nonzero-bool");

        var section = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.TypeSpecific,
            SyntheticPssObjectType.Section);
        U64(section, 88, 0x7766554433221100);
        U32(section, 96, 0x12345678);
        U64(section, 104, unchecked((ulong)-0x0102030405L));
        Require(PssHandleEntryDecoder.Decode(section).TypeSpecificInformation is PssSectionInformation
        {
            BaseAddress: 0x7766554433221100,
            AllocationAttributes: 0x12345678,
            MaximumSize: -0x0102030405L,
        }, "Literal section union LARGE_INTEGER was not decoded as signed 64-bit.");
        positive.Add("literal-section-union-signed-large-integer");

        var semaphore = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.TypeSpecific,
            SyntheticPssObjectType.Semaphore);
        I32(semaphore, 88, -7);
        I32(semaphore, 92, 9);
        var semaphoreResult = PssHandleEntryDecoder.Decode(semaphore);
        Require(semaphoreResult.TypeSpecificInformation is PssSemaphoreInformation
        { CurrentCount: -7, MaximumCount: 9 } && !semaphoreResult.ProductionAdmissionGranted,
            "SDK semaphore union layout drifted or manufactured admission.");
        positive.Add("literal-sdk-semaphore-union-without-runtime-admission");
    }

    private static void CheckDecoder(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var valueOnly = Entry(SyntheticPssHandleFlags.Type, SyntheticPssObjectType.Process);
        U64(valueOnly, 16, 123456);
        var valueOnlyResult = PssHandleEntryDecoder.Decode(valueOnly);
        Require(valueOnlyResult.Status == PssHandleDecodeStatus.Decoded &&
                valueOnlyResult.CaptureTime == 123456 && valueOnlyResult.RawBasicFields is null &&
                !valueOnlyResult.IncompleteReasons.Contains("basic-fields-present-without-validity", StringComparer.Ordinal),
            "CaptureTime was incorrectly gated as BASIC data.");
        positive.Add("value-only-capturetime-independent-of-basic");

        var basicAmbiguity = Entry(SyntheticPssHandleFlags.Type, SyntheticPssObjectType.Process);
        U32(basicAmbiguity, 28, 0x120187);
        var basicAmbiguityResult = PssHandleEntryDecoder.Decode(basicAmbiguity);
        RequireIncomplete(basicAmbiguityResult, "basic-fields-present-without-validity");
        Require(basicAmbiguityResult.RawBasicFields is null && !basicAmbiguityResult.InheritableKnown,
            "Absent BASIC validity produced a capability observation.");
        incomplete.Add("absent-basic-validity-remains-ambiguous");

        var unknownFlags = Entry((SyntheticPssHandleFlags)0x81, SyntheticPssObjectType.Process);
        RequireIncomplete(PssHandleEntryDecoder.Decode(unknownFlags), "unknown-handle-flags");
        incomplete.Add("unknown-flags-incomplete");

        var unknownType = Entry(SyntheticPssHandleFlags.Type, SyntheticPssObjectType.Unknown);
        RequireIncomplete(PssHandleEntryDecoder.Decode(unknownType), "unsupported-object-type");
        incomplete.Add("type-valid-unknown-type-incomplete");

        var unavailableType = Entry(SyntheticPssHandleFlags.None, SyntheticPssObjectType.Unknown);
        RequireIncomplete(PssHandleEntryDecoder.Decode(unavailableType), "object-type-unavailable");
        incomplete.Add("untyped-zero-object-type-unavailable");

        var reservedCreation = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.Basic, SyntheticPssObjectType.Process);
        U64(reservedCreation, 48, 99887766);
        var reservedResult = PssHandleEntryDecoder.Decode(reservedCreation);
        Require(reservedResult.ReservedCreationTime == 99887766 &&
                reservedResult.RawBasicFields?.ReservedCreationTime == 99887766 &&
                !reservedResult.TargetCreationTimeKnown,
            "Reserved creation field became target identity evidence.");
        positive.Add("reserved-creation-is-raw-never-target-creation");

        var typeName = Entry(SyntheticPssHandleFlags.Type, SyntheticPssObjectType.Process);
        U16(typeName, 56, 8);
        U64(typeName, 64, 0x1111);
        var typeBuffers = new SyntheticPssNameBuffers(new Dictionary<ulong, byte[]>
        {
            [0x1111] = Encoding.Unicode.GetBytes("File"),
        });
        var typeNameResult = PssHandleEntryDecoder.Decode(typeName, typeBuffers);
        RequireIncomplete(typeNameResult, "type-name-validity-undocumented");
        Require(typeNameResult.TypeName is null && typeNameResult.RawTypeNameReference == new PssRawNameReference(8, 0x1111),
            "HAVE_TYPE was incorrectly treated as TypeName validity.");
        incomplete.Add("type-name-pointer-preserved-unvalidated");

        var nameSource = Encoding.Unicode.GetBytes("alpha");
        var nameBuffers = new SyntheticPssNameBuffers(new Dictionary<ulong, byte[]> { [0x2222] = nameSource });
        Array.Fill(nameSource, (byte)0x7f);
        var named = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.Name, SyntheticPssObjectType.Event);
        U16(named, 72, 10);
        U64(named, 80, 0x2222);
        var namedResult = PssHandleEntryDecoder.Decode(named, nameBuffers);
        Array.Fill(named, (byte)0xff);
        Require(namedResult.Status == PssHandleDecodeStatus.Decoded && namedResult.ObjectName?.Value == "alpha" &&
                namedResult.ObjectName.CopiedBytes.SequenceEqual(Encoding.Unicode.GetBytes("alpha")),
            "Decoded name did not survive caller buffer mutation.");
        positive.Add("object-name-bounded-copy-lifetime-independent");

        var oddName = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.Name, SyntheticPssObjectType.Event);
        U16(oddName, 72, 3);
        U64(oddName, 80, 0x3333);
        RequireInvalid(PssHandleEntryDecoder.Decode(oddName), "object-name-odd-byte-length");
        rejected.Add("odd-utf16-byte-length-rejected");

        var nullName = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.Name, SyntheticPssObjectType.Event);
        U16(nullName, 72, 4);
        RequireInvalid(PssHandleEntryDecoder.Decode(nullName), "object-name-null-pointer");
        rejected.Add("nonzero-name-null-pointer-rejected");

        var overName = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.Name, SyntheticPssObjectType.Event);
        U16(overName, 72, 6);
        U64(overName, 80, 0x4444);
        RequireInvalid(PssHandleEntryDecoder.Decode(overName, null, maximumNameByteLength: 4),
            "object-name-byte-length-exceeds-bound");
        rejected.Add("name-copy-bound-rejected");

        var invalidUtf16 = Entry(SyntheticPssHandleFlags.Type | SyntheticPssHandleFlags.Name, SyntheticPssObjectType.Event);
        U16(invalidUtf16, 72, 2);
        U64(invalidUtf16, 80, 0x5555);
        var invalidUtfBuffers = new SyntheticPssNameBuffers(new Dictionary<ulong, byte[]> { [0x5555] = [0x00, 0xd8] });
        RequireInvalid(PssHandleEntryDecoder.Decode(invalidUtf16, invalidUtfBuffers), "object-name-invalid-utf16");
        rejected.Add("invalid-utf16-rejected");

        RequireInvalid(PssHandleEntryDecoder.Decode(new byte[135]), "entry-size-not-136");
        rejected.Add("short-entry-rejected");

        var badUnion = Entry(SyntheticPssHandleFlags.TypeSpecific, SyntheticPssObjectType.Process);
        RequireInvalid(PssHandleEntryDecoder.Decode(badUnion), "type-specific-without-type-validity");
        rejected.Add("type-specific-without-type-validity-rejected");

        RequireThrows<ArgumentException>(() => new SyntheticPssNameBuffers(new Dictionary<ulong, byte[]>
        {
            [1] = new byte[SyntheticPssNameBuffers.MaximumBufferLength + 1],
        }), "oversized resolver buffer was accepted");
        rejected.Add("resolver-per-buffer-bound-rejected");

        var tooMany = Enumerable.Range(1, SyntheticPssNameBuffers.MaximumBufferCount + 1)
            .ToDictionary(value => (ulong)value, _ => Array.Empty<byte>());
        RequireThrows<ArgumentException>(() => new SyntheticPssNameBuffers(tooMany), "resolver count bound was accepted");
        rejected.Add("resolver-count-bound-rejected");

        var shared = new byte[SyntheticPssNameBuffers.MaximumBufferLength];
        var aggregate = Enumerable.Range(1, 129).ToDictionary(value => (ulong)value, _ => shared);
        RequireThrows<ArgumentException>(() => new SyntheticPssNameBuffers(aggregate), "resolver aggregate bound was accepted");
        rejected.Add("resolver-aggregate-bound-rejected");
    }

    private static void CheckLifecycle(List<string> positive, List<string> rejected)
    {
        var happy = NewModel();
        var sequence = 1L;
        Acquire(happy, ObserverResourceKind.Marker, 101, ref sequence);
        Acquire(happy, ObserverResourceKind.Snapshot, 202, ref sequence);
        Walk(happy, ObserverResourceLifecycle.Success, ref sequence);
        Walk(happy, ObserverResourceLifecycle.NoMoreItems, ref sequence);
        Release(happy, ObserverResourceKind.Marker, ObserverResourceLifecycle.Success, ref sequence);
        Release(happy, ObserverResourceKind.Snapshot, ObserverResourceLifecycle.Success, ref sequence);
        var happyGate = Gate(happy, ref sequence);
        Require(happyGate.CanTriggerSyntheticTransition && !happyGate.NativeCaptureProven &&
                !happyGate.NativeAuthorityProven && !happyGate.AdmissionGranted,
            "Happy synthetic lifecycle did not reach its proof-bounded gate.");
        positive.Add("synthetic-snapshot-marker-walk-release-gate");

        var secondGate = Gate(happy, ref sequence);
        Require(!secondGate.CanTriggerSyntheticTransition &&
                secondGate.Blockers.Contains("liveness-evidence-already-consumed", StringComparer.Ordinal),
            "Liveness gate was not one-use.");
        rejected.Add("liveness-gate-one-use");

        var invalidTerminal = AcquiredModel(out sequence);
        Require(!Walk(invalidTerminal, 1168, ref sequence).Accepted &&
                invalidTerminal.Snapshot().InvalidWalkTerminalObserved &&
                !Gate(invalidTerminal, ref sequence).CanTriggerSyntheticTransition,
            "Invalid walk terminal status passed.");
        rejected.Add("only-no-more-items-259-is-terminal");

        var wrongOwner = NewModel();
        var wrongIdentity = Event(1, Guid.NewGuid(), actor: Writer);
        Require(!wrongOwner.BeginAcquisition(ObserverResourceKind.Snapshot, wrongIdentity).Accepted,
            "Wrong free/acquire owner passed.");
        rejected.Add("wrong-resource-owner-rejected");

        var failedWithCoordinate = NewModel();
        sequence = 1;
        var failedOp = Guid.NewGuid();
        Require(failedWithCoordinate.BeginAcquisition(ObserverResourceKind.Marker, Event(sequence++, failedOp)).Accepted,
            "Failed-coordinate setup begin failed.");
        var failedReturn = failedWithCoordinate.CompleteAcquisition(
            ObserverResourceKind.Marker, Event(sequence++, failedOp), 5, 303);
        Require(!failedReturn.Accepted && failedReturn.State.MarkerState == ObserverResourceState.UnknownCustody &&
                failedReturn.State.UnknownReturns.Any(item => item.Coordinate == 303),
            "Error plus returned coordinate did not become unknown custody.");
        rejected.Add("acquisition-error-returned-coordinate-is-unknown-custody");

        var successInvalid = NewModel();
        sequence = 1;
        var invalidOp = Guid.NewGuid();
        successInvalid.BeginAcquisition(ObserverResourceKind.Marker, Event(sequence++, invalidOp));
        Require(!successInvalid.CompleteAcquisition(
                ObserverResourceKind.Marker, Event(sequence++, invalidOp), ObserverResourceLifecycle.Success, 0).Accepted,
            "Success with a zero descriptor passed.");
        rejected.Add("successful-acquisition-requires-valid-coordinate");

        var negativeCoordinate = NewModel();
        sequence = 1;
        var negativeOp = Guid.NewGuid();
        negativeCoordinate.BeginAcquisition(ObserverResourceKind.Marker, Event(sequence++, negativeOp));
        Require(!negativeCoordinate.CompleteAcquisition(
                ObserverResourceKind.Marker, Event(sequence++, negativeOp), ObserverResourceLifecycle.Success, -2).Accepted,
            "Negative pseudo coordinate passed as an owned descriptor.");
        rejected.Add("negative-pseudo-coordinate-rejected");

        var invalidKind = NewModel();
        Require(!invalidKind.BeginAcquisition((ObserverResourceKind)99, Event(1, Guid.NewGuid())).Accepted,
            "Undefined resource kind passed.");
        rejected.Add("undefined-resource-kind-rejected");

        var beforeTerminal = AcquiredModel(out sequence);
        Require(!Release(beforeTerminal, ObserverResourceKind.Marker, ObserverResourceLifecycle.Success, ref sequence).Accepted,
            "Release before terminal walk passed.");
        rejected.Add("release-before-terminal-rejected");

        var wrongOrder = AcquiredModel(out sequence);
        Walk(wrongOrder, ObserverResourceLifecycle.NoMoreItems, ref sequence);
        Require(!Release(wrongOrder, ObserverResourceKind.Snapshot, ObserverResourceLifecycle.Success, ref sequence).Accepted,
            "Snapshot release before marker passed.");
        rejected.Add("marker-must-release-before-snapshot");

        var releaseFailure = AcquiredModel(out sequence);
        Walk(releaseFailure, ObserverResourceLifecycle.NoMoreItems, ref sequence);
        Require(!Release(releaseFailure, ObserverResourceKind.Marker, 5, ref sequence).Accepted &&
                releaseFailure.Snapshot().MarkerState == ObserverResourceState.UnknownCustody,
            "Failed marker release did not preserve unknown custody.");
        Require(!Release(releaseFailure, ObserverResourceKind.Marker, ObserverResourceLifecycle.Success, ref sequence).Accepted,
            "Failed release was rewritten as retry success.");
        rejected.Add("release-failure-sticky-no-retry-success-fiction");

        var replay = NewModel();
        var replayOperation = Guid.NewGuid();
        var replayEvent = Event(1, replayOperation);
        replay.BeginAcquisition(ObserverResourceKind.Marker, replayEvent);
        Require(!replay.BeginAcquisition(ObserverResourceKind.Marker, replayEvent).Accepted,
            "Replayed event/operation passed.");
        rejected.Add("event-and-operation-replay-rejected");

        var timeout = NewModel();
        sequence = 1;
        var timeoutOp = Guid.NewGuid();
        timeout.BeginAcquisition(ObserverResourceKind.Marker, Event(sequence++, timeoutOp));
        Require(timeout.RecordTimeout(Event(sequence++, timeoutOp)).Accepted, "Timeout record failed.");
        var late = timeout.CompleteAcquisition(
            ObserverResourceKind.Marker, Event(sequence++, timeoutOp), ObserverResourceLifecycle.Success, 404);
        Require(late.Accepted && late.Reason == "late-cleanup-only" && late.State.Marker?.CleanupOnly == true,
            "Late completion was not retained as cleanup-only.");
        Require(Release(timeout, ObserverResourceKind.Marker, ObserverResourceLifecycle.Success, ref sequence).Accepted &&
                !Gate(timeout, ref sequence).CanTriggerSyntheticTransition,
            "Timeout was cleared by late cleanup.");
        rejected.Add("timeout-sticky-late-completion-cleanup-only");

        var walkTimeout = AcquiredModel(out sequence);
        var walkTimeoutOp = Guid.NewGuid();
        Require(walkTimeout.BeginWalk(Event(sequence++, walkTimeoutOp)).Accepted, "Walk timeout begin failed.");
        walkTimeout.RecordTimeout(Event(sequence++, walkTimeoutOp));
        var lateWalk = walkTimeout.CompleteWalk(Event(sequence++, walkTimeoutOp), ObserverResourceLifecycle.NoMoreItems);
        Require(lateWalk.Accepted && lateWalk.Reason == "late-terminal-cleanup-only",
            "Late walk completion was not cleanup-only.");
        Release(walkTimeout, ObserverResourceKind.Marker, ObserverResourceLifecycle.Success, ref sequence);
        Release(walkTimeout, ObserverResourceKind.Snapshot, ObserverResourceLifecycle.Success, ref sequence);
        Require(!Gate(walkTimeout, ref sequence).CanTriggerSyntheticTransition, "Timed out walk reached the gate.");
        rejected.Add("walk-timeout-sticky-after-late-terminal");

        var releaseTimeout = AcquiredModel(out sequence);
        Walk(releaseTimeout, ObserverResourceLifecycle.NoMoreItems, ref sequence);
        var releaseTimeoutOp = Guid.NewGuid();
        var timedReleaseDescriptor = releaseTimeout.Snapshot().Marker!;
        Require(releaseTimeout.BeginRelease(ObserverResourceKind.Marker, Event(sequence++, releaseTimeoutOp), timedReleaseDescriptor).Accepted,
            "Release timeout begin failed.");
        releaseTimeout.RecordTimeout(Event(sequence++, releaseTimeoutOp));
        var lateRelease = releaseTimeout.CompleteRelease(
            ObserverResourceKind.Marker, Event(sequence++, releaseTimeoutOp), timedReleaseDescriptor, ObserverResourceLifecycle.Success);
        Require(lateRelease.Accepted && lateRelease.Reason == "late-release-cleanup-only",
            "Late release completion was not cleanup-only.");
        Release(releaseTimeout, ObserverResourceKind.Snapshot, ObserverResourceLifecycle.Success, ref sequence);
        Require(!Gate(releaseTimeout, ref sequence).CanTriggerSyntheticTransition, "Timed out release reached the gate.");
        rejected.Add("release-timeout-sticky-after-late-completion");

        var eventBound = NewModel(maximumEvents: 1);
        sequence = 1;
        var boundedOp = Guid.NewGuid();
        eventBound.BeginAcquisition(ObserverResourceKind.Marker, Event(sequence++, boundedOp));
        var boundedLate = eventBound.CompleteAcquisition(
            ObserverResourceKind.Marker, Event(sequence++, boundedOp), ObserverResourceLifecycle.Success, 505);
        Require(!boundedLate.Accepted && boundedLate.State.MarkerState == ObserverResourceState.UnknownCustody &&
                boundedLate.State.UnknownReturns.Any(item => item.Coordinate == 505),
            "Event bound discarded a returned descriptor obligation.");
        rejected.Add("event-bound-preserves-late-return-obligation");

        var stale = NewModel();
        sequence = 1;
        Acquire(stale, ObserverResourceKind.Marker, 606, ref sequence);
        Acquire(stale, ObserverResourceKind.Snapshot, 707, ref sequence);
        Walk(stale, ObserverResourceLifecycle.NoMoreItems, ref sequence);
        Release(stale, ObserverResourceKind.Marker, ObserverResourceLifecycle.Success, ref sequence);
        Release(stale, ObserverResourceKind.Snapshot, ObserverResourceLifecycle.Success, ref sequence);
        var staleCollector = Binding.Collector with { CreationFileTime = Binding.Collector.CreationFileTime + 1 };
        Require(!Gate(stale, ref sequence, collector: staleCollector).CanTriggerSyntheticTransition,
            "Stale collector identity passed liveness revalidation.");
        rejected.Add("exact-pid-creation-liveness-revalidation-required");

        var notLive = NewModel();
        sequence = 1;
        Acquire(notLive, ObserverResourceKind.Marker, 616, ref sequence);
        Acquire(notLive, ObserverResourceKind.Snapshot, 717, ref sequence);
        Walk(notLive, ObserverResourceLifecycle.NoMoreItems, ref sequence);
        Release(notLive, ObserverResourceKind.Marker, ObserverResourceLifecycle.Success, ref sequence);
        Release(notLive, ObserverResourceKind.Snapshot, ObserverResourceLifecycle.Success, ref sequence);
        Require(!Gate(notLive, ref sequence, targetLive: false).CanTriggerSyntheticTransition,
            "False target liveness reached the gate.");
        rejected.Add("all-bound-roles-must-be-live-after-release");

        var invalidIdentityReturn = NewModel();
        sequence = 1;
        var returnOp = Guid.NewGuid();
        invalidIdentityReturn.BeginAcquisition(ObserverResourceKind.Marker, Event(sequence++, returnOp));
        var wrongIssuer = Event(sequence++, returnOp, issuer: Writer);
        var rawReturn = invalidIdentityReturn.CompleteAcquisition(
            ObserverResourceKind.Marker, wrongIssuer, ObserverResourceLifecycle.Success, 808);
        Require(!rawReturn.Accepted && rawReturn.State.UnknownReturns.Any(item => item.Coordinate == 808),
            "Identity rejection discarded an untrusted returned descriptor.");
        rejected.Add("identity-rejection-preserves-raw-return-obligation");

        var crossBinding = NewModel();
        var crossEvent = Event(1, Guid.NewGuid()) with { RunId = Guid.NewGuid() };
        Require(!crossBinding.BeginAcquisition(ObserverResourceKind.Marker, crossEvent).Accepted,
            "Cross-binding event passed.");
        rejected.Add("event-run-session-anchor-attempt-binding-required");

        var nullCompletion = NewModel();
        sequence = 1;
        var nullOp = Guid.NewGuid();
        nullCompletion.BeginAcquisition(ObserverResourceKind.Marker, Event(sequence++, nullOp));
        var nullReturned = nullCompletion.CompleteAcquisition(
            ObserverResourceKind.Marker, null!, ObserverResourceLifecycle.Success, 909);
        Require(!nullReturned.Accepted && nullReturned.State.UnknownReturns.Any(item => item.Coordinate == 909),
            "Null completion identity discarded returned custody.");
        rejected.Add("null-completion-preserves-returned-custody");

        var nullGate = NewModel();
        Require(!nullGate.EvaluateSyntheticTransition(null!).CanTriggerSyntheticTransition,
            "Null liveness evidence threw or passed.");
        rejected.Add("null-liveness-evidence-fails-closed");

        var consumedBound = NewModel(maximumEvents: 2);
        sequence = 1;
        var activeOp = Guid.NewGuid();
        consumedBound.BeginAcquisition(ObserverResourceKind.Marker, Event(sequence++, activeOp));
        var wrongOp = Guid.NewGuid();
        consumedBound.CompleteAcquisition(
            ObserverResourceKind.Marker, Event(sequence++, wrongOp), ObserverResourceLifecycle.Success, 1001);
        var afterBound = consumedBound.CompleteAcquisition(
            ObserverResourceKind.Marker, Event(sequence++, activeOp), ObserverResourceLifecycle.Success, 1002);
        Require(!afterBound.Accepted && afterBound.Reason == "event-bound-exceeded" &&
                afterBound.State.UnknownReturns.Any(item => item.Coordinate == 1002),
            "Accepted-but-unrecorded identities bypassed the bound or lost custody.");
        rejected.Add("consumed-event-bound-covers-invalid-completions");

        RequireThrows<ArgumentOutOfRangeException>(() => NewModel(ObserverResourceLifecycle.MaximumOperations + 1),
            "Operation bound constructor accepted an oversized bound.");
        rejected.Add("operation-bound-enforced");

        RequireThrows<ArgumentException>(() => new ObserverResourceLifecycle(
                Binding with { CommandIssuer = Owner }),
            "Non-controller command issuer was accepted.");
        rejected.Add("trusted-binding-command-issuer-role-fixed");

        RequireThrows<ArgumentException>(() => new ObserverResourceLifecycle(
                Binding with { Target = Writer with { ProcessId = Controller.ProcessId } }),
            "Different roles aliased one PID.");
        rejected.Add("trusted-binding-cross-role-pid-alias-rejected");

        var wrongRelease = AcquiredModel(out sequence);
        Walk(wrongRelease, ObserverResourceLifecycle.NoMoreItems, ref sequence);
        var actualMarker = wrongRelease.Snapshot().Marker!;
        var wrongMarker = actualMarker with { Coordinate = actualMarker.Coordinate + 1 };
        Require(!wrongRelease.BeginRelease(ObserverResourceKind.Marker, Event(sequence++, Guid.NewGuid()), wrongMarker).Accepted &&
                wrongRelease.Snapshot().MarkerState == ObserverResourceState.Acquired,
            "Release accepted a different descriptor coordinate.");
        rejected.Add("release-begin-exact-coordinate-required");

        var staleRelease = AcquiredModel(out sequence);
        Walk(staleRelease, ObserverResourceLifecycle.NoMoreItems, ref sequence);
        var releaseOp = Guid.NewGuid();
        var retained = staleRelease.Snapshot().Marker!;
        Require(staleRelease.BeginRelease(ObserverResourceKind.Marker, Event(sequence++, releaseOp), retained).Accepted,
            "Could not start exact descriptor release.");
        var staleTuple = retained with { AcquisitionOperationId = Guid.NewGuid() };
        var staleReleaseResult = staleRelease.CompleteRelease(ObserverResourceKind.Marker,
            Event(sequence++, releaseOp), staleTuple, 0);
        Require(!staleReleaseResult.Accepted && staleReleaseResult.State.MarkerState == ObserverResourceState.UnknownCustody &&
                staleReleaseResult.State.Events.Last().AcquisitionOperationId == staleTuple.AcquisitionOperationId &&
                !Gate(staleRelease, ref sequence).CanTriggerSyntheticTransition,
            "Successful free of a stale acquisition tuple established release.");
        rejected.Add("release-return-exact-acquisition-tuple-required");

        foreach (var status in new uint[] { 5, 6 })
        {
            var rawStatusModel = NewModel();
            sequence = 1;
            var rawStatusOp = Guid.NewGuid();
            rawStatusModel.BeginAcquisition(ObserverResourceKind.Marker, Event(sequence++, rawStatusOp));
            var rawStatusResult = rawStatusModel.CompleteAcquisition(ObserverResourceKind.Marker,
                Event(sequence++, rawStatusOp), status, 1122);
            Require(rawStatusResult.State.Events.Last() is
            { Coordinate: 1122, ResourceKind: ObserverResourceKind.Marker } recorded &&
                    recorded.ApiStatus == status && recorded.AcquisitionOperationId == rawStatusOp &&
                    rawStatusResult.State.UnknownReturns.Single().ApiStatus == status,
                "Raw native-style API status or acquisition tuple was lost.");
        }
        positive.Add("raw-api-error-statuses-and-return-tuples-distinct");

        foreach (var failedRole in Enum.GetValues<InventoryRole>())
        {
            var rolesModel = AcquiredModel(out sequence);
            Walk(rolesModel, ObserverResourceLifecycle.NoMoreItems, ref sequence);
            Release(rolesModel, ObserverResourceKind.Marker, 0, ref sequence);
            Release(rolesModel, ObserverResourceKind.Snapshot, 0, ref sequence);
            var gateEvent = Event(sequence++, Guid.NewGuid());
            var evidence = new ObserverLivenessEvidence(gateEvent, Owner, Controller, Writer, true, true, true,
                new(Controller, failedRole != InventoryRole.Controller, gateEvent.MonotonicTicks),
                new(Owner, failedRole != InventoryRole.Owner, gateEvent.MonotonicTicks),
                new(Writer, failedRole != InventoryRole.Writer, gateEvent.MonotonicTicks));
            Require(!rolesModel.EvaluateSyntheticTransition(evidence).CanTriggerSyntheticTransition,
                "A missing live role passed the global transition gate.");
        }
        rejected.Add("complete-p-o-w-live-role-evidence-required");

        var staleRoleModel = AcquiredModel(out sequence);
        Walk(staleRoleModel, ObserverResourceLifecycle.NoMoreItems, ref sequence);
        Release(staleRoleModel, ObserverResourceKind.Marker, 0, ref sequence);
        Release(staleRoleModel, ObserverResourceKind.Snapshot, 0, ref sequence);
        var staleRoleEvent = Event(sequence++, Guid.NewGuid());
        var staleRoleEvidence = new ObserverLivenessEvidence(staleRoleEvent, Owner, Controller, Writer, true, true, true,
            new(Controller, true, staleRoleEvent.MonotonicTicks),
            new(Owner, true, 1),
            new(Writer, true, staleRoleEvent.MonotonicTicks));
        Require(!staleRoleModel.EvaluateSyntheticTransition(staleRoleEvidence).CanTriggerSyntheticTransition,
            "A pre-release role liveness observation passed.");
        rejected.Add("each-role-liveness-fresh-after-final-release");

        var selfBinding = Binding with
        {
            Collector = Controller,
            ResourceOwner = Controller,
            CommandIssuer = Controller,
            Target = Controller,
        };
        _ = new ObserverResourceLifecycle(selfBinding);
        positive.Add("trusted-binding-exact-self-collection-allowed");
    }

    private static ObserverResourceLifecycle AcquiredModel(out long sequence)
    {
        var model = NewModel();
        sequence = 1;
        Acquire(model, ObserverResourceKind.Marker, 11, ref sequence);
        Acquire(model, ObserverResourceKind.Snapshot, 22, ref sequence);
        return model;
    }

    private static ObserverTransitionResult Acquire(
        ObserverResourceLifecycle model,
        ObserverResourceKind kind,
        long coordinate,
        ref long sequence)
    {
        var operation = Guid.NewGuid();
        Require(model.BeginAcquisition(kind, Event(sequence++, operation)).Accepted, $"Could not begin {kind} acquisition.");
        var result = model.CompleteAcquisition(
            kind, Event(sequence++, operation), ObserverResourceLifecycle.Success, coordinate);
        Require(result.Accepted, $"Could not complete {kind} acquisition: {result.Reason}");
        return result;
    }

    private static ObserverTransitionResult Walk(ObserverResourceLifecycle model, uint status, ref long sequence)
    {
        var operation = Guid.NewGuid();
        Require(model.BeginWalk(Event(sequence++, operation)).Accepted, "Could not begin walk.");
        return model.CompleteWalk(Event(sequence++, operation), status);
    }

    private static ObserverTransitionResult Release(
        ObserverResourceLifecycle model,
        ObserverResourceKind kind,
        uint status,
        ref long sequence)
    {
        var operation = Guid.NewGuid();
        var state = model.Snapshot();
        var descriptor = (kind == ObserverResourceKind.Marker ? state.Marker : state.Snapshot) ??
            new ObserverDescriptor(kind, 0, state.Binding.ResourceOwner, Guid.Empty, false);
        var begin = model.BeginRelease(kind, Event(sequence++, operation), descriptor);
        if (!begin.Accepted)
        {
            return begin;
        }

        return model.CompleteRelease(kind, Event(sequence++, operation), descriptor, status);
    }

    private static ObserverGateResult Gate(
        ObserverResourceLifecycle model,
        ref long sequence,
        InventoryProcessIdentity? collector = null,
        InventoryProcessIdentity? issuer = null,
        InventoryProcessIdentity? target = null,
        bool collectorLive = true,
        bool issuerLive = true,
        bool targetLive = true)
    {
        var operation = Guid.NewGuid();
        var gateEvent = Event(sequence++, operation);
        return model.EvaluateSyntheticTransition(new ObserverLivenessEvidence(
            gateEvent,
            collector ?? Binding.Collector,
            issuer ?? Binding.CommandIssuer,
            target ?? Binding.Target,
            collectorLive,
            issuerLive,
            targetLive,
            new ObserverRoleLiveness(Controller, true, gateEvent.MonotonicTicks),
            new ObserverRoleLiveness(Owner, true, gateEvent.MonotonicTicks),
            new ObserverRoleLiveness(Writer, true, gateEvent.MonotonicTicks)));
    }

    private static ObserverResourceLifecycle NewModel(int maximumEvents = ObserverResourceLifecycle.MaximumOperations) =>
        new(Binding, maximumEvents);

    private static ObserverEventIdentity Event(
        long sequence,
        Guid operation,
        InventoryProcessIdentity? actor = null,
        InventoryProcessIdentity? issuer = null) =>
        new(
            Binding.RunId,
            Binding.SessionId,
            Binding.Anchor,
            Binding.Attempt,
            sequence,
            sequence * 10,
            Guid.NewGuid(),
            operation,
            actor ?? Binding.ResourceOwner,
            issuer ?? Binding.CommandIssuer);

    private static byte[] Entry(SyntheticPssHandleFlags flags, SyntheticPssObjectType type)
    {
        var bytes = new byte[PssHandleEntryDecoder.EntrySize];
        U32(bytes, 8, (uint)flags);
        U32(bytes, 12, (uint)type);
        return bytes;
    }

    private static void RequireIncomplete(PssHandleDecodeResult result, string reason) =>
        Require(result.Status == PssHandleDecodeStatus.Incomplete && result.IncompleteReasons.Contains(reason, StringComparer.Ordinal),
            $"Expected incomplete decoder reason '{reason}'.");

    private static void RequireInvalid(PssHandleDecodeResult result, string reason) =>
        Require(result.Status == PssHandleDecodeStatus.Invalid && result.InvalidReasons.Contains(reason, StringComparer.Ordinal),
            $"Expected invalid decoder reason '{reason}'.");

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

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void U16(byte[] value, int offset, ushort field) =>
        BinaryPrimitives.WriteUInt16LittleEndian(value.AsSpan(offset, 2), field);

    private static void U32(byte[] value, int offset, uint field) =>
        BinaryPrimitives.WriteUInt32LittleEndian(value.AsSpan(offset, 4), field);

    private static void I32(byte[] value, int offset, int field) =>
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(offset, 4), field);

    private static void U64(byte[] value, int offset, ulong field) =>
        BinaryPrimitives.WriteUInt64LittleEndian(value.AsSpan(offset, 8), field);

    private static readonly InventoryProcessIdentity Controller = new(InventoryRole.Controller, 4101, 10101);
    private static readonly InventoryProcessIdentity Owner = new(InventoryRole.Owner, 4102, 10102);
    private static readonly InventoryProcessIdentity Writer = new(InventoryRole.Writer, 4103, 10103);

    private static readonly ObserverRunBinding Binding = new(
        Guid.Parse("c0dd3f67-9b74-41d5-bbd3-ed8eb817e150"),
        Guid.Parse("50bedbd6-7fc6-4dc5-b151-5aa6f556a88f"),
        InventoryAnchor.B2,
        1,
        Owner,
        Owner,
        Controller,
        Writer,
        Controller,
        Owner,
        Writer);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
