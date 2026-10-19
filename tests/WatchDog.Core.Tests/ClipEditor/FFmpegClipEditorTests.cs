using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.ClipEditor;

namespace WatchDog.Core.Tests.ClipEditor;

public sealed class FFmpegClipEditorTests
{
    private const string ValidProbeJson =
        "{\"streams\":[{\"codec_type\":\"video\"}],\"format\":{\"duration\":\"4.500000\"}}";

    // Bounds only the child's startup; it sleeps 30 seconds after publishing its PID.
    private static readonly TimeSpan PidPublicationTimeout = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task TrimAsync_PreservesExistingDestinationAndPublishesWithSuffix()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var input = CreateInput(directory);
            var existing = Path.Combine(directory, "clip_trimmed.mp4");
            await File.WriteAllBytesAsync(existing, [9]);
            var runner = SuccessfulRunner([1, 2, 3]);
            var editor = CreateEditor(runner);

            var result = await editor.TrimAsync(input, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

            Assert.Equal(Path.Combine(directory, "clip_trimmed_2.mp4"), result);
            Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(existing));
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(result));
            AssertNoCandidates(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TrimAsync_RepeatedExportsNeverOverwritePublishedFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var input = CreateInput(directory);
            var call = 0;
            var runner = new FakeProcessRunner(async (executable, arguments, ct) =>
            {
                await Task.Yield();
                ct.ThrowIfCancellationRequested();
                if (executable == "ffmpeg")
                {
                    var value = (byte)Interlocked.Increment(ref call);
                    await File.WriteAllBytesAsync(arguments[^1], [value], ct);
                    return string.Empty;
                }

                return ValidProbeJson;
            });
            var editor = CreateEditor(runner);

            var first = await editor.TrimAsync(input, TimeSpan.Zero, TimeSpan.FromSeconds(4));
            var second = await editor.TrimAsync(input, TimeSpan.Zero, TimeSpan.FromSeconds(4));

            Assert.Equal(Path.Combine(directory, "clip_trimmed.mp4"), first);
            Assert.Equal(Path.Combine(directory, "clip_trimmed_2.mp4"), second);
            Assert.Equal(new byte[] { 1 }, await File.ReadAllBytesAsync(first));
            Assert.Equal(new byte[] { 2 }, await File.ReadAllBytesAsync(second));
            AssertNoCandidates(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TrimAsync_ConcurrentExportsPublishDistinctFiles()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var input = CreateInput(directory);
            var ffmpegCalls = 0;
            var bothCandidatesReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new FakeProcessRunner(async (executable, arguments, ct) =>
            {
                if (executable != "ffmpeg")
                    return ValidProbeJson;

                var value = (byte)Interlocked.Increment(ref ffmpegCalls);
                await File.WriteAllBytesAsync(arguments[^1], [value], ct);
                if (value == 2)
                    bothCandidatesReady.TrySetResult();
                await bothCandidatesReady.Task.WaitAsync(ct);
                return string.Empty;
            });
            var editor = CreateEditor(runner);

            var exports = await Task.WhenAll(
                editor.TrimAsync(input, TimeSpan.Zero, TimeSpan.FromSeconds(4)),
                editor.TrimAsync(input, TimeSpan.Zero, TimeSpan.FromSeconds(4)));

            Assert.Equal(2, exports.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.Contains(Path.Combine(directory, "clip_trimmed.mp4"), exports);
            Assert.Contains(Path.Combine(directory, "clip_trimmed_2.mp4"), exports);
            Assert.Equal(
                new byte[] { 1, 2 },
                exports.Select(path => File.ReadAllBytes(path)[0]).Order().ToArray());
            AssertNoCandidates(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TrimAsync_RejectsEmptyCandidateAndCleansItUp()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var input = CreateInput(directory);
            var runner = new FakeProcessRunner((executable, arguments, _) =>
            {
                if (executable == "ffmpeg")
                    File.Create(arguments[^1]).Dispose();
                return Task.FromResult(executable == "ffprobe" ? ValidProbeJson : string.Empty);
            });
            var editor = CreateEditor(runner);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => editor.TrimAsync(input, TimeSpan.Zero, TimeSpan.FromSeconds(4)));

            Assert.False(File.Exists(Path.Combine(directory, "clip_trimmed.mp4")));
            AssertNoCandidates(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"streams\":[],\"format\":{\"duration\":\"4.5\"}}")]
    [InlineData("{\"streams\":[{\"codec_type\":\"video\"}],\"format\":{\"duration\":\"0\"}}")]
    [InlineData("not-json")]
    public async Task TrimAsync_InvalidProbeOutputPublishesNothingAndCleansCandidate(string probeJson)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var input = CreateInput(directory);
            var runner = SuccessfulRunner([1, 2, 3], probeJson);
            var editor = CreateEditor(runner);

            await Assert.ThrowsAsync<InvalidDataException>(
                () => editor.TrimAsync(input, TimeSpan.Zero, TimeSpan.FromSeconds(4)));

            Assert.False(File.Exists(Path.Combine(directory, "clip_trimmed.mp4")));
            AssertNoCandidates(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("ffmpeg")]
    [InlineData("ffprobe")]
    public async Task TrimAsync_ProcessFailurePublishesNothingAndCleansCandidate(string failedExecutable)
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var input = CreateInput(directory);
            var runner = new FakeProcessRunner(async (executable, arguments, ct) =>
            {
                if (executable == "ffmpeg")
                {
                    await File.WriteAllBytesAsync(arguments[^1], [1, 2, 3], ct);
                }

                if (executable == failedExecutable)
                    throw new InvalidOperationException($"{executable} failed");

                return ValidProbeJson;
            });
            var editor = CreateEditor(runner);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => editor.TrimAsync(input, TimeSpan.Zero, TimeSpan.FromSeconds(4)));

            Assert.Equal($"{failedExecutable} failed", error.Message);
            Assert.False(File.Exists(Path.Combine(directory, "clip_trimmed.mp4")));
            AssertNoCandidates(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TrimAsync_CancellationPublishesNothingAndCleansCandidate()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var input = CreateInput(directory);
            var candidateReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new FakeProcessRunner(async (executable, arguments, ct) =>
            {
                if (executable != "ffmpeg")
                    return ValidProbeJson;

                await File.WriteAllBytesAsync(arguments[^1], [1, 2, 3], ct);
                candidateReady.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                return string.Empty;
            });
            var editor = CreateEditor(runner);
            using var cts = new CancellationTokenSource();

            var trim = editor.TrimAsync(input, TimeSpan.Zero, TimeSpan.FromSeconds(4), cts.Token);
            await candidateReady.Task;
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trim);
            Assert.False(File.Exists(Path.Combine(directory, "clip_trimmed.mp4")));
            AssertNoCandidates(directory);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TrimAsync_UsesNoOverwriteCandidateAndProbesBeforePublication()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var input = CreateInput(directory);
            var runner = SuccessfulRunner([1, 2, 3]);
            var editor = CreateEditor(runner);

            var result = await editor.TrimAsync(input, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5));

            Assert.Equal(2, runner.Calls.Count);
            var ffmpeg = runner.Calls[0];
            Assert.Equal("ffmpeg", ffmpeg.Executable);
            Assert.Contains("-n", ffmpeg.Arguments);
            Assert.DoesNotContain("-y", ffmpeg.Arguments);
            Assert.Contains("watchdog-exporting", ffmpeg.Arguments[^1]);
            var ffprobe = runner.Calls[1];
            Assert.Equal("ffprobe", ffprobe.Executable);
            Assert.Equal(ffmpeg.Arguments[^1], ffprobe.Arguments[^1]);
            Assert.True(File.Exists(result));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ProcessRunner_CancellationKillsAndReapsProcessTree()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var pidPath = Path.Combine(directory, "pid.txt");
            var escapedPidPath = pidPath.Replace("'", "''", StringComparison.Ordinal);
            var runner = new ReapingClipEditorProcessRunner(TimeSpan.FromSeconds(5));
            using var cts = new CancellationTokenSource();
            var running = runner.RunAsync(
                "powershell.exe",
                ["-NoProfile", "-NonInteractive", "-Command",
                    $"$PID | Set-Content -LiteralPath '{escapedPidPath}'; Start-Sleep -Seconds 30"],
                cts.Token);

            var pid = await WaitForPublishedPidAsync(pidPath, running);

            cts.Cancel();

            if (pid is null)
            {
                // Observe the run so its cleanup finishes before the directory is deleted.
                var exitedEarly = running.IsCompleted;
                var outcome = await Record.ExceptionAsync(() => running);
                Assert.Fail(exitedEarly
                    ? $"The child process exited before publishing its PID: {outcome?.GetBaseException().Message ?? "no error"}."
                    : "The child process did not publish its PID.");
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            try
            {
                using var process = Process.GetProcessById(pid.Value);
                Assert.True(process.HasExited, "The canceled child process was not reaped.");
            }
            catch (ArgumentException)
            {
                // A reaped process is normally absent from the process table.
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // powershell.exe can take several seconds to cold-start on CI runners, so poll with a
    // generous deadline and stop early if the child has already exited.
    private static async Task<int?> WaitForPublishedPidAsync(string pidPath, Task running)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < PidPublicationTimeout && !running.IsCompleted)
        {
            if (TryReadPublishedPid(pidPath, out var pid))
                return pid;
            await Task.Delay(25);
        }

        return TryReadPublishedPid(pidPath, out var lastPid) ? lastPid : null;
    }

    // Set-Content creates the file before writing it and ends the value with a newline,
    // so only a newline-terminated value is a complete PID.
    private static bool TryReadPublishedPid(string pidPath, out int pid)
    {
        pid = 0;
        try
        {
            using var stream = new FileStream(
                pidPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            return text.EndsWith('\n') && int.TryParse(text.Trim(), out pid) && pid > 0;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static FFmpegClipEditor CreateEditor(IClipEditorProcessRunner runner) =>
        new(NullLogger<FFmpegClipEditor>.Instance, runner, "ffmpeg", "ffprobe");

    private static FakeProcessRunner SuccessfulRunner(byte[] bytes, string probeJson = ValidProbeJson) =>
        new(async (executable, arguments, ct) =>
        {
            if (executable == "ffmpeg")
            {
                await File.WriteAllBytesAsync(arguments[^1], bytes, ct);
                return string.Empty;
            }

            return probeJson;
        });

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WatchDog-clip-editor-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string CreateInput(string directory)
    {
        var input = Path.Combine(directory, "clip.mp4");
        File.WriteAllBytes(input, [7, 8, 9]);
        return input;
    }

    private static void AssertNoCandidates(string directory) =>
        Assert.Empty(Directory.GetFiles(directory, "*.watchdog-exporting-*"));

    private sealed class FakeProcessRunner(
        Func<string, IReadOnlyList<string>, CancellationToken, Task<string>> run) : IClipEditorProcessRunner
    {
        private readonly object _lock = new();
        private readonly List<ProcessCall> _calls = [];

        public IReadOnlyList<ProcessCall> Calls
        {
            get
            {
                lock (_lock)
                    return [.. _calls];
            }
        }

        public Task<string> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken ct)
        {
            lock (_lock)
                _calls.Add(new ProcessCall(executable, [.. arguments]));
            return run(executable, arguments, ct);
        }
    }

    private sealed record ProcessCall(string Executable, IReadOnlyList<string> Arguments);
}
