using System.Buffers.Binary;
using WatchDog.Integration.Tests.Fixtures;

namespace WatchDog.Integration.Tests;

public sealed class OwnerSidOnlyAllowDaclV1Tests
{
    [Fact]
    public void ExactOrdinaryOwnerAllowProjectionMatchesOnlyTheNamedProfile()
    {
        var sample = Capture(Projection());
        var result = sample.Evaluate();

        Assert.Equal(DescriptorProfileStatus.MatchesProfile, result.Status);
        Assert.Equal(DescriptorProfileReason.None, result.Reason);
        Assert.Equal(1u, result.SchemaVersion);
        Assert.Matches("^[0-9A-F]{64}$", result.ProjectionDigest!);
        Assert.Equal(result, sample.Evaluate());
        Assert.Equal(result, Capture(Projection()).Evaluate());
    }

    [Fact]
    public void InputArraysAndAceListAreFrozenBeforeEvaluation()
    {
        var intended = Sid();
        var owner = Sid();
        var trustee = Sid();
        var aces = new[] { Ace(trustee: trustee) };
        var projection = Projection(owner: owner, aces: aces);
        var sample = OwnerSidOnlyAllowDaclV1.Capture(intended, projection);
        var before = sample.Evaluate();

        intended[8] = 255;
        owner[0] = 9;
        trustee[0] = 9;
        aces[0] = Ace(mask: 1);

        Assert.Equal(DescriptorProfileStatus.MatchesProfile, before.Status);
        Assert.Equal(before, sample.Evaluate());
        Assert.NotEqual(before, OwnerSidOnlyAllowDaclV1.Capture(intended, projection).Evaluate());
    }

    [Fact]
    public void ExtraPayloadIsPreservedAndFrozenEvenForUnsupportedProjections()
    {
        var extra = new byte[] { 1, 2, 3, 4 };
        var projection = Projection(aces: [Ace(extra: extra)]);
        var sample = Capture(projection);
        var before = sample.Evaluate();
        extra[0] = 99;

        Assert.Equal(DescriptorProfileStatus.UnsupportedRepresentation, before.Status);
        Assert.Equal(DescriptorProfileReason.ExtraAcePayload, before.Reason);
        Assert.Equal(before, sample.Evaluate());
        Assert.NotEqual(before.ProjectionDigest, Capture(projection).Evaluate().ProjectionDigest);
    }

