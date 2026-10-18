namespace WatchDog.CaptureIsolation;

/// <summary>
/// Opaque identity for an exact retained media-root handle chain. It is not a path, volume
/// identifier, file identifier, or proof that the retained root is still admissible.
/// </summary>
public sealed record RetainedMediaRootIdentity(Guid OpaqueHandleIdentity)
{
    internal void Validate()
    {
        if (OpaqueHandleIdentity == Guid.Empty)
            throw new ArgumentException("A retained media-root identity must be nonempty.");
    }
}

public enum LocalMediaRootAdmissionStatus
{
    Confirmed = 1,
    InvalidPath = 2,
    UnsupportedNamespace = 3,
    AlternateDataStream = 4,
    NetworkRoot = 5,
    NonFixedRoot = 6,
    VolumeRoot = 7,
    NotFound = 8,
    NotDirectory = 9,
    AccessDenied = 10,
    ReparsePoint = 11,
    IdentityUnavailable = 12,
    FinalPathMismatch = 13,
    Stale = 14,
    Unavailable = 15,
    ClosePending = 16,
}

/// <summary>
/// Privacy-safe neutral observation from a local media-root boundary. This is not trusted
/// admission evidence. It deliberately contains no path, drive name, volume serial number,
/// file identifier, reparse tag, or operating-system handle.
/// </summary>
public sealed class LocalMediaRootObservation
{
    public LocalMediaRootObservation(
        LocalMediaRootAdmissionStatus status,
        RetainedMediaRootIdentity? identity,
        string? canonicalPathDigest,
        string? failureCode)
    {
        if (status == LocalMediaRootAdmissionStatus.Confirmed)
        {
            ArgumentNullException.ThrowIfNull(identity);
            identity.Validate();
            if (!IsSha256Digest(canonicalPathDigest))
                throw new ArgumentException("A confirmed root requires a path digest.", nameof(canonicalPathDigest));
            if (failureCode is not null)
                throw new ArgumentException("A confirmed root cannot contain a failure code.", nameof(failureCode));
        }
        else
        {
            if (identity is not null || canonicalPathDigest is not null)
                throw new ArgumentException("A rejected root cannot expose retained identity data.");
            if (!IsFailureCode(failureCode))
                throw new ArgumentException("A rejected root requires a bounded failure code.", nameof(failureCode));
        }

        Status = status;
        Identity = identity;
        CanonicalPathDigest = canonicalPathDigest;
        FailureCode = failureCode;
    }

    public LocalMediaRootAdmissionStatus Status { get; }
    public RetainedMediaRootIdentity? Identity { get; }
    public string? CanonicalPathDigest { get; }
    public string? FailureCode { get; }

    public override string ToString() =>
        $"LocalMediaRootObservation {{ Status = {Status}, " +
        $"Identity = {(Identity is null ? "absent" : "present")}, " +
        $"CanonicalPathDigest = {(CanonicalPathDigest is null ? "absent" : "present")}, " +
        $"FailureCode = {FailureCode ?? "none"} }}";

    private static bool IsSha256Digest(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsFailureCode(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
}

/// <summary>
/// Unregistered observation seam for retaining and re-observing an exact local media root. The
/// implementation owns every retained operating-system handle until ReleaseAsync or disposal
/// completes. These observations cannot mint admission evidence for later path-based I/O.
/// </summary>
public interface ILocalMediaRootBoundary : IAsyncDisposable
{
    ValueTask<LocalMediaRootObservation> AdmitAsync(
        string candidatePath,
        CancellationToken cancellationToken);

    ValueTask<LocalMediaRootObservation> ObserveAsync(
        RetainedMediaRootIdentity identity,
        CancellationToken cancellationToken);

    ValueTask ReleaseAsync(
        RetainedMediaRootIdentity identity,
        CancellationToken cancellationToken);
}

public sealed record RetainedMediaFileIdentity(
    Guid OpaqueFileIdentity,
    RetainedMediaRootIdentity RootIdentity)
{
    internal void Validate()
    {
        RootIdentity.Validate();
        if (OpaqueFileIdentity == Guid.Empty)
            throw new ArgumentException("A retained media-file identity must be nonempty.");
    }

    public override string ToString() =>
        "RetainedMediaFileIdentity { OpaqueFileIdentity = redacted, RootIdentity = redacted }";
}

public enum HandleRelativeMediaFileStatus
{
    Created = 1,
    InvalidName,
    AlreadyExists,
    RootStale,
    ReparsePoint,
    AccessDenied,
    Cancelled,
    ClosePending,
    Unavailable,
}

/// <summary>
/// Privacy-safe result from creating one new file relative to an already-retained directory
/// handle. It contains no filename, path, volume/file identifier, or operating-system handle.
/// </summary>
public sealed class HandleRelativeMediaFileObservation
{
    public HandleRelativeMediaFileObservation(
        HandleRelativeMediaFileStatus status,
        RetainedMediaFileIdentity? identity,
        string? failureCode)
    {
        if (status == HandleRelativeMediaFileStatus.Created)
        {
            ArgumentNullException.ThrowIfNull(identity);
            identity.Validate();
            if (failureCode is not null)
                throw new ArgumentException("A created file cannot contain a failure code.");
        }
        else
        {
            if (identity is not null)
                throw new ArgumentException("A rejected file cannot expose retained identity data.");
            if (!IsFailureCode(failureCode))
                throw new ArgumentException("A rejected file requires a bounded failure code.");
        }

        Status = status;
        Identity = identity;
        FailureCode = failureCode;
    }

    public HandleRelativeMediaFileStatus Status { get; }
    public RetainedMediaFileIdentity? Identity { get; }
    public string? FailureCode { get; }

    public override string ToString() =>
        $"HandleRelativeMediaFileObservation {{ Status = {Status}, " +
        $"Identity = {(Identity is null ? "absent" : "present")}, " +
        $"FailureCode = {FailureCode ?? "none"} }}";

    private static bool IsFailureCode(string? value) =>
        value is { Length: > 0 and <= 128 } && value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
}

/// <summary>
/// Unregistered, synchronous-native handle-relative file capability. Calls are serialized by the
/// owner and use bounded buffers; their native duration is not production-qualified.
/// </summary>
public interface IHandleRelativeMediaRootBoundary : ILocalMediaRootBoundary
{
    const int MaximumTransferBytes = 1_048_576;

    ValueTask<HandleRelativeMediaFileObservation> CreateNewFileAsync(
        RetainedMediaRootIdentity rootIdentity,
        string singleComponentName,
        CancellationToken cancellationToken);

    ValueTask WriteFileAsync(
        RetainedMediaFileIdentity fileIdentity,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken);

    ValueTask<byte[]> ReadFileAsync(
        RetainedMediaFileIdentity fileIdentity,
        int maximumBytes,
        CancellationToken cancellationToken);

    ValueTask FlushFileAsync(
        RetainedMediaFileIdentity fileIdentity,
        CancellationToken cancellationToken);

    ValueTask ReleaseFileAsync(
        RetainedMediaFileIdentity fileIdentity,
        CancellationToken cancellationToken);
}

public sealed class LocalMediaRootBoundaryException : Exception
{
    public LocalMediaRootBoundaryException(string failureCode)
        : base("A local media-root boundary operation failed.")
    {
        if (string.IsNullOrWhiteSpace(failureCode) ||
            failureCode.Length > 128 ||
            !failureCode.All(static character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-'))
        {
            throw new ArgumentException("A bounded failure code is required.", nameof(failureCode));
        }

        FailureCode = failureCode;
    }

    public string FailureCode { get; }
}
