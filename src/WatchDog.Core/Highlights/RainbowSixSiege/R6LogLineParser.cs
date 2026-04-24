using System.Text.RegularExpressions;

namespace WatchDog.Core.Highlights.RainbowSixSiege;

public static partial class R6LogLineParser
{
    private static readonly Regex ScoreRegex = CreateScoreRegex();

    public static R6LogEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();

        if (trimmed.StartsWith("[KILL]", StringComparison.OrdinalIgnoreCase))
            return new R6LogEvent(R6LogEventType.Kill);

        if (trimmed.StartsWith("[DEATH]", StringComparison.OrdinalIgnoreCase))
            return new R6LogEvent(R6LogEventType.Death);

        if (trimmed.StartsWith("[ASSIST]", StringComparison.OrdinalIgnoreCase))
            return new R6LogEvent(R6LogEventType.Assist);

        if (trimmed.StartsWith("[ROUND_START]", StringComparison.OrdinalIgnoreCase))
            return new R6LogEvent(R6LogEventType.RoundStart);

        if (trimmed.StartsWith("[ROUND_END]", StringComparison.OrdinalIgnoreCase))
            return new R6LogEvent(R6LogEventType.RoundEnd);

        if (trimmed.StartsWith("[MATCH_END]", StringComparison.OrdinalIgnoreCase))
            return new R6LogEvent(R6LogEventType.MatchEnd);

        var scoreMatch = ScoreRegex.Match(trimmed);
        if (scoreMatch.Success)
            return new R6LogEvent(R6LogEventType.ScoreUpdate,
                $"{scoreMatch.Groups[1].Value}-{scoreMatch.Groups[2].Value}");

        if (trimmed.Contains("bomb has been planted", StringComparison.OrdinalIgnoreCase))
            return new R6LogEvent(R6LogEventType.BombPlant);

        if (trimmed.Contains("bomb has been defused", StringComparison.OrdinalIgnoreCase))
            return new R6LogEvent(R6LogEventType.BombDefuse);

        return null;
    }

    [GeneratedRegex(@"\[SCORE\]\s*Attackers:\s*(\d+)\s*-\s*Defenders:\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CreateScoreRegex();
}
