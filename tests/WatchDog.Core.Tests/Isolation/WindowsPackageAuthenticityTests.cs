using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WatchDog.CaptureIsolation;
using WatchDog.CaptureIsolation.Windows;

namespace WatchDog.Core.Tests.Isolation;

public sealed class WindowsPackageAuthenticityTests
{
    [Fact]
    public void RetainedLeaseHashesExactFileAndRevalidatesPublisherObservation()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watchdog-package-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray());
        var api = new FakeAuthenticodeApi();
        try
        {
            using var lease = WindowsPackageFileLease.Open(
                path,
                CaptureIsolationPayloadRole.Helper,
                "helper.exe",
                CancellationToken.None,
                api);

            Assert.Equal(256, lease.Observation.Length);
            Assert.Equal(PackagePublisherTrustStatus.Trusted, lease.Observation.PublisherTrust);
            Assert.True(lease.Revalidate(CancellationToken.None));
            Assert.Equal(2, api.Calls);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("relative.exe")]
    [InlineData("\\\\server\\share\\helper.exe")]
    [InlineData("\\\\?\\C:\\helper.exe")]
    [InlineData("\\\\.\\C:\\helper.exe")]
    public void NonLocalOrNonCanonicalPackagePathsFailClosed(string path)
    {
        var error = Assert.Throws<HelperBoundaryException>(() =>
            WindowsPackageFileLease.Open(
                path,
                CaptureIsolationPayloadRole.Helper,
                "helper.exe",
                CancellationToken.None,
                new FakeAuthenticodeApi()));

        Assert.Equal("helper.package.path-invalid", error.FailureCode);
    }

    [Fact]
    public void DisposedLeaseCannotBeRevalidated()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watchdog-package-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "fixture");
        var lease = WindowsPackageFileLease.Open(
            path,
            CaptureIsolationPayloadRole.Parent,
            "parent.exe",
            CancellationToken.None,
            new FakeAuthenticodeApi());
        lease.Dispose();
        try
        {
            Assert.Throws<ObjectDisposedException>(() => lease.Revalidate(CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NativeAuthenticodeBoundaryFailsClosedForUnsignedTestAssembly()
    {
        var path = typeof(WindowsPackageAuthenticityTests).Assembly.Location;

        using var lease = WindowsPackageFileLease.Open(
            path,
            CaptureIsolationPayloadRole.Parent,
            "parent.dll",
            CancellationToken.None);

        Assert.NotEqual(PackagePublisherTrustStatus.Trusted, lease.Observation.PublisherTrust);
        Assert.Empty(lease.Observation.PublisherCertificateSha256);
        Assert.True(lease.Revalidate(CancellationToken.None));
    }

    [Fact]
    public void TrustedProviderResultFailsClosedWhenStateCleanupFails()
    {
        using var certificate = CreateCodeSigningCertificate();
        var provider = new FakeTrustProvider(certificate) { CloseSucceeds = false };
        var api = new WinVerifyTrustAuthenticodeApi(provider);
        using var stream = File.OpenRead(typeof(WindowsPackageAuthenticityTests).Assembly.Location);

        var result = api.Verify(stream.SafeFileHandle, "redacted");

        Assert.Equal(PackagePublisherTrustStatus.Unavailable, result.Status);
        Assert.Empty(result.PublisherCertificateSha256);
        Assert.Equal(1, provider.CloseCalls);
    }

    [Fact]
    public void FailedVerificationAndCertificateExtractionBothCloseProviderState()
    {
        using var certificate = CreateCodeSigningCertificate();
        var failedProvider = new FakeTrustProvider(certificate)
        {
            VerifyStatus = unchecked((int)0x800B0100),
        };
        var failedApi = new WinVerifyTrustAuthenticodeApi(failedProvider);
        using var stream = File.OpenRead(typeof(WindowsPackageAuthenticityTests).Assembly.Location);
        var failed = failedApi.Verify(stream.SafeFileHandle, "redacted");
        Assert.Equal(PackagePublisherTrustStatus.Unsigned, failed.Status);
        Assert.Equal(1, failedProvider.CloseCalls);

        var extractionProvider = new FakeTrustProvider(certificate) { ThrowOnLoad = true };
        var extractionApi = new WinVerifyTrustAuthenticodeApi(extractionProvider);
        var extraction = extractionApi.Verify(stream.SafeFileHandle, "redacted");
        Assert.Equal(PackagePublisherTrustStatus.Unavailable, extraction.Status);
        Assert.Equal(1, extractionProvider.CloseCalls);
    }

    [Fact]
    public void PackageOpenFailureUsesPrivacySafeBoundedCode()
    {
        var path = Path.Combine(Path.GetTempPath(), $"watchdog-package-{Guid.NewGuid():N}.bin");
        File.WriteAllText(path, "fixture");
        try
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var error = Assert.Throws<HelperBoundaryException>(() =>
                WindowsPackageFileLease.Open(
                    path,
                    CaptureIsolationPayloadRole.Helper,
                    "helper.exe",
                    CancellationToken.None,
                    new FakeAuthenticodeApi()));

            Assert.Equal("helper.package.open-failed", error.FailureCode);
            Assert.DoesNotContain(path, error.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static X509Certificate2 CreateCodeSigningCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=WatchDog Fixture",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var usages = new OidCollection { new("1.3.6.1.5.5.7.3.3") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
        return request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private sealed class FakeAuthenticodeApi : IWindowsAuthenticodeApi
    {
        internal int Calls { get; private set; }

        public WindowsAuthenticodeResult Verify(Microsoft.Win32.SafeHandles.SafeFileHandle file, string path)
        {
            Calls++;
            return new(PackagePublisherTrustStatus.Trusted, new string('c', 64));
        }
    }

    private sealed class FakeTrustProvider(X509Certificate2 certificate) : IWindowsTrustProviderApi
    {
        internal int VerifyStatus { get; set; }
        internal bool CloseSucceeds { get; set; } = true;
        internal bool ThrowOnLoad { get; set; }
        internal int CloseCalls { get; private set; }

        public WindowsTrustProviderState Verify(
            Microsoft.Win32.SafeHandles.SafeFileHandle file,
            string path) =>
            new(VerifyStatus, new object());

        public X509Certificate2 LoadSignerCertificate(WindowsTrustProviderState state)
        {
            if (ThrowOnLoad)
                throw new CryptographicException();
            return X509CertificateLoader.LoadCertificate(certificate.RawData);
        }

        public bool Close(WindowsTrustProviderState state)
        {
            CloseCalls++;
            return CloseSucceeds;
        }
    }
}
