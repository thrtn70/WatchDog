using System.Security.Cryptography;
using System.Text;

namespace WatchDog.Core.Tests.Updates;

/// <summary>Throwaway P-256 key that builds and signs update manifests the way release.yml does.</summary>
internal sealed class UpdateManifestTestSigner : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public byte[] PublicKey => _key.ExportSubjectPublicKeyInfo();

    public byte[] Sign(byte[] manifest) => _key.SignData(manifest, HashAlgorithmName.SHA256);

    public static byte[] Manifest(string tag, byte[] installer) =>
        ManifestJson(tag, installer.LongLength, Convert.ToHexStringLower(SHA256.HashData(installer)));

    public static byte[] ManifestJson(
        string tag = "v1.4.0",
        long length = 1234,
        string? sha256 = null,
        string product = "WatchDog",
        string file = "WatchDog-Setup.exe") =>
        Encoding.UTF8.GetBytes(
            $$"""{"schema":1,"product":"{{product}}","tag":"{{tag}}","file":"{{file}}","length":{{length}},"sha256":"{{sha256 ?? new string('a', 64)}}"}""");

    public void Dispose() => _key.Dispose();
}
