using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static class DiagnosticEnvelopeCodecChecks
{
    internal static int Run()
    {
        var positive = new List<string>();
        var rejected = new List<string>();
        var incomplete = new List<string>();
        var fixtureEvidence = new List<DiagnosticFixtureEvidence>();
        try
        {
            CheckExactFramesAndAtomicPublication(positive, rejected, fixtureEvidence);
            CheckStrictDecode(rejected);
            CheckImmutableContextAndReplay(positive, rejected);
            CheckPinnedBindingsAndJourneys(positive, rejected, incomplete);
            CheckTranscriptPartitions(positive, rejected, incomplete, fixtureEvidence);
            CheckUnknownAndPending(positive, rejected, incomplete);

            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "watchdog.ow-diagnostic-envelope-codec-checks.v1",
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
                frozenLimits = LimitsReceipt(),
                encodedFixtureEvidence = fixtureEvidence,
            }, JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema = "watchdog.ow-diagnostic-envelope-codec-checks.v1",
                succeeded = false,
                syntheticOnly = true,
                nativeCallsPerformed = false,
                nativeCapturePerformed = false,
                peerAuthenticated = false,
                nativeAuthorityProven = false,
                nativeDurabilityProven = false,
                productionAdmissionGranted = false,
                releaseApproved = false,
                positiveChecks = positive.Count,
                rejectionChecks = rejected.Count,
                incompleteChecks = incomplete.Count,
                positiveCases = positive,
                rejectionCases = rejected,
                incompleteCases = incomplete,
                frozenLimits = LimitsReceipt(),
                encodedFixtureEvidence = fixtureEvidence,
                failure = $"{exception.GetType().Name}: {exception.Message}",
            }, JsonOptions));
            return 1;
        }
    }

    private static void CheckExactFramesAndAtomicPublication(
        List<string> positive,
        List<string> rejected,
        List<DiagnosticFixtureEvidence> fixtureEvidence)
    {
        var baseline = AtSequence(RowEnvelope(""), 3);
        var baselineBytes = EncodeOrdinary(baseline);
        var padding = new string('a', DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes - baselineBytes.Length);
        var exact = AtSequence(RowEnvelope(padding), 3);
        var exactBytes = EncodeOrdinary(exact);
        Require(exactBytes.Length == DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes &&
                BinaryPrimitives.ReadUInt32LittleEndian(exactBytes) == DiagnosticEnvelopeCodec.MaximumPayloadBytes &&
                DiagnosticEnvelopeCodec.TryDecode(exactBytes, Expected, RowDecodeTranscript(),
                    out _, out _, out _),
            "Exact correlated complete-frame boundary did not encode and decode at 65,536 bytes.");
        positive.Add("exact-65536-frame-and-65532-payload-encodes-and-decodes-correlated-row");
        fixtureEvidence.Add(Evidence("maximum-accepted", DiagnosticEnvelopeKind.ValueRowBatch, exactBytes, true));

        var rejectedFrame = new byte[DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes + 1];
        exactBytes.CopyTo(rejectedFrame, 0);
        rejectedFrame[^1] = (byte)'b';
        BinaryPrimitives.WriteUInt32LittleEndian(rejectedFrame,
            (uint)(DiagnosticEnvelopeCodec.MaximumPayloadBytes + 1));
        fixtureEvidence.Add(Evidence("maximum-plus-one-rejected", DiagnosticEnvelopeKind.ValueRowBatch,
            rejectedFrame, false));
        Require(!DiagnosticEnvelopeCodec.TryDecode(rejectedFrame, Expected, new DiagnosticTranscript(),
                out _, out _, out var rejectedFrameObservation) &&
                rejectedFrameObservation.PayloadAllocationRequestedBytes == 0,
            "65,537-byte rejection fixture requested payload allocation or decoded.");

        var destination = Enumerable.Repeat((byte)0xA5, DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes + 16).ToArray();
        var before = destination.ToArray();
        var tooLarge = RowEnvelope(padding + "b");
        var tooLargeTranscript = new DiagnosticTranscript();
        Require(tooLargeTranscript.TryReserveOrdinary(tooLarge, out var tooLargeReservation, out _),
            "Could not reserve oversized-frame fixture.");
        Require(!DiagnosticEnvelopeCodec.TryEncode(
                tooLarge, tooLargeReservation!, destination, out var written, out _) &&
                written == 0 && destination.SequenceEqual(before),
            "One-byte excess partially published or changed caller output.");
        rejected.Add("one-byte-frame-excess-fails-before-publication");

        var ascii = EncodeOrdinary(RowEnvelope("ee"));
        var multibyteEnvelope = RowEnvelope("éé");
        var multibyteTranscript = new DiagnosticTranscript();
        Require(multibyteTranscript.TryReserveOrdinary(multibyteEnvelope, out var multibyteReservation, out _),
            "Could not reserve multibyte fixture.");
        var multibyte = Encode(multibyteEnvelope, multibyteReservation!);
        Require(multibyte.Length == ascii.Length + 10 &&
                multibyteTranscript.Snapshot().ActualEncodedBytes == multibyte.Length &&
                BinaryPrimitives.ReadUInt32LittleEndian(multibyte) == multibyte.Length - 4,
            "Escaped Unicode wire bytes or prefix were not charged exactly.");
        var rawMultibyte = Frame(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(ascii.AsSpan(4)).Replace("\"value\":\"ee\"", "\"value\":\"éé\"")
                .Replace("\"sequence\":1", "\"sequence\":3")));
        var rawMultibyteTranscript = RowDecodeTranscript();
        var beforeRawMultibyteBytes = rawMultibyteTranscript.Snapshot().ActualEncodedBytes;
        Require(rawMultibyte.Length == ascii.Length + 2 &&
                DiagnosticEnvelopeCodec.TryDecode(rawMultibyte, Expected, rawMultibyteTranscript,
                    out _, out _, out _) && rawMultibyteTranscript.Snapshot().ActualEncodedBytes -
                    beforeRawMultibyteBytes == rawMultibyte.Length,
            "Literal multibyte UTF-8 frame was not charged exactly.");
        positive.Add("multibyte-utf8-prefix-and-budget-accounting");

        var supplementary = EncodeOrdinary(AtSequence(RowEnvelope("😀"), 3));
        Require(DiagnosticEnvelopeCodec.TryDecode(supplementary, Expected, RowDecodeTranscript(),
                out _, out _, out _),
            "Valid paired supplementary Unicode scalar was rejected.");
        positive.Add("paired-supplementary-unicode-scalar-accepted");

        var prefixOnly = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(prefixOnly, uint.MaxValue);
        var prefixTranscript = new DiagnosticTranscript();
        Require(!DiagnosticEnvelopeCodec.TryDecode(
                prefixOnly, Expected, prefixTranscript, out _, out _, out var observation) &&
                observation.PayloadAllocationRequestedBytes == 0,
            "Oversized prefix requested an oversized payload allocation.");
        rejected.Add("oversized-prefix-rejected-before-payload-request");
    }

    private static void CheckStrictDecode(List<string> rejected)
    {
        var valid = EncodeOrdinary(CommandEnvelope(Guid.NewGuid(), SupervisionCallKind.AcquireMarker));
        Require(DiagnosticEnvelopeCodec.TryDecode(
                valid, Expected, new DiagnosticTranscript(), out var decoded, out _, out _) &&
                decoded?.Kind == DiagnosticEnvelopeKind.Command,
            "Valid command frame did not decode.");

        using (var golden = JsonDocument.Parse(valid.AsMemory(4)))
        {
            var rootNames = golden.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet();
            Require(rootNames.SetEquals(["schema", "version", "kind", "contextDigest", "epoch", "sequence",
                    "eventId", "operationId", "body"]) && !rootNames.Contains("bodyObject"),
                "Encoded envelope did not contain the exact frozen common header/body keys.");
        }

        RejectJson("{\"schema\":\"watchdog.ow-diagnostic-envelope\",\"\\u0073chema\":\"watchdog.ow-diagnostic-envelope\"}",
            "escaped-top-level-duplicate-property", rejected);
        RejectJson(ValidCommandJson().Replace("\"issuer\":", "\"issuer\":\"duplicate\",\"\\u0069ssuer\":"),
            "escaped-nested-duplicate-property", rejected);
        RejectJson(ValidCommandJson().Replace("\"issuer\":", "\"unknownNested\":1,\"issuer\":"),
            "unknown-nested-property", rejected);
        RejectJson(ValidCommandJson().Replace("\"issuer\":", "\"Issuer\":"),
            "case-alias-property", rejected);
        RejectJson(ValidCommandJson().Replace("\"nativeCallsPerformed\":false,", string.Empty),
            "missing-proof-boolean", rejected);
        RejectJson(Encoding.UTF8.GetString(EncodeOrdinary(FirstReturnEnvelope(Guid.NewGuid(), 0, [])).AsSpan(4))
                .Replace("\"apiStatus\":0,", string.Empty),
            "missing-api-status", rejected);
        RejectJson(ValidCommandJson().Replace("\"version\":1", "\"version\":2"),
            "unknown-version", rejected);
        RejectJson(ValidCommandJson().Replace("\"kind\":\"Command\"", "\"kind\":\"NotARecord\""),
            "unknown-record-kind", rejected);
        RejectJson(ValidCommandJson().Replace("\"epoch\":1", "\"epoch\":9223372036854775808"),
            "integer-width-overflow", rejected);
        RejectJson(ValidCommandJson() + " ", "trailing-frame-data", rejected, includeTrailingInPrefix: true);
        RejectJson(ValidCommandJson()[..^1], "truncated-json", rejected);
        RejectJson("\uFEFF" + ValidCommandJson(), "utf8-bom", rejected);
        RejectJson(ValidCommandJson().Replace("\"clockDomain\":\"p-clock\"", "\"clockDomain\":\"\\uD800\""),
            "unpaired-surrogate", rejected);

        var invalidUtf8 = Frame(Encoding.UTF8.GetBytes(ValidCommandJson()));
        invalidUtf8[^2] = 0xC0;
        Require(!DiagnosticEnvelopeCodec.TryDecode(
                invalidUtf8, Expected, new DiagnosticTranscript(), out _, out _, out _),
            "Invalid UTF-8 passed.");
        rejected.Add("invalid-utf8-rejected");

        var wrongContext = Expected with { ContextDigest = new string('f', 64) };
        Require(!DiagnosticEnvelopeCodec.TryDecode(
                valid, wrongContext, new DiagnosticTranscript(), out _, out _, out _),
            "Wrong independently expected context passed.");
        var wrongActorJson = ValidCommandJson().Replace("\"processId\":5102", "\"processId\":9999");
        RejectJson(wrongActorJson, "wrong-actor-target-source-or-epoch", rejected);

        var positiveWrongOwnerJson = Encoding.UTF8.GetString(
                EncodeOrdinary(FirstReturnEnvelope(FixtureGuid(1_700), 5, [1_234])).AsSpan(4))
            .Replace("\"processId\":5102", "\"processId\":9999");
        var positiveWrongOwnerTranscript = new DiagnosticTranscript();
        Require(!DiagnosticEnvelopeCodec.TryDecode(Frame(Encoding.UTF8.GetBytes(positiveWrongOwnerJson)), Expected,
                positiveWrongOwnerTranscript, out _, out _, out _) &&
                positiveWrongOwnerTranscript.Snapshot().UnknownObligations.Any(item => item.Coordinate == 1_234),
            "Rejected positive raw return lost its bounded unknown custody obligation.");
        rejected.Add("malformed-positive-first-return-retains-unknown-custody");

        var trueProof = ValidCommandJson().Replace("\"nativeAuthorityProven\":false", "\"nativeAuthorityProven\":true");
        RejectJson(trueProof, "authority-promoting-proof-input", rejected);

        var maxFinalJson = Encoding.UTF8.GetString(EncodeOrdinary(MaximumFinalEnvelope()).AsSpan(4));
        RejectJson(maxFinalJson.Replace($"\"originalMonotonicTicks\":{long.MaxValue}",
                "\"originalMonotonicTicks\":-1"),
            "malformed-nested-final-pending", rejected);
        RejectJson(maxFinalJson.Replace("\"jobObservation\":\"NotObserved\"",
                "\"jobObservation\":\"BogusObservation\""),
            "malformed-nested-final-termination", rejected);

        var replayTranscript = new DiagnosticTranscript();
        Require(DiagnosticEnvelopeCodec.TryDecode(valid, Expected, replayTranscript, out _, out _, out _) &&
                !DiagnosticEnvelopeCodec.TryDecode(valid, Expected, replayTranscript, out _, out _, out _),
            "Exact decoded event/sequence/operation replay succeeded.");
        rejected.Add("decoded-event-sequence-operation-replay");
    }

    private static void CheckImmutableContextAndReplay(List<string> positive, List<string> rejected)
    {
        var mutableSlots = Slots().ToList();
        var prepared = PreparedEnvelope(mutableSlots);
        var frozenCoordinate = ((DiagnosticPreparedContext)prepared.Body).ExpectedCustody[0].Coordinate;
        mutableSlots[0] = mutableSlots[0] with { Coordinate = 999999 };
        Require(((DiagnosticPreparedContext)prepared.Body).ExpectedCustody[0].Coordinate == frozenCoordinate,
            "Prepared context retained mutable nested slots.");
        positive.Add("prepared-context-and-nested-slots-deep-copied");

        var mutableExpectedSlots = Slots().ToList();
        var frozenDecodeContext = (Expected with { ExpectedCustody = mutableExpectedSlots }).Freeze();
        var frozenExpectedCoordinate = frozenDecodeContext.ExpectedCustody![0].Coordinate;
        mutableExpectedSlots[0] = mutableExpectedSlots[0] with { Coordinate = 888888 };
        Require(frozenDecodeContext.ExpectedCustody[0].Coordinate == frozenExpectedCoordinate,
            "Frozen independent decode context adopted a later caller slot mutation.");
        positive.Add("independent-decode-context-explicitly-frozen");

        var mutableRows = new List<DiagnosticValueRow> { new(0, "before") };
        var bypassEnvelope = new DiagnosticEnvelope(
            Header(DiagnosticEnvelopeKind.ValueRowBatch, FixtureGuid(10_050)),
            new DiagnosticValueRowBatch(Owner, 0, false, mutableRows, DiagnosticProofClaims.False));
        var bypassTranscript = new DiagnosticTranscript();
        Require(bypassTranscript.TryReserveOrdinary(bypassEnvelope, out var bypassReservation, out _),
            "Could not reserve mutable bypass fixture.");
        mutableRows[0] = new DiagnosticValueRow(0, "after");
        var bypassDestination = Enumerable.Repeat((byte)0xC3,
            DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes).ToArray();
        var bypassBefore = bypassDestination.ToArray();
        Require(!DiagnosticEnvelopeCodec.TryEncode(bypassEnvelope, bypassReservation!, bypassDestination,
                out var bypassWritten, out _) && bypassWritten == 0 && bypassDestination.SequenceEqual(bypassBefore),
            "TryEncode serialized mutable caller state after reservation or partially published it.");
        rejected.Add("tryencode-snapshots-once-and-rejects-postreservation-mutation");

        var operation = Guid.NewGuid();
        var command = CommandEnvelope(operation, SupervisionCallKind.AcquireMarker);
        var transcript = new DiagnosticTranscript();
        Require(transcript.TryCommitCommand(command, null, out var reservation, out _),
            "Could not commit ordinary command.");
        var destination = new byte[DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes];
        Require(DiagnosticEnvelopeCodec.TryEncode(command, reservation!, destination, out _, out _),
            "Committed command did not encode.");
        Require(!transcript.TryCommitCommand(command, null, out _, out _),
            "One-use operation token replayed.");
        rejected.Add("one-use-command-operation-token-never-refunded-or-replayed");

        var freeTranscript = new DiagnosticTranscript();
        var descriptor = new ObserverDescriptor(ObserverResourceKind.Marker, 777, Owner, Guid.NewGuid(), true);
        var freeCommand = CommandEnvelope(Guid.NewGuid(), SupervisionCallKind.ReleaseMarker, descriptor);
        Require(freeTranscript.TryCommitFreeCommand(freeCommand, descriptor, out var freeReservation, out _),
            "Could not reserve substitution fixture free command.");
        var substituted = CommandEnvelope(freeCommand.Header.OperationId, SupervisionCallKind.AcquireMarker) with
        {
            Header = freeCommand.Header,
        };
        var unchanged = Enumerable.Repeat((byte)0x5A, DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes).ToArray();
        var unchangedBefore = unchanged.ToArray();
        Require(!DiagnosticEnvelopeCodec.TryEncode(substituted, freeReservation!, unchanged, out var substituteWritten,
                out _) && substituteWritten == 0 && unchanged.SequenceEqual(unchangedBefore) &&
                freeTranscript.Snapshot().ReleaseEvents == 0,
            "Altered command spent a protected free reservation.");
        rejected.Add("reservation-binds-full-immutable-envelope");

        var first = FirstReturnEnvelope(operation, 0, [101]);
        var slot = new DiagnosticFirstReturnSlot(Expected.ContextDigest, 1, operation,
            SupervisionCallKind.AcquireMarker, Owner);
        Require(slot.TryStore(first, transcript, out var retained, out var disposition) &&
                disposition == DiagnosticFirstReturnDisposition.Stored && retained.PositiveCoordinates.SequenceEqual([101L]),
            "First raw return was not stored.");
        var conflictingBody = (DiagnosticFirstRawReturn)FirstReturnEnvelope(operation, 5, [202]).Body;
        var conflicting = DiagnosticEnvelope.Create(first.Header with { EventId = Guid.NewGuid(), Sequence = 2 },
            conflictingBody);
        Require(!slot.TryStore(conflicting, transcript, out var retainedAgain, out disposition) &&
                disposition == DiagnosticFirstReturnDisposition.Conflict &&
                retainedAgain.PositiveCoordinates.SequenceEqual([101L]) &&
                transcript.Snapshot().UnknownObligations.Any(item => item.Coordinate == 202),
            "Conflicting duplicate overwrote first return or lost positive custody.");
        rejected.Add("first-return-first-wins-conflict-preserves-unknown");
    }

    private static void CheckPinnedBindingsAndJourneys(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var completeExpected = CompleteExpected;
        var validAck = CustodyAckEnvelope(CustodyDigest, Slots());
        Require(DiagnosticEnvelopeCodec.TryDecode(EncodeOrdinary(validAck), completeExpected,
                new DiagnosticTranscript(completeExpected), out _, out _, out _),
            "Exact P custody acknowledgment did not decode.");
        var wrongDigestAck = CustodyAckEnvelope(new string('9', 64), Slots());
        Require(!DiagnosticEnvelopeCodec.TryDecode(EncodeOrdinary(wrongDigestAck), completeExpected,
                new DiagnosticTranscript(completeExpected), out _, out _, out _),
            "Alternate custody digest passed ACK validation.");
        var wrongSlots = Slots().Select(slot => slot with { Coordinate = slot.Coordinate + 1 }).ToArray();
        Require(!DiagnosticEnvelopeCodec.TryDecode(EncodeOrdinary(CustodyAckEnvelope(CustodyDigest, wrongSlots)),
                completeExpected, new DiagnosticTranscript(completeExpected), out _, out _, out _),
            "Alternate custody coordinate passed ACK validation.");
        positive.Add("exact-p-custody-digest-and-slots-acknowledged");
        rejected.Add("alternate-ack-digest-or-coordinate-rejected");

        var prepared = PreparedEnvelope(Slots());
        Require(Decode(prepared, completeExpected, new DiagnosticTranscript(completeExpected)),
            "Exact prepared clock/policy context did not decode.");
        var changedPolicyBody = ((DiagnosticPreparedContext)prepared.Body) with
        { Policy = SupervisionPolicy.FromFrequency(2_000) };
        var changedPolicy = DiagnosticEnvelope.Create(prepared.Header, changedPolicyBody);
        Require(!Decode(changedPolicy, completeExpected, new DiagnosticTranscript(completeExpected)),
            "Prepared policy/frequency drift passed the same context digest.");
        var repeatedPreparedTranscript = new DiagnosticTranscript(completeExpected);
        Require(Decode(prepared, completeExpected, repeatedPreparedTranscript) &&
                !Decode(Reidentify(AtSequence(prepared, 2), FixtureGuid(12_001)), completeExpected,
                    repeatedPreparedTranscript),
            "Repeated PreparedContext mutation passed one-use transcript state.");
        var repeatedAckTranscript = new DiagnosticTranscript(completeExpected);
        Require(Decode(validAck, completeExpected, repeatedAckTranscript) &&
                !Decode(Reidentify(AtSequence(validAck, 2), FixtureGuid(12_002)), completeExpected,
                    repeatedAckTranscript),
            "Repeated custody ACK mutation passed one-use transcript state.");
        var frequencyOperation = FixtureGuid(12_003);
        var frequencyTranscript = new DiagnosticTranscript(completeExpected);
        Require(Decode(AtSequence(CommandEnvelope(frequencyOperation, SupervisionCallKind.AcquireMarker), 1),
                    completeExpected, frequencyTranscript) &&
                !Decode(AtSequence(FirstReturnEnvelope(frequencyOperation, 0, [44],
                    ownerClockFrequency: 2_000), 2), completeExpected, frequencyTranscript),
            "Owner raw-return clock-frequency drift passed frozen expectations.");
        rejected.Add("prepared-policy-ownerfrequency-and-oneuse-prepack-drift-rejected");

        var pinnedTranscript = new DiagnosticTranscript(completeExpected);
        var pinnedCommand = CommandEnvelope(FixtureGuid(10_100), SupervisionCallKind.AcquireMarker);
        Require(DiagnosticEnvelopeCodec.TryDecode(EncodeOrdinary(pinnedCommand), completeExpected, pinnedTranscript,
                out _, out _, out _),
            "Pinned-context command did not decode.");
        var driftedExpected = (completeExpected with { SourceDigest = new string('8', 64) }).Freeze();
        var laterFinal = AtSequence(FinalEnvelope("context-drift"), 2);
        Require(!DiagnosticEnvelopeCodec.TryDecode(EncodeOrdinary(laterFinal), driftedExpected, pinnedTranscript,
                out _, out _, out _),
            "Caller with-spoof changed transcript-private expected context.");
        var callerMutableSlots = Slots().ToList();
        var callerMutableContext = completeExpected with { ExpectedCustody = callerMutableSlots };
        var constructorPinnedTranscript = new DiagnosticTranscript(callerMutableContext);
        callerMutableSlots[0] = callerMutableSlots[0] with { Coordinate = 123_456 };
        Require(!Decode(CommandEnvelope(FixtureGuid(10_101), SupervisionCallKind.AcquireMarker),
                callerMutableContext, constructorPinnedTranscript),
            "Transcript constructor adopted later mutation through an IsFrozen with-copy.");
        rejected.Add("transcript-private-context-rejects-later-caller-drift");

        var committedOperation = FixtureGuid(10_200);
        var committed = CommandEnvelope(committedOperation, SupervisionCallKind.AcquireMarker);
        var committedTranscript = new DiagnosticTranscript();
        Require(committedTranscript.TryCommitCommand(committed, null, out var committedReservation, out _),
            "Could not commit binding fixture command.");
        Encode(committed, committedReservation!);
        var foreignReturn = FirstReturnEnvelope(committedOperation, 0, [777]) with
        {
            Header = FirstReturnEnvelope(committedOperation, 0, [777]).Header with
            { ContextDigest = new string('7', 64) },
        };
        Require(!committedTranscript.TryReserveFirstReturn(foreignReturn, out _, out _),
            "Foreign context claimed a held completion.");
        Require(committedTranscript.TryReserveFirstReturn(
                FirstReturnEnvelope(committedOperation, 0, [778]), out var boundReturnReservation, out _),
            "Foreign context blocked the matching tuple.");
        Encode(FirstReturnEnvelope(committedOperation, 0, [778]), boundReturnReservation!);
        rejected.Add("held-return-requires-full-immutable-command-binding");

        var writerExpected = WriterTargetExpected;
        var walkOperation = FixtureGuid(10_300);
        var walkCommand = AtSequence(CommandEnvelope(walkOperation, SupervisionCallKind.Walk, target: Writer), 1);
        var walkReturn = AtSequence(FirstReturnEnvelope(walkOperation, 0, [], SupervisionCallKind.Walk), 2);
        var rows0 = RowBatchEnvelope(walkOperation, 0, false, "first", 3);
        var rows1 = RowBatchEnvelope(walkOperation, 1, true, "last", 4);
        var delivery = DeliveryEnvelope(walkOperation, SupervisionCallKind.Walk, 5);
        var journey = new DiagnosticTranscript(writerExpected);
        Require(Decode(walkCommand, writerExpected, journey) && Decode(walkReturn, writerExpected, journey) &&
                Decode(rows0, writerExpected, journey) && Decode(rows1, writerExpected, journey) &&
                Decode(delivery, writerExpected, journey),
            "Command to W, O return/rows, and P delivery journey did not decode in order.");
        positive.Add("w-target-command-firstreturn-orderedrows-delivery-journey");

        foreach (var rawStatus in new uint[] { 259, 5 })
        {
            var noRowsOperation = FixtureGuid(10_310 + (int)rawStatus);
            var noRowsJourney = new DiagnosticTranscript(writerExpected);
            Require(Decode(AtSequence(CommandEnvelope(noRowsOperation, SupervisionCallKind.Walk,
                            target: Writer), 1), writerExpected, noRowsJourney) &&
                    Decode(AtSequence(FirstReturnEnvelope(noRowsOperation, rawStatus, [],
                            SupervisionCallKind.Walk), 2), writerExpected, noRowsJourney) &&
                    Decode(DeliveryEnvelope(noRowsOperation, SupervisionCallKind.Walk, 3),
                        writerExpected, noRowsJourney),
                $"Walk raw status {rawStatus} required fabricated successful rows before delivery.");
        }
        positive.Add("walk-nomoreitems-and-error-first-return-deliver-without-fabricated-rows");
        var missingRowsOperation = FixtureGuid(10_309);
        var missingRowsJourney = new DiagnosticTranscript(writerExpected);
        Require(Decode(AtSequence(CommandEnvelope(missingRowsOperation, SupervisionCallKind.Walk,
                        target: Writer), 1), writerExpected, missingRowsJourney) &&
                Decode(AtSequence(FirstReturnEnvelope(missingRowsOperation, 0, [],
                        SupervisionCallKind.Walk), 2), writerExpected, missingRowsJourney) &&
                !Decode(DeliveryEnvelope(missingRowsOperation, SupervisionCallKind.Walk, 3),
                    writerExpected, missingRowsJourney),
            "Successful Walk delivered without a final row batch.");
        rejected.Add("successful-walk-still-requires-final-row-batch-before-delivery");

        Require(!Decode(RowBatchEnvelope(FixtureGuid(10_301), 0, true, "orphan", 1), writerExpected,
                new DiagnosticTranscript(writerExpected)),
            "Orphan row batch passed.");
        var wrongBatchJourney = new DiagnosticTranscript(writerExpected);
        Require(Decode(walkCommand, writerExpected, wrongBatchJourney) &&
                Decode(walkReturn, writerExpected, wrongBatchJourney) &&
                !Decode(RowBatchEnvelope(walkOperation, 1, true, "wrong-order", 3), writerExpected,
                    wrongBatchJourney),
            "Out-of-order batch passed.");
        var wrongDeliveryJourney = new DiagnosticTranscript(writerExpected);
        Require(Decode(walkCommand, writerExpected, wrongDeliveryJourney) &&
                Decode(walkReturn, writerExpected, wrongDeliveryJourney) &&
                !Decode(DeliveryEnvelope(walkOperation, SupervisionCallKind.AcquireMarker, 3), writerExpected,
                    wrongDeliveryJourney),
            "Wrong-kind delivery passed.");
        rejected.Add("orphan-wrong-order-and-wrong-kind-operation-traffic-rejected");

        var oExit = FinalWithTermination(Owner);
        Require(Decode(oExit, writerExpected, new DiagnosticTranscript(writerExpected)),
            "W sampling target with exact O exit evidence was rejected.");
        Require(!Decode(FinalWithTermination(Writer), writerExpected, new DiagnosticTranscript(writerExpected)),
            "W termination was admitted as O collector exit evidence.");
        var samePidWrongCreation = Owner with { CreationFileTime = Owner.CreationFileTime + 1 };
        Require(!Decode(FinalWithTermination(samePidWrongCreation), writerExpected,
                new DiagnosticTranscript(writerExpected)),
            "Same PID with wrong creation time passed O exit identity.");
        positive.Add("w-sample-target-with-exact-o-termination-evidence");
        rejected.Add("w-or-samepid-wrongcreation-not-o-termination-evidence");
        incomplete.Add("separate-w-cleanup-evidence-remains-unrepresented-and-unqualified");

        foreach (var coordinate in new long[] { 0, -1 })
        {
            var pendingFinal = FinalWithPending(coordinate, coordinate == 0 ? 5u : 0u);
            var frame = EncodeOrdinary(pendingFinal);
            Require(DiagnosticEnvelopeCodec.TryDecode(frame, completeExpected,
                    new DiagnosticTranscript(completeExpected), out var decoded, out _, out _) &&
                    ((DiagnosticAbortExitFinalReport)decoded!.Body).Pending?.Coordinate == coordinate,
                "Unresolved zero/negative pending raw coordinate did not round trip.");
        }
        RequireThrows(() => DiagnosticEnvelope.Create(
                Header(DiagnosticEnvelopeKind.Command, FixtureGuid(10_350)),
                new DiagnosticCommand(Controller, Owner, Owner, SupervisionCallKind.ReleaseMarker,
                    new ObserverDescriptor(ObserverResourceKind.Marker, -1, Owner, Guid.NewGuid(), true),
                    SupervisionPayloadBudget.Empty, 100, 5_100, "p-clock", DiagnosticProofClaims.False)),
            "negative-owned-descriptor-fixture-construction");
        positive.Add("pending-raw-zero-and-negative-coordinates-roundtrip-unresolved");
        rejected.Add("owned-descriptor-coordinate-remains-strictly-positive");

        var conflictOperation = FixtureGuid(10_400);
        var conflictTranscript = new DiagnosticTranscript(completeExpected);
        Require(Decode(AtSequence(CommandEnvelope(conflictOperation, SupervisionCallKind.AcquireMarker), 1),
                    completeExpected, conflictTranscript) &&
                Decode(AtSequence(FirstReturnEnvelope(conflictOperation, 0, [1_001]), 2),
                    completeExpected, conflictTranscript) &&
                !Decode(AtSequence(FirstReturnEnvelope(conflictOperation, 5, [1_002]), 3),
                    completeExpected, conflictTranscript) &&
                conflictTranscript.Snapshot().UnknownObligations.Any(item => item.Coordinate == 1_002) &&
                conflictTranscript.InboundFirstReturnSnapshot(conflictOperation)?.PositiveCoordinates.Single() == 1_001,
            "Direct decoded conflicting positive return was dropped or admitted as new work.");
        rejected.Add("direct-decoded-conflict-preserves-positive-unknown-before-reject");

        foreach (var rejection in new[] { "same-sequence", "reused-event", "wrong-sequence" })
        {
            var operation = FixtureGuid(10_410 + rejection.Length);
            var replayTranscript = new DiagnosticTranscript(completeExpected);
            var acceptedFirst = AtSequence(FirstReturnEnvelope(operation, 0, [1_010]), 2);
            Require(Decode(AtSequence(CommandEnvelope(operation, SupervisionCallKind.AcquireMarker), 1),
                        completeExpected, replayTranscript) &&
                    Decode(acceptedFirst, completeExpected, replayTranscript),
                "Could not establish first-return replay fixture.");
            var rejectedFirst = rejection switch
            {
                "same-sequence" => DiagnosticEnvelope.Create(
                    AtSequence(FirstReturnEnvelope(operation, 5, [1_011]), 2).Header with
                    { EventId = FixtureGuid(10_420) },
                    FirstReturnEnvelope(operation, 5, [1_011]).Body),
                "reused-event" => DiagnosticEnvelope.Create(
                    AtSequence(FirstReturnEnvelope(operation, 5, [1_012]), 3).Header with
                    { EventId = acceptedFirst.Header.EventId },
                    FirstReturnEnvelope(operation, 5, [1_012]).Body),
                _ => AtSequence(FirstReturnEnvelope(operation, 5, [1_013]), 4),
            };
            var rejectedCoordinate = ((DiagnosticFirstRawReturn)rejectedFirst.Body).PositiveCoordinates.Single();
            Require(!Decode(rejectedFirst, completeExpected, replayTranscript) &&
                    replayTranscript.Snapshot().UnknownObligations.Any(item =>
                        item.Coordinate == rejectedCoordinate) &&
                    replayTranscript.InboundFirstReturnSnapshot(operation)?.PositiveCoordinates.Single() == 1_010,
                $"{rejection} positive return was dropped or overwrote the first tuple.");
        }
        rejected.Add("same-sequence-reused-event-and-wrong-sequence-positive-returns-preserved-unknown");

        var decodedRowsOperation = FixtureGuid(10_450);
        var decodedRowsTranscript = new DiagnosticTranscript(completeExpected);
        Require(Decode(AtSequence(CommandEnvelope(decodedRowsOperation, SupervisionCallKind.Walk), 1),
                    completeExpected, decodedRowsTranscript) &&
                Decode(AtSequence(FirstReturnEnvelope(decodedRowsOperation, 0, [], SupervisionCallKind.Walk), 2),
                    completeExpected, decodedRowsTranscript),
            "Could not begin decoded aggregate-row fixture.");
        for (var batchIndex = 0; batchIndex < 16; batchIndex++)
        {
            Require(Decode(ManyRowsBatchEnvelope(decodedRowsOperation, batchIndex, 1_024, 3 + batchIndex),
                    completeExpected, decodedRowsTranscript),
                "Legal aggregate row batch was rejected before 16,384 rows.");
        }
        var decodedRowsBeforeOverflow = decodedRowsTranscript.Snapshot();
        Require(decodedRowsBeforeOverflow.ActualRows == SupervisionPolicy.MaximumRows &&
                !Decode(ManyRowsBatchEnvelope(decodedRowsOperation, 16, 1, 19), completeExpected,
                    decodedRowsTranscript) &&
                decodedRowsTranscript.Snapshot().ActualRows == SupervisionPolicy.MaximumRows,
            "Decoded aggregate row overflow mutated or exceeded the frozen 16,384-row ledger.");
        decodedRowsTranscript.ActivateAbortFinal("row-budget-cleanup");
        var rowBudgetFinal = FinalEnvelope("row-budget-final");
        Require(decodedRowsTranscript.TryReserveAbortFinal(rowBudgetFinal, out var rowBudgetFinalReservation, out _) &&
                Encode(rowBudgetFinal, rowBudgetFinalReservation!).Length > 4 &&
                decodedRowsTranscript.Snapshot().ActualRows == SupervisionPolicy.MaximumRows,
            "Row overflow stole protected final capacity or altered the accepted-row ledger.");

        var publicationRowsTranscript = new DiagnosticTranscript();
        for (var batchIndex = 0; batchIndex < 16; batchIndex++)
        {
            var batch = ManyRowsBatchEnvelope(FixtureGuid(10_451), batchIndex, 1_024, batchIndex + 1);
            Require(publicationRowsTranscript.TryReserveOrdinary(batch, out var batchReservation, out _) &&
                    Encode(batch, batchReservation!).Length > 4,
                "Legal publication row batch was rejected before 16,384 rows.");
        }
        var overflowBatch = ManyRowsBatchEnvelope(FixtureGuid(10_451), 16, 1, 17);
        Require(publicationRowsTranscript.TryReserveOrdinary(overflowBatch, out var overflowReservation, out _),
            "Could not reserve transactional row-overflow fixture.");
        var overflowDestination = Enumerable.Repeat((byte)0xA7,
            DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes).ToArray();
        var overflowBefore = overflowDestination.ToArray();
        Require(!DiagnosticEnvelopeCodec.TryEncode(overflowBatch, overflowReservation!, overflowDestination,
                out var overflowWritten, out _) && overflowWritten == 0 &&
                overflowDestination.SequenceEqual(overflowBefore) &&
                publicationRowsTranscript.Snapshot().ActualRows == SupervisionPolicy.MaximumRows,
            "Publication aggregate row overflow changed output or ledger.");
        rejected.Add("aggregate-16384-row-limit-transactional-for-decode-and-publication");

        var heldOperation = FixtureGuid(10_500);
        var heldTranscript = new DiagnosticTranscript(completeExpected);
        var heldCommand = CommandEnvelope(heldOperation, SupervisionCallKind.AcquireMarker);
        Require(heldTranscript.TryCommitCommand(heldCommand, null, out var heldCommandReservation, out _) &&
                Encode(heldCommand, heldCommandReservation!).Length > 4,
            "Could not commit held-return fixture command.");
        Require(heldTranscript.TryCreateHeldReturnAdmission(heldOperation, out var heldAdmission, out _),
            "Could not create trusted held-return receive admission.");
        var malformed = Frame(Encoding.UTF8.GetBytes("{}"));
        for (var index = 0; index < SupervisionPolicy.OrdinaryEventLimit + 20; index++)
            DiagnosticEnvelopeCodec.TryDecode(malformed, completeExpected, heldTranscript, out _, out _, out _);
        var foreignHeld = AtSequence(FirstReturnEnvelope(heldOperation, 5, [1_100]), 1) with
        {
            Header = AtSequence(FirstReturnEnvelope(heldOperation, 5, [1_100]), 1).Header with
            { ContextDigest = new string('6', 64) },
        };
        var foreignHeldFrame = EncodeOrdinary(foreignHeld);
        var beforeForeignInspection = heldTranscript.Snapshot();
        Require(!DiagnosticEnvelopeCodec.TryDecodeHeldReturn(foreignHeldFrame, completeExpected,
                heldTranscript, heldAdmission!, out _, out _, out _) &&
                heldTranscript.Snapshot().UnknownObligations.Any(item => item.Coordinate == 1_100) &&
                heldTranscript.Snapshot().OrdinaryEvents == beforeForeignInspection.OrdinaryEvents + 1 &&
                heldTranscript.Snapshot().ActualEncodedBytes ==
                beforeForeignInspection.ActualEncodedBytes + foreignHeldFrame.Length,
            "Foreign held return was not charged to its funded inspection credit or lost positive custody.");
        var validHeld = AtSequence(FirstReturnEnvelope(heldOperation, 0, [1_101]), 1);
        Require(DiagnosticEnvelopeCodec.TryDecodeHeldReturn(EncodeOrdinary(validHeld), completeExpected,
                heldTranscript, heldAdmission!, out _, out _, out _) &&
                heldTranscript.Snapshot().OrdinaryEvents <= SupervisionPolicy.OrdinaryEventLimit,
            "Stopped ordinary ingestion could not receive the locally admitted matching return.");
        var conflictingHeld = AtSequence(FirstReturnEnvelope(heldOperation, 5, [1_102]), 2);
        Require(!DiagnosticEnvelopeCodec.TryDecodeHeldReturn(EncodeOrdinary(conflictingHeld), completeExpected,
                heldTranscript, heldAdmission!, out _, out _, out _) &&
                heldTranscript.Snapshot().UnknownObligations.Any(item => item.Coordinate == 1_102) &&
                heldTranscript.HeldFirstReturnSnapshot(heldOperation)?.PositiveCoordinates.Single() == 1_101,
            "Held-return replay overwrote first custody or spent another credit.");
        positive.Add("held-matching-return-received-after-flood-with-reserved-credit");
        rejected.Add("foreign-or-replayed-held-return-consumes-funded-inspection-and-preserves-unknown");

        var exhaustedOperation = FixtureGuid(10_510);
        var exhaustedTranscript = new DiagnosticTranscript(completeExpected);
        var exhaustedCommand = CommandEnvelope(exhaustedOperation, SupervisionCallKind.AcquireMarker);
        Require(exhaustedTranscript.TryCommitCommand(exhaustedCommand, null,
                    out var exhaustedCommandReservation, out _) &&
                Encode(exhaustedCommand, exhaustedCommandReservation!).Length > 4,
            "Could not commit finite held-inspection fixture command.");
        Require(exhaustedTranscript.TryCreateHeldReturnAdmission(exhaustedOperation,
                out var exhaustedAdmission, out _),
            "Could not establish finite held-inspection pool fixture.");
        for (var index = 0; index < SupervisionPolicy.OrdinaryEventLimit + 20; index++)
            DiagnosticEnvelopeCodec.TryDecode(malformed, completeExpected, exhaustedTranscript,
                out _, out _, out _);
        var beforeInspectionPool = exhaustedTranscript.Snapshot();
        for (var index = 0; index < DiagnosticTranscript.MaximumHeldInspectionFrames; index++)
        {
            Require(!DiagnosticEnvelopeCodec.TryDecodeHeldReturn(malformed, completeExpected,
                    exhaustedTranscript, exhaustedAdmission!, out _, out _, out _),
                "Malformed held inspection decoded.");
        }
        var afterInspectionPool = exhaustedTranscript.Snapshot();
        Require(afterInspectionPool.OrdinaryEvents == beforeInspectionPool.OrdinaryEvents +
                    DiagnosticTranscript.MaximumHeldInspectionFrames &&
                afterInspectionPool.ActualEncodedBytes == beforeInspectionPool.ActualEncodedBytes +
                    DiagnosticTranscript.MaximumHeldInspectionFrames * malformed.Length &&
                afterInspectionPool.ReleaseEvents == 0 && afterInspectionPool.AbortFinalEvents == 0,
            "Finite held inspection pool was not charged exact actual frames or touched protected partitions.");
        var deniedMatchingFrame = EncodeOrdinary(
            AtSequence(FirstReturnEnvelope(exhaustedOperation, 0, [1_120]), 1));
        Require(!DiagnosticEnvelopeCodec.TryDecodeHeldReturn(deniedMatchingFrame, completeExpected,
                exhaustedTranscript, exhaustedAdmission!, out _, out var deniedError, out var deniedObservation) &&
                deniedError == "ordinary-ingestion-stopped" &&
                deniedObservation.PayloadAllocationRequestedBytes == 0 &&
                exhaustedTranscript.Snapshot().OrdinaryEvents == afterInspectionPool.OrdinaryEvents &&
                exhaustedTranscript.Snapshot().ActualEncodedBytes == afterInspectionPool.ActualEncodedBytes &&
                exhaustedTranscript.HeldFirstReturnSnapshot(exhaustedOperation) is null,
            "Exhausted inspection pool admitted another parser frame or consumed the held completion.");
        incomplete.Add("eight-funded-held-inspections-exhaust-to-unresolved-pending-without-parser-retry");
    }

    private static void CheckTranscriptPartitions(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete,
        List<DiagnosticFixtureEvidence> fixtureEvidence)
    {
        var transcript = new DiagnosticTranscript();
        for (var index = 0; index < 2; index++)
        {
            var operation = FixtureGuid(2_500 + index);
            var descriptor = new ObserverDescriptor(index == 0 ? ObserverResourceKind.Marker : ObserverResourceKind.Snapshot,
                500 + index, Owner, FixtureGuid(3_000 + index), true);
            var command = CommandEnvelope(operation,
                index == 0 ? SupervisionCallKind.ReleaseMarker : SupervisionCallKind.ReleaseSnapshot,
                descriptor);
            Require(transcript.TryCommitFreeCommand(command, descriptor, out var commandReservation, out _),
                "Could not reserve trusted free command.");
            var commandBytes = Encode(command, commandReservation!);
            var result = FirstReturnEnvelope(operation, 0, [descriptor.Coordinate], ((DiagnosticCommand)command.Body).Kind, descriptor);
            Require(transcript.TryReserveFirstReturn(result, out var returnReservation, out _),
                "Could not claim held free first-return reservation.");
            var returnBytes = Encode(result, returnReservation!);
            fixtureEvidence.Add(Evidence($"release-{index}-command", command.Kind, commandBytes, true));
            fixtureEvidence.Add(Evidence($"release-{index}-first-return", result.Kind, returnBytes, true));
        }
        Require(transcript.Snapshot().ReleaseEvents == SupervisionPolicy.ReservedReleaseEvents,
            "Two free commands and two free first returns did not consume exactly four release frames.");

        transcript.ActivateAbortFinal("synthetic-local-abort");
        for (var index = 0; index < SupervisionPolicy.ReservedAbortAndFinalEvents; index++)
        {
            var final = MaximumFinalEnvelope(index);
            Require(transcript.TryReserveAbortFinal(final, out var finalReservation, out _),
                "Could not reserve trusted local final frame.");
            var encoded = Encode(final, finalReservation!);
            Require(encoded.Length <= DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes,
                "Actual cleanup/final fixture exceeded a full-frame reservation.");
            fixtureEvidence.Add(Evidence($"abort-final-{index}-maximal-bounded", final.Kind, encoded, true));
        }
        var partition = transcript.Snapshot();
        Require(partition.ReleaseEvents == 4 && partition.AbortFinalEvents == 8 &&
                partition.ActualEncodedBytes <= SupervisionPolicy.MaximumEncodedBytes,
            "Actual four release/eight final fixtures exceeded frozen partitions.");
        positive.Add("actual-four-release-and-eight-final-frames-fit-reservations");

        var heldTranscript = new DiagnosticTranscript();
        var heldOperation = Guid.NewGuid();
        var heldCommand = CommandEnvelope(heldOperation, SupervisionCallKind.AcquireMarker);
        Require(heldTranscript.TryCommitCommand(heldCommand, null, out var heldCommandReservation, out _),
            "Could not commit held-credit command.");
        Encode(heldCommand, heldCommandReservation!);
        var malformedHeld = Frame(Encoding.UTF8.GetBytes("{}"));
        for (var index = 0; index < SupervisionPolicy.OrdinaryEventLimit + 20; index++)
            DiagnosticEnvelopeCodec.TryDecode(malformedHeld, Expected, heldTranscript, out _, out _, out _);
        var heldReturn = FirstReturnEnvelope(heldOperation, 0, [901]);
        Require(heldTranscript.TryReserveFirstReturn(heldReturn, out var heldReturnReservation, out _) &&
                Encode(heldReturn, heldReturnReservation!).Length > 4 &&
                heldTranscript.Snapshot().OrdinaryEvents <= SupervisionPolicy.OrdinaryEventLimit,
            "Rejected flood stole the held matching-completion event or byte credit.");
        positive.Add("rejected-flood-cannot-steal-held-matching-completion-credit");

        var forged = EncodeOrdinary(FinalEnvelope("counterfeit"));
        var forgedTranscript = new DiagnosticTranscript();
        DiagnosticEnvelopeCodec.TryDecode(Frame(Encoding.UTF8.GetBytes("{}")), Expected, forgedTranscript,
            out _, out _, out _);
        Require(DiagnosticEnvelopeCodec.TryDecode(
                forged, Expected, forgedTranscript, out _, out _, out var forgedObservation) &&
                forgedObservation.ProtectedCapacityClaimRejected &&
                forgedTranscript.Snapshot().AbortFinalEvents == 0,
            "Decoded final record self-activated protected report capacity.");
        rejected.Add("decoded-final-and-release-records-cannot-self-claim-reserves");

        var flood = new DiagnosticTranscript();
        var malformed = Frame(Encoding.UTF8.GetBytes("{}"));
        for (var index = 0; index < SupervisionPolicy.OrdinaryEventLimit + 20; index++)
            DiagnosticEnvelopeCodec.TryDecode(malformed, Expected, flood, out _, out _, out _);
        Require(flood.Snapshot().ReleaseEvents == 0 && flood.Snapshot().AbortFinalEvents == 0 &&
                flood.Snapshot().RejectedSummary.Count <= DiagnosticTranscript.MaximumRejectedSummaryKinds,
            "Rejected flood consumed protected frames or unbounded its summary.");
        incomplete.Add("ordinary-rejected-flood-stops-before-protected-reserves");
    }

    private static void CheckUnknownAndPending(
        List<string> positive,
        List<string> rejected,
        List<string> incomplete)
    {
        var transcript = new DiagnosticTranscript();
        var operation = Guid.NewGuid();
        var slot = new DiagnosticFirstReturnSlot(Expected.ContextDigest, 1, operation,
            SupervisionCallKind.AcquireMarker, Owner);
        var first = FirstReturnEnvelope(operation, 0, [700]);
        Require(slot.TryStore(first, transcript, out _, out _), "Could not retain pending first tuple.");
        for (var index = 0; index < SupervisionPolicy.MaximumUnknownReturns + 1; index++)
        {
            var duplicateBody = ((DiagnosticFirstRawReturn)first.Body) with
            { PositiveCoordinates = new[] { 800L + index }, ApiStatus = 5 };
            var duplicate = DiagnosticEnvelope.Create(first.Header with
            { EventId = Guid.NewGuid(), Sequence = index + 2 }, duplicateBody);
            slot.TryStore(duplicate, transcript, out _, out _);
        }
        var snapshot = transcript.Snapshot();
        Require(snapshot.UnknownObligations.Count == SupervisionPolicy.MaximumUnknownReturns &&
                snapshot.UnknownOverflow && slot.Snapshot()?.PositiveCoordinates.Single() == 700,
            "Unknown overflow evicted the separate first/pending tuple.");
        incomplete.Add("eight-unknowns-sticky-overflow-and-separate-pending-retained");

        Require(!snapshot.NativeCallsPerformed && !snapshot.PeerAuthenticated &&
                !snapshot.NativeAuthorityProven && !snapshot.ProductionAdmissionGranted &&
                !snapshot.B1WriteReleaseIssued && !snapshot.ReleaseApproved,
            "Codec transcript promoted a native/admission/release proof.");
        positive.Add("codec-transcript-proof-flags-always-false");
        rejected.Add("unknown-overflow-never-fabricates-release-or-new-work");
    }

    private static byte[] EncodeOrdinary(DiagnosticEnvelope envelope)
    {
        var transcript = new DiagnosticTranscript();
        Require(transcript.TryReserveOrdinary(envelope, out var reservation, out var error),
            $"Could not reserve ordinary envelope: {error}");
        return Encode(envelope, reservation!);
    }

    private static byte[] Encode(DiagnosticEnvelope envelope, DiagnosticFrameReservation reservation)
    {
        var destination = new byte[DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes];
        Require(DiagnosticEnvelopeCodec.TryEncode(envelope, reservation, destination, out var written, out var error),
            $"Encode failed: {error}");
        return destination[..written];
    }

    private static void RejectJson(
        string json,
        string name,
        List<string> rejected,
        bool includeTrailingInPrefix = false)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        var frame = includeTrailingInPrefix ? Frame(payload[..^1]).Concat(payload[^1..]).ToArray() : Frame(payload);
        Require(!DiagnosticEnvelopeCodec.TryDecode(
                frame, Expected, new DiagnosticTranscript(), out _, out _, out _),
            $"Malformed fixture passed: {name}");
        rejected.Add(name);
    }

    private static string ValidCommandJson() =>
        Encoding.UTF8.GetString(EncodeOrdinary(CommandEnvelope(Guid.Parse("781a4186-3507-4c56-9e29-a7bd8214ef79"),
            SupervisionCallKind.AcquireMarker)).AsSpan(4));

    private static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var frame = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)payload.Length);
        payload.CopyTo(frame.AsSpan(4));
        return frame;
    }

    private static DiagnosticEnvelope PreparedEnvelope(IReadOnlyList<SupervisionCapabilitySlot> slots) =>
        DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.PreparedContext, Guid.Empty), new DiagnosticPreparedContext(
            RunId, SessionId, 1, InventoryAnchor.B1, "trusted-parent",
            SourceDigest, BinaryDigest, SdkDigest, "windows-x64-supplied",
            Controller, Owner, Writer, Owner, SupervisionTargetContext.OwnerCurrentProcess,
            "o:self-current-process", InventoryMeasurementProvenance.Synthetic,
            CustodyDigest, true, slots, Policy, "p-clock", "o-clock", DiagnosticProofClaims.False));

    private static DiagnosticEnvelope CommandEnvelope(
        Guid operation,
        SupervisionCallKind kind,
        ObserverDescriptor? descriptor = null,
        InventoryProcessIdentity? target = null) =>
        DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.Command, operation), new DiagnosticCommand(
            Controller, Owner, target ?? Owner, kind, descriptor, SupervisionPayloadBudget.Empty,
            100, 5_100, "p-clock", DiagnosticProofClaims.False));

    private static DiagnosticEnvelope CustodyAckEnvelope(
        string digest,
        IReadOnlyList<SupervisionCapabilitySlot> slots) =>
        DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.CustodyAcknowledgment, FixtureGuid(11_000)),
            new DiagnosticCustodyAcknowledgment(Controller, digest, slots, DiagnosticProofClaims.False));

    private static DiagnosticEnvelope FirstReturnEnvelope(
        Guid operation,
        uint status,
        IReadOnlyList<long> coordinates,
        SupervisionCallKind kind = SupervisionCallKind.AcquireMarker,
        ObserverDescriptor? descriptor = null,
        long ownerClockFrequency = 1_000) =>
        DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.FirstRawReturn, operation), new DiagnosticFirstRawReturn(
            Owner, kind, status, coordinates, descriptor,
            200, "o-clock", ownerClockFrequency, null, DiagnosticProofClaims.False));

    private static DiagnosticEnvelope RowEnvelope(string value) =>
        DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.ValueRowBatch, FixtureGuid(2_000)),
            new DiagnosticValueRowBatch(Owner, 0, false, [new DiagnosticValueRow(0, value)], DiagnosticProofClaims.False));

    private static DiagnosticEnvelope RowBatchEnvelope(
        Guid operation,
        int batchIndex,
        bool isFinal,
        string value,
        long sequence) => AtSequence(
        DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.ValueRowBatch, operation),
            new DiagnosticValueRowBatch(Owner, batchIndex, isFinal,
                [new DiagnosticValueRow(batchIndex, value)], DiagnosticProofClaims.False)), sequence);

    private static DiagnosticEnvelope ManyRowsBatchEnvelope(
        Guid operation,
        int batchIndex,
        int rowCount,
        long sequence) => AtSequence(
        DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.ValueRowBatch, operation),
            new DiagnosticValueRowBatch(Owner, batchIndex, false,
                Enumerable.Range(0, rowCount).Select(index => new DiagnosticValueRow(index, string.Empty)).ToArray(),
                DiagnosticProofClaims.False)), sequence);

    private static DiagnosticEnvelope DeliveryEnvelope(
        Guid operation,
        SupervisionCallKind kind,
        long sequence) => AtSequence(
        DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.DeliveryObservation, operation),
            new DiagnosticDeliveryObservation(Controller, kind, true, 900 + sequence, "p-clock",
                DiagnosticProofClaims.False)), sequence);

    private static DiagnosticEnvelope FinalEnvelope(string reason) =>
        DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.AbortExitFinalReport, Guid.NewGuid()),
            new DiagnosticAbortExitFinalReport(
                CollectorSupervisionState.CleanupUnresolved, reason, [], null, false, [], null,
                SupervisionJobObservation.NotObserved, DiagnosticProofClaims.False));

    private static DiagnosticEnvelope FinalWithTermination(InventoryProcessIdentity target) =>
        DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.AbortExitFinalReport, FixtureGuid(11_100)),
            new DiagnosticAbortExitFinalReport(
                CollectorSupervisionState.ExactExited, "exact-o-exit", [], null, false, [],
                new SupervisionTerminationEvidence(target, 0, 100, true, 0, 200, 300,
                    SupervisionJobObservation.EmptyWhileOwned, 400),
                SupervisionJobObservation.EmptyWhileOwned, DiagnosticProofClaims.False));

    private static DiagnosticEnvelope FinalWithPending(long coordinate, uint apiStatus) =>
        DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.AbortExitFinalReport,
                FixtureGuid(11_200 + (int)Math.Abs(coordinate))),
            new DiagnosticAbortExitFinalReport(
                CollectorSupervisionState.CleanupUnresolved, "raw-pending-unresolved", [],
                new SupervisionPendingCompletionSnapshot(SupervisionCallKind.AcquireMarker, FixtureGuid(11_300),
                    500, apiStatus, coordinate, null, SupervisionPayloadBudget.Empty),
                false, [], null, SupervisionJobObservation.NotObserved, DiagnosticProofClaims.False));

    private static DiagnosticEnvelope AtSequence(DiagnosticEnvelope envelope, long sequence)
    {
        var eventBytes = envelope.Header.EventId.ToByteArray();
        BinaryPrimitives.WriteInt64LittleEndian(eventBytes.AsSpan(8), sequence);
        return DiagnosticEnvelope.Create(envelope.Header with { Sequence = sequence, EventId = new Guid(eventBytes) },
            envelope.Body);
    }

    private static DiagnosticEnvelope Reidentify(DiagnosticEnvelope envelope, Guid operationId)
    {
        var eventBytes = operationId.ToByteArray();
        eventBytes[^1] ^= (byte)(0xA0 + (int)envelope.Kind);
        return DiagnosticEnvelope.Create(envelope.Header with
        {
            OperationId = operationId,
            EventId = new Guid(eventBytes),
        }, envelope.Body);
    }

    private static bool Decode(
        DiagnosticEnvelope envelope,
        DiagnosticDecodeContext expected,
        DiagnosticTranscript transcript) => DiagnosticEnvelopeCodec.TryDecode(
        EncodeOrdinary(envelope), expected, transcript, out _, out _, out _);

    private static DiagnosticTranscript RowDecodeTranscript()
    {
        var operation = FixtureGuid(2_000);
        var transcript = new DiagnosticTranscript(Expected);
        Require(Decode(AtSequence(CommandEnvelope(operation, SupervisionCallKind.Walk), 1), Expected, transcript) &&
                Decode(AtSequence(FirstReturnEnvelope(operation, 0, [], SupervisionCallKind.Walk), 2), Expected, transcript),
            "Could not prepare a correlated Unicode row fixture.");
        return transcript;
    }

    private static DiagnosticEnvelope MaximumFinalEnvelope(int seed = 0)
    {
        var escapedReason = new string('\u0001', DiagnosticValidation.MaximumFinalReasonBytes);
        var escapedEvidence = new string('\u0001', DiagnosticValidation.MaximumEvidenceBytes);
        var unknowns = Enumerable.Range(0, SupervisionPolicy.MaximumUnknownReturns)
            .Select(index => new SupervisionUnknownObligation(
                ObserverResourceKind.Marker, Owner, FixtureGuid(4_000 + seed * 100 + index), uint.MaxValue,
                long.MaxValue - index, escapedEvidence, escapedReason, false, long.MaxValue - index))
            .ToArray();
        var descriptor = new ObserverDescriptor(ObserverResourceKind.Marker, long.MaxValue, Owner,
            FixtureGuid(5_000 + seed), true);
        var pending = new SupervisionPendingCompletionSnapshot(
            SupervisionCallKind.AcquireMarker, FixtureGuid(6_000 + seed), long.MaxValue, uint.MaxValue,
            long.MaxValue, descriptor,
            SupervisionPayloadBudget.Empty);
        var tickets = Enumerable.Range(0, 128)
            .Select(_ => new string('\u0001', DiagnosticValidation.MaximumTicketBytes))
            .ToArray();
        var termination = new SupervisionTerminationEvidence(
            Owner, uint.MaxValue, long.MaxValue - 3, true, uint.MaxValue, long.MaxValue - 2,
            long.MaxValue - 2, SupervisionJobObservation.NotObserved, long.MaxValue - 1);
        return DiagnosticEnvelope.Create(Header(DiagnosticEnvelopeKind.AbortExitFinalReport, FixtureGuid(7_000 + seed)),
            new DiagnosticAbortExitFinalReport(
                CollectorSupervisionState.CleanupUnresolved, escapedReason, unknowns, pending, true, tickets,
                termination, SupervisionJobObservation.NotObserved, DiagnosticProofClaims.False));
    }

    private static DiagnosticEnvelopeHeader Header(DiagnosticEnvelopeKind kind, Guid operation)
    {
        if (operation == Guid.Empty) operation = FixtureGuid(9_000 + (int)kind);
        var eventBytes = operation.ToByteArray();
        eventBytes[^1] ^= (byte)(0xA0 + (int)kind);
        return new(
            DiagnosticEnvelopeCodec.Schema,
            DiagnosticEnvelopeCodec.Version,
            kind,
            Expected.ContextDigest,
            1,
            1,
            new Guid(eventBytes),
            operation);
    }

    private static Guid FixtureGuid(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        bytes[15] = 0x5D;
        return new Guid(bytes);
    }

    private static IReadOnlyList<SupervisionCapabilitySlot> Slots()
    {
        var process = new InventoryObjectIdentity("o-process", TargetProcessId: Owner.ProcessId,
            TargetCreationFileTime: Owner.CreationFileTime);
        return
        [
            new("p:o-process-original", Controller, InventoryRole.Controller, null, InventoryRole.Owner,
                InventoryObjectClass.Process, InventoryCoordinateKind.Owned, 100, Controller, process,
                0x001FFFFF, 0x001FFFFF, false, InventoryMeasurementProvenance.Synthetic),
        ];
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void RequireThrows(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static DiagnosticFixtureEvidence Evidence(
        string name,
        DiagnosticEnvelopeKind kind,
        byte[] frame,
        bool accepted) => new(
            name,
            kind.ToString(),
            accepted,
            frame.Length,
            frame.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(frame) : 0,
            Convert.ToHexString(SHA256.HashData(frame)).ToLowerInvariant());

    private sealed record DiagnosticFixtureEvidence(
        string Name,
        string Kind,
        bool Accepted,
        int CompleteFrameBytes,
        uint DeclaredPayloadBytes,
        string Sha256);

    private static object LimitsReceipt() => new
    {
        completeFrameBytes = DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes,
        payloadBytes = DiagnosticEnvelopeCodec.MaximumPayloadBytes,
        totalEncodedBytes = SupervisionPolicy.MaximumEncodedBytes,
        protectedFullFrames = SupervisionPolicy.ReservedReleaseEvents +
            SupervisionPolicy.ReservedAbortAndFinalEvents,
        fundedHeldInspectionFrames = DiagnosticTranscript.MaximumHeldInspectionFrames,
        fundedHeldInspectionBytes = DiagnosticTranscript.MaximumHeldInspectionFrames *
            DiagnosticEnvelopeCodec.MaximumCompleteFrameBytes,
        ordinaryEncodedBytes = SupervisionPolicy.MaximumOrdinaryEncodedBytes,
        aggregateRows = SupervisionPolicy.MaximumRows,
        aggregateCopiedBytes = SupervisionPolicy.MaximumCopiedBytes,
        finalReasonBytes = DiagnosticValidation.MaximumFinalReasonBytes,
        finalEvidenceBytes = DiagnosticValidation.MaximumEvidenceBytes,
        finalTicketCount = 128,
        finalTicketBytes = DiagnosticValidation.MaximumTicketBytes,
        unknownCount = SupervisionPolicy.MaximumUnknownReturns,
    };

    private static readonly Guid RunId = Guid.Parse("105ed191-871c-47b6-84ff-eb46cf52c477");
    private static readonly Guid SessionId = Guid.Parse("691683ee-38fa-4cb9-a245-2b8a1afc6e39");
    private static readonly InventoryProcessIdentity Controller = new(InventoryRole.Controller, 5101, 20101);
    private static readonly InventoryProcessIdentity Owner = new(InventoryRole.Owner, 5102, 20102);
    private static readonly InventoryProcessIdentity Writer = new(InventoryRole.Writer, 5103, 20103);
    private static readonly SupervisionPolicy Policy = SupervisionPolicy.FromFrequency(1_000);
    private static readonly string SourceDigest = new('a', 64);
    private static readonly string BinaryDigest = new('b', 64);
    private static readonly string SdkDigest = new('c', 64);
    private static readonly string CustodyDigest = new('d', 64);
    private static readonly DiagnosticDecodeContext Expected = new DiagnosticDecodeContext(
        new string('e', 64), 1, RunId, SessionId, 1, InventoryAnchor.B1,
        SourceDigest, BinaryDigest, Controller, Owner, Writer, Owner,
        SupervisionTargetContext.OwnerCurrentProcess, "o:self-current-process").Freeze();
    private static readonly DiagnosticDecodeContext CompleteExpected = (Expected with
    {
        SelectedResource = "trusted-parent",
        SdkDigest = SdkDigest,
        OsExpectation = "windows-x64-supplied",
        CustodyDigest = CustodyDigest,
        ExpectedCustody = Slots(),
        ExpectedPolicy = Policy,
        OwnerClockFrequency = 1_000,
    }).Freeze();
    private static readonly DiagnosticDecodeContext WriterTargetExpected = (CompleteExpected with
    {
        Target = Writer,
        TargetContext = SupervisionTargetContext.OwnerOriginalWriterSlot,
        TargetSlotId = "w:owner-original-writer-slot",
    }).Freeze();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
