using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1EntryDispatchTests
{
    [Fact]
    public void ExistingRunnerArgumentsRetainLegacyRoute()
    {
        var arguments = LegacyRunnerArguments();
        Assert.Equal(FixtureEntryRoute.Legacy, FixtureB1EntryDispatch.Classify(arguments));
        Assert.True(QualificationOptions.TryParse(arguments, out _));
    }

    [Fact]
    public void ExistingChildAndBootstrapFieldsRetainLegacyRoute()
    {
        var arguments = LegacyRunnerArguments()[..^8];
        arguments[3] = "helper";
        string[] withBootstrap =
        [
            .. arguments,
            "--scenario", "ownerdeath",
            "--bootstrap-section", "123",
            "--bootstrap-parent", "456",
            "--bootstrap-bytes", "4096",
            "--sentinel-handle", "789",
        ];
        Assert.Equal(FixtureEntryRoute.Legacy,
            FixtureB1EntryDispatch.Classify(withBootstrap));
        Assert.True(QualificationOptions.TryParse(withBootstrap, out _));
    }

    [Theory]
    [InlineData("artifact-launcher")]
    [InlineData("artifact-runner")]
    public void B1ModesStayUnavailableBeforeLegacyParser(string mode)
    {
        var arguments = LegacyRunnerArguments();
        arguments[3] = mode;
        Assert.Equal(FixtureEntryRoute.B1Unavailable,
            FixtureB1EntryDispatch.Classify(arguments));
    }

    [Theory]
    [InlineData("--LAUNCH-ID", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee")]
    [InlineData("--launch-id=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "ignored")]
    [InlineData("--CONTROL-ENDPOINT", "WDB1-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("--mode=artifact-launcher", "ignored")]
    public void MixedOrAliasedB1FieldsRejectBeforeLegacyArtifactAdmission(
        string extraKey, string extraValue)
    {
        string[] arguments = [.. LegacyRunnerArguments(), extraKey, extraValue];
        Assert.Equal(FixtureEntryRoute.Reject,
            FixtureB1EntryDispatch.Classify(arguments));
        // The old parser ignores these unknown names, so the entry guard matters.
        Assert.True(QualificationOptions.TryParse(arguments, out _));
    }

    [Fact]
    public void InvalidLegacyOptionShapeRejectsBeforePermissiveParser()
    {
        var arguments = LegacyRunnerArguments();
        Assert.Equal(FixtureEntryRoute.Reject,
            FixtureB1EntryDispatch.Classify([.. arguments, "--unknown", "x"]));
        Assert.Equal(FixtureEntryRoute.Reject,
            FixtureB1EntryDispatch.Classify([.. arguments, "--repo-head", "x"]));
        Assert.Equal(FixtureEntryRoute.Reject,
            FixtureB1EntryDispatch.Classify([.. arguments, "--unknown"]));
        Assert.Equal(FixtureEntryRoute.Reject,
            FixtureB1EntryDispatch.Classify([.. arguments, "--mode=artifact-runner", "x"]));
        Assert.Equal(FixtureEntryRoute.Reject,
            FixtureB1EntryDispatch.Classify(null));
        Assert.Equal(FixtureEntryRoute.Reject,
            FixtureB1EntryDispatch.Classify([]));
    }

    [Fact]
    public void CaseAliasedB1ModeRejectsWithoutLegacyFallback()
    {
        var arguments = LegacyRunnerArguments();
        arguments[3] = "Artifact-Runner";
        Assert.Equal(FixtureEntryRoute.Reject,
            FixtureB1EntryDispatch.Classify(arguments));
    }

    [Fact]
    public void B1OptionTokensInLegacyValueSlotsRejectBeforeArtifactAdmission()
    {
        var inRepoHead = LegacyRunnerArguments();
        inRepoHead[11] = "--launch-id";
        Assert.Equal(FixtureEntryRoute.Reject,
            FixtureB1EntryDispatch.Classify(inRepoHead));
        Assert.True(QualificationOptions.TryParse(inRepoHead, out _));

        string[] inScenario = [.. LegacyRunnerArguments(), "--scenario", "--launch-id"];
        Assert.Equal(FixtureEntryRoute.Reject,
            FixtureB1EntryDispatch.Classify(inScenario));
        Assert.True(QualificationOptions.TryParse(inScenario, out _));
    }

    private static string[] LegacyRunnerArguments() =>
    [
        "--opt-in", QualificationConstants.OptInToken,
        "--mode", "runner",
        "--nonce", Guid.NewGuid().ToString("D"),
        "--artifact-root", Path.GetTempPath(),
        "--executable", typeof(FixtureB1EntryDispatchTests).Assembly.Location,
        "--repo-head", "legacy",
        "--dirty-summary-hash", "legacy",
        "--source-hash", "legacy",
        "--binary-hash", "legacy",
        "--qualification-assembly-hash", new string('a', 64),
        "--capture-isolation-windows-hash", new string('b', 64),
        "--capture-isolation-hash", new string('c', 64),
        "--capture-protocol-hash", new string('d', 64),
    ];
}
