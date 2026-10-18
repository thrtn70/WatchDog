using CoreBounds = WatchDog.Core.Capture.PhysicalCaptureBounds;

namespace WatchDog.Core.Metadata;

internal enum MetadataMappingFailureReason
{
    NullSource,
    MissingMappingEvidence,
    MissingRequest,
    MissingRequestTarget,
    InvalidSessionId,
    InvalidRequestId,
    InvalidGeneration,
    InvalidInitiation,
    MissingRequestedTarget,
    MissingActualTarget,
    MissingDeviceManifest,
    RequestTargetMismatch,
    UnsupportedTargetKind,
    UnsupportedGameTarget,
    MissingDisplayEvidence,
    DisplayEvidenceIdentityMismatch,
    UnexpectedDisplayEvidence,
    InvalidWindowIdentity,
    InvalidDisplayIdentity,
    InvalidPhysicalBounds,
    InvalidRegionGeometry,
    UnsupportedDeviceRole,
    UnsupportedDeviceSelectionMode,
    InvalidDeviceIdentity,
    WebcamAdmissionMismatch,
    UnsupportedAudioDirection,
    UnsupportedAudioDefaultRole,
    InvalidWebcamCatalog,
    DestinationContractRejected,
}

/// <summary>
/// A bounded mapping result. Success has one non-null value and no failure reason; failure has one
/// defined reason and no value. Its diagnostic form deliberately excludes mapped values.
/// </summary>
internal sealed class MetadataMappingResult<T>
    where T : class
{
    private MetadataMappingResult(T value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    private MetadataMappingResult(MetadataMappingFailureReason failureReason)
    {
        if (!Enum.IsDefined(failureReason))
            throw new ArgumentOutOfRangeException(nameof(failureReason));

        FailureReason = failureReason;
    }

    internal bool Succeeded => Value is not null;
    internal T? Value { get; }
    internal MetadataMappingFailureReason? FailureReason { get; }

    internal static MetadataMappingResult<T> Success(T value) => new(value);

    internal static MetadataMappingResult<T> Failure(MetadataMappingFailureReason reason) => new(reason);

    public override string ToString() => Succeeded
        ? "MetadataMappingResult { Status = Success, Value = [redacted] }"
        : $"MetadataMappingResult {{ Status = Failure, Reason = {FailureReason} }}";
}

/// <summary>Frozen, caller-supplied bounds correlated to one exact stable display identity.</summary>
internal sealed class FrozenDisplayMappingEvidence
{
    internal FrozenDisplayMappingEvidence(string stableDisplayId, CoreBounds bounds)
    {
        StableDisplayId = stableDisplayId;
        Bounds = bounds;
    }

    internal string StableDisplayId { get; }
    internal CoreBounds Bounds { get; }

    public override string ToString() => "FrozenDisplayMappingEvidence { Identity = [redacted] }";
}

/// <summary>Independent evidence for the requested and actual targets in one correlation snapshot.</summary>
internal sealed class CaptureMetadataMappingEvidence
{
    internal CaptureMetadataMappingEvidence(
        FrozenDisplayMappingEvidence? requestedDisplay,
        FrozenDisplayMappingEvidence? actualDisplay)
    {
        RequestedDisplay = requestedDisplay;
        ActualDisplay = actualDisplay;
    }

    internal static CaptureMetadataMappingEvidence Empty { get; } = new(null, null);

    internal FrozenDisplayMappingEvidence? RequestedDisplay { get; }
    internal FrozenDisplayMappingEvidence? ActualDisplay { get; }

    public override string ToString() =>
        "CaptureMetadataMappingEvidence { Requested = [redacted], Actual = [redacted] }";
}