    [Fact]
    public void MissingInputsDoNotBecomeOwnerEvidence()
    {
        AssertResult(OwnerSidOnlyAllowDaclV1.Capture(null, Projection()).Evaluate(),
            DescriptorProfileStatus.InvalidData, DescriptorProfileReason.MissingInput, digest: false);
        AssertResult(OwnerSidOnlyAllowDaclV1.Capture(Sid(), null).Evaluate(),
            DescriptorProfileStatus.InvalidData, DescriptorProfileReason.MissingInput, digest: false);
        AssertResult(Capture(Projection() with { OwnerSid = null }).Evaluate(),
            DescriptorProfileStatus.InvalidData, DescriptorProfileReason.MissingInput, digest: false);
        AssertResult(Capture(Projection() with { Aces = null }).Evaluate(),
            DescriptorProfileStatus.InvalidData, DescriptorProfileReason.MissingInput, digest: false);
        AssertResult(Capture(Projection() with { Aces = [null] }).Evaluate(),
            DescriptorProfileStatus.InvalidData, DescriptorProfileReason.MissingInput, digest: false);
        AssertResult(Capture(Projection(aces: [Ace() with { TrusteeSid = null }])).Evaluate(),
            DescriptorProfileStatus.InvalidData, DescriptorProfileReason.MissingInput, digest: false);
        AssertResult(Capture(Projection(aces: [Ace() with { ExtraPayload = null }])).Evaluate(),
            DescriptorProfileStatus.InvalidData, DescriptorProfileReason.MissingInput, digest: false);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(27)]
    [InlineData(29)]
    [InlineData(69)]
    public void MalformedSidLengthsAreRejectedInEveryIdentityPosition(int length)
    {
        var bad = new byte[length];
        Sid().AsSpan(0, Math.Min(length, Sid().Length)).CopyTo(bad);
        AssertInvalidSid(OwnerSidOnlyAllowDaclV1.Capture(bad, Projection()).Evaluate());
        AssertInvalidSid(Capture(Projection(owner: bad)).Evaluate());
        AssertInvalidSid(Capture(Projection(aces: [Ace(trustee: bad)])).Evaluate());
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 2)]
    [InlineData(1, 16)]
    [InlineData(1, 255)]
    [InlineData(1, 4)]
    public void SidRevisionAndSubauthorityCountMustDescribeExactlyTheBytes(int index, byte value)
    {
        var bad = Sid();
        bad[index] = value;
        AssertInvalidSid(OwnerSidOnlyAllowDaclV1.Capture(bad, Projection()).Evaluate());
        AssertInvalidSid(Capture(Projection(owner: bad)).Evaluate());
        AssertInvalidSid(Capture(Projection(aces: [Ace(trustee: bad)])).Evaluate());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(3u)]
    [InlineData(4u)]
    [InlineData(999u)]
    public void CreatorAuthorityIsRejectedWhenIntentOwnerAndTrusteeAllAgree(uint rid)
    {
        var special = Sid(3, rid);
        AssertResult(OwnerSidOnlyAllowDaclV1.Capture(special,
            Projection(owner: special, aces: [Ace(trustee: special)])).Evaluate(),
            DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.SubstitutionSid);
    }

    [Fact]
    public void PrincipalSelfIsRejectedWhenAllThreeIdentitiesAgree()
    {
        var special = Sid(5, 10);
        AssertResult(OwnerSidOnlyAllowDaclV1.Capture(special,
            Projection(owner: special, aces: [Ace(trustee: special)])).Evaluate(),
            DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.SubstitutionSid);
    }

    [Fact]
    public void SpecialSidsAreNotDetectedUsingOnlyLastByteOrRid()
    {
        foreach (var ordinary in new[] { Sid(259, 4), Sid(5, 21, 10), Sid(5, 1000) })
            Assert.Equal(DescriptorProfileStatus.MatchesProfile,
                OwnerSidOnlyAllowDaclV1.Capture(ordinary,
                    Projection(owner: ordinary, aces: [Ace(trustee: ordinary)])).Evaluate().Status);
        // These synthetic SIDs demonstrate binary shape only, not actual user provenance.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OwnerAndTrusteeMustEqualTheIndependentIntendedSid(bool changeOwner)
    {
        var other = Sid(5, 21, 1, 2, 3, 1002);
        var projection = changeOwner ? Projection(owner: other) : Projection(aces: [Ace(trustee: other)]);
        AssertResult(Capture(projection).Evaluate(), DescriptorProfileStatus.DoesNotMatchProfile,
            changeOwner ? DescriptorProfileReason.OwnerMismatch : DescriptorProfileReason.TrusteeMismatch);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(4u)]
    [InlineData(7u)]
    public void AbsentNullAndUnprotectedDaclsNeverMatch(uint flags)
    {
        AssertResult(Capture(Projection() with { Flags = (OwnerDaclProjectionFlags)flags }).Evaluate(),
            DescriptorProfileStatus.DoesNotMatchProfile, DescriptorProfileReason.DaclShape);
    }

    [Theory]
    [InlineData(8u)]
    [InlineData(16u)]
    [InlineData(32u)]
    [InlineData(64u)]
    [InlineData(128u)]
    [InlineData(0x80000000u)]
    public void DefaultedInheritedAndUnknownControlFlagsAreUnsupported(uint flags)
    {
        AssertResult(Capture(Projection() with { Flags = (OwnerDaclProjectionFlags)(5u | flags) }).Evaluate(),
            DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.ControlFlags);
    }

    [Fact]
    public void UnknownNativeControlBitsAreNotDiscarded()
    {
        AssertResult(Capture(Projection() with { UnknownOwnerDaclControlBits = 1 }).Evaluate(),
            DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.ControlFlags);
    }

    [Fact]
    public void UnsupportedVersionsStayUnsupported()
    {
        foreach (var value in new uint[] { 0, 2, uint.MaxValue })
            AssertResult(Capture(Projection() with { SchemaVersion = value }).Evaluate(),
                DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.SchemaVersion);
        foreach (var value in new byte[] { 0, 2, 255 })
            AssertResult(Capture(Projection() with { DescriptorRevision = value }).Evaluate(),
                DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.DescriptorRevision);
        foreach (var value in new byte[] { 0, 1, 4, 255 })
            AssertResult(Capture(Projection() with { AclRevision = value }).Evaluate(),
                DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.AclRevision);
    }

    [Theory]
    [InlineData(1)] // deny
    [InlineData(2)] // audit
    [InlineData(4)] // compound
    [InlineData(5)] // object
    [InlineData(9)] // callback/conditional
    [InlineData(11)] // callback object
    [InlineData(18)] // resource attribute
    [InlineData(255)]
    public void NonordinaryAceTypesCannotBeNormalizedToAllow(byte type)
    {
        AssertResult(Capture(Projection(aces: [Ace() with { Type = type }])).Evaluate(),
            DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.AceType);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(128)]
    public void EveryAceFlagBitIsRejected(byte flags)
    {
        AssertResult(Capture(Projection(aces: [Ace() with { Flags = flags }])).Evaluate(),
            DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.AceFlags);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0x0012019Fu)]
    [InlineData(0x001F01FEu)]
    public void PartialMasksDoNotImplyOwnerFullAccess(uint mask)
    {
        AssertResult(Capture(Projection(aces: [Ace(mask: mask)])).Evaluate(),
            DescriptorProfileStatus.DoesNotMatchProfile, DescriptorProfileReason.AccessMask);
    }

    [Theory]
    [InlineData(0x10000000u)] // generic all
    [InlineData(0x80000000u)] // generic read
    [InlineData(0x02000000u)] // maximum allowed
    [InlineData(0x01000000u)] // system security
    [InlineData(0x00000200u)] // unknown file-specific bit
    public void UnsupportedMaskBitsCannotBeMappedIntoFullAccess(uint bit)
    {
        AssertResult(Capture(Projection(aces: [Ace(mask: 0x001F01FFu | bit)])).Evaluate(),
            DescriptorProfileStatus.UnsupportedRepresentation, DescriptorProfileReason.AccessMask);
    }

    [Fact]
    public void EmptyDuplicateAndAdditionalGrantListsCannotMatch()
    {
        foreach (var aces in new[] { Array.Empty<OwnerDaclAceInput>(), new[] { Ace(), Ace() },
            new[] { Ace(), Ace(trustee: Sid(5, 32, 545), mask: 1) } })
            AssertResult(Capture(Projection(aces: aces)).Evaluate(),
                DescriptorProfileStatus.DoesNotMatchProfile, DescriptorProfileReason.AceCount);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]
    [InlineData(39u)]
    [InlineData(43u)]
    [InlineData(48u)]
    [InlineData(uint.MaxValue)]
    public void DeclaredAclSizeMustMatchExactlyAndBeAligned(uint size)
    {
        AssertResult(Capture(Projection() with { DeclaredAclSize = size }).Evaluate(),
            DescriptorProfileStatus.InvalidData, DescriptorProfileReason.AclSize);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]
    [InlineData(35u)]
    [InlineData(40u)]
    [InlineData(uint.MaxValue)]
    public void DeclaredAceSizeMustMatchExactlyAndBeAligned(uint size)
    {
        AssertResult(Capture(Projection() with { Aces = [Ace() with { DeclaredSize = size }] }).Evaluate(),
            DescriptorProfileStatus.InvalidData, DescriptorProfileReason.AceSize);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void DeclaredAceCountMustMatchTheEntireCopiedList(uint count)
    {
        AssertResult(Capture(Projection() with { DeclaredAceCount = count }).Evaluate(),
            DescriptorProfileStatus.InvalidData, DescriptorProfileReason.AceCount);
    }

    [Fact]
    public void CaptureRejectsHugeCountsAndBuffersWithoutAcceptingAPrefix()
    {
        AssertTooLarge(Capture(Projection() with { OwnerSid = new byte[65537] }).Evaluate());
        AssertTooLarge(Capture(Projection() with { Aces = new OwnerDaclAceInput?[65536] }).Evaluate());
        AssertTooLarge(Capture(Projection(aces: [Ace(extra: new byte[65536])])).Evaluate());
        AssertTooLarge(Capture(Projection(aces: [Ace(trustee: new byte[65536])])).Evaluate());
    }

    [Fact]
    public void EntireProjectionAndIntentShareOneCopyBudget()
    {
        // Independent encoding size: 33-byte domain, 30-byte header, 28-byte owner,
        // 18-byte ACE header, 28-byte trustee; plus 28 bytes for the separate intended SID.
        const int extraAtLimit = 65536 - (33 + 30 + 28 + 18 + 28 + 28);
        var atLimit = Capture(Projection(aces: [Ace(extra: new byte[extraAtLimit])])).Evaluate();
        Assert.NotNull(atLimit.ProjectionDigest);
        Assert.NotEqual(DescriptorProfileStatus.MatchesProfile, atLimit.Status);
        AssertTooLarge(Capture(Projection(aces: [Ace(extra: new byte[extraAtLimit + 1])])).Evaluate());
    }

    [Fact]
    public void DigestCoversProjectionFactsButDoesNotPretendToAuthenticateIntent()
    {
        var good = Projection();
        var digest = Capture(good).Evaluate().ProjectionDigest;
        var changed = new[]
        {
            good with { SchemaVersion = 2 }, good with { DescriptorRevision = 2 },
            good with { Flags = 0 }, good with { UnknownOwnerDaclControlBits = 1 },
            good with { AclRevision = 4 }, good with { DeclaredAclSize = 0 },
            good with { DeclaredAceCount = 0 }, good with { OwnerSid = Sid(5, 999) },
            good with { Aces = [Ace() with { Type = 1 }] },
            good with { Aces = [Ace() with { Flags = 1 }] },
            good with { Aces = [Ace() with { DeclaredSize = 0 }] },
            good with { Aces = [Ace(mask: 1)] },
            good with { Aces = [Ace(trustee: Sid(5, 999))] },
            Projection(aces: [Ace(extra: [1, 2, 3, 4])]),
            Projection(aces: []),
        };
        foreach (var projection in changed)
            Assert.NotEqual(digest, Capture(projection).Evaluate().ProjectionDigest);
        var otherIntent = OwnerSidOnlyAllowDaclV1.Capture(Sid(5, 999), good).Evaluate();
        Assert.Equal(digest, otherIntent.ProjectionDigest);
        Assert.Equal(DescriptorProfileStatus.DoesNotMatchProfile, otherIntent.Status);
    }

    [Fact]
    public void ClassifierAndResultAreFixturePrivateAndExposeNoAdmissionSurface()
    {
        Assert.Same(typeof(OwnerSidOnlyAllowDaclV1Tests).Assembly, typeof(OwnerSidOnlyAllowDaclV1).Assembly);
        Assert.False(typeof(OwnerSidOnlyAllowDaclV1).IsPublic);
        Assert.False(typeof(DescriptorProfileResult).IsPublic);
        Assert.Equal(new[] { "ProjectionDigest", "Reason", "SchemaVersion", "Status" },
            typeof(DescriptorProfileResult).GetProperties().Select(p => p.Name).Order().ToArray());
        Assert.DoesNotContain("Complete", Enum.GetNames<DescriptorProfileStatus>());
    }

    private static OwnerSidOnlyAllowDaclV1 Capture(OwnerDaclProjectionInput projection) =>
        OwnerSidOnlyAllowDaclV1.Capture(Sid(), projection);

    private static OwnerDaclProjectionInput Projection(byte[]? owner = null, OwnerDaclAceInput?[]? aces = null)
    {
        aces ??= [Ace()];
        return new(1, 1, owner ?? Sid(), OwnerDaclProjectionFlags.Present | OwnerDaclProjectionFlags.Protected,
            0, 2, (uint)(8L + aces.Sum(a => (long)a!.DeclaredSize)), (uint)aces.Length, aces);
    }

    private static OwnerDaclAceInput Ace(byte[]? trustee = null, uint mask = 0x001F01FF, byte[]? extra = null)
    {
        trustee ??= Sid();
        extra ??= [];
        return new(0, 0, (uint)(8 + trustee.Length + extra.Length), mask, trustee, extra);
    }

    private static byte[] Sid() => Sid(5, 21, 1, 2, 3, 1001);

    private static byte[] Sid(ulong authority, params uint[] subauthorities)
    {
        var bytes = new byte[8 + 4 * subauthorities.Length];
        bytes[0] = 1;
        bytes[1] = (byte)subauthorities.Length;
        for (var index = 0; index < 6; index++)
            bytes[7 - index] = (byte)(authority >> (8 * index));
        for (var index = 0; index < subauthorities.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + 4 * index), subauthorities[index]);
        return bytes;
    }

    private static void AssertInvalidSid(DescriptorProfileResult result) =>
        AssertResult(result, DescriptorProfileStatus.InvalidData, DescriptorProfileReason.InvalidSid,
            digest: result.ProjectionDigest is not null);

    private static void AssertTooLarge(DescriptorProfileResult result) =>
        AssertResult(result, DescriptorProfileStatus.InvalidData, DescriptorProfileReason.InputTooLarge, digest: false);

    private static void AssertResult(DescriptorProfileResult result, DescriptorProfileStatus status,
        DescriptorProfileReason reason, bool digest = true)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(reason, result.Reason);
        if (digest) Assert.Matches("^[0-9A-F]{64}$", result.ProjectionDigest!);
        else Assert.Null(result.ProjectionDigest);
    }
}
