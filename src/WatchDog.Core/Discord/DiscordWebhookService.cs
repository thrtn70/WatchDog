using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Events;
using WatchDog.Core.Settings;

namespace WatchDog.Core.Discord;

public sealed class DiscordWebhookService : IDiscordWebhookService
{
    private readonly HttpClient _httpClient;
    private readonly ISettingsService _settingsService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<DiscordWebhookService> _logger;

    private const int ChunkSize = 81920; // 80KB chunks for progress reporting
    private const int AccentColor = 0x89B4FA; // WatchDog embed accent (light blue)

    public DiscordWebhookService(
        HttpClient httpClient,
        ISettingsService settingsService,
        IEventBus eventBus,
        ILogger<DiscordWebhookService> logger)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _eventBus = eventBus;
        _logger = logger;
    }

    public async Task<DiscordUploadResult> UploadClipAsync(
        string filePath,
        ClipMetadata metadata,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var settings = _settingsService.Load().Discord;

        if (string.IsNullOrWhiteSpace(settings.WebhookUrl))
            return new DiscordUploadResult(false, "No Discord webhook URL configured.");

        // SSRF prevention: only allow HTTPS Discord webhook hosts
        if (!Uri.TryCreate(settings.WebhookUrl, UriKind.Absolute, out var webhookUri) ||
            webhookUri.Scheme != Uri.UriSchemeHttps ||
            (webhookUri.Host != "discord.com" && webhookUri.Host != "discordapp.com"))
        {
            return new DiscordUploadResult(false, "Invalid or non-Discord webhook URL.");
        }

        if (!File.Exists(filePath))
            return new DiscordUploadResult(false, $"Clip file not found: {Path.GetFileName(filePath)}");

        _eventBus.Publish(new DiscordUploadStartedEvent(filePath, metadata.GameName));
        _logger.LogInformation("Uploading clip to Discord: {File}", Path.GetFileName(filePath));

        try
        {
            using var content = new MultipartFormDataContent();

            // File attachment with progress tracking — ProgressStreamContent takes
            // ownership of the stream and disposes it via MultipartFormDataContent.
            var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var progressContent = new ProgressStreamContent(fileStream, ChunkSize, progress);
            progressContent.Headers.ContentType = new MediaTypeHeaderValue("video/mp4");
            content.Add(progressContent, "file", metadata.FileName);

            // Discord embed payload
            if (settings.IncludeEmbed)
            {
                var payload = BuildPayloadJson(settings, metadata);
                content.Add(new StringContent(payload, Encoding.UTF8, "application/json"), "payload_json");
            }
            else
            {
                var message = ApplyTemplate(settings.MessageTemplate, metadata);
                var simplePayload = JsonSerializer.Serialize(new
                {
                    username = settings.Username,
                    content = message
                });
                content.Add(new StringContent(simplePayload, Encoding.UTF8, "application/json"), "payload_json");
            }

            var response = await _httpClient.PostAsync(settings.WebhookUrl, content, ct);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Clip uploaded to Discord: {File}", metadata.FileName);
                _eventBus.Publish(new DiscordUploadCompletedEvent(filePath, true));
                return new DiscordUploadResult(true);
            }

            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var errorMsg = $"Discord returned {(int)response.StatusCode}: {TruncateError(errorBody)}";
            _logger.LogWarning("Discord upload failed: {Error}", errorMsg);
            _eventBus.Publish(new DiscordUploadCompletedEvent(filePath, false, errorMsg));
            return new DiscordUploadResult(false, errorMsg);
        }
        catch (OperationCanceledException)
        {
            _eventBus.Publish(new DiscordUploadCompletedEvent(filePath, false, "Upload cancelled"));
            throw;
        }
        catch (Exception ex)
        {
            var msg = $"Upload failed: {ex.Message}";
            _logger.LogError(ex, "Discord upload error");
            _eventBus.Publish(new DiscordUploadCompletedEvent(filePath, false, msg));
            return new DiscordUploadResult(false, msg);
        }
    }

    public async Task<bool> ValidateWebhookUrlAsync(string webhookUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl)) return false;
        if (!Uri.TryCreate(webhookUrl, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps) return false;
        if (uri.Host != "discord.com" && uri.Host != "discordapp.com") return false;

        try
        {
            var response = await _httpClient.GetAsync(webhookUrl, ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildPayloadJson(DiscordSettings settings, ClipMetadata metadata)
    {
        var title = ApplyTemplate(settings.MessageTemplate, metadata);
        var fileSizeMb = metadata.FileSizeBytes / (1024.0 * 1024.0);
        var footer = $"{fileSizeMb:F1} MB | {metadata.Duration:m\\:ss}";

        var payload = new
        {
            username = settings.Username,
            embeds = new[]
            {
                new
                {
                    title,
                    color = AccentColor,
                    timestamp = metadata.CreatedAt.UtcDateTime.ToString("o"),
                    footer = new { text = footer },
                    author = new { name = settings.Username }
                }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private static string ApplyTemplate(string template, ClipMetadata metadata)
    {
        return template
            .Replace("{GameName}", metadata.GameName ?? "Unknown")
            .Replace("{HighlightType}", metadata.HighlightType?.ToString() ?? "Clip")
            .Replace("{Duration}", metadata.Duration.ToString(@"m\:ss"))
            .Replace("{FileName}", metadata.FileName);
    }

    private static string TruncateError(string error) =>
        error.Length > 200 ? error[..200] + "..." : error;

    /// <summary>
    /// StreamContent wrapper that reports upload progress in chunks.
    /// </summary>
    private sealed class ProgressStreamContent : HttpContent
    {
        private readonly Stream _stream;
        private readonly int _chunkSize;
        private readonly IProgress<double>? _progress;

        public ProgressStreamContent(Stream stream, int chunkSize, IProgress<double>? progress)
        {
            _stream = stream;
            _chunkSize = chunkSize;
            _progress = progress;
            Headers.ContentLength = stream.Length;
        }

        protected override async Task SerializeToStreamAsync(Stream targetStream, TransportContext? context)
        {
            var buffer = new byte[_chunkSize];
            var totalBytes = _stream.Length;
            long bytesWritten = 0;

            int bytesRead;
            while ((bytesRead = await _stream.ReadAsync(buffer)) > 0)
            {
                await targetStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                bytesWritten += bytesRead;
                _progress?.Report(totalBytes > 0 ? (double)bytesWritten / totalBytes * 100.0 : 0);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _stream.Length;
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _stream.Dispose();
            base.Dispose(disposing);
        }
    }
}
