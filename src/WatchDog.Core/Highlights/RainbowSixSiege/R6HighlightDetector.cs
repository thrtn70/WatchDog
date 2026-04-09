using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Highlights.RainbowSixSiege;

public sealed class R6HighlightDetector : LogFileHighlightDetector
{
    private R6GameState _state = new();

    public override string GameExecutableName => "rainbowsix.exe";
    public override IReadOnlyList<string> SupportedExecutableNames =>
        ["rainbowsix.exe", "r6-siege.exe"];

    protected override string LogDirectoryPath => ResolveLogDirectory();

    protected override string LogFilePattern => "*.log";

    public R6HighlightDetector(ILogger<R6HighlightDetector> logger) : base(logger) { }

    protected override void ProcessLogLine(string line)
    {
        var evt = R6LogLineParser.ParseLine(line);
        if (evt is null) return;

        switch (evt.Type)
        {
            case R6LogEventType.Kill:
                _state = _state with
                {
                    Kills = _state.Kills + 1,
                    RoundKills = _state.RoundKills + 1
                };
                RaiseHighlight(HighlightType.Kill, evt.Details ?? "Kill");

                // Ace detection (5 kills in R6 = full team wipe in 5v5)
                if (_state.RoundKills >= 5)
                {
                    RaiseHighlight(HighlightType.Ace,
                        $"ACE! {_state.RoundKills} kills in one round");
                }
                // Multikill (3+ kills in a round)
                else if (_state.RoundKills >= 3)
                {
                    RaiseHighlight(HighlightType.Multikill,
                        $"{_state.RoundKills}K round");
                }
                break;

            case R6LogEventType.Death:
                _state = _state with { Deaths = _state.Deaths + 1 };
                RaiseHighlight(HighlightType.Death, evt.Details);
                break;

            case R6LogEventType.Assist:
                _state = _state with { Assists = _state.Assists + 1 };
                RaiseHighlight(HighlightType.Assist, evt.Details);
                break;

            case R6LogEventType.BombPlant:
                RaiseHighlight(HighlightType.BombPlant, "Bomb planted");
                break;

            case R6LogEventType.BombDefuse:
                RaiseHighlight(HighlightType.BombDefuse, "Bomb defused");
                break;

            case R6LogEventType.RoundStart:
                _state = _state with { RoundKills = 0 };
                break;

            case R6LogEventType.RoundEnd:
                _state = _state with { RoundKills = 0 };
                break;

            case R6LogEventType.ScoreUpdate when evt.Details is not null:
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

            case R6LogEventType.MatchEnd:
                if (_state.TeamScore > _state.EnemyScore)
                    RaiseHighlight(HighlightType.MatchWin,
                        $"Match won {_state.TeamScore}-{_state.EnemyScore}");
                else
                    RaiseHighlight(HighlightType.MatchLoss,
                        $"Match lost {_state.TeamScore}-{_state.EnemyScore}");

                _state = new R6GameState();
                break;
        }
    }

    private static string ResolveLogDirectory()
    {
        // Primary: Ubisoft Connect logs
        var ubisoftPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Ubisoft Game Launcher", "logs");

        if (Directory.Exists(ubisoftPath))
            return ubisoftPath;

        // Fallback: User's Documents R6 folder
        var docsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "My Games", "Rainbow Six - Siege");

        if (Directory.Exists(docsPath))
            return docsPath;

        return ubisoftPath;
    }
}
