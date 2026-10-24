using WatchDog.Core.Capture;
using WatchDog.Core.Tests.Helpers;
using WatchDog.Core.Webcam;

namespace WatchDog.Core.Tests.Capture;

public sealed class CaptureOwnedManualStopTests
{
    [Theory]
    [MemberData(nameof(CaptureStartupBaselineTests.ExpectationFields), MemberType = typeof(CaptureStartupBaselineTests))]
    public async Task BoundExpectationIsPartOfExactOwnerIdentity(string field)
    {
        var baseline = CaptureStartupBaselineResult.Create(new WatchDog.Core.Settings.AppSettings());
        var request = CaptureStartupBaselineTests.Request(baseline);
        var backend = new Backend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(request)).Succeeded);
        var exact = coordinator.Snapshot.Context!;
        var changed = new CaptureRequest(request.RequestId, request.RequestedAt, request.Initiation, request.Target,
            request.Webcam, request.StartPolicy, CaptureStartupBaselineTests.Change(request.StartupExpectation!, field));
        await AssertRejectedUnchanged(coordinator, backend, exact with { Request = changed });
        Assert.True((await coordinator.StopOwnedManualAsync(exact)).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ExactOwnerStopsAndStaleOwnerCannotStopReplacement(int kind)
    {
        var backend = new Backend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(Request(10, kind))).Succeeded);
        var first = coordinator.Snapshot.Context!;
        Assert.True((await coordinator.SwitchAsync(Request(20, kind))).Succeeded);
        var second = coordinator.Snapshot.Context!;
        var before = coordinator.Snapshot;
        var stops = backend.StopCalls.Count;

        var stale = await coordinator.StopOwnedManualAsync(first);
        Assert.Equal(CaptureCommandDisposition.Rejected, stale.Disposition);
        Assert.Same(before, stale.After);
        Assert.Same(before, coordinator.Snapshot);
        Assert.Equal(stops, backend.StopCalls.Count);
        Assert.True((await coordinator.StopOwnedManualAsync(second)).Succeeded);
        Assert.Equal(stops + 1, backend.StopCalls.Count);
        Assert.True((await coordinator.StartAsync(Automatic())).Succeeded);
    }

    [Fact]
    public async Task EveryForeignIdentityOrWindowFieldRejectsBeforeBackendStop()
    {
        var backend = new Backend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(Request(10))).Succeeded);
        var exact = coordinator.Snapshot.Context!;
        var target = (WindowCaptureTarget)exact.ActualTarget;
        var changedRequest = new CaptureRequest(exact.Request.RequestId, exact.Request.RequestedAt,
            CaptureInitiation.Manual, target with { WindowTitle = "Changed" });
        foreach (var foreign in new[]
        {
            exact with { SessionId = Guid.NewGuid() },
            exact with { Generation = exact.Generation + 1 },
            exact with { Request = changedRequest },
            exact with { RequestedTarget = target with { WindowClass = "Changed" } },
            exact with { ActualTarget = target with { WindowTitle = "Changed" } },
            exact with { ActualTarget = target with { ExecutableName = "other.exe" } },
            exact with { ActualTarget = target with { ProcessId = target.ProcessId + 1 } },
            exact with { ActualTarget = target with { WindowHandle = new IntPtr(0x9999) } },
        })
        {
            var before = coordinator.Snapshot;
            var result = await coordinator.StopOwnedManualAsync(foreign);
            Assert.Equal(CaptureCommandDisposition.Rejected, result.Disposition);
            Assert.Same(before, result.After);
            Assert.Same(before, coordinator.Snapshot);
            Assert.Empty(backend.StopCalls);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NullContextDoesNotReleaseManualReservationOrStopAnything(int kind)
    {
        var backend = new Backend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        var request = Request(10, kind);
        Assert.True((await coordinator.StartAsync(request)).Succeeded);
        var exact = coordinator.Snapshot.Context!;
        Assert.True((await coordinator.HandleSourceLostAsync(new CaptureSourceLoss(
            exact.SessionId, exact.Generation, exact.ActualTarget, DateTimeOffset.UnixEpoch))).Succeeded);
        var stopCount = backend.StopCalls.Count;
        var before = coordinator.Snapshot;
        var rejected = await coordinator.StopOwnedManualAsync(exact);
        Assert.Equal(CaptureCommandDisposition.Rejected, rejected.Disposition);
        Assert.Same(before, rejected.After);
        Assert.Equal(stopCount, backend.StopCalls.Count);
        var automatic = new CaptureRequest(Guid.NewGuid(), DateTimeOffset.UnixEpoch,
            CaptureInitiation.Automatic,
            new GameCaptureTarget("game", 22, "game.exe", DateTimeOffset.UnixEpoch));
        Assert.Equal(CaptureFailureKind.ManualPriority,
            (await coordinator.StartAsync(automatic)).Failure?.Kind);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task RetainedFaultedOwnerMayRetryWhileClosedAdmissionRejects(int kind)
    {
        var backend = new Backend { FailStop = true };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(Request(10, kind))).Succeeded);
        var exact = coordinator.Snapshot.Context!;
        var failed = await coordinator.StopOwnedManualAsync(exact);
        Assert.Equal(CaptureCommandDisposition.Failed, failed.Disposition);
        Assert.Same(exact, failed.After.Context);
        Assert.Equal(CaptureLifecycleStatus.Faulted, failed.After.Status);
        await AssertRejectedUnchanged(coordinator, backend, exact with { Generation = exact.Generation + 1 });
        Assert.Equal(CaptureFailureKind.ManualPriority, (await coordinator.StartAsync(Automatic())).Failure?.Kind);
        backend.FailStop = false;
        Assert.True((await coordinator.StopOwnedManualAsync(exact)).Succeeded);
        Assert.Equal(2, backend.StopCalls.Count);

        Assert.True((await coordinator.StartAsync(Request(20, kind))).Succeeded);
        var later = coordinator.Snapshot.Context!;
        await coordinator.ShutdownAsync();
        var count = backend.StopCalls.Count;
        var closed = await coordinator.StopOwnedManualAsync(later);
        Assert.Equal(CaptureCommandDisposition.Rejected, closed.Disposition);
        Assert.Equal(count, backend.StopCalls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnapprovedActualTargetIsCleanupOnlyAndExactRetainedStopMayReleaseIt(int kind)
    {
        var backend = new Backend
        {
            ForcedActualTarget = new DisplayCaptureTarget("unapproved-display"),
            FailStop = true,
        };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        var start = await coordinator.StartAsync(Request(10, kind));
        Assert.Equal(CaptureCommandDisposition.Failed, start.Disposition);
        var retained = Assert.IsType<CaptureSessionContext>(start.After.Context);
        Assert.IsType<DisplayCaptureTarget>(retained.ActualTarget);
        Assert.Equal(CaptureLifecycleStatus.Faulted, start.After.Status);
        backend.FailStop = false;
        var stop = await coordinator.StopOwnedManualAsync(retained);
        Assert.True(stop.Succeeded);
        Assert.Null(stop.After.Context);
        Assert.Equal(2, backend.StopCalls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedFinalizationAfterReleaseDoesNotLeaveScopedStopAuthority(int kind)
    {
        var backend = new Backend { FailStop = true, ReleaseOnStopFailure = true };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(Request(10, kind))).Succeeded);
        var exact = coordinator.Snapshot.Context!;
        var warning = await coordinator.StopOwnedManualAsync(exact);
        Assert.Equal(CaptureCommandDisposition.Failed, warning.Disposition);
        Assert.Null(warning.After.Context);
        Assert.Equal(CaptureLifecycleStatus.Faulted, warning.After.Status);
        Assert.Equal(CaptureCommandDisposition.Rejected,
            (await coordinator.StopOwnedManualAsync(exact)).Disposition);
        Assert.Single(backend.StopCalls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task EntireFrozenRequestAndBothTargetRecordsMustMatch(int kind)
    {
        var backend = new Backend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(Request(10, kind))).Succeeded);
        var exact = coordinator.Snapshot.Context!;
        var request = exact.Request;
        var camera = new WebcamCaptureSelection("fixture-camera", "Camera", 640, 480, 30, "NV12", 1000);
        foreach (var foreign in new[]
        {
            exact with { SessionId = Guid.Empty }, exact with { SessionId = Guid.NewGuid() },
            exact with { Generation = 0 }, exact with { Generation = exact.Generation + 1 },
            exact with { Request = Copy(request, id: Guid.Empty) },
            exact with { Request = Copy(request, id: Guid.NewGuid()) },
            exact with { Request = Copy(request, time: request.RequestedAt.AddTicks(1)) },
            exact with { Request = Copy(request, initiation: CaptureInitiation.Automatic) },
            exact with { Request = Copy(request, initiation: CaptureInitiation.ConfiguredStartup) },
            exact with { Request = Copy(request, webcam: camera) },
            exact with { Request = Copy(request, policy: new(new(), new(), new())) },
        })
            await AssertRejectedUnchanged(coordinator, backend, foreign);

        foreach (var changed in ChangedTargets(request.Target))
        {
            await AssertRejectedUnchanged(coordinator, backend, exact with { Request = Copy(request, target: changed) });
            await AssertRejectedUnchanged(coordinator, backend, exact with { RequestedTarget = changed });
            await AssertRejectedUnchanged(coordinator, backend, exact with { ActualTarget = changed });
        }
        Assert.Equal(CaptureFailureKind.ManualPriority, (await coordinator.StartAsync(Automatic())).Failure?.Kind);
        Assert.Empty(backend.StopCalls);
        Assert.True((await coordinator.StopOwnedManualAsync(exact)).Succeeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task UnsupportedOwnerKindsCannotUseScopedStopEvenForExactRetainedContext(int ownerKind)
    {
        var backend = new Backend { FailStartWithOwnership = true };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        var request = ownerKind switch
        {
            0 => Copy(Request(10, 1), initiation: CaptureInitiation.Automatic),
            1 => Copy(Request(10, 2), initiation: CaptureInitiation.ConfiguredStartup),
            2 => Copy(Automatic(), initiation: CaptureInitiation.Manual),
            _ => Copy(Request(10), target: new UnknownTarget()),
        };
        var start = await coordinator.StartAsync(request);
        Assert.Equal(CaptureCommandDisposition.Failed, start.Disposition);
        var retained = Assert.IsType<CaptureSessionContext>(start.After.Context);
        await AssertRejectedUnchanged(coordinator, backend, retained);
        Assert.Empty(backend.StopCalls);
        // The existing shutdown owner still performs cleanup; scoped Stop does not widen.
        Assert.True((await coordinator.ShutdownAsync()).Succeeded);
        Assert.Single(backend.StopCalls);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task QueuedStopRechecksOwnerUnderSerializationAndCancellationHasNoEffect(int kind, bool cancel)
    {
        var backend = new Backend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(Request(10, kind))).Succeeded);
        var first = coordinator.Snapshot.Context!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.StartHandler = async context =>
        {
            entered.TrySetResult();
            await release.Task;
            return CaptureBackendStartResult.Success(context.RequestedTarget);
        };
        using var cancellation = new CancellationTokenSource();
        var switching = coordinator.SwitchAsync(Request(20, kind));
        Task<CaptureTransitionResult>? stopping = null;
        try
        {
            await entered.Task.WaitAsync(TestTimeouts.Signal);
            Assert.Equal(CaptureLifecycleStatus.Starting, coordinator.Snapshot.Status);
            stopping = coordinator.StopOwnedManualAsync(first, cancellation.Token);
            Assert.False(stopping.IsCompleted);
            Assert.Single(backend.StopCalls); // Only switch cleanup of A has run.
            if (cancel)
            {
                cancellation.Cancel();
                Assert.Equal(CaptureFailureKind.Cancelled,
                    (await stopping.WaitAsync(TestTimeouts.Signal)).Failure?.Kind);
            }
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.True((await switching.WaitAsync(TestTimeouts.Signal)).Succeeded);
        var second = coordinator.Snapshot.Context!;
        var stopped = await stopping!.WaitAsync(TestTimeouts.Signal);
        Assert.Equal(cancel ? CaptureCommandDisposition.Failed : CaptureCommandDisposition.Rejected, stopped.Disposition);
        Assert.Same(second, coordinator.Snapshot.Context);
        Assert.Single(backend.StopCalls);
        Assert.True((await coordinator.StopOwnedManualAsync(second)).Succeeded);
        Assert.Equal(2, backend.StopCalls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PreCancelledStopHasNoEffectAndCancellationAfterDispatchRetainsCleanup(int kind)
    {
        var backend = new Backend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(Request(10, kind))).Succeeded);
        var exact = coordinator.Snapshot.Context!;
        var before = coordinator.Snapshot;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await coordinator.StopOwnedManualAsync(exact, cancellation.Token);
        Assert.Equal(CaptureFailureKind.Cancelled, cancelled.Failure?.Kind);
        Assert.Same(before, coordinator.Snapshot);
        Assert.Empty(backend.StopCalls);

        backend.CancelStop = true;
        var failed = await coordinator.StopOwnedManualAsync(exact);
        Assert.Equal(CaptureCommandDisposition.Failed, failed.Disposition);
        Assert.Equal(CaptureFailureKind.Cancelled, failed.Failure?.Kind);
        Assert.Same(exact, failed.After.Context);
        Assert.Equal(CaptureLifecycleStatus.Faulted, failed.After.Status);
        Assert.Equal(CaptureFailureKind.ManualPriority, (await coordinator.StartAsync(Automatic())).Failure?.Kind);
        backend.CancelStop = false;
        Assert.True((await coordinator.StopOwnedManualAsync(exact)).Succeeded);
        Assert.Equal(2, backend.StopCalls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedStartRetainsExactOwnerForScopedCleanup(int kind)
    {
        var backend = new Backend { FailStartWithOwnership = true };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        var result = await coordinator.StartAsync(Request(10, kind));
        Assert.Equal(CaptureCommandDisposition.Failed, result.Disposition);
        var exact = Assert.IsType<CaptureSessionContext>(result.After.Context);
        Assert.Equal(CaptureLifecycleStatus.Faulted, result.After.Status);
        await AssertRejectedUnchanged(coordinator, backend, exact with { SessionId = Guid.NewGuid() });
        Assert.True((await coordinator.StopOwnedManualAsync(exact)).Succeeded);
        Assert.Single(backend.StopCalls);
    }

    [Fact]
    public async Task ResolvedDisplayMetadataDoesNotStrandItsExactOwner()
    {
        var request = Request(10, 1);
        var resolved = ((DisplayCaptureTarget)request.Target) with { DisplayName = "Resolved", MonitorIndex = 99 };
        var backend = new Backend { ForcedActualTarget = resolved };
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(request)).Succeeded);
        var exact = coordinator.Snapshot.Context!;
        Assert.NotEqual(exact.RequestedTarget, exact.ActualTarget);
        await AssertRejectedUnchanged(coordinator, backend, exact with { ActualTarget = exact.RequestedTarget });
        Assert.True((await coordinator.StopOwnedManualAsync(exact)).Succeeded);
        Assert.Single(backend.StopCalls);
        Assert.Same(exact, backend.StopCalls[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnsupportedCommandsAndStaleSourceLossNeverAlterManualOwner(int kind)
    {
        var backend = new Backend();
        await using var coordinator = new CaptureLifecycleCoordinator(backend);
        Assert.True((await coordinator.StartAsync(Request(10, kind))).Succeeded);
        var before = coordinator.Snapshot;
        var exact = before.Context!;
        foreach (var result in new[] { await coordinator.PauseAsync(), await coordinator.ResumeAsync(), await coordinator.SplitAsync() })
        {
            Assert.Equal(CaptureCommandDisposition.Unsupported, result.Disposition);
            Assert.Same(before, result.After);
        }
        Assert.Equal(CaptureCommandDisposition.Ignored,
            (await coordinator.HandleSourceLostAsync(new(exact.SessionId, exact.Generation + 1,
                exact.ActualTarget, DateTimeOffset.UnixEpoch))).Disposition);
        Assert.Same(before, coordinator.Snapshot);
        Assert.Equal(CaptureFailureKind.ManualPriority, (await coordinator.StartAsync(Automatic())).Failure?.Kind);
        Assert.Empty(backend.StopCalls);
    }

    private static async Task AssertRejectedUnchanged(CaptureLifecycleCoordinator coordinator, Backend backend,
        CaptureSessionContext expected)
    {
        var before = coordinator.Snapshot;
        var calls = backend.StopCalls.Count;
        var result = await coordinator.StopOwnedManualAsync(expected);
        Assert.Equal(CaptureCommandDisposition.Rejected, result.Disposition);
        Assert.Same(before, result.After);
        Assert.Same(before, coordinator.Snapshot);
        Assert.Equal(calls, backend.StopCalls.Count);
    }

    private static CaptureRequest Copy(CaptureRequest request, Guid? id = null, DateTimeOffset? time = null,
        CaptureInitiation? initiation = null, CaptureTarget? target = null, WebcamCaptureSelection? webcam = null,
        CaptureStartPolicy? policy = null) =>
        new(id ?? request.RequestId, time ?? request.RequestedAt, initiation ?? request.Initiation,
            target ?? request.Target, webcam ?? request.Webcam, policy ?? request.StartPolicy);

    private static CaptureTarget[] ChangedTargets(CaptureTarget target) => target switch
    {
        WindowCaptureTarget w => [w with { ProcessId = w.ProcessId + 1 }, w with { WindowHandle = new(9999) },
            w with { WindowTitle = "Other" }, w with { ExecutableName = "other.exe" }, w with { WindowClass = "Other" },
            new DisplayCaptureTarget("display-10")],
        DisplayCaptureTarget d => [d with { StableDisplayId = "other" }, d with { DisplayName = "Other" },
            d with { MonitorIndex = 99 }, new RegionCaptureTarget(d.StableDisplayId, new(0, 0, 10, 10), new(0, 0, 100, 100))],
        RegionCaptureTarget r => [r with { OwningDisplayId = "other" },
            r with { PhysicalBounds = r.PhysicalBounds with { X = r.PhysicalBounds.X + 1 } },
            r with { PhysicalBounds = r.PhysicalBounds with { Y = r.PhysicalBounds.Y + 1 } },
            r with { PhysicalBounds = r.PhysicalBounds with { Width = r.PhysicalBounds.Width + 1 } },
            r with { PhysicalBounds = r.PhysicalBounds with { Height = r.PhysicalBounds.Height + 1 } },
            r with { OwningDisplayBounds = r.OwningDisplayBounds with { X = r.OwningDisplayBounds.X + 1 } },
            r with { OwningDisplayBounds = r.OwningDisplayBounds with { Y = r.OwningDisplayBounds.Y + 1 } },
            r with { OwningDisplayBounds = r.OwningDisplayBounds with { Width = r.OwningDisplayBounds.Width + 1 } },
            r with { OwningDisplayBounds = r.OwningDisplayBounds with { Height = r.OwningDisplayBounds.Height + 1 } },
            new DisplayCaptureTarget(r.OwningDisplayId)],
        _ => throw new ArgumentOutOfRangeException(nameof(target)),
    };

    private static CaptureRequest Automatic() =>
        new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, CaptureInitiation.Automatic,
            new GameCaptureTarget("game", 22, "game.exe", DateTimeOffset.UnixEpoch));

    private static CaptureRequest Request(int pid, int kind = 0) =>
        new(Guid.NewGuid(), DateTimeOffset.UnixEpoch, CaptureInitiation.Manual,
            kind switch
            {
                0 => new WindowCaptureTarget(pid, new IntPtr(pid * 100), $"Window {pid}", "manual.exe", "ManualClass"),
                1 => new DisplayCaptureTarget($"display-{pid}", $"Display {pid}", pid),
                _ => new RegionCaptureTarget($"display-{pid}", new(-90, 10, 20, 20), new(-100, 0, 100, 100)),
            });

    private sealed record UnknownTarget : CaptureTarget;

    private sealed class Backend : ICaptureLifecycleBackend
    {
        public bool FailStop { get; set; }
        public bool CancelStop { get; set; }
        public bool FailStartWithOwnership { get; set; }
        public Func<CaptureSessionContext, Task<CaptureBackendStartResult>>? StartHandler { get; set; }
        public bool ReleaseOnStopFailure { get; set; }
        public CaptureTarget? ForcedActualTarget { get; set; }
        public List<CaptureSessionContext> StopCalls { get; } = [];
        public Task<CaptureBackendStartResult> StartAsync(CaptureSessionContext context, CancellationToken cancellationToken) =>
            StartHandler?.Invoke(context) ?? Task.FromResult(FailStartWithOwnership
                ? CaptureBackendStartResult.Failed(new(CaptureFailureKind.Backend, "fixture.start", "start failed"),
                    ForcedActualTarget ?? context.RequestedTarget)
                : CaptureBackendStartResult.Success(ForcedActualTarget ?? context.RequestedTarget));
        public Task<CaptureBackendCommandResult> StopAsync(CaptureSessionContext context, CancellationToken cancellationToken)
        {
            StopCalls.Add(context);
            if (CancelStop) throw new OperationCanceledException(cancellationToken);
            return Task.FromResult(FailStop
                ? CaptureBackendCommandResult.Failed(new(CaptureFailureKind.Backend, "fixture.stop", "cleanup failed"),
                    cleanupRequired: !ReleaseOnStopFailure)
                : CaptureBackendCommandResult.Success());
        }
        public Task<CaptureBackendSaveResult> SaveAsync(CaptureSessionContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
