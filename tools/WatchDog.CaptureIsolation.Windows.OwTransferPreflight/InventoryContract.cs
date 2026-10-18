using System.Text;
using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal enum InventoryRole
{
    Controller,
    Owner,
    Writer,
}

internal enum InventoryAnchor
{
    B0,
    B1,
    B2,
}

internal enum InventoryObjectClass
{
    Directory,
    File,
    Process,
    Thread,
    Job,
    Pipe,
    CustodyFile,
    ForeignObligation,
}

internal enum InventoryCoordinateKind
{
    Owned,
    Foreign,
    Zero,
    Invalid,
    Pseudo,
}

internal enum InventoryPresence
{
    Present,
    Unknown,
    DemonstratedAbsent,
}

internal enum InventoryLifecycle
{
    Active,
    PendingAck,
}

internal enum InventoryCapabilityClass
{
    Unknown,
    ReadOnly,
    WriteCapable,
    ProcessControl,
}

internal enum InventoryMeasurementProvenance
{
    Missing,
    Synthetic,
    NativeQuery,
    Unsupported,
    QueryFailed,
}

internal enum InventoryRowState
{
    Observed,
    Unknown,
    Unsupported,
    QueryFailed,
    DemonstratedAbsent,
}

internal enum InventoryCollectionTerminal
{
    NotAttempted,
    Exhausted,
    Error,
    Unsupported,
    LimitReached,
}

internal enum InventoryReleaseStatus
{
    NotApplicable,
    Completed,
    Unknown,
    Failed,
}

internal enum InventorySemanticStatus
{
    NotAttempted,
    Incomplete,
    Rejected,
}

internal sealed record InventoryProcessIdentity(
    InventoryRole Role,
    int ProcessId,
    long CreationFileTime);

internal sealed record InventoryFixtureIdentity(
    string Resource,
    string Name,
    string FinalPath,
    ulong VolumeSerialNumber,
    string FileId,
    bool IsDirectory,
    bool IsReparsePoint,
    Guid? LeaseId,
    long? Generation);

internal sealed record InventoryObjectIdentity(
    string StableId,
    string? Resource = null,
    string? Name = null,
    string? FinalPath = null,
    ulong? VolumeSerialNumber = null,
    string? FileId = null,
    bool? IsDirectory = null,
    bool? IsReparsePoint = null,
    int? TargetProcessId = null,
    long? TargetCreationFileTime = null,
    uint? ThreadId = null,
    string? JobSlot = null,
    Guid? LeaseId = null,
    long? Generation = null);

internal sealed record InventoryCoordinate(
    InventoryCoordinateKind Kind,
    long? Value);

internal sealed record InventoryCapabilityObservation(
    uint? GrantedAccess,
    bool AccessFieldValid,
    bool? Inheritable,
    bool InheritanceFieldValid,
    InventoryMeasurementProvenance Provenance,
    string? Method);

internal sealed record InventoryManifestSlot(
    string LogicalSlotId,
    InventoryProcessIdentity Holder,
    InventoryRole Creator,
    InventoryRole? InvokingDuplicator,
    InventoryRole? Target,
    InventoryObjectClass ObjectClass,
    InventoryCoordinate Coordinate,
    InventoryProcessIdentity CoordinateOwner,
    InventoryObjectIdentity ObjectIdentity,
    uint? RequestedAccess,
    InventoryCapabilityClass Classification,
    InventoryPresence Presence,
    InventoryLifecycle Lifecycle,
    InventoryCapabilityObservation? Observation);

internal sealed record InventoryCensusRow(
    InventoryProcessIdentity Holder,
    InventoryCoordinate Coordinate,
    string? LogicalSlotId,
    InventoryRowState State,
    InventoryObjectIdentity? ObjectIdentity,
    uint? GrantedAccess,
    bool BasicFieldsValid,
    bool? Inheritable,
    bool InheritanceFieldValid,
    string? RawTypeName,
    bool TypeNameValid,
    string? RawObjectName,
    bool ObjectNameValid,
    long CaptureMonotonicTicks,
    DateTimeOffset? CapturedAtUtc,
    InventoryMeasurementProvenance Provenance,
    string? ClassificationReason);

internal sealed record InventoryProofClaims(
    bool NativeAuthorityProven = false,
    bool NativeDurabilityProven = false,
    bool CompleteHandleInventoryProven = false,
    bool ZeroExtraInheritanceProven = false,
    bool TrustedBaseAdmissionProven = false,
    bool ProductionAdmissionGranted = false,
    bool B1WriteReleaseIssued = false,
    bool B1JournalWritesPerformed = false);

internal sealed record InventoryPacket(
    string Schema,
    Guid RunId,
    Guid SessionId,
    string Scenario,
    string SelectedResource,
    InventoryAnchor Anchor,
    int Attempt,
    InventoryProcessIdentity Collector,
    InventoryProcessIdentity Target,
    string Stage,
    DateTimeOffset SampleStartedAtUtc,
    DateTimeOffset SampleEndedAtUtc,
    long SampleStartedMonotonicTicks,
    long SampleEndedMonotonicTicks,
    bool TargetLiveBefore,
    bool TargetLiveAfter,
    bool SnapshotCollectionCompleted,
    InventoryCollectionTerminal CollectionTerminal,
    string? CollectionError,
    bool TemporalChurnObserved,
    InventoryReleaseStatus ResourceReleaseStatus,
    InventoryMeasurementProvenance PacketProvenance,
    IReadOnlyList<string> DeclaredRequiredSlotIds,
    IReadOnlyList<InventoryManifestSlot> Manifest,
    IReadOnlyList<InventoryCensusRow> Rows,
    InventoryProofClaims ProofClaims);

internal sealed record InventoryTrustedContext(
    Guid RunId,
    Guid SessionId,
    string Scenario,
    string SelectedResource,
    InventoryAnchor Anchor,
    int Attempt,
    InventoryProcessIdentity Collector,
    InventoryRole TargetRole,
    string ExpectedStage,
    bool RequireOwnerControllerClient,
    IReadOnlyDictionary<InventoryRole, InventoryProcessIdentity> Processes,
    IReadOnlyDictionary<InventoryRole, uint> PrimaryThreadIds,
    IReadOnlyDictionary<string, InventoryFixtureIdentity> FixtureIdentities,
    bool SyntheticOnly = true);

