using System.Text;
using System.Text.Json;
using WatchDog.CaptureIsolation.Windows.Qualification;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1CommandTests
{
    private static readonly Guid RunNonce = Guid.Parse("aaaaaaaa-2222-3333-4444-555555555555");
    private static readonly Guid LaunchId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureHandoffProcessIdentity Launcher = new(1234, 987654321);
    private const string Executable = @"C:\WatchDog\Qualification.exe";

    [Fact]
    public void ExactLauncherAndRunnerArgumentsParseWithoutGrantingRuntimeAuthority()
    {
        Assert.True(FixtureB1Command.TryParseLauncher(
            LauncherArguments(), Executable, out var launcher));
        Assert.NotNull(launcher);
        Assert.Equal(RunNonce, launcher.RunNonce);
        Assert.Equal(Executable, launcher.Executable);

        Assert.True(FixtureB1Command.TryParseRunner(
            RunnerArguments(), RunNonce, out var runner));
        Assert.NotNull(runner);
        Assert.Equal(LaunchId, runner.LaunchId);
        Assert.Equal("WDB1-" + new string('a', 32), runner.ControlEndpoint);

        Assert.True(FixtureB1Command.TryParseRunnerSyntax(
            RunnerArguments(), out var untrustedRouting));
        Assert.Equal(runner, untrustedRouting);
    }

    [Fact]
    public void LauncherRejectsAliasesMixedLegacyFieldsAndAttributionDrift()
    {
        var cases = new Action<string[]>[]
        {
            values => values[0] = "--Opt-in",
            values => values[1] = "wrong-token",
            values => values[2] = "--mode=artifact-launcher",
            values => values[3] = "runner",
            values => values[4] = "--artifact-root",
            values => values[5] = RunNonce.ToString("D").ToUpperInvariant(),
            values => values[7] = @".\Qualification.exe",
            values => values[8] = "--publication-only",
            values => values[9] = new string('A', 40),
            values => values[11] = new string('G', 64),
            values => values[13] = new string('a', 63),
            values => values[15] = new string('a', 1025),
            values => values[17] = "abc\nxyz",
            values => values[18] = "--binary-hash",
        };
        foreach (var mutate in cases)
        {
            var arguments = LauncherArguments();
            mutate(arguments);
            Assert.False(FixtureB1Command.TryParseLauncher(
                arguments, Executable, out var parsed));
            Assert.Null(parsed);
        }

        Assert.False(FixtureB1Command.TryParseLauncher(
            LauncherArguments(), @"C:\Different\Qualification.exe", out _));
        Assert.False(FixtureB1Command.TryParseLauncher(
            LauncherArguments()[..^1], Executable, out _));
        Assert.False(FixtureB1Command.TryParseLauncher(
            [.. LauncherArguments(), "--sentinel-handle", "123"], Executable, out _));
    }

    [Fact]
    public void RunnerRejectsWrongRunReusedLaunchAndCoordinateArguments()
    {
        Assert.True(FixtureB1Command.TryParseRunnerSyntax(
            RunnerArguments(), out var untrustedRouting));
        Assert.Equal(RunNonce, untrustedRouting!.RunNonce);
        Assert.False(FixtureB1Command.TryParseRunner(
            RunnerArguments(), Guid.NewGuid(), out _));
        var cases = new Action<string[]>[]
        {
            values => values[3] = "runner",
            values => values[5] = Guid.Empty.ToString("D"),
            values => values[7] = RunNonce.ToString("D"),
            values => values[9] = "WDB1-" + new string('A', 32),
            values => values[9] = "WDB1-" + new string('a', 31),
            values => values[8] = "--bootstrap-section",
        };
        foreach (var mutate in cases)
        {
            var arguments = RunnerArguments();
            mutate(arguments);
            Assert.False(FixtureB1Command.TryParseRunner(
                arguments, RunNonce, out var parsed));
            Assert.Null(parsed);
        }
        Assert.False(FixtureB1Command.TryParseRunner(
            [.. RunnerArguments(), "--artifact-root", @"C:\Temp"], RunNonce, out _));
    }

    [Theory]
    [InlineData("passed", 0, true, true)]
    [InlineData("rejected", 2, true, false)]
    [InlineData("failed", 3, true, true)]
    [InlineData("failed", 4, true, true)]
    [InlineData("failed", 5, false, true)]
    [InlineData("failed", 6, true, false)]
    public void ExactTerminalStatusEnvelopeParsesButIsNotCleanupProof(
        string outcome, int exitCode, bool cleanup, bool hasLaunchId)
    {
        var exit = (QualificationExitCode)exitCode;
        var record = Status(outcome, exit, cleanup, hasLaunchId ? LaunchId : null);
        Assert.True(FixtureB1Command.TryParseTerminalStatus(
            record, RunNonce, Launcher, exit, out var parsed));
        Assert.NotNull(parsed);
        Assert.Equal(outcome, parsed.Outcome.ToString().ToLowerInvariant());
        Assert.Equal(cleanup, parsed.CleanupConfirmed);
    }

    [Fact]
    public void TerminalStatusRejectsWrongRunOrProcessAndContradictoryOutcome()
    {
        var passed = Status("passed", QualificationExitCode.Pass, true, LaunchId);
        Assert.False(FixtureB1Command.TryParseTerminalStatus(
            passed, Guid.NewGuid(), Launcher, QualificationExitCode.Pass, out _));
        Assert.False(FixtureB1Command.TryParseTerminalStatus(
            passed, RunNonce, Launcher with { CreationFileTime = 9 },
            QualificationExitCode.Pass, out _));
        Assert.False(FixtureB1Command.TryParseTerminalStatus(
            passed, RunNonce, Launcher, QualificationExitCode.CleanupIncomplete, out _));

        AssertRejectedStatus("failed", QualificationExitCode.Pass, true, LaunchId);
        AssertRejectedStatus("passed", QualificationExitCode.CleanupIncomplete, false, LaunchId);
        AssertRejectedStatus("passed", QualificationExitCode.Pass, true, null);
        AssertRejectedStatus("passed", QualificationExitCode.Pass, true, RunNonce);
        AssertRejectedStatus("rejected", QualificationExitCode.CleanupIncomplete, false, null);
        AssertRejectedStatus("failed", QualificationExitCode.AssertionFailedCleanupConfirmed,
            false, LaunchId);
        AssertRejectedStatus("failed", QualificationExitCode.CleanupIncomplete,
            true, LaunchId);
    }

    [Fact]
    public void TerminalStatusRejectsMalformedDuplicateUnknownOrUnboundedFrames()
    {
        var valid = Encoding.UTF8.GetString(Status("passed", QualificationExitCode.Pass,
            true, LaunchId));
        var cases = new[]
        {
            valid.Replace("\"version\":1", "\"version\":1,\"version\":1",
                StringComparison.Ordinal),
            valid.Replace("\"version\":1", "\"version\":1,\"artifactRoot\":\"x\"",
                StringComparison.Ordinal),
            valid.Replace("\"version\":1,", string.Empty, StringComparison.Ordinal),
            valid.Replace("b1-handshake-cleanup", "b1-general-pass", StringComparison.Ordinal),
            valid.Replace("\"launcherPid\":1234", "\"launcherPid\":1235",
                StringComparison.Ordinal),
            valid.Replace("\"launcherCreationFileTime\":987654321",
                "\"launcherCreationFileTime\":987654322", StringComparison.Ordinal),
            valid.TrimEnd('\n'),
            valid.Insert(1, "\n"),
            valid.Insert(1, "\r"),
            valid + "{}\n",
            "\uFEFF" + valid,
            "{" + new string(' ', FixtureB1Command.MaximumStatusBytes) + "}\n",
        };
        foreach (var value in cases)
            Assert.False(FixtureB1Command.TryParseTerminalStatus(
                Encoding.UTF8.GetBytes(value), RunNonce, Launcher,
                QualificationExitCode.Pass, out _));

        var malformedUtf8 = Status("passed", QualificationExitCode.Pass, true, LaunchId);
        malformedUtf8[malformedUtf8.AsSpan().IndexOf("passed"u8)] = 0xFF;
        Assert.False(FixtureB1Command.TryParseTerminalStatus(
            malformedUtf8, RunNonce, Launcher, QualificationExitCode.Pass, out _));
    }

    private static void AssertRejectedStatus(
        string outcome, QualificationExitCode exit, bool cleanup, Guid? launchId) =>
        Assert.False(FixtureB1Command.TryParseTerminalStatus(
            Status(outcome, exit, cleanup, launchId), RunNonce, Launcher, exit, out _));

    private static string[] LauncherArguments() =>
    [
        "--opt-in", QualificationConstants.OptInToken,
        "--mode", "artifact-launcher",
        "--nonce", RunNonce.ToString("D"),
        "--executable", Executable,
        "--repo-head", new string('a', 40),
        "--dirty-summary-hash", new string('b', 64),
        "--source-hash", new string('c', 64),
        "--binary-hash", new string('d', 64),
        "--qualification-assembly-hash", new string('e', 64),
        "--capture-isolation-windows-hash", new string('f', 64),
        "--capture-isolation-hash", new string('1', 64),
        "--capture-protocol-hash", new string('2', 64),
    ];

    private static string[] RunnerArguments() =>
    [
        "--opt-in", QualificationConstants.OptInToken,
        "--mode", "artifact-runner",
        "--nonce", RunNonce.ToString("D"),
        "--launch-id", LaunchId.ToString("D"),
        "--control-endpoint", "WDB1-" + new string('a', 32),
    ];

    private static byte[] Status(
        string outcome, QualificationExitCode exit, bool cleanup, Guid? launchId) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            version = 1,
            scope = FixtureB1Command.StatusScope,
            nonce = RunNonce.ToString("D"),
            launchId = launchId?.ToString("D"),
            launcherPid = Launcher.ProcessId,
            launcherCreationFileTime = Launcher.CreationFileTime,
            outcome,
            exitCode = (int)exit,
            cleanupConfirmed = cleanup,
        }) + "\n");
}
