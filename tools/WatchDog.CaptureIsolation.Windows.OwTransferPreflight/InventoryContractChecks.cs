using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal static class InventoryContractChecks
{
    internal static int Run()
    {
        var positiveCases = new List<string>();
        var rejectionCases = new List<string>();
        var incompleteCases = new List<string>();
        try
        {
            RequireGoldenCatalog();
            positiveCases.Add("golden-source-catalog-counts-masks-and-roles");

            foreach (var anchor in new[] { InventoryAnchor.B0, InventoryAnchor.B1 })
            {
                foreach (var role in Enum.GetValues<InventoryRole>())
                {
                    var context = NewContext(anchor, role, "trusted-parent");
                    RequireValid(context, NewPacket(context));
                    positiveCases.Add($"valid-{anchor}-{role}");
                }
            }

            foreach (var resource in new[]
                     { "trusted-parent", "private-root", "frames", "anchor" })
            {
                foreach (var role in Enum.GetValues<InventoryRole>())
                {
                    var context = NewContext(InventoryAnchor.B2, role, resource);
                    RequireValid(context, NewPacket(context));
                    positiveCases.Add($"valid-B2-{resource}-{role}");
                }
            }

            var optionalClientContext = NewContext(
                InventoryAnchor.B0,
                InventoryRole.Owner,
                "trusted-parent",
                requireOwnerControllerClient: false);
            RequireValid(optionalClientContext, NewPacket(optionalClientContext));
            positiveCases.Add("trusted-subanchor-optional-client-fixed-false");

            var pB1 = NewContext(InventoryAnchor.B1, InventoryRole.Controller, "trusted-parent");
            var pB1Packet = NewPacket(pB1);
            Reject("packet-self-reduced-required-set", pB1,
                pB1Packet with
                {
                    DeclaredRequiredSlotIds = pB1Packet.DeclaredRequiredSlotIds.Skip(1).ToArray(),
                }, rejectionCases);
            Reject("missing-required-manifest-slot", pB1,
                pB1Packet with { Manifest = pB1Packet.Manifest.Skip(1).ToArray() }, rejectionCases);
            Reject("demonstrated-required-absence", pB1,
                ReplaceSlot(pB1Packet, pB1Packet.Manifest[0].LogicalSlotId,
                    slot => slot with { Presence = InventoryPresence.DemonstratedAbsent }),
                rejectionCases);
            Reject("target-pid-drift", pB1,
                pB1Packet with
                {
                    Target = pB1Packet.Target with { ProcessId = pB1Packet.Target.ProcessId + 1 },
                }, rejectionCases);
            Reject("target-creation-drift", pB1,
                pB1Packet with
                {
                    Target = pB1Packet.Target with
                    {
                        CreationFileTime = pB1Packet.Target.CreationFileTime + 1,
                    },
                }, rejectionCases);
            Reject("stage-drift", pB1,
                pB1Packet with { Stage = "B2-selected-before-ack" }, rejectionCases);
            Reject("attempt-drift", pB1,
                pB1Packet with { Attempt = 2 }, rejectionCases);
            Reject("schema-drift", pB1,
                pB1Packet with { Schema = "watchdog.ow-handle-inventory-contract.v0" },
                rejectionCases);

            var knownId = pB1Packet.Manifest.First(slot => slot.Observation is not null).LogicalSlotId;
            Reject("excess-rights", pB1,
                ReplaceSlot(pB1Packet, knownId, slot => slot with
                {
                    Observation = slot.Observation! with
                    {
                        GrantedAccess = slot.Observation!.GrantedAccess!.Value | 0x80000000,
                    },
                }), rejectionCases);
            Reject("unsafe-classification", pB1,
                ReplaceSlot(pB1Packet, knownId, slot => slot with
                {
                    Classification = slot.Classification == InventoryCapabilityClass.WriteCapable
                        ? InventoryCapabilityClass.ReadOnly
                        : InventoryCapabilityClass.WriteCapable,
                }), rejectionCases);
            Reject("inheritance-true", pB1,
                ReplaceSlot(pB1Packet, knownId, slot => slot with
                {
                    Observation = slot.Observation! with { Inheritable = true },
                }), rejectionCases);
            Reject("invalid-observation-field-flags", pB1,
                ReplaceSlot(pB1Packet, knownId, slot => slot with
                {
                    Observation = slot.Observation! with { AccessFieldValid = false },
                }), rejectionCases);
            Reject("role-drift", pB1,
                ReplaceSlot(pB1Packet, pB1Packet.Manifest[0].LogicalSlotId, slot => slot with
                {
                    Holder = slot.Holder with { Role = InventoryRole.Writer },
                }), rejectionCases);
            Reject("invalid-manifest-coordinate", pB1,
                ReplaceSlot(pB1Packet, pB1Packet.Manifest[0].LogicalSlotId, slot => slot with
                {
                    Coordinate = new(InventoryCoordinateKind.Zero, 0),
                }), rejectionCases);
            Reject("invalid-slot-enum", pB1,
                ReplaceSlot(pB1Packet, pB1Packet.Manifest[0].LogicalSlotId, slot => slot with
                {
                    Presence = (InventoryPresence)99,
                }), rejectionCases);
            Reject("invalid-release-enum", pB1,
                pB1Packet with { ResourceReleaseStatus = (InventoryReleaseStatus)99 },
                rejectionCases);

            var fixtureId = pB1Packet.Manifest.First(slot =>
                slot.ObjectIdentity.FileId is not null).LogicalSlotId;
            Reject("file-identity-drift", pB1,
                ReplaceSlot(pB1Packet, fixtureId, slot => slot with
                {
                    ObjectIdentity = slot.ObjectIdentity with { FileId = "drifted" },
                }), rejectionCases);
            var coordinateCollision = pB1Packet.Manifest[0].Coordinate;
            Reject("duplicate-coordinate-claim", pB1,
                ReplaceSlot(pB1Packet, pB1Packet.Manifest[1].LogicalSlotId, slot => slot with
                {
                    Coordinate = coordinateCollision,
                }), rejectionCases);
            var aliasSource = pB1Packet.Manifest.First(slot => slot.Observation is not null);
            var aliasTarget = pB1Packet.Manifest.First(slot =>
                slot.LogicalSlotId != aliasSource.LogicalSlotId && slot.Observation is not null);
            var duplicateAliasPacket = ReplaceSlot(
                pB1Packet,
                aliasTarget.LogicalSlotId,
                slot => slot with
                {
                    Coordinate = aliasSource.Coordinate,
                    ObjectIdentity = aliasSource.ObjectIdentity,
                }) with
            {
                Rows = [KnownRow(pB1Packet) with { LogicalSlotId = null }],
            };
            Reject("duplicate-alias-unlabeled-row-structured", pB1,
                duplicateAliasPacket, rejectionCases);
            Reject("duplicate-logical-slot", pB1,
                pB1Packet with { Manifest = [.. pB1Packet.Manifest, pB1Packet.Manifest[0]] },
                rejectionCases);
            var unexpectedSlot = pB1Packet.Manifest[0] with
            {
                LogicalSlotId = "p:untrusted-extra",
                Coordinate = new(InventoryCoordinateKind.Owned, 77_777),
            };
            var unexpectedRow = KnownRow(pB1Packet) with
            {
                LogicalSlotId = unexpectedSlot.LogicalSlotId,
                Coordinate = unexpectedSlot.Coordinate,
            };
            Reject("unexpected-slot-with-labeled-row-structured", pB1,
                pB1Packet with
                {
                    Manifest = [.. pB1Packet.Manifest, unexpectedSlot],
                    Rows = [unexpectedRow],
                }, rejectionCases);
            Reject("collector-resource-release-failed", pB1,
                pB1Packet with { ResourceReleaseStatus = InventoryReleaseStatus.Failed },
                rejectionCases);
            Reject("unsupported-proof-claim", pB1,
                pB1Packet with
                {
                    ProofClaims = pB1Packet.ProofClaims with
                    {
                        CompleteHandleInventoryProven = true,
                    },
                }, rejectionCases);

            var rootContext = NewContext(
                InventoryAnchor.B2, InventoryRole.Controller, "private-root");
            var rootPacket = NewPacket(rootContext);
            var leasedId = rootPacket.Manifest.First(slot =>
                slot.ObjectIdentity.LeaseId is not null).LogicalSlotId;
            Reject("lease-drift", rootContext,
                ReplaceSlot(rootPacket, leasedId, slot => slot with
                {
                    ObjectIdentity = slot.ObjectIdentity with { LeaseId = Guid.NewGuid() },
                }), rejectionCases);
            Reject("generation-drift", rootContext,
                ReplaceSlot(rootPacket, leasedId, slot => slot with
                {
                    ObjectIdentity = slot.ObjectIdentity with
                    {
                        Generation = slot.ObjectIdentity.Generation + 1,
                    },
                }), rejectionCases);

            var foreignContext = NewContext(
                InventoryAnchor.B2, InventoryRole.Owner, "trusted-parent");
            var foreignPacket = NewPacket(foreignContext);
            Reject("foreign-coordinate-made-owner-local", foreignContext,
                ReplaceSlot(foreignPacket, "o:foreign-parent-obligation", slot => slot with
                {
                    Coordinate = slot.Coordinate with { Kind = InventoryCoordinateKind.Owned },
                    CoordinateOwner = slot.Holder,
                }), rejectionCases);
            var foreignSlot = foreignPacket.Manifest.Single(slot =>
                slot.LogicalSlotId == "o:foreign-parent-obligation");
            var foreignMasquerade = new InventoryCensusRow(
                foreignPacket.Target,
                foreignSlot.Coordinate,
                foreignSlot.LogicalSlotId,
                InventoryRowState.Observed,
                foreignSlot.ObjectIdentity,
                foreignSlot.Observation!.GrantedAccess,
                BasicFieldsValid: true,
                Inheritable: false,
                InheritanceFieldValid: true,
                "Directory",
                TypeNameValid: true,
                foreignSlot.ObjectIdentity.FinalPath,
                ObjectNameValid: true,
                CaptureMonotonicTicks: 150,
                CapturedAtUtc: foreignPacket.SampleStartedAtUtc.AddMilliseconds(1),
                InventoryMeasurementProvenance.Synthetic,
                "invalid O-local census claim for W coordinate");
            Reject("foreign-obligation-cannot-masquerade-as-owner-row", foreignContext,
                foreignPacket with { Rows = [foreignMasquerade] }, rejectionCases);

            var contextSubstitution = NewContext(
                InventoryAnchor.B2, InventoryRole.Controller, "frames");
            Reject("trusted-context-substitution", contextSubstitution, rootPacket, rejectionCases);

            var invalidRow = KnownRow(pB1Packet) with { BasicFieldsValid = false };
            Reject("invalid-census-field-flags", pB1,
                pB1Packet with { Rows = [invalidRow] }, rejectionCases);
            foreach (var unavailableProvenance in new[]
                     { InventoryMeasurementProvenance.Missing, InventoryMeasurementProvenance.Unsupported,
                         InventoryMeasurementProvenance.QueryFailed })
            {
                Reject("unavailable-row-provenance-with-measured-fields-" + unavailableProvenance,
                    pB1, pB1Packet with
                    {
                        Rows = [KnownRow(pB1Packet) with { Provenance = unavailableProvenance }],
                    }, rejectionCases);
            }
            Incomplete("unavailable-row-provenance-without-measured-fields", pB1,
                pB1Packet with
                {
                    Rows = [KnownRow(pB1Packet) with
                    {
                        Provenance = InventoryMeasurementProvenance.Missing,
                        GrantedAccess = null,
                        BasicFieldsValid = false,
                        Inheritable = null,
                        InheritanceFieldValid = false,
                    }],
                }, incompleteCases);
            var absentRow = KnownRow(pB1Packet) with
            {
                State = InventoryRowState.DemonstratedAbsent,
            };
            Reject("census-demonstrates-required-absence", pB1,
                pB1Packet with { Rows = [absentRow] }, rejectionCases);
            Reject("invalid-row-enum", pB1,
                pB1Packet with
                {
                    Rows = [KnownRow(pB1Packet) with { State = (InventoryRowState)99 }],
                }, rejectionCases);

            var invalidContext = pB1 with { TargetRole = (InventoryRole)99 };
            var invalidContextResult = InventoryContract.Reconcile(
                invalidContext, pB1Packet, new InventoryReplayState());
            Require(invalidContextResult.SemanticInventoryStatus == InventorySemanticStatus.Rejected &&
                    invalidContextResult.Rejections.Contains(
                        "trusted-context-invalid", StringComparer.Ordinal),
                "Invalid trusted context did not return a structured rejection.");
            rejectionCases.Add("invalid-trusted-context-structured-rejection");
            var aliasedProcesses = pB1.Processes.ToDictionary(
                item => item.Key,
                item => item.Value);
            aliasedProcesses[InventoryRole.Writer] = new(
                InventoryRole.Writer,
                pB1.Processes[InventoryRole.Owner].ProcessId,
                pB1.Processes[InventoryRole.Owner].CreationFileTime);
            var roleAliasResult = InventoryContract.Reconcile(
                pB1 with { Processes = aliasedProcesses },
                pB1Packet,
                new InventoryReplayState());
            Require(roleAliasResult.SemanticInventoryStatus == InventorySemanticStatus.Rejected &&
                    roleAliasResult.Rejections.Contains(
                        "trusted-context-invalid", StringComparer.Ordinal),
                "Trusted P/O/W process identity alias did not reject structurally.");
            rejectionCases.Add("trusted-context-process-role-alias");

            var framesContext = NewContext(
                InventoryAnchor.B2, InventoryRole.Controller, "frames");
            var framesPacket = NewPacket(framesContext);
            var framesRow = framesPacket.Rows.Single(row => row.LogicalSlotId == "p:frames");
            Reject("unlabeled-known-frames-write-rights", framesContext,
                framesPacket with
                {
                    Rows = [framesRow with
                    {
                        LogicalSlotId = null,
                        GrantedAccess = InventoryContract.WriterFileAccess,
                        ObjectIdentity = framesRow.ObjectIdentity! with
                        {
                            StableId = "forged-stable-id",
                            Resource = "forged-resource",
                            Name = "forged-name.bin",
                            FinalPath = @"C:\forged\forged-name.bin",
                        },
                    }],
                }, rejectionCases);
            var innerJobIdentity = new InventoryObjectIdentity(
                "o-inner-job", JobSlot: "o-inner-job");
            Reject("controller-cannot-hide-owner-inner-job", pB1,
                pB1Packet with
                {
                    Rows = [UnknownRow(pB1Packet, 88_888, InventoryRowState.Observed) with
                    {
                        ObjectIdentity = innerJobIdentity,
                        RawTypeName = "Job",
                        TypeNameValid = true,
                        RawObjectName = "o-inner-job",
                        ObjectNameValid = true,
                    }],
                }, rejectionCases);
            Reject("unavailable-row-cannot-hide-excess-rights", pB1,
                pB1Packet with
                {
                    Rows = [KnownRow(pB1Packet) with
                    {
                        State = InventoryRowState.Unsupported,
                        GrantedAccess = 0xFFFFFFFF,
                    }],
                }, rejectionCases);

            var unknownRow = UnknownRow(pB1Packet, 90_001, InventoryRowState.Unknown);
            var excessAndUnknown = ReplaceSlot(pB1Packet, knownId, slot => slot with
            {
                Observation = slot.Observation! with
                {
                    GrantedAccess = slot.Observation!.GrantedAccess!.Value | 0x80000000,
                },
            }) with
            { Rows = [unknownRow] };
            var rejectionWins = InventoryContract.Reconcile(
                pB1, excessAndUnknown, new InventoryReplayState());
            Require(rejectionWins.SemanticInventoryStatus == InventorySemanticStatus.Rejected,
                "Known rejection did not override an unknown census row.");
            rejectionCases.Add("rejection-overrides-unknown");

            var rejectedAdmissionState = new InventoryReplayState();
            var malformed = pB1Packet with { Stage = "wrong" };
            Require(InventoryContract.Reconcile(pB1, malformed, rejectedAdmissionState)
                    .SemanticInventoryStatus == InventorySemanticStatus.Rejected,
                "Malformed admission was not rejected.");
            var replayedFixed = InventoryContract.Reconcile(
                pB1, pB1Packet, rejectedAdmissionState);
            Require(replayedFixed.SemanticInventoryStatus == InventorySemanticStatus.Rejected &&
                    replayedFixed.Rejections.Contains("replay", StringComparer.Ordinal),
                "Rejected admission did not consume the one-use replay key.");
            rejectionCases.Add("rejected-admission-consumes-replay-key");

            Incomplete("missing-required-observation", pB1,
                ReplaceSlot(pB1Packet, knownId, slot => slot with { Observation = null }),
                incompleteCases);
            Incomplete("unknown-runtime-row", pB1,
                pB1Packet with { Rows = [unknownRow] }, incompleteCases);
            Incomplete("unsupported-row-observation", pB1,
                pB1Packet with
                {
                    Rows = [KnownRow(pB1Packet) with
                    {
                        State = InventoryRowState.Unsupported,
                        GrantedAccess = null,
                        BasicFieldsValid = false,
                        Inheritable = null,
                        InheritanceFieldValid = false,
                    }],
                }, incompleteCases);
            Incomplete("query-failed-row-observation", pB1,
                pB1Packet with
                {
                    Rows = [KnownRow(pB1Packet) with
                    {
                        State = InventoryRowState.QueryFailed,
                        GrantedAccess = null,
                        BasicFieldsValid = false,
                        Inheritable = null,
                        InheritanceFieldValid = false,
                    }],
                }, incompleteCases);
            Incomplete("temporal-churn", pB1,
                pB1Packet with { TemporalChurnObserved = true }, incompleteCases);
            Incomplete("collection-error", pB1,
                pB1Packet with
                {
                    CollectionTerminal = InventoryCollectionTerminal.Error,
                    CollectionError = "synthetic query denial",
                }, incompleteCases);
            Incomplete("resource-release-unknown", pB1,
                pB1Packet with { ResourceReleaseStatus = InventoryReleaseStatus.Unknown },
                incompleteCases);
            Incomplete("field-byte-limit", pB1,
                pB1Packet with { CollectionError = new string('x', InventoryContract.MaxFieldBytes + 1) },
                incompleteCases);

            var limitRows = Enumerable.Range(0, InventoryContract.MaxRows + 1)
                .Select(index => UnknownRow(
                    pB1Packet, 100_000L + index, InventoryRowState.Unknown))
                .ToArray();
            var rowLimitResult = InventoryContract.Reconcile(
                pB1,
                pB1Packet with { Rows = limitRows },
                new InventoryReplayState());
            Require(rowLimitResult.SemanticInventoryStatus == InventorySemanticStatus.Incomplete &&
                    rowLimitResult.RawRowCount == InventoryContract.MaxRows + 1 &&
                    rowLimitResult.InspectedRowCount == InventoryContract.MaxRows &&
                    rowLimitResult.UninspectedRowCount == 1 &&
                    rowLimitResult.IncompleteReasons.Contains(
                        "row-limit-exceeded", StringComparer.Ordinal),
                "Row limit did not preserve explicit raw/inspected/uninspected counts.");
            incompleteCases.Add("row-count-limit");

            var encodedRows = Enumerable.Range(0, 130)
                .Select(index => UnknownRow(
                    pB1Packet,
                    200_000L + index,
                    InventoryRowState.Unknown,
                    new string('e', InventoryContract.MaxFieldBytes)))
                .ToArray();
            var encodedLimitResult = InventoryContract.Reconcile(
                pB1,
                pB1Packet with { Rows = encodedRows },
                new InventoryReplayState());
            Require(encodedLimitResult.SemanticInventoryStatus == InventorySemanticStatus.Incomplete &&
                    encodedLimitResult.EncodedByteLimitExceeded &&
                    encodedLimitResult.EncodedBytesObserved >
                        InventoryContract.MaxEncodedPacketBytes &&
                    encodedLimitResult.IncompleteReasons.Contains(
                        "encoded-packet-limit-exceeded", StringComparer.Ordinal),
                "Capped encoding did not report the actual byte-limit crossing.");
            incompleteCases.Add("encoded-packet-byte-limit");

            Incomplete("zero-and-pseudo-coordinates", pB1,
                pB1Packet with
                {
                    Rows =
                    [
                        UnknownRow(pB1Packet, 0, InventoryRowState.Unknown) with
                        {
                            Coordinate = new(InventoryCoordinateKind.Zero, 0),
                        },
                        UnknownRow(pB1Packet, -2, InventoryRowState.Unknown) with
                        {
                            Coordinate = new(InventoryCoordinateKind.Pseudo, -2),
                        },
                    ],
                }, incompleteCases);

            var wB0 = NewContext(InventoryAnchor.B0, InventoryRole.Writer, "trusted-parent");
            var wB0Packet = NewPacket(wB0);
            var diagnosticOnly = InventoryContract.Reconcile(
                wB0,
                wB0Packet with { Rows = [UnknownRow(wB0Packet, 300_000, InventoryRowState.Unknown)] },
                new InventoryReplayState());
            Require(diagnosticOnly.SemanticInventoryStatus == InventorySemanticStatus.Incomplete &&
                    diagnosticOnly.KnownCapabilityChecksPassed,
                "Diagnostic-only uncertainty erased vacuously satisfied known W B0 checks.");
            incompleteCases.Add("diagnostic-unknown-preserves-known-checks");

            var receipt = new
            {
                schema = "watchdog.ow-handle-inventory-contract-checks.v1",
                succeeded = true,
                snapshotCollectionCompleted = false,
                semanticInventoryCompleteOrAdmitted = false,
                positiveChecks = positiveCases.Count,
                rejectionChecks = rejectionCases.Count,
                incompleteChecks = incompleteCases.Count,
                positiveCases,
                rejectionCases,
                incompleteCases,
                nativeAuthorityProven = false,
                nativeDurabilityProven = false,
                completeHandleInventoryProven = false,
                zeroExtraInheritanceProven = false,
                trustedBaseAdmissionProven = false,
                productionAdmissionGranted = false,
                b1WriteReleaseIssued = false,
                b1JournalWritesPerformed = false,
            };
            Console.WriteLine(JsonSerializer.Serialize(receipt, JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            var receipt = new
            {
                schema = "watchdog.ow-handle-inventory-contract-checks.v1",
                succeeded = false,
                snapshotCollectionCompleted = false,
                semanticInventoryCompleteOrAdmitted = false,
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

    private static void RequireGoldenCatalog()
    {
        static IReadOnlyList<InventorySlotExpectation> Catalog(
            InventoryAnchor anchor,
            InventoryRole role,
            string resource,
            bool client = true) =>
            InventoryContract.DeriveRequiredSlots(
                NewContext(anchor, role, resource, client));

        Require(Catalog(InventoryAnchor.B0, InventoryRole.Controller, "trusted-parent").Count == 4,
            "P B0 golden slot count drifted.");
        Require(Catalog(InventoryAnchor.B1, InventoryRole.Controller, "trusted-parent").Count == 8,
            "P B1 golden slot count drifted.");
        Require(Catalog(InventoryAnchor.B2, InventoryRole.Controller, "trusted-parent").Count == 8,
            "P B2 parent golden slot count drifted.");
        Require(Catalog(InventoryAnchor.B2, InventoryRole.Controller, "frames").Count == 11,
            "P B2 read golden slot count drifted.");
        Require(Catalog(InventoryAnchor.B0, InventoryRole.Owner, "trusted-parent").Count == 9,
            "O B0 golden slot count drifted.");
        Require(Catalog(
                InventoryAnchor.B0, InventoryRole.Owner, "trusted-parent", client: false).Count == 8,
            "O B0 no-client trusted sub-anchor count drifted.");
        Require(Catalog(InventoryAnchor.B2, InventoryRole.Owner, "trusted-parent").Count == 10,
            "O B2 parent golden slot count drifted.");
        Require(Catalog(InventoryAnchor.B2, InventoryRole.Owner, "private-root").Count == 11,
            "O B2 root golden slot count drifted.");
        Require(Catalog(InventoryAnchor.B2, InventoryRole.Owner, "frames").Count == 12,
            "O B2 frames golden slot count drifted.");
        Require(Catalog(InventoryAnchor.B2, InventoryRole.Owner, "anchor").Count == 13,
            "O B2 anchor golden slot count drifted.");
        Require(Catalog(InventoryAnchor.B0, InventoryRole.Writer, "trusted-parent").Count == 0 &&
                Catalog(InventoryAnchor.B1, InventoryRole.Writer, "trusted-parent").Count == 0,
            "W suspended anchors unexpectedly gained required fixture slots.");
        Require(Catalog(InventoryAnchor.B2, InventoryRole.Writer, "trusted-parent").Count == 2,
            "W B2 parent golden slot count drifted.");
        Require(Catalog(InventoryAnchor.B2, InventoryRole.Writer, "frames").Count == 5,
            "W B2 read golden slot count drifted.");

        var pRead = Catalog(InventoryAnchor.B2, InventoryRole.Controller, "anchor");
        Require(pRead.Single(item => item.LogicalSlotId == "p:w-process-retained").RequestedAccess ==
                    0x00101041 &&
                pRead.Single(item => item.LogicalSlotId == "p:trusted-parent").RequestedAccess ==
                    0x001200A5 &&
                pRead.Single(item => item.LogicalSlotId == "p:private-root").RequestedAccess ==
                    0x001200A1 &&
                pRead.Single(item => item.LogicalSlotId == "p:frames").RequestedAccess ==
                    0x00120081,
            "P golden capability masks drifted.");
        var wRead = Catalog(InventoryAnchor.B2, InventoryRole.Writer, "anchor");
        Require(wRead.Single(item => item.LogicalSlotId == "w:private-root").RequestedAccess ==
                    0x001200A3 &&
                wRead.Single(item => item.LogicalSlotId == "w:frames").RequestedAccess ==
                    0x00120187,
            "W golden writable capability masks drifted.");
        var oRead = Catalog(InventoryAnchor.B2, InventoryRole.Owner, "anchor");
        Require(oRead.Where(item => item.LogicalSlotId is
                    "o:private-root" or "o:frames" or "o:anchor")
                .All(item => item.Creator == InventoryRole.Writer &&
                             item.InvokingDuplicator == InventoryRole.Owner &&
                             item.Holder.Role == InventoryRole.Owner &&
                             item.Target == InventoryRole.Owner),
            "O read creator/invoker/holder/target roles drifted.");
        Require(oRead.Single(item => item.LogicalSlotId == "o:foreign-parent-obligation")
                    .CoordinateKind == InventoryCoordinateKind.Foreign &&
                oRead.Single(item => item.LogicalSlotId == "o:foreign-parent-obligation")
                    .Lifecycle == InventoryLifecycle.Active,
            "O post-ACK foreign parent obligation drifted.");
        var unresolved = Catalog(InventoryAnchor.B0, InventoryRole.Owner, "trusted-parent")
            .Where(item => item.ObjectClass is InventoryObjectClass.Process or
                InventoryObjectClass.Thread or InventoryObjectClass.Job or
                InventoryObjectClass.CustodyFile or InventoryObjectClass.Pipe);
        Require(unresolved.All(item => item.RequestedAccess is null && item.AllowedAccess is null),
            "Golden source catalog invented a birth/job/custody/pipe rights allowlist.");
    }

    private static void RequireValid(
        InventoryTrustedContext context,
        InventoryPacket packet)
    {
        var result = InventoryContract.Reconcile(context, packet, new InventoryReplayState());
        Require(result.ManifestValidated, "Valid manifest was not validated.");
        Require(result.SemanticInventoryStatus != InventorySemanticStatus.Rejected,
            "Valid synthetic packet was rejected: " + string.Join(",", result.Rejections));
        Require(!result.SnapshotCollectionCompleted && !result.CompleteHandleInventoryProven &&
                !result.ProductionAdmissionGranted && !result.B1WriteReleaseIssued,
            "Valid synthetic packet widened an evidence or release claim.");
    }

    private static void Reject(
        string name,
        InventoryTrustedContext context,
        InventoryPacket packet,
        List<string> cases)
    {
        var result = InventoryContract.Reconcile(context, packet, new InventoryReplayState());
        Require(result.SemanticInventoryStatus == InventorySemanticStatus.Rejected,
            $"{name} was not rejected.");
        cases.Add(name);
    }

    private static void Incomplete(
        string name,
        InventoryTrustedContext context,
        InventoryPacket packet,
        List<string> cases,
        string? requiredReason = null)
    {
        var result = InventoryContract.Reconcile(context, packet, new InventoryReplayState());
        Require(result.SemanticInventoryStatus == InventorySemanticStatus.Incomplete,
            $"{name} was not incomplete: {string.Join(',', result.Rejections)}");
        Require(result.Rejections.Count == 0, $"{name} unexpectedly rejected.");
        if (requiredReason is not null)
        {
            Require(result.IncompleteReasons.Contains(requiredReason, StringComparer.Ordinal),
                $"{name} did not report {requiredReason}.");
        }
        cases.Add(name);
    }

    private static InventoryTrustedContext NewContext(
        InventoryAnchor anchor,
        InventoryRole targetRole,
        string resource,
        bool requireOwnerControllerClient = true)
    {
        var processes = new Dictionary<InventoryRole, InventoryProcessIdentity>
        {
            [InventoryRole.Controller] = new(InventoryRole.Controller, 1001, 10_001),
            [InventoryRole.Owner] = new(InventoryRole.Owner, 1002, 10_002),
            [InventoryRole.Writer] = new(InventoryRole.Writer, 1003, 10_003),
        };
        var threads = new Dictionary<InventoryRole, uint>
        {
            [InventoryRole.Controller] = 2001,
            [InventoryRole.Owner] = 2002,
            [InventoryRole.Writer] = 2003,
        };
        var leases = new Dictionary<string, Guid>(StringComparer.Ordinal)
        {
            ["private-root"] = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ["frames"] = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ["anchor"] = Guid.Parse("33333333-3333-3333-3333-333333333333"),
        };
        var fixture = new Dictionary<string, InventoryFixtureIdentity>(StringComparer.Ordinal);
        var names = new[]
            { "fixture-base", "run-root", "trusted-parent", "private-root", "frames", "anchor" };
        for (var index = 0; index < names.Length; index++)
        {
            var name = names[index];
            var directory = index < 4;
            fixture[name] = new(
                name,
                directory ? name : name + ".bin",
                directory
                    ? $@"C:\inventory\{name}"
                    : $@"C:\inventory\private-root\{name}.bin",
                77,
                (index + 1).ToString("X32"),
                directory,
                false,
                leases.TryGetValue(name, out var lease) ? lease : null,
                leases.ContainsKey(name) ? 1 : null);
        }
        return new(
            Guid.Parse("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"),
            Guid.Parse("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"),
            "owner-death-" + resource,
            resource,
            anchor,
            1,
            processes[InventoryRole.Controller],
            targetRole,
            anchor switch
            {
                InventoryAnchor.B0 => "B0-w-born-suspended",
                InventoryAnchor.B1 => "B1-prearm-accepted-suspended",
                _ => "B2-selected-before-ack",
            },
            requireOwnerControllerClient,
            processes,
            threads,
            fixture);
    }

    private static InventoryPacket NewPacket(InventoryTrustedContext context)
    {
        var expectations = InventoryContract.DeriveRequiredSlots(context);
        var coordinate = 10_000L;
        var manifest = expectations.Select(expected =>
        {
            var observation = expected.AllowedAccess is null
                ? null
                : new InventoryCapabilityObservation(
                    expected.AllowedAccess,
                    AccessFieldValid: true,
                    Inheritable: false,
                    InheritanceFieldValid: true,
                    InventoryMeasurementProvenance.Synthetic,
                    "synthetic-contract-check");
            return new InventoryManifestSlot(
                expected.LogicalSlotId,
                expected.Holder,
                expected.Creator,
                expected.InvokingDuplicator,
                expected.Target,
                expected.ObjectClass,
                new(expected.CoordinateKind, coordinate++),
                expected.CoordinateOwner,
                expected.ObjectIdentity,
                expected.RequestedAccess,
                expected.Classification,
                InventoryPresence.Present,
                expected.Lifecycle,
                observation);
        }).ToArray();

        var started = DateTimeOffset.UnixEpoch.AddDays(1);
        var rows = manifest.Where(slot => slot.Observation is not null &&
                                         slot.Coordinate.Kind != InventoryCoordinateKind.Foreign)
            .Select(slot => new InventoryCensusRow(
                slot.Holder,
                slot.Coordinate,
                slot.LogicalSlotId,
                InventoryRowState.Observed,
                slot.ObjectIdentity,
                slot.Observation!.GrantedAccess,
                BasicFieldsValid: true,
                Inheritable: false,
                InheritanceFieldValid: true,
                slot.ObjectClass.ToString(),
                TypeNameValid: true,
                slot.ObjectIdentity.FinalPath ?? slot.ObjectIdentity.StableId,
                ObjectNameValid: true,
                CaptureMonotonicTicks: 150,
                CapturedAtUtc: started.AddMilliseconds(1),
                InventoryMeasurementProvenance.Synthetic,
                "synthetic expected slot"))
            .ToArray();
        return new(
            InventoryContract.Schema,
            context.RunId,
            context.SessionId,
            context.Scenario,
            context.SelectedResource,
            context.Anchor,
            context.Attempt,
            context.Collector,
            context.Processes[context.TargetRole],
            context.ExpectedStage,
            started,
            started.AddMilliseconds(2),
            100,
            200,
            TargetLiveBefore: true,
            TargetLiveAfter: true,
            SnapshotCollectionCompleted: false,
            InventoryCollectionTerminal.NotAttempted,
            CollectionError: null,
            TemporalChurnObserved: false,
            InventoryReleaseStatus.NotApplicable,
            InventoryMeasurementProvenance.Synthetic,
            expectations.Select(item => item.LogicalSlotId).ToArray(),
            manifest,
            rows,
            new());
    }

    private static InventoryPacket ReplaceSlot(
        InventoryPacket packet,
        string logicalSlotId,
        Func<InventoryManifestSlot, InventoryManifestSlot> replace) =>
        packet with
        {
            Manifest = packet.Manifest.Select(slot =>
                    slot.LogicalSlotId == logicalSlotId ? replace(slot) : slot)
                .ToArray(),
        };

    private static InventoryCensusRow KnownRow(InventoryPacket packet) =>
        packet.Rows.First();

    private static InventoryCensusRow UnknownRow(
        InventoryPacket packet,
        long coordinate,
        InventoryRowState state,
        string? reason = "synthetic unresolved row") =>
        new(
            packet.Target,
            new(InventoryCoordinateKind.Owned, coordinate),
            LogicalSlotId: null,
            state,
            ObjectIdentity: null,
            GrantedAccess: null,
            BasicFieldsValid: false,
            Inheritable: null,
            InheritanceFieldValid: false,
            RawTypeName: null,
            TypeNameValid: false,
            RawObjectName: null,
            ObjectNameValid: false,
            CaptureMonotonicTicks: 150,
            CapturedAtUtc: packet.SampleStartedAtUtc.AddMilliseconds(1),
            InventoryMeasurementProvenance.Synthetic,
            reason);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
}
