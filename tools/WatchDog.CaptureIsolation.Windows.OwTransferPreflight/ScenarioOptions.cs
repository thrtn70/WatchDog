namespace WatchDog.CaptureIsolation.Windows.OwTransferPreflight;

internal sealed record ScenarioOptions(string Name, string Kind, string? Resource)
{
    internal static ScenarioOptions Success { get; } = new("success", "success", null);
    internal bool IsEarlyOwnerDeath => Kind == "owner-death-edge";

    internal static bool TryParse(string name, out ScenarioOptions? scenario)
    {
        scenario = null;
        if (name is "success" or "source-reuse" or "job-close" or "owner-death" or "birth-failure")
        {
            scenario = new(name, name, null);
            return true;
        }
        foreach (var kind in new[] { "store-failure", "stop-after-success", "writer-death", "owner-death" })
        {
            var prefix = kind + "-";
            if (!name.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var resource = name[prefix.Length..];
            if (resource is not ("trusted-parent" or "private-root" or "frames" or "anchor"))
                return false;
            scenario = new(name, kind == "owner-death" ? "owner-death-edge" : kind, resource);
            return true;
        }
        return false;
    }
}

internal sealed class ExpectedScenarioFault(string code) : Exception(code)
{
    internal string Code { get; } = code;
}
