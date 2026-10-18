using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace WatchDog.Integration.Tests.Fixtures;

// Fixture-private classifier of copied managed data. No Windows lookup, effective-access
// assertion, custody-policy conversion, or writer admission is provided by this type.
internal sealed class OwnerSidOnlyAllowDaclV1
{
    private const int MaxSampleBytes = 64 * 1024;
    private const int MaxSidBytes = 8 + 4 * 15;
    private const uint FileAllAccess = 0x001F01FF;
    private const int AceEncodingHeaderBytes = 18;
    private static readonly byte[] DigestDomain = Encoding.ASCII.GetBytes("WatchDog.OwnerSidOnlyAllowDaclV1\0");
    private static readonly int ProjectionEncodingHeaderBytes = DigestDomain.Length + 30;

    private readonly ImmutableArray<byte> _intendedSid;
    private readonly Projection? _projection;
    private readonly string? _digest;
    private readonly DescriptorProfileReason _captureFailure;

    private OwnerSidOnlyAllowDaclV1(DescriptorProfileReason failure) => _captureFailure = failure;

    private OwnerSidOnlyAllowDaclV1(ImmutableArray<byte> intendedSid, Projection projection)
    {
        _intendedSid = intendedSid;
        _projection = projection;
        _digest = ComputeDigest(projection);
    }

    // Mutable transport arrays are accepted only here and copied into a bounded snapshot.
    // Concurrent caller mutation is not an atomic external observation; the frozen copy
    // is simply the value classified. Later caller mutations cannot change it.
    internal static OwnerSidOnlyAllowDaclV1 Capture(byte[]? intendedSid, OwnerDaclProjectionInput? input)
    {
        if (intendedSid is null || input is null || input.OwnerSid is null || input.Aces is null)
            return new(DescriptorProfileReason.MissingInput);
        if (intendedSid.Length > MaxSidBytes)
            return new(DescriptorProfileReason.InvalidSid);

        var budget = MaxSampleBytes - intendedSid.Length - ProjectionEncodingHeaderBytes;
        if (!TakeBytes(ref budget, input.OwnerSid.Length) ||
            input.Aces.Length > budget / AceEncodingHeaderBytes)
            return new(DescriptorProfileReason.InputTooLarge);

        var intendedCopy = ImmutableArray.CreateRange(intendedSid);
        var ownerCopy = ImmutableArray.CreateRange(input.OwnerSid);
        var aces = ImmutableArray.CreateBuilder<Ace>(input.Aces.Length);
        foreach (var inputAce in input.Aces)
        {
            if (inputAce?.TrusteeSid is null || inputAce.ExtraPayload is null)
                return new(DescriptorProfileReason.MissingInput);
            if (!TakeBytes(ref budget, AceEncodingHeaderBytes) ||
                !TakeBytes(ref budget, inputAce.TrusteeSid.Length) ||
                !TakeBytes(ref budget, inputAce.ExtraPayload.Length))
                return new(DescriptorProfileReason.InputTooLarge);

            aces.Add(new(inputAce.Type, inputAce.Flags, inputAce.DeclaredSize, inputAce.Mask,
                ImmutableArray.CreateRange(inputAce.TrusteeSid), ImmutableArray.CreateRange(inputAce.ExtraPayload)));
        }

        return new(intendedCopy, new(input.SchemaVersion, input.DescriptorRevision, ownerCopy,
            input.Flags, input.UnknownOwnerDaclControlBits, input.AclRevision,
            input.DeclaredAclSize, input.DeclaredAceCount, aces.MoveToImmutable()));
    }

