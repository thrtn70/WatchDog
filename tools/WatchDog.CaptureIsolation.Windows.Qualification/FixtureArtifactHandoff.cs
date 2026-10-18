using System.Collections.ObjectModel;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

// B0 pure policy. There is no launcher, child, native, or artifact runtime call site.
internal enum FixtureHandoffEdge
{
    LauncherToRunner,
    RunnerToOwner,
    OwnerToHelper,
    RunnerToBootstrapHelper,
    HelperToDescendant,
}

internal enum FixtureHandoffHandleKind { Section, Process, Directory, File }

[Flags]
internal enum FixtureHandoffRights : uint
{
    None = 0,
    SectionRead = 1 << 0,
    ProcessQueryLimited = 1 << 1,
    Synchronize = 1 << 2,
    DirectoryObserve = 1 << 3,
    DirectoryCreateFile = 1 << 4,
    DirectoryCreateDirectory = 1 << 5,
    FileAppend = 1 << 6,
    FileRead = 1 << 7,
    FileWrite = 1 << 8,
}

internal sealed record FixtureHandoffSlot(
    string Label, FixtureHandoffHandleKind Kind, FixtureHandoffRights Rights);

internal sealed record FixtureHandoffProcessIdentity(uint ProcessId, long CreationFileTime)
{
    internal bool IsValid => ProcessId != 0 && CreationFileTime > 0;
}

internal sealed record FixtureHandoffObservedHandle(
    nint RawValue, FixtureHandoffHandleKind Kind, FixtureHandoffRights Rights,
    FixtureArtifactIdentity? Identity);

internal sealed record FixtureHandoffClaim(string Label, nint RawValue);

internal sealed record FixtureHandoffBinding(
    FixtureHandoffEdge Edge, QualificationScenario? Scenario, Guid RunNonce, Guid LaunchId,
    FixtureHandoffProcessIdentity Parent, FixtureHandoffProcessIdentity Child);

internal interface IFixtureHandoffProbe
{
    FixtureHandoffProcessIdentity ObserveProcess(nint retainedProcessHandle);
    bool IsProcessAlive(nint retainedProcessHandle);
    FixtureHandoffObservedHandle ObserveHandle(nint handle);
    byte[] ReadSectionKey(nint sectionHandle);
    // A native adapter must prove this relationship from retained object authority, not a
    // pathname precheck. B0 only injects the outcome for deterministic policy tests.
    bool IsRegisteredChild(
        nint retainedDirectoryHandle, string literalName, FixtureArtifactIdentity expected);
}

internal sealed class FixtureHandoffContractException(string code) : Exception
{
    internal string Code { get; } = code;
}

internal sealed record FixtureHandoffLedgerEntry(
    string Name, FixtureArtifactIdentity Identity, QualificationRole Writer,
    FixtureHandoffRights WriterRights);

internal interface IFixtureHandoffRegisteredOutputObserver
{
    // Native implementation must use retained exact-object authority; name lookup alone
    // cannot establish this relation under concurrent path mutation.
    bool IsStillOwnedChild(
        FixtureArtifactIdentity directory, string literalName,
        FixtureArtifactIdentity expectedOutput);
}

/// <summary>
/// Runner-owned immutable output registry. All expected descendant outputs are registered
/// before an owner may be killed. Creation, native rights and exact parentage are not proven
/// here; the injected probe must corroborate them at each transfer.
/// </summary>
internal sealed class FixtureArtifactHandoffLedger
{
    private readonly ReadOnlyDictionary<string, FixtureHandoffLedgerEntry> _entries;
    private readonly FixtureArtifactOwnership _authority;
    private readonly FixtureArtifactOwnership.Node _directoryNode;
    private readonly IFixtureHandoffRegisteredOutputObserver _outputObserver;

    private FixtureArtifactHandoffLedger(
        QualificationScenario? scenario, FixtureArtifactOwnership authority,
        FixtureArtifactOwnership.Node directoryNode, FixtureArtifactIdentity directory,
        IFixtureHandoffRegisteredOutputObserver outputObserver,
        Dictionary<string, FixtureHandoffLedgerEntry> entries)
    {
        Scenario = scenario;
        _authority = authority;
        _directoryNode = directoryNode;
        _outputObserver = outputObserver;
        Directory = directory;
        _entries = new(entries);
    }

