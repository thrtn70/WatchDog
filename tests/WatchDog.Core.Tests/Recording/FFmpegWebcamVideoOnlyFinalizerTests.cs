using System.Diagnostics;
using WatchDog.Core.Recording;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Recording;

// Runs outside the parallel suite: TimedOutProcessIsKilledWithinTerminationBound times a cold
// powershell.exe start, which has taken more than 20 seconds under full parallel load on
// GitHub's Windows runners.
[Collection(SerialProcessCollection.Name)]
public sealed class FFmpegWebcamVideoOnlyFinalizerTests
{
    [Fact]
    public void ValidCandidateAtomicallyReplacesFinalWithoutDeletingIntermediate()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var intermediate = Path.Combine(directory, "camera.watchdog-mux.mkv");
            var final = Path.Combine(directory, "camera.mkv");
            File.WriteAllBytes(intermediate, [1, 2, 3]);
            File.WriteAllBytes(final, [9]);
            var runner = new FakeProcessRunner((call, _, arguments) =>
            {
                if (call == 1)
                {
                    File.WriteAllBytes(arguments[^1], [4, 5, 6]);
                    return string.Empty;
                }

                return "{\"streams\":[{\"codec_type\":\"video\"}]}";
            });
            var finalizer = new FFmpegWebcamVideoOnlyFinalizer(runner);

            finalizer.Finalize(intermediate, final);

            Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(final));
            Assert.True(File.Exists(intermediate));
            Assert.Equal(2, runner.Calls);
            Assert.Empty(Directory.GetFiles(directory, "*.watchdog-finalizing-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidCandidateStreamsLeaveExistingFinalUntouchedAndRemoveCandidate()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var intermediate = Path.Combine(directory, "camera.watchdog-mux.mkv");
            var final = Path.Combine(directory, "camera.mkv");
            File.WriteAllBytes(intermediate, [1, 2, 3]);
            File.WriteAllBytes(final, [9]);
            var runner = new FakeProcessRunner((call, _, arguments) =>
            {
                if (call == 1)
                {
                    File.WriteAllBytes(arguments[^1], [4, 5, 6]);
                    return string.Empty;
                }

                return "{\"streams\":[{\"codec_type\":\"video\"},{\"codec_type\":\"audio\"}]}";
            });
            var finalizer = new FFmpegWebcamVideoOnlyFinalizer(runner);

            Assert.Throws<InvalidDataException>(() => finalizer.Finalize(intermediate, final));

            Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(final));
            Assert.True(File.Exists(intermediate));
            Assert.Empty(Directory.GetFiles(directory, "*.watchdog-finalizing-*"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TimedOutProcessIsKilledWithinTerminationBound()
    {
        var runner = new BoundedWebcamFinalizationProcessRunner(
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(3));
        var elapsed = Stopwatch.StartNew();

        Assert.Throws<TimeoutException>(() => runner.Run(
            "powershell.exe",
            ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"]));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(5));
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "WatchDog-webcam-finalizer-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class FakeProcessRunner(
        Func<int, string, IReadOnlyList<string>, string> run) : IWebcamFinalizationProcessRunner
    {
        public int Calls { get; private set; }

        public string Run(string executable, IReadOnlyList<string> arguments)
        {
            Calls++;
            return run(Calls, executable, arguments);
        }
    }
}
