using WatchDog.Core.Capture;

namespace WatchDog.Core.GameDetection;

/// <summary>
/// Compares immutable process identities without querying live process state.
/// A PID without a captured start time is never an exact identity.
/// </summary>
public static class GameProcessIdentity
{
    public static bool Matches(GameInfo game, GameCaptureTarget target) =>
        Matches(game.ProcessId, game.ProcessStartedAt, game.ExecutableName, target);

    public static bool Matches(
        int processId,
        DateTimeOffset? processStartedAt,
        string executableName,
        GameCaptureTarget target) =>
        processId == target.ProcessId &&
        processStartedAt is not null &&
        target.ProcessStartedAt is not null &&
        processStartedAt == target.ProcessStartedAt &&
        ExecutableNamesEqual(executableName, target.ExecutableName);

    private static bool ExecutableNamesEqual(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;

        return string.Equals(
            Path.GetFileName(left),
            Path.GetFileName(right),
            StringComparison.OrdinalIgnoreCase);
    }
}
