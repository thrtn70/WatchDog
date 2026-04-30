using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Highlights.Audio;

/// <summary>
/// Downloads the YAMNet ONNX model on first launch if not already present.
/// Uses Essentia's CDN (University Pompeu Fabra, Barcelona) as the source.
/// </summary>
public static class AudioModelDownloader
{
    private const string ModelUrl =
        "https://essentia.upf.edu/models/audio-event-recognition/yamnet/audioset-yamnet-1.onnx";

    private static readonly string[] AllowedHosts = ["essentia.upf.edu"];

    private const long MaxModelBytes = 50L * 1024 * 1024;
    private const int BufferSize = 81920;

    private static readonly string? ExpectedSha256 = "9bc15ac91426e431527196ee6663de78dffcc7db53ac002d5afbda61429b456f";

    /// <summary>
    /// Ensures the ONNX model file exists at the given path.
    /// Downloads it from the Essentia CDN if missing. Returns true if the model
    /// is available (already existed or successfully downloaded), false if download failed.
    /// </summary>
    public static async Task<bool> EnsureModelAsync(
        string modelPath,
        ILogger logger,
        CancellationToken ct = default)
    {
        if (File.Exists(modelPath))
            return true;

        var directory = Path.GetDirectoryName(modelPath);
        if (directory is not null)
            Directory.CreateDirectory(directory);

        logger.LogInformation("ONNX model not found at {Path}, downloading from {Url}...",
            modelPath, ModelUrl);

        try
        {
            // Validate URL before downloading
            if (!IsAllowedUrl(ModelUrl))
            {
                logger.LogError("Model URL is not on the allowed host list");
                return false;
            }

            using var http = new HttpClient();
            http.Timeout = TimeSpan.FromMinutes(5);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("WatchDog/1.3.0");

            using var response = await http.GetAsync(ModelUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;

            if (totalBytes > MaxModelBytes)
            {
                logger.LogError("Model Content-Length ({Bytes} bytes) exceeds 50MB limit, aborting", totalBytes);
                return false;
            }

            // Write to a temp file first, then rename (atomic)
            var tempPath = modelPath + ".downloading";
            try
            {
                await using var contentStream = await response.Content.ReadAsStreamAsync(ct);
                await using var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write,
                    FileShare.None, BufferSize, useAsync: true);

                var buffer = new byte[BufferSize];
                var bytesRead = 0L;
                int read;

                while ((read = await contentStream.ReadAsync(buffer, ct)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
                    bytesRead += read;

                    // Reject suspiciously large files (model should be ~14MB)
                    if (bytesRead > MaxModelBytes)
                    {
                        logger.LogError("Model download exceeds 50MB limit, aborting");
                        try { File.Delete(tempPath); } catch { /* best-effort */ }
                        return false;
                    }
                }

                if (bytesRead == 0)
                {
                    logger.LogError("Downloaded model file is empty");
                    try { File.Delete(tempPath); } catch { /* best-effort */ }
                    return false;
                }

                logger.LogInformation("Model downloaded: {Size:F1} MB", bytesRead / (1024.0 * 1024.0));
            }
            catch
            {
                try { File.Delete(tempPath); } catch { /* best-effort */ }
                throw;
            }

            // Verify integrity if a known hash is pinned
            if (ExpectedSha256 is not null)
            {
                using var sha = System.Security.Cryptography.SHA256.Create();
                await using var verifyStream = File.OpenRead(tempPath);
                var hash = Convert.ToHexString(await sha.ComputeHashAsync(verifyStream, ct));
                if (!hash.Equals(ExpectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogError("Model hash mismatch: expected {Expected}, got {Actual}",
                        ExpectedSha256, hash);
                    try { File.Delete(tempPath); } catch { /* best-effort */ }
                    return false;
                }
            }
            else
            {
                // Log the hash on first download so it can be pinned
                using var sha = System.Security.Cryptography.SHA256.Create();
                await using var verifyStream = File.OpenRead(tempPath);
                var hash = Convert.ToHexString(await sha.ComputeHashAsync(verifyStream, ct));
                logger.LogInformation("Model SHA-256: {Hash} (pin this value in ExpectedSha256)", hash);
            }

            // Atomic rename
            File.Move(tempPath, modelPath, overwrite: true);
            logger.LogInformation("ONNX model saved to {Path}", modelPath);
            return true;
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("Model download cancelled");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to download ONNX model — AI highlights will be disabled");
            return false;
        }
    }

    private static bool IsAllowedUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        return AllowedHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase));
    }
}
