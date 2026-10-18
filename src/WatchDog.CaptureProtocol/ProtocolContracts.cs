using System.Text.Json.Serialization;

namespace WatchDog.CaptureProtocol;

/// <summary>
/// Explicit framing and JSON limits. The protocol intentionally has no production defaults;
/// callers must inject an approved profile. Tests may inject bounded fixture profiles.
/// </summary>
public sealed record CaptureProtocolLimits
{
    public CaptureProtocolLimits(
        int maxFrameBytes,
        int maxPayloadBytes,
        int maxStringUtf8Bytes,
        int maxCollectionItems,
        int maxObjectProperties,
        int maxTotalJsonNodes,
        int maxJsonDepth)
    {
        if (maxFrameBytes <= AuthenticatedProtocolSession.FrameOverheadBytes)
            throw new ArgumentOutOfRangeException(nameof(maxFrameBytes));
        if (maxPayloadBytes <= 0 ||
            maxPayloadBytes > maxFrameBytes - AuthenticatedProtocolSession.FrameOverheadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPayloadBytes));
        }
        if (maxStringUtf8Bytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStringUtf8Bytes));
        if (maxCollectionItems <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxCollectionItems));
        if (maxObjectProperties <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxObjectProperties));
        if (maxTotalJsonNodes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxTotalJsonNodes));
        if (maxJsonDepth <= 0 || maxJsonDepth > 64)
            throw new ArgumentOutOfRangeException(nameof(maxJsonDepth));

        MaxFrameBytes = maxFrameBytes;
        MaxPayloadBytes = maxPayloadBytes;
        MaxStringUtf8Bytes = maxStringUtf8Bytes;
        MaxCollectionItems = maxCollectionItems;
        MaxObjectProperties = maxObjectProperties;
        MaxTotalJsonNodes = maxTotalJsonNodes;
        MaxJsonDepth = maxJsonDepth;
    }

    public int MaxFrameBytes { get; }
    public int MaxPayloadBytes { get; }
    public int MaxStringUtf8Bytes { get; }
    public int MaxCollectionItems { get; }
    public int MaxObjectProperties { get; }
    public int MaxTotalJsonNodes { get; }
    public int MaxJsonDepth { get; }
}

public enum ProtocolChannel : byte
{
    Control = 1,
    Telemetry = 2,
}

public enum ProtocolDirection : byte
{
    ParentToHelper = 1,
    HelperToParent = 2,
}

public enum ProtocolMessageKind : ushort
{
    Hello = 1,
    Command = 2,
    CommandAccepted = 3,
    CommandCompleted = 4,
    Event = 5,
    Telemetry = 6,
    HelperHello = 7,
    ParentHello = 8,
    HelperAck = 9,
}

public enum HelperCommandKind
{
    Start,
    Stop,
    Fence,
    Split,
}

public enum HelperCommandResultKind
{
    Accepted,
    Completed,
    Failed,
    Rejected,
}

public enum ProtocolErrorCode
{
    FrameTooLarge,
    TruncatedFrame,
    TrailingFrameData,
    InvalidHeader,
    UnsupportedVersion,
    ChannelOrDirectionMismatch,
    UnexpectedMessageKind,
    PayloadTooLarge,
    AuthenticationFailed,
    SequenceMismatch,
    CorrelationMismatch,
    MalformedJson,
    DuplicateJsonField,
    JsonDepthExceeded,
    JsonCollectionLimitExceeded,
    JsonObjectLimitExceeded,
    JsonNodeLimitExceeded,
    JsonStringLimitExceeded,
    JsonContractInvalid,
}

/// <summary>
/// Privacy-safe diagnostic metadata. It deliberately contains no secret, target, path, payload,
/// device identity, exception text, or user-provided value.
/// </summary>
public sealed record ProtocolDiagnostic(
    ProtocolErrorCode Code,
    ProtocolChannel? Channel = null,
    ProtocolDirection? Direction = null,
    ProtocolMessageKind? MessageKind = null,
    long? Sequence = null);

public sealed class CaptureProtocolException : Exception
{
    internal CaptureProtocolException(ProtocolDiagnostic diagnostic)
        : base($"Capture protocol rejected input ({diagnostic.Code}).")
    {
        Diagnostic = diagnostic;
    }

    public ProtocolDiagnostic Diagnostic { get; }
}

public interface IProtocolValidatable
{
    void Validate(CaptureProtocolLimits limits);
}

public interface ICorrelatedProtocolMessage : IProtocolValidatable
{
    CaptureCorrelation Correlation { get; }
}