    internal QualificationScenario? Scenario { get; }
    internal FixtureArtifactIdentity Directory { get; }
    internal IReadOnlyDictionary<string, FixtureHandoffLedgerEntry> Entries => _entries;
    internal bool IsStillAdmitted =>
        _authority.ObserveRetainedDirectory(_directoryNode) == Directory &&
        _entries.Values.All(entry => _outputObserver.IsStillOwnedChild(
            Directory, entry.Name, entry.Identity));

    internal static FixtureArtifactHandoffLedger Create(
        QualificationScenario? scenario, FixtureArtifactOwnership authority,
        FixtureArtifactOwnership.Node directoryNode,
        IFixtureHandoffRegisteredOutputObserver outputObserver,
        IEnumerable<FixtureHandoffLedgerEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(directoryNode);
        ArgumentNullException.ThrowIfNull(outputObserver);
        ArgumentNullException.ThrowIfNull(entries);
        var directory = authority.ObserveRetainedDirectory(directoryNode);
        if (!ValidIdentity(directory, FixtureArtifactKind.Directory, directory.Volume))
            throw new FixtureHandoffContractException("fixture.handoff.ledger-invalid");
        var expected = ExpectedEntries(scenario);
        var actual = new Dictionary<string, FixtureHandoffLedgerEntry>(StringComparer.Ordinal);
        var namesIgnoreCase = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var identities = new HashSet<FixtureArtifactIdentity> { directory };
        foreach (var entry in entries)
        {
            if (entry is null || !expected.TryGetValue(entry.Name, out var required) ||
                !namesIgnoreCase.Add(entry.Name) || !actual.TryAdd(entry.Name, entry) ||
                entry.Writer != required.Writer || entry.WriterRights != required.WriterRights ||
                !ValidIdentity(entry.Identity, FixtureArtifactKind.File, directory.Volume) ||
                !identities.Add(entry.Identity))
                throw new FixtureHandoffContractException("fixture.handoff.ledger-invalid");
        }
        if (actual.Count != expected.Count)
            throw new FixtureHandoffContractException("fixture.handoff.ledger-invalid");
        var ledger = new FixtureArtifactHandoffLedger(
            scenario, authority, directoryNode, directory, outputObserver, actual);
        if (!ledger.IsStillAdmitted)
            throw new FixtureHandoffContractException("fixture.handoff.ledger-invalid");
        return ledger;
    }

    internal static Dictionary<string, (QualificationRole Writer, FixtureHandoffRights WriterRights)>
        ExpectedEntries(QualificationScenario? scenario)
    {
        var expected = new Dictionary<string,
            (QualificationRole Writer, FixtureHandoffRights WriterRights)>(StringComparer.Ordinal);
        static void Add(Dictionary<string, (QualificationRole, FixtureHandoffRights)> target,
            string name, QualificationRole role, FixtureHandoffRights rights) =>
            target.Add(name, (role, rights));
        if (scenario is null)
        {
            Add(expected, "manifest.json", QualificationRole.Runner, FixtureHandoffRights.FileWrite);
            Add(expected, "result.json", QualificationRole.Runner, FixtureHandoffRights.FileWrite);
            Add(expected, "report.md", QualificationRole.Runner, FixtureHandoffRights.FileWrite);
            Add(expected, "runner-run.jsonl", QualificationRole.Runner,
                FixtureHandoffRights.FileAppend);
            return expected;
        }
        var name = ScenarioName(scenario);
        if (name is null)
            throw new FixtureHandoffContractException("fixture.handoff.ledger-invalid");
        Add(expected, $"runner-{name}.jsonl", QualificationRole.Runner,
            FixtureHandoffRights.FileAppend);
        if (IsDescendantScenario(scenario))
        {
            Add(expected, $"owner-{name}.jsonl", QualificationRole.Owner,
                FixtureHandoffRights.FileAppend);
            Add(expected, "membership-enumeration.json", QualificationRole.Owner,
                FixtureHandoffRights.FileWrite);
            Add(expected, "membership-snapshot.json", QualificationRole.Owner,
                FixtureHandoffRights.FileWrite);
            Add(expected, "owner-result.json", QualificationRole.Owner,
                FixtureHandoffRights.FileWrite);
            if (scenario == QualificationScenario.OwnerDeath)
                Add(expected, "owner-armed.json", QualificationRole.Owner,
                    FixtureHandoffRights.FileWrite);
            Add(expected, $"helper-{name}.jsonl", QualificationRole.Helper,
                FixtureHandoffRights.FileAppend);
            Add(expected, "helper-startup.json", QualificationRole.Helper,
                FixtureHandoffRights.FileWrite);
            Add(expected, "helper-ready.json", QualificationRole.Helper,
                FixtureHandoffRights.FileWrite);
            Add(expected, $"descendant-{name}.jsonl", QualificationRole.Descendant,
                FixtureHandoffRights.FileAppend);
            Add(expected, "descendant-startup.json", QualificationRole.Descendant,
                FixtureHandoffRights.FileWrite);
        }
        else
        {
            Add(expected, $"helper-{name}.jsonl", QualificationRole.Helper,
                FixtureHandoffRights.FileAppend);
            Add(expected, "bootstrap-ready.json", QualificationRole.Helper,
                FixtureHandoffRights.FileWrite);
        }
        return expected;
    }