internal sealed record InventorySlotExpectation(
    string LogicalSlotId,
    InventoryProcessIdentity Holder,
    InventoryRole Creator,
    InventoryRole? InvokingDuplicator,
    InventoryRole? Target,
    InventoryObjectClass ObjectClass,
    InventoryCoordinateKind CoordinateKind,
    InventoryProcessIdentity CoordinateOwner,
    InventoryObjectIdentity ObjectIdentity,
    uint? RequestedAccess,
    uint? AllowedAccess,
    InventoryCapabilityClass Classification,
    InventoryLifecycle Lifecycle);

internal sealed record InventoryReconciliationResult(
    bool ManifestValidated,
    bool KnownCapabilityChecksPassed,
    bool SnapshotCollectionCompleted,
    InventorySemanticStatus SemanticInventoryStatus,
    IReadOnlyList<string> Rejections,
    IReadOnlyList<string> IncompleteReasons,
    int RawRowCount,
    int InspectedRowCount,
    int UninspectedRowCount,
    long? EncodedBytesObserved,
    bool EncodedByteLimitExceeded,
    bool NativeAuthorityProven = false,
    bool NativeDurabilityProven = false,
    bool CompleteHandleInventoryProven = false,
    bool ZeroExtraInheritanceProven = false,
    bool TrustedBaseAdmissionProven = false,
    bool ProductionAdmissionGranted = false,
    bool B1WriteReleaseIssued = false,
    bool B1JournalWritesPerformed = false);

internal sealed class InventoryReplayState
{
    private readonly HashSet<InventoryAdmissionKey> _consumed = [];

    internal bool TryConsume(InventoryTrustedContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var key = new InventoryAdmissionKey(
            context.RunId,
            context.SessionId,
            context.Scenario,
            context.SelectedResource,
            context.Anchor,
            context.Attempt,
            context.Collector,
            context.TargetRole,
            context.ExpectedStage);
        lock (_consumed)
            return _consumed.Add(key);
    }

    private sealed record InventoryAdmissionKey(
        Guid RunId,
        Guid SessionId,
        string Scenario,
        string SelectedResource,
        InventoryAnchor Anchor,
        int Attempt,
        InventoryProcessIdentity Collector,
        InventoryRole TargetRole,
        string Stage);
}

internal static class InventoryContract
{
    internal const string Schema = "watchdog.ow-handle-inventory-contract.v1";
    internal const int MaxAttempts = 3;
    internal const int MaxRows = 16_384;
    internal const int MaxManifestSlots = 64;
    internal const int MaxDeclaredSlots = 64;
    internal const int MaxFieldBytes = 64 * 1024;
    internal const int MaxEncodedPacketBytes = 8 * 1024 * 1024;

    internal const uint WritableDirectoryAccess = 0x001200A5;
    internal const uint RootReadAccess = 0x001200A1;
    internal const uint FileReadAccess = 0x00120081;
    internal const uint WriterRootAccess = 0x001200A3;
    internal const uint WriterFileAccess = 0x00120187;
    internal const uint RetainedWriterProcessAccess = 0x00101041;

    private static readonly string[] Resources =
        ["trusted-parent", "private-root", "frames", "anchor"];

    internal static InventoryReconciliationResult Reconcile(
        InventoryTrustedContext context,
        InventoryPacket packet,
        InventoryReplayState replayState)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(replayState);

        context = SnapshotContext(context);
        var rawRowCount = packet.Rows.Count;
        var rawManifestCount = packet.Manifest.Count;
        var rawDeclaredCount = packet.DeclaredRequiredSlotIds.Count;
        var inspectedRowCount = Math.Min(rawRowCount, MaxRows);
        packet = packet with
        {
            DeclaredRequiredSlotIds = SnapshotBounded(
                packet.DeclaredRequiredSlotIds, MaxDeclaredSlots),
            Manifest = SnapshotBounded(packet.Manifest, MaxManifestSlots),
            Rows = SnapshotBounded(packet.Rows, MaxRows),
        };
        var rejections = new List<string>();
        var incomplete = new List<string>();
        var manifestRejected = false;
        var knownChecksPassed = true;
        long? encodedBytesObserved = null;
        var encodedByteLimitExceeded = false;

        void Reject(string code, bool affectsManifest = true)
        {
            if (!rejections.Contains(code, StringComparer.Ordinal))
                rejections.Add(code);
            manifestRejected |= affectsManifest;
            knownChecksPassed = false;
        }

        void Incomplete(string code, bool affectsKnown = false)
        {
            if (!incomplete.Contains(code, StringComparer.Ordinal))
                incomplete.Add(code);
            knownChecksPassed &= !affectsKnown;
        }

        if (!replayState.TryConsume(context))
            Reject("replay", affectsManifest: false);

        IReadOnlyList<InventorySlotExpectation> expected;
        try
        {
            ValidateTrustedContext(context);
            expected = DeriveRequiredSlots(context);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            Reject("trusted-context-invalid");
            return NewResult(
                false,
                false,
                InventorySemanticStatus.Rejected,
                rejections,
                incomplete,
                rawRowCount,
                inspectedRowCount,
                encodedBytesObserved,
                encodedByteLimitExceeded);
        }

        ValidatePacketBinding(context, packet, Reject);
        ValidateBounds(
            packet,
            rawRowCount,
            rawManifestCount,
            rawDeclaredCount,
            Incomplete,
            Reject,
            out encodedBytesObserved,
            out encodedByteLimitExceeded);
        ValidateCollectionState(context, packet, Incomplete, Reject);

        var expectedIds = expected.Select(item => item.LogicalSlotId)
            .Order(StringComparer.Ordinal).ToArray();
        var declaredIds = packet.DeclaredRequiredSlotIds
            .Order(StringComparer.Ordinal).ToArray();
        if (!expectedIds.SequenceEqual(declaredIds, StringComparer.Ordinal))
            Reject("packet-required-slot-substitution");

        var manifestById = new Dictionary<string, InventoryManifestSlot>(StringComparer.Ordinal);
        foreach (var slot in packet.Manifest)
        {
            if (string.IsNullOrWhiteSpace(slot.LogicalSlotId) ||
                !manifestById.TryAdd(slot.LogicalSlotId, slot))
            {
                Reject("duplicate-or-empty-logical-slot");
            }
        }
        if (manifestById.Keys.Any(id => !expectedIds.Contains(id, StringComparer.Ordinal)))
            Reject("unexpected-manifest-slot");

