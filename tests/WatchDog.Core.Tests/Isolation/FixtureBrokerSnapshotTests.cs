using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureBrokerSnapshotTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, 32)
        .Select(value => (byte)value).ToArray();
    private static readonly Guid Run = Guid.Parse("6e32e942-4337-4f2e-8aa8-1656dd6f9632");
    private static readonly Guid Launch = Guid.Parse("4ca029ac-65a3-4fbf-9246-ed5f0c04b5f8");
    private static readonly FixtureHandoffProcessIdentity Launcher = new(100, 101);
    private static readonly FixtureHandoffProcessIdentity Runner = new(200, 202);
    private static readonly FixtureHandoffProcessIdentity Owner = new(300, 303);
    private static readonly FixtureArtifactIdentity Directory =
        new(7, 9, 0, FixtureArtifactKind.Directory, false);

    [Fact]
    public void CanonicalRoundTripIsDeterministicAndChildLocal()
    {
        var (plan, snapshot, observer) = NewSnapshot();
        var first = FixtureBrokerSnapshotCodec.Seal(snapshot, Secret);
        var second = FixtureBrokerSnapshotCodec.Seal(snapshot, Secret);
        Assert.Equal(first.Bytes, second.Bytes);
        Assert.Equal(first.Mac, second.Mac);
        Assert.Equal(snapshot, FixtureBrokerSnapshotCodec.Open(first.Bytes, first.Mac,
            Secret, plan, snapshot.Parties, observer) with
        { Outputs = snapshot.Outputs });
        observer.Directory = Directory with { FileIdLow = 77 };
        AssertInvalid(first.Bytes, first.Mac, plan, snapshot.Parties, observer);
    }

    [Fact]
    public void MalformedLengthsCountsVersionFlagsAndTrailingBytesAreRejected()
    {
        var (plan, snapshot, observer) = NewSnapshot();
        var seal = FixtureBrokerSnapshotCodec.Seal(snapshot, Secret);
        foreach (var length in new[] { 0, 8, 116, seal.Bytes.Length - 1 })
            AssertInvalid(seal.Bytes[..length], Sign(seal.Bytes[..length]),
                plan, snapshot.Parties, observer);
        AssertInvalid([.. seal.Bytes, 0], Sign([.. seal.Bytes, 0]),
            plan, snapshot.Parties, observer);
        foreach (var (offset, value) in new[]
        {
            (8, (byte)2), (10, (byte)1), (12, (byte)255),
            (115, (byte)255), (117, (byte)255), (117, (byte)0),
            (118, (byte)255), (88, (byte)255)
        })
        {
            var changed = (byte[])seal.Bytes.Clone();
            changed[offset] = value;
            AssertInvalid(changed, Sign(changed), plan, snapshot.Parties, observer);
        }
        var huge = new byte[8193];
        AssertInvalid(huge, new byte[32], plan, snapshot.Parties, observer);
        var badMac = (byte[])seal.Mac.Clone();
        badMac[0] ^= 1;
        AssertInvalid(seal.Bytes, badMac, plan, snapshot.Parties, observer);
    }

    [Fact]
    public void WrongCatalogueRoleRightIdentityParentageAndAliasAreRejected()
    {
        var (plan, snapshot, observer) = NewSnapshot();
        var seal = FixtureBrokerSnapshotCodec.Seal(snapshot, Secret);
        var firstNameLength = seal.Bytes[117];
        var writer = 118 + firstNameLength;
        var rights = writer + 1;
        var identity = rights + 4;
        var parent = identity + 26;
        foreach (var (offset, value) in new[]
        {
            (118, (byte)'M'), // Case alias, if the first name begins with m.
            (writer, (byte)QualificationRole.Descendant),
            (rights, (byte)FixtureHandoffRights.FileRead),
            (identity, (byte)8), // Wrong volume.
            (parent + 8, (byte)88) // Wrong retained parent file ID.
        })
        {
            var changed = (byte[])seal.Bytes.Clone();
            changed[offset] = value;
            AssertInvalid(changed, Sign(changed), plan, snapshot.Parties, observer);
        }
        var duplicateIdentity = (byte[])seal.Bytes.Clone();
        Array.Copy(duplicateIdentity, parent, duplicateIdentity, identity, 26);
        AssertInvalid(duplicateIdentity, Sign(duplicateIdentity), plan,
            snapshot.Parties, observer);
        var wrongScenario = (byte[])seal.Bytes.Clone();
        BinaryPrimitives.WriteInt32LittleEndian(wrongScenario.AsSpan(16),
            (int)QualificationScenario.OwnerDeath);
        AssertInvalid(wrongScenario, Sign(wrongScenario), plan,
            snapshot.Parties, observer);
        observer.Outputs[snapshot.Outputs[0].Name] = snapshot.Outputs[0].Identity
            with
        { FileIdLow = 40 };
        AssertInvalid(seal.Bytes, seal.Mac, plan, snapshot.Parties, observer);
    }

    [Fact]
    public void CreatorIssuerAndTargetCannotBeSubstituted()
    {
        var (plan, snapshot, observer) = NewSnapshot();
        var seal = FixtureBrokerSnapshotCodec.Seal(snapshot, Secret);
        AssertInvalid(seal.Bytes, seal.Mac, plan,
            snapshot.Parties with { Target = new(200, 999) }, observer);
        Assert.Throws<FixtureHandoffContractException>(() =>
            FixtureBrokerSnapshotCodec.Seal(snapshot with
            {
                Parties = snapshot.Parties with { Issuer = Owner }
            }, Secret));
        var later = FixtureArtifactHandoffPlan.Create(FixtureHandoffEdge.OwnerToHelper,
            QualificationScenario.OwnerDeath, Run, Launch);
        Assert.Throws<FixtureHandoffContractException>(() =>
            FixtureBrokerSnapshotCodec.Seal(snapshot with
            {
                Edge = later.Edge,
                Scenario = later.Scenario,
                Delivery = FixtureBrokerDeliveryMode.DirectIssuer
            }, Secret));
    }

    [Fact]
    public void DirectBrokerEdgeBindsDistinctCreatorAndIssuer()
    {
        var plan = FixtureArtifactHandoffPlan.Create(
            FixtureHandoffEdge.OwnerToHelper, QualificationScenario.OwnerDeath,
            Run, Launch);
        var entries = FixtureArtifactHandoffLedger.ExpectedEntries(plan.Scenario)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select((entry, index) => new FixtureBrokerOutput(entry.Key,
                entry.Value.Writer, entry.Value.WriterRights,
                new FixtureArtifactIdentity(7, (ulong)(index + 10), 0,
                    FixtureArtifactKind.File, false), Directory)).ToArray();
        var parties = new FixtureBrokerParties(Owner, Runner, new(400, 404));
        var snapshot = new FixtureBrokerSnapshot(plan.Edge, plan.Scenario, Run,
            Launch, parties, FixtureBrokerDeliveryMode.DirectIssuer,
            Directory, entries);
        var observer = new FakeObserver(plan, entries, parties);
        var sealedSnapshot = FixtureBrokerSnapshotCodec.Seal(snapshot, Secret);
        Assert.Equal(parties, FixtureBrokerSnapshotCodec.Open(sealedSnapshot.Bytes,
            sealedSnapshot.Mac, Secret, plan, parties, observer).Parties);
        Assert.Throws<FixtureHandoffContractException>(() =>
            FixtureBrokerSnapshotCodec.Seal(snapshot with
            {
                Parties = parties with { Issuer = Owner }
            }, Secret));
        observer.ParentageValid = false;
        AssertInvalid(sealedSnapshot.Bytes, sealedSnapshot.Mac, plan,
            parties, observer);
    }

    [Fact]
    public void ReorderedAndDuplicateLiteralEntriesAreRejectedEvenWithValidMac()
    {
        var (plan, snapshot, observer) = NewSnapshot();
        var reordered = snapshot with
        {
            Outputs = snapshot.Outputs.Reverse().ToArray()
        };
        Assert.Throws<FixtureHandoffContractException>(() =>
            FixtureBrokerSnapshotCodec.Seal(reordered, Secret));
        var duplicate = snapshot with
        {
            Outputs = snapshot.Outputs.Select((entry, index) =>
                index == 1 ? entry with
                {
                    Identity = snapshot.Outputs[0].Identity
                } : entry).ToArray()
        };
        Assert.Throws<FixtureHandoffContractException>(() =>
            FixtureBrokerSnapshotCodec.Seal(duplicate, Secret));
        var sealedSnapshot = FixtureBrokerSnapshotCodec.Seal(snapshot, Secret);
        var changed = (byte[])sealedSnapshot.Bytes.Clone();
        changed[118] = (byte)'x';
        AssertInvalid(changed, Sign(changed), plan, snapshot.Parties, observer);
    }

    [Fact]
    public void IndependentProcessAndDeliveredHandleObservationsMustAgree()
    {
        var (plan, snapshot, observer) = NewSnapshot();
        var seal = FixtureBrokerSnapshotCodec.Seal(snapshot, Secret);
        observer.Target = observer.Target with { CreationFileTime = 999 };
        AssertInvalid(seal.Bytes, seal.Mac, plan, snapshot.Parties, observer);
        observer.Target = snapshot.Parties.Target;
        observer.Issuer = new(999, 999);
        AssertInvalid(seal.Bytes, seal.Mac, plan, snapshot.Parties, observer);
        observer.Issuer = snapshot.Parties.Issuer;
        var label = plan.Slots[0].Label;
        observer.Slots[label] = observer.Slots[label] with
        {
            Rights = FixtureHandoffRights.None
        };
        AssertInvalid(seal.Bytes, seal.Mac, plan, snapshot.Parties, observer);
        observer.Slots[label] = observer.Slots[label] with
        {
            Rights = plan.Slots[0].Rights,
            RawValue = observer.Slots[plan.Slots[1].Label].RawValue
        };
        AssertInvalid(seal.Bytes, seal.Mac, plan, snapshot.Parties, observer);
        observer.Slots[label] = observer.Slots[label] with
        {
            RawValue = 500
        };
        observer.ProcessAtCoordinate = new(999, 999);
        AssertInvalid(seal.Bytes, seal.Mac, plan, snapshot.Parties, observer);
        observer.ProcessAtCoordinate = snapshot.Parties.Creator;
        observer.SectionKey = new byte[32];
        AssertInvalid(seal.Bytes, seal.Mac, plan, snapshot.Parties, observer);
    }

    [Fact]
    public void ExactEdgeSlotInventoryIsCanonical()
    {
        var (plan, snapshot, observer) = NewSnapshot();
        var seal = FixtureBrokerSnapshotCodec.Seal(snapshot, Secret);
        var slotOffset = 117 + snapshot.Outputs.Sum(output =>
            1 + Encoding.UTF8.GetByteCount(output.Name) + 1 + 4 + 26 + 26);
        foreach (var (offset, value) in new[]
        {
            (slotOffset, (byte)(plan.Slots.Count + 1)),
            (slotOffset + 2, (byte)'X'),
            (slotOffset + 2 + Encoding.UTF8.GetByteCount(plan.Slots[0].Label) + 1,
                (byte)0)
        })
        {
            var changed = (byte[])seal.Bytes.Clone();
            changed[offset] = value;
            AssertInvalid(changed, Sign(changed), plan, snapshot.Parties, observer);
        }
    }

    private static (FixtureArtifactHandoffPlan Plan, FixtureBrokerSnapshot Snapshot,
        FakeObserver Observer) NewSnapshot()
    {
        var plan = FixtureArtifactHandoffPlan.Create(
            FixtureHandoffEdge.LauncherToRunner, null, Run, Launch);
        var entries = FixtureArtifactHandoffLedger.ExpectedEntries(null)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select((entry, index) => new FixtureBrokerOutput(entry.Key,
                entry.Value.Writer, entry.Value.WriterRights,
                new FixtureArtifactIdentity(7, (ulong)(index + 10), 0,
                    FixtureArtifactKind.File, false), Directory)).ToArray();
        var snapshot = new FixtureBrokerSnapshot(plan.Edge, plan.Scenario,
            Run, Launch, new(Launcher, Launcher, Runner),
            FixtureBrokerDeliveryMode.LocalLauncher, Directory, entries);
        return (plan, snapshot, new FakeObserver(plan, entries,
            snapshot.Parties));
    }

    private static byte[] Sign(byte[] bytes)
    {
        var domain = "WatchDog.fixture.broker.snapshot.v1\0"u8.ToArray();
        var message = new byte[domain.Length + 4 + bytes.Length];
        domain.CopyTo(message, 0);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(domain.Length),
            bytes.Length);
        bytes.CopyTo(message, domain.Length + 4);
        return HMACSHA256.HashData(Secret, message);
    }

    private static void AssertInvalid(byte[] bytes, byte[] mac,
        FixtureArtifactHandoffPlan plan, FixtureBrokerParties parties,
        FakeObserver observer) => Assert.Throws<FixtureHandoffContractException>(() =>
        FixtureBrokerSnapshotCodec.Open(bytes, mac, Secret, plan, parties, observer));

    private sealed class FakeObserver :
        IFixtureBrokerChildLocalObserver
    {
        internal FakeObserver(FixtureArtifactHandoffPlan plan,
            IEnumerable<FixtureBrokerOutput> entries, FixtureBrokerParties parties)
        {
            Target = parties.Target;
            Creator = parties.Creator;
            Issuer = parties.Issuer;
            ProcessAtCoordinate = parties.Creator;
            Outputs = entries.ToDictionary(entry => entry.Name,
                entry => entry.Identity, StringComparer.Ordinal);
            var index = 0;
            foreach (var slot in plan.Slots)
            {
                FixtureArtifactIdentity? identity = slot.Kind switch
                {
                    FixtureHandoffHandleKind.Directory => Directory,
                    FixtureHandoffHandleKind.File =>
                        Outputs[slot.Label[(slot.Label.IndexOf(':') + 1)..]],
                    _ => null
                };
                Slots.Add(slot.Label, new((nint)(500 + index++), slot.Kind,
                    slot.Rights, identity));
            }
        }

        internal FixtureHandoffProcessIdentity Target { get; set; }
        internal FixtureHandoffProcessIdentity Creator { get; set; }
        internal FixtureHandoffProcessIdentity Issuer { get; set; }
        internal FixtureHandoffProcessIdentity ProcessAtCoordinate { get; set; }
        internal byte[] SectionKey { get; set; } = (byte[])Secret.Clone();
        internal FixtureArtifactIdentity Directory { get; set; } =
            FixtureBrokerSnapshotTests.Directory;
        internal Dictionary<string, FixtureArtifactIdentity> Outputs { get; }
        internal Dictionary<string, FixtureHandoffObservedHandle> Slots { get; } =
            new(StringComparer.Ordinal);
        internal bool ParentageValid { get; set; } = true;
        public FixtureHandoffProcessIdentity ObserveTargetProcess() => Target;
        public FixtureHandoffProcessIdentity ObserveCreatorProcess() => Creator;
        public FixtureHandoffProcessIdentity ObserveIssuerProcess() => Issuer;
        public FixtureArtifactIdentity ObserveRetainedDirectory() => Directory;
        public FixtureArtifactIdentity ObserveOutput(string literalName) => Outputs[literalName];
        public int DeliveredSlotCount => Slots.Count;
        public FixtureHandoffObservedHandle ObserveDeliveredSlot(string label) => Slots[label];
        public FixtureHandoffProcessIdentity ObserveProcessAtTargetCoordinate(
            nint coordinate) => Slots.Values.Any(slot =>
                slot.Kind == FixtureHandoffHandleKind.Process &&
                slot.RawValue == coordinate) ? ProcessAtCoordinate : new(0, 0);
        public byte[] ReadSectionKeyAtTargetCoordinate(nint coordinate) =>
            Slots["artifact-auth-section"].RawValue == coordinate
                ? (byte[])SectionKey.Clone() : new byte[32];
        public bool IsExactChild(FixtureArtifactIdentity parent, string literalName,
            FixtureArtifactIdentity output) => parent == Directory &&
            ParentageValid && Outputs.TryGetValue(literalName, out var observed) &&
            observed == output;
    }
}
