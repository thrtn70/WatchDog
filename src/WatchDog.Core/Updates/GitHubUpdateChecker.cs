using System.Net.Http.Headers;
using System.Security.Cryptography;
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
    private readonly UpdateManifestVerifier _verifier;
    private readonly string _downloadDirectory;

    private const string StableApiUrl = "https://api.github.com/repos/thrtn70/WatchDog/releases/latest";
    private const string ReleasesListUrl = "https://api.github.com/repos/thrtn70/WatchDog/releases?per_page=10";
    private const string InstallerAssetSuffix = "-Setup.exe";
    private const string ManifestAssetSuffix = InstallerAssetSuffix + ".manifest.json";
    private const string SignatureAssetSuffix = InstallerAssetSuffix + ".manifest.sig";
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(5);

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
        Func<Version?> versionProvider,
        UpdateManifestVerifier? verifier = null,
        string? downloadDirectory = null)
    {
        _http = http;
        _logger = logger;
        _settings = settings;
        _channelProvider = channelProvider;
        _versionProvider = versionProvider;
        _verifier = verifier ?? UpdateManifestVerifier.CreatePinned();
        _downloadDirectory = downloadDirectory ?? Path.Combine(Path.GetTempPath(), "WatchDog-Update");
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
                IsUpdateAvailable: isNewer && !string.IsNullOrEmpty(downloadUrl),
                ReleaseTag: tagName,
                ManifestUrl: FindSignedManifestAssetUrl(root, ManifestAssetSuffix),
                SignatureUrl: FindSignedManifestAssetUrl(root, SignatureAssetSuffix));
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
                            DisplayMessage: $"WatchDog v{stableVersion} (stable) is available.",
                            ReleaseTag: tagName,
                            ManifestUrl: FindSignedManifestAssetUrl(release, ManifestAssetSuffix),
                            SignatureUrl: FindSignedManifestAssetUrl(release, SignatureAssetSuffix));
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
                        DisplayMessage: hasNewBuild ? "New CI build available." : null,
                        ReleaseTag: tagName,
                        ManifestUrl: FindSignedManifestAssetUrl(release, ManifestAssetSuffix),
                        SignatureUrl: FindSignedManifestAssetUrl(release, SignatureAssetSuffix));
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

    public async Task<UpdateDownloadResult> DownloadVerifiedInstallerAsync(
        UpdateInfo update,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!IsAllowedDownloadUrl(update.DownloadUrl))
        {
            _logger.LogWarning("Blocked download from untrusted URL");
            return UpdateDownloadResult.Refused(UpdateVerificationFailure.DownloadFailed);
        }
        if (string.IsNullOrEmpty(update.ReleaseTag) ||
            !IsAllowedDownloadUrl(update.ManifestUrl) || !IsAllowedDownloadUrl(update.SignatureUrl))
        {
            _logger.LogWarning("Refusing update {Tag}: release has no signed update manifest", update.ReleaseTag);
            return UpdateDownloadResult.Refused(UpdateVerificationFailure.MissingManifest);
        }
        if (_versionProvider() is not { } currentVersion)
        {
            _logger.LogWarning("Refusing update: could not determine current app version");
            return UpdateDownloadResult.Refused(UpdateVerificationFailure.DownloadFailed);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DownloadTimeout);
        string? installerPath = null;
        var verified = false;

        try
        {
            // Authenticate the small manifest first so an unverifiable release never downloads the installer.
            var manifestBytes = await DownloadSmallAssetAsync(update.ManifestUrl!, UpdateManifestVerifier.MaxManifestBytes, cts.Token);
            var signature = await DownloadSmallAssetAsync(update.SignatureUrl!, UpdateManifestVerifier.MaxSignatureBytes, cts.Token);
            var failure = _verifier.Verify(manifestBytes, signature, update.ReleaseTag, currentVersion, out var manifest);
            if (failure != UpdateVerificationFailure.None || manifest is null)
            {
                _logger.LogWarning("Refusing update {Tag}: manifest verification failed ({Failure})", update.ReleaseTag, failure);
                return UpdateDownloadResult.Refused(
                    failure == UpdateVerificationFailure.None ? UpdateVerificationFailure.ManifestInvalid : failure);
            }

            Directory.CreateDirectory(_downloadDirectory);
            installerPath = Path.Combine(_downloadDirectory, $"WatchDog-Setup-{Guid.NewGuid():N}.exe");
            if (!await DownloadMatchingInstallerAsync(update.DownloadUrl, installerPath, manifest, progress, cts.Token))
            {
                _logger.LogWarning("Refusing update {Tag}: installer bytes do not match the signed manifest", update.ReleaseTag);
                return UpdateDownloadResult.Refused(UpdateVerificationFailure.InstallerMismatch);
            }

            verified = true;
            progress?.Report(1.0);
            _logger.LogInformation("Verified installer for {Tag}: {Path} ({Size:F1} MB)",
                update.ReleaseTag, installerPath, manifest.Length / (1024.0 * 1024.0));
            return UpdateDownloadResult.Verified(installerPath, manifest);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Installer download timed out or cancelled");
            return UpdateDownloadResult.Refused(UpdateVerificationFailure.DownloadFailed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to download update");
            return UpdateDownloadResult.Refused(UpdateVerificationFailure.DownloadFailed);
        }
        finally
        {
            if (!verified && installerPath is not null)
            {
                try { File.Delete(installerPath); } catch { /* best-effort */ }
            }
        }
    }

    /// <summary>Reads at most <paramref name="maxBytes"/> + 1 bytes so the verifier can reject oversize assets.</summary>
    private async Task<byte[]> DownloadSmallAssetAsync(string url, int maxBytes, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);

        var buffer = new byte[maxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), ct)) > 0)
            total += read;
        return buffer[..total];
    }

    /// <summary>
    /// Streams the installer to <paramref name="path"/> while hashing it; stops as soon as it exceeds the
    /// signed length. True only when the length and SHA-256 equal the manifest's.
    /// </summary>
    private async Task<bool> DownloadMatchingInstallerAsync(
        string url, string path, UpdateManifest manifest, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        var bytesRead = 0L;
        int read;
        while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
        {
            bytesRead += read;
            if (bytesRead > manifest.Length)
                return false;

            hash.AppendData(buffer, 0, read);
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            progress?.Report((double)bytesRead / manifest.Length);
        }

        return bytesRead == manifest.Length &&
            string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), manifest.Sha256, StringComparison.Ordinal);
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
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
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

    private string? FindSignedManifestAssetUrl(JsonElement release, string suffix)
    {
        if (!release.TryGetProperty("assets", out var assets)) return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameEl) ||
                nameEl.GetString()?.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) != true)
            {
                continue;
            }

            var url = asset.TryGetProperty("browser_download_url", out var urlEl) ? urlEl.GetString() : null;
            if (IsAllowedDownloadUrl(url)) return url;

            _logger.LogWarning("Rejected update manifest asset from untrusted host");
            return null;
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
