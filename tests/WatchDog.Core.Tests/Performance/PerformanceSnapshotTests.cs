using WatchDog.Core.Performance;

namespace WatchDog.Core.Tests.Performance;

public sealed class PerformanceSnapshotTests
{
    [Fact]
    public void DropRate_CalculatesCorrectly()
    {
        var snap = new PerformanceSnapshot { DroppedFrames = 5, TotalFrames = 100 };
        Assert.Equal(5.0, snap.DropRate);
    }

    [Fact]
    public void DropRate_ZeroTotalFrames_ReturnsZero()
    {
        var snap = new PerformanceSnapshot { DroppedFrames = 0, TotalFrames = 0 };
        Assert.Equal(0.0, snap.DropRate);
    }

    [Fact]
    public void Summary_FormatsCorrectly()
    {
        var snap = new PerformanceSnapshot
        {
            RenderFps = 59.9,
            DroppedFrames = 3,
            TotalFrames = 1000,
            CpuUsage = 12.7,
            MemoryUsageMb = 256,
        };

        Assert.Contains("59.9 fps", snap.Summary);
        Assert.Contains("3 dropped", snap.Summary);
        Assert.Contains("CPU 13%", snap.Summary); // F0 rounds 12.7 to 13
        Assert.Contains("RAM 256MB", snap.Summary);
    }

    [Fact]
    public void Defaults_AreZero()
    {
        var snap = new PerformanceSnapshot();

        Assert.Equal(0, snap.RenderFps);
        Assert.Equal(0, snap.DroppedFrames);
        Assert.Equal(0, snap.TotalFrames);
        Assert.Equal(0, snap.CpuUsage);
        Assert.Equal(0, snap.MemoryUsageMb);
    }
}
