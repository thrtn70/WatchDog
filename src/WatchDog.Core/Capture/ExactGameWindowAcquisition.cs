namespace WatchDog.Core.Capture;

/// <summary>
/// Performs a bounded, cancellable wait for a detected process to expose its exact main window.
/// Resolution remains fail-closed: no executable wildcard or display fallback is permitted.
/// </summary>
public static class ExactGameWindowAcquisition
{
    private const int DefaultMaxAttempts = 30;
    private static readonly TimeSpan DefaultRetryDelay = TimeSpan.FromMilliseconds(500);

    public static Task<WindowCaptureTarget?> ResolveAsync(
        ICaptureEngine captureEngine,
        GameCaptureTarget game,
        CancellationToken cancellationToken = default) =>
        ResolveAsync(
            captureEngine,
            game,
            DefaultMaxAttempts,
            DefaultRetryDelay,
            static (delay, token) => Task.Delay(delay, token),
            cancellationToken);

    internal static async Task<WindowCaptureTarget?> ResolveAsync(
        ICaptureEngine captureEngine,
        GameCaptureTarget game,
        int maxAttempts,
        TimeSpan retryDelay,
        Func<TimeSpan, CancellationToken, Task> delayAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(captureEngine);
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(delayAsync);
        if (maxAttempts <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (retryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(retryDelay));

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (captureEngine.ResolveGameWindowTarget(game) is { } window)
                return window;

            if (attempt + 1 < maxAttempts)
                await delayAsync(retryDelay, cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}
