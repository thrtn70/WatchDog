namespace WatchDog.Core.Highlights.Audio;

/// <summary>
/// Maps YAMNet AudioSet class indices to WatchDog highlight types.
/// YAMNet outputs 521 class scores; we only care about game-relevant audio events.
/// Class indices are from the AudioSet ontology.
/// See: https://github.com/tensorflow/models/blob/master/research/audioset/yamnet/yamnet_class_map.csv
/// </summary>
public static class AudioEventMapping
{
    /// <summary>
    /// Evaluate YAMNet output scores and return the best matching highlight event, if any.
    /// Returns null if no game-relevant audio event exceeds the confidence threshold.
    /// </summary>
    public static AudioHighlightCandidate? Evaluate(float[] scores, float threshold = 0.6f)
    {
        // Aggregate scores across related classes for each highlight category.
        // Indices from YAMNet AudioSet ontology:
        // https://github.com/tensorflow/models/blob/master/research/audioset/yamnet/yamnet_class_map.csv
        var killScore = Max(scores,
            421, // Gunshot, gunfire
            422, // Machine gun
            423, // Fusillade
            424, // Artillery fire
            420  // Explosion
        );

        var explosionScore = Max(scores,
            420, // Explosion
            428, // Burst, pop
            430  // Boom
        );

        var crowdCheerScore = Max(scores,
            64,  // Crowd
            61,  // Cheering
            62,  // Applause
            6,   // Shout
            9    // Yell
        );

        var announcerScore = Max(scores,
            0,   // Speech
            2,   // Conversation
            3    // Narration, monologue
        );

        var uiSoundScore = Max(scores,
            477, // Ding
            382, // Alarm
            475, // Beep, bleep
            195  // Bell
        );

        // Find the highest-scoring category above threshold
        var candidates = new (float Score, HighlightType Type, string Description)[]
        {
            (killScore, HighlightType.Kill, "Gunfire detected"),
            (explosionScore, HighlightType.Kill, "Explosion detected"),
            (crowdCheerScore, HighlightType.RoundWin, "Crowd/cheer detected"),
        };

        AudioHighlightCandidate? best = null;
        foreach (var (score, type, desc) in candidates)
        {
            if (score >= threshold && (best is null || score > best.Confidence))
            {
                best = new AudioHighlightCandidate(type, desc, score);
            }
        }

        return best;
    }

    private static float Max(float[] scores, params int[] indices)
    {
        var max = 0f;
        foreach (var i in indices)
        {
            if (i >= 0 && i < scores.Length && scores[i] > max)
                max = scores[i];
        }
        return max;
    }
}

public sealed record AudioHighlightCandidate(
    HighlightType Type,
    string Description,
    float Confidence);