public sealed record CaptureCorrelation(
    Guid ParentInstanceId,
    Guid HelperInstanceId,
    Guid SessionId,
    Guid RequestId,
    long Generation,
    Guid CommandId,
    long CommandSequence,
    string PlanDigest) : IProtocolValidatable
{
    public void Validate(CaptureProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (ParentInstanceId == Guid.Empty ||
            HelperInstanceId == Guid.Empty ||
            SessionId == Guid.Empty ||
            RequestId == Guid.Empty ||
            CommandId == Guid.Empty ||
            Generation <= 0 ||
            CommandSequence <= 0 ||
            !ProtocolValueValidation.IsSha256Digest(PlanDigest))
        {
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
        }
    }

    public void EnsureExact(CaptureCorrelation expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (this != expected)
            throw ProtocolErrors.Create(ProtocolErrorCode.CorrelationMismatch);
    }
}

public sealed record OutputCorrelation(
    Guid OutputEpoch,
    Guid MediaId,
    string PipelineOwner,
    string StreamKind,
    string MeasurementKind,
    long EventSequence) : IProtocolValidatable
{
    public void Validate(CaptureProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (OutputEpoch == Guid.Empty || MediaId == Guid.Empty || EventSequence <= 0 ||
            !ProtocolValueValidation.IsIdentifier(PipelineOwner) ||
            !ProtocolValueValidation.IsIdentifier(StreamKind) ||
            !ProtocolValueValidation.IsIdentifier(MeasurementKind))
        {
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
        }
    }
}

public sealed record BootstrapLimitSnapshot(
    int MaxControlFrameBytes,
    int MaxPayloadBytes,
    int MaxPendingCommands,
    int MaxTelemetrySamples) : IProtocolValidatable
{
    public void Validate(CaptureProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (MaxControlFrameBytes <= AuthenticatedProtocolSession.FrameOverheadBytes ||
            MaxControlFrameBytes > limits.MaxFrameBytes ||
            MaxPayloadBytes <= 0 ||
            MaxPayloadBytes > limits.MaxPayloadBytes ||
            MaxPayloadBytes > MaxControlFrameBytes - AuthenticatedProtocolSession.FrameOverheadBytes ||
            MaxPendingCommands <= 0 || MaxPendingCommands > MaxPayloadBytes ||
            MaxTelemetrySamples <= 0 || MaxTelemetrySamples > MaxPayloadBytes)
        {
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
        }
    }
}

/// <summary>
/// Authenticated launch descriptor carried only in the inherited page-file section. Its
/// string representation is deliberately redacted because the endpoint is launch-sensitive.
/// </summary>
public sealed class BootstrapLaunchDescriptor : IProtocolValidatable
{
    public BootstrapLaunchDescriptor(
        CaptureCorrelation correlation,
        int protocolVersion,
        int schemaVersion,
        int architecture,
        string packageManifestDigest,
        string parentPayloadDigest,
        string helperPayloadDigest,
        BootstrapLimitSnapshot effectiveLimits,
        string endpoint,
        uint parentProcessId,
        long parentCreationFileTime)
    {
        Correlation = correlation;
        ProtocolVersion = protocolVersion;
        SchemaVersion = schemaVersion;
        Architecture = architecture;
        PackageManifestDigest = packageManifestDigest;
        ParentPayloadDigest = parentPayloadDigest;
        HelperPayloadDigest = helperPayloadDigest;
        EffectiveLimits = effectiveLimits;
        Endpoint = endpoint;
        ParentProcessId = parentProcessId;
        ParentCreationFileTime = parentCreationFileTime;
    }

    public CaptureCorrelation Correlation { get; }
    public int ProtocolVersion { get; }
    public int SchemaVersion { get; }
    public int Architecture { get; }
    public string PackageManifestDigest { get; }
    public string ParentPayloadDigest { get; }
    public string HelperPayloadDigest { get; }
    public BootstrapLimitSnapshot EffectiveLimits { get; }
    public string Endpoint { get; }
    public uint ParentProcessId { get; }
    public long ParentCreationFileTime { get; }

    public void Validate(CaptureProtocolLimits limits)
    {
        BootstrapContractValidation.ValidateCommon(
            Correlation,
            ProtocolVersion,
            SchemaVersion,
            Architecture,
            PackageManifestDigest,
            EffectiveLimits,
            limits);
        BootstrapContractValidation.ValidatePayloadDigests(
            ParentPayloadDigest,
            HelperPayloadDigest);
        if (!ProtocolValueValidation.IsIdentifier(Endpoint) ||
            ParentProcessId == 0 || ParentCreationFileTime <= 0)
        {
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
        }
    }

    public override string ToString() => "BootstrapLaunchDescriptor { redacted }";
}

