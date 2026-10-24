using WatchDog.Core.Buffer;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.ReplayBuffer;

public sealed class ReplaySaveCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BlockedFilesystemValidationDoesNotBlockCancellationOrDisposal(bool dispose)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var saves = new ReplaySaveCoordinator(_ => { entered.Set(); release.Wait(); return true; });
        var save = saves.SaveAsync(() => { }, cancellation.Token);
        try
        {
            saves.Complete("clip.mp4");
            Assert.True(entered.Wait(TestTimeouts.Signal));
            if (dispose)
            {
                saves.Dispose();
                await Assert.ThrowsAsync<ObjectDisposedException>(() => save.WaitAsync(TestTimeouts.Signal));
            }
            else
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save.WaitAsync(TestTimeouts.Signal));
            }
        }
        finally { release.Set(); }
    }

    [Fact]
    public async Task SaveWaitsForCompletionRatherThanPreviouslySavedPath()
    {
        using var saves = new ReplaySaveCoordinator(_ => true);
        saves.Complete("previous.mp4");
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = saves.SaveAsync(() => requested.SetResult(), CancellationToken.None);
        await requested.Task;
        Assert.False(save.IsCompleted);
        saves.Complete("new.mp4");
        Assert.Equal("new.mp4", await save);
    }

    [Fact]
    public async Task ConcurrentSavesAreSerializedAndDuplicateSignalsCannotCompleteNextSave()
    {
        using var saves = new ReplaySaveCoordinator(_ => true);
        var requests = 0;
        var secondRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = saves.SaveAsync(() => Interlocked.Increment(ref requests), CancellationToken.None);
        var second = saves.SaveAsync(() => { Interlocked.Increment(ref requests); secondRequested.SetResult(); }, CancellationToken.None);
        Assert.Equal(1, requests);
        saves.Complete("first.mp4");
        Assert.Equal("first.mp4", await first);
        await secondRequested.Task;
        saves.Complete("first.mp4");
        Assert.False(second.IsCompleted);
        saves.Complete("second.mp4");
        Assert.Equal("second.mp4", await second);
    }

    [Fact]
    public async Task InvalidOutputIsNotReportedAsSaved()
    {
        using var saves = new ReplaySaveCoordinator(_ => false);
        var save = saves.SaveAsync(() => { }, CancellationToken.None);
        saves.Complete("empty.mp4");
        await Assert.ThrowsAsync<IOException>(() => save);
    }

    [Fact]
    public async Task CancelledDispatchedSaveCannotBeMistakenForLaterRequest()
    {
        using var saves = new ReplaySaveCoordinator(_ => true);
        using var cancellation = new CancellationTokenSource();
        var save = saves.SaveAsync(() => { }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => save);
        saves.Complete("late.mp4");
        await Assert.ThrowsAsync<InvalidOperationException>(() => saves.SaveAsync(() => { }, CancellationToken.None));
    }

    [Fact]
    public async Task DisposeReleasesPendingSaveWithoutSuccess()
    {
        var saves = new ReplaySaveCoordinator(_ => true);
        var save = saves.SaveAsync(() => { }, CancellationToken.None);
        saves.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => save);
    }

    [Fact]
    public async Task TimeoutRequiresBufferRestartBeforeAnotherSave()
    {
        using var saves = new ReplaySaveCoordinator(_ => true, TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAsync<TimeoutException>(() => saves.SaveAsync(() => { }, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => saves.SaveAsync(() => { }, CancellationToken.None));
    }
}
