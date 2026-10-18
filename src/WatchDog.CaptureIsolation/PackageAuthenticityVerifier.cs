using System.Security.Cryptography;
using System.Text.Json;

namespace WatchDog.CaptureIsolation;

/// <summary>
/// Unregistered verifier for an exact-byte, detached RSA-PSS/SHA-256 fixture manifest. Publisher
/// trust observations must come from a separate OS boundary; this verifier correlates and mints
/// package evidence only after every independent check agrees.
/// </summary>
internal sealed class PackageAuthenticityVerifier
{
    internal PackageAuthenticityDecision Evaluate(
        CaptureIsolationLaunchPlan plan,
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signatureBytes,
        PackageAuthenticityTrustPolicy policy,
        IReadOnlyList<PackageFileObservation> observations)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(observations);
        if (!ValidPolicy(policy))
            return Reject(PackageAuthenticityFailure.PolicyInvalid);
        if (manifestBytes.Length == 0 || manifestBytes.Length > policy.MaximumManifestBytes)
            return Reject(PackageAuthenticityFailure.ManifestTooLarge);
        if (signatureBytes.Length == 0 || signatureBytes.Length > policy.MaximumSignatureBytes)
            return Reject(PackageAuthenticityFailure.SignatureInvalid);

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(policy.ManifestPublicKey.Span, out var consumed);
            if (consumed != policy.ManifestPublicKey.Length || rsa.KeySize < 2048 ||
                !rsa.VerifyData(
                    manifestBytes,
                    signatureBytes,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pss))
            {
                return Reject(PackageAuthenticityFailure.SignatureInvalid);
            }
        }
        catch (CryptographicException)
        {
            return Reject(PackageAuthenticityFailure.PolicyInvalid);
        }

        Manifest manifest;
        try
        {
            manifest = ParseManifest(manifestBytes);
        }
        catch (JsonException)
        {
            return Reject(PackageAuthenticityFailure.ManifestMalformed);
        }
        catch (InvalidDataException)
        {
            return Reject(PackageAuthenticityFailure.ManifestInvalid);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or FormatException or OverflowException)
        {
            return Reject(PackageAuthenticityFailure.ManifestInvalid);
        }

        var manifestDigest = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        if (manifest.Product != policy.Product || manifest.Channel != policy.Channel ||
            manifest.PackageVersion != policy.PackageVersion ||
            manifest.Architecture != (int)plan.Architecture ||
            manifest.ProtocolVersion != plan.ProtocolVersion ||
            manifest.SchemaVersion != (int)plan.SchemaVersion ||
            !string.Equals(manifestDigest, plan.PackageManifestDigest, StringComparison.Ordinal))
        {
            return Reject(PackageAuthenticityFailure.PackageMismatch, manifestDigest);
        }
        if (observations.Count != 2)
            return Reject(PackageAuthenticityFailure.PayloadMismatch, manifestDigest);

        var payloadEvidence = new List<CaptureIsolationPayloadEvidence>(2);
        foreach (var entry in manifest.Entries)
        {
            var matches = observations.Where(observation => observation.Role == entry.Role).ToArray();
            if (matches.Length != 1)
                return Reject(PackageAuthenticityFailure.PayloadMismatch, manifestDigest);
            var observation = matches[0];
            if (!string.Equals(observation.RelativePath, entry.Path, StringComparison.Ordinal) ||
                observation.Length != entry.Length ||
                !string.Equals(observation.Sha256Digest, entry.Sha256, StringComparison.Ordinal))
            {
                return Reject(PackageAuthenticityFailure.PayloadMismatch, manifestDigest);
            }
            if (observation.PublisherTrust != PackagePublisherTrustStatus.Trusted ||
                !policy.PublisherCertificateSha256Pins.Contains(
                    observation.PublisherCertificateSha256,
                    StringComparer.Ordinal))
            {
                return Reject(PackageAuthenticityFailure.PublisherUnverified, manifestDigest);
            }
            var instanceId = entry.Role == CaptureIsolationPayloadRole.Parent
                ? plan.Correlation.ParentInstanceId
                : plan.Correlation.HelperInstanceId;
            var expectedDigest = entry.Role == CaptureIsolationPayloadRole.Parent
                ? plan.ParentPayloadDigest
                : plan.HelperPayloadDigest;
            if (!string.Equals(entry.Sha256, expectedDigest, StringComparison.Ordinal))
                return Reject(PackageAuthenticityFailure.PayloadMismatch, manifestDigest);
            payloadEvidence.Add(new(
                entry.Role,
                instanceId,
                plan.ProtocolVersion,
                plan.SchemaVersion,
                plan.Architecture,
                entry.Sha256));
        }

        return new(
            CaptureIsolationAdmissionOutcome.Admitted,
            PackageAuthenticityFailure.None,
            null,
            manifestDigest,
            new CaptureIsolationPackageEvidence(
                CaptureIsolationEvidenceProvenance.TrustedPackageBoundary,
                CaptureIsolationPublisherAuthenticity.TrustedPublisherBoundary,
                plan.Correlation,
                plan.Correlation.PlanDigest,
                manifestDigest,
                payloadEvidence));
    }

    private static bool ValidPolicy(PackageAuthenticityTrustPolicy policy) =>
        IsToken(policy.Product) && IsToken(policy.Channel) && IsToken(policy.PackageVersion) &&
        policy.ManifestPublicKey.Length is >= 256 and <= 8192 &&
        policy.MaximumManifestBytes is >= 256 and <= 1_048_576 &&
        policy.MaximumSignatureBytes is >= 256 and <= 8192 &&
        policy.PublisherCertificateSha256Pins.Count > 0 &&
        policy.PublisherCertificateSha256Pins.Count <= 8 &&
        policy.PublisherCertificateSha256Pins.All(IsSha256) &&
        policy.PublisherCertificateSha256Pins.Distinct(StringComparer.Ordinal).Count() ==
            policy.PublisherCertificateSha256Pins.Count;

    private static Manifest ParseManifest(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 8,
        });
        var root = document.RootElement;
        RequireObject(root, ["version", "product", "channel", "architecture", "protocolVersion", "schemaVersion", "packageVersion", "entries"]);
        if (root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException();
        var entriesElement = root.GetProperty("entries");
        if (entriesElement.ValueKind != JsonValueKind.Array || entriesElement.GetArrayLength() != 2)
            throw new InvalidDataException();
        var entries = new List<ManifestEntry>(2);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roles = new HashSet<CaptureIsolationPayloadRole>();
        foreach (var element in entriesElement.EnumerateArray())
        {
            RequireObject(element, ["role", "path", "length", "sha256"]);
            var role = (CaptureIsolationPayloadRole)element.GetProperty("role").GetInt32();
            var path = element.GetProperty("path").GetString() ?? string.Empty;
            var length = element.GetProperty("length").GetInt64();
            var digest = element.GetProperty("sha256").GetString() ?? string.Empty;
            if (!Enum.IsDefined(role) || !roles.Add(role) || !IsSafeRelativePath(path) ||
                !paths.Add(path) || length <= 0 || !IsSha256(digest))
            {
                throw new InvalidDataException();
            }
            entries.Add(new(role, path, length, digest));
        }
        var product = root.GetProperty("product").GetString() ?? string.Empty;
        var channel = root.GetProperty("channel").GetString() ?? string.Empty;
        var packageVersion = root.GetProperty("packageVersion").GetString() ?? string.Empty;
        if (!IsToken(product) || !IsToken(channel) || !IsToken(packageVersion))
            throw new InvalidDataException();
        return new(
            product,
            channel,
            packageVersion,
            root.GetProperty("architecture").GetInt32(),
            root.GetProperty("protocolVersion").GetInt32(),
            root.GetProperty("schemaVersion").GetInt32(),
            entries);
    }

    private static void RequireObject(JsonElement element, IReadOnlyCollection<string> allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
                throw new InvalidDataException();
        }
        if (names.Count != allowed.Count)
            throw new InvalidDataException();
    }

    private static bool IsSafeRelativePath(string value) =>
        value.Length is > 0 and <= 240 && !Path.IsPathFullyQualified(value) &&
        !value.Contains('\\') && !value.Contains(':') &&
        value.Split('/').All(static part => part.Length > 0 && part is not "." and not "..");

    private static bool IsToken(string? value) =>
        value is { Length: > 0 and <= 64 } &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_');

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static PackageAuthenticityDecision Reject(
        PackageAuthenticityFailure failure,
        string? manifestDigest = null) =>
        new(
            CaptureIsolationAdmissionOutcome.Rejected,
            failure,
            failure switch
            {
                PackageAuthenticityFailure.PolicyInvalid => "helper.package.policy-invalid",
                PackageAuthenticityFailure.ManifestTooLarge => "helper.package.manifest-size-invalid",
                PackageAuthenticityFailure.ManifestMalformed => "helper.package.manifest-malformed",
                PackageAuthenticityFailure.ManifestInvalid => "helper.package.manifest-invalid",
                PackageAuthenticityFailure.SignatureInvalid => "helper.package.signature-invalid",
                PackageAuthenticityFailure.PackageMismatch => "helper.package.identity-mismatch",
                PackageAuthenticityFailure.PayloadMismatch => "helper.package.payload-mismatch",
                PackageAuthenticityFailure.PublisherUnverified => "helper.package.publisher-unverified",
                _ => "helper.package.rejected",
            },
            manifestDigest,
            null);

    private sealed record Manifest(
        string Product,
        string Channel,
        string PackageVersion,
        int Architecture,
        int ProtocolVersion,
        int SchemaVersion,
        IReadOnlyList<ManifestEntry> Entries);

    private sealed record ManifestEntry(
        CaptureIsolationPayloadRole Role,
        string Path,
        long Length,
        string Sha256);
}
