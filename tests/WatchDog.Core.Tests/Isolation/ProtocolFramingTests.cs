using System.Buffers.Binary;
using System.Text;
using WatchDog.CaptureProtocol;

namespace WatchDog.Core.Tests.Isolation;

public sealed class ProtocolFramingTests
{
    private static readonly byte[] Secret = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();

    [Fact]
    public void RoundTripPreservesFullCorrelation()
    {
        var correlation = Correlation();
        using var writer = new AuthenticatedProtocolSession(
            Secret,
            ProtocolChannel.Control,
            ProtocolDirection.ParentToHelper,
            TestLimits());
        using var reader = new AuthenticatedProtocolSession(
            Secret,
            ProtocolChannel.Control,
            ProtocolDirection.ParentToHelper,
            TestLimits());

        var frame = writer.Encode(
            ProtocolMessageKind.Command,
            new HelperCommand(correlation, HelperCommandKind.Start, Digest('b')));
        var decoded = reader.Decode<HelperCommand>(
            frame,
            ProtocolMessageKind.Command,
            correlation);

        Assert.Equal(correlation, decoded.Correlation);
        Assert.Equal(HelperCommandKind.Start, decoded.Kind);
    }

    [Fact]
    public void BootstrapHandshakeBindsCorrelationPlanPeersArchitectureAndLimits()
    {
        var correlation = Correlation();
        var limits = BootstrapLimits();
        using var helperProtocol = new HelperBootstrapProtocol(Secret, TestLimits());
        using var parentProtocol = new ParentBootstrapProtocol(Secret, TestLimits());

        var helperHello = new HelperBootstrapHello(
            correlation,
            AuthenticatedProtocolSession.ProtocolVersion,
            SchemaVersion: 1,
            Architecture: 1,
            Digest('c'),
            Digest('e'),
            limits);
        var decodedHelper = parentProtocol.AcceptHelperHello(
            helperProtocol.CreateHelperHello(helperHello),
            correlation);
        var parentHello = new ParentBootstrapHello(
            correlation,
            AuthenticatedProtocolSession.ProtocolVersion,
            SchemaVersion: 1,
            Architecture: 1,
            Digest('c'),
            Digest('d'),
            Digest('e'),
            limits);
        var decodedParent = helperProtocol.AcceptParentHello(
            parentProtocol.CreateParentHello(parentHello),
            correlation);
        var ack = new HelperBootstrapAck(
            correlation,
            AuthenticatedProtocolSession.ProtocolVersion,
            SchemaVersion: 1,
            Architecture: 1,
            Digest('c'),
            Digest('d'),
            Digest('e'),
            limits);
        var decodedAck = parentProtocol.AcceptHelperAck(
            helperProtocol.CreateHelperAck(ack),
            correlation);

        Assert.Equal(helperHello, decodedHelper);
        Assert.Equal(parentHello, decodedParent);
        Assert.Equal(ack, decodedAck);
        Assert.Equal(correlation.PlanDigest, decodedAck.Correlation.PlanDigest);
        Assert.Equal(limits, decodedAck.EffectiveLimits);
        Assert.Throws<InvalidOperationException>(() =>
            helperProtocol.CreateHelperAck(ack));
    }