    private static bool ValidIdentity(
        FixtureArtifactIdentity identity, FixtureArtifactKind kind, ulong volume) =>
        volume != 0 && identity.Volume == volume &&
        (identity.FileIdLow != 0 || identity.FileIdHigh != 0) &&
        identity.Kind == kind && !identity.IsReparse;

    internal static bool IsDescendantScenario(QualificationScenario? scenario) =>
        scenario is QualificationScenario.ExplicitDescendantTermination or
            QualificationScenario.OwnerDeath;

    internal static bool IsBootstrapScenario(QualificationScenario? scenario) =>
        scenario is QualificationScenario.AuthenticatedBootstrap or
            QualificationScenario.RejectedBootstrapMaterial or
            QualificationScenario.RejectedUnintendedPeer or
            QualificationScenario.RejectedPartialHandshake;

    private static string? ScenarioName(QualificationScenario? scenario) => scenario switch
    {
        QualificationScenario.ExplicitDescendantTermination => "ExplicitDescendantTermination",
        QualificationScenario.OwnerDeath => "OwnerDeath",
        QualificationScenario.AuthenticatedBootstrap => "AuthenticatedBootstrap",
        QualificationScenario.RejectedBootstrapMaterial => "RejectedBootstrapMaterial",
        QualificationScenario.RejectedUnintendedPeer => "RejectedUnintendedPeer",
        QualificationScenario.RejectedPartialHandshake => "RejectedPartialHandshake",
        _ => null,
    };
}

/// <summary>Exact per-edge receiver schema, not a general handle-list overload.</summary>
internal sealed class FixtureArtifactHandoffPlan
{
    private static readonly FixtureHandoffRights ParentProofRights =
        FixtureHandoffRights.ProcessQueryLimited | FixtureHandoffRights.Synchronize;
    private readonly ReadOnlyCollection<FixtureHandoffSlot> _slots;

    private FixtureArtifactHandoffPlan(
        FixtureHandoffEdge edge, QualificationScenario? scenario,
        Guid runNonce, Guid launchId, List<FixtureHandoffSlot> slots)
    {
        Edge = edge;
        Scenario = scenario;
        RunNonce = runNonce;
        LaunchId = launchId;
        _slots = slots.AsReadOnly();
    }

    internal FixtureHandoffEdge Edge { get; }
    internal QualificationScenario? Scenario { get; }
    internal Guid RunNonce { get; }
    internal Guid LaunchId { get; }
    internal IReadOnlyList<FixtureHandoffSlot> Slots => _slots;
    internal QualificationRole Receiver => Edge switch
    {
        FixtureHandoffEdge.LauncherToRunner => QualificationRole.Runner,
        FixtureHandoffEdge.RunnerToOwner => QualificationRole.Owner,
        FixtureHandoffEdge.OwnerToHelper or FixtureHandoffEdge.RunnerToBootstrapHelper =>
            QualificationRole.Helper,
        FixtureHandoffEdge.HelperToDescendant => QualificationRole.Descendant,
        _ => throw new FixtureHandoffContractException("fixture.handoff.plan-invalid"),
    };

