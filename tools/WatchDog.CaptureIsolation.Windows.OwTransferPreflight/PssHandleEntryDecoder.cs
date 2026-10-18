using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Text;

namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal enum PssHandleDecodeStatus
{
    Decoded,
    Incomplete,
    Invalid,
}

[Flags]
internal enum SyntheticPssHandleFlags : uint
{
    None = 0,
    Type = 0x1,
    Name = 0x2,
    Basic = 0x4,
    TypeSpecific = 0x8,
}

internal enum SyntheticPssObjectType : uint
{
    Unknown = 0,
    Process = 1,
    Thread = 2,
    Mutant = 3,
    Event = 4,
    Section = 5,
    Semaphore = 6,
}

internal sealed class SyntheticPssNameBuffers
{
    internal const int MaximumBufferCount = 16_384;
    internal const int MaximumBufferLength = 65_536;
    internal const int MaximumAggregateBytes = 8 * 1024 * 1024;

    private readonly IReadOnlyDictionary<ulong, byte[]> _buffers;

    internal SyntheticPssNameBuffers(Dictionary<ulong, byte[]> buffers)
    {
        ArgumentNullException.ThrowIfNull(buffers);
        if (buffers.Count > MaximumBufferCount)
        {
            throw new ArgumentException("Synthetic name buffer count exceeds the bound.", nameof(buffers));
        }

        var copy = new Dictionary<ulong, byte[]>();
        var aggregateBytes = 0L;
        foreach (var pair in buffers)
        {
            if (pair.Key == 0 || pair.Value is null || pair.Value.Length > MaximumBufferLength)
            {
                throw new ArgumentException("Synthetic name buffers require nonzero pointers and bounded non-null bytes.", nameof(buffers));
            }

            aggregateBytes += pair.Value.Length;
            if (aggregateBytes > MaximumAggregateBytes)
            {
                throw new ArgumentException("Synthetic name buffer bytes exceed the aggregate bound.", nameof(buffers));
            }

            copy.Add(pair.Key, pair.Value.ToArray());
        }

        _buffers = new ReadOnlyDictionary<ulong, byte[]>(copy);
    }

    internal bool TryCopy(ulong pointer, int byteLength, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (!_buffers.TryGetValue(pointer, out var source) || byteLength < 0 || source.Length < byteLength)
        {
            return false;
        }

        bytes = source.AsSpan(0, byteLength).ToArray();
        return true;
    }
}

internal sealed record PssCopiedName(
    ushort ByteLength,
    string Value,
    IReadOnlyList<byte> CopiedBytes);

internal sealed record PssRawNameReference(
    ushort ByteLength,
    ulong Pointer);

internal sealed record PssRawBasicFields(
    uint Attributes,
    uint GrantedAccess,
    uint HandleCount,
    uint PointerCount,
    uint PagedPoolCharge,
    uint NonPagedPoolCharge,
    ulong ReservedCreationTime);

internal abstract record PssTypeSpecificInformation;

internal sealed record PssProcessInformation(
    uint ExitStatus,
    ulong PebBaseAddress,
    ulong AffinityMask,
    int BasePriority,
    uint ProcessId,
    uint ParentProcessId,
    uint Flags) : PssTypeSpecificInformation;

internal sealed record PssThreadInformation(
    uint ExitStatus,
    ulong TebBaseAddress,
    uint ProcessId,
    uint ThreadId,
    ulong AffinityMask,
    int Priority,
    int BasePriority,
    ulong StartAddress) : PssTypeSpecificInformation;

internal sealed record PssMutantInformation(
    int CurrentCount,
    bool Abandoned,
    uint OwnerProcessId,
    uint OwnerThreadId) : PssTypeSpecificInformation;

internal sealed record PssEventInformation(
    bool ManualReset,
    bool Signaled) : PssTypeSpecificInformation;

internal sealed record PssSectionInformation(
    ulong BaseAddress,
    uint AllocationAttributes,
    long MaximumSize) : PssTypeSpecificInformation;

internal sealed record PssSemaphoreInformation(
    int CurrentCount,
    int MaximumCount) : PssTypeSpecificInformation;

