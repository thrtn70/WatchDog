using WatchDog.Core.ClipEditor;
using WatchDog.Core.Settings;
using WatchDog.Core.Storage;

namespace WatchDog.Core.Tests.Storage;

public sealed class ExistingClipsNoticeTests
{
    private static readonly StorageSettings Defaults = new() { AutoDeleteDays = 30, MaxStorageGb = 50 };

    [Fact]
    public void NoNoticeWhenEveryClipWasRecordedByThisVersion()
    {
        Assert.Null(ExistingClipsNotice.Create([], Defaults));
        Assert.Null(ExistingClipsNotice.Create([Clip(eligible: true), Clip(eligible: true)], Defaults));
    }

    [Fact]
    public void NoNoticeOnceDismissed()
    {
        var dismissed = Defaults with { ExistingClipsNoticeDismissed = true };

        Assert.Null(ExistingClipsNotice.Create([Clip(eligible: false)], dismissed));
    }

    [Fact]
    public void NoticeCountsOnlyProtectedClipsAndNamesBothSettings()
    {
        var clips = new[] { Clip(eligible: false), Clip(eligible: false), Clip(eligible: false), Clip(eligible: true) };

        Assert.Equal(
            "WatchDog won't automatically delete the 3 clips you already had, and they don't count toward " +
            "your storage limit. Auto-delete after 30 days and the 50 GB storage limit apply only to clips " +
            "you record from now on. You can change them in Settings > Storage.",
            ExistingClipsNotice.Create(clips, Defaults));
    }

    [Fact]
    public void NoticeLeavesOutAutoDeleteWhenItIsOff()
    {
        var ageOff = Defaults with { AutoDeleteDays = 0 };

        Assert.Equal(
            "WatchDog won't automatically delete the clip you already had, and it doesn't count toward " +
            "your storage limit. The 50 GB storage limit applies only to clips you record from now on. " +
            "You can change it in Settings > Storage.",
            ExistingClipsNotice.Create([Clip(eligible: false)], ageOff));
    }

    [Fact]
    public void NoticeUsesSingularDay()
    {
        var oneDay = Defaults with { AutoDeleteDays = 1 };

        Assert.Contains("Auto-delete after 1 day and", ExistingClipsNotice.Create([Clip(eligible: false)], oneDay));
    }

    private static ClipMetadata Clip(bool eligible) => new()
    {
        FilePath = $@"C:\Clips\{Guid.NewGuid():N}.mp4",
        FileName = "clip.mp4",
        CreatedAt = DateTimeOffset.UtcNow,
        AutoDeleteEligible = eligible,
    };
}