    internal DescriptorProfileResult Evaluate()
    {
        if (_projection is not { } projection)
            return Result(DescriptorProfileStatus.InvalidData, _captureFailure);
        if (!ValidSid(_intendedSid) || !ValidSid(projection.OwnerSid) ||
            projection.Aces.Any(ace => !ValidSid(ace.TrusteeSid)))
            return Result(DescriptorProfileStatus.InvalidData, DescriptorProfileReason.InvalidSid);
        if (SubstitutionSid(_intendedSid) || SubstitutionSid(projection.OwnerSid) ||
            projection.Aces.Any(ace => SubstitutionSid(ace.TrusteeSid)))
            return Result(DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.SubstitutionSid);
        if (projection.SchemaVersion != 1)
            return Result(DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.SchemaVersion);
        if (projection.DescriptorRevision != 1)
            return Result(DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.DescriptorRevision);
        if (projection.AclRevision != 2)
            return Result(DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.AclRevision);

        const OwnerDaclProjectionFlags supportedFlags = OwnerDaclProjectionFlags.Present |
            OwnerDaclProjectionFlags.Null | OwnerDaclProjectionFlags.Protected;
        if ((projection.Flags & ~supportedFlags) != 0 || projection.UnknownOwnerDaclControlBits != 0)
            return Result(DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.ControlFlags);
        if (projection.DeclaredAceCount != (uint)projection.Aces.Length)
            return Result(DescriptorProfileStatus.InvalidData, DescriptorProfileReason.AceCount);

        long aclSize = 8;
        foreach (var ace in projection.Aces)
        {
            var size = 8L + ace.TrusteeSid.Length + ace.ExtraPayload.Length;
            if (ace.DeclaredSize != size || size % 4 != 0 || size > ushort.MaxValue)
                return Result(DescriptorProfileStatus.InvalidData, DescriptorProfileReason.AceSize);
            aclSize += size;
        }
        if (projection.DeclaredAclSize != aclSize || aclSize > ushort.MaxValue)
            return Result(DescriptorProfileStatus.InvalidData, DescriptorProfileReason.AclSize);

        if (projection.Flags != (OwnerDaclProjectionFlags.Present | OwnerDaclProjectionFlags.Protected))
            return Result(DescriptorProfileStatus.DoesNotMatchProfile, DescriptorProfileReason.DaclShape);
        if (!projection.OwnerSid.AsSpan().SequenceEqual(_intendedSid.AsSpan()))
            return Result(DescriptorProfileStatus.DoesNotMatchProfile, DescriptorProfileReason.OwnerMismatch);
        if (projection.Aces.Length != 1)
            return Result(DescriptorProfileStatus.DoesNotMatchProfile, DescriptorProfileReason.AceCount);

        var only = projection.Aces[0];
        if (only.Type != 0)
            return Result(DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.AceType);
        if (only.Flags != 0)
            return Result(DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.AceFlags);
        if (!only.ExtraPayload.IsEmpty)
            return Result(DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.ExtraAcePayload);
        if ((only.Mask & ~FileAllAccess) != 0)
            return Result(DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.AccessMask);
        if (only.Mask != FileAllAccess)
            return Result(DescriptorProfileStatus.DoesNotMatchProfile, DescriptorProfileReason.AccessMask);
        if (!only.TrusteeSid.AsSpan().SequenceEqual(_intendedSid.AsSpan()))
            return Result(DescriptorProfileStatus.DoesNotMatchProfile, DescriptorProfileReason.TrusteeMismatch);

        return Result(DescriptorProfileStatus.MatchesProfile, DescriptorProfileReason.None);
    }

    private DescriptorProfileResult Result(DescriptorProfileStatus status, DescriptorProfileReason reason) =>
        new(1, status, reason, _digest);

    private static bool TakeBytes(ref int budget, int count)
    {
        if (count > budget) return false;
        budget -= count;
        return true;
    }

    private static bool ValidSid(ImmutableArray<byte> sid) =>
        sid.Length >= 8 && sid[0] == 1 && sid[1] <= 15 && sid.Length == 8 + 4 * sid[1];

    private static bool SubstitutionSid(ImmutableArray<byte> sid)
    {
        // Identifier authority is a 48-bit big-endian field; subauthorities are LE.
        var smallAuthority = sid[2] == 0 && sid[3] == 0 && sid[4] == 0 && sid[5] == 0 && sid[6] == 0;
        return smallAuthority && (sid[7] == 3 ||
            (sid[7] == 5 && sid[1] == 1 && BinaryPrimitives.ReadUInt32LittleEndian(sid.AsSpan()[8..]) == 10));
    }

