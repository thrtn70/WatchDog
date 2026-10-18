using System.Collections.ObjectModel;

namespace WatchDog.CaptureIsolation;

public enum PackageAuthenticityFailure
{
    None = 0,
    PolicyInvalid,
    ManifestTooLarge,
    ManifestMalformed,
    ManifestInvalid,
    SignatureInvalid,
    PackageMismatch,
    PayloadMismatch,
    PublisherUnverified,
}

public enum PackagePublisherTrustStatus
{
    Trusted = 1,
    Unsigned,
    InvalidSignature,
    ChainUntrusted,
    Revoked,
    RevocationUnknown,
    Expired,
    WrongPublisher,
    Unavailable,
}

public sealed record PackageFileObservation(
    CaptureIsolationPayloadRole Role,
    string RelativePath,
    long Length,
    string Sha256Digest,
    PackagePublisherTrustStatus PublisherTrust,
    string PublisherCertificateSha256);

public sealed class PackageAuthenticityTrustPolicy
{
    private readonly byte[] _manifestPublicKey;
    private readonly ReadOnlyCollection<string> _publisherPins;

    public PackageAuthenticityTrustPolicy(
        string product,
        string channel,
        string packageVersion,
        byte[] manifestPublicKey,
        IEnumerable<string> publisherCertificateSha256Pins,
        int maximumManifestBytes,
        int maximumSignatureBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(product);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(packageVersion);
        ArgumentNullException.ThrowIfNull(manifestPublicKey);
        ArgumentNullException.ThrowIfNull(publisherCertificateSha256Pins);
        Product = product;
        Channel = channel;
        PackageVersion = packageVersion;
        _manifestPublicKey = manifestPublicKey.ToArray();
        _publisherPins = Array.AsReadOnly(publisherCertificateSha256Pins.ToArray());
        MaximumManifestBytes = maximumManifestBytes;
        MaximumSignatureBytes = maximumSignatureBytes;
    }

    public string Product { get; }
    public string Channel { get; }
    public string PackageVersion { get; }
    public ReadOnlyMemory<byte> ManifestPublicKey => _manifestPublicKey;
    public IReadOnlyList<string> PublisherCertificateSha256Pins => _publisherPins;
    public int MaximumManifestBytes { get; }
    public int MaximumSignatureBytes { get; }
}

public sealed class PackageAuthenticityDecision
{
    internal PackageAuthenticityDecision(
        CaptureIsolationAdmissionOutcome outcome,
        PackageAuthenticityFailure failure,
        string? failureCode,
        string? manifestDigest,
        CaptureIsolationPackageEvidence? evidence)
    {
        Outcome = outcome;
        Failure = failure;
        FailureCode = failureCode;
        ManifestDigest = manifestDigest;
        Evidence = evidence;
    }

    public CaptureIsolationAdmissionOutcome Outcome { get; }
    public PackageAuthenticityFailure Failure { get; }
    public string? FailureCode { get; }
    public string? ManifestDigest { get; }
    public CaptureIsolationPackageEvidence? Evidence { get; }
}
