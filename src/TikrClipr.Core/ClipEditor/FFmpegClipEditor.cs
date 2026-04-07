using System.Diagnostics;
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

        _logger.LogInformation("Trimming clip: {Input} [{Start} -> {End}]", inputPath, start, end);

        await RunFFmpegAsync(["-ss", FormatTime(start), "-to", FormatTime(end), "-i", inputPath,
            "-c", "copy", "-avoid_negative_ts", "make_zero", outputPath], ct);

        if (!File.Exists(outputPath))
            throw new InvalidOperationException($"FFmpeg trim failed for: {inputPath}");

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

        await RunFFmpegAsync(["-ss", FormatTime(timestamp), "-i", inputPath,
            "-vframes", "1", "-q:v", "2", outputPath], ct);

        if (!File.Exists(outputPath))
            throw new InvalidOperationException("FFmpeg thumbnail generation failed.");

        return outputPath;
    }

    /// <summary>
    /// Get video duration using ffprobe.
    /// </summary>
    public async Task<TimeSpan> GetDurationAsync(string inputPath, CancellationToken ct = default)
    {
        var ffprobeDir = Path.GetDirectoryName(_ffmpegPath);
        var ffprobePath = ffprobeDir is not null
            ? Path.Combine(ffprobeDir, "ffprobe.exe")
            : null;
        if (ffprobePath is null || !File.Exists(ffprobePath))
            ffprobePath = "ffprobe";

        var result = await RunProcessAsync(ffprobePath,
            ["-v", "error", "-show_entries", "format=duration",
             "-of", "default=noprint_wrappers=1:nokey=1", inputPath], ct);
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
            var outputPath = Path.Combine(stripDir, $"{name}_strip_{i:D3}_w{thumbnailWidth}.jpg");

            if (!File.Exists(outputPath))
            {
                await RunFFmpegAsync(["-ss", FormatTime(time), "-i", inputPath,
                    "-vframes", "1", "-vf", $"scale={thumbnailWidth}:-1",
                    "-q:v", "4", outputPath], ct);
            }

            if (File.Exists(outputPath))
                paths.Add(outputPath);
        }

        _logger.LogInformation("Generated {Count} thumbnail strip frames for {File}", paths.Count, inputPath);
        return paths;
    }

    private Task<string> RunFFmpegAsync(string[] args, CancellationToken ct)
    {
        // Prepend -y to overwrite output without prompting
        var fullArgs = new string[args.Length + 1];
        fullArgs[0] = "-y";
        args.CopyTo(fullArgs, 1);
        return RunProcessAsync(_ffmpegPath, fullArgs, ct);
    }

    private async Task<string> RunProcessAsync(string executable, string[] args, CancellationToken ct)
    {
        _logger.LogDebug("Running: {Exe} {Args}", executable, string.Join(' ', args));

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        // Drain stdout and stderr concurrently to prevent pipe deadlock —
        // FFmpeg writes heavily to stderr; sequential reads can deadlock if the pipe fills.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask);
        await process.WaitForExitAsync(ct);

        var stdout = stdoutTask.Result;
        var stderr = stderrTask.Result;

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Process exited with code {Code}. Stderr: {Stderr}", process.ExitCode, stderr);
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} exited with code {process.ExitCode}. Stderr: {stderr}");
        }

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
