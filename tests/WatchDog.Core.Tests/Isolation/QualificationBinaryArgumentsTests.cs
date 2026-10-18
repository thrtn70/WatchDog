using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class QualificationBinaryArgumentsTests
{
    private static readonly string[] HashKeys =
    [
        "--qualification-assembly-hash",
        "--capture-isolation-windows-hash",
        "--capture-isolation-hash",
        "--capture-protocol-hash",
    ];

    [Fact]
    public void RunnerRequiresEachExpectedAssemblyHash()
    {
        var complete = RunnerArguments();
        Assert.True(QualificationOptions.TryParse(complete, out _));
        foreach (var key in HashKeys)
        {
            var missing = RemovePair(complete, key);
            Assert.False(QualificationOptions.TryParse(missing, out _));
            var malformed = ReplaceValue(complete, key, "unavailable");
            Assert.False(QualificationOptions.TryParse(malformed, out _));
        }
    }

    [Fact]
    public void RunnerNormalizesUppercaseAssemblyHashes()
    {
        Assert.True(QualificationOptions.TryParse(RunnerArguments(), out var options));
        Assert.NotNull(options);
        Assert.All(new[]
        {
            options.QualificationAssemblyHash,
            options.CaptureIsolationWindowsHash,
            options.CaptureIsolationHash,
            options.CaptureProtocolHash,
        }, hash => Assert.Equal(new string('a', 64), hash));
    }

    [Fact]
    public void ChildRolesNeedNoRunnerOnlyHashArguments()
    {
        var arguments = BaseArguments("owner")
            .Concat(["--scenario", "ownerdeath"])
            .ToArray();
        Assert.True(QualificationOptions.TryParse(arguments, out var options));
        Assert.NotNull(options);
        Assert.Equal(string.Empty, options.QualificationAssemblyHash);
        Assert.False(QualificationOptions.TryParse(
            arguments.Concat([HashKeys[0], new string('a', 64)]).ToArray(), out _));
    }

    private static string[] RunnerArguments() => BaseArguments("runner")
        .Concat(HashKeys.SelectMany(key => new[] { key, new string('A', 64) }))
        .ToArray();

    private static string[] BaseArguments(string role) =>
    [
        "--opt-in", QualificationConstants.OptInToken,
        "--mode", role,
        "--nonce", Guid.NewGuid().ToString("D"),
        "--artifact-root", Path.GetTempPath(),
        "--executable", typeof(QualificationBinaryArgumentsTests).Assembly.Location,
    ];

    private static string[] RemovePair(string[] values, string key)
    {
        var index = Array.IndexOf(values, key);
        return values.Where((_, current) => current != index && current != index + 1).ToArray();
    }

    private static string[] ReplaceValue(string[] values, string key, string replacement)
    {
        var copy = (string[])values.Clone();
        copy[Array.IndexOf(copy, key) + 1] = replacement;
        return copy;
    }
}
