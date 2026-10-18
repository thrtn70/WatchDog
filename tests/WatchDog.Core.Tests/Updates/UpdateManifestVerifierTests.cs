using System.Security.Cryptography;
using System.Text;
using WatchDog.Core.Updates;

namespace WatchDog.Core.Tests.Updates;

public sealed class UpdateManifestVerifierTests : IDisposable
{
    private static readonly Version Current = new(1, 3, 0);
    private const string Tag = "v1.4.0";

    private readonly UpdateManifestTestSigner _signer = new();

    public void Dispose() => _signer.Dispose();

    private UpdateVerificationFailure Verify(
        byte[] manifest,
        byte[]? signature = null,
        string expectedTag = Tag,
        Version? current = null) =>
        new UpdateManifestVerifier(_signer.PublicKey).Verify(
            manifest, signature ?? _signer.Sign(manifest), expectedTag, current ?? Current, out _);

    [Fact]
    public void AcceptsManifestSignedByPinnedKeyForExpectedRelease()
    {
        var manifest = UpdateManifestTestSigner.ManifestJson();
        var verifier = new UpdateManifestVerifier(_signer.PublicKey);

        var failure = verifier.Verify(manifest, _signer.Sign(manifest), Tag, Current, out var parsed);

        Assert.Equal(UpdateVerificationFailure.None, failure);
        Assert.Equal(new UpdateManifest(Tag, "WatchDog-Setup.exe", 1234, new string('a', 64)), parsed);
    }

    [Fact]
    public void AcceptsSameVersionPreReleaseRebuild()
    {
        var manifest = UpdateManifestTestSigner.ManifestJson(tag: "v1.3.0-rc.2");

        Assert.Equal(UpdateVerificationFailure.None, Verify(manifest, expectedTag: "v1.3.0-rc.2"));
    }

    [Fact]
    public void RejectsManifestChangedAfterSigning()
    {
        var manifest = UpdateManifestTestSigner.ManifestJson();
        var signature = _signer.Sign(manifest);
        var tampered = UpdateManifestTestSigner.ManifestJson(length: 1235);

        Assert.Equal(UpdateVerificationFailure.SignatureInvalid, Verify(tampered, signature));
    }

