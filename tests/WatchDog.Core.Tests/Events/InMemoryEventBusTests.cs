using WatchDog.Core.Events;

namespace WatchDog.Core.Tests.Events;

public sealed class InMemoryEventBusTests
{
    private sealed record TestEvent(string Message);
    private sealed record OtherEvent(int Value);

    [Fact]
    public void Publish_NotifiesSubscribers()
    {
        var bus = new InMemoryEventBus();
        string? received = null;

        bus.Subscribe<TestEvent>(e => received = e.Message);
        bus.Publish(new TestEvent("hello"));

        Assert.Equal("hello", received);
    }

    [Fact]
    public void Publish_NotifiesMultipleSubscribers()
    {
        var bus = new InMemoryEventBus();
        var received = new List<string>();

        bus.Subscribe<TestEvent>(e => received.Add($"1:{e.Message}"));
        bus.Subscribe<TestEvent>(e => received.Add($"2:{e.Message}"));
        bus.Publish(new TestEvent("test"));

        Assert.Equal(2, received.Count);
        Assert.Contains("1:test", received);
        Assert.Contains("2:test", received);
    }

    [Fact]
    public void Publish_DoesNotNotifyUnrelatedSubscribers()
    {
        var bus = new InMemoryEventBus();
        var received = false;

        bus.Subscribe<OtherEvent>(_ => received = true);
        bus.Publish(new TestEvent("hello"));

        Assert.False(received);
    }

    [Fact]
    public void Unsubscribe_StopsNotifications()
    {
        var bus = new InMemoryEventBus();
        var count = 0;

        var sub = bus.Subscribe<TestEvent>(_ => count++);
        bus.Publish(new TestEvent("first"));
        Assert.Equal(1, count);

        sub.Dispose();
        bus.Publish(new TestEvent("second"));
        Assert.Equal(1, count);
    }

    [Fact]
    public void Publish_WithNoSubscribers_DoesNotThrow()
    {
        var bus = new InMemoryEventBus();
        var ex = Record.Exception(() => bus.Publish(new TestEvent("orphan")));
        Assert.Null(ex);
    }
}
