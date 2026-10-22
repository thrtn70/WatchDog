using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Settings;
using WatchDog.Core.Updates;

namespace WatchDog.Core.Tests.Updates;

public sealed class GitHubUpdateCheckerTests : IDisposable
{
    private static readonly Version TestVersion = new(1, 2, 0);
    private const string ReleaseBase = "https://github.com/thrtn70/WatchDog/releases/download/v1.4.0/";
    private const string InstallerUrl = ReleaseBase + "WatchDog-Setup.exe";
    private const string ManifestUrl = ReleaseBase + "WatchDog-Setup.exe.manifest.json";
    private const string SignatureUrl = ReleaseBase + "WatchDog-Setup.exe.manifest.sig";
    private static readonly byte[] Installer = Enumerable.Range(0, 200_000).Select(i => (byte)(i * 31)).ToArray();
    private static readonly TimeSpan TestWaitLimit = TimeSpan.FromSeconds(10);

    private readonly UpdateManifestTestSigner _signer = new();
    private readonly ManualClock _clock = new();
    private readonly string _downloadDirectory =
        Path.Combine(Path.GetTempPath(), $"WatchDog-UpdateTests-{Guid.NewGuid():N}");

    public void Dispose()
    {
        _signer.Dispose();
        if (Directory.Exists(_downloadDirectory))
            Directory.Delete(_downloadDirectory, recursive: true);
    }

    private static GitHubUpdateChecker CreateChecker(HttpMessageHandler handler)
        => new(
            new HttpClient(handler),
            NullLogger<GitHubUpdateChecker>.Instance,
            new StubSettingsService(),
            () => BuildChannel.Stable,
            () => TestVersion);

    private GitHubUpdateChecker CreateDownloadChecker(HttpMessageHandler handler, TimeProvider? clock = null)
        => new(
            new HttpClient(handler),
            NullLogger<GitHubUpdateChecker>.Instance,
            new StubSettingsService(),
            () => BuildChannel.Stable,
            () => TestVersion,
            new UpdateManifestVerifier(_signer.PublicKey),
            _downloadDirectory,
            clock);

    private static UpdateInfo Update(
        string? manifestUrl = ManifestUrl,
        string? signatureUrl = SignatureUrl,
        string installerUrl = InstallerUrl)
        => new(
            CurrentVersion: "1.2.0",
            LatestVersion: "1.4.0",
            DownloadUrl: installerUrl,
            ReleaseNotesUrl: "https://github.com/thrtn70/WatchDog/releases/tag/v1.4.0",
            IsUpdateAvailable: true,
            ReleaseTag: "v1.4.0",
            ManifestUrl: manifestUrl,
            SignatureUrl: signatureUrl);

    private RoutingHandler SignedRelease(byte[]? servedInstaller = null, byte[]? manifest = null, byte[]? signature = null)
    {
        manifest ??= UpdateManifestTestSigner.Manifest("v1.4.0", Installer);
        return new RoutingHandler(new Dictionary<string, byte[]>
        {
            [InstallerUrl] = servedInstaller ?? Installer,
            [ManifestUrl] = manifest,
            [SignatureUrl] = signature ?? _signer.Sign(manifest),
        });
    }

