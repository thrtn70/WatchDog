using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Settings;
using WatchDog.Core.Updates;

namespace WatchDog.Core.Tests.Updates;

public sealed class StaleUpdateInstallerCleanupTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LongAgo = Now - StaleUpdateInstallerCleanup.MinimumAge - TimeSpan.FromSeconds(1);

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"WatchDog-UpdateCleanupTests-{Guid.NewGuid():N}");

    public StaleUpdateInstallerCleanupTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        // A just-killed process can hold its image for a moment; leftovers in %TEMP% are harmless.
        try { Directory.Delete(_directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    [Fact]
    public void DeletesInstallersAnEarlierRunLeftBehind()
    {
        var first = Installer();
        var second = Installer();

        var deleted = StaleUpdateInstallerCleanup.DeleteStaleInstallers(_directory, Now);

        Assert.Equal(2, deleted);
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
    }

    [Fact]
    public void KeepsAnInstallerThatIsStillDownloading()
    {
        var path = Installer();
        // GitHubUpdateChecker writes the installer through an unshared stream.
        using var download = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);

        Assert.Equal(0, StaleUpdateInstallerCleanup.DeleteStaleInstallers(_directory, Now));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void KeepsAnInstallerHeldForLaunch()
    {
        byte[] bytes = [0x4D, 0x5A, 0x90, 0x00];
        var path = Installer(bytes: bytes);
        var manifest = new UpdateManifest("v1.4.1", "WatchDog-Setup.exe", bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        using var launchLock = UpdateDownloadResult.Verified(path, manifest).LockForLaunch();
        Assert.NotNull(launchLock);

        Assert.Equal(0, StaleUpdateInstallerCleanup.DeleteStaleInstallers(_directory, Now));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void KeepsAnInstallerThatIsRunning()
    {
        var path = Path.Combine(_directory, $"WatchDog-Setup-{Guid.NewGuid():N}.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), path);
        File.SetLastWriteTimeUtc(path, LongAgo);
        using var installer = Process.Start(new ProcessStartInfo(path, "-n 60 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;

        try
        {
            Assert.Equal(0, StaleUpdateInstallerCleanup.DeleteStaleInstallers(_directory, Now));
            Assert.True(File.Exists(path));
        }
        finally
        {
            installer.Kill(entireProcessTree: true);
            installer.WaitForExit();
        }
    }

    [Fact]
    public void KeepsInstallersWrittenRecently()
    {
        var recent = Installer(lastWriteUtc: Now - StaleUpdateInstallerCleanup.MinimumAge + TimeSpan.FromMinutes(1));

        Assert.Equal(0, StaleUpdateInstallerCleanup.DeleteStaleInstallers(_directory, Now));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void IgnoresFilesThatAreNotDownloadedInstallers()
    {
        string[] others =
        [
            Installer("WatchDog-Setup.exe"),
            Installer("WatchDog-Setup-abc.exe.tmp"),
            Installer("WatchDog-Setup-abc.txt"),
            Installer("Other-Setup-abc.exe"),
            Installer("notes.txt"),
        ];

        Assert.Equal(0, StaleUpdateInstallerCleanup.DeleteStaleInstallers(_directory, Now));
        Assert.All(others, path => Assert.True(File.Exists(path), path));
    }

    [Fact]
    public void IgnoresInstallersOutsideTheDirectoryItself()
    {
        var nested = Path.Combine(_directory, "nested");
        Directory.CreateDirectory(nested);
        var path = Path.Combine(nested, "WatchDog-Setup-abc.exe");
        File.WriteAllBytes(path, [1]);
        File.SetLastWriteTimeUtc(path, LongAgo);

        Assert.Equal(0, StaleUpdateInstallerCleanup.DeleteStaleInstallers(_directory, Now));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void ReturnsZero_WhenTheDirectoryDoesNotExist()
    {
        var missing = Path.Combine(_directory, "missing");

        Assert.Equal(0, StaleUpdateInstallerCleanup.DeleteStaleInstallers(missing, Now));
    }

    [Fact]
    public async Task CleansUpWhereAndWhatTheUpdateCheckerActuallyDownloads()
    {
        const string releaseBase = "https://github.com/thrtn70/WatchDog/releases/download/v1.4.0/";
        using var signer = new UpdateManifestTestSigner();
        byte[] installer = [0x4D, 0x5A, 0x90, 0x00];
        var manifest = UpdateManifestTestSigner.Manifest("v1.4.0", installer);
        var routes = new Dictionary<string, byte[]>
        {
            [releaseBase + "WatchDog-Setup.exe"] = installer,
            [releaseBase + "WatchDog-Setup.exe.manifest.json"] = manifest,
            [releaseBase + "WatchDog-Setup.exe.manifest.sig"] = signer.Sign(manifest),
        };
        // No download directory override: the checker uses its production default.
        var checker = new GitHubUpdateChecker(
            new HttpClient(new RouteHandler(routes)),
            NullLogger<GitHubUpdateChecker>.Instance,
            new StubSettingsService(),
            () => BuildChannel.Stable,
            () => new Version(1, 2, 0),
            new UpdateManifestVerifier(signer.PublicKey));

        var result = await checker.DownloadVerifiedInstallerAsync(new UpdateInfo(
            CurrentVersion: "1.2.0",
            LatestVersion: "1.4.0",
            DownloadUrl: releaseBase + "WatchDog-Setup.exe",
            ReleaseNotesUrl: "https://github.com/thrtn70/WatchDog/releases/tag/v1.4.0",
            IsUpdateAvailable: true,
            ReleaseTag: "v1.4.0",
            ManifestUrl: releaseBase + "WatchDog-Setup.exe.manifest.json",
            SignatureUrl: releaseBase + "WatchDog-Setup.exe.manifest.sig"));

        try
        {
            Assert.True(result.IsVerified);
            Assert.Equal(StaleUpdateInstallerCleanup.DefaultDirectory, Path.GetDirectoryName(result.InstallerPath));

            // The cleanup recognises the checker's file name (tried on a copy, never the real directory).
            var copy = Path.Combine(_directory, Path.GetFileName(result.InstallerPath)!);
            File.Copy(result.InstallerPath!, copy);
            File.SetLastWriteTimeUtc(copy, LongAgo);
            Assert.Equal(1, StaleUpdateInstallerCleanup.DeleteStaleInstallers(_directory, Now));
        }
        finally
        {
            if (result.InstallerPath is { } leftover)
                File.Delete(leftover);
        }
    }

    private string Installer(string? name = null, DateTime? lastWriteUtc = null, byte[]? bytes = null)
    {
        var path = Path.Combine(_directory, name ?? $"WatchDog-Setup-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, bytes ?? [1, 2, 3]);
        File.SetLastWriteTimeUtc(path, lastWriteUtc ?? LongAgo);
        return path;
    }

    private sealed class RouteHandler(IReadOnlyDictionary<string, byte[]> routes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private sealed class StubSettingsService : ISettingsService
    {
        private AppSettings _settings = new();
        public AppSettings Load() => _settings;
        public void Save(AppSettings settings) => _settings = settings;
        public event Action<AppSettings>? SettingsChanged
        {
            add { }
            remove { }
        }
    }
}
