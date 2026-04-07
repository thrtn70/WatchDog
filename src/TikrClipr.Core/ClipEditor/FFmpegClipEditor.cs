using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TikrClipr.Core.ClipEditor;

public sealed partial class FFmpegClipEditor : IClipEditor
{
    private readonly ILogger<FFmpegClipEditor> _logger;
    private readonly string _ffmpegPath;

    public FFmpegClipEditor(ILogger<FFmpegClipEditor> logger, string? ffmpegPath = null)
    {
        _logger = logger;
        _ffmpegPath = ffmpegPath ?? FindFFmpeg();
    }

    /// <summary>
    /// Lossless trim using stream copy — instant, no re-encoding.
    /// </summary>
    public async Task<string> TrimAsync(string inputPath, TimeSpan start, TimeSpan end, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(inputPath)!;
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        var outputPath = Path.Combine(dir, $"{name}_trimmed{ext}");

        var args = $"-ss {FormatTime(start)} -to {FormatTime(end)} -i \"{inputPath}\" -c copy -avoid_negative_ts make_zero \"{outputPath}\"";

        _logger.LogInformation("Trimming clip: {Input} [{Start} -> {End}]", inputPath, start, end);

        var result = await RunFFmpegAsync(args, ct);

        if (!File.Exists(outputPath))
            throw new InvalidOperationException($"FFmpeg trim failed. Stderr: {result}");

        _logger.LogInformation("Trim complete: {Output}", outputPath);
        return outputPath;
    }

    /// <summary>
    /// Extract a single frame as JPEG thumbnail.
    /// </summary>
    public async Task<string> GenerateThumbnailAsync(string inputPath, TimeSpan timestamp, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(inputPath)!;
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var outputPath = Path.Combine(dir, $"{name}_thumb.jpg");

        var args = $"-ss {FormatTime(timestamp)} -i \"{inputPath}\" -vframes 1 -q:v 2 \"{outputPath}\"";

        await RunFFmpegAsync(args, ct);

        if (!File.Exists(outputPath))
            throw new InvalidOperationException("FFmpeg thumbnail generation failed.");

        return outputPath;
    }

    /// <summary>
    /// Get video duration using ffprobe.
    /// </summary>
    public async Task<TimeSpan> GetDurationAsync(string inputPath, CancellationToken ct = default)
    {
        var ffprobePath = Path.Combine(Path.GetDirectoryName(_ffmpegPath)!, "ffprobe.exe");
        if (!File.Exists(ffprobePath))
            ffprobePath = "ffprobe";

        var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputPath}\"";

        var result = await RunProcessAsync(ffprobePath, args, ct);
        var trimmed = result.Trim();

        if (double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return TimeSpan.FromSeconds(seconds);

        _logger.LogWarning("Could not parse duration from ffprobe output: {Output}", trimmed);
        return TimeSpan.Zero;
    }

    /// <summary>
    /// Extract multiple evenly-spaced frames for a visual timeline strip.
    /// </summary>
    public async Task<IReadOnlyList<string>> GenerateThumbnailStripAsync(
        string inputPath, int frameCount, int thumbnailWidth, CancellationToken ct = default)
    {
        var duration = await GetDurationAsync(inputPath, ct);
        if (duration <= TimeSpan.Zero || frameCount <= 0)
            return Array.Empty<string>();

        var interval = duration.TotalSeconds / frameCount;
        var dir = Path.GetDirectoryName(inputPath)!;
        var name = Path.GetFileNameWithoutExtension(inputPath);
        var stripDir = Path.Combine(dir, ".thumbnails");
        Directory.CreateDirectory(stripDir);

        var paths = new List<string>();
        for (var i = 0; i < frameCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var time = TimeSpan.FromSeconds(i * interval);
            var outputPath = Path.Combine(stripDir, $"{name}_strip_{i:D3}.jpg");

            if (!File.Exists(outputPath))
            {
                var args = $"-ss {FormatTime(time)} -i \"{inputPath}\" -vframes 1 -vf scale={thumbnailWidth}:-1 -q:v 4 \"{outputPath}\"";
                await RunFFmpegAsync(args, ct);
            }

            if (File.Exists(outputPath))
                paths.Add(outputPath);
        }

        _logger.LogInformation("Generated {Count} thumbnail strip frames for {File}", paths.Count, inputPath);
        return paths;
    }

    private async Task<string> RunFFmpegAsync(string args, CancellationToken ct)
    {
        // -y to overwrite output without prompting
        return await RunProcessAsync(_ffmpegPath, $"-y {args}", ct);
    }

    private async Task<string> RunProcessAsync(string executable, string args, CancellationToken ct)
    {
        _logger.LogDebug("Running: {Exe} {Args}", executable, args);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };

        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            _logger.LogWarning("Process exited with code {Code}. Stderr: {Stderr}", process.ExitCode, stderr);

        return string.IsNullOrEmpty(stdout) ? stderr : stdout;
    }

    private static string FormatTime(TimeSpan ts)
        => ts.ToString(@"hh\:mm\:ss\.fff");

    private static string FindFFmpeg()
    {
        // Check alongside the application first
        var appDir = AppContext.BaseDirectory;
        var local = Path.Combine(appDir, "ffmpeg.exe");
        if (File.Exists(local))
            return local;

        // Fall back to PATH
        return "ffmpeg";
    }
}
