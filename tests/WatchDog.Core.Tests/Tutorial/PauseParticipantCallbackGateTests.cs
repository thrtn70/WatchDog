using System.Collections;
using WatchDog.Core.Audio;
using WatchDog.Core.Capture;
using WatchDog.Core.Settings;
using WatchDog.Core.Tests.Helpers;
using WatchDog.Core.Tutorial;

namespace WatchDog.Core.Tests.Tutorial;

public sealed class PauseParticipantCallbackGateTests
{
    [Fact]
    public async Task ExactRegistrationEntersWhileForeignAndStaleTokensRejectWithoutPoisoning()
    {
        var fixture = CreateFixture();
        var foreign = CreateFixture().Registration;
        var stale = new PauseParticipantRegistration(
            fixture.Command,
            fixture.Command.Outputs[0],
            fixture.Registration.RegistrationGeneration + 1);

        Assert.False(fixture.Gate.TryEnter(foreign, out var foreignLease));
        Assert.Null(foreignLease);
        Assert.False(fixture.Gate.TryEnter(stale, out var staleLease));
        Assert.Null(staleLease);
        Assert.False(fixture.Gate.HasInvalidEvidence);

        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var lease));
        Assert.NotNull(lease);
        try
        {
            Assert.True(lease.TryCopy(fixture.Acknowledgment));
        }
        finally
        {
            lease.Dispose();
        }

        await fixture.Gate.CloseAsync().WaitAsync(TestTimeouts.Signal);
        Assert.True(fixture.Gate.TryGetAcknowledgment(out var copied));
        AssertAcknowledgmentEqual(fixture.Acknowledgment, copied);
    }

    [Fact]
    public async Task CloseClosesAdmissionImmediatelyAndDrainsCallbackThatCopiesAfterClose()
    {
        var fixture = CreateFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callback = Task.Run(async () =>
        {
            Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var lease));
            Assert.NotNull(lease);
            entered.TrySetResult();
            try
            {
                await release.Task;
                Assert.True(lease.TryCopy(fixture.Acknowledgment));
            }
            finally
            {
                lease.Dispose();
            }
        });

        await entered.Task.WaitAsync(TestTimeouts.Signal);
        var close = fixture.Gate.CloseAsync();
        try
        {
            Assert.True(fixture.Gate.IsClosed);
            Assert.False(fixture.Gate.IsDrained);
            Assert.False(close.IsCompleted);
            Assert.False(fixture.Gate.TryEnter(fixture.Registration, out var rejected));
            Assert.Null(rejected);
            Assert.False(fixture.Gate.TryGetAcknowledgment(out _));
        }
        finally
        {
            release.TrySetResult();
        }

        await callback.WaitAsync(TestTimeouts.Signal);
        await close.WaitAsync(TestTimeouts.Signal);
        Assert.True(fixture.Gate.IsDrained);
        Assert.True(fixture.Gate.TryGetAcknowledgment(out var copied));
        AssertAcknowledgmentEqual(fixture.Acknowledgment, copied);
    }

    [Fact]
    public async Task CanceledCloseWaitDoesNotCancelTheUnderlyingCloseOrDrain()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var lease));
        Assert.NotNull(lease);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Gate.CloseAsync(canceled.Token));
        Assert.True(fixture.Gate.IsClosed);
        Assert.False(fixture.Gate.IsDrained);

        lease.Dispose();
        await fixture.Gate.CloseAsync().WaitAsync(TestTimeouts.Signal);
        Assert.True(fixture.Gate.IsDrained);
    }

    [Fact]
    public async Task LeaseDisposeIsIdempotentAndCannotCompleteDrainWhileAnotherLeaseIsActive()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var first));
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var second));
        Assert.NotNull(first);
        Assert.NotNull(second);
        var close = fixture.Gate.CloseAsync();

        first.Dispose();
        first.Dispose();
        Assert.False(close.IsCompleted);
        Assert.False(fixture.Gate.IsDrained);

        second.Dispose();
        await close.WaitAsync(TestTimeouts.Signal);
        Assert.True(fixture.Gate.IsDrained);
    }

    [Fact]
    public async Task EntryBeyondDeclaredStreamCapacityLatchesInvalidEvidence()
    {
        var fixture = CreateFixture();
        var leases = new List<PauseParticipantCallbackLease>();
        try
        {
            for (var index = 0; index < fixture.Registration.Streams.Count; index++)
            {
                Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var lease));
                Assert.NotNull(lease);
                leases.Add(lease);
            }

            Assert.False(fixture.Gate.TryEnter(fixture.Registration, out var excess));
            Assert.Null(excess);
            Assert.True(fixture.Gate.HasInvalidEvidence);
        }
        finally
        {
            foreach (var lease in leases)
                lease.Dispose();
        }

        await fixture.Gate.CloseAsync().WaitAsync(TestTimeouts.Signal);
        Assert.False(fixture.Gate.TryGetAcknowledgment(out _));
    }

    [Fact]
    public async Task ASecondCopyLatchesInvalidEvidenceEvenWhenTheAcknowledgmentIsIdentical()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var lease));
        Assert.NotNull(lease);
        try
        {
            Assert.True(lease.TryCopy(fixture.Acknowledgment));
            Assert.False(lease.TryCopy(fixture.Acknowledgment));
            Assert.True(fixture.Gate.HasInvalidEvidence);
        }
        finally
        {
            lease.Dispose();
        }

        await fixture.Gate.CloseAsync().WaitAsync(TestTimeouts.Signal);
        Assert.False(fixture.Gate.TryGetAcknowledgment(out _));
    }

    [Fact]
    public async Task CopiedAcknowledgmentUsesBoundedIndexReadsAndFreezesCallerOwnedEffects()
    {
        var fixture = CreateFixture();
        var callerEffects = fixture.Acknowledgment.Streams.ToArray();
        var indexOnly = new IndexOnlyList<PauseStreamEffect>(callerEffects);
        var input = fixture.Acknowledgment with { Streams = indexOnly };
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var lease));
        Assert.NotNull(lease);
        try
        {
            Assert.True(lease.TryCopy(input));
            Assert.Equal(callerEffects.Length, indexOnly.IndexReads);
            Assert.Equal(0, indexOnly.EnumeratorRequests);
            callerEffects[0] = callerEffects[0] with
            {
                InputBoundaryIndex = callerEffects[0].InputBoundaryIndex + 1000,
            };
        }
        finally
        {
            lease.Dispose();
        }

        Assert.False(fixture.Gate.TryGetAcknowledgment(out _));
        await fixture.Gate.CloseAsync().WaitAsync(TestTimeouts.Signal);
        Assert.True(fixture.Gate.TryGetAcknowledgment(out var copied));
        Assert.NotNull(copied);
        Assert.NotSame(input, copied);
        Assert.NotSame(input.Streams, copied.Streams);
        Assert.Equal(fixture.Acknowledgment.Streams[0], copied.Streams[0]);
        Assert.Throws<NotSupportedException>(() =>
            ((IList<PauseStreamEffect>)copied.Streams).Clear());
    }

    [Fact]
    public async Task ThrowingCopyLatchesInvalidEvidenceAndFinallyReturnedLeaseStillDrains()
    {
        var fixture = CreateFixture();
        var throwing = fixture.Acknowledgment with
        {
            Streams = new ThrowingIndexList<PauseStreamEffect>(fixture.Acknowledgment.Streams.Count),
        };
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var lease));
        Assert.NotNull(lease);
        var close = fixture.Gate.CloseAsync();
        try
        {
            Assert.False(lease.TryCopy(throwing));
            Assert.True(fixture.Gate.HasInvalidEvidence);
        }
        finally
        {
            lease.Dispose();
        }

        await close.WaitAsync(TestTimeouts.Signal);
        Assert.True(fixture.Gate.IsDrained);
        Assert.False(fixture.Gate.TryGetAcknowledgment(out _));
    }

    [Fact]
    public async Task NestedSecondLeaseCopyDuringCallerReadLatchesInvalidAndPublishesNeitherAcknowledgment()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var first));
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var second));
        Assert.NotNull(first);
        Assert.NotNull(second);
        bool? nestedResult = null;
        var nestedCalls = 0;
        var outerEffects = fixture.Acknowledgment.Streams
            .Select(effect => effect with { InputBoundaryIndex = effect.InputBoundaryIndex + 10 })
            .ToArray();
        var outer = fixture.Acknowledgment with
        {
            EffectiveBoundary = fixture.Acknowledgment.EffectiveBoundary + TimeSpan.FromSeconds(1),
            Streams = new CallbackCountList<PauseStreamEffect>(outerEffects, () =>
            {
                nestedCalls++;
                nestedResult = second.TryCopy(fixture.Acknowledgment);
            }),
        };

        try
        {
            Assert.False(first.TryCopy(outer));
            Assert.Equal(false, nestedResult);
            Assert.Equal(1, nestedCalls);
            Assert.True(fixture.Gate.HasInvalidEvidence);
        }
        finally
        {
            first.Dispose();
            second.Dispose();
        }

        await fixture.Gate.CloseAsync().WaitAsync(TestTimeouts.Signal);
        Assert.False(fixture.Gate.TryGetAcknowledgment(out _));
    }

    [Fact]
    public async Task CloseCanAcquireGateWhileCallerStreamCountIsBlockedOnAnotherThread()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var lease));
        Assert.NotNull(lease);
        var countEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCount = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closeReturned = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var streams = new BlockingCountList<PauseStreamEffect>(
            fixture.Acknowledgment.Streams.ToArray(),
            countEntered,
            releaseCount);
        var copy = Task.Run(() => lease.TryCopy(fixture.Acknowledgment with { Streams = streams }));

        await countEntered.Task.WaitAsync(TestTimeouts.Signal);
        var closeInvoker = Task.Run(() =>
        {
            var close = fixture.Gate.CloseAsync();
            closeReturned.TrySetResult(close);
        });
        var closeReturnedBeforeReaderRelease = false;
        try
        {
            await closeReturned.Task.WaitAsync(TestTimeouts.Signal);
            closeReturnedBeforeReaderRelease = true;
            Assert.True(fixture.Gate.IsClosed);
            Assert.False(fixture.Gate.IsDrained);
        }
        catch (TimeoutException)
        {
            // The assertion below reports the lock-order regression after cleanup unblocks both tasks.
        }
        finally
        {
            releaseCount.TrySetResult();
        }

        Assert.True(await copy.WaitAsync(TestTimeouts.Signal));
        await closeInvoker.WaitAsync(TestTimeouts.Signal);
        lease.Dispose();
        await (await closeReturned.Task.WaitAsync(TestTimeouts.Signal))
            .WaitAsync(TestTimeouts.Signal);
        Assert.True(closeReturnedBeforeReaderRelease);
        Assert.True(fixture.Gate.IsDrained);
    }

    [Fact]
    public async Task ReentrantCloseAndDisposeDuringCallerReadKeepLeaseCountedUntilCopyReturns()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var lease));
        Assert.NotNull(lease);
        Task? close = null;
        bool? drainedDuringRead = null;
        var streams = new CallbackCountList<PauseStreamEffect>(
            fixture.Acknowledgment.Streams.ToArray(),
            () =>
            {
                close = fixture.Gate.CloseAsync();
                lease.Dispose();
                drainedDuringRead = fixture.Gate.IsDrained;
                Assert.False(close.IsCompleted);
            });

        try
        {
            Assert.True(lease.TryCopy(fixture.Acknowledgment with { Streams = streams }));
        }
        finally
        {
            lease.Dispose();
        }

        Assert.Equal(false, drainedDuringRead);
        Assert.NotNull(close);
        await close.WaitAsync(TestTimeouts.Signal);
        Assert.True(fixture.Gate.IsDrained);
        Assert.True(fixture.Gate.TryGetAcknowledgment(out _));
    }

    [Fact]
    public async Task MalformedAdmittedEvidenceLatchesInvalidWhileZeroLeaseCloseIsIdempotent()
    {
        var fixture = CreateFixture();
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var lease));
        Assert.NotNull(lease);
        try
        {
            var malformed = fixture.Acknowledgment with { OutputEpoch = Guid.NewGuid() };
            Assert.False(lease.TryCopy(malformed));
        }
        finally
        {
            lease.Dispose();
        }

        await fixture.Gate.CloseAsync().WaitAsync(TestTimeouts.Signal);
        await fixture.Gate.CloseAsync().WaitAsync(TestTimeouts.Signal);
        Assert.True(fixture.Gate.IsClosed);
        Assert.True(fixture.Gate.IsDrained);
        Assert.True(fixture.Gate.HasInvalidEvidence);
        Assert.False(fixture.Gate.TryGetAcknowledgment(out _));

        var empty = CreateFixture().Gate;
        await empty.CloseAsync().WaitAsync(TestTimeouts.Signal);
        await empty.CloseAsync().WaitAsync(TestTimeouts.Signal);
        Assert.True(empty.IsClosed);
        Assert.True(empty.IsDrained);
        Assert.False(empty.HasInvalidEvidence);
    }

    [Fact]
    public void DiagnosticStringsDoNotExposeRegistrationOrAcknowledgmentData()
    {
        var fixture = CreateFixture();
        Assert.Equal(nameof(PauseParticipantCallbackGate), fixture.Gate.ToString());
        Assert.True(fixture.Gate.TryEnter(fixture.Registration, out var lease));
        Assert.NotNull(lease);
        try
        {
            Assert.Equal(nameof(PauseParticipantCallbackLease), lease.ToString());
        }
        finally
        {
            lease.Dispose();
        }
    }

    private static GateFixture CreateFixture(long registrationGeneration = 1)
    {
        var settings = new AppSettings();
        var target = new WindowCaptureTarget(
            23,
            new IntPtr(234),
            "Tutorial",
            "tutorial.exe",
            "TutorialClass");
        var plan = TutorialRecordingPolicy.Create(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            target,
            CaptureStartupBaselineResult.Create(settings)).Plan!;
        var context = new CaptureSessionContext(
            Guid.NewGuid(),
            1,
            plan.Request,
            plan.Request.Target,
            plan.Request.Target);
        var state = PauseCommandModel.CreateInitial(context, Guid.NewGuid());
        var anchors = new List<PauseStreamAnchor>
        {
            new(new(PauseStreamKind.ScreenVideo, 0), Clock(), MediaTiming(PauseDecodedUnitKind.VideoFrame)),
        };
        anchors.AddRange(plan.AudioTracks.Encoders.Select(route =>
            new PauseStreamAnchor(
                new PauseStreamId(PauseStreamKind.ProductAudioTrack, route.OutputTrackIndex),
                Clock(), MediaTiming(PauseDecodedUnitKind.AudioSample))));
        var capability = new PauseOutputCapability(
            PauseOutputKind.Screen,
            Guid.NewGuid(),
            CanPause: true,
            CanResume: true,
            anchors,
            state.Identity,
            CameraDeviceId: null);
        var snapshot = new CaptureLifecycleSnapshot(
            CaptureLifecycleStatus.Active,
            CaptureReadiness.Available,
            CaptureHealth.Healthy,
            context,
            null);
        var command = PauseCommandPolicy.Plan(
            plan,
            snapshot,
            state,
            PauseCommandAction.Pause,
            Guid.NewGuid(),
            1,
            [capability]).Command!;
        var registration = new PauseParticipantRegistration(
            command,
            command.Outputs[0],
            registrationGeneration);
        var acknowledgment = new PauseOutputAck(
            command.OperationId,
            command.Sequence,
            command.Identity,
            command.Action,
            registration.Kind,
            registration.OutputEpoch,
            PauseOutputEffect.Applied,
            TimeSpan.FromSeconds(1),
            registration.Streams.Select(stream =>
                new PauseStreamEffect(stream.Id, stream.InputClock.TransformId, 10)).ToArray());
        return new(command, registration, new PauseParticipantCallbackGate(registration), acknowledgment);
    }

    private static PauseInputClock Clock() => new(Guid.NewGuid(), 0, 0, 1, 1, 10);

    // Explicit synthetic media periods, independent of the fixture's capture clock.
    private static PauseMediaTimingDeclaration MediaTiming(PauseDecodedUnitKind kind) =>
        new(Guid.NewGuid(), kind, 1, kind == PauseDecodedUnitKind.VideoFrame ? 60 : 48_000);

    private static void AssertAcknowledgmentEqual(PauseOutputAck expected, PauseOutputAck? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.OperationId, actual.OperationId);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.Identity, actual.Identity);
        Assert.Equal(expected.Action, actual.Action);
        Assert.Equal(expected.Kind, actual.Kind);
        Assert.Equal(expected.OutputEpoch, actual.OutputEpoch);
        Assert.Equal(expected.Effect, actual.Effect);
        Assert.Equal(expected.EffectiveBoundary, actual.EffectiveBoundary);
        Assert.Equal(expected.Streams.ToArray(), actual.Streams.ToArray());
    }

    private sealed record GateFixture(
        PauseCommand Command,
        PauseParticipantRegistration Registration,
        PauseParticipantCallbackGate Gate,
        PauseOutputAck Acknowledgment);

    private sealed class IndexOnlyList<T>(T[] items) : IReadOnlyList<T>
    {
        public int Count => items.Length;
        public int IndexReads { get; private set; }
        public int EnumeratorRequests { get; private set; }

        public T this[int index]
        {
            get
            {
                IndexReads++;
                return items[index];
            }
        }

        public IEnumerator<T> GetEnumerator()
        {
            EnumeratorRequests++;
            throw new InvalidOperationException("The bounded copy must not enumerate caller evidence.");
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class ThrowingIndexList<T>(int count) : IReadOnlyList<T>
    {
        public int Count => count;
        public T this[int index] => throw new InvalidOperationException("Fake callback storage became unreadable.");
        public IEnumerator<T> GetEnumerator() =>
            throw new InvalidOperationException("The bounded copy must not enumerate caller evidence.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CallbackCountList<T>(T[] items, Action onCount) : IReadOnlyList<T>
    {
        private bool _callbackInvoked;
        public int Count
        {
            get
            {
                if (!_callbackInvoked)
                {
                    _callbackInvoked = true;
                    onCount();
                }
                return items.Length;
            }
        }

        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class BlockingCountList<T>(
        T[] items,
        TaskCompletionSource entered,
        TaskCompletionSource release) : IReadOnlyList<T>
    {
        public int Count
        {
            get
            {
                entered.TrySetResult();
                release.Task.GetAwaiter().GetResult();
                return items.Length;
            }
        }

        public T this[int index] => items[index];
        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)items).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
