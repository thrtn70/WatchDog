namespace TikrClipr.Core.Highlights;

public sealed record HighlightConfig
{
    public bool Enabled { get; init; } = true;
    public int PostEventDelaySeconds { get; init; } = 15;
    public int CooldownSeconds { get; init; } = 5;
    public HashSet<HighlightType> EnabledTypes { get; init; } =
    [
        HighlightType.Kill,
        HighlightType.Death,
        HighlightType.RoundWin,
        HighlightType.MatchWin,
        HighlightType.Ace,
    ];
}