        foreach (var expectation in expected)
        {
            if (!manifestById.TryGetValue(expectation.LogicalSlotId, out var slot))
            {
                Reject("missing-required-slot:" + expectation.LogicalSlotId);
                continue;
            }
            ValidateManifestSlot(expectation, slot, Reject, Incomplete);
        }

        ValidateManifestCoordinateClaims(packet.Manifest, Reject);
        var knownCatalog = Enum.GetValues<InventoryRole>()
            .SelectMany(role => DeriveRequiredSlots(context with { TargetRole = role }))
            .ToArray();
        ValidateRows(packet, manifestById, knownCatalog, Incomplete, Reject);

        if (packet.ProofClaims.NativeAuthorityProven ||
            packet.ProofClaims.NativeDurabilityProven ||
            packet.ProofClaims.CompleteHandleInventoryProven ||
            packet.ProofClaims.ZeroExtraInheritanceProven ||
            packet.ProofClaims.TrustedBaseAdmissionProven ||
            packet.ProofClaims.ProductionAdmissionGranted ||
            packet.ProofClaims.B1WriteReleaseIssued ||
            packet.ProofClaims.B1JournalWritesPerformed)
        {
            Reject("unsupported-proof-or-release-claim", affectsManifest: false);
        }

        var status = rejections.Count > 0
            ? InventorySemanticStatus.Rejected
            : packet.CollectionTerminal == InventoryCollectionTerminal.NotAttempted &&
              packet.Rows.Count == 0
                ? InventorySemanticStatus.NotAttempted
                : InventorySemanticStatus.Incomplete;
        return NewResult(
            !manifestRejected,
            knownChecksPassed,
            status,
            rejections,
            incomplete,
            rawRowCount,
            inspectedRowCount,
            encodedBytesObserved,
            encodedByteLimitExceeded);
    }

    internal static IReadOnlyList<InventorySlotExpectation> DeriveRequiredSlots(
        InventoryTrustedContext context)
    {
        ValidateTrustedContext(context);
        var slots = new List<InventorySlotExpectation>();
        var p = Process(context, InventoryRole.Controller);
        var o = Process(context, InventoryRole.Owner);
        var w = Process(context, InventoryRole.Writer);
        var target = Process(context, context.TargetRole);

        void Add(
            string id,
            InventoryRole creator,
            InventoryRole? invoking,
            InventoryRole? targetRole,
            InventoryObjectClass objectClass,
            InventoryCoordinateKind coordinateKind,
            InventoryProcessIdentity coordinateOwner,
            InventoryObjectIdentity identity,
            uint? requested,
            uint? allowed,
            InventoryCapabilityClass classification,
            InventoryLifecycle lifecycle = InventoryLifecycle.Active)
        {
            slots.Add(new(
                id, target, creator, invoking, targetRole, objectClass, coordinateKind,
                coordinateOwner, identity, requested, allowed, classification, lifecycle));
        }

        if (context.TargetRole == InventoryRole.Controller)
        {
            Add("p:o-process-original", InventoryRole.Controller, null, InventoryRole.Owner,
                InventoryObjectClass.Process, InventoryCoordinateKind.Owned, p,
                ProcessObject("o-process", o), null, null, InventoryCapabilityClass.Unknown);
            Add("p:o-primary-thread-original", InventoryRole.Controller, null, InventoryRole.Owner,
                InventoryObjectClass.Thread, InventoryCoordinateKind.Owned, p,
                ThreadObject("o-primary-thread", o, context.PrimaryThreadIds[InventoryRole.Owner]),
                null, null, InventoryCapabilityClass.Unknown);
            Add("p:outer-job", InventoryRole.Controller, null, InventoryRole.Owner,
                InventoryObjectClass.Job, InventoryCoordinateKind.Owned, p,
                JobObject("p-outer-job"), null, null, InventoryCapabilityClass.Unknown);
            Add("p:po-server", InventoryRole.Controller, null, InventoryRole.Owner,
                InventoryObjectClass.Pipe, InventoryCoordinateKind.Owned, p,
                StableObject("p-po-server"), null, null, InventoryCapabilityClass.Unknown);

            if (context.Anchor >= InventoryAnchor.B1)
            {
                Add("p:w-process-retained", InventoryRole.Owner, InventoryRole.Controller,
                    InventoryRole.Controller, InventoryObjectClass.Process,
                    InventoryCoordinateKind.Owned, p, ProcessObject("w-process", w),
                    RetainedWriterProcessAccess, RetainedWriterProcessAccess,
                    InventoryCapabilityClass.ProcessControl);
                AddFixtureCopies(slots, context, target, p, "p", InventoryRole.Owner,
                    InventoryRole.Controller, InventoryRole.Controller,
                    ["fixture-base", "run-root", "trusted-parent"], WritableDirectoryAccess,
                    InventoryCapabilityClass.WriteCapable);
            }
            if (context.Anchor == InventoryAnchor.B2 &&
                context.SelectedResource != "trusted-parent")
            {
                AddFixtureCopies(slots, context, target, p, "p", InventoryRole.Writer,
                    InventoryRole.Controller, InventoryRole.Controller,
                    ["private-root"], RootReadAccess, InventoryCapabilityClass.ReadOnly);
                AddFixtureCopies(slots, context, target, p, "p", InventoryRole.Writer,
                    InventoryRole.Controller, InventoryRole.Controller,
                    ["frames", "anchor"], FileReadAccess, InventoryCapabilityClass.ReadOnly);
            }
        }
        else if (context.TargetRole == InventoryRole.Owner)
        {
            Add("o:w-process-original", InventoryRole.Owner, null, InventoryRole.Writer,
                InventoryObjectClass.Process, InventoryCoordinateKind.Owned, o,
                ProcessObject("w-process", w), null, null, InventoryCapabilityClass.Unknown);
            Add("o:w-primary-thread-original", InventoryRole.Owner, null, InventoryRole.Writer,
                InventoryObjectClass.Thread, InventoryCoordinateKind.Owned, o,
                ThreadObject("w-primary-thread", w, context.PrimaryThreadIds[InventoryRole.Writer]),
                null, null, InventoryCapabilityClass.Unknown);
            Add("o:inner-job", InventoryRole.Owner, null, InventoryRole.Writer,
                InventoryObjectClass.Job, InventoryCoordinateKind.Owned, o,
                JobObject("o-inner-job"), null, null, InventoryCapabilityClass.Unknown);
            AddFixtureCopies(slots, context, target, o, "o", InventoryRole.Owner, null, null,
                ["fixture-base", "run-root", "trusted-parent"], WritableDirectoryAccess,
                InventoryCapabilityClass.WriteCapable);
            Add("o:custody-file", InventoryRole.Owner, null, null,
                InventoryObjectClass.CustodyFile, InventoryCoordinateKind.Owned, o,
                StableObject("o-custody-file"), null, null, InventoryCapabilityClass.Unknown);
            Add("o:ow-server", InventoryRole.Owner, null, InventoryRole.Writer,
                InventoryObjectClass.Pipe, InventoryCoordinateKind.Owned, o,
                StableObject("o-ow-server"), null, null, InventoryCapabilityClass.Unknown);
            if (context.RequireOwnerControllerClient)
            {
                Add("o:po-client", InventoryRole.Owner, null, InventoryRole.Controller,
                    InventoryObjectClass.Pipe, InventoryCoordinateKind.Owned, o,
                    StableObject("o-po-client"), null, null, InventoryCapabilityClass.Unknown);
            }
            if (context.Anchor == InventoryAnchor.B2)
            {
                Add("o:foreign-parent-obligation", InventoryRole.Owner, InventoryRole.Owner,
                    InventoryRole.Writer, InventoryObjectClass.ForeignObligation,
                    InventoryCoordinateKind.Foreign, w,
                    FixtureObject(context, "trusted-parent"), WritableDirectoryAccess,
                    WritableDirectoryAccess, InventoryCapabilityClass.WriteCapable,
                    context.SelectedResource == "trusted-parent"
                        ? InventoryLifecycle.PendingAck
                        : InventoryLifecycle.Active);
                if (context.SelectedResource != "trusted-parent")
                {
                    var selected = Array.IndexOf(Resources, context.SelectedResource);
                    for (var index = 1; index <= selected; index++)
                    {
                        var resource = Resources[index];
                        var access = resource == "private-root" ? RootReadAccess : FileReadAccess;
                        AddFixtureCopies(slots, context, target, o, "o", InventoryRole.Writer,
                            InventoryRole.Owner, InventoryRole.Owner, [resource], access,
                            InventoryCapabilityClass.ReadOnly);
                    }
                }
            }
        }
        else if (context.Anchor == InventoryAnchor.B2)
        {
            Add("w:ow-client", InventoryRole.Writer, null, InventoryRole.Owner,
                InventoryObjectClass.Pipe, InventoryCoordinateKind.Owned, w,
                StableObject("w-ow-client"), null, null, InventoryCapabilityClass.Unknown);
            AddFixtureCopies(slots, context, target, w, "w", InventoryRole.Owner,
                InventoryRole.Owner, InventoryRole.Writer, ["trusted-parent"],
                WritableDirectoryAccess, InventoryCapabilityClass.WriteCapable);
            if (context.SelectedResource != "trusted-parent")
            {
                AddFixtureCopies(slots, context, target, w, "w", InventoryRole.Writer,
                    null, null, ["private-root"], WriterRootAccess,
                    InventoryCapabilityClass.WriteCapable);
                AddFixtureCopies(slots, context, target, w, "w", InventoryRole.Writer,
                    null, null, ["frames", "anchor"], WriterFileAccess,
                    InventoryCapabilityClass.WriteCapable);
            }
        }
        return slots.AsReadOnly();
    }

    private static void AddFixtureCopies(
        List<InventorySlotExpectation> slots,
        InventoryTrustedContext context,
        InventoryProcessIdentity holder,
        InventoryProcessIdentity coordinateOwner,
        string prefix,
        InventoryRole creator,
        InventoryRole? invoking,
        InventoryRole? target,
        IReadOnlyList<string> resources,
        uint access,
        InventoryCapabilityClass classification)
    {
        foreach (var resource in resources)
        {
            var fixture = context.FixtureIdentities[resource];
            slots.Add(new(
                $"{prefix}:{resource}", holder, creator, invoking, target,
                fixture.IsDirectory ? InventoryObjectClass.Directory : InventoryObjectClass.File,
                InventoryCoordinateKind.Owned, coordinateOwner, FixtureObject(context, resource),
                access, access, classification, InventoryLifecycle.Active));
        }
    }

    private static void ValidateTrustedContext(InventoryTrustedContext context)
    {
        if (!Enum.IsDefined(context.Anchor) || !Enum.IsDefined(context.TargetRole) ||
            !context.SyntheticOnly ||
            context.RunId == Guid.Empty || context.SessionId == Guid.Empty ||
            string.IsNullOrWhiteSpace(context.Scenario) ||
            !Resources.Contains(context.SelectedResource, StringComparer.Ordinal) ||
            context.Attempt is < 1 or > MaxAttempts ||
            context.Processes.Count != Enum.GetValues<InventoryRole>().Length ||
            context.PrimaryThreadIds.Count != Enum.GetValues<InventoryRole>().Length ||
            context.FixtureIdentities.Count != 6 ||
            context.Processes.Any(item => !Enum.IsDefined(item.Key) ||
                !Enum.IsDefined(item.Value.Role) || item.Key != item.Value.Role) ||
            context.PrimaryThreadIds.Keys.Any(role => !Enum.IsDefined(role)) ||
            context.Collector.Role != InventoryRole.Controller ||
            context.ExpectedStage != ExpectedStage(context.Anchor) ||
            !context.Processes.TryGetValue(context.TargetRole, out var target) ||
            target.Role != context.TargetRole || context.Collector != Process(context, InventoryRole.Controller))
        {
            throw new InvalidDataException("The trusted inventory context is invalid.");
        }
        foreach (var role in Enum.GetValues<InventoryRole>())
        {
            var process = Process(context, role);
            if (!Enum.IsDefined(process.Role) || process.Role != role ||
                process.ProcessId <= 0 || process.CreationFileTime <= 0 ||
                !context.PrimaryThreadIds.TryGetValue(role, out var threadId) || threadId == 0)
            {
                throw new InvalidDataException("The trusted process graph is invalid.");
            }
        }
        if (context.Processes.Values.Select(item => item.ProcessId).Distinct().Count() != 3 ||
            context.Processes.Values.Select(item =>
                    $"{item.ProcessId}:{item.CreationFileTime}")
                .Distinct(StringComparer.Ordinal).Count() != 3 ||
            context.PrimaryThreadIds[InventoryRole.Owner] ==
                context.PrimaryThreadIds[InventoryRole.Writer])
        {
            throw new InvalidDataException("The trusted P/O/W identities alias roles.");
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var resource in new[]
                 { "fixture-base", "run-root", "trusted-parent", "private-root", "frames", "anchor" })
        {
            if (!context.FixtureIdentities.TryGetValue(resource, out var fixture) ||
                fixture.Resource != resource || string.IsNullOrWhiteSpace(fixture.Name) ||
                string.IsNullOrWhiteSpace(fixture.FinalPath) || fixture.VolumeSerialNumber == 0 ||
                string.IsNullOrWhiteSpace(fixture.FileId) || fixture.IsReparsePoint ||
                !identities.Add($"{fixture.VolumeSerialNumber:X16}:{fixture.FileId}"))
            {
                throw new InvalidDataException("The trusted fixture graph is invalid.");
            }
            var isDirectory = resource is "fixture-base" or "run-root" or
                "trusted-parent" or "private-root";
            if (fixture.IsDirectory != isDirectory)
                throw new InvalidDataException("The trusted fixture object class is invalid.");
            var leased = resource is "private-root" or "frames" or "anchor";
            if (leased && (fixture.LeaseId is null || fixture.LeaseId == Guid.Empty ||
                           fixture.Generation is not > 0) ||
                !leased && (fixture.LeaseId is not null || fixture.Generation is not null))
            {
                throw new InvalidDataException("The trusted fixture lease binding is invalid.");
            }
        }
    }

    private static void ValidatePacketBinding(
        InventoryTrustedContext context,
        InventoryPacket packet,
        Action<string, bool> reject)
    {
        if (!Enum.IsDefined(packet.Anchor) || !Enum.IsDefined(packet.Collector.Role) ||
            !Enum.IsDefined(packet.Target.Role) ||
            packet.Schema != Schema || packet.RunId != context.RunId ||
            packet.SessionId != context.SessionId || packet.Scenario != context.Scenario ||
            packet.SelectedResource != context.SelectedResource || packet.Anchor != context.Anchor ||
            packet.Attempt != context.Attempt || packet.Collector != context.Collector ||
            packet.Target != Process(context, context.TargetRole) ||
            packet.Stage != context.ExpectedStage)
        {
            reject("packet-context-drift", true);
        }
        if (packet.SampleStartedAtUtc == default || packet.SampleEndedAtUtc == default ||
            packet.SampleStartedAtUtc > packet.SampleEndedAtUtc ||
            packet.SampleStartedMonotonicTicks < 0 ||
            packet.SampleEndedMonotonicTicks < packet.SampleStartedMonotonicTicks ||
            !packet.TargetLiveBefore || !packet.TargetLiveAfter)
        {
            reject("sample-identity-or-time-drift", true);
        }
    }

    private static void ValidateBounds(
        InventoryPacket packet,
        int rawRowCount,
        int rawManifestCount,
        int rawDeclaredCount,
        Action<string, bool> incomplete,
        Action<string, bool> reject,
        out long? encodedBytesObserved,
        out bool encodedByteLimitExceeded)
    {
        encodedBytesObserved = null;
        encodedByteLimitExceeded = false;
        if (rawRowCount > MaxRows)
            incomplete("row-limit-exceeded", false);
        if (rawManifestCount > MaxManifestSlots || rawDeclaredCount > MaxDeclaredSlots)
        {
            reject("manifest-structural-limit-exceeded", true);
            incomplete("manifest-structural-limit-exceeded", false);
        }
        var oversizedField = false;
        foreach (var value in PacketStrings(packet))
        {
            if (value is not null &&
                (value.Length > MaxFieldBytes || Encoding.UTF8.GetByteCount(value) > MaxFieldBytes))
            {
                incomplete("field-limit-exceeded", false);
                oversizedField = true;
                break;
            }
        }
        if (rawRowCount <= MaxRows && rawManifestCount <= MaxManifestSlots &&
            rawDeclaredCount <= MaxDeclaredSlots && !oversizedField)
        {
            using var counter = new CappedCountingStream(MaxEncodedPacketBytes);
            try
            {
                JsonSerializer.Serialize(counter, packet);
                encodedBytesObserved = counter.BytesWritten;
            }
            catch (Exception) when (counter.BytesWritten > MaxEncodedPacketBytes)
            {
                encodedBytesObserved = counter.BytesWritten;
                encodedByteLimitExceeded = true;
                incomplete("encoded-packet-limit-exceeded", false);
            }
        }
        else
        {
            incomplete("encoded-size-not-measured-after-prior-bound", false);
        }
        foreach (var row in packet.Rows)
        {
            if (row.CaptureMonotonicTicks < packet.SampleStartedMonotonicTicks ||
                row.CaptureMonotonicTicks > packet.SampleEndedMonotonicTicks ||
                row.CapturedAtUtc is not null &&
                (row.CapturedAtUtc < packet.SampleStartedAtUtc ||
                 row.CapturedAtUtc > packet.SampleEndedAtUtc))
            {
                reject("row-time-outside-sample", false);
            }
        }
    }

    private static void ValidateCollectionState(
        InventoryTrustedContext context,
        InventoryPacket packet,
        Action<string, bool> incomplete,
        Action<string, bool> reject)
    {
        if (!Enum.IsDefined(packet.PacketProvenance) ||
            !Enum.IsDefined(packet.CollectionTerminal) ||
            !Enum.IsDefined(packet.ResourceReleaseStatus))
        {
            reject("invalid-collection-enum", false);
        }
        if (context.SyntheticOnly &&
            (packet.PacketProvenance != InventoryMeasurementProvenance.Synthetic ||
             packet.SnapshotCollectionCompleted))
        {
            reject("synthetic-route-collection-claim", false);
        }
        if (packet.SnapshotCollectionCompleted !=
            (packet.CollectionTerminal == InventoryCollectionTerminal.Exhausted))
        {
            if (packet.SnapshotCollectionCompleted)
                reject("invalid-collection-terminal", false);
            else
                incomplete("snapshot-collection-not-completed", false);
        }
        else if (!packet.SnapshotCollectionCompleted)
        {
            incomplete("snapshot-collection-not-completed", false);
        }
        if (!string.IsNullOrWhiteSpace(packet.CollectionError) ||
            packet.CollectionTerminal is InventoryCollectionTerminal.Error or
                InventoryCollectionTerminal.Unsupported or InventoryCollectionTerminal.LimitReached)
        {
            incomplete("collection-error-or-unsupported", false);
        }
        if (packet.TemporalChurnObserved)
            incomplete("temporal-census-churn", false);
        if (packet.ResourceReleaseStatus == InventoryReleaseStatus.Failed)
            reject("collector-resource-release-failed", false);
        else if (packet.ResourceReleaseStatus == InventoryReleaseStatus.Unknown)
            incomplete("collector-resource-release-unknown", false);
    }

    private static void ValidateManifestSlot(
        InventorySlotExpectation expected,
        InventoryManifestSlot actual,
        Action<string, bool> reject,
        Action<string, bool> incomplete)
    {
        var code = expected.LogicalSlotId;
        if (!Enum.IsDefined(actual.Holder.Role) || !Enum.IsDefined(actual.Creator) ||
            actual.InvokingDuplicator is not null && !Enum.IsDefined(actual.InvokingDuplicator.Value) ||
            actual.Target is not null && !Enum.IsDefined(actual.Target.Value) ||
            !Enum.IsDefined(actual.ObjectClass) || !Enum.IsDefined(actual.Coordinate.Kind) ||
            !Enum.IsDefined(actual.CoordinateOwner.Role) || !Enum.IsDefined(actual.Classification) ||
            !Enum.IsDefined(actual.Presence) || !Enum.IsDefined(actual.Lifecycle))
        {
            reject("invalid-slot-enum:" + code, true);
        }
        if (actual.Holder != expected.Holder || actual.Creator != expected.Creator ||
            actual.InvokingDuplicator != expected.InvokingDuplicator ||
            actual.Target != expected.Target || actual.ObjectClass != expected.ObjectClass ||
            actual.Coordinate.Kind != expected.CoordinateKind ||
            actual.CoordinateOwner != expected.CoordinateOwner ||
            actual.ObjectIdentity != expected.ObjectIdentity ||
            actual.RequestedAccess != expected.RequestedAccess ||
            actual.Classification != expected.Classification ||
            actual.Lifecycle != expected.Lifecycle)
        {
            reject("slot-binding-drift:" + code, true);
        }
        if (actual.Presence == InventoryPresence.DemonstratedAbsent)
        {
            reject("required-slot-demonstrated-absent:" + code, true);
            return;
        }
        if (actual.Presence == InventoryPresence.Unknown)
            incomplete("required-slot-presence-unknown:" + code, true);
        if (actual.Coordinate.Kind is InventoryCoordinateKind.Owned or
                InventoryCoordinateKind.Foreign && actual.Coordinate.Value is not > 0)
        {
            reject("invalid-required-coordinate:" + code, true);
        }
        var observation = actual.Observation;
        if (observation is null)
        {
            incomplete("required-observation-missing:" + code, true);
            return;
        }
        if (!Enum.IsDefined(observation.Provenance))
            reject("invalid-observation-provenance:" + code, true);
        var unavailable = observation.Provenance is
            InventoryMeasurementProvenance.Missing or
            InventoryMeasurementProvenance.Unsupported or
            InventoryMeasurementProvenance.QueryFailed;
        if (!unavailable && observation.Provenance != InventoryMeasurementProvenance.Synthetic)
            reject("non-synthetic-observation:" + code, true);
        if (observation.AccessFieldValid != (observation.GrantedAccess is not null) ||
            observation.InheritanceFieldValid != (observation.Inheritable is not null))
        {
            reject("invalid-observation-flags:" + code, true);
        }
        if (observation.Inheritable == true)
            reject("inheritable-required-slot:" + code, true);
        if (unavailable && (observation.AccessFieldValid || observation.GrantedAccess is not null ||
                            observation.InheritanceFieldValid ||
                            observation.Inheritable is not null))
        {
            reject("unavailable-observation-populated-fields:" + code, true);
        }
        if (!observation.AccessFieldValid || !observation.InheritanceFieldValid)
            incomplete("required-observation-fields-missing:" + code, true);
        if (expected.AllowedAccess is uint allowed && observation.GrantedAccess is uint measured &&
            ((measured & expected.RequestedAccess!.Value) != expected.RequestedAccess.Value ||
             (measured & ~allowed) != 0))
        {
            reject("incompatible-or-excess-rights:" + code, true);
        }
        if (unavailable)
        {
            incomplete("required-observation-missing:" + code, true);
            return;
        }
        if (expected.AllowedAccess is null)
        {
            incomplete("rights-policy-unresolved:" + code, true);
            return;
        }
    }

    private static void ValidateManifestCoordinateClaims(
        IReadOnlyList<InventoryManifestSlot> manifest,
        Action<string, bool> reject)
    {
        var claims = new HashSet<string>(StringComparer.Ordinal);
        foreach (var slot in manifest.Where(item =>
                     item.Coordinate.Kind is InventoryCoordinateKind.Owned or
                         InventoryCoordinateKind.Foreign && item.Coordinate.Value is > 0))
        {
            var owner = slot.CoordinateOwner;
            var key = $"{owner.Role}:{owner.ProcessId}:{owner.CreationFileTime}:{slot.Coordinate.Value}";
            if (!claims.Add(key))
                reject("duplicate-coordinate-claim", true);
        }
    }

    private static void ValidateRows(
        InventoryPacket packet,
        IReadOnlyDictionary<string, InventoryManifestSlot> manifest,
        IReadOnlyList<InventorySlotExpectation> knownCatalog,
        Action<string, bool> incomplete,
        Action<string, bool> reject)
    {
        var coordinates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in packet.Rows)
        {
            var provenanceUnavailable = row.Provenance is InventoryMeasurementProvenance.Missing or
                InventoryMeasurementProvenance.Unsupported or InventoryMeasurementProvenance.QueryFailed;
            if (provenanceUnavailable && (row.BasicFieldsValid || row.GrantedAccess is not null ||
                                         row.InheritanceFieldValid || row.Inheritable is not null))
            {
                reject("unavailable-row-populated-capability-fields", false);
            }
            if (!Enum.IsDefined(row.Holder.Role) || !Enum.IsDefined(row.Coordinate.Kind) ||
                !Enum.IsDefined(row.State) || !Enum.IsDefined(row.Provenance))
            {
                reject("invalid-row-enum", false);
            }
            if (row.Provenance != InventoryMeasurementProvenance.Synthetic &&
                row.Provenance is not InventoryMeasurementProvenance.Missing and
                    not InventoryMeasurementProvenance.Unsupported and
                    not InventoryMeasurementProvenance.QueryFailed)
            {
                reject("non-synthetic-row", false);
            }
            if (row.Holder != packet.Target)
                reject("raw-row-holder-drift", false);
            if (!CoordinateShapeValid(row.Coordinate))
                reject("invalid-coordinate-shape", false);
            if (row.BasicFieldsValid != (row.GrantedAccess is not null) ||
                row.InheritanceFieldValid != (row.Inheritable is not null) ||
                row.TypeNameValid != (row.RawTypeName is not null) ||
                row.ObjectNameValid != (row.RawObjectName is not null))
            {
                reject("invalid-row-field-flags", false);
            }
            if (row.Inheritable == true)
                reject("inheritable-census-row", false);

            if (row.Coordinate.Kind is InventoryCoordinateKind.Owned or
                    InventoryCoordinateKind.Foreign && row.Coordinate.Value is > 0)
            {
                var key = $"{row.Holder.ProcessId}:{row.Holder.CreationFileTime}:{row.Coordinate.Value}";
                if (!coordinates.Add(key))
                    reject("duplicate-census-coordinate", false);
            }

            InventoryManifestSlot? slot = null;
            if (row.LogicalSlotId is not null)
            {
                if (!manifest.TryGetValue(row.LogicalSlotId, out slot))
                {
                    reject("row-claims-unknown-logical-slot", false);
                    continue;
                }
            }
            else if (row.ObjectIdentity is not null)
            {
                var identityMatches = knownCatalog.Where(item =>
                    PrimaryIdentityMatches(item, row.ObjectIdentity)).ToArray();
                var targetMatches = identityMatches.Where(item =>
                    item.Holder == packet.Target && item.CoordinateOwner == packet.Target).ToArray();
                if (identityMatches.Length > 0 && targetMatches.Length == 0)
                {
                    reject("known-object-forbidden-holder", false);
                    continue;
                }
                if (targetMatches.Length > 0)
                {
                    var candidateIds = targetMatches.Select(item => item.LogicalSlotId)
                        .ToHashSet(StringComparer.Ordinal);
                    var candidateMatches = manifest.Values.Where(item =>
                            candidateIds.Contains(item.LogicalSlotId) &&
                            item.CoordinateOwner == row.Holder && item.Coordinate == row.Coordinate)
                        .Take(2)
                        .ToArray();
                    if (candidateMatches.Length != 1)
                    {
                        reject(candidateMatches.Length == 0
                            ? "known-object-coordinate-conflict"
                            : "ambiguous-known-object-coordinate-claim", false);
                        continue;
                    }
                    slot = candidateMatches[0];
                }
            }

            if (slot is null)
            {
                incomplete("unknown-runtime-row", false);
                continue;
            }

            var rowCode = slot.LogicalSlotId;
            if (slot.Coordinate.Kind == InventoryCoordinateKind.Foreign ||
                slot.CoordinateOwner != row.Holder)
            {
                if (row.State == InventoryRowState.Observed || row.BasicFieldsValid ||
                    row.InheritanceFieldValid)
                {
                    reject("foreign-obligation-presented-as-local-row:" + rowCode, false);
                }
                else
                {
                    incomplete("foreign-obligation-row-unresolved:" + rowCode, false);
                }
                continue;
            }
            if (row.Coordinate.Kind is InventoryCoordinateKind.Zero or
                    InventoryCoordinateKind.Invalid or InventoryCoordinateKind.Pseudo ||
                row.Coordinate != slot.Coordinate)
            {
                reject("row-slot-coordinate-conflict:" + rowCode, false);
            }
            if (row.ObjectIdentity is null)
                incomplete("row-object-identity-unavailable:" + rowCode, true);
            else if (row.ObjectIdentity != slot.ObjectIdentity)
                reject("row-slot-object-conflict:" + rowCode, false);

            var policy = knownCatalog.SingleOrDefault(item => item.LogicalSlotId == rowCode);
            if (policy is null)
            {
                reject("row-policy-not-trusted:" + rowCode, false);
                continue;
            }
            if (row.GrantedAccess is uint rowAccess)
            {
                if (slot.Observation?.GrantedAccess is uint manifestAccess &&
                    rowAccess != manifestAccess)
                {
                    reject("row-manifest-rights-conflict:" + rowCode, false);
                }
                if (policy.AllowedAccess is uint allowed &&
                    ((rowAccess & policy.RequestedAccess!.Value) != policy.RequestedAccess.Value ||
                     (rowAccess & ~allowed) != 0))
                {
                    reject("row-incompatible-or-excess-rights:" + rowCode, false);
                }
            }
            else if (row.State == InventoryRowState.Observed)
            {
                incomplete("row-rights-unavailable:" + rowCode, true);
            }

            if (row.State == InventoryRowState.DemonstratedAbsent)
            {
                reject("row-demonstrates-required-slot-absent", false);
                continue;
            }
            if (row.State is InventoryRowState.Unknown or InventoryRowState.Unsupported or
                InventoryRowState.QueryFailed || provenanceUnavailable)
            {
                incomplete("row-observation-unavailable:" + rowCode, true);
                continue;
            }
        }
    }

    private static bool PrimaryIdentityMatches(
        InventorySlotExpectation expected,
        InventoryObjectIdentity actual) =>
        expected.ObjectClass switch
        {
            InventoryObjectClass.Directory or InventoryObjectClass.File or
                InventoryObjectClass.ForeignObligation =>
                expected.ObjectIdentity.VolumeSerialNumber is not null &&
                expected.ObjectIdentity.FileId is not null &&
                actual.VolumeSerialNumber == expected.ObjectIdentity.VolumeSerialNumber &&
                StringComparer.Ordinal.Equals(actual.FileId, expected.ObjectIdentity.FileId),
            InventoryObjectClass.Process =>
                actual.TargetProcessId == expected.ObjectIdentity.TargetProcessId &&
                actual.TargetCreationFileTime == expected.ObjectIdentity.TargetCreationFileTime,
            InventoryObjectClass.Thread =>
                actual.TargetProcessId == expected.ObjectIdentity.TargetProcessId &&
                actual.TargetCreationFileTime == expected.ObjectIdentity.TargetCreationFileTime &&
                actual.ThreadId == expected.ObjectIdentity.ThreadId,
            InventoryObjectClass.Job =>
                !string.IsNullOrWhiteSpace(expected.ObjectIdentity.JobSlot) &&
                StringComparer.Ordinal.Equals(actual.JobSlot, expected.ObjectIdentity.JobSlot),
            InventoryObjectClass.Pipe or InventoryObjectClass.CustodyFile =>
                StringComparer.Ordinal.Equals(actual.StableId, expected.ObjectIdentity.StableId),
            _ => false,
        };

    private static bool CoordinateShapeValid(InventoryCoordinate coordinate) =>
        coordinate.Kind switch
        {
            InventoryCoordinateKind.Owned or InventoryCoordinateKind.Foreign =>
                coordinate.Value is > 0,
            InventoryCoordinateKind.Zero => coordinate.Value == 0,
            InventoryCoordinateKind.Invalid => coordinate.Value == -1,
            InventoryCoordinateKind.Pseudo => coordinate.Value is < -1,
            _ => false,
        };

    private static IEnumerable<string?> PacketStrings(InventoryPacket packet)
    {
        yield return packet.Schema;
        yield return packet.Scenario;
        yield return packet.SelectedResource;
        yield return packet.Stage;
        yield return packet.CollectionError;
        foreach (var id in packet.DeclaredRequiredSlotIds)
            yield return id;
        foreach (var slot in packet.Manifest)
        {
            yield return slot.LogicalSlotId;
            foreach (var value in IdentityStrings(slot.ObjectIdentity))
                yield return value;
            yield return slot.Observation?.Method;
        }
        foreach (var row in packet.Rows)
        {
            yield return row.LogicalSlotId;
            yield return row.RawTypeName;
            yield return row.RawObjectName;
            yield return row.ClassificationReason;
            if (row.ObjectIdentity is not null)
            {
                foreach (var value in IdentityStrings(row.ObjectIdentity))
                    yield return value;
            }
        }
    }

    private static IEnumerable<string?> IdentityStrings(InventoryObjectIdentity identity)
    {
        yield return identity.StableId;
        yield return identity.Resource;
        yield return identity.Name;
        yield return identity.FinalPath;
        yield return identity.FileId;
        yield return identity.JobSlot;
    }

    private static string ExpectedStage(InventoryAnchor anchor) => anchor switch
    {
        InventoryAnchor.B0 => "B0-w-born-suspended",
        InventoryAnchor.B1 => "B1-prearm-accepted-suspended",
        InventoryAnchor.B2 => "B2-selected-before-ack",
        _ => throw new InvalidDataException("Unknown inventory anchor."),
    };

    private static InventoryProcessIdentity Process(
        InventoryTrustedContext context,
        InventoryRole role) =>
        context.Processes.TryGetValue(role, out var identity)
            ? identity
            : throw new InvalidDataException($"The trusted {role} identity is missing.");

    private static InventoryObjectIdentity ProcessObject(
        string stableId,
        InventoryProcessIdentity process) =>
        new(stableId, TargetProcessId: process.ProcessId,
            TargetCreationFileTime: process.CreationFileTime);

    private static InventoryObjectIdentity ThreadObject(
        string stableId,
        InventoryProcessIdentity process,
        uint threadId) =>
        new(stableId, TargetProcessId: process.ProcessId,
            TargetCreationFileTime: process.CreationFileTime, ThreadId: threadId);

    private static InventoryObjectIdentity JobObject(string slot) =>
        new(slot, JobSlot: slot);

    private static InventoryObjectIdentity StableObject(string stableId) => new(stableId);

    private static InventoryObjectIdentity FixtureObject(
        InventoryTrustedContext context,
        string resource)
    {
        var fixture = context.FixtureIdentities[resource];
        return new(
            $"fixture:{resource}", fixture.Resource, fixture.Name, fixture.FinalPath,
            fixture.VolumeSerialNumber, fixture.FileId, fixture.IsDirectory,
            fixture.IsReparsePoint, LeaseId: fixture.LeaseId, Generation: fixture.Generation);
    }

    private static InventoryTrustedContext SnapshotContext(InventoryTrustedContext context) =>
        context with
        {
            Processes = context.Processes.ToDictionary(item => item.Key, item => item.Value),
            PrimaryThreadIds = context.PrimaryThreadIds.ToDictionary(
                item => item.Key,
                item => item.Value),
            FixtureIdentities = context.FixtureIdentities.ToDictionary(
                item => item.Key,
                item => item.Value,
                StringComparer.Ordinal),
        };

    private static IReadOnlyList<T> SnapshotBounded<T>(IReadOnlyList<T> source, int maximum)
    {
        var count = Math.Min(source.Count, maximum);
        var result = new T[count];
        for (var index = 0; index < count; index++)
            result[index] = source[index];
        return Array.AsReadOnly(result);
    }

    private static InventoryReconciliationResult NewResult(
        bool manifestValidated,
        bool knownChecksPassed,
        InventorySemanticStatus status,
        List<string> rejections,
        List<string> incomplete,
        int rawRowCount,
        int inspectedRowCount,
        long? encodedBytesObserved,
        bool encodedByteLimitExceeded) =>
        new(
            manifestValidated,
            knownChecksPassed,
            false,
            status,
            rejections.AsReadOnly(),
            incomplete.AsReadOnly(),
            rawRowCount,
            inspectedRowCount,
            Math.Max(0, rawRowCount - inspectedRowCount),
            encodedBytesObserved,
            encodedByteLimitExceeded);

    private sealed class CappedCountingStream(long limit) : Stream
    {
        internal long BytesWritten { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;

        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Add(count);

        public override void Write(ReadOnlySpan<byte> buffer) => Add(buffer.Length);

        public override void WriteByte(byte value) => Add(1);

        private void Add(int count)
        {
            if (count < 0 || BytesWritten > limit - count)
            {
                BytesWritten = limit + 1;
                throw new InventoryPacketLimitException();
            }
            BytesWritten += count;
        }
    }

    private sealed class InventoryPacketLimitException : Exception
    {
    }
}
