using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

// Pure fixture policy. Bytes are a child-local copy, never a parent-ledger reference.
internal enum FixtureBrokerDeliveryMode : byte { LocalLauncher = 1, DirectIssuer = 2 }

internal sealed record FixtureBrokerParties(
    FixtureHandoffProcessIdentity Creator,
    FixtureHandoffProcessIdentity Issuer,
    FixtureHandoffProcessIdentity Target);

internal sealed record FixtureBrokerOutput(
    string Name, QualificationRole Writer, FixtureHandoffRights Rights,
    FixtureArtifactIdentity Identity, FixtureArtifactIdentity Parent);

internal sealed record FixtureBrokerSnapshot(
    FixtureHandoffEdge Edge, QualificationScenario? Scenario,
    Guid RunNonce, Guid LaunchId, FixtureBrokerParties Parties,
    FixtureBrokerDeliveryMode Delivery, FixtureArtifactIdentity Directory,
    IReadOnlyList<FixtureBrokerOutput> Outputs);

internal interface IFixtureBrokerChildLocalObserver
{
    FixtureHandoffProcessIdentity ObserveTargetProcess();
    FixtureHandoffProcessIdentity ObserveCreatorProcess();
    FixtureHandoffProcessIdentity ObserveIssuerProcess();
    FixtureArtifactIdentity ObserveRetainedDirectory();
    FixtureArtifactIdentity ObserveOutput(string literalName);
    int DeliveredSlotCount { get; }
    FixtureHandoffObservedHandle ObserveDeliveredSlot(string label);
    FixtureHandoffProcessIdentity ObserveProcessAtTargetCoordinate(nint coordinate);
    // Return an owned copy; the validator erases it after comparison.
    byte[] ReadSectionKeyAtTargetCoordinate(nint coordinate);
    bool IsExactChild(FixtureArtifactIdentity parent, string literalName,
        FixtureArtifactIdentity output);
}

