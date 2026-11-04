using WatchDog.Core.ClipEditor;
using WatchDog.Core.Settings;

namespace WatchDog.Core.Storage;

/// <summary>
/// Explains, once, that clips an older version indexed are never deleted automatically and do
/// not count toward the quota (see <see cref="ClipMetadata.AutoDeleteEligible"/>).
/// </summary>
public static class ExistingClipsNotice
{
    /// <returns>The notice text, or null when no clip is protected or the user dismissed it.</returns>
    public static string? Create(IReadOnlyCollection<ClipMetadata> clips, StorageSettings storage)
    {
        ArgumentNullException.ThrowIfNull(clips);
        ArgumentNullException.ThrowIfNull(storage);
        if (storage.ExistingClipsNoticeDismissed)
            return null;

        var protectedCount = clips.Count(clip => !clip.AutoDeleteEligible);
        if (protectedCount == 0)
            return null;

        var existing = protectedCount == 1
            ? "the clip you already had, and it doesn't"
            : $"the {protectedCount} clips you already had, and they don't";
        var limit = $"{storage.MaxStorageGb} GB storage limit";
        var rules = storage.AutoDeleteDays > 0
            ? $"Auto-delete after {storage.AutoDeleteDays} {(storage.AutoDeleteDays == 1 ? "day" : "days")} and the {limit} apply"
            : $"The {limit} applies";
        var pronoun = storage.AutoDeleteDays > 0 ? "them" : "it";

        return $"WatchDog won't automatically delete {existing} count toward your storage limit. " +
            $"{rules} only to clips you record from now on. You can change {pronoun} in Settings > Storage.";
    }
}
