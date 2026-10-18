using System.Globalization;
using System.Text.Json;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal static class FixtureArtifactRootAdmission
{
    private const string NamespaceName = "WatchDog-Isolation-Qualification";
    private const string StampFormat = "yyyyMMdd'T'HHmmss.fffffff'Z'";

    internal static bool IsAdmitted(QualificationOptions options)
    {
        try
        {
            var namespacePath = Path.Combine(Path.GetTempPath(), NamespaceName);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ArtifactRoot));
            var runRoot = options.Role == QualificationRole.Runner
                ? root
                : Path.GetDirectoryName(root);
            if (runRoot is null ||
                !Path.GetDirectoryName(runRoot)!.Equals(
                    Path.TrimEndingDirectorySeparator(namespacePath),
                    StringComparison.OrdinalIgnoreCase) ||
                !HasExpectedRunName(Path.GetFileName(runRoot), options.Nonce) ||
                !IsExistingPlainDirectory(runRoot))
                return false;

            if (options.Role == QualificationRole.Runner)
                return options.Scenario is null && HasOnlyEmptyRedirects(runRoot);

            return options.Scenario is { } scenario &&
                Path.GetFileName(root).Equals(
                    scenario.ToString().ToLowerInvariant(), StringComparison.Ordinal) &&
                IsExistingPlainDirectory(root) &&
                HasMatchingManifest(runRoot, options.Nonce) &&
                IsPlainFile(Path.Combine(root, $"runner-{scenario}.jsonl"));
        }
        catch
        {
            // Admission must fail before any artifact creation or replacement.
            return false;
        }
    }

    private static bool HasExpectedRunName(string name, Guid nonce)
    {
        var suffix = $"-{nonce:D}";
        if (!name.EndsWith(suffix, StringComparison.Ordinal) ||
            name.Length != 24 + suffix.Length)
            return false;
        return DateTime.TryParseExact(name[..24], StampFormat,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }

    private static bool IsExistingPlainDirectory(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
        {
            var attributes = directory.Attributes;
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    private static bool HasOnlyEmptyRedirects(string runRoot)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(runRoot))
        {
            var name = Path.GetFileName(entry);
            if (name is not ("runner.stdout.txt" or "runner.stderr.txt") ||
                !IsPlainFile(entry) || new FileInfo(entry).Length != 0)
                return false;
        }
        return true;
    }

    private static bool IsPlainFile(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }

    private static bool HasMatchingManifest(string runRoot, Guid nonce)
    {
        var path = Path.Combine(runRoot, "manifest.json");
        if (!IsPlainFile(path) || new FileInfo(path).Length > 65536)
            return false;
        using var stream = File.OpenRead(path);
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.TryGetProperty("nonce", out var value) &&
            value.TryGetGuid(out var observed) && observed == nonce;
    }
}