internal sealed record PssHandleDecodeResult(
    PssHandleDecodeStatus Status,
    ulong Handle,
    uint RawFlags,
    uint RawObjectType,
    ulong CaptureTime,
    ulong ReservedCreationTime,
    PssRawBasicFields? RawBasicFields,
    PssRawNameReference RawTypeNameReference,
    PssCopiedName? TypeName,
    PssRawNameReference RawObjectNameReference,
    PssCopiedName? ObjectName,
    PssTypeSpecificInformation? TypeSpecificInformation,
    IReadOnlyList<byte> RawTypeSpecificBytes,
    IReadOnlyList<string> InvalidReasons,
    IReadOnlyList<string> IncompleteReasons)
{
    internal bool InheritableKnown => false;

    internal bool TargetCreationTimeKnown => false;

    internal bool NativeAuthorityProven => false;

    internal bool NativeDurabilityProven => false;

    internal bool CompleteHandleInventoryProven => false;

    internal bool ProductionAdmissionGranted => false;
}

internal static class PssHandleEntryDecoder
{
    internal const int EntrySize = 136;
    internal const int UnionOffset = 88;
    internal const int UnionSize = 48;
    internal const int MaximumNameByteLength = 65_534;

    private const uint KnownFlags = 0x0f;
    private static readonly Encoding StrictUtf16 = new UnicodeEncoding(false, true, true);

    internal static PssHandleDecodeResult Decode(
        ReadOnlySpan<byte> entry,
        SyntheticPssNameBuffers? nameBuffers = null,
        int maximumNameByteLength = MaximumNameByteLength)
    {
        var invalid = new List<string>();
        var incomplete = new List<string>();
        if (maximumNameByteLength < 0 || maximumNameByteLength > MaximumNameByteLength)
        {
            invalid.Add("name-bound-invalid");
            maximumNameByteLength = MaximumNameByteLength;
        }

        if (entry.Length != EntrySize)
        {
            invalid.Add("entry-size-not-136");
            return EmptyInvalid(invalid);
        }

        // Parsing only this private copy prevents later caller mutation from changing the result.
        var snapshot = entry.ToArray();
        var span = snapshot.AsSpan();
        var handle = U64(span, 0);
        var flags = U32(span, 8);
        var objectType = U32(span, 12);
        var captureTime = U64(span, 16);
        var reservedCreation = U64(span, 48);
        var flagSet = (SyntheticPssHandleFlags)flags;

        if ((flags & ~KnownFlags) != 0)
        {
            incomplete.Add("unknown-handle-flags");
        }

        var typeIsValid = flagSet.HasFlag(SyntheticPssHandleFlags.Type);
        var supportedType = objectType is >= (uint)SyntheticPssObjectType.Process and <= (uint)SyntheticPssObjectType.Semaphore;
        if (typeIsValid && !supportedType)
        {
            incomplete.Add("unsupported-object-type");
        }
        else if (!typeIsValid)
        {
            incomplete.Add(objectType == 0 ? "object-type-unavailable" : "object-type-present-without-validity");
        }

        PssRawBasicFields? basic = null;
        var rawBasicHasData = span.Slice(24, 24).IndexOfAnyExcept((byte)0) >= 0;
        if (flagSet.HasFlag(SyntheticPssHandleFlags.Basic))
        {
            basic = new PssRawBasicFields(
                U32(span, 24),
                U32(span, 28),
                U32(span, 32),
                U32(span, 36),
                U32(span, 40),
                U32(span, 44),
                reservedCreation);
        }
        else if (rawBasicHasData)
        {
            incomplete.Add("basic-fields-present-without-validity");
        }

        var rawTypeName = new PssRawNameReference(U16(span, 56), U64(span, 64));
        PssCopiedName? typeName = null;
        if (rawTypeName.ByteLength != 0 || rawTypeName.Pointer != 0)
        {
            incomplete.Add("type-name-validity-undocumented");
        }

        var rawObjectName = new PssRawNameReference(U16(span, 72), U64(span, 80));
        var objectName = DecodeName(
            "object-name",
            flagSet.HasFlag(SyntheticPssHandleFlags.Name),
            U16(span, 72),
            U64(span, 80),
            nameBuffers,
            maximumNameByteLength,
            invalid,
            incomplete);

        var rawUnion = Array.AsReadOnly(span.Slice(UnionOffset, UnionSize).ToArray());
        PssTypeSpecificInformation? union = null;
        if (flagSet.HasFlag(SyntheticPssHandleFlags.TypeSpecific))
        {
            if (!flagSet.HasFlag(SyntheticPssHandleFlags.Type))
            {
                invalid.Add("type-specific-without-type-validity");
            }
            else if (!supportedType || objectType == 0)
            {
                incomplete.Add("type-specific-union-unsupported");
            }
            else
            {
                union = DecodeUnion((SyntheticPssObjectType)objectType, span.Slice(UnionOffset, UnionSize));
            }
        }
        else if (rawUnion.Any(value => value != 0))
        {
            incomplete.Add("type-specific-bytes-present-without-validity");
        }

        var status = invalid.Count > 0
            ? PssHandleDecodeStatus.Invalid
            : incomplete.Count > 0
                ? PssHandleDecodeStatus.Incomplete
                : PssHandleDecodeStatus.Decoded;

        return new PssHandleDecodeResult(
            status,
            handle,
            flags,
            objectType,
            captureTime,
            reservedCreation,
            basic,
            rawTypeName,
            typeName,
            rawObjectName,
            objectName,
            union,
            rawUnion,
            invalid.AsReadOnly(),
            incomplete.AsReadOnly());
    }

