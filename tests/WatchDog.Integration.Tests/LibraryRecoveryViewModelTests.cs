using WatchDog.App.ViewModels;
using WatchDog.Core.Storage;

namespace WatchDog.Integration.Tests;

public sealed class LibraryRecoveryViewModelTests
{
    private const string PrimaryEvidence = @"E:\Videos\WatchDog\clips-index.json.corrupt-20261022181500000-0a1b";
    private const string BackupEvidence = @"E:\Videos\WatchDog\clips-index.json.bak.corrupt-20261022181500000-2c3d";

    [Fact]
    public void AvailableLibraryShowsNoBannerAndCannotRebuild()
    {
        var viewModel = Create(new FakeHealth(ClipLibraryStatus.Available), new FakeRebuild());

        Assert.False(viewModel.IsLibraryUnavailable);
        Assert.False(viewModel.IsBannerVisible);
        Assert.False(viewModel.RebuildLibraryCommand.CanExecute(null));
    }

    [Fact]
    public void UnavailableLibraryBannerNamesTheEvidenceAndSaysCaptureContinues()
    {
        var viewModel = Create(new FakeHealth(Corrupt()), new FakeRebuild());

        Assert.True(viewModel.IsLibraryUnavailable);
        Assert.True(viewModel.IsBannerVisible);
        Assert.Contains(PrimaryEvidence, viewModel.BannerText);
        Assert.Contains(BackupEvidence, viewModel.BannerText);
        Assert.Contains("still being saved", viewModel.BannerText);
        Assert.True(viewModel.RebuildLibraryCommand.CanExecute(null));
    }

    [Fact]
    public async Task DeclinedConfirmationNeverRebuilds()
    {
        var rebuild = new FakeRebuild();
        string? prompt = null;
        var viewModel = Create(new FakeHealth(Corrupt()), rebuild, confirm: text =>
        {
            prompt = text;
            return false;
        });

        await viewModel.RebuildLibraryCommand.ExecuteAsync(null);

        Assert.Equal(0, rebuild.Calls);
        Assert.True(viewModel.IsLibraryUnavailable);
        Assert.NotNull(prompt);
        Assert.Contains("Favorites and tags", prompt);
        Assert.Contains(PrimaryEvidence, prompt);
    }

    [Fact]
    public async Task PromptForAnIndexThatMayStillBeValidPromisesNotToReplaceIt()
    {
        string? prompt = null;
        var unreadable = new ClipLibraryStatus(false, "The clip index could not be read.", [], false);
        var viewModel = Create(new FakeHealth(unreadable), new FakeRebuild(), confirm: text =>
        {
            prompt = text;
            return false;
        });

        await viewModel.RebuildLibraryCommand.ExecuteAsync(null);

        Assert.NotNull(prompt);
        Assert.Contains("will not replace", prompt);
        Assert.DoesNotContain("Favorites and tags", prompt);
    }

    [Fact]
    public async Task ConfirmedRebuildRunsOnceHidesTheBannerAndAnnouncesTheResult()
    {
        var health = new FakeHealth(Corrupt());
        var rebuild = new FakeRebuild
        {
            Handler = () =>
            {
                health.Status = ClipLibraryStatus.Available;
                return new ClipLibraryRebuildResult(ClipLibraryStatus.Available, 12, 2, null);
            },
        };
        var notices = new List<(string Message, bool IsError)>();
        var restored = 0;
        var viewModel = Create(health, rebuild, notify: (message, isError) => notices.Add((message, isError)));
        viewModel.LibraryRestored += () => restored++;

        await viewModel.RebuildLibraryCommand.ExecuteAsync(null);

        Assert.Equal(1, rebuild.Calls);
        Assert.True(rebuild.LastAllowNewIndex);
        Assert.False(viewModel.IsLibraryUnavailable);
        Assert.False(viewModel.IsBannerVisible);
        Assert.False(viewModel.IsRebuilding);
        Assert.Equal(1, restored);
        var notice = Assert.Single(notices);
        Assert.False(notice.IsError);
        Assert.Contains("12", notice.Message);
        Assert.False(viewModel.RebuildLibraryCommand.CanExecute(null));
    }