    internal static FixtureArtifactHandoffPlan Create(
        FixtureHandoffEdge edge, QualificationScenario? scenario,
        Guid runNonce, Guid launchId)
    {
        if (!Enum.IsDefined(edge) || runNonce == Guid.Empty || launchId == Guid.Empty ||
            runNonce == launchId ||
            (edge == FixtureHandoffEdge.LauncherToRunner ? scenario is not null : scenario is null) ||
            (edge is FixtureHandoffEdge.RunnerToOwner or FixtureHandoffEdge.OwnerToHelper or
                FixtureHandoffEdge.HelperToDescendant &&
                !FixtureArtifactHandoffLedger.IsDescendantScenario(scenario)) ||
            (edge == FixtureHandoffEdge.RunnerToBootstrapHelper &&
                !FixtureArtifactHandoffLedger.IsBootstrapScenario(scenario)))
            throw new FixtureHandoffContractException("fixture.handoff.plan-invalid");

        var slots = new List<FixtureHandoffSlot>
        {
            new("artifact-auth-section", FixtureHandoffHandleKind.Section,
                FixtureHandoffRights.SectionRead),
            new("artifact-parent-proof", FixtureHandoffHandleKind.Process, ParentProofRights),
            new("artifact-directory", FixtureHandoffHandleKind.Directory,
                edge == FixtureHandoffEdge.LauncherToRunner
                    ? FixtureHandoffRights.DirectoryObserve |
                        FixtureHandoffRights.DirectoryCreateFile |
                        FixtureHandoffRights.DirectoryCreateDirectory
                    : FixtureHandoffRights.DirectoryObserve),
        };
        if (edge == FixtureHandoffEdge.RunnerToBootstrapHelper)
        {
            slots.Add(new("bootstrap-section", FixtureHandoffHandleKind.Section,
                FixtureHandoffRights.SectionRead));
            slots.Add(new("bootstrap-parent-proof", FixtureHandoffHandleKind.Process,
                ParentProofRights));
        }

        var name = scenario?.ToString();
        switch (edge)
        {
            case FixtureHandoffEdge.LauncherToRunner:
                Write(slots, "manifest.json");
                Write(slots, "result.json");
                Write(slots, "report.md");
                Append(slots, "runner-run.jsonl");
                break;
            case FixtureHandoffEdge.RunnerToOwner:
                Append(slots, $"owner-{name}.jsonl");
                Write(slots, "membership-enumeration.json");
                Write(slots, "membership-snapshot.json");
                Write(slots, "owner-result.json");
                if (scenario == QualificationScenario.OwnerDeath)
                    Write(slots, "owner-armed.json");
                Read(slots, "helper-ready.json");
                break;
            case FixtureHandoffEdge.OwnerToHelper:
                Append(slots, $"helper-{name}.jsonl");
                Write(slots, "helper-startup.json");
                Write(slots, "helper-ready.json");
                Read(slots, "descendant-startup.json");
                break;
            case FixtureHandoffEdge.RunnerToBootstrapHelper:
                Append(slots, $"helper-{name}.jsonl");
                Write(slots, "bootstrap-ready.json");
                break;
            case FixtureHandoffEdge.HelperToDescendant:
                Append(slots, $"descendant-{name}.jsonl");
                Write(slots, "descendant-startup.json");
                break;
        }
        if (slots.Count > 16 ||
            slots.Select(slot => slot.Label).Distinct(StringComparer.Ordinal).Count() != slots.Count)
            throw new FixtureHandoffContractException("fixture.handoff.plan-invalid");
        return new(edge, scenario, runNonce, launchId, slots);
    }

    private static void Write(List<FixtureHandoffSlot> slots, string name) =>
        slots.Add(new($"write:{name}", FixtureHandoffHandleKind.File,
            FixtureHandoffRights.FileWrite));
    private static void Append(List<FixtureHandoffSlot> slots, string name) =>
        slots.Add(new($"write:{name}", FixtureHandoffHandleKind.File,
            FixtureHandoffRights.FileAppend));
    private static void Read(List<FixtureHandoffSlot> slots, string name) =>
        slots.Add(new($"read:{name}", FixtureHandoffHandleKind.File,
            FixtureHandoffRights.FileRead));
}