internal static class FixtureBrokerSnapshotCodec
{
    private static readonly byte[] Magic = "WDBRK001"u8.ToArray();
    private static readonly byte[] MacDomain = "WatchDog.fixture.broker.snapshot.v1\0"u8.ToArray();
    private const int MaxBytes = 8192;
    private const int MaxNameBytes = 96;
    private const int MacSize = 32;
    private const uint AllowedRights = (uint)(FixtureHandoffRights.FileAppend |
        FixtureHandoffRights.FileWrite | FixtureHandoffRights.FileRead);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static (byte[] Bytes, byte[] Mac) Seal(
        FixtureBrokerSnapshot snapshot, ReadOnlySpan<byte> secret)
    {
        Validate(snapshot);
        if (secret.Length != MacSize)
            throw Invalid();
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write((ushort)1);
            writer.Write((ushort)0); // Reserved flags; no extension is silently accepted.
            writer.Write((int)snapshot.Edge);
            writer.Write(snapshot.Scenario is null ? -1 : (int)snapshot.Scenario.Value);
            writer.Write(snapshot.RunNonce.ToByteArray());
            writer.Write(snapshot.LaunchId.ToByteArray());
            WriteProcess(writer, snapshot.Parties.Creator);
            WriteProcess(writer, snapshot.Parties.Issuer);
            WriteProcess(writer, snapshot.Parties.Target);
            writer.Write((byte)snapshot.Delivery);
            WriteIdentity(writer, snapshot.Directory);
            writer.Write((ushort)snapshot.Outputs.Count);
            foreach (var output in snapshot.Outputs)
            {
                var name = StrictUtf8.GetBytes(output.Name);
                writer.Write((byte)name.Length);
                writer.Write(name);
                writer.Write((byte)output.Writer);
                writer.Write((uint)output.Rights);
                WriteIdentity(writer, output.Identity);
                WriteIdentity(writer, output.Parent);
            }
            var plan = FixtureArtifactHandoffPlan.Create(snapshot.Edge,
                snapshot.Scenario, snapshot.RunNonce, snapshot.LaunchId);
            writer.Write((byte)plan.Slots.Count);
            foreach (var slot in plan.Slots)
            {
                var label = StrictUtf8.GetBytes(slot.Label);
                writer.Write((byte)label.Length);
                writer.Write(label);
                writer.Write((byte)slot.Kind);
                writer.Write((uint)slot.Rights);
                var identity = SlotIdentity(snapshot, slot);
                writer.Write((byte)(identity is null ? 0 : 1));
                if (identity is not null)
                    WriteIdentity(writer, identity);
            }
        }
        var bytes = stream.ToArray();
        if (bytes.Length > MaxBytes)
            throw Invalid();
        return (bytes, Mac(bytes, secret));
    }

    internal static FixtureBrokerSnapshot Open(
        ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> mac,
        ReadOnlySpan<byte> secret, FixtureArtifactHandoffPlan expectedPlan,
        FixtureBrokerParties expectedParties,
        IFixtureBrokerChildLocalObserver observer)
    {
        ArgumentNullException.ThrowIfNull(expectedPlan);
        ArgumentNullException.ThrowIfNull(expectedParties);
        ArgumentNullException.ThrowIfNull(observer);
        if (bytes.Length > MaxBytes || bytes.Length < 128 || mac.Length != MacSize ||
            secret.Length != MacSize)
            throw Invalid();
        var expectedMac = Mac(bytes, secret);
        if (!CryptographicOperations.FixedTimeEquals(expectedMac, mac))
            throw Invalid();

        // Every length and count is checked before allocation. A fixed-width schema and
        // exact EOF make the accepted representation unique.
        var reader = new Reader(bytes);
        if (!reader.Take(Magic.Length).SequenceEqual(Magic) ||
            reader.U16() != 1 || reader.U16() != 0)
            throw Invalid();
        var edge = (FixtureHandoffEdge)reader.I32();
        var scenarioValue = reader.I32();
        var scenario = scenarioValue == -1 ? null : (QualificationScenario?)scenarioValue;
        var runNonce = new Guid(reader.Take(16));
        var launchId = new Guid(reader.Take(16));
        var parties = new FixtureBrokerParties(
            ReadProcess(ref reader), ReadProcess(ref reader), ReadProcess(ref reader));
        var delivery = (FixtureBrokerDeliveryMode)reader.U8();
        var directory = ReadIdentity(ref reader);
        var count = reader.U16();
        var expected = FixtureArtifactHandoffLedger.ExpectedEntries(scenario);
        if (count != expected.Count || count > 16)
            throw Invalid();
        var ordered = expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        var outputs = new List<FixtureBrokerOutput>(count);
        for (var index = 0; index < count; index++)
        {
            var nameLength = reader.U8();
            if (nameLength is 0 or > MaxNameBytes)
                throw Invalid();
            string name;
            try { name = StrictUtf8.GetString(reader.Take(nameLength)); }
            catch (DecoderFallbackException) { throw Invalid(); }
            if (!string.Equals(name, ordered[index].Key, StringComparison.Ordinal))
                throw Invalid();
            outputs.Add(new(name, (QualificationRole)reader.U8(),
                (FixtureHandoffRights)reader.U32(), ReadIdentity(ref reader),
                ReadIdentity(ref reader)));
        }
        var slots = expectedPlan.Slots;
        if (reader.U8() != slots.Count)
            throw Invalid();
        for (var index = 0; index < slots.Count; index++)
        {
            var slot = slots[index];
            var labelLength = reader.U8();
            if (labelLength is 0 or > MaxNameBytes)
                throw Invalid();
            string label;
            try { label = StrictUtf8.GetString(reader.Take(labelLength)); }
            catch (DecoderFallbackException) { throw Invalid(); }
            var kind = (FixtureHandoffHandleKind)reader.U8();
            var rights = (FixtureHandoffRights)reader.U32();
            var hasIdentity = reader.U8();
            if (hasIdentity > 1)
                throw Invalid();
            var identity = hasIdentity == 1 ? ReadIdentity(ref reader) : null;
            var expectedIdentity = SlotIdentity(directory, outputs, slot);
            if (label != slot.Label || kind != slot.Kind || rights != slot.Rights ||
                identity != expectedIdentity)
                throw Invalid();
        }
        if (!reader.AtEnd)
            throw Invalid();
        var snapshot = new FixtureBrokerSnapshot(edge, scenario, runNonce, launchId,
            parties, delivery, directory, outputs.AsReadOnly());
        Validate(snapshot);
        if (edge != expectedPlan.Edge || scenario != expectedPlan.Scenario ||
            runNonce != expectedPlan.RunNonce || launchId != expectedPlan.LaunchId ||
            parties != expectedParties)
            throw Invalid();
        // The child obtains these observations from its own retained handles. It cannot
        // consult the issuer's live ledger to establish authority.
        if (observer.ObserveTargetProcess() != parties.Target ||
            observer.ObserveCreatorProcess() != parties.Creator ||
            observer.ObserveIssuerProcess() != parties.Issuer ||
            observer.ObserveRetainedDirectory() != directory)
            throw Invalid();
        foreach (var output in outputs)
            if (observer.ObserveOutput(output.Name) != output.Identity ||
                !observer.IsExactChild(directory, output.Name, output.Identity))
                throw Invalid();
        if (observer.DeliveredSlotCount != slots.Count)
            throw Invalid();
        var targetCoordinates = new HashSet<nint>();
        foreach (var slot in slots)
        {
            var observed = observer.ObserveDeliveredSlot(slot.Label);
            if (observed.RawValue is 0 or -1 ||
                !targetCoordinates.Add(observed.RawValue) ||
                observed.Kind != slot.Kind || observed.Rights != slot.Rights ||
                observed.Identity != SlotIdentity(directory, outputs, slot))
                throw Invalid();
            if (slot.Kind == FixtureHandoffHandleKind.Process &&
                observer.ObserveProcessAtTargetCoordinate(observed.RawValue) !=
                    parties.Creator)
                throw Invalid();
            if (slot.Label == "artifact-auth-section")
            {
                var sectionKey = observer.ReadSectionKeyAtTargetCoordinate(
                    observed.RawValue);
                try
                {
                    if (sectionKey is null || sectionKey.Length != MacSize ||
                        !CryptographicOperations.FixedTimeEquals(sectionKey, secret))
                        throw Invalid();
                }
                finally
                {
                    if (sectionKey is not null)
                        CryptographicOperations.ZeroMemory(sectionKey);
                }
            }
        }
        return snapshot;
    }

    internal static FixtureBrokerSnapshot FromLedger(
        FixtureArtifactHandoffPlan plan, FixtureArtifactHandoffLedger ledger,
        FixtureBrokerParties parties, FixtureBrokerDeliveryMode delivery)
    {
        if (plan.Scenario != ledger.Scenario || !ledger.IsStillAdmitted)
            throw Invalid();
        var outputs = ledger.Entries.Values.OrderBy(entry => entry.Name,
            StringComparer.Ordinal).Select(entry => new FixtureBrokerOutput(
                entry.Name, entry.Writer, entry.WriterRights, entry.Identity,
                ledger.Directory)).ToArray();
        var result = new FixtureBrokerSnapshot(plan.Edge, plan.Scenario,
            plan.RunNonce, plan.LaunchId, parties, delivery, ledger.Directory, outputs);
        Validate(result);
        return result;
    }

    private static void Validate(FixtureBrokerSnapshot value)
    {
        if (value is null || value.Parties is null || value.Outputs is null ||
            value.RunNonce == Guid.Empty || value.LaunchId == Guid.Empty ||
            value.RunNonce == value.LaunchId || !value.Parties.Creator.IsValid ||
            !value.Parties.Issuer.IsValid || !value.Parties.Target.IsValid ||
            value.Parties.Target == value.Parties.Creator ||
            value.Parties.Target == value.Parties.Issuer ||
            !ValidIdentity(value.Directory, FixtureArtifactKind.Directory,
                value.Directory.Volume))
            throw Invalid();
        var plan = FixtureArtifactHandoffPlan.Create(value.Edge, value.Scenario,
            value.RunNonce, value.LaunchId);
        if (value.Delivery != (value.Edge == FixtureHandoffEdge.LauncherToRunner
                ? FixtureBrokerDeliveryMode.LocalLauncher
                : FixtureBrokerDeliveryMode.DirectIssuer) ||
            (value.Parties.Creator == value.Parties.Issuer) !=
                (value.Edge is FixtureHandoffEdge.LauncherToRunner or
                    FixtureHandoffEdge.RunnerToOwner or
                    FixtureHandoffEdge.RunnerToBootstrapHelper) ||
            plan.Receiver == QualificationRole.Runner && value.Scenario is not null)
            throw Invalid();
        var expected = FixtureArtifactHandoffLedger.ExpectedEntries(value.Scenario)
            .OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        if (value.Outputs.Count != expected.Length || expected.Length > 16)
            throw Invalid();
        var identities = new HashSet<FixtureArtifactIdentity> { value.Directory };
        for (var index = 0; index < expected.Length; index++)
        {
            var output = value.Outputs[index];
            if (output is null || output.Name != expected[index].Key ||
                output.Writer != expected[index].Value.Writer ||
                output.Rights != expected[index].Value.WriterRights ||
                ((uint)output.Rights & ~AllowedRights) != 0 ||
                output.Parent != value.Directory ||
                !ValidIdentity(output.Identity, FixtureArtifactKind.File,
                    value.Directory.Volume) || !identities.Add(output.Identity) ||
                StrictUtf8.GetByteCount(output.Name) > MaxNameBytes)
                throw Invalid();
        }
    }

    private static bool ValidIdentity(FixtureArtifactIdentity identity,
        FixtureArtifactKind kind, ulong volume) => identity is not null &&
        volume != 0 && identity.Volume == volume &&
        (identity.FileIdLow != 0 || identity.FileIdHigh != 0) &&
        identity.Kind == kind && !identity.IsReparse;

    private static FixtureArtifactIdentity? SlotIdentity(
        FixtureBrokerSnapshot snapshot, FixtureHandoffSlot slot) =>
        SlotIdentity(snapshot.Directory, snapshot.Outputs, slot);

    private static FixtureArtifactIdentity? SlotIdentity(
        FixtureArtifactIdentity directory, IReadOnlyList<FixtureBrokerOutput> outputs,
        FixtureHandoffSlot slot)
    {
        if (slot.Kind == FixtureHandoffHandleKind.Directory)
            return directory;
        if (slot.Kind != FixtureHandoffHandleKind.File)
            return null;
        var colon = slot.Label.IndexOf(':');
        if (colon < 0)
            throw Invalid();
        var name = slot.Label[(colon + 1)..];
        var output = outputs.SingleOrDefault(entry => entry.Name == name);
        return output?.Identity ?? throw Invalid();
    }

    private static byte[] Mac(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> secret)
    {
        var message = new byte[MacDomain.Length + 4 + bytes.Length];
        MacDomain.CopyTo(message, 0);
        BinaryPrimitives.WriteInt32LittleEndian(message.AsSpan(MacDomain.Length), bytes.Length);
        bytes.CopyTo(message.AsSpan(MacDomain.Length + 4));
        try { return HMACSHA256.HashData(secret, message); }
        finally { CryptographicOperations.ZeroMemory(message); }
    }

    private static void WriteProcess(BinaryWriter writer,
        FixtureHandoffProcessIdentity process)
    {
        writer.Write(process.ProcessId);
        writer.Write(process.CreationFileTime);
    }

    private static FixtureHandoffProcessIdentity ReadProcess(ref Reader reader) =>
        new(reader.U32(), reader.I64());

    private static void WriteIdentity(BinaryWriter writer, FixtureArtifactIdentity identity)
    {
        writer.Write(identity.Volume);
        writer.Write(identity.FileIdLow);
        writer.Write(identity.FileIdHigh);
        writer.Write((byte)identity.Kind);
        writer.Write((byte)(identity.IsReparse ? 1 : 0));
    }

    private static FixtureArtifactIdentity ReadIdentity(ref Reader reader)
    {
        var volume = reader.U64();
        var low = reader.U64();
        var high = reader.U64();
        var kind = (FixtureArtifactKind)reader.U8();
        var reparse = reader.U8();
        if (reparse > 1)
            throw Invalid();
        return new(volume, low, high, kind, reparse == 1);
    }

    private static FixtureHandoffContractException Invalid() =>
        new("fixture.broker.snapshot-invalid");

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _offset;
        internal bool AtEnd => _offset == _bytes.Length;
        internal ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > _bytes.Length - _offset)
                throw Invalid();
            var result = _bytes.Slice(_offset, count);
            _offset += count;
            return result;
        }
        internal byte U8() => Take(1)[0];
        internal ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        internal uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        internal int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        internal ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        internal long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
    }
}
