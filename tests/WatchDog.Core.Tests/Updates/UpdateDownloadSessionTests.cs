using System.Security.Cryptography;
using WatchDog.Core.Updates;

namespace WatchDog.Core.Tests.Updates;

public sealed class UpdateDownloadSessionTests : IDisposable
{
    private static readonly UpdateInfo Update = new(
        CurrentVersion: "1.4.0",
        LatestVersion: "1.4.1",
        DownloadUrl: "https://github.com/thrtn70/WatchDog/releases/download/v1.4.1/WatchDog-Setup.exe",
        ReleaseNotesUrl: "https://github.com/thrtn70/WatchDog/releases/tag/v1.4.1",
        IsUpdateAvailable: true,
        ReleaseTag: "v1.4.1");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"WatchDog-UpdateSessionTests-{Guid.NewGuid():N}");
    private readonly UpdateDownloadSession _session = new();

    public UpdateDownloadSessionTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task DownloadAsync_ReturnsTheVerifiedInstaller_WhenNotCancelled()
    {
        var verified = VerifiedInstaller();
        var progress = new Progress<double>();
        IProgress<double>? seenProgress = null;
        var seenToken = new CancellationToken(canceled: true);
        var checker = new FakeChecker((p, ct) =>
        {
            seenProgress = p;
            seenToken = ct;
            return Task.FromResult(verified);
        });

        var result = await _session.DownloadAsync(checker, Update, progress);

        Assert.Same(verified, result);
        Assert.True(File.Exists(verified.InstallerPath));
        Assert.Same(progress, seenProgress);
        Assert.True(seenToken.CanBeCanceled);
        Assert.False(seenToken.IsCancellationRequested);
        Assert.False(_session.IsDownloading);
    }

    [Fact]
    public async Task DownloadAsync_PassesRefusalsThrough_WhenNotCancelled()
    {
        var checker = new FakeChecker((_, _) =>
            Task.FromResult(UpdateDownloadResult.Refused(UpdateVerificationFailure.SignatureInvalid)));

        var result = await _session.DownloadAsync(checker, Update);

        Assert.Equal(UpdateVerificationFailure.SignatureInvalid, result.Failure);
    }

    [Fact]
    public async Task Cancel_EndsTheDownloadAsCancelled_InsteadOfDownloadFailed()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new FakeChecker(async (_, ct) =>
        {
            started.SetResult();
            // GitHubUpdateChecker swallows its own cancellation and reports DownloadFailed.
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return UpdateDownloadResult.Refused(UpdateVerificationFailure.DownloadFailed);
        });

        var download = _session.DownloadAsync(checker, Update);
        await started.Task;
        Assert.True(_session.IsDownloading);

        Assert.True(_session.Cancel());
        var result = await download;

        Assert.Equal(UpdateVerificationFailure.Cancelled, result.Failure);
        Assert.False(result.IsVerified);
        Assert.Null(result.InstallerPath);
        Assert.False(_session.IsDownloading);
    }

    [Fact]
    public async Task Cancel_EndsTheDownloadAsCancelled_WhenTheCheckerThrows()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new FakeChecker(async (_, ct) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

        var download = _session.DownloadAsync(checker, Update);
        await started.Task;
        _session.Cancel();
        var result = await download;

        Assert.Equal(UpdateVerificationFailure.Cancelled, result.Failure);
    }

    [Fact]
    public async Task Cancel_DoesNotThrow_WhenACancellationCallbackFails()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var checker = new FakeChecker(async (_, ct) =>
        {
            using var failing = ct.Register(() => throw new InvalidOperationException("callback failed"));
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            return UpdateDownloadResult.Refused(UpdateVerificationFailure.DownloadFailed);
        });

        var download = _session.DownloadAsync(checker, Update);
        await started.Task;

        Assert.True(_session.Cancel());
        Assert.Equal(UpdateVerificationFailure.Cancelled, (await download).Failure);
    }

    [Fact]
    public async Task Cancel_DeletesAndNeverReturnsAnInstallerThatVerifiedAsTheCancelArrived()
    {
        var verified = VerifiedInstaller();
        var checker = new FakeChecker((_, _) =>
        {
            _session.Cancel();
            return Task.FromResult(verified);
        });

        var result = await _session.DownloadAsync(checker, Update);

        Assert.Equal(UpdateVerificationFailure.Cancelled, result.Failure);
        Assert.False(result.IsVerified);
        Assert.Null(result.InstallerPath);
        Assert.Null(result.LockForLaunch());
        Assert.False(File.Exists(verified.InstallerPath));
    }

    [Fact]
    public void Cancel_ReturnsFalse_WhenNothingIsDownloading()
    {
        Assert.False(_session.Cancel());
    }

    [Fact]
    public async Task Cancel_AfterTheDownloadFinished_LeavesTheInstallerAlone()
    {
        var verified = VerifiedInstaller();

        var result = await _session.DownloadAsync(new FakeChecker((_, _) => Task.FromResult(verified)), Update);

        Assert.False(_session.Cancel());
        Assert.True(result.IsVerified);
        using var launchLock = result.LockForLaunch();
        Assert.NotNull(launchLock);
    }

    [Fact]
    public async Task Cancel_DoesNotCarryOverToTheNextDownload()
    {
        var first = new FakeChecker((_, _) =>
        {
            _session.Cancel();
            return Task.FromResult(UpdateDownloadResult.Refused(UpdateVerificationFailure.DownloadFailed));
        });
        Assert.Equal(UpdateVerificationFailure.Cancelled, (await _session.DownloadAsync(first, Update)).Failure);

        var verified = VerifiedInstaller();
        var retryToken = new CancellationToken(canceled: true);
        var retry = new FakeChecker((_, ct) =>
        {
            retryToken = ct;
            return Task.FromResult(verified);
        });
        var result = await _session.DownloadAsync(retry, Update);

        Assert.Same(verified, result);
        Assert.False(retryToken.IsCancellationRequested);
    }

    [Fact]
    public async Task DownloadAsync_Throws_WhenADownloadIsAlreadyInProgress()
    {
        var release = new TaskCompletionSource<UpdateDownloadResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = _session.DownloadAsync(new FakeChecker((_, _) => release.Task), Update);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _session.DownloadAsync(new FakeChecker((_, _) => release.Task), Update));

        release.SetResult(UpdateDownloadResult.Refused(UpdateVerificationFailure.DownloadFailed));
        Assert.Equal(UpdateVerificationFailure.DownloadFailed, (await first).Failure);
    }

    [Fact]
    public async Task DownloadAsync_PropagatesFailuresTheUserDidNotCause()
    {
        var checker = new FakeChecker((_, _) => throw new OperationCanceledException());

        await Assert.ThrowsAsync<OperationCanceledException>(() => _session.DownloadAsync(checker, Update));
        Assert.False(_session.IsDownloading);
    }

    [Fact]
    public void Cancelled_IsNotReportedAsAVerificationFailure()
    {
        var message = UpdateDownloadResult.Refused(UpdateVerificationFailure.Cancelled).RefusalMessage;

        Assert.Contains("cancelled", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verify", message, StringComparison.OrdinalIgnoreCase);
    }

    private UpdateDownloadResult VerifiedInstaller()
    {
        byte[] bytes = [0x4D, 0x5A, 0x90, 0x00, 0x03];
        var path = Path.Combine(_directory, $"WatchDog-Setup-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, bytes);
        var manifest = new UpdateManifest("v1.4.1", "WatchDog-Setup.exe", bytes.Length,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return UpdateDownloadResult.Verified(path, manifest);
    }

    private sealed class FakeChecker(
        Func<IProgress<double>?, CancellationToken, Task<UpdateDownloadResult>> download) : IUpdateChecker
    {
        public Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<UpdateDownloadResult> DownloadVerifiedInstallerAsync(
            UpdateInfo update, IProgress<double>? progress = null, CancellationToken ct = default) =>
            download(progress, ct);
    }
}
