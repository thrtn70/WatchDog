using System.Diagnostics;
using WatchDog.Core.ClipEditor;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.ClipEditor;

// Runs outside the parallel suite: under full parallel load powershell.exe has taken more
// than 20 seconds to start on GitHub's Windows runners.
[Collection(SerialProcessCollection.Name)]
public sealed class ReapingClipEditorProcessRunnerTests
{
    // Bounds only the child's startup; it sleeps 30 seconds after publishing its PID.
    private static readonly TimeSpan PidPublicationTimeout = TimeSpan.FromSeconds(20);

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

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WatchDog-clip-editor-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }
}
