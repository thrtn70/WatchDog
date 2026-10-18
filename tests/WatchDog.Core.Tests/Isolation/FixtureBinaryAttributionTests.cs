using System.Security.Cryptography;
using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureBinaryAttributionTests
{
    [Theory]
    [InlineData("")]
    [InlineData("unavailable")]
    [InlineData("xyz")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void RejectsMissingOrMalformedExpectedHash(string hash) =>
        Assert.False(FixtureBinaryAttribution.IsSha256(hash));

    [Fact]
    public void AcceptsUppercaseHashAndMatchesSameStreamMetadata()
    {
        var assembly = typeof(FixtureBinaryAttributionTests).Assembly;
        var path = assembly.Location;
        var expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

        var observed = FixtureBinaryAttribution.ObserveLoaded(
            Path.GetDirectoryName(path)!, Path.GetFileName(path), assembly, expected);

        Assert.NotNull(observed);
        Assert.Equal(expected.ToLowerInvariant(), observed.Sha256);
        Assert.Equal(assembly.ManifestModule.ModuleVersionId, observed.Mvid);
    }

    [Fact]
    public void RejectsHashMismatchDespiteMatchingLoadedMvid()
    {
        var assembly = typeof(FixtureBinaryAttributionTests).Assembly;
        Assert.Null(FixtureBinaryAttribution.ObserveFile(
            assembly.Location, assembly.ManifestModule.ModuleVersionId,
            new string('0', 64)));
    }

    [Fact]
    public void RejectsMvidMismatchDespiteMatchingFileHash()
    {
        var assembly = typeof(FixtureBinaryAttributionTests).Assembly;
        var expected = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(assembly.Location)));
        Assert.Null(FixtureBinaryAttribution.ObserveFile(
            assembly.Location, Guid.NewGuid(), expected));
        Assert.Null(FixtureBinaryAttribution.ObserveFile(
            assembly.Location, Guid.Empty, expected));
    }

    [Fact]
    public void RejectsLoadedPathMismatch()
    {
        var assembly = typeof(FixtureBinaryAttributionTests).Assembly;
        var expected = Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(assembly.Location)));
        Assert.Null(FixtureBinaryAttribution.ObserveLoaded(
            Path.GetTempPath(), Path.GetFileName(assembly.Location), assembly, expected));
    }

    [Fact]
    public void RejectsMissingAndNonManagedFiles()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"watchdog-fixture-attribution-{Guid.NewGuid():N}.bin");
        var bytes = new byte[] { 1, 2, 3, 4 };
        var expected = Convert.ToHexString(SHA256.HashData(bytes));
        Assert.Null(FixtureBinaryAttribution.ObserveFile(path, Guid.NewGuid(), expected));
        try
        {
            File.WriteAllBytes(path, bytes);
            Assert.Null(FixtureBinaryAttribution.ObserveFile(path, Guid.NewGuid(), expected));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