    private static PssHandleDecodeResult EmptyInvalid(List<string> invalid) => new(
        PssHandleDecodeStatus.Invalid,
        0,
        0,
        0,
        0,
        0,
        null,
        new PssRawNameReference(0, 0),
        null,
        new PssRawNameReference(0, 0),
        null,
        null,
        Array.AsReadOnly(Array.Empty<byte>()),
        invalid.AsReadOnly(),
        Array.AsReadOnly(Array.Empty<string>()));

    private static PssCopiedName? DecodeName(
        string field,
        bool valid,
        ushort byteLength,
        ulong pointer,
        SyntheticPssNameBuffers? buffers,
        int maximumNameByteLength,
        List<string> invalid,
        List<string> incomplete)
    {
        if (!valid)
        {
            if (byteLength != 0 || pointer != 0)
            {
                incomplete.Add($"{field}-present-without-validity");
            }

            return null;
        }

        if (byteLength == 0)
        {
            if (pointer != 0)
            {
                incomplete.Add($"{field}-zero-length-nonzero-pointer-unsupported");
                return null;
            }

            return new PssCopiedName(0, string.Empty, Array.AsReadOnly(Array.Empty<byte>()));
        }

        if (pointer == 0)
        {
            invalid.Add($"{field}-null-pointer");
            return null;
        }

        if ((byteLength & 1) != 0)
        {
            invalid.Add($"{field}-odd-byte-length");
            return null;
        }

        if (byteLength > maximumNameByteLength)
        {
            invalid.Add($"{field}-byte-length-exceeds-bound");
            return null;
        }

        if (buffers is null || !buffers.TryCopy(pointer, byteLength, out var bytes))
        {
            incomplete.Add($"{field}-buffer-unavailable");
            return null;
        }

        try
        {
            return new PssCopiedName(byteLength, StrictUtf16.GetString(bytes), Array.AsReadOnly(bytes));
        }
        catch (DecoderFallbackException)
        {
            invalid.Add($"{field}-invalid-utf16");
            return null;
        }
    }

    private static PssTypeSpecificInformation DecodeUnion(SyntheticPssObjectType type, ReadOnlySpan<byte> value) => type switch
    {
        SyntheticPssObjectType.Process => new PssProcessInformation(
            U32(value, 0), U64(value, 8), U64(value, 16), I32(value, 24), U32(value, 28), U32(value, 32), U32(value, 36)),
        SyntheticPssObjectType.Thread => new PssThreadInformation(
            U32(value, 0), U64(value, 8), U32(value, 16), U32(value, 20), U64(value, 24), I32(value, 32), I32(value, 36), U64(value, 40)),
        SyntheticPssObjectType.Mutant => new PssMutantInformation(I32(value, 0), I32(value, 4) != 0, U32(value, 8), U32(value, 12)),
        SyntheticPssObjectType.Event => new PssEventInformation(I32(value, 0) != 0, I32(value, 4) != 0),
        SyntheticPssObjectType.Section => new PssSectionInformation(U64(value, 0), U32(value, 8), I64(value, 16)),
        SyntheticPssObjectType.Semaphore => new PssSemaphoreInformation(I32(value, 0), I32(value, 4)),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static ushort U16(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(value.Slice(offset, 2));

    private static uint U32(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(offset, 4));

    private static int I32(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadInt32LittleEndian(value.Slice(offset, 4));

    private static ulong U64(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(value.Slice(offset, 8));

    private static long I64(ReadOnlySpan<byte> value, int offset) => BinaryPrimitives.ReadInt64LittleEndian(value.Slice(offset, 8));
}
