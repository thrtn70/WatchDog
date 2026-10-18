using System.Security.Cryptography;
using System.Text;
using WatchDog.CaptureIsolation;
using WatchDog.CaptureProtocol;

namespace WatchDog.Core.Tests.Isolation;

public sealed class PackageAuthenticityTests
{
    [Fact]
    public void SignedExactManifestMintsCorrelatedPackageEvidence()
    {
        using var fixture = Fixture.Create();

        var decision = fixture.Verify();

        Assert.Equal(CaptureIsolationAdmissionOutcome.Admitted, decision.Outcome);
        Assert.Equal(PackageAuthenticityFailure.None, decision.Failure);
        Assert.Equal(fixture.Plan.PackageManifestDigest, decision.ManifestDigest);
        Assert.NotNull(decision.Evidence);
        Assert.Equal(CaptureIsolationEvidenceProvenance.TrustedPackageBoundary, decision.Evidence.Provenance);
        Assert.Equal(CaptureIsolationPublisherAuthenticity.TrustedPublisherBoundary, decision.Evidence.PublisherAuthenticity);
        Assert.Equal(fixture.Plan.Correlation, decision.Evidence.Correlation);
        Assert.Equal(2, decision.Evidence.Payloads.Count);
    }

    [Fact]
    public void ChangedManifestOrWrongKeyFailsBeforeEvidenceMinting()
    {
        using var fixture = Fixture.Create();
        var changed = fixture.Manifest.ToArray();
        changed[^2] ^= 1;
        var changedDecision = fixture.Verify(manifest: changed);
        Assert.Equal(PackageAuthenticityFailure.SignatureInvalid, changedDecision.Failure);
        Assert.Null(changedDecision.Evidence);

        using var foreignKey = RSA.Create(2048);
        var wrongPolicy = fixture.PolicyWithKey(foreignKey.ExportSubjectPublicKeyInfo());
        var wrongKey = fixture.Verify(policy: wrongPolicy);
        Assert.Equal(PackageAuthenticityFailure.SignatureInvalid, wrongKey.Failure);
        Assert.Null(wrongKey.Evidence);
    }

    [Fact]
    public void DuplicateUnknownAndTraversalManifestFieldsFailClosed()
    {
        using var fixture = Fixture.Create();
        var duplicate = Encoding.UTF8.GetBytes(fixture.Json.Replace(
            "\"version\":1,",
            "\"version\":1,\"version\":1,",
            StringComparison.Ordinal));
        Assert.Equal(PackageAuthenticityFailure.ManifestInvalid, fixture.VerifySigned(duplicate).Failure);

        var unknown = Encoding.UTF8.GetBytes(fixture.Json.Replace(
            "\"version\":1,",
            "\"version\":1,\"unknown\":1,",
            StringComparison.Ordinal));
        Assert.Equal(PackageAuthenticityFailure.ManifestInvalid, fixture.VerifySigned(unknown).Failure);

        var traversal = Encoding.UTF8.GetBytes(fixture.Json.Replace(
            "parent.exe",
            "../parent.exe",
            StringComparison.Ordinal));
        Assert.Equal(PackageAuthenticityFailure.ManifestInvalid, fixture.VerifySigned(traversal).Failure);
    }

    [Fact]
    public void SignedManifestForUnexpectedPackageVersionFailsClosed()
    {
        using var fixture = Fixture.Create();
        var policy = fixture.PolicyWithVersion("1.0.1-test");

        var decision = fixture.Verify(policy: policy);

        Assert.Equal(PackageAuthenticityFailure.PackageMismatch, decision.Failure);
        Assert.Null(decision.Evidence);
    }

