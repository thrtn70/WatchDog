using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.Updates;

namespace WatchDog.Core.Tests.Updates;

public sealed class GitHubUpdateCheckerTests
{
    private static readonly Version TestVersion = new(1, 2, 0);

    private static GitHubUpdateChecker CreateChecker(HttpMessageHandler handler)
        => new(new HttpClient(handler), NullLogger<GitHubUpdateChecker>.Instance, () => TestVersion);

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
