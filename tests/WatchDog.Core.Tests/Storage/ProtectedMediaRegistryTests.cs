using WatchDog.Core.Storage;

namespace WatchDog.Core.Tests.Storage;

public sealed class ProtectedMediaRegistryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "WatchDog-protected-media-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void EquivalentPathsShareReferenceCountedProtection()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "clip.mp4");
        var equivalentPath = Path.Combine(_directory, ".", "clip.mp4");
        var registry = new ProtectedMediaRegistry();

        var first = registry.Protect(path);
        var second = registry.Protect(equivalentPath);

        Assert.True(registry.IsProtected(path));
        first.Dispose();
        Assert.True(registry.IsProtected(equivalentPath));
        first.Dispose();
        Assert.True(registry.IsProtected(path));
        second.Dispose();
        Assert.False(registry.IsProtected(path));
    }

    [Fact]
    public void DeletionReservationIsMutuallyExclusiveWithProtection()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "clip.mp4");
        var registry = new ProtectedMediaRegistry();

        var deletion = registry.TryReserveDeletion(path);

        Assert.NotNull(deletion);
        Assert.Null(registry.TryReserveDeletion(path));
        Assert.Throws<InvalidOperationException>(() => registry.Protect(path));
        deletion!.Dispose();
        using var protection = registry.Protect(path);
        Assert.Null(registry.TryReserveDeletion(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