    [Theory]
    [InlineData("\"architecture\":1", "\"architecture\":\"x64\"")]
    [InlineData("\"protocolVersion\":1", "\"protocolVersion\":1.5")]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":999999999999999999999")]
    [InlineData("\"packageVersion\":\"1.0.0-test\"", "\"packageVersion\":null")]
    public void SignedManifestWithWrongOrOutOfRangeFieldTypesFailsClosed(
        string original,
        string replacement)
    {
        using var fixture = Fixture.Create();
        var malformed = Encoding.UTF8.GetBytes(fixture.Json.Replace(
            original,
            replacement,
            StringComparison.Ordinal));

        var decision = fixture.VerifySigned(malformed);

        Assert.Equal(PackageAuthenticityFailure.ManifestInvalid, decision.Failure);
        Assert.Null(decision.Evidence);
    }

    [Theory]
    [InlineData(PackagePublisherTrustStatus.Unsigned)]
    [InlineData(PackagePublisherTrustStatus.InvalidSignature)]
    [InlineData(PackagePublisherTrustStatus.ChainUntrusted)]
    [InlineData(PackagePublisherTrustStatus.Revoked)]
    [InlineData(PackagePublisherTrustStatus.RevocationUnknown)]
    [InlineData(PackagePublisherTrustStatus.Expired)]
    [InlineData(PackagePublisherTrustStatus.WrongPublisher)]
    [InlineData(PackagePublisherTrustStatus.Unavailable)]
    public void EveryNonTrustedPublisherStateFailsClosed(PackagePublisherTrustStatus status)
    {
        using var fixture = Fixture.Create();
        var observations = fixture.Observations.ToArray();
        observations[1] = observations[1] with { PublisherTrust = status };

        var decision = fixture.Verify(observations: observations);

        Assert.Equal(PackageAuthenticityFailure.PublisherUnverified, decision.Failure);
        Assert.Null(decision.Evidence);
    }

    [Fact]
    public void PayloadHashSizePathAndPublisherPinMustAllMatch()
    {
        using var fixture = Fixture.Create();
        var variants = new[]
        {
            fixture.Observations[0] with { Sha256Digest = Digest('f') },
            fixture.Observations[0] with { Length = fixture.Observations[0].Length + 1 },
            fixture.Observations[0] with { RelativePath = "other.exe" },
            fixture.Observations[0] with { PublisherCertificateSha256 = Digest('f') },
        };

        foreach (var variant in variants)
        {
            var observations = fixture.Observations.ToArray();
            observations[0] = variant;
            var decision = fixture.Verify(observations: observations);
            Assert.NotEqual(PackageAuthenticityFailure.None, decision.Failure);
            Assert.Null(decision.Evidence);
        }
    }

    private static string Digest(char value) => new(value, 64);

    private sealed class Fixture : IDisposable
    {
        private readonly RSA _rsa;
        private readonly PackageAuthenticityVerifier _verifier = new();

        private Fixture(
            RSA rsa,
            string json,
            byte[] manifest,
            byte[] signature,
            PackageAuthenticityTrustPolicy policy,
            CaptureIsolationLaunchPlan plan,
            IReadOnlyList<PackageFileObservation> observations)
        {
            _rsa = rsa;
            Json = json;
            Manifest = manifest;
            Signature = signature;
            Policy = policy;
            Plan = plan;
            Observations = observations;
        }

        internal string Json { get; }
        internal byte[] Manifest { get; }
        internal byte[] Signature { get; }
        internal PackageAuthenticityTrustPolicy Policy { get; }
        internal CaptureIsolationLaunchPlan Plan { get; }
        internal IReadOnlyList<PackageFileObservation> Observations { get; }

        internal static Fixture Create()
        {
            var parentDigest = Digest('d');
            var helperDigest = Digest('e');
            var json = $$"""
                {"version":1,"product":"WatchDog","channel":"fixture","architecture":1,"protocolVersion":1,"schemaVersion":1,"packageVersion":"1.0.0-test","entries":[{"role":1,"path":"parent.exe","length":101,"sha256":"{{parentDigest}}"},{"role":2,"path":"helper.exe","length":202,"sha256":"{{helperDigest}}"}]}
                """;
            var manifest = Encoding.UTF8.GetBytes(json);
            var manifestDigest = Convert.ToHexString(SHA256.HashData(manifest)).ToLowerInvariant();
            var rsa = RSA.Create(2048);
            var signature = rsa.SignData(
                manifest,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pss);
            var publisherPin = Digest('c');
            var policy = new PackageAuthenticityTrustPolicy(
                "WatchDog",
                "fixture",
                "1.0.0-test",
                rsa.ExportSubjectPublicKeyInfo(),
                [publisherPin],
                16_384,
                1024);
            var correlation = ProtocolFramingTests.Correlation() with { PlanDigest = Digest('a') };
            var plan = new CaptureIsolationLaunchPlan(
                correlation,
                AuthenticatedProtocolSession.ProtocolVersion,
                CaptureIsolationSchemaVersion.V1,
                CaptureIsolationArchitecture.X64,
                manifestDigest,
                parentDigest,
                helperDigest,
                Guid.Parse("70000000-0000-0000-0000-000000000007"));
            var observations = new PackageFileObservation[]
            {
                new(CaptureIsolationPayloadRole.Parent, "parent.exe", 101, parentDigest,
                    PackagePublisherTrustStatus.Trusted, publisherPin),
                new(CaptureIsolationPayloadRole.Helper, "helper.exe", 202, helperDigest,
                    PackagePublisherTrustStatus.Trusted, publisherPin),
            };
            return new(rsa, json, manifest, signature, policy, plan, observations);
        }

        internal PackageAuthenticityDecision Verify(
            byte[]? manifest = null,
            PackageAuthenticityTrustPolicy? policy = null,
            IReadOnlyList<PackageFileObservation>? observations = null) =>
            _verifier.Evaluate(
                Plan,
                manifest ?? Manifest,
                Signature,
                policy ?? Policy,
                observations ?? Observations);

        internal PackageAuthenticityDecision VerifySigned(byte[] manifest)
        {
            var signature = _rsa.SignData(manifest, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            return _verifier.Evaluate(Plan, manifest, signature, Policy, Observations);
        }

        internal PackageAuthenticityTrustPolicy PolicyWithKey(byte[] key) => new(
            Policy.Product,
            Policy.Channel,
            Policy.PackageVersion,
            key,
            Policy.PublisherCertificateSha256Pins,
            Policy.MaximumManifestBytes,
            Policy.MaximumSignatureBytes);

        internal PackageAuthenticityTrustPolicy PolicyWithVersion(string packageVersion) => new(
            Policy.Product,
            Policy.Channel,
            packageVersion,
            Policy.ManifestPublicKey.ToArray(),
            Policy.PublisherCertificateSha256Pins,
            Policy.MaximumManifestBytes,
            Policy.MaximumSignatureBytes);

        public void Dispose() => _rsa.Dispose();
    }
}
