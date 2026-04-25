using System.Text.RegularExpressions;

namespace WatchDog.Core.Highlights.RainbowSixSiege;

internal static partial class R6LogLineParser
{
    public static R6LogEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        var killMatch = KillPattern().Match(line);
        if (killMatch.Success)
            return new R6LogEvent(R6LogEventType.Kill);

        var deathMatch = DeathPattern().Match(line);
        if (deathMatch.Success)
            return new R6LogEvent(R6LogEventType.Death);

        var assistMatch = AssistPattern().Match(line);
        if (assistMatch.Success)
            return new R6LogEvent(R6LogEventType.Assist);

        if (BombPlantPattern().IsMatch(line))
            return new R6LogEvent(R6LogEventType.BombPlant);

        if (BombDefusePattern().IsMatch(line))
            return new R6LogEvent(R6LogEventType.BombDefuse);

        if (RoundStartPattern().IsMatch(line))
            return new R6LogEvent(R6LogEventType.RoundStart);

        if (RoundEndPattern().IsMatch(line))
            return new R6LogEvent(R6LogEventType.RoundEnd);

        if (MatchEndPattern().IsMatch(line))
            return new R6LogEvent(R6LogEventType.MatchEnd);

        var scoreMatch = ScorePattern().Match(line);
        if (scoreMatch.Success)
        {
            return new R6LogEvent(R6LogEventType.ScoreUpdate,
                $"{scoreMatch.Groups["team1"].Value}-{scoreMatch.Groups["team2"].Value}");
        }

        return null;
    }

    [GeneratedRegex(@"\[KILL\]", RegexOptions.IgnoreCase)]
    private static partial Regex KillPattern();

    [GeneratedRegex(@"\[DEATH\]", RegexOptions.IgnoreCase)]
    private static partial Regex DeathPattern();

    [GeneratedRegex(@"\[ASSIST\]", RegexOptions.IgnoreCase)]
    private static partial Regex AssistPattern();

    [GeneratedRegex(@"bomb has been planted", RegexOptions.IgnoreCase)]
    private static partial Regex BombPlantPattern();

    [GeneratedRegex(@"bomb has been defused", RegexOptions.IgnoreCase)]
    private static partial Regex BombDefusePattern();

    [GeneratedRegex(@"\[ROUND[_\s]?START\]", RegexOptions.IgnoreCase)]
    private static partial Regex RoundStartPattern();

    [GeneratedRegex(@"\[ROUND[_\s]?END\]", RegexOptions.IgnoreCase)]
    private static partial Regex RoundEndPattern();

    [GeneratedRegex(@"\[MATCH[_\s]?END\]", RegexOptions.IgnoreCase)]
    private static partial Regex MatchEndPattern();

    [GeneratedRegex(@"\[SCORE\]\s*.*?:\s*(?<team1>\d+)\s*[-–]\s*.*?:\s*(?<team2>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScorePattern();
}

internal sealed record R6LogEvent(
    R6LogEventType Type,
    string? Details = null);

internal enum R6LogEventType
{
    Kill,
    Death,
    Assist,
    BombPlant,
    BombDefuse,
    RoundStart,
    RoundEnd,
    MatchEnd,
    ScoreUpdate,
}
