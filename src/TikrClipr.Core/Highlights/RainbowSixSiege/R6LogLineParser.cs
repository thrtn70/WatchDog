using System.Text.RegularExpressions;

namespace TikrClipr.Core.Highlights.RainbowSixSiege;

/// <summary>
/// Parses Rainbow Six Siege game and Ubisoft Connect log lines into structured events.
/// R6 Siege writes gameplay events to log files in the Ubisoft Connect logs directory
/// and the game's own log output. Patterns cover common log formats from
/// Ubisoft's game telemetry output.
/// </summary>
internal static partial class R6LogLineParser
{
    public static R6LogEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // Kill events: "[KILL] Player killed Target" or structured game telemetry
        var killMatch = KillPattern().Match(line);
        if (killMatch.Success)
        {
            return new R6LogEvent(R6LogEventType.Kill, killMatch.Groups["detail"].Value);
        }

        // Death events: "[DEATH] Player died" or "Player was killed by Target"
        var deathMatch = DeathPattern().Match(line);
        if (deathMatch.Success)
        {
            return new R6LogEvent(R6LogEventType.Death, deathMatch.Groups["detail"].Value);
        }

        // Assist events: "[ASSIST] Player assisted"
        var assistMatch = AssistPattern().Match(line);
        if (assistMatch.Success)
        {
            return new R6LogEvent(R6LogEventType.Assist, assistMatch.Groups["detail"].Value);
        }

        // Objective events: bomb planted, defused
        if (BombPlantPattern().IsMatch(line))
            return new R6LogEvent(R6LogEventType.BombPlant, "Bomb planted");

        if (BombDefusePattern().IsMatch(line))
            return new R6LogEvent(R6LogEventType.BombDefuse, "Bomb defused");

        // Round transitions
        if (RoundStartPattern().IsMatch(line))
            return new R6LogEvent(R6LogEventType.RoundStart);

        if (RoundEndPattern().IsMatch(line))
            return new R6LogEvent(R6LogEventType.RoundEnd);

        // Match end
        if (MatchEndPattern().IsMatch(line))
            return new R6LogEvent(R6LogEventType.MatchEnd);

        // Score updates: "Score: 3-2" or "[SCORE] Team1: 3 - Team2: 2"
        var scoreMatch = ScorePattern().Match(line);
        if (scoreMatch.Success)
        {
            return new R6LogEvent(R6LogEventType.ScoreUpdate,
                $"{scoreMatch.Groups["team1"].Value}-{scoreMatch.Groups["team2"].Value}");
        }

        return null;
    }

    [GeneratedRegex(@"(?:\[KILL\]|killed|eliminated)\s*(?<detail>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex KillPattern();

    [GeneratedRegex(@"(?:\[DEATH\]|died|was killed)\s*(?<detail>.*)", RegexOptions.IgnoreCase)]
    private static partial Regex DeathPattern();

    [GeneratedRegex(@"\[ASSIST\]\s*(?<detail>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex AssistPattern();

    [GeneratedRegex(@"bomb\s*(?:has been\s*)?plant", RegexOptions.IgnoreCase)]
    private static partial Regex BombPlantPattern();

    [GeneratedRegex(@"bomb\s*(?:has been\s*)?defuse", RegexOptions.IgnoreCase)]
    private static partial Regex BombDefusePattern();

    [GeneratedRegex(@"\[ROUND[_\s]?START\]|round\s+(?:has\s+)?start", RegexOptions.IgnoreCase)]
    private static partial Regex RoundStartPattern();

    [GeneratedRegex(@"\[ROUND[_\s]?END\]|round\s+(?:has\s+)?end", RegexOptions.IgnoreCase)]
    private static partial Regex RoundEndPattern();

    [GeneratedRegex(@"\[MATCH[_\s]?END\]|match\s+(?:has\s+)?end", RegexOptions.IgnoreCase)]
    private static partial Regex MatchEndPattern();

    [GeneratedRegex(@"(?:\[SCORE\]|score[:\s])\s*.*?(?<team1>\d+)\s*[-–]\s*(?<team2>\d+)", RegexOptions.IgnoreCase)]
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
