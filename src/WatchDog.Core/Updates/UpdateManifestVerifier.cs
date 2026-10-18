using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WatchDog.Core.Updates;

/// <summary>Fields of an authenticated update manifest; produced only by <see cref="UpdateManifestVerifier"/>.</summary>
public sealed record UpdateManifest(string Tag, string File, long Length, string Sha256);

/// <summary>
/// Authenticates a release's update manifest (written by tools/sign-update-manifest.ps1): the exact
/// manifest bytes must carry a detached ECDSA P-256/SHA-256 signature (IEEE P1363) from the pinned
/// release key, parse strictly, name the expected release tag and not be older than this build.
/// </summary>
public sealed partial class UpdateManifestVerifier
{
    public const int MaxManifestBytes = 4096;
    public const int MaxSignatureBytes = 256;
    public const long MaxInstallerBytes = 512L * 1024 * 1024;
    private const int SchemaVersion = 1;
    private const string Product = "WatchDog";
    private const string InstallerFileName = "WatchDog-Setup.exe";
    private static readonly string[] ManifestProperties = ["schema", "product", "tag", "file", "length", "sha256"];

    private readonly byte[] _publicKey;

    public UpdateManifestVerifier(ReadOnlySpan<byte> publicKeySpki) => _publicKey = publicKeySpki.ToArray();

    /// <summary>Verifier bound to the release key compiled into this build.</summary>
    public static UpdateManifestVerifier CreatePinned() =>
        new(Convert.FromBase64String(UpdateSigningKey.PublicKeySpkiBase64));

    public UpdateVerificationFailure Verify(
        ReadOnlySpan<byte> manifestBytes,
        ReadOnlySpan<byte> signature,
        string expectedTag,
        Version currentVersion,
        out UpdateManifest? manifest)
    {
        ArgumentNullException.ThrowIfNull(expectedTag);
        ArgumentNullException.ThrowIfNull(currentVersion);
        manifest = null;

        if (manifestBytes.IsEmpty || manifestBytes.Length > MaxManifestBytes)
            return UpdateVerificationFailure.ManifestInvalid;
        if (!IsSignedByPinnedKey(manifestBytes, signature))
            return UpdateVerificationFailure.SignatureInvalid;

        UpdateManifest parsed;
        try
        {
            parsed = Parse(manifestBytes);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or InvalidOperationException
            or FormatException or ArgumentException)
        {
            return UpdateVerificationFailure.ManifestInvalid;
        }

        if (!string.Equals(parsed.Tag, expectedTag, StringComparison.Ordinal))
            return UpdateVerificationFailure.ReleaseMismatch;
        if (TagBaseVersion(parsed.Tag) < Normalize(currentVersion))
            return UpdateVerificationFailure.Downgrade;

        manifest = parsed;
        return UpdateVerificationFailure.None;
    }

    private bool IsSignedByPinnedKey(ReadOnlySpan<byte> manifestBytes, ReadOnlySpan<byte> signature)
    {
        if (signature.IsEmpty || signature.Length > MaxSignatureBytes)
            return false;

        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(_publicKey, out var consumed);
            return consumed == _publicKey.Length && key.KeySize == 256 &&
                key.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static UpdateManifest Parse(ReadOnlySpan<byte> bytes)
    {
        using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
        var root = document.RootElement;
        RequireExactProperties(root);

        var schema = root.GetProperty("schema").GetInt32();
        var product = root.GetProperty("product").GetString();
        var tag = root.GetProperty("tag").GetString();
        var file = root.GetProperty("file").GetString();
        var length = root.GetProperty("length").GetInt64();
        var sha256 = root.GetProperty("sha256").GetString();

        if (schema != SchemaVersion || product != Product || file != InstallerFileName ||
            tag is null || !TagPattern().IsMatch(tag) ||
            length is <= 0 or > MaxInstallerBytes ||
            sha256 is null || !Sha256Pattern().IsMatch(sha256))
        {
            throw new InvalidDataException("Update manifest fields are invalid.");
        }

        return new UpdateManifest(tag, file, length, sha256);
    }

    private static void RequireExactProperties(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Update manifest must be a JSON object.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!ManifestProperties.Contains(property.Name) || !seen.Add(property.Name))
                throw new InvalidDataException("Update manifest has an unexpected or repeated property.");
        }

        if (seen.Count != ManifestProperties.Length)
            throw new InvalidDataException("Update manifest is missing a property.");
    }

    private static Version TagBaseVersion(string tag)
    {
        var match = TagPattern().Match(tag);
        return new Version(Component("major"), Component("minor"), Component("patch"));

        int Component(string name) =>
            int.Parse(match.Groups[name].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    [GeneratedRegex(@"\Av(?<major>[0-9]{1,5})\.(?<minor>[0-9]{1,5})\.(?<patch>[0-9]{1,5})(-[0-9A-Za-z.-]{1,32})?\z")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\A[0-9a-f]{64}\z")]
    private static partial Regex Sha256Pattern();
}
