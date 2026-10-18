using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;

namespace WatchDog.App.Services;

/// <summary>
/// Sequentially polls explicitly registered counter providers and submits only exact, freshly
/// revalidated fail-closed candidates. Counter advancement does not prove binding, visible or
/// audible content, or A/V sync; native adapters and qualified thresholds are not present yet.
/// </summary>
public sealed class ActiveCaptureLivenessObserver : IHostedService, IAsyncDisposable, IDisposable
{
    private readonly CaptureLivenessRegistry _registry;
    private readonly CaptureLifecycleCoordinator _lifecycle;
    private readonly ILogger<ActiveCaptureLivenessObserver> _logger;
    private readonly IActiveCaptureLivenessTickSource _ticks;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, long> _lastPollTimestamps = [];
    private readonly object _lifetimeGate = new();

    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _disposed;

    public ActiveCaptureLivenessObserver(
        CaptureLivenessRegistry registry,
        CaptureLifecycleCoordinator lifecycle,
        ILogger<ActiveCaptureLivenessObserver> logger)
        : this(
            registry,
            lifecycle,
            logger,
            new DelayCaptureLivenessTickSource(),
            TimeProvider.System)
    {
    }

    internal ActiveCaptureLivenessObserver(
        CaptureLivenessRegistry registry,
        CaptureLifecycleCoordinator lifecycle,
        ILogger<ActiveCaptureLivenessObserver> logger,
        IActiveCaptureLivenessTickSource ticks,
        TimeProvider timeProvider)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _lifecycle = lifecycle ?? throw new ArgumentNullException(nameof(lifecycle));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ticks = ticks ?? throw new ArgumentNullException(nameof(ticks));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loopTask is not null)
                return Task.CompletedTask;

            _loopCancellation = new CancellationTokenSource();
            _loopTask = RunAsync(_loopCancellation.Token);
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        Task? loop;
        lock (_lifetimeGate)
        {
            _loopCancellation?.Cancel();
            loop = _loopTask;
        }
        if (loop is not null)
            await loop.ConfigureAwait(false);
    }

    internal async Task PollOnceAsync(CancellationToken cancellationToken = default)
    {
        await PollTargetsAsync(_registry.GetPollTargets(), cancellationToken).ConfigureAwait(false);
    }

    private async Task PollTargetsAsync(
        IReadOnlyList<CaptureLivenessPollTarget> targets,
        CancellationToken cancellationToken)
    {
        foreach (var target in targets)
        {
            var candidate = await _registry.ObserveAsync(target, cancellationToken)
                .ConfigureAwait(false);
            if (candidate is null)
                continue;

            // ObserveAsync has released both provider execution and registry locks before this
            // awaited lifecycle handoff.
            var transition = await _lifecycle.HandleLivenessFailureAsync(candidate, cancellationToken)
                .ConfigureAwait(false);
            if (transition.Disposition != CaptureCommandDisposition.Ignored)
                break;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                _registry.ConsumePendingChange();
                var targets = _registry.GetPollTargets();
                if (targets.Count == 0)
                {
                    _lastPollTimestamps.Clear();
                    await _registry.WaitForChangeAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var currentIds = targets.Select(target => target.RegistrationId).ToHashSet();
                foreach (var staleId in _lastPollTimestamps.Keys
                             .Where(id => !currentIds.Contains(id)).ToArray())
                {
                    _lastPollTimestamps.Remove(staleId);
                }

                var now = _timeProvider.GetTimestamp();
                foreach (var target in targets)
                    _lastPollTimestamps.TryAdd(target.RegistrationId, now);

                var due = targets.Where(target =>
                    _timeProvider.GetElapsedTime(
                        _lastPollTimestamps[target.RegistrationId],
                        now) >= target.PollInterval).ToArray();
                if (due.Length != 0)
                {
                    await PollTargetsAsync(due, cancellationToken).ConfigureAwait(false);
                    var completedAt = _timeProvider.GetTimestamp();
                    foreach (var target in due)
                        _lastPollTimestamps[target.RegistrationId] = completedAt;
                    continue;
                }

                var wait = targets.Min(target =>
                    target.PollInterval - _timeProvider.GetElapsedTime(
                        _lastPollTimestamps[target.RegistrationId],
                        now));
                var waitResult = await WaitForDeadlineOrChangeAsync(wait, cancellationToken)
                    .ConfigureAwait(false);
                if (waitResult == ObserverWaitResult.Stopped)
                    break;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The active capture liveness observer loop stopped unexpectedly");
        }
    }

    private async Task<ObserverWaitResult> WaitForDeadlineOrChangeAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tickTask = _ticks.WaitForNextTickAsync(interval, waitCancellation.Token).AsTask();
        var changeTask = _registry.WaitForChangeAsync(waitCancellation.Token);
        try
        {
            var completed = await Task.WhenAny(tickTask, changeTask).ConfigureAwait(false);
            if (completed == changeTask)
            {
                await changeTask.ConfigureAwait(false);
                return ObserverWaitResult.Changed;
            }

            return await tickTask.ConfigureAwait(false)
                ? ObserverWaitResult.Deadline
                : ObserverWaitResult.Stopped;
        }
        finally
        {
            waitCancellation.Cancel();
            await DrainCancelledAsync(tickTask).ConfigureAwait(false);
            await DrainCancelledAsync(changeTask).ConfigureAwait(false);
        }
    }

    private static async Task DrainCancelledAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_lifetimeGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _loopCancellation?.Cancel();
            loop = _loopTask;
        }
        if (loop is not null)
            await loop.ConfigureAwait(false);
        _ticks.Dispose();
        _loopCancellation?.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private enum ObserverWaitResult
    {
        Deadline,
        Changed,
        Stopped,
    }
}

internal interface IActiveCaptureLivenessTickSource : IDisposable
{
    ValueTask<bool> WaitForNextTickAsync(TimeSpan interval, CancellationToken cancellationToken);
}

internal sealed class DelayCaptureLivenessTickSource : IActiveCaptureLivenessTickSource
{
    public async ValueTask<bool> WaitForNextTickAsync(
        TimeSpan interval,
        CancellationToken cancellationToken)
    {
        await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public void Dispose()
    {
    }
}