    private string[] LeftoverFiles() =>
        Directory.Exists(_downloadDirectory) ? Directory.GetFiles(_downloadDirectory) : [];

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

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNull_WhenHttpFails()
    {
        var handler = new StubHandler(HttpStatusCode.InternalServerError, "");
        var checker = CreateChecker(handler);

        var result = await checker.CheckForUpdateAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNull_WhenRateLimited()
    {
        var handler = new StubHandler(HttpStatusCode.Forbidden, "rate limit exceeded");
        var checker = CreateChecker(handler);

        var result = await checker.CheckForUpdateAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsUpdateInfo_WhenNewerVersionAvailable()
    {
        var json = """
        {
            "tag_name": "v99.0.0",
            "html_url": "https://github.com/thrtn70/WatchDog/releases/tag/v99.0.0",
            "assets": [
                {
                    "name": "WatchDog-Setup.exe",
                    "browser_download_url": "https://github.com/thrtn70/WatchDog/releases/download/v99.0.0/WatchDog-Setup.exe"
                }
            ]
        }
        """;
        var handler = new StubHandler(HttpStatusCode.OK, json);
        var checker = CreateChecker(handler);

        var result = await checker.CheckForUpdateAsync();

        Assert.NotNull(result);
        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("99.0.0", result.LatestVersion);
        Assert.Contains("WatchDog-Setup.exe", result.DownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNoUpdate_WhenCurrentVersionIsCurrent()
    {
        var json = """
        {
            "tag_name": "v1.2.0",
            "html_url": "https://github.com/thrtn70/WatchDog/releases/tag/v1.2.0",
            "assets": [
                {
                    "name": "WatchDog-Setup.exe",
                    "browser_download_url": "https://github.com/thrtn70/WatchDog/releases/download/v1.2.0/WatchDog-Setup.exe"
                }
            ]
        }
        """;
        var handler = new StubHandler(HttpStatusCode.OK, json);
        var checker = CreateChecker(handler);

        var result = await checker.CheckForUpdateAsync();

        Assert.NotNull(result);
        Assert.False(result.IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNull_WhenJsonMalformed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "not valid json {{{");
        var checker = CreateChecker(handler);

        var result = await checker.CheckForUpdateAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ReturnsNull_WhenTimeout()
    {
        var handler = new SlowHandler(delay: TimeSpan.FromSeconds(30));
        var checker = CreateChecker(handler);

        var result = await checker.CheckForUpdateAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task CheckForUpdateAsync_RejectsUntrustedHost()
    {
        var json = """
        {
            "tag_name": "v99.0.0",
            "html_url": "https://github.com/thrtn70/WatchDog/releases/tag/v99.0.0",
            "assets": [
                {
                    "name": "WatchDog-Setup.exe",
                    "browser_download_url": "https://evil.example.com/WatchDog-Setup.exe"
                }
            ]
        }
        """;
        var handler = new StubHandler(HttpStatusCode.OK, json);
        var checker = CreateChecker(handler);

        var result = await checker.CheckForUpdateAsync();

        Assert.NotNull(result);
        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("", result.DownloadUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_CapturesManifestSignatureAndTag()
    {
        var json = $$"""
        {
            "tag_name": "v1.4.0",
            "html_url": "https://github.com/thrtn70/WatchDog/releases/tag/v1.4.0",
            "assets": [
                { "name": "WatchDog-Setup.exe.manifest.json", "browser_download_url": "{{ManifestUrl}}" },
                { "name": "WatchDog-Setup.exe", "browser_download_url": "{{InstallerUrl}}" },
                { "name": "WatchDog-Setup.exe.manifest.sig", "browser_download_url": "{{SignatureUrl}}" }
            ]
        }
        """;
        var checker = CreateChecker(new StubHandler(HttpStatusCode.OK, json));

        var result = await checker.CheckForUpdateAsync();

        Assert.NotNull(result);
        Assert.Equal(InstallerUrl, result.DownloadUrl);
        Assert.Equal("v1.4.0", result.ReleaseTag);
        Assert.Equal(ManifestUrl, result.ManifestUrl);
        Assert.Equal(SignatureUrl, result.SignatureUrl);
    }

    [Fact]
    public async Task CheckForUpdateAsync_IgnoresManifestOnUntrustedHost()
    {
        var json = $$"""
        {
            "tag_name": "v1.4.0",
            "html_url": "https://github.com/thrtn70/WatchDog/releases/tag/v1.4.0",
            "assets": [
                { "name": "WatchDog-Setup.exe", "browser_download_url": "{{InstallerUrl}}" },
                { "name": "WatchDog-Setup.exe.manifest.json", "browser_download_url": "https://evil.example.com/m.json" },
                { "name": "WatchDog-Setup.exe.manifest.sig", "browser_download_url": "http://github.com/m.sig" }
            ]
        }
        """;
        var checker = CreateChecker(new StubHandler(HttpStatusCode.OK, json));

        var result = await checker.CheckForUpdateAsync();

        Assert.NotNull(result);
        Assert.Null(result.ManifestUrl);
        Assert.Null(result.SignatureUrl);
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_ReturnsInstaller_WhenSignedManifestMatchesBytes()
    {
        var checker = CreateDownloadChecker(SignedRelease());

        var result = await checker.DownloadVerifiedInstallerAsync(Update());

        Assert.True(result.IsVerified);
        Assert.Equal(UpdateVerificationFailure.None, result.Failure);
        Assert.NotNull(result.InstallerPath);
        Assert.StartsWith(_downloadDirectory + Path.DirectorySeparatorChar, result.InstallerPath);
        Assert.EndsWith(".exe", result.InstallerPath);
        Assert.Equal(Installer, File.ReadAllBytes(result.InstallerPath));
    }

    [Fact]
    public async Task VerifiedInstaller_LockForLaunch_BlocksWritesUntilReleased()
    {
        var checker = CreateDownloadChecker(SignedRelease());
        var result = await checker.DownloadVerifiedInstallerAsync(Update());

        using (var launchLock = result.LockForLaunch())
        {
            Assert.NotNull(launchLock);
            Assert.Throws<IOException>(() => File.Open(result.InstallerPath!, FileMode.Open, FileAccess.Write));
            Assert.Throws<IOException>(() => File.Delete(result.InstallerPath!));
        }

        using (File.Open(result.InstallerPath!, FileMode.Open, FileAccess.Write)) { }
    }

    [Fact]
    public async Task VerifiedInstaller_LockForLaunch_Refuses_WhenFileChangedAfterVerification()
    {
        var checker = CreateDownloadChecker(SignedRelease());
        var result = await checker.DownloadVerifiedInstallerAsync(Update());
        var swapped = (byte[])Installer.Clone();
        swapped[0] ^= 0xFF;
        File.WriteAllBytes(result.InstallerPath!, swapped);

        Assert.Null(result.LockForLaunch());
    }

    [Fact]
    public void RefusedDownload_LockForLaunch_ReturnsNull()
    {
        Assert.Null(UpdateDownloadResult.Refused(UpdateVerificationFailure.SignatureInvalid).LockForLaunch());
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_RefusesAndDeletes_WhenInstallerBytesDiffer()
    {
        var tampered = (byte[])Installer.Clone();
        tampered[^1] ^= 0xFF;
        var checker = CreateDownloadChecker(SignedRelease(servedInstaller: tampered));

        var result = await checker.DownloadVerifiedInstallerAsync(Update());

        Assert.False(result.IsVerified);
        Assert.Equal(UpdateVerificationFailure.InstallerMismatch, result.Failure);
        Assert.Null(result.InstallerPath);
        Assert.Empty(LeftoverFiles());
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_RefusesAndDeletes_WhenInstallerLongerThanManifest()
    {
        var checker = CreateDownloadChecker(SignedRelease(servedInstaller: [.. Installer, 0x00]));

        var result = await checker.DownloadVerifiedInstallerAsync(Update());

        Assert.Equal(UpdateVerificationFailure.InstallerMismatch, result.Failure);
        Assert.Null(result.InstallerPath);
        Assert.Empty(LeftoverFiles());
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_RefusesAndDeletes_WhenInstallerTruncated()
    {
        var checker = CreateDownloadChecker(SignedRelease(servedInstaller: Installer[..^1]));

        var result = await checker.DownloadVerifiedInstallerAsync(Update());

        Assert.Equal(UpdateVerificationFailure.InstallerMismatch, result.Failure);
        Assert.Empty(LeftoverFiles());
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_RefusesWithoutDownloading_WhenReleaseHasNoManifest()
    {
        var handler = SignedRelease();
        var checker = CreateDownloadChecker(handler);

        var result = await checker.DownloadVerifiedInstallerAsync(Update(manifestUrl: null));

        Assert.Equal(UpdateVerificationFailure.MissingManifest, result.Failure);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_RefusesWithoutDownloading_WhenSignatureAssetMissing()
    {
        var handler = SignedRelease();
        var checker = CreateDownloadChecker(handler);

        var result = await checker.DownloadVerifiedInstallerAsync(Update(signatureUrl: null));

        Assert.Equal(UpdateVerificationFailure.MissingManifest, result.Failure);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_NeverFetchesInstaller_WhenSignatureIsFromAnotherKey()
    {
        using var attacker = new UpdateManifestTestSigner();
        var manifest = UpdateManifestTestSigner.Manifest("v1.4.0", Installer);
        var handler = SignedRelease(manifest: manifest, signature: attacker.Sign(manifest));
        var checker = CreateDownloadChecker(handler);

        var result = await checker.DownloadVerifiedInstallerAsync(Update());

        Assert.Equal(UpdateVerificationFailure.SignatureInvalid, result.Failure);
        Assert.DoesNotContain(InstallerUrl, handler.Requested);
        Assert.Empty(LeftoverFiles());
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_Refuses_WhenManifestIsForAnotherRelease()
    {
        var manifest = UpdateManifestTestSigner.Manifest("v1.3.5", Installer);
        var checker = CreateDownloadChecker(SignedRelease(manifest: manifest));

        var result = await checker.DownloadVerifiedInstallerAsync(Update());

        Assert.Equal(UpdateVerificationFailure.ReleaseMismatch, result.Failure);
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_Refuses_WhenManifestCannotBeFetched()
    {
        var handler = new RoutingHandler(new Dictionary<string, byte[]> { [InstallerUrl] = Installer });
        var checker = CreateDownloadChecker(handler);

        var result = await checker.DownloadVerifiedInstallerAsync(Update());

        Assert.Equal(UpdateVerificationFailure.DownloadFailed, result.Failure);
        Assert.DoesNotContain(InstallerUrl, handler.Requested);
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_Refuses_WhenInstallerUrlIsUntrusted()
    {
        var handler = SignedRelease();
        var checker = CreateDownloadChecker(handler);

        var result = await checker.DownloadVerifiedInstallerAsync(
            Update(installerUrl: "https://evil.example.com/WatchDog-Setup.exe"));

        Assert.Equal(UpdateVerificationFailure.DownloadFailed, result.Failure);
        Assert.Empty(handler.Requested);
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_ReturnsInstaller_WhenSlowDownloadKeepsProgressing()
    {
        // 16 chunks 30 s apart: 8 minutes in all, past the old fixed 5-minute budget, never silent for a minute.
        var link = new SlowLinkStream(Installer, chunkSize: 12_500, _clock, perChunk: TimeSpan.FromSeconds(30));
        var checker = CreateDownloadChecker(new SlowLinkHandler(SignedRelease(), link), _clock);
        var started = _clock.GetUtcNow();

        var result = await checker.DownloadVerifiedInstallerAsync(Update());

        Assert.True(result.IsVerified);
        Assert.Equal(TimeSpan.FromMinutes(8), _clock.GetUtcNow() - started);
        Assert.Equal(Installer, File.ReadAllBytes(result.InstallerPath!));
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_RefusesAndDeletes_WhenDownloadStalls()
    {
        var link = new SlowLinkStream(Installer, chunkSize: 10_000, _clock, perChunk: TimeSpan.Zero, stallAt: 50_000);
        var checker = CreateDownloadChecker(new SlowLinkHandler(SignedRelease(), link), _clock);

        var download = checker.DownloadVerifiedInstallerAsync(Update());
        await link.Stalled.WaitAsync(TestWaitLimit);
        Assert.Single(LeftoverFiles());

        _clock.Advance(TimeSpan.FromSeconds(59));
        Assert.False(link.ReadToken.IsCancellationRequested);

        _clock.Advance(TimeSpan.FromSeconds(2));
        var result = await download.WaitAsync(TestWaitLimit);

        Assert.Equal(UpdateVerificationFailure.DownloadFailed, result.Failure);
        Assert.Null(result.InstallerPath);
        Assert.Empty(LeftoverFiles());
    }

    [Fact]
    public async Task DownloadVerifiedInstaller_RefusesAndDeletes_WhenTrickleOutlastsCeiling()
    {
        // 1 KB every 50 s never trips the stall timeout, but this 200 KB installer would take almost three hours.
        var link = new SlowLinkStream(Installer, chunkSize: 1_000, _clock, perChunk: TimeSpan.FromSeconds(50));
        var checker = CreateDownloadChecker(new SlowLinkHandler(SignedRelease(), link), _clock);

        var result = await checker.DownloadVerifiedInstallerAsync(Update());

        Assert.Equal(UpdateVerificationFailure.DownloadFailed, result.Failure);
        Assert.True(link.Sent < Installer.Length);
        Assert.Empty(LeftoverFiles());
    }

    /// <summary>Serves the signed manifest and signature from <paramref name="signedAssets"/>, and the installer from a slow link.</summary>
    private sealed class SlowLinkHandler(RoutingHandler signedAssets, SlowLinkStream installer)
        : DelegatingHandler(signedAssets)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => request.RequestUri!.AbsoluteUri == InstallerUrl
                ? Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(installer) })
                : base.SendAsync(request, cancellationToken);
    }

    /// <summary>
    /// Installer body on a slow link: each read hands out at most <paramref name="chunkSize"/> bytes, after
    /// <paramref name="perChunk"/> of <paramref name="clock"/> time has passed. From byte <paramref name="stallAt"/>
    /// on it sends nothing until the read is cancelled.
    /// </summary>
    private sealed class SlowLinkStream(
        byte[] body, int chunkSize, ManualClock clock, TimeSpan perChunk, int stallAt = int.MaxValue) : Stream
    {
        private readonly TaskCompletionSource _stalled = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Stalled => _stalled.Task;
        public int Sent { get; private set; }

        /// <summary>The token of the latest read; the checker's timeouts cancel it synchronously.</summary>
        public CancellationToken ReadToken { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ReadToken = cancellationToken;
            if (Sent >= stallAt)
            {
                _stalled.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }

            var count = Math.Min(Math.Min(chunkSize, buffer.Length), body.Length - Sent);
            if (count == 0) return 0;

            clock.Advance(perChunk);
            cancellationToken.ThrowIfCancellationRequested();
            body.AsSpan(Sent, count).CopyTo(buffer.Span);
            Sent += count;
            return count;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
    }

    /// <summary>Serves fixed bodies by absolute URL (404 otherwise) and records every request.</summary>
    private sealed class RoutingHandler(IReadOnlyDictionary<string, byte[]> routes) : HttpMessageHandler
    {
        private readonly List<string> _requested = [];

        public IReadOnlyList<string> Requested
        {
            get { lock (_requested) return [.. _requested]; }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (_requested) _requested.Add(url);
            return Task.FromResult(routes.TryGetValue(url, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    /// <summary>Stub HTTP handler that returns a fixed response.</summary>
    private sealed class StubHandler(HttpStatusCode statusCode, string content)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>Stub HTTP handler that delays longer than the checker's timeout.</summary>
    private sealed class SlowHandler(TimeSpan delay) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}")
            };
        }
    }
}
