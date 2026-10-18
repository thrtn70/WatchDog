namespace WatchDog.CaptureIsolation.Windows;

internal interface IWindowsReapDelay
{
    ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class SystemWindowsReapDelay : IWindowsReapDelay
{
    internal static SystemWindowsReapDelay Instance { get; } = new();

    private SystemWindowsReapDelay()
    {
    }

    public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay == TimeSpan.Zero
            ? ValueTask.CompletedTask
            : new(Task.Delay(delay, cancellationToken));
}