    private static string ComputeDigest(Projection projection)
    {
        // Versioned fixture encoding, not a native SECURITY_DESCRIPTOR serialization.
        // All integers are little endian. Variable byte fields carry UInt32 lengths.
        // Intended SID is deliberately excluded: a projection hash is not intent authority.
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(DigestDomain);
            writer.Write(projection.SchemaVersion);
            writer.Write(projection.DescriptorRevision);
            writer.Write((uint)projection.Flags);
            writer.Write(projection.UnknownOwnerDaclControlBits);
            writer.Write(projection.AclRevision);
            writer.Write(projection.DeclaredAclSize);
            writer.Write(projection.DeclaredAceCount);
            WriteBytes(writer, projection.OwnerSid);
            writer.Write((uint)projection.Aces.Length);
            foreach (var ace in projection.Aces)
            {
                writer.Write(ace.Type);
                writer.Write(ace.Flags);
                writer.Write(ace.DeclaredSize);
                writer.Write(ace.Mask);
                WriteBytes(writer, ace.TrusteeSid);
                WriteBytes(writer, ace.ExtraPayload);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.GetBuffer().AsSpan(0, (int)stream.Length)));
    }

    private static void WriteBytes(BinaryWriter writer, ImmutableArray<byte> bytes)
    {
        writer.Write((uint)bytes.Length);
        writer.Write(bytes.AsSpan());
    }

    private sealed record Projection(uint SchemaVersion, byte DescriptorRevision, ImmutableArray<byte> OwnerSid,
        OwnerDaclProjectionFlags Flags, uint UnknownOwnerDaclControlBits, byte AclRevision,
        uint DeclaredAclSize, uint DeclaredAceCount, ImmutableArray<Ace> Aces);

    private sealed record Ace(byte Type, byte Flags, uint DeclaredSize, uint Mask,
        ImmutableArray<byte> TrusteeSid, ImmutableArray<byte> ExtraPayload);
}

// Raw managed transport values for the snapshot boundary; never interpreted as native
// pointers. Primary-group/SACL, provenance, privileges and effective access are unassessed.
internal sealed record OwnerDaclProjectionInput(uint SchemaVersion, byte DescriptorRevision, byte[]? OwnerSid,
    OwnerDaclProjectionFlags Flags, uint UnknownOwnerDaclControlBits, byte AclRevision,
    uint DeclaredAclSize, uint DeclaredAceCount, OwnerDaclAceInput?[]? Aces);

internal sealed record OwnerDaclAceInput(byte Type, byte Flags, uint DeclaredSize, uint Mask,
    byte[]? TrusteeSid, byte[]? ExtraPayload);

[Flags]
internal enum OwnerDaclProjectionFlags : uint
{
    Present = 1,
    Null = 2,
    Protected = 4,
    OwnerDefaulted = 8,
    DaclDefaulted = 16,
    AutoInheritRequested = 32,
    AutoInherited = 64,
}

internal enum DescriptorProfileStatus { InvalidData, MatchesProfile, DoesNotMatchProfile, UnsupportedRepresentation }

internal enum DescriptorProfileReason
{
    None,
    MissingInput,
    InputTooLarge,
    InvalidSid,
    SubstitutionSid,
    SchemaVersion,
    DescriptorRevision,
    AclRevision,
    ControlFlags,
    AceCount,
    AceSize,
    AclSize,
    DaclShape,
    OwnerMismatch,
    TrusteeMismatch,
    AceType,
    AceFlags,
    ExtraAcePayload,
    AccessMask,
}

internal sealed record DescriptorProfileResult(uint SchemaVersion, DescriptorProfileStatus Status,
    DescriptorProfileReason Reason, string? ProjectionDigest);
