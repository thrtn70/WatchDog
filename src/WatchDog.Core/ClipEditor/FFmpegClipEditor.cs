using System.Diagnostics;
using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Runtime;

namespace WatchDog.Core.ClipEditor;

public sealed partial class FFmpegClipEditor : IClipEditor
{
    private readonly ILogger<FFmpegClipEditor> _logger;
    private readonly IClipEditorProcessRunner _processRunner;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;

    public FFmpegClipEditor(ILogger<FFmpegClipEditor> logger, string? ffmpegPath = null)
        : this(logger, new ReapingClipEditorProcessRunner(), ffmpegPath, ffprobePath: null)
    {
    }

    internal FFmpegClipEditor(
        ILogger<FFmpegClipEditor> logger,
        IClipEditorProcessRunner processRunner,
        string? ffmpegPath = null,
        string? ffprobePath = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _ffmpegPath = ffmpegPath ?? FFmpegRuntime.FfmpegPath;
        _ffprobePath = ffprobePath ?? FFmpegRuntime.FfprobePath;
    }

    /// <summary>
    /// Lossless trim using stream copy — instant, no re-encoding.
    /// </summary>
    public async Task<string> TrimAsync(string inputPath, TimeSpan start, TimeSpan end, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        if (start < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(start), "Trim start must not be negative.");
        if (end <= start)
            throw new ArgumentOutOfRangeException(nameof(end), "Trim end must be after trim start.");

        var fullInputPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullInputPath))
            throw new FileNotFoundException("Input clip was not found.", fullInputPath);

        var dir = Path.GetDirectoryName(fullInputPath)
            ?? throw new ArgumentException("inputPath must not be a root path.", nameof(inputPath));
        var name = Path.GetFileNameWithoutExtension(fullInputPath);
        var ext = Path.GetExtension(fullInputPath);
        var candidatePath = Path.Combine(
            dir,
            $".{name}.watchdog-exporting-{Guid.NewGuid():N}{ext}");

        _logger.LogInformation("Trimming clip: {Input} [{Start} -> {End}]", fullInputPath, start, end);

        string? publishedPath = null;
        Exception? failure = null;
        try
        {
            await RunProcessAsync(
                _ffmpegPath,
                BuildTrimArguments(fullInputPath, candidatePath, start, end),
                ct);
            ValidateCandidateFile(candidatePath);

            var probeJson = await RunProcessAsync(
                _ffprobePath,
                BuildValidationProbeArguments(candidatePath),
                ct);
            ValidateProbeJson(probeJson);

            publishedPath = PublishCandidate(candidatePath, dir, name, ext);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        try
        {
            if (File.Exists(candidatePath))
                File.Delete(candidatePath);
        }
        catch (Exception cleanupFailure)
        {
            failure = failure is null
                ? cleanupFailure
                : new AggregateException(
                    "Clip export failed and its unpublished candidate could not be removed.",
                    failure,
                    cleanupFailure);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();

        _logger.LogInformation("Trim complete: {Output}", publishedPath);
        return publishedPath!;
    }

    /// <summary>
    /// Extract a single frame as JPEG thumbnail.
    /// </summary>
    public async Task<string> GenerateThumbnailAsync(string inputPath, TimeSpan timestamp, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(inputPath)
            ?? throw new ArgumentException("inputPath must not be a root path.", nameof(inputPath));
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
        var result = await RunProcessAsync(_ffprobePath,
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
        var dir = Path.GetDirectoryName(inputPath)
            ?? throw new ArgumentException("inputPath must not be a root path.", nameof(inputPath));
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
        return await _processRunner.RunAsync(executable, args, ct);
    }

    internal static string[] BuildTrimArguments(
        string inputPath,
        string candidatePath,
        TimeSpan start,
        TimeSpan end) =>
    [
        "-n",
        "-ss", FormatTime(start),
        "-to", FormatTime(end),
        "-i", inputPath,
        "-c", "copy",
        "-avoid_negative_ts", "make_zero",
        candidatePath,
    ];

    internal static string[] BuildValidationProbeArguments(string candidatePath) =>
    [
        "-v", "error",
        "-show_entries", "format=duration:stream=codec_type",
        "-of", "json",
        candidatePath,
    ];

    internal static void ValidateProbeJson(string probeJson)
    {
        try
        {
            using var document = JsonDocument.Parse(probeJson);
            if (!document.RootElement.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array ||
                !streams.EnumerateArray().Any(stream =>
                    stream.TryGetProperty("codec_type", out var codecType) &&
                    string.Equals(codecType.GetString(), "video", StringComparison.Ordinal)))
            {
                throw new InvalidDataException("FFprobe found no video stream in the exported clip.");
            }

            if (!document.RootElement.TryGetProperty("format", out var format) ||
                !format.TryGetProperty("duration", out var durationElement) ||
                !TryReadPositiveDuration(durationElement, out _))
            {
                throw new InvalidDataException("FFprobe found no positive duration in the exported clip.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("FFprobe returned malformed JSON for the exported clip.", ex);
        }
    }

    private static bool TryReadPositiveDuration(JsonElement element, out double duration)
    {
        duration = 0;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out duration))
            return double.IsFinite(duration) && duration > 0;

        return element.ValueKind == JsonValueKind.String &&
            double.TryParse(
                element.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out duration) &&
            double.IsFinite(duration) &&
            duration > 0;
    }

    private static void ValidateCandidateFile(string candidatePath)
    {
        var candidate = new FileInfo(candidatePath);
        if (!candidate.Exists || candidate.Length <= 0)
            throw new InvalidDataException("FFmpeg trim produced no usable output file.");
    }

    private static string PublishCandidate(
        string candidatePath,
        string directory,
        string inputName,
        string extension)
    {
        for (var sequence = 1; ; sequence++)
        {
            var suffix = sequence == 1 ? string.Empty : $"_{sequence}";
            var destinationPath = Path.Combine(directory, $"{inputName}_trimmed{suffix}{extension}");
            try
            {
                File.Move(candidatePath, destinationPath, overwrite: false);
                return destinationPath;
            }
            catch (IOException) when (File.Exists(destinationPath))
            {
                // Another export already owns this name. The no-overwrite move is the
                // reservation and publication boundary, so retry with the next suffix.
            }
        }
    }

    private static string FormatTime(TimeSpan ts)
        => ts.ToString(@"hh\:mm\:ss\.fff");

}

internal interface IClipEditorProcessRunner
{
    Task<string> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct);
}