    [Fact]
    public void BootstrapReplayAndForeignCorrelationFailClosed()
    {
        var correlation = Correlation();
        using var writer = new AuthenticatedProtocolSession(
            Secret,
            ProtocolChannel.Control,
            ProtocolDirection.HelperToParent,
            TestLimits());
        using var reader = new AuthenticatedProtocolSession(
            Secret,
            ProtocolChannel.Control,
            ProtocolDirection.HelperToParent,
            TestLimits());
        var hello = new HelperBootstrapHello(
            correlation,
            AuthenticatedProtocolSession.ProtocolVersion,
            1,
            1,
            Digest('c'),
            Digest('e'),
            BootstrapLimits());
        var frame = writer.Encode(ProtocolMessageKind.HelperHello, hello);

        Assert.Equal(
            ProtocolErrorCode.CorrelationMismatch,
            Assert.Throws<CaptureProtocolException>(() =>
                reader.Decode<HelperBootstrapHello>(
                    frame,
                    ProtocolMessageKind.HelperHello,
                    correlation with { Generation = correlation.Generation + 1 }))
                .Diagnostic.Code);

        using var freshReader = new AuthenticatedProtocolSession(
            Secret,
            ProtocolChannel.Control,
            ProtocolDirection.HelperToParent,
            TestLimits());
        _ = freshReader.Decode<HelperBootstrapHello>(
            frame,
            ProtocolMessageKind.HelperHello,
            correlation);
        Assert.Equal(
            ProtocolErrorCode.SequenceMismatch,
            Assert.Throws<CaptureProtocolException>(() =>
                freshReader.Decode<HelperBootstrapHello>(
                    frame,
                    ProtocolMessageKind.HelperHello,
                    correlation))
                .Diagnostic.Code);
    }

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(1, 0, 1)]
    [InlineData(1, 1, 0)]
    public void BootstrapHelloRejectsProtocolSchemaAndArchitectureMismatches(
        int protocolVersion,
        int schemaVersion,
        int architecture)
    {
        var hello = new HelperBootstrapHello(
            Correlation(),
            protocolVersion,
            schemaVersion,
            architecture,
            Digest('c'),
            Digest('e'),
            BootstrapLimits());

        var error = Assert.Throws<CaptureProtocolException>(() =>
            BoundedJsonCodec.Serialize(hello, TestLimits()));

        Assert.Equal(ProtocolErrorCode.JsonContractInvalid, error.Diagnostic.Code);
    }

    [Fact]
    public void BootstrapHelloRejectsLimitsOutsideFrameProfile()
    {
        var hello = new HelperBootstrapHello(
            Correlation(),
            AuthenticatedProtocolSession.ProtocolVersion,
            1,
            1,
            Digest('c'),
            Digest('e'),
            new BootstrapLimitSnapshot(8192, 4096, 8, 32));

        var error = Assert.Throws<CaptureProtocolException>(() =>
            BoundedJsonCodec.Serialize(hello, TestLimits()));

        Assert.Equal(ProtocolErrorCode.JsonContractInvalid, error.Diagnostic.Code);
    }

    [Fact]
    public void BootstrapHelloRejectsWrongLaunchSecret()
    {
        var correlation = Correlation();
        var wrongSecret = Enumerable.Range(33, 32).Select(static value => (byte)value).ToArray();
        using var helper = new HelperBootstrapProtocol(Secret, TestLimits());
        using var parent = new ParentBootstrapProtocol(wrongSecret, TestLimits());
        var frame = helper.CreateHelperHello(new HelperBootstrapHello(
            correlation,
            AuthenticatedProtocolSession.ProtocolVersion,
            1,
            1,
            Digest('c'),
            Digest('e'),
            BootstrapLimits()));

        var error = Assert.Throws<CaptureProtocolException>(() =>
            parent.AcceptHelperHello(frame, correlation));

        Assert.Equal(ProtocolErrorCode.AuthenticationFailed, error.Diagnostic.Code);
    }

    [Theory]
    [InlineData(ProtocolChannel.Telemetry, ProtocolDirection.ParentToHelper)]
    [InlineData(ProtocolChannel.Control, ProtocolDirection.HelperToParent)]
    public void ChannelAndDirectionHaveIndependentAuthenticationKeys(
        ProtocolChannel channel,
        ProtocolDirection direction)
    {
        using var writer = new AuthenticatedProtocolSession(
            Secret,
            ProtocolChannel.Control,
            ProtocolDirection.ParentToHelper,
            TestLimits());
        using var reader = new AuthenticatedProtocolSession(Secret, channel, direction, TestLimits());
        var frame = writer.Encode(
            ProtocolMessageKind.Command,
            new HelperCommand(Correlation(), HelperCommandKind.Start, Digest('b')));

        var error = Assert.Throws<CaptureProtocolException>(
            () => reader.Decode<HelperCommand>(frame, ProtocolMessageKind.Command));

        Assert.Equal(ProtocolErrorCode.ChannelOrDirectionMismatch, error.Diagnostic.Code);
        Assert.DoesNotContain(Digest('b'), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TamperedTagIsRejectedWithoutAdvancingSequence()
    {
        using var writer = Session();
        using var reader = Session();
        var frame = writer.Encode(
            ProtocolMessageKind.Command,
            new HelperCommand(Correlation(), HelperCommandKind.Start, Digest('b')));
        frame[^1] ^= 0x40;

        var error = Assert.Throws<CaptureProtocolException>(
            () => reader.Decode<HelperCommand>(frame, ProtocolMessageKind.Command));
        Assert.Equal(ProtocolErrorCode.AuthenticationFailed, error.Diagnostic.Code);

        frame[^1] ^= 0x40;
        _ = reader.Decode<HelperCommand>(frame, ProtocolMessageKind.Command, Correlation());
    }

    [Fact]
    public void TruncatedAndOversizedFramesAreRejectedBeforePayloadParsing()
    {
        using var writer = Session();
        using var reader = Session();
        var frame = writer.Encode(
            ProtocolMessageKind.Command,
            new HelperCommand(Correlation(), HelperCommandKind.Start, Digest('b')));

        var truncated = Assert.Throws<CaptureProtocolException>(
            () => reader.Decode<HelperCommand>(frame.AsSpan(0, frame.Length - 1), ProtocolMessageKind.Command));
        Assert.Equal(ProtocolErrorCode.TruncatedFrame, truncated.Diagnostic.Code);

        var oversized = new byte[TestLimits().MaxFrameBytes + 1];
        var tooLarge = Assert.Throws<CaptureProtocolException>(
            () => reader.Decode<HelperCommand>(oversized, ProtocolMessageKind.Command));
        Assert.Equal(ProtocolErrorCode.FrameTooLarge, tooLarge.Diagnostic.Code);
    }

    [Fact]
    public void InvalidMagicVersionAndPayloadLengthAreRejected()
    {
        using var writer = Session();
        var correlation = Correlation();
        var frame = writer.Encode(
            ProtocolMessageKind.Command,
            new HelperCommand(correlation, HelperCommandKind.Start, Digest('b')));

        using var magicReader = Session();
        var invalidMagic = frame.ToArray();
        invalidMagic[0] ^= 0x20;
        Assert.Equal(
            ProtocolErrorCode.InvalidHeader,
            Assert.Throws<CaptureProtocolException>(
                () => magicReader.Decode<HelperCommand>(invalidMagic, ProtocolMessageKind.Command, correlation))
                .Diagnostic.Code);

        using var versionReader = Session();
        var invalidVersion = frame.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(invalidVersion.AsSpan(4, 2), 2);
        Assert.Equal(
            ProtocolErrorCode.UnsupportedVersion,
            Assert.Throws<CaptureProtocolException>(
                () => versionReader.Decode<HelperCommand>(invalidVersion, ProtocolMessageKind.Command, correlation))
                .Diagnostic.Code);

        using var payloadReader = Session();
        var invalidPayload = frame.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(
            invalidPayload.AsSpan(12, 4),
            TestLimits().MaxPayloadBytes + 1);
        Assert.Equal(
            ProtocolErrorCode.PayloadTooLarge,
            Assert.Throws<CaptureProtocolException>(
                () => payloadReader.Decode<HelperCommand>(invalidPayload, ProtocolMessageKind.Command, correlation))
                .Diagnostic.Code);
    }

    [Fact]
    public void DuplicateJsonFieldsAreRejectedBeforeDeserialization()
    {
        var json = Encoding.UTF8.GetBytes("{\"correlation\":{},\"correlation\":{}}");

        var error = Assert.Throws<CaptureProtocolException>(
            () => BoundedJsonCodec.Deserialize<HelperCommand>(json, TestLimits()));

        Assert.Equal(ProtocolErrorCode.DuplicateJsonField, error.Diagnostic.Code);
    }

    [Fact]
    public void MalformedJsonIsRejectedBeforeDeserialization()
    {
        var error = Assert.Throws<CaptureProtocolException>(
            () => BoundedJsonCodec.Deserialize<HelperCommand>("{"u8, TestLimits()));

        Assert.Equal(ProtocolErrorCode.MalformedJson, error.Diagnostic.Code);
    }

    [Fact]
    public void JsonDepthCollectionAndStringLimitsAreEnforced()
    {
        var depthLimits = new CaptureProtocolLimits(512, 448, 32, 2, 2, 16, 3);
        Assert.Equal(
            ProtocolErrorCode.JsonDepthExceeded,
            Assert.Throws<CaptureProtocolException>(
                () => BoundedJsonCodec.Validate(
                    Encoding.UTF8.GetBytes("{\"a\":{\"b\":{\"c\":{\"d\":1}}}}"),
                    depthLimits))
                .Diagnostic.Code);

        Assert.Equal(
            ProtocolErrorCode.JsonCollectionLimitExceeded,
            Assert.Throws<CaptureProtocolException>(
                () => BoundedJsonCodec.Validate(Encoding.UTF8.GetBytes("[1,2,3]"), depthLimits))
                .Diagnostic.Code);

        Assert.Equal(
            ProtocolErrorCode.JsonStringLimitExceeded,
            Assert.Throws<CaptureProtocolException>(
                () => BoundedJsonCodec.Validate(
                    Encoding.UTF8.GetBytes($"\"{new string('x', 33)}\""),
                    depthLimits))
                .Diagnostic.Code);
    }

    [Fact]
    public void JsonObjectNodeAndPayloadLimitsAreEnforced()
    {
        var objectLimits = new CaptureProtocolLimits(256, 200, 32, 8, 2, 32, 8);
        Assert.Equal(
            ProtocolErrorCode.JsonObjectLimitExceeded,
            Assert.Throws<CaptureProtocolException>(
                () => BoundedJsonCodec.Validate(Encoding.UTF8.GetBytes("{\"a\":1,\"b\":2,\"c\":3}"), objectLimits))
                .Diagnostic.Code);

        var nodeLimits = new CaptureProtocolLimits(256, 200, 32, 8, 8, 3, 8);
        Assert.Equal(
            ProtocolErrorCode.JsonNodeLimitExceeded,
            Assert.Throws<CaptureProtocolException>(
                () => BoundedJsonCodec.Validate(Encoding.UTF8.GetBytes("{\"a\":1,\"b\":2}"), nodeLimits))
                .Diagnostic.Code);

        var payloadLimits = new CaptureProtocolLimits(128, 16, 32, 8, 8, 32, 8);
        Assert.Equal(
            ProtocolErrorCode.PayloadTooLarge,
            Assert.Throws<CaptureProtocolException>(
                () => BoundedJsonCodec.Validate(Encoding.UTF8.GetBytes($"\"{new string('x', 20)}\""), payloadLimits))
                .Diagnostic.Code);
    }

    [Fact]
    public void SequenceAndExpectedIdentityMustMatchExactly()
    {
        using var writer = Session();
        using var reader = Session();
        var actual = Correlation();
        var frame = writer.Encode(
            ProtocolMessageKind.Command,
            new HelperCommand(actual, HelperCommandKind.Start, Digest('b')));
        var foreign = actual with { Generation = actual.Generation + 1 };

        var identityError = Assert.Throws<CaptureProtocolException>(
            () => reader.Decode<HelperCommand>(frame, ProtocolMessageKind.Command, foreign));
        Assert.Equal(ProtocolErrorCode.CorrelationMismatch, identityError.Diagnostic.Code);

        using var freshReader = Session();
        _ = freshReader.Decode<HelperCommand>(frame, ProtocolMessageKind.Command, actual);
        Assert.Equal(
            ProtocolErrorCode.SequenceMismatch,
            Assert.Throws<CaptureProtocolException>(
                () => freshReader.Decode<HelperCommand>(frame, ProtocolMessageKind.Command, actual))
                .Diagnostic.Code);
    }

    [Fact]
    public void CorrelatedMessagesRequireAnExplicitExpectedIdentity()
    {
        using var writer = Session();
        using var reader = Session();
        var correlation = Correlation();
        var frame = writer.Encode(
            ProtocolMessageKind.Command,
            new HelperCommand(correlation, HelperCommandKind.Start, Digest('b')));

        Assert.Equal(
            ProtocolErrorCode.CorrelationMismatch,
            Assert.Throws<CaptureProtocolException>(
                () => reader.Decode<HelperCommand>(frame, ProtocolMessageKind.Command))
                .Diagnostic.Code);
        _ = reader.Decode<HelperCommand>(frame, ProtocolMessageKind.Command, correlation);
    }

    private static AuthenticatedProtocolSession Session() => new(
        Secret,
        ProtocolChannel.Control,
        ProtocolDirection.ParentToHelper,
        TestLimits());

    internal static CaptureProtocolLimits TestLimits() =>
        new(4096, 4032, 256, 16, 16, 256, 8);

    internal static BootstrapLimitSnapshot BootstrapLimits() => new(4096, 2048, 8, 32);

    internal static CaptureCorrelation Correlation() => new(
        Guid.Parse("10000000-0000-0000-0000-000000000001"),
        Guid.Parse("20000000-0000-0000-0000-000000000002"),
        Guid.Parse("30000000-0000-0000-0000-000000000003"),
        Guid.Parse("40000000-0000-0000-0000-000000000004"),
        5,
        Guid.Parse("60000000-0000-0000-0000-000000000006"),
        7,
        Digest('a'));

    internal static string Digest(char value) => new(value, 64);
}
