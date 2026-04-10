using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Updates;

public sealed class GitHubUpdateChecker : IUpdateChecker
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubUpdateChecker> _logger;
    private readonly Func<Version?> _versionProvider;

    private const string ReleasesApiUrl = "https://api.github.com/repos/thrtn70/WatchDog/releases/latest";
    private const string InstallerAssetSuffix = "-Setup.exe";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

    private static readonly string[] AllowedHosts =
    [
        "github.com",
        "objects.githubusercontent.com",
    ];

    public GitHubUpdateChecker(HttpClient http, ILogger<GitHubUpdateChecker> logger)
        : this(http, logger, GetCurrentVersion)
    {
    }

    internal GitHubUpdateChecker(HttpClient http, ILogger<GitHubUpdateChecker> logger, Func<Version?> versionProvider)
    {
        _http = http;
        _logger = logger;
        _versionProvider = versionProvider;
    }

    public async Task<UpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
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

            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("WatchDog", currentVersion.ToString()));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            if ((int)response.StatusCode == 403)
            {
                _logger.LogWarning("GitHub API rate limited — skipping update check");
                return null;
            }

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
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

            // Find the Setup.exe asset with a validated HTTPS URL on an allowed host
            string? downloadUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    if (name is not null && name.EndsWith(InstallerAssetSuffix, StringComparison.OrdinalIgnoreCase))
                    {
                        var url = asset.GetProperty("browser_download_url").GetString();
                        if (IsAllowedDownloadUrl(url))
                            downloadUrl = url;
                        else
                            _logger.LogWarning("Rejected installer URL from untrusted host: {Host}",
                                url is not null && Uri.TryCreate(url, UriKind.Absolute, out var rejected) ? rejected.Host : "null");
                        break;
                    }
                }
            }

            return new UpdateInfo(
                CurrentVersion: currentVersion.ToString(),
                LatestVersion: latestVersion.ToString(),
                DownloadUrl: downloadUrl ?? "",
                ReleaseNotesUrl: htmlUrl,
                IsUpdateAvailable: isNewer && !string.IsNullOrEmpty(downloadUrl));
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Update check timed out or cancelled");
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Failed to check for updates (network error)");
            return null;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse GitHub release response");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during update check");
            return null;
        }
    }

    public async Task<string?> DownloadInstallerAsync(
        string downloadUrl,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        // Defense-in-depth: re-validate URL before downloading
        if (!IsAllowedDownloadUrl(downloadUrl))
        {
            _logger.LogWarning("Blocked download from untrusted URL");
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DownloadTimeout);

        try
        {
            // Use a unique temp file to avoid TOCTOU races
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

                if (totalBytes > 0)
                    progress?.Report((double)bytesRead / totalBytes);
            }

            // Verify the download produced a non-empty file
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
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Installer download timed out or cancelled");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download installer");
            return null;
        }
    }

    internal static Version? GetCurrentVersion()
    {
        var infoVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        // InformationalVersion may have +commitHash suffix; strip it
        var clean = infoVersion?.Split('+')[0];
        return Version.TryParse(clean, out var v) ? v : null;
    }

    private static bool IsAllowedDownloadUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return AllowedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
    }
}
