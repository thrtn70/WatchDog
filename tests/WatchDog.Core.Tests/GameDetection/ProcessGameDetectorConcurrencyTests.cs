using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.Core.GameDetection;

namespace WatchDog.Core.Tests.GameDetection;

public sealed class ProcessGameDetectorConcurrencyTests
{
    [Fact]
    public async Task ConcurrentTrackingCandidates_CommitAndAnnounceExactlyOnce()
    {
        using var detector = new ProcessGameDetector(
            new GameDatabase(),
            NullLogger<ProcessGameDetector>.Instance);
        SetRunning(detector, true);
        using var process = Process.GetCurrentProcess();
        var game = new GameInfo
        {
            ExecutableName = process.ProcessName + ".exe",
            DisplayName = "Concurrent fixture",
            ProcessId = process.Id,
            ProcessStartedAt = process.StartTime.ToUniversalTime(),
        };
        var announcements = 0;
        detector.GameStarted += _ => Interlocked.Increment(ref announcements);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => detector.TrackGame(game))));

        Assert.Same(game, detector.CurrentGame);
        Assert.Equal(1, Volatile.Read(ref announcements));
        detector.Stop();
        Assert.Null(detector.CurrentGame);
    }

    [Fact]
    public void TrackingCandidateAfterStop_IsRejectedWithoutAnnouncement()
    {
        using var detector = new ProcessGameDetector(
            new GameDatabase(),
            NullLogger<ProcessGameDetector>.Instance);
        using var process = Process.GetCurrentProcess();
        var game = new GameInfo
        {
            ExecutableName = process.ProcessName + ".exe",
            DisplayName = "Stopped fixture",
            ProcessId = process.Id,
            ProcessStartedAt = process.StartTime.ToUniversalTime(),
        };
        var announcements = 0;
        detector.GameStarted += _ => announcements++;

        detector.TrackGame(game);

        Assert.Null(detector.CurrentGame);
        Assert.Equal(0, announcements);
    }

    private static void SetRunning(ProcessGameDetector detector, bool value)
    {
        var field = typeof(ProcessGameDetector).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(detector, value);
    }
}
