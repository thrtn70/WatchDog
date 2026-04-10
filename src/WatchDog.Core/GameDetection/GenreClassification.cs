namespace WatchDog.Core.GameDetection;

/// <summary>
/// Determines recording mode recommendations based on game genre.
/// Used by the game-launch smart toast and the profiles UI.
/// </summary>
public static class GenreClassification
{
    /// <summary>
    /// Whether this genre supports highlight detection (native or AI audio).
    /// FPS, Battle Royale, MOBA, and Fighting games have strong audio feedback.
    /// </summary>
    public static bool SupportsHighlights(GameGenre genre) => genre switch
    {
        GameGenre.FPS => true,
        GameGenre.BattleRoyale => true,
        GameGenre.MOBA => true,
        GameGenre.Fighting => true,
        // Survival games often have combat — offer highlights with lower confidence
        GameGenre.Survival => true,
        // RPGs with action combat (Elden Ring, Diablo) can benefit
        GameGenre.RPG => true,
        // Sports/horror — mixed results, offer with caveat
        GameGenre.Sports => true,
        GameGenre.Horror => true,
        // Strategy, racing, sandbox, MMO, platformer — highlights unreliable
        GameGenre.Strategy => false,
        GameGenre.Racing => false,
        GameGenre.Sandbox => false,
        GameGenre.MMO => false,
        GameGenre.Platformer => false,
        GameGenre.Sim => false,
        // Unknown — offer AI highlights with accuracy caveat
        GameGenre.Unknown => true,
        _ => true,
    };

    /// <summary>
    /// Recommended default recording mode for a genre when no profile exists.
    /// </summary>
    public static Settings.GameRecordingMode DefaultMode(GameGenre genre) =>
        SupportsHighlights(genre)
            ? Settings.GameRecordingMode.Highlight
            : Settings.GameRecordingMode.ReplayBuffer;

    /// <summary>
    /// User-facing description of why highlights may not work for this genre.
    /// Returns null if highlights are supported.
    /// </summary>
    public static string? GetHighlightCaveat(GameGenre genre) => genre switch
    {
        GameGenre.Strategy => "Strategy games lack the audio cues needed for reliable highlight detection.",
        GameGenre.Racing => "Racing games don't have distinct combat audio for highlight detection.",
        GameGenre.Sandbox => "Sandbox games have varied audio that makes highlight detection unreliable.",
        GameGenre.MMO => "MMO gameplay is too varied for reliable automatic highlight detection.",
        GameGenre.Platformer => "Platformer audio patterns don't map well to highlight events.",
        GameGenre.Sim => "Simulation games lack distinct combat audio for highlight detection.",
        _ => null,
    };
}
