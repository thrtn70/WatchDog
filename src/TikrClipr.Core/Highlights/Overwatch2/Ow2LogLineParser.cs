using System.Text.RegularExpressions;

namespace TikrClipr.Core.Highlights.Overwatch2;

/// <summary>
/// Parses Overwatch 2 Workshop and game log lines into structured events.
/// OW2 Workshop scripts can output structured log lines via "Log To Inspector"
/// and custom game modes. The patterns here cover common Workshop output formats
/// and the native game event log.
/// </summary>
internal static partial class Ow2LogLineParser
{
    public static Ow2LogEvent? ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        // Workshop elimination output: "Player Eliminated Target"
        // or structured format: "[ELIMINATION] PlayerName killed TargetName with Ability"
        var elimMatch = EliminationPattern().Match(line);
        if (elimMatch.Success)
        {
            return new Ow2LogEvent(Ow2LogEventType.Elimination, elimMatch.Groups["detail"].Value);
        }

        // Workshop death output: "[DEATH] PlayerName died" or "Player Died"
        var deathMatch = DeathPattern().Match(line);
        if (deathMatch.Success)
        {
            return new Ow2LogEvent(Ow2LogEventType.Death, deathMatch.Groups["detail"].Value);
        }

        // Assist events: "[ASSIST] PlayerName assisted"
        var assistMatch = AssistPattern().Match(line);
        if (assistMatch.Success)
        {
            return new Ow2LogEvent(Ow2LogEventType.Assist, assistMatch.Groups["detail"].Value);
        }

        // Ultimate activation: "[ULTIMATE] PlayerName activated ultimate"
        var ultMatch = UltimatePattern().Match(line);
        if (ultMatch.Success)
        {
            return new Ow2LogEvent(Ow2LogEventType.UltimateActivated, ultMatch.Groups["detail"].Value);
        }

        // Match state transitions
        if (MatchStartPattern().IsMatch(line))
            return new Ow2LogEvent(Ow2LogEventType.MatchStart);

        if (MatchEndPattern().IsMatch(line))
            return new Ow2LogEvent(Ow2LogEventType.MatchEnd);

        // Round transitions
        if (RoundEndPattern().IsMatch(line))
            return new Ow2LogEvent(Ow2LogEventType.RoundEnd);

        // Score updates: "[SCORE] Team1: 2 - Team2: 1"
        var scoreMatch = ScorePattern().Match(line);
        if (scoreMatch.Success)
        {
            return new Ow2LogEvent(Ow2LogEventType.ScoreUpdate,
                $"{scoreMatch.Groups["team1"].Value}-{scoreMatch.Groups["team2"].Value}");
        }

        // Multikill: "DOUBLE KILL", "TRIPLE KILL", "QUADRUPLE KILL", "QUINTUPLE KILL", "SEXTUPLE KILL"
        var multikillMatch = MultikillPattern().Match(line);
        if (multikillMatch.Success)
        {
            return new Ow2LogEvent(Ow2LogEventType.Multikill, multikillMatch.Groups["type"].Value);
        }

        return null;
    }

    // Regex patterns for OW2 log line formats
    // These cover Workshop structured output and common game event patterns

    [GeneratedRegex(@"\[ELIMINATION\]\s*(?<detail>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex EliminationPattern();

    [GeneratedRegex(@"\[DEATH\]\s*(?<detail>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex DeathPattern();

    [GeneratedRegex(@"\[ASSIST\]\s*(?<detail>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex AssistPattern();

    [GeneratedRegex(@"\[ULTIMATE\]\s*(?<detail>.+)", RegexOptions.IgnoreCase)]
    private static partial Regex UltimatePattern();

    [GeneratedRegex(@"\[MATCH[_\s]?START\]", RegexOptions.IgnoreCase)]
    private static partial Regex MatchStartPattern();

    [GeneratedRegex(@"\[MATCH[_\s]?END\]", RegexOptions.IgnoreCase)]
    private static partial Regex MatchEndPattern();

    [GeneratedRegex(@"\[ROUND[_\s]?END\]", RegexOptions.IgnoreCase)]
    private static partial Regex RoundEndPattern();

    [GeneratedRegex(@"\[SCORE\]\s*.*?:\s*(?<team1>\d+)\s*[-–]\s*.*?:\s*(?<team2>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScorePattern();

    [GeneratedRegex(@"(?<type>DOUBLE|TRIPLE|QUADRUPLE|QUINTUPLE|SEXTUPLE)\s*KILL", RegexOptions.IgnoreCase)]
    private static partial Regex MultikillPattern();
}

internal sealed record Ow2LogEvent(
    Ow2LogEventType Type,
    string? Details = null);

internal enum Ow2LogEventType
{
    Elimination,
    Death,
    Assist,
    UltimateActivated,
    Multikill,
    MatchStart,
    MatchEnd,
    RoundEnd,
    ScoreUpdate,
}
