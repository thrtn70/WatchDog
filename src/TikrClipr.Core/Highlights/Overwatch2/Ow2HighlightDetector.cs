using Microsoft.Extensions.Logging;

namespace TikrClipr.Core.Highlights.Overwatch2;

public sealed class Ow2HighlightDetector : LogFileHighlightDetector
{
    private Ow2GameState _state = new();

    public override string GameExecutableName => "overwatch.exe";
    public override IReadOnlyList<string> SupportedExecutableNames => ["overwatch.exe"];

    protected override string LogDirectoryPath => ResolveLogDirectory();

    protected override string LogFilePattern => "*.log";

    public Ow2HighlightDetector(ILogger<Ow2HighlightDetector> logger) : base(logger) { }

    protected override void ProcessLogLine(string line)
    {
        var evt = Ow2LogLineParser.ParseLine(line);
        if (evt is null) return;

        switch (evt.Type)
        {
            case Ow2LogEventType.Elimination:
                _state = _state with
                {
                    Eliminations = _state.Eliminations + 1,
                    RoundEliminations = _state.RoundEliminations + 1
                };
                RaiseHighlight(HighlightType.Kill, evt.Details ?? "Elimination");

                // Team wipe: exactly 5 eliminations in 5v5 (fires once per round)
                if (_state.RoundEliminations == 5)
                {
                    RaiseHighlight(HighlightType.Ace,
                        $"Team wipe! {_state.RoundEliminations} eliminations");
                }
                break;

            case Ow2LogEventType.Death:
                _state = _state with { Deaths = _state.Deaths + 1 };
                RaiseHighlight(HighlightType.Death, evt.Details);
                break;

            case Ow2LogEventType.Assist:
                _state = _state with { Assists = _state.Assists + 1 };
                RaiseHighlight(HighlightType.Assist, evt.Details);
                break;

            case Ow2LogEventType.UltimateActivated:
                RaiseHighlight(HighlightType.UltimateUsed, evt.Details ?? "Ultimate activated");
                break;

            case Ow2LogEventType.Multikill:
                RaiseHighlight(HighlightType.Multikill, evt.Details ?? "Multi-kill");
                break;

            case Ow2LogEventType.RoundEnd:
                // Reset round-scoped counters
                _state = _state with { RoundEliminations = 0 };
                break;

            case Ow2LogEventType.ScoreUpdate when evt.Details is not null:
                var scores = evt.Details.Split('-', '\u2013');
                if (scores.Length == 2 &&
                    int.TryParse(scores[0].Trim(), out var t1) &&
                    int.TryParse(scores[1].Trim(), out var t2))
                {
                    var prevTeam = _state.TeamScore;
                    var prevEnemy = _state.EnemyScore;
                    _state = _state with { TeamScore = t1, EnemyScore = t2 };

                    if (t1 > prevTeam)
                        RaiseHighlight(HighlightType.RoundWin, $"Round won ({t1}-{t2})");
                    else if (t2 > prevEnemy)
                        RaiseHighlight(HighlightType.RoundLoss, $"Round lost ({t1}-{t2})");
                }
                break;

            case Ow2LogEventType.MatchEnd:
                if (_state.TeamScore > _state.EnemyScore)
                    RaiseHighlight(HighlightType.MatchWin,
                        $"Match won {_state.TeamScore}-{_state.EnemyScore}");
                else
                    RaiseHighlight(HighlightType.MatchLoss,
                        $"Match lost {_state.TeamScore}-{_state.EnemyScore}");

                // Reset state for next match
                _state = new Ow2GameState();
                break;

            case Ow2LogEventType.MatchStart:
                _state = new Ow2GameState();
                break;
        }
    }

    private static string ResolveLogDirectory()
    {
        // Primary: OW2 Workshop output
        var workshopPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Overwatch", "Workshop");

        if (Directory.Exists(workshopPath))
            return workshopPath;

        // Fallback: Battle.net logs
        var battleNetPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Battle.net", "Logs");

        if (Directory.Exists(battleNetPath))
            return battleNetPath;

        // Last resort — return primary path (StartAsync will handle missing directory)
        return workshopPath;
    }
}
