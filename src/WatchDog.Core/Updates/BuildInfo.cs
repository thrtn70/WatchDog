using System.Reflection;

namespace WatchDog.Core.Updates;

/// <summary>
/// Provides build-time metadata: version string, channel (stable vs pre-release),
/// and base version. Reads from AssemblyInformationalVersionAttribute.
/// </summary>
public static class BuildInfo
{
    private static readonly Lazy<string?> CachedVersion = new(ReadVersionString);

    /// <summary>
    /// Returns the full version string (e.g., "1.3.0-rc.1" or "1.2.3").
    /// Strips the +commitHash suffix if present.
    /// </summary>
    public static string? GetVersionString() => CachedVersion.Value;

    /// <summary>
    /// Returns the base version without pre-release suffix (e.g., "1.3.0" from "1.3.0-rc.1").
    /// </summary>
    public static string? GetBaseVersion()
    {
        var version = GetVersionString();
        if (version is null) return null;

        var dashIndex = version.IndexOf('-');
        return dashIndex >= 0 ? version[..dashIndex] : version;
    }

    /// <summary>
    /// Returns the build channel: PreRelease if the version contains a '-' suffix, Stable otherwise.
    /// </summary>
    public static BuildChannel GetChannel()
    {
        var version = GetVersionString();
        if (version is null) return BuildChannel.Stable;

        return version.Contains('-') ? BuildChannel.PreRelease : BuildChannel.Stable;
    }

    /// <summary>Convenience: true if this is a pre-release build.</summary>
    public static bool IsPreRelease() => GetChannel() == BuildChannel.PreRelease;

    /// <summary>
    /// Parses the base version as a System.Version for numeric comparison.
    /// Returns null if parsing fails.
    /// </summary>
    public static Version? GetParsedBaseVersion()
    {
        var baseVersion = GetBaseVersion();
        return baseVersion is not null && Version.TryParse(baseVersion, out var v) ? v : null;
    }

    private static string? ReadVersionString()
    {
        var infoVersion = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (infoVersion is null) return null;

        // Strip +commitHash suffix (e.g., "1.3.0-rc.1+abc123" → "1.3.0-rc.1")
        var plusIndex = infoVersion.IndexOf('+');
        return plusIndex >= 0 ? infoVersion[..plusIndex] : infoVersion;
    }
}
