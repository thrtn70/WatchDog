namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal enum FixtureEntryRoute
{
    Reject,
    Legacy,
    B1Unavailable,
}

/// <summary>
/// Entry-only syntax guard. B1 remains unavailable; legacy options are closed
/// before the older, permissive QualificationOptions parser can create artifacts.
/// </summary>
internal static class FixtureB1EntryDispatch
{
    private static readonly HashSet<string> LegacyKeys = new(StringComparer.Ordinal)
    {
        "--opt-in", "--mode", "--scenario", "--nonce", "--artifact-root",
        "--executable", "--repo-head", "--dirty-summary-hash", "--source-hash",
        "--binary-hash", "--qualification-assembly-hash",
        "--capture-isolation-windows-hash", "--capture-isolation-hash",
        "--capture-protocol-hash", "--bootstrap-section", "--bootstrap-parent",
        "--bootstrap-bytes", "--sentinel-handle", "--publication-only",
        "--distinct-root-only",
    };

    private static readonly HashSet<string> B1OnlyKeys = new(StringComparer.Ordinal)
    {
        "--launch-id", "--control-endpoint",
    };

    internal static FixtureEntryRoute Classify(IReadOnlyList<string>? arguments)
    {
        if (arguments is null || arguments.Count == 0 || arguments.Count % 2 != 0)
            return FixtureEntryRoute.Reject;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? mode = null;
        var hasB1OnlyKey = false;
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var key = arguments[index];
            var value = arguments[index + 1];
            if (key is null || value is null ||
                !key.StartsWith("--", StringComparison.Ordinal) ||
                key.Contains('=') || value.StartsWith("--", StringComparison.Ordinal) ||
                !seen.Add(key))
                return FixtureEntryRoute.Reject;

            if (B1OnlyKeys.Contains(key))
                hasB1OnlyKey = true;
            else if (!LegacyKeys.Contains(key))
                return FixtureEntryRoute.Reject;

            if (key == "--mode")
                mode = value;
        }

        if (mode is "artifact-launcher" or "artifact-runner")
            return FixtureEntryRoute.B1Unavailable;
        if (hasB1OnlyKey ||
            string.Equals(mode, "artifact-launcher", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mode, "artifact-runner", StringComparison.OrdinalIgnoreCase))
            return FixtureEntryRoute.Reject;
        return FixtureEntryRoute.Legacy;
    }
}