internal sealed class ReapingClipEditorProcessRunner : IClipEditorProcessRunner
{
    private static readonly TimeSpan DefaultTerminationTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _terminationTimeout;

    public ReapingClipEditorProcessRunner()
        : this(DefaultTerminationTimeout)
    {
    }

    internal ReapingClipEditorProcessRunner(TimeSpan terminationTimeout)
    {
        if (terminationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(terminationTimeout));
        _terminationTimeout = terminationTimeout;
    }

    public async Task<string> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process: {Path.GetFileName(executable)}");

        // Never cancel the pipe drains independently. On caller cancellation the process
        // is killed and reaped first, which closes both pipes and lets these tasks finish.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync(CancellationToken.None);
        var completion = Task.WhenAll(exitTask, stdoutTask, stderrTask);

        try
        {
            await completion.WaitAsync(ct);
        }
        catch (OperationCanceledException cancellation) when (ct.IsCancellationRequested)
        {
            var cleanupFailures = new List<Exception>();
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception killFailure)
            {
                cleanupFailures.Add(new IOException(
                    $"The canceled {Path.GetFileName(executable)} process could not be killed.",
                    killFailure));
            }

            try
            {
                await completion.WaitAsync(_terminationTimeout);
            }
            catch (Exception terminationFailure)
            {
                cleanupFailures.Add(new IOException(
                    $"The canceled {Path.GetFileName(executable)} process was not reaped within the cleanup timeout.",
                    terminationFailure));
                _ = completion.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            if (cleanupFailures.Count == 0)
                ExceptionDispatchInfo.Capture(cancellation).Throw();

            cleanupFailures.Insert(0, cancellation);
            throw new AggregateException(
                $"{Path.GetFileName(executable)} was canceled but process cleanup was incomplete.",
                cleanupFailures);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} exited with code {process.ExitCode}: {stderr.Trim()}");
        }

        return string.IsNullOrEmpty(stdout) ? stderr : stdout;
    }
}
