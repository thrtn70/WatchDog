using WatchDog.Core.ClipEditor;

namespace WatchDog.Core.Storage;

/// <summary>
/// v1.2.x and v1.3.0 compared index paths as plain strings, so a replay indexed from
/// OBS's save event (forward slashes) and again from the folder scan (backslashes)
/// was stored twice. Loading merges those entries instead of rejecting the library.
/// </summary>
internal static class ClipIndexDuplicateMerger
{
    /// <param name="clips">Validated entries whose paths are already canonical.</param>
    public static ClipIndexMergeResult Merge(IReadOnlyList<ClipMetadata> clips)
    {
        var groups = new Dictionary<string, List<ClipMetadata>>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<List<ClipMetadata>>();
        foreach (var clip in clips)
        {
            if (!groups.TryGetValue(clip.FilePath, out var group))
            {
                group = [];
                groups.Add(clip.FilePath, group);
                ordered.Add(group);
            }
            group.Add(clip);
        }

        if (ordered.Count == clips.Count)
            return new ClipIndexMergeResult(clips, 0);
        return new ClipIndexMergeResult([.. ordered.Select(MergeGroup)], clips.Count - ordered.Count);
    }

    // The first entry in index order supplies the file facts. A user edit on any copy
    // survives, and attribution comes from the first copy that has it.
    private static ClipMetadata MergeGroup(IReadOnlyList<ClipMetadata> group)
    {
        if (group.Count == 1)
            return group[0];

        return group[0] with
        {
            GameName = group.Select(clip => clip.GameName).FirstOrDefault(value => value is not null),
            CreatedAt = group.Min(clip => clip.CreatedAt),
            ThumbnailPath = group.Select(clip => clip.ThumbnailPath).FirstOrDefault(value => value is not null),
            IsFavorite = group.Any(clip => clip.IsFavorite),
            HighlightType = group.Select(clip => clip.HighlightType).FirstOrDefault(value => value is not null),
            SessionId = group.Select(clip => clip.SessionId).FirstOrDefault(value => value is not null),
            CaptureRequestId = group.Select(clip => clip.CaptureRequestId).FirstOrDefault(value => value is not null),
            CaptureGeneration = group.Select(clip => clip.CaptureGeneration).FirstOrDefault(value => value is not null),
            MatchNumber = group.Select(clip => clip.MatchNumber).FirstOrDefault(value => value is not null),
            Tags = [.. group.SelectMany(clip => clip.Tags).Distinct(StringComparer.OrdinalIgnoreCase)],
            HighlightSource = group.Select(clip => clip.HighlightSource).FirstOrDefault(value => value is not null),
        };
    }
}

internal sealed record ClipIndexMergeResult(IReadOnlyList<ClipMetadata> Clips, int MergedCount);
