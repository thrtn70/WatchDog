using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal sealed record FixtureAssemblyAttribution(string Sha256, Guid Mvid);

/// <summary>Runner-only, same-stream disk evidence matched to an already loaded assembly.</summary>
internal static class FixtureBinaryAttribution
{
    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } &&
        value.All(static character => character is
            >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    internal static FixtureAssemblyAttribution? ObserveLoaded(
        string outputDirectory,
        string fileName,
        Assembly loadedAssembly,
        string expectedSha256)
    {
        if (!IsSha256(expectedSha256) || string.IsNullOrWhiteSpace(loadedAssembly.Location))
            return null;
        var outputPath = Path.GetFullPath(Path.Combine(outputDirectory, fileName));
        if (!Path.GetFullPath(loadedAssembly.Location).Equals(
                outputPath, StringComparison.OrdinalIgnoreCase))
            return null;
        return ObserveFile(outputPath, loadedAssembly.ManifestModule.ModuleVersionId,
            expectedSha256);
    }

    internal static FixtureAssemblyAttribution? ObserveFile(
        string path,
        Guid loadedMvid,
        string expectedSha256)
    {
        if (loadedMvid == Guid.Empty || !IsSha256(expectedSha256))
            return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            stream.Position = 0;
            using var peReader = new PEReader(stream, PEStreamOptions.LeaveOpen);
            if (!peReader.HasMetadata)
                return null;
            var metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly)
                return null;
            var diskMvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
            if (diskMvid == Guid.Empty || diskMvid != loadedMvid ||
                !sha256.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                return null;
            return new(sha256, diskMvid);
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or BadImageFormatException or
            ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }
}