    [Fact]
    public async Task FailedRebuildKeepsTheBannerAndReportsWhy()
    {
        var rebuild = new FakeRebuild
        {
            Handler = () => new ClipLibraryRebuildResult(Corrupt(), 0, 0, "sessions index is locked"),
        };
        var notices = new List<(string Message, bool IsError)>();
        var restored = 0;
        var viewModel = Create(new FakeHealth(Corrupt()), rebuild, notify: (message, isError) => notices.Add((message, isError)));
        viewModel.LibraryRestored += () => restored++;

        await viewModel.RebuildLibraryCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsLibraryUnavailable);
        Assert.Equal(0, restored);
        var notice = Assert.Single(notices);
        Assert.True(notice.IsError);
        Assert.Contains("sessions index is locked", notice.Message);
        Assert.True(viewModel.RebuildLibraryCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConfirmedRetryOfAnIndexThatMayStillBeValidNeverAllowsANewIndex()
    {
        var rebuild = new FakeRebuild();
        var unreadable = new ClipLibraryStatus(false, "The clip index could not be read.", [], false);
        var viewModel = Create(new FakeHealth(unreadable), rebuild);

        await viewModel.RebuildLibraryCommand.ExecuteAsync(null);

        Assert.Equal(1, rebuild.Calls);
        Assert.False(rebuild.LastAllowNewIndex);
    }

    [Fact]
    public async Task IncompleteRebuildKeepsTheBannerAndOffersToScanAgain()
    {
        var health = new FakeHealth(Corrupt());
        var rebuild = new FakeRebuild
        {
            Handler = () =>
            {
                health.Status = ClipLibraryStatus.RebuildIncomplete;
                return new ClipLibraryRebuildResult(
                    ClipLibraryStatus.RebuildIncomplete,
                    4,
                    0,
                    "1 file(s) could not be added to the library. broken.mp4: Could not read broken.mp4.");
            },
        };
        var prompts = new List<string>();
        var notices = new List<(string Message, bool IsError)>();
        var restored = 0;
        var viewModel = Create(
            health,
            rebuild,
            confirm: text =>
            {
                prompts.Add(text);
                return true;
            },
            notify: (message, isError) => notices.Add((message, isError)));
        viewModel.LibraryRestored += () => restored++;

        await viewModel.RebuildLibraryCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsLibraryUnavailable);
        Assert.True(viewModel.IsBannerVisible);
        Assert.Contains("broken.mp4", viewModel.BannerText);
        Assert.Equal(1, restored);
        Assert.True(Assert.Single(notices).IsError);
        Assert.True(viewModel.RebuildLibraryCommand.CanExecute(null));

        await viewModel.RebuildLibraryCommand.ExecuteAsync(null);

        Assert.Equal(2, rebuild.Calls);
        Assert.Contains("scan the recordings folder again", prompts[1]);
        Assert.False(rebuild.LastAllowNewIndex);
    }

    [Fact]
    public void StartupAfterAnUnfinishedRebuildShowsTheBanner()
    {
        var viewModel = Create(new FakeHealth(ClipLibraryStatus.RebuildIncomplete), new FakeRebuild());

        Assert.False(viewModel.IsLibraryUnavailable);
        Assert.True(viewModel.IsBannerVisible);
        Assert.Contains("did not finish", viewModel.BannerText);
        Assert.True(viewModel.RebuildLibraryCommand.CanExecute(null));
    }

    [Fact]
    public async Task UnexpectedRebuildExceptionIsReportedInsteadOfEscaping()
    {
        var rebuild = new FakeRebuild { Handler = () => throw new IOException("disk removed") };
        var notices = new List<(string Message, bool IsError)>();
        var viewModel = Create(new FakeHealth(Corrupt()), rebuild, notify: (message, isError) => notices.Add((message, isError)));

        await viewModel.RebuildLibraryCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsLibraryUnavailable);
        Assert.False(viewModel.IsRebuilding);
        Assert.Contains("disk removed", Assert.Single(notices).Message);
    }

    private static LibraryRecoveryViewModel Create(
        FakeHealth health,
        FakeRebuild rebuild,
        Func<string, bool>? confirm = null,
        Action<string, bool>? notify = null) =>
        new(health, rebuild, confirm ?? (_ => true), notify ?? ((_, _) => { }));

    private static ClipLibraryStatus Corrupt() => new(
        false,
        "The clip index and its backup are both damaged.",
        [PrimaryEvidence, BackupEvidence],
        true);

    private sealed class FakeHealth(ClipLibraryStatus status) : IClipLibraryHealth
    {
        public ClipLibraryStatus Status { get; set; } = status;
        public ClipLibraryStatus LibraryStatus => Status;
    }

    private sealed class FakeRebuild : IClipLibraryRebuild
    {
        public int Calls { get; private set; }
        public bool? LastAllowNewIndex { get; private set; }
        public Func<ClipLibraryRebuildResult>? Handler { get; init; }

        public Task<ClipLibraryRebuildResult> RebuildFromFolderAsync(
            bool allowNewIndex,
            CancellationToken ct = default)
        {
            Calls++;
            LastAllowNewIndex = allowNewIndex;
            return Task.FromResult(Handler?.Invoke() ??
                new ClipLibraryRebuildResult(ClipLibraryStatus.Available, 0, 0, null));
        }
    }
}
