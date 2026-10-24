using WatchDog.CaptureIsolation.Windows;
using WatchDog.CaptureIsolation.Windows.Qualification;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixturePendingFileCleanupTests
{
    [Fact]
    public void MarkFailureRetainsHandleUntilRetrySucceeds()
    {
        var closer = new FakeCloser();
        var handle = NewHandle(closer);
        var marks = 0;
        var owner = new FixturePendingFileCleanup(new object(), _ => ++marks > 1);

        Assert.False(owner.TryCleanup(handle));
        Assert.Equal(1, owner.PendingCount);
        Assert.Equal(0, closer.Calls);
        Assert.False(handle.IsClosed);

        Assert.True(owner.TryDrain());
        Assert.Equal(0, owner.PendingCount);
        Assert.Equal(1, closer.Calls);
        Assert.True(handle.IsClosed);
        Assert.True(owner.TryCleanup(handle));
        Assert.Equal(1, closer.Calls);
    }

    [Fact]
    public void CloseFailureRetainsHandleUntilRetrySucceeds()
    {
        var closer = new FakeCloser { FailFirst = true };
        var handle = NewHandle(closer);
        var owner = new FixturePendingFileCleanup(new object(), _ => true);

        Assert.False(owner.TryCleanup(handle));
        Assert.Equal(1, owner.PendingCount);
        Assert.False(handle.IsClosed);
        Assert.Equal(1, closer.Calls);

        Assert.True(owner.TryDrain());
        Assert.Equal(0, owner.PendingCount);
        Assert.True(handle.IsClosed);
        Assert.Equal(2, closer.Calls);
    }

    [Fact]
    public void RootCloseFailureRetainsExactHandleUntilRetrySucceeds()
    {
        var closer = new FakeCloser { FailFirst = true };
        var root = NewHandle(closer);
        var owner = new FixturePendingRootClose(new object());

        Assert.False(owner.TryClose(root));
        Assert.Equal(1, owner.PendingCount);
        Assert.False(root.IsClosed);
        Assert.Equal(1, closer.Calls);

        Assert.True(owner.TryDrain());
        Assert.Equal(0, owner.PendingCount);
        Assert.True(root.IsClosed);
        Assert.Equal(2, closer.Calls);
        Assert.True(owner.TryClose(root));
        Assert.Equal(2, closer.Calls);
    }

    [Fact]
    public void ObserverCloseFailureRetainsExactHandleUntilRetrySucceeds()
    {
        var closer = new FakeCloser { FailFirst = true };
        var observed = NewHandle(closer);
        var owner = new FixtureRelativeFileCloseOwner(new object());

        Assert.False(owner.TryClose(observed));
        Assert.Equal(1, owner.PendingCount);
        Assert.False(observed.IsClosed);

        Assert.True(owner.TryClosePending());
        Assert.Equal(0, owner.PendingCount);
        Assert.True(observed.IsClosed);
        Assert.True(owner.TryClose(observed));
        Assert.Equal(2, closer.Calls);
    }

    [Fact]
    public async Task ObserverCloseWaitsForSharedLifetimeGate()
    {
        var lifetimeGate = new object();
        using var operationEntered = new ManualResetEventSlim();
        using var releaseOperation = new ManualResetEventSlim();
        using var closeStarted = new ManualResetEventSlim();
        var closer = new FakeCloser();
        var observed = NewHandle(closer);
        var owner = new FixtureRelativeFileCloseOwner(lifetimeGate);

        var operation = Task.Run(() =>
        {
            lock (lifetimeGate)
            {
                operationEntered.Set();
                if (!releaseOperation.Wait(TestTimeouts.Signal))
                    throw new TimeoutException();
                Assert.Equal(0, closer.Calls);
            }
        });
        try
        {
            Assert.True(operationEntered.Wait(TestTimeouts.Signal));
            var close = Task.Run(() =>
            {
                closeStarted.Set();
                return owner.TryClose(observed);
            });
            Assert.True(closeStarted.Wait(TestTimeouts.Signal));
            var early = await Task.WhenAny(close, Task.Delay(TimeSpan.FromMilliseconds(100)));
            Assert.NotSame(close, early);
            Assert.Equal(0, closer.Calls);
            releaseOperation.Set();
            await operation;
            Assert.True(await close);
            Assert.Equal(1, closer.Calls);
        }
        finally
        {
            releaseOperation.Set();
            await operation;
        }
    }

    [Fact]
    public async Task ConcurrentDrainCannotCloseWhileMarkIsInProgress()
    {
        using var marking = new ManualResetEventSlim();
        using var releaseMark = new ManualResetEventSlim();
        var closer = new FakeCloser();
        var handle = NewHandle(closer);
        var owner = new FixturePendingFileCleanup(new object(), _ =>
        {
            marking.Set();
            if (!releaseMark.Wait(TestTimeouts.Signal))
                throw new TimeoutException();
            return true;
        });

        var cleanup = Task.Run(() => owner.TryCleanup(handle));
        try
        {
            Assert.True(marking.Wait(TestTimeouts.Signal));
            var drain = Task.Run(owner.TryDrain);
            Assert.Equal(0, closer.Calls);
            releaseMark.Set();
            Assert.True(await cleanup);
            Assert.True(await drain);
            Assert.Equal(1, closer.Calls);
            Assert.Equal(0, owner.PendingCount);
        }
        finally
        {
            releaseMark.Set();
            await cleanup;
        }
    }

    private static SafeWindowsHandle NewHandle(FakeCloser closer) =>
        new(new nint(1234), closer);

    private sealed class FakeCloser : IWindowsHandleCloser
    {
        internal bool FailFirst { get; init; }

        internal int Calls { get; private set; }

        public bool Close(nint handle)
        {
            Calls++;
            return !FailFirst || Calls > 1;
        }
    }
}
