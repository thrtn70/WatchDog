using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Settings;

namespace WatchDog.Core.Updates;

public sealed class GitHubUpdateChecker : IUpdateChecker
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubUpdateChecker> _logger;
    private readonly ISettingsService _settings;
    private readonly Func<BuildChannel> _channelProvider;
    private readonly Func<Version?> _versionProvider;

    private const string StableApiUrl = "https://api.github.com/repos/thrtn70/WatchDog/releases/latest";
    private const string ReleasesListUrl = "https://api.github.com/repos/thrtn70/WatchDog/releases?per_page=10";
    private const string InstallerAssetSuffix = "-Setup.exe";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);
    private const long MaxInstallerBytes = 512L * 1024 * 1024;

    private static readonly string[] AllowedHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
    ];

    public GitHubUpdateChecker(HttpClient http, ILogger<GitHubUpdateChecker> logger, ISettingsService settings)
        : this(http, logger, settings, BuildInfo.GetChannel, BuildInfo.GetParsedBaseVersion)
    {
    }

    internal GitHubUpdateChecker(
        HttpClient http,
        ILogger<GitHubUpdateChecker> logger,
        ISettingsService settings,
        Func<BuildChannel> channelProvider,
        Func<Version?> versionProvider)
    {
        _http = http;
        _logger = logger;
        _settings = settings;
        _channelProvider = channelProvider;
        _versionProvider = versionProvider;
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        var channel = _channelProvider();
        return channel == BuildChannel.PreRelease
            ? await CheckPreReleaseUpdateAsync(ct)
            : await CheckStableUpdateAsync(ct);
    }

    // ── Stable Update Path ─────────────────────────────────────

    private async Task<UpdateInfo?> CheckStableUpdateAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CheckTimeout);

        try
        {
            var currentVersion = _versionProvider();
            if (currentVersion is null)
            {
                _logger.LogWarning("Could not determine current app version");
                return null;
            }

            using var doc = await FetchJsonAsync(StableApiUrl, currentVersion, cts.Token);
            if (doc is null) return null;

            var root = doc.RootElement;
            var tagName = root.GetProperty("tag_name").GetString();
            if (tagName is null) return null;

            var versionString = tagName.TrimStart('v');
            if (!Version.TryParse(versionString, out var latestVersion))
            {
                _logger.LogDebug("Could not parse version from tag: {Tag}", tagName);
                return null;
            }

            var isNewer = latestVersion > currentVersion;
            var htmlUrl = root.GetProperty("html_url").GetString() ?? "";
            var downloadUrl = FindInstallerAssetUrl(root);

            return new UpdateInfo(
                CurrentVersion: currentVersion.ToString(),
                LatestVersion: latestVersion.ToString(),
                DownloadUrl: downloadUrl ?? "",
                ReleaseNotesUrl: htmlUrl,
                IsUpdateAvailable: isNewer && !string.IsNullOrEmpty(downloadUrl));
        }
        catch (OperationCanceledException) { _logger.LogDebug("Update check timed out or cancelled"); return null; }
        catch (HttpRequestException ex) { _logger.LogWarning(ex, "Failed to check for updates (network error)"); return null; }
        catch (JsonException ex) { _logger.LogWarning(ex, "Failed to parse GitHub release response"); return null; }
        catch (Exception ex) { _logger.LogWarning(ex, "Unexpected error during update check"); return null; }
    }

    // ── Pre-Release Update Path ────────────────────────────────

    private async Task<UpdateInfo?> CheckPreReleaseUpdateAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(CheckTimeout);

        try
        {
            var currentVersion = _versionProvider();
            var baseVersionStr = BuildInfo.GetBaseVersion();
            if (currentVersion is null || baseVersionStr is null)
            {
                _logger.LogWarning("Could not determine current app version for pre-release check");
                return null;
            }

            using var doc = await FetchJsonAsync(ReleasesListUrl, currentVersion, cts.Token);
            if (doc is null) return null;

            var currentVersionStr = BuildInfo.GetVersionString() ?? currentVersion.ToString();
            var storedTimestamp = _settings.Load().Update.LastSeenAssetTimestamp;

            // Walk releases to find: (a) our pre-release tag, (b) any newer stable release
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                var tagName = release.GetProperty("tag_name").GetString();
                if (tagName is null) continue;

                var versionString = tagName.TrimStart('v');
                var isPreRelease = release.TryGetProperty("prerelease", out var preProp) && preProp.GetBoolean();

                // Case 1: A stable release with a higher version supersedes our pre-release
                if (!isPreRelease && Version.TryParse(versionString, out var stableVersion) && stableVersion > currentVersion)
                {
                    var htmlUrl = release.GetProperty("html_url").GetString() ?? "";
                    var downloadUrl = FindInstallerAssetUrl(release);
                    if (!string.IsNullOrEmpty(downloadUrl))
                    {
                        _logger.LogInformation("Stable release v{Version} supersedes pre-release", stableVersion);
                        return new UpdateInfo(
                            CurrentVersion: currentVersionStr,
                            LatestVersion: stableVersion.ToString(),
                            DownloadUrl: downloadUrl,
                            ReleaseNotesUrl: htmlUrl,
                            IsUpdateAvailable: true,
                            DisplayMessage: $"WatchDog v{stableVersion} (stable) is available.");
                    }
                }

                // Case 2: Our pre-release tag — check if asset was updated
                if (versionString.StartsWith(baseVersionStr, StringComparison.OrdinalIgnoreCase) && isPreRelease)
                {
                    var (assetUrl, assetUpdatedAt) = FindInstallerAssetWithTimestamp(release);
                    if (assetUrl is null) continue;

                    var htmlUrl = release.GetProperty("html_url").GetString() ?? "";
                    var hasNewBuild = !string.IsNullOrEmpty(storedTimestamp)
                        && DateTimeOffset.TryParse(assetUpdatedAt, out var assetDt)
                        && DateTimeOffset.TryParse(storedTimestamp, out var storedDt)
                        && assetDt > storedDt;

                    // Persist current timestamp: on first run (silent seed) or when a new build is found
                    // (so the next check after install won't re-trigger the same notification)
                    if (assetUpdatedAt is not null && (string.IsNullOrEmpty(storedTimestamp) || hasNewBuild))
                    {
                        PersistAssetTimestamp(assetUpdatedAt);
                    }

                    var parsedTimestamp = DateTimeOffset.TryParse(assetUpdatedAt, out var dt) ? dt : (DateTimeOffset?)null;

                    return new UpdateInfo(
                        CurrentVersion: currentVersionStr,
                        LatestVersion: tagName,
                        DownloadUrl: assetUrl,
                        ReleaseNotesUrl: htmlUrl,
                        IsUpdateAvailable: hasNewBuild,
                        AssetUpdatedAt: parsedTimestamp,
                        DisplayMessage: hasNewBuild ? "New CI build available." : null);
                }
            }

            _logger.LogDebug("No matching release found for pre-release base version {Base}", baseVersionStr);
            return null;
        }
        catch (OperationCanceledException) { _logger.LogDebug("Pre-release update check timed out or cancelled"); return null; }
        catch (HttpRequestException ex) { _logger.LogWarning(ex, "Failed to check for pre-release updates (network error)"); return null; }
        catch (JsonException ex) { _logger.LogWarning(ex, "Failed to parse GitHub releases response"); return null; }
        catch (Exception ex) { _logger.LogWarning(ex, "Unexpected error during pre-release update check"); return null; }
    }

    // ── Download ───────────────────────────────────────────────

    public async Task<string?> DownloadInstallerAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsAllowedDownloadUrl(downloadUrl))
        {
            _logger.LogWarning("Blocked download from untrusted URL");
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DownloadTimeout);

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "WatchDog-Update");
            Directory.CreateDirectory(tempDir);
            var tempPath = Path.Combine(tempDir, $"WatchDog-Setup-{Guid.NewGuid():N}.exe");

            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var bytesRead = 0L;

            await using var contentStream = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

            var buffer = new byte[81920];
            int read;
            while ((read = await contentStream.ReadAsync(buffer, cts.Token)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                bytesRead += read;

                if (bytesRead > MaxInstallerBytes)
                {
                    _logger.LogWarning("Installer download exceeds maximum size ({Max} MB), aborting", MaxInstallerBytes / (1024 * 1024));
                    try { File.Delete(tempPath); } catch { /* best-effort */ }
                    return null;
                }

                if (totalBytes > 0)
                    progress?.Report((double)bytesRead / totalBytes);
            }

            if (bytesRead == 0)
            {
                _logger.LogWarning("Downloaded installer is empty");
                try { File.Delete(tempPath); } catch { /* best-effort */ }
                return null;
            }

            progress?.Report(1.0);
            _logger.LogInformation("Installer downloaded: {Path} ({Size:F1} MB)", tempPath, bytesRead / (1024.0 * 1024.0));
            return tempPath;
        }
        catch (OperationCanceledException) { _logger.LogDebug("Installer download timed out or cancelled"); return null; }
        catch (Exception ex) { _logger.LogWarning(ex, "Failed to download installer"); return null; }
    }

    /// <summary>Persist the asset timestamp so the next check can detect newer builds.</summary>
    private void PersistAssetTimestamp(string timestamp)
    {
        var current = _settings.Load();
        _settings.Save(current with
        {
            Update = current.Update with { LastSeenAssetTimestamp = timestamp }
        });
    }

    // ── Helpers ─────────────────────────────────────────────────

    private async Task<JsonDocument?> FetchJsonAsync(string url, Version currentVersion, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WatchDog", currentVersion.ToString()));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if ((int)response.StatusCode == 403)
        {
            _logger.LogWarning("GitHub API rate limited — skipping update check");
            return null;
        }

        response.EnsureSuccessStatusCode();

        const long maxResponseBytes = 5 * 1024 * 1024;
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength > maxResponseBytes)
        {
            _logger.LogWarning("GitHub API response too large ({Bytes} bytes), skipping", contentLength);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private string? FindInstallerAssetUrl(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameEl)) continue;
            var name = nameEl.GetString();
            if (name is not null && name.EndsWith(InstallerAssetSuffix, StringComparison.OrdinalIgnoreCase))
            {
                if (!asset.TryGetProperty("browser_download_url", out var urlEl)) continue;
                var url = urlEl.GetString();
                if (IsAllowedDownloadUrl(url)) return url;

                _logger.LogWarning("Rejected installer URL from untrusted host: {Host}",
                    url is not null && Uri.TryCreate(url, UriKind.Absolute, out var rejected) ? rejected.Host : "null");
                break;
            }
        }

        return null;
    }

    private (string? Url, string? UpdatedAt) FindInstallerAssetWithTimestamp(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)) return (null, null);

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameEl)) continue;
            var name = nameEl.GetString();
            if (name is not null && name.EndsWith(InstallerAssetSuffix, StringComparison.OrdinalIgnoreCase))
            {
                if (!asset.TryGetProperty("browser_download_url", out var urlEl)) continue;
                var url = urlEl.GetString();
                var updatedAt = asset.TryGetProperty("updated_at", out var ts) ? ts.GetString() : null;

                if (IsAllowedDownloadUrl(url)) return (url, updatedAt);

                _logger.LogWarning("Rejected installer URL from untrusted host");
                break;
            }
        }

        return (null, null);
    }

    private static bool IsAllowedDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return AllowedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
    }
}
