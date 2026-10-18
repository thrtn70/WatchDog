using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using WatchDog.Core.Runtime;

namespace WatchDog.Core.Recording;

internal interface IWebcamVideoOnlyFinalizer
{
    void Finalize(string intermediatePath, string finalPath);
}

internal interface IWebcamFinalizationProcessRunner
{
    string Run(string executable, IReadOnlyList<string> arguments);
}

internal sealed class FFmpegWebcamVideoOnlyFinalizer : IWebcamVideoOnlyFinalizer
{
    private readonly IWebcamFinalizationProcessRunner _processRunner;

    public FFmpegWebcamVideoOnlyFinalizer()
        : this(new BoundedWebcamFinalizationProcessRunner())
    {
    }

    internal FFmpegWebcamVideoOnlyFinalizer(IWebcamFinalizationProcessRunner processRunner)
    {
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    }

    public void Finalize(string intermediatePath, string finalPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(intermediatePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(finalPath);

        var fullIntermediatePath = Path.GetFullPath(intermediatePath);
        var fullFinalPath = Path.GetFullPath(finalPath);
        var finalDirectory = Path.GetDirectoryName(fullFinalPath)
            ?? throw new ArgumentException("The final webcam path must have a parent directory.", nameof(finalPath));
        var extension = Path.GetExtension(fullFinalPath);
        var candidatePath = Path.Combine(
            finalDirectory,
            $".{Path.GetFileNameWithoutExtension(fullFinalPath)}.watchdog-finalizing-{Guid.NewGuid():N}{extension}");

        Exception? failure = null;
        try
        {
            _processRunner.Run(
                FFmpegRuntime.FfmpegPath,
                BuildFfmpegArguments(fullIntermediatePath, candidatePath));
            ObsWebcamNativeSession.ValidateFinalizedMedia(candidatePath);

            var probeJson = _processRunner.Run(
                FFmpegRuntime.FfprobePath,
                BuildFfprobeArguments(candidatePath));
            ValidateProbeJson(probeJson);

            File.Move(candidatePath, fullFinalPath, overwrite: true);
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
                    "Webcam finalization failed and its unpublished candidate could not be removed.",
                    failure,
                    cleanupFailure);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static string[] BuildFfmpegArguments(string intermediatePath, string candidatePath) =>
    [
        "-v", "error",
        "-y",
        "-i", intermediatePath,
        "-map", "0:v:0",
        "-c:v", "copy",
        "-an",
        "-map_metadata", "-1",
        candidatePath,
    ];

    internal static string[] BuildFfprobeArguments(string candidatePath) =>
    [
        "-v", "error",
        "-show_entries", "stream=codec_type",
        "-of", "json",
        candidatePath,
    ];

    internal static void ValidateProbeJson(string probeJson)
    {
        try
        {
            using var document = JsonDocument.Parse(probeJson);
            if (!document.RootElement.TryGetProperty("streams", out var streams) ||
                streams.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("FFprobe did not return a stream array for finalized webcam media.");
            }

            var videoStreams = 0;
            var audioStreams = 0;
            foreach (var stream in streams.EnumerateArray())
            {
                if (!stream.TryGetProperty("codec_type", out var codecType))
                    continue;

                switch (codecType.GetString())
                {
                    case "video":
                        videoStreams++;
                        break;
                    case "audio":
                        audioStreams++;
                        break;
                }
            }

            if (videoStreams != 1 || audioStreams != 0)
            {
                throw new InvalidDataException(
                    $"Finalized webcam media must contain exactly one video stream and zero audio streams; " +
                    $"found {videoStreams} video and {audioStreams} audio streams.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("FFprobe returned malformed JSON for finalized webcam media.", ex);
        }
    }

}

internal sealed class BoundedWebcamFinalizationProcessRunner : IWebcamFinalizationProcessRunner
{
    private static readonly TimeSpan DefaultProcessTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DefaultTerminationTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _processTimeout;
    private readonly TimeSpan _terminationTimeout;

    public BoundedWebcamFinalizationProcessRunner()
        : this(DefaultProcessTimeout, DefaultTerminationTimeout)
    {
    }

    internal BoundedWebcamFinalizationProcessRunner(
        TimeSpan processTimeout,
        TimeSpan terminationTimeout)
    {
        if (processTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        if (terminationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(terminationTimeout));
        _processTimeout = processTimeout;
        _terminationTimeout = terminationTimeout;
    }

    public string Run(string executable, IReadOnlyList<string> arguments) =>
        RunAsync(executable, arguments).GetAwaiter().GetResult();

    private async Task<string> RunAsync(string executable, IReadOnlyList<string> arguments)
    {
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
            throw new IOException($"Failed to start {Path.GetFileName(executable)} for webcam finalization.");

        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var exit = process.WaitForExitAsync();
        var drain = Task.WhenAll(standardOutput, standardError);
        var completion = Task.WhenAll(exit, drain);
        try
        {
            await completion.WaitAsync(_processTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException timeout)
        {
            var failures = new List<Exception>
            {
                new TimeoutException(
                    $"{Path.GetFileName(executable)} exceeded the webcam finalization timeout.",
                    timeout),
            };
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception killFailure)
            {
                failures.Add(new IOException("The timed-out webcam finalization process could not be killed.", killFailure));
            }

            try
            {
                await completion.WaitAsync(_terminationTimeout).ConfigureAwait(false);
            }
            catch (Exception terminationFailure)
            {
                failures.Add(new IOException(
                    "The timed-out webcam finalization process did not terminate and close its redirected pipes within the cleanup timeout.",
                    terminationFailure));
                _ = completion.ContinueWith(
                    task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            if (failures.Count == 1)
                ExceptionDispatchInfo.Capture(failures[0]).Throw();
            throw new AggregateException("Webcam finalization timed out and cleanup was incomplete.", failures);
        }

        if (process.ExitCode != 0)
        {
            throw new IOException(
                $"{Path.GetFileName(executable)} exited with code {process.ExitCode} during webcam finalization: " +
                (await standardError.ConfigureAwait(false)).Trim());
        }

        return await standardOutput.ConfigureAwait(false);
    }
}