public sealed record HelperBootstrapHello(
    CaptureCorrelation Correlation,
    int ProtocolVersion,
    int SchemaVersion,
    int Architecture,
    string PackageManifestDigest,
    string HelperPayloadDigest,
    BootstrapLimitSnapshot EffectiveLimits) : ICorrelatedProtocolMessage
{
    public void Validate(CaptureProtocolLimits limits)
    {
        BootstrapContractValidation.ValidateCommon(
            Correlation,
            ProtocolVersion,
            SchemaVersion,
            Architecture,
            PackageManifestDigest,
            EffectiveLimits,
            limits);
        if (!ProtocolValueValidation.IsSha256Digest(HelperPayloadDigest))
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
    }
}

public sealed record ParentBootstrapHello(
    CaptureCorrelation Correlation,
    int ProtocolVersion,
    int SchemaVersion,
    int Architecture,
    string PackageManifestDigest,
    string ParentPayloadDigest,
    string HelperPayloadDigest,
    BootstrapLimitSnapshot EffectiveLimits) : ICorrelatedProtocolMessage
{
    public void Validate(CaptureProtocolLimits limits)
    {
        BootstrapContractValidation.ValidateCommon(
            Correlation,
            ProtocolVersion,
            SchemaVersion,
            Architecture,
            PackageManifestDigest,
            EffectiveLimits,
            limits);
        BootstrapContractValidation.ValidatePayloadDigests(
            ParentPayloadDigest,
            HelperPayloadDigest);
    }
}

public sealed record HelperBootstrapAck(
    CaptureCorrelation Correlation,
    int ProtocolVersion,
    int SchemaVersion,
    int Architecture,
    string PackageManifestDigest,
    string ParentPayloadDigest,
    string HelperPayloadDigest,
    BootstrapLimitSnapshot EffectiveLimits) : ICorrelatedProtocolMessage
{
    public void Validate(CaptureProtocolLimits limits)
    {
        BootstrapContractValidation.ValidateCommon(
            Correlation,
            ProtocolVersion,
            SchemaVersion,
            Architecture,
            PackageManifestDigest,
            EffectiveLimits,
            limits);
        BootstrapContractValidation.ValidatePayloadDigests(
            ParentPayloadDigest,
            HelperPayloadDigest);
    }
}

public sealed record HelperCommand(
    CaptureCorrelation Correlation,
    HelperCommandKind Kind,
    string PayloadDigest) : ICorrelatedProtocolMessage
{
    public void Validate(CaptureProtocolLimits limits)
    {
        if (Correlation is null)
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
        Correlation.Validate(limits);
        if (!Enum.IsDefined(Kind) || !ProtocolValueValidation.IsSha256Digest(PayloadDigest))
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
    }
}

public sealed record HelperCommandResult(
    CaptureCorrelation Correlation,
    HelperCommandResultKind Result,
    string ResultCode,
    OutputCorrelation? Output = null) : ICorrelatedProtocolMessage
{
    public void Validate(CaptureProtocolLimits limits)
    {
        if (Correlation is null)
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
        Correlation.Validate(limits);
        Output?.Validate(limits);
        if (!Enum.IsDefined(Result) || !ProtocolValueValidation.IsIdentifier(ResultCode))
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
    }
}

internal static class ProtocolErrors
{
    public static CaptureProtocolException Create(
        ProtocolErrorCode code,
        ProtocolChannel? channel = null,
        ProtocolDirection? direction = null,
        ProtocolMessageKind? kind = null,
        long? sequence = null) =>
        new(new ProtocolDiagnostic(code, channel, direction, kind, sequence));
}

internal static class BootstrapContractValidation
{
    internal static void ValidateCommon(
        CaptureCorrelation? correlation,
        int protocolVersion,
        int schemaVersion,
        int architecture,
        string? packageManifestDigest,
        BootstrapLimitSnapshot? effectiveLimits,
        CaptureProtocolLimits limits)
    {
        if (correlation is null || effectiveLimits is null ||
            protocolVersion != AuthenticatedProtocolSession.ProtocolVersion ||
            schemaVersion <= 0 || architecture <= 0 ||
            !ProtocolValueValidation.IsSha256Digest(packageManifestDigest))
        {
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
        }
        correlation.Validate(limits);
        effectiveLimits.Validate(limits);
    }

    internal static void ValidatePayloadDigests(
        string? parentPayloadDigest,
        string? helperPayloadDigest)
    {
        if (!ProtocolValueValidation.IsSha256Digest(parentPayloadDigest) ||
            !ProtocolValueValidation.IsSha256Digest(helperPayloadDigest))
        {
            throw ProtocolErrors.Create(ProtocolErrorCode.JsonContractInvalid);
        }
    }
}

internal static class ProtocolValueValidation
{
    public static bool IsSha256Digest(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    public static bool IsIdentifier(string? value) =>
        value is { Length: >= 1 and <= 64 } && value.All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');
}
