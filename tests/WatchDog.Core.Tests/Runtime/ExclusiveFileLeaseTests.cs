using WatchDog.Core.Runtime;

namespace WatchDog.Core.Tests.Runtime;

public sealed class ExclusiveFileLeaseTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        $"WatchDog_Lease_Test_{Guid.NewGuid():N}");

    [Fact]
    public void TryAcquire_HoldsExclusiveLeaseUntilDisposed_AndKeepsLockFile()
    {
        var path = Path.Combine(_tempDir, "watchdog.lock");

        using var first = ExclusiveFileLease.TryAcquire(path);
        var second = ExclusiveFileLease.TryAcquire(path);

        Assert.NotNull(first);
        Assert.Null(second);
        first.Dispose();

        using var reacquired = ExclusiveFileLease.TryAcquire(path);
        Assert.NotNull(reacquired);
        Assert.True(File.Exists(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }
}
