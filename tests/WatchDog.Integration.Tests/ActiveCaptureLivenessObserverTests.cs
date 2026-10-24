using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using WatchDog.App.Services;
using WatchDog.Core.Capture;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Integration.Tests;

public sealed class ActiveCaptureLivenessObserverTests
{
    [Fact]
    public async Task NoProviderRegistryIsInert()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        var backend = new Backend();
        await using var lifecycle = new CaptureLifecycleCoordinator(backend, registry);
        await using var observer = Observer(registry, lifecycle, new ManualTickSource());

        await observer.PollOnceAsync();

        Assert.Equal(0, backend.StopCalls);
    }

    [Fact]
    public async Task ProductionLoopWakesWhenFirstProviderRegistersAfterStart()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        await using var lifecycle = new CaptureLifecycleCoordinator(new Backend(), registry);
        var provider = new CountingProvider();
        var ticks = new ManualTickSource();
        await using var observer = Observer(registry, lifecycle, ticks, time);
        await observer.StartAsync(CancellationToken.None);

        registry.Register(Registration(
            CaptureLivenessPipelineOwner.SessionSegment,
            provider,
            TimeSpan.FromMilliseconds(100)));

        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            await ticks.Waits.Reader.ReadAsync().AsTask().WaitAsync(TestTimeouts.Signal));
        time.Advance(TimeSpan.FromMilliseconds(100));
        ticks.Tick();
        Assert.Equal(
            1,
            await provider.Reads.Reader.ReadAsync().AsTask().WaitAsync(TestTimeouts.Signal));

        await observer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PollOnceReadsProviderTargetsSequentially()
    {
        using var registry = new CaptureLivenessRegistry(new ManualTimeProvider());
        await using var lifecycle = new CaptureLifecycleCoordinator(new Backend(), registry);
        var first = new BlockingProvider();
        var second = new BlockingProvider();
        registry.Register(Registration(CaptureLivenessPipelineOwner.Replay, first));
        registry.Register(Registration(CaptureLivenessPipelineOwner.SessionSegment, second));
        await using var observer = Observer(registry, lifecycle, new ManualTickSource());

        var poll = observer.PollOnceAsync();
        await first.Entered.Task;
        Assert.False(second.Entered.Task.IsCompleted);

        first.Release.SetResult();
        await second.Entered.Task;
        Assert.False(poll.IsCompleted);
        second.Release.SetResult();
        await poll;
    }

    [Fact]
    public async Task StopAsyncDrainsInFlightProviderRead()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        await using var lifecycle = new CaptureLifecycleCoordinator(new Backend(), registry);
        var provider = new BlockingProvider(ignoreCancellation: true);
        registry.Register(Registration(CaptureLivenessPipelineOwner.SessionSegment, provider));
        var ticks = new ManualTickSource();
        await using var observer = Observer(registry, lifecycle, ticks, time);
        await observer.StartAsync(CancellationToken.None);
        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            await ticks.Waits.Reader.ReadAsync());
        time.Advance(TimeSpan.FromMilliseconds(100));
        ticks.Tick();
        await provider.Entered.Task;

        var stop = observer.StopAsync(CancellationToken.None);
        Assert.False(stop.IsCompleted);

        provider.Release.SetResult();
        await stop;
    }

    [Fact]
    public async Task StopAsyncCancelsCooperativeInFlightProviderRead()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        await using var lifecycle = new CaptureLifecycleCoordinator(new Backend(), registry);
        var provider = new BlockingProvider();
        registry.Register(Registration(CaptureLivenessPipelineOwner.SessionSegment, provider));
        var ticks = new ManualTickSource();
        await using var observer = Observer(registry, lifecycle, ticks, time);
        await observer.StartAsync(CancellationToken.None);
        Assert.Equal(
            TimeSpan.FromMilliseconds(100),
            await ticks.Waits.Reader.ReadAsync().AsTask().WaitAsync(TestTimeouts.Signal));
        time.Advance(TimeSpan.FromMilliseconds(100));
        ticks.Tick();
        await provider.Entered.Task.WaitAsync(TestTimeouts.Signal);

        await observer.StopAsync(CancellationToken.None).WaitAsync(TestTimeouts.Signal);
        await provider.CancellationObserved.Task.WaitAsync(TestTimeouts.Signal);

        Assert.False(provider.Release.Task.IsCompleted);
    }

    [Fact]
    public async Task ProductionLoopRespectsEachRegistrationInterval()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        await using var lifecycle = new CaptureLifecycleCoordinator(new Backend(), registry);
        var fast = new CountingProvider();
        var slow = new CountingProvider();
        registry.Register(Registration(
            CaptureLivenessPipelineOwner.Replay,
            fast,
            TimeSpan.FromSeconds(1)));
        registry.Register(Registration(
            CaptureLivenessPipelineOwner.SessionSegment,
            slow,
            TimeSpan.FromSeconds(2)));
        var ticks = new ManualTickSource();
        await using var observer = Observer(registry, lifecycle, ticks, time);
        await observer.StartAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromSeconds(1), await ticks.Waits.Reader.ReadAsync());
        time.Advance(TimeSpan.FromSeconds(1));
        ticks.Tick();
        Assert.Equal(1, await fast.Reads.Reader.ReadAsync());
        Assert.Equal(0, slow.ReadCount);

        Assert.Equal(TimeSpan.FromSeconds(1), await ticks.Waits.Reader.ReadAsync());
        time.Advance(TimeSpan.FromSeconds(1));
        ticks.Tick();
        Assert.Equal(2, await fast.Reads.Reader.ReadAsync());
        Assert.Equal(1, await slow.Reads.Reader.ReadAsync());
        await observer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RegistryChangesCancelOldDeadlineAndRecomputeRepeatedly()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        await using var lifecycle = new CaptureLifecycleCoordinator(new Backend(), registry);
        var slow = new CountingProvider();
        var medium = new CountingProvider();
        var fast = new CountingProvider();
        registry.Register(Registration(
            CaptureLivenessPipelineOwner.Replay,
            slow,
            TimeSpan.FromSeconds(10)));
        var ticks = new ManualTickSource();
        await using var observer = Observer(registry, lifecycle, ticks, time);
        await observer.StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(10), await ticks.Waits.Reader.ReadAsync());

        registry.Register(Registration(
            CaptureLivenessPipelineOwner.SessionSegment,
            medium,
            TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromSeconds(5), await ticks.Waits.Reader.ReadAsync());

        registry.Register(Registration(
            CaptureLivenessPipelineOwner.WebcamIntermediate,
            fast,
            TimeSpan.FromSeconds(1)));
        Assert.Equal(TimeSpan.FromSeconds(1), await ticks.Waits.Reader.ReadAsync());
        time.Advance(TimeSpan.FromSeconds(1));
        ticks.Tick();

        Assert.Equal(1, await fast.Reads.Reader.ReadAsync());
        Assert.Equal(0, medium.ReadCount);
        Assert.Equal(0, slow.ReadCount);
        await observer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsyncDrainsCombinedDeadlineAndChangeWait()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        await using var lifecycle = new CaptureLifecycleCoordinator(new Backend(), registry);
        registry.Register(Registration(
            CaptureLivenessPipelineOwner.SessionSegment,
            new CountingProvider(),
            TimeSpan.FromSeconds(10)));
        var ticks = new ManualTickSource();
        await using var observer = Observer(registry, lifecycle, ticks, time);
        await observer.StartAsync(CancellationToken.None);
        Assert.Equal(TimeSpan.FromSeconds(10), await ticks.Waits.Reader.ReadAsync());

        await observer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task RealRegistryObserverAndCoordinatorStopOnFreshClaimedFailure()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        var backend = new Backend();
        await using var lifecycle = new CaptureLifecycleCoordinator(backend, registry);
        var request = new CaptureRequest(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            CaptureInitiation.Automatic,
            new WindowCaptureTarget(10, new IntPtr(0x1000), "window"));
        Assert.True((await lifecycle.StartAsync(request)).Succeeded);
        var context = Assert.IsType<CaptureSessionContext>(lifecycle.Snapshot.Context);
        var provider = new QueueProvider(
            context => context.Missing(),
            context => context.Missing());
        registry.Register(new CaptureLivenessRegistration(
            Guid.NewGuid(),
            CaptureLivenessPipelineOwner.SessionSegment,
            new CaptureLivenessIdentity(
                context.SessionId,
                context.Request.RequestId,
                context.Generation,
                context.ActualTarget,
                Guid.NewGuid(),
                new CaptureLivenessStream(CaptureLivenessStreamKind.EncodedOutputVideo, 0),
                CaptureLivenessMeasurementKind.EncodedOutputVideoFrames,
                new CaptureLivenessTimingPolicy(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2))),
            TimeSpan.FromMilliseconds(100),
            provider));
        await using var observer = Observer(registry, lifecycle, new ManualTickSource(), time);

        await observer.PollOnceAsync();

        Assert.Equal(1, backend.StopCalls);
        Assert.Equal(CaptureLifecycleStatus.Faulted, lifecycle.Snapshot.Status);
        Assert.Equal(CaptureFailureKind.Liveness, lifecycle.Snapshot.LastFailure?.Kind);
        Assert.Empty(registry.GetPollTargets());
    }

    [Fact]
    public async Task CleanupRequiredKeepsFenceAndSuccessfulRetryMakesContextTerminal()
    {
        var time = new ManualTimeProvider();
        using var registry = new CaptureLivenessRegistry(time);
        var backend = new Backend
        {
            StopResults = new Queue<CaptureBackendCommandResult>(
            [
                CaptureBackendCommandResult.Failed(
                    new CaptureFailure(CaptureFailureKind.Backend, "cleanup", "cleanup"),
                    cleanupRequired: true),
                CaptureBackendCommandResult.Success(),
            ]),
        };
        await using var lifecycle = new CaptureLifecycleCoordinator(backend, registry);
        var request = new CaptureRequest(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            CaptureInitiation.Automatic,
            new WindowCaptureTarget(10, new IntPtr(0x1000), "window"));
        await lifecycle.StartAsync(request);
        var context = Assert.IsType<CaptureSessionContext>(lifecycle.Snapshot.Context);
        var registration = Registration(
            context,
            CaptureLivenessPipelineOwner.SessionSegment,
            new QueueProvider(ctx => ctx.Missing(), ctx => ctx.Missing()));
        registry.Register(registration);
        await using var observer = Observer(registry, lifecycle, new ManualTickSource(), time);

        await observer.PollOnceAsync();
        Assert.NotNull(lifecycle.Snapshot.Context);
        var blocked = Registration(
            context,
            CaptureLivenessPipelineOwner.Replay,
            new CountingProvider());
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Register(blocked).Disposition);

        Assert.True((await lifecycle.StopAsync(CaptureInitiation.Automatic)).Succeeded);
        Assert.Equal(
            CaptureLivenessRegistrationDisposition.Rejected,
            registry.Register(blocked).Disposition);
    }

    private static ActiveCaptureLivenessObserver Observer(
        CaptureLivenessRegistry registry,
        CaptureLifecycleCoordinator lifecycle,
        IActiveCaptureLivenessTickSource ticks,
        TimeProvider? timeProvider = null) =>
        new(
            registry,
            lifecycle,
            NullLogger<ActiveCaptureLivenessObserver>.Instance,
            ticks,
            timeProvider ?? new ManualTimeProvider());

    private static CaptureLivenessRegistration Registration(
        CaptureLivenessPipelineOwner owner,
        ICaptureLivenessTelemetryProvider provider,
        TimeSpan? pollInterval = null) =>
        new(
            Guid.NewGuid(),
            owner,
            new CaptureLivenessIdentity(
                new Guid("10000000-0000-0000-0000-000000000001"),
                new Guid("20000000-0000-0000-0000-000000000002"),
                1,
                new WindowCaptureTarget(10, new IntPtr(0x1000), "window"),
                Guid.NewGuid(),
                new CaptureLivenessStream(CaptureLivenessStreamKind.EncodedOutputVideo, 0),
                CaptureLivenessMeasurementKind.EncodedOutputVideoFrames,
                new CaptureLivenessTimingPolicy(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2))),
            pollInterval ?? TimeSpan.FromMilliseconds(100),
            provider);

    private static CaptureLivenessRegistration Registration(
        CaptureSessionContext context,
        CaptureLivenessPipelineOwner owner,
        ICaptureLivenessTelemetryProvider provider) =>
        new(
            Guid.NewGuid(),
            owner,
            new CaptureLivenessIdentity(
                context.SessionId,
                context.Request.RequestId,
                context.Generation,
                context.ActualTarget,
                Guid.NewGuid(),
                new CaptureLivenessStream(CaptureLivenessStreamKind.EncodedOutputVideo, 0),
                CaptureLivenessMeasurementKind.EncodedOutputVideoFrames,
                new CaptureLivenessTimingPolicy(
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(1),
                    TimeSpan.FromSeconds(2))),
            TimeSpan.FromMilliseconds(100),
            provider);

    private sealed class CountingProvider : ICaptureLivenessTelemetryProvider
    {
        public Channel<int> Reads { get; } = Channel.CreateUnbounded<int>();
        public int ReadCount { get; private set; }

        public ValueTask<CaptureLivenessTelemetryReading> ReadAsync(
            CaptureLivenessReadContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCount++;
            Reads.Writer.TryWrite(ReadCount);
            return ValueTask.FromResult(context.Available(ReadCount));
        }
    }

    private sealed class QueueProvider(
        params Func<CaptureLivenessReadContext, CaptureLivenessTelemetryReading>[] readings)
        : ICaptureLivenessTelemetryProvider
    {
        private readonly Queue<Func<CaptureLivenessReadContext, CaptureLivenessTelemetryReading>> _readings =
            new(readings);

        public ValueTask<CaptureLivenessTelemetryReading> ReadAsync(
            CaptureLivenessReadContext context,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_readings.Dequeue()(context));
        }
    }

    private sealed class BlockingProvider(bool ignoreCancellation = false)
        : ICaptureLivenessTelemetryProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<CaptureLivenessTelemetryReading> ReadAsync(
            CaptureLivenessReadContext context,
            CancellationToken cancellationToken)
        {
            Entered.SetResult();
            if (ignoreCancellation)
                return await CompleteAsync(Release.Task, context);

            try
            {
                return await CompleteAsync(Release.Task.WaitAsync(cancellationToken), context);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        private static async Task<CaptureLivenessTelemetryReading> CompleteAsync(
            Task release,
            CaptureLivenessReadContext context)
        {
            await release;
            return context.Available(1);
        }
    }

    private sealed class ManualTickSource : IActiveCaptureLivenessTickSource
    {
        private readonly Channel<bool> _ticks = Channel.CreateUnbounded<bool>();
        public Channel<TimeSpan> Waits { get; } = Channel.CreateUnbounded<TimeSpan>();

        public void Tick() => _ticks.Writer.TryWrite(true);

        public ValueTask<bool> WaitForNextTickAsync(
            TimeSpan interval,
            CancellationToken cancellationToken)
        {
            Waits.Writer.TryWrite(interval);
            return _ticks.Reader.ReadAsync(cancellationToken);
        }

        public void Dispose()
        {
            _ticks.Writer.TryComplete();
            Waits.Writer.TryComplete();
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan elapsed) => _timestamp += elapsed.Ticks;
    }

    private sealed class Backend : ICaptureLifecycleBackend
    {
        public int StopCalls { get; private set; }
        public Queue<CaptureBackendCommandResult>? StopResults { get; init; }

        public Task<CaptureBackendStartResult> StartAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(CaptureBackendStartResult.Success(context.RequestedTarget));

        public Task<CaptureBackendCommandResult> StopAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken)
        {
            StopCalls++;
            return Task.FromResult(
                StopResults is { Count: > 0 }
                    ? StopResults.Dequeue()
                    : CaptureBackendCommandResult.Success());
        }

        public Task<CaptureBackendSaveResult> SaveAsync(
            CaptureSessionContext context,
            CancellationToken cancellationToken) =>
            Task.FromResult(CaptureBackendSaveResult.Success("clip.mp4"));
    }
}