    [Fact]
    public void RejectsManifestSignedByAnotherKey()
    {
        using var other = new UpdateManifestTestSigner();
        var manifest = UpdateManifestTestSigner.ManifestJson();

        Assert.Equal(UpdateVerificationFailure.SignatureInvalid, Verify(manifest, other.Sign(manifest)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(63)]
    [InlineData(65)]
    public void RejectsMissingOrTruncatedSignature(int length)
    {
        var manifest = UpdateManifestTestSigner.ManifestJson();
        var signature = _signer.Sign(manifest).Concat(new byte[1]).Take(length).ToArray();

        Assert.Equal(UpdateVerificationFailure.SignatureInvalid, Verify(manifest, signature));
    }

    [Fact]
    public void RejectsEverythingWhenPinnedKeyIsNotP256()
    {
        using var rsa = RSA.Create(2048);
        var manifest = UpdateManifestTestSigner.ManifestJson();
        var verifier = new UpdateManifestVerifier(rsa.ExportSubjectPublicKeyInfo());

        var failure = verifier.Verify(manifest, _signer.Sign(manifest), Tag, Current, out var parsed);

        Assert.Equal(UpdateVerificationFailure.SignatureInvalid, failure);
        Assert.Null(parsed);
    }

    [Fact]
    public void RejectsOversizedManifestBeforeCheckingSignature()
    {
        var manifest = new byte[UpdateManifestVerifier.MaxManifestBytes + 1];

        Assert.Equal(UpdateVerificationFailure.ManifestInvalid, Verify(manifest));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"schema":2,"product":"WatchDog","tag":"v1.4.0","file":"WatchDog-Setup.exe","length":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("""{"schema":"1","product":"WatchDog","tag":"v1.4.0","file":"WatchDog-Setup.exe","length":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("""{"schema":1,"product":"WatchDog","tag":"v1.4.0","file":"WatchDog-Setup.exe","length":1}""")]
    [InlineData("""{"schema":1,"product":"WatchDog","tag":"v1.4.0","file":"WatchDog-Setup.exe","length":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","url":"x"}""")]
    [InlineData("""{"schema":1,"product":"WatchDog","tag":"v1.4.0","tag":"v1.4.0","file":"WatchDog-Setup.exe","length":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""")]
    [InlineData("""{"schema":1,"product":"WatchDog","tag":"v1.4.0","file":"WatchDog-Setup.exe","length":1,"sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"} // c""")]
    public void RejectsMalformedOrUnexpectedManifestShape(string json)
    {
        Assert.Equal(UpdateVerificationFailure.ManifestInvalid, Verify(Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("Other", "WatchDog-Setup.exe", 1L, null)]
    [InlineData("WatchDog", "Evil-Setup.exe", 1L, null)]
    [InlineData("WatchDog", "WatchDog-Setup.exe", 0L, null)]
    [InlineData("WatchDog", "WatchDog-Setup.exe", -1L, null)]
    [InlineData("WatchDog", "WatchDog-Setup.exe", 536_870_913L, null)]
    [InlineData("WatchDog", "WatchDog-Setup.exe", 1L, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("WatchDog", "WatchDog-Setup.exe", 1L, "aaaa")]
    public void RejectsManifestForWrongProductFileOrDigestShape(
        string product, string file, long length, string? sha256)
    {
        var manifest = UpdateManifestTestSigner.ManifestJson(
            length: length, sha256: sha256, product: product, file: file);

        Assert.Equal(UpdateVerificationFailure.ManifestInvalid, Verify(manifest));
    }

    [Theory]
    [InlineData("1.4.0")]
    [InlineData("v1.4")]
    [InlineData("v1.4.0-")]
    [InlineData("v1.4.0 ")]
    [InlineData("v1.4.0-rc/1")]
    public void RejectsManifestWithInvalidTag(string tag)
    {
        var manifest = UpdateManifestTestSigner.ManifestJson(tag: tag);

        Assert.Equal(UpdateVerificationFailure.ManifestInvalid, Verify(manifest, expectedTag: tag));
    }

    [Fact]
    public void RejectsManifestForAnotherRelease()
    {
        var manifest = UpdateManifestTestSigner.ManifestJson(tag: "v1.3.5");

        Assert.Equal(UpdateVerificationFailure.ReleaseMismatch, Verify(manifest, expectedTag: Tag));
    }

    [Theory]
    [InlineData("v1.2.9")]
    [InlineData("v0.9.0")]
    public void RejectsDowngradeToOlderSignedRelease(string tag)
    {
        var manifest = UpdateManifestTestSigner.ManifestJson(tag: tag);

        Assert.Equal(UpdateVerificationFailure.Downgrade, Verify(manifest, expectedTag: tag));
    }

    [Fact]
    public void AcceptsManifestProducedBySigningScript()
    {
        // Produced by tools/sign-update-manifest.ps1 -ExpectedPublicKey <scriptPublicKey> with a throwaway
        // key (not the release key) for a 34-byte installer, so the release script and the app agree on exact bytes.
        const string scriptPublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEf2gMRjdCLwXJkdNq+bmZAzPEijwBKH009DCQwcVFuhzRZzWI7l5Zm9xFAS/bBSh7JWCylAHKDt6x1EngtjglnA==";
        const string scriptManifest = "eyJzY2hlbWEiOjEsInByb2R1Y3QiOiJXYXRjaERvZyIsInRhZyI6InYxLjQuMCIsImZpbGUiOiJXYXRjaERvZy1TZXR1cC5leGUiLCJsZW5ndGgiOjM0LCJzaGEyNTYiOiJiYmRkMmRjM2VjNGUwYWI4ZjdiMGNhYzA3YTIyMGU0MDBhODE0ODM1MTZiNWVkM2JhZjdlY2NiOTg3OGFlMjY5In0=";
        const string scriptSignature = "/5h8m2NNXwWy09jHYwI1hXmyvJ7rUATOBobza5ERXuNAl+I2lWgQNVhV8RopcmqWKtVcx63DyLMMFKbiB4kE4A==";
        var installer = Encoding.ASCII.GetBytes("WatchDog update fixture installer\n");
        var verifier = new UpdateManifestVerifier(Convert.FromBase64String(scriptPublicKey));

        var failure = verifier.Verify(
            Convert.FromBase64String(scriptManifest),
            Convert.FromBase64String(scriptSignature),
            Tag,
            Current,
            out var parsed);

        Assert.Equal(UpdateVerificationFailure.None, failure);
        Assert.NotNull(parsed);
        Assert.Equal(installer.LongLength, parsed.Length);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(installer)), parsed.Sha256);
    }

    [Fact]
    public void PinnedReleaseKeyIsP256AndNotTheTestKey()
    {
        var pinned = Convert.FromBase64String(UpdateSigningKey.PublicKeySpkiBase64);
        using var key = ECDsa.Create();

        key.ImportSubjectPublicKeyInfo(pinned, out var consumed);

        Assert.Equal(pinned.Length, consumed);
        Assert.Equal(256, key.KeySize);
        Assert.NotEqual(
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEf2gMRjdCLwXJkdNq+bmZAzPEijwBKH009DCQwcVFuhzRZzWI7l5Zm9xFAS/bBSh7JWCylAHKDt6x1EngtjglnA==",
            UpdateSigningKey.PublicKeySpkiBase64);
    }

    [Theory]
    [InlineData(UpdateVerificationFailure.DownloadFailed)]
    [InlineData(UpdateVerificationFailure.MissingManifest)]
    [InlineData(UpdateVerificationFailure.SignatureInvalid)]
    [InlineData(UpdateVerificationFailure.ManifestInvalid)]
    [InlineData(UpdateVerificationFailure.ReleaseMismatch)]
    [InlineData(UpdateVerificationFailure.Downgrade)]
    [InlineData(UpdateVerificationFailure.InstallerMismatch)]
    public void EveryRefusalTellsTheUserNothingWasInstalled(UpdateVerificationFailure failure)
    {
        var message = UpdateDownloadResult.Refused(failure).RefusalMessage;

        Assert.Contains("wasn't installed", message, StringComparison.Ordinal);
        Assert.Contains("current version is unchanged", message, StringComparison.Ordinal);
    }
}
