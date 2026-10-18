using WatchDog.Core.Capture;
using WatchDog.Core.GameDetection;

namespace WatchDog.Core.Tests.GameDetection;

public sealed class GameProcessIdentityTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Matches_RequiresPidStartTimeAndExecutable()
    {
        var game = Game(42, StartedAt, @"C:\Games\GAME.exe");
        var target = new GameCaptureTarget("game.exe", 42, "game.exe", StartedAt);

        Assert.True(GameProcessIdentity.Matches(game, target));
        Assert.True(GameProcessIdentity.Matches(42, StartedAt, "GAME.EXE", target));
    }

    [Fact]
    public void Matches_RejectsReusedPidWithDifferentStartTime()
    {
        var game = Game(42, StartedAt.AddMinutes(1), "game.exe");
        var target = new GameCaptureTarget("game.exe", 42, "game.exe", StartedAt);

        Assert.False(GameProcessIdentity.Matches(game, target));
    }

    [Fact]
    public void Matches_RejectsDifferentExecutableWithSamePidAndStartTime()
    {
        var game = Game(42, StartedAt, "launcher.exe");
        var target = new GameCaptureTarget("game.exe", 42, "game.exe", StartedAt);

        Assert.False(GameProcessIdentity.Matches(game, target));
    }

    [Fact]
    public void Matches_RejectsMissingStartTime()
    {
        var game = Game(42, null, "game.exe");
        var target = new GameCaptureTarget("game.exe", 42, "game.exe", null);

        Assert.False(GameProcessIdentity.Matches(game, target));
        Assert.False(GameProcessIdentity.Matches(42, null, "game.exe", target));
    }

    private static GameInfo Game(int processId, DateTimeOffset? startedAt, string executableName) => new()
    {
        ExecutableName = executableName,
        DisplayName = "Game",
        ProcessId = processId,
        ProcessStartedAt = startedAt,
    };
}
