using WatchDog.Core.ClipEditor;

namespace WatchDog.Core.Discord;

public interface IDiscordWebhookService
{
    Task<DiscordUploadResult> UploadClipAsync(
        string filePath,
        ClipMetadata metadata,
        IProgress<double>? progress = null,
        CancellationToken ct = default);

    Task<bool> ValidateWebhookUrlAsync(string webhookUrl, CancellationToken ct = default);
}

public sealed record DiscordUploadResult(bool Success, string? ErrorMessage = null);
