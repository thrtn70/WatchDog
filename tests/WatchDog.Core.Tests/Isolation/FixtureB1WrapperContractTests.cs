using System.Text;
using System.Text.Json;
using WatchDog.CaptureIsolation.Windows.Qualification;
using WatchDog.Core.Tests.Helpers;

namespace WatchDog.Core.Tests.Isolation;

public sealed class FixtureB1WrapperContractTests
{
    private static readonly Guid RunNonce =
        Guid.Parse("aaaaaaaa-2222-3333-4444-555555555555");
    private static readonly Guid LaunchId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly FixtureHandoffProcessIdentity Launcher = new(1234, 987654321);

    [Fact]
    public async Task FragmentedTerminalRecordAndConcurrentStderrDrainReturnParsedStatusOnly()
    {
        var process = new FakeProcess(
            Launcher,
            new FragmentedReadStream(Status(QualificationExitCode.Pass), 3),
            new FragmentedReadStream(Encoding.UTF8.GetBytes("diagnostic"), 2),
            QualificationExitCode.Pass);

        var result = await ObserveAsync(process);

        Assert.Equal(FixtureB1WrapperDisposition.Parsed, result.Disposition);
        Assert.Equal(QualificationExitCode.Pass, result.ExitCode);
        Assert.NotNull(result.ParsedStatus);
        Assert.Equal(RunNonce, result.ParsedStatus.RunNonce);
        Assert.Equal(10, result.StandardErrorBytesObserved);
        Assert.False(result.TerminateAndReapAttempted);
        Assert.True(result.OutputDrainCompleted);
        Assert.True(result.ObservationOperationsStopped);
        Assert.Equal(FixtureB1WrapperCloseReport.Complete, result.CloseReport);
        Assert.False(result.AdapterRetainsOwnership);
        Assert.Equal(1, process.CloseCalls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExtraRecordOrInvalidUtf8ReturnsExplicitCodeFiveUncertainty(
        bool appendExtraRecord)
    {
        var stdout = Status(QualificationExitCode.Pass);
        if (appendExtraRecord)
            stdout = [.. stdout, .. Status(QualificationExitCode.Pass)];
        else
            stdout[stdout.AsSpan().IndexOf("passed"u8)] = 0xff;
        var process = new FakeProcess(
            Launcher, new MemoryStream(stdout), new MemoryStream(), QualificationExitCode.Pass);

        var result = await ObserveAsync(process);

        Assert.Equal(FixtureB1WrapperDisposition.InvalidTerminalRecord, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.Null(result.ParsedStatus);
        Assert.Equal(1, process.TerminateAndReapCalls);
        Assert.Equal(1, process.CloseCalls);
        Assert.Equal(FixtureB1WrapperCloseReport.Complete, result.CloseReport);
    }

    [Fact]
    public async Task MissingTerminalRecordAfterExitAndBothEofsStillRunsCleanup()
    {
        var process = new FakeProcess(
            Launcher,
            new MemoryStream(),
            new MemoryStream(),
            QualificationExitCode.Pass);

        var result = await ObserveAsync(process);

        Assert.Equal(FixtureB1WrapperDisposition.InvalidTerminalRecord, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.Null(result.ParsedStatus);
        Assert.Equal(1, process.TerminateAndReapCalls);
        Assert.Equal(1, process.CloseCalls);
        Assert.True(result.OutputDrainCompleted);
    }

    [Fact]
    public async Task FullStderrCapacityDoesNotBlockStdoutOrProcessProgress()
    {
        var process = new FakeProcess(
            Launcher,
            new FragmentedReadStream(Status(QualificationExitCode.Pass), 1),
            new FragmentedReadStream(new byte[FixtureB1WrapperContract.MaximumStandardErrorBytes], 257),
            QualificationExitCode.Pass);

        var result = await ObserveAsync(process);

        Assert.Equal(FixtureB1WrapperDisposition.Parsed, result.Disposition);
        Assert.Equal(FixtureB1WrapperContract.MaximumStandardErrorBytes,
            result.StandardErrorBytesObserved);
    }

    [Fact]
    public async Task StdoutAndStderrReadsBeginConcurrently()
    {
        var barrier = new TwoReaderBarrier();
        var process = new FakeProcess(
            Launcher,
            new BarrierReadStream(Status(QualificationExitCode.Pass), barrier),
            new BarrierReadStream(Encoding.UTF8.GetBytes("stderr"), barrier),
            QualificationExitCode.Pass);

        var result = await ObserveAsync(process, overallDeadline: TimeSpan.FromSeconds(2));

        Assert.Equal(FixtureB1WrapperDisposition.Parsed, result.Disposition);
        Assert.Equal(2, barrier.ReaderCount);
    }

    [Fact]
    public async Task StderrOverflowTerminatesReapsAndReturnsCodeFive()
    {
        var process = new FakeProcess(
            Launcher,
            new MemoryStream(Status(QualificationExitCode.Pass)),
            new MemoryStream(new byte[FixtureB1WrapperContract.MaximumStandardErrorBytes + 1]),
            QualificationExitCode.Pass);

        var result = await ObserveAsync(process);

        Assert.Equal(FixtureB1WrapperDisposition.OutputLimitExceeded, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.Null(result.ParsedStatus);
        Assert.True(result.TerminateAndReapAttempted);
        Assert.True(result.TerminateAndReapCompleted);
        Assert.Equal(1, process.TerminateAndReapCalls);
    }

    [Fact]
    public async Task StdoutBytePastTerminalLimitTerminatesReapsAndReturnsCodeFive()
    {
        var process = new FakeProcess(
            Launcher,
            new MemoryStream(new byte[FixtureB1WrapperContract.MaximumStandardOutputBytes + 1]),
            new MemoryStream(),
            QualificationExitCode.Pass);

        var result = await ObserveAsync(process);

        Assert.Equal(FixtureB1WrapperDisposition.OutputLimitExceeded, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.Null(result.ParsedStatus);
        Assert.True(result.TerminateAndReapCompleted);
        Assert.Equal(1, process.CloseCalls);
    }

    [Fact]
    public async Task StatusObservedBeforeExitIsRejectedWhenProcessLaterCrashes()
    {
        var exit = new TaskCompletionSource<QualificationExitCode>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess(
            Launcher,
            new MemoryStream(Status(QualificationExitCode.Pass)),
            new MemoryStream(),
            exit.Task);

        var pending = ObserveAsync(process);
        await Task.Yield();
        Assert.False(pending.IsCompleted);

        exit.SetResult(QualificationExitCode.InfrastructureExceptionCleanupConfirmed);
        var result = await pending;

        Assert.Equal(FixtureB1WrapperDisposition.InvalidTerminalRecord, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.Null(result.ParsedStatus);
        Assert.Equal(1, process.TerminateAndReapCalls);
        Assert.Equal(1, process.CloseCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CloseFailureOrTimeoutNeverReturnsParsedStatus(bool ignoreCancellation)
    {
        var never = new TaskCompletionSource<FixtureB1WrapperCloseReport>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess(
            Launcher,
            new MemoryStream(Status(QualificationExitCode.Pass)),
            new MemoryStream(),
            Task.FromResult(QualificationExitCode.Pass),
            close: ignoreCancellation
                ? _ => never.Task
                : _ => Task.FromResult(new FixtureB1WrapperCloseReport(
                    ProcessHandleClosed: true,
                    StandardOutputClosed: true,
                    StandardErrorClosed: false,
                    RetainedBinaryStreamsClosed: true)));

        var result = await ObserveAsync(
            process,
            overallDeadline: ignoreCancellation
                ? TimeSpan.FromMilliseconds(500)
                : TimeSpan.FromSeconds(2),
            cleanupDeadline: TimeSpan.FromMilliseconds(25));

        if (ignoreCancellation)
            Assert.True(result.Disposition is FixtureB1WrapperDisposition.TimedOut or
                FixtureB1WrapperDisposition.CloseIncomplete);
        else
            Assert.Equal(FixtureB1WrapperDisposition.CloseIncomplete, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.Null(result.ParsedStatus);
        Assert.True(result.AdapterRetainsOwnership);
        Assert.Equal(1, process.CloseCalls);
    }

    [Fact]
    public async Task SlowNormalCloseCannotExtendOverallWatchdogIntoParsedPass()
    {
        var process = new FakeProcess(
            Launcher,
            new MemoryStream(Status(QualificationExitCode.Pass)),
            new MemoryStream(),
            Task.FromResult(QualificationExitCode.Pass),
            close: async _ =>
            {
                await Task.Delay(1000);
                return FixtureB1WrapperCloseReport.Complete;
            });

        var result = await ObserveAsync(
            process,
            overallDeadline: TimeSpan.FromMilliseconds(250),
            cleanupDeadline: TimeSpan.FromSeconds(2));

        Assert.True(result.Disposition is FixtureB1WrapperDisposition.TimedOut or
            FixtureB1WrapperDisposition.CloseIncomplete);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.Null(result.ParsedStatus);
        Assert.True(result.AdapterRetainsOwnership);
        Assert.Equal(1, process.CloseCalls);
    }

    [Fact]
    public async Task DeadlineWaitsForIndependentTerminateReapAndStreamDrain()
    {
        var stdout = new ReleasableEofStream();
        var stderr = new ReleasableEofStream();
        var exit = new TaskCompletionSource<QualificationExitCode>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess(Launcher, stdout, stderr, exit.Task, async cancellationToken =>
        {
            await allowCleanup.Task.WaitAsync(cancellationToken);
            stdout.Release();
            stderr.Release();
            exit.TrySetResult(QualificationExitCode.CleanupIncomplete);
        });

        var pending = ObserveAsync(
            process, TimeSpan.FromMilliseconds(25), TimeSpan.FromSeconds(2));
        await process.TerminateAndReapEntered.Task.WaitAsync(TestTimeouts.Signal);
        Assert.False(pending.IsCompleted);

        allowCleanup.SetResult();
        var result = await pending;

        Assert.Equal(FixtureB1WrapperDisposition.TimedOut, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.True(result.TerminateAndReapCompleted);
        Assert.True(stdout.EofObserved);
        Assert.True(stderr.EofObserved);
        Assert.Equal(FixtureB1WrapperCloseReport.Complete, result.CloseReport);
    }

    [Fact]
    public async Task CallerCancellationUsesIndependentCleanupTokenAndDrains()
    {
        using var cancellation = new CancellationTokenSource();
        var stdout = new ReleasableEofStream();
        var stderr = new ReleasableEofStream();
        var exit = new TaskCompletionSource<QualificationExitCode>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess(Launcher, stdout, stderr, exit.Task, _ =>
        {
            stdout.Release();
            stderr.Release();
            exit.TrySetResult(QualificationExitCode.CleanupIncomplete);
            return Task.CompletedTask;
        });

        var pending = ObserveAsync(
            process, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2), cancellation.Token);
        cancellation.Cancel();
        var result = await pending;

        Assert.Equal(FixtureB1WrapperDisposition.Cancelled, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.True(result.TerminateAndReapCompleted);
        Assert.NotEqual(cancellation.Token, process.LastTerminateAndReapToken);
        Assert.True(stdout.EofObserved);
        Assert.True(stderr.EofObserved);
    }

    [Fact]
    public async Task NoncooperativeTerminateIsBoundedAndRetainsAdapterOwnership()
    {
        var neverTerminates = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess(
            Launcher,
            new CancellableNeverStream(),
            new CancellableNeverStream(),
            new TaskCompletionSource<QualificationExitCode>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task,
            _ => neverTerminates.Task);

        var result = await ObserveAsync(
                process,
                overallDeadline: TimeSpan.FromMilliseconds(20),
                cleanupDeadline: TimeSpan.FromMilliseconds(25))
            .WaitAsync(TestTimeouts.Signal);

        Assert.Equal(FixtureB1WrapperDisposition.TimedOut, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.True(result.TerminateAndReapAttempted);
        Assert.False(result.TerminateAndReapCompleted);
        Assert.False(result.OutputDrainCompleted);
        Assert.True(result.AdapterRetainsOwnership);
        Assert.Equal(0, process.CloseCalls);
    }

    [Fact]
    public async Task FailedCleanupWithMissingEofReportsUnstoppedObservation()
    {
        var process = new FakeProcess(
            Launcher,
            new ThrowingReadStream(),
            new NoncooperativeNeverStream(),
            new TaskCompletionSource<QualificationExitCode>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task,
            _ => Task.FromException(new InvalidOperationException("cleanup failed")));

        var result = await ObserveAsync(
                process,
                cleanupDeadline: TimeSpan.FromMilliseconds(25))
            .WaitAsync(TestTimeouts.Signal);

        Assert.Equal(FixtureB1WrapperDisposition.StreamFault, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.False(result.TerminateAndReapCompleted);
        Assert.False(result.OutputDrainCompleted);
        Assert.False(result.ObservationOperationsStopped);
        Assert.True(result.AdapterRetainsOwnership);
        Assert.Equal(0, process.CloseCalls);
    }

    [Fact]
    public async Task StreamFaultTriggersTerminateReapBeforeReturning()
    {
        var stderr = new ReleasableEofStream();
        var exit = new TaskCompletionSource<QualificationExitCode>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var process = new FakeProcess(
            Launcher, new ThrowingReadStream(), stderr, exit.Task, _ =>
            {
                stderr.Release();
                exit.TrySetResult(QualificationExitCode.CleanupIncomplete);
                return Task.CompletedTask;
            });

        var result = await ObserveAsync(process);

        Assert.Equal(FixtureB1WrapperDisposition.StreamFault, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.True(result.TerminateAndReapCompleted);
        Assert.True(stderr.EofObserved);
    }

    [Fact]
    public async Task ProcessWaitFaultTriggersTerminateReapAndCodeFive()
    {
        var process = new FakeProcess(
            Launcher,
            new MemoryStream(Status(QualificationExitCode.Pass)),
            new MemoryStream(),
            Task.FromException<QualificationExitCode>(
                new InvalidOperationException("synthetic wait fault")));

        var result = await ObserveAsync(process);

        Assert.Equal(FixtureB1WrapperDisposition.ProcessFault, result.Disposition);
        Assert.Equal(QualificationExitCode.CleanupIncomplete, result.ExitCode);
        Assert.True(result.TerminateAndReapCompleted);
        Assert.True(result.OutputDrainCompleted);
    }

    private static Task<FixtureB1WrapperResult> ObserveAsync(
        FakeProcess process,
        TimeSpan? overallDeadline = null,
        TimeSpan? cleanupDeadline = null,
        CancellationToken cancellationToken = default) =>
        FixtureB1WrapperContract.ObserveAsync(
            RunNonce,
            process,
            overallDeadline ?? TimeSpan.FromSeconds(1),
            cleanupDeadline ?? TimeSpan.FromSeconds(1),
            cancellationToken);

    private static byte[] Status(QualificationExitCode exitCode) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            version = 1,
            scope = FixtureB1Command.StatusScope,
            nonce = RunNonce.ToString("D"),
            launchId = LaunchId.ToString("D"),
            launcherPid = Launcher.ProcessId,
            launcherCreationFileTime = Launcher.CreationFileTime,
            outcome = "passed",
            exitCode = (int)exitCode,
            cleanupConfirmed = true,
        }) + "\n");

    private sealed class FakeProcess : IFixtureB1WrapperProcess
    {
        private readonly Task<QualificationExitCode> _exit;
        private readonly Func<CancellationToken, Task> _terminateAndReap;
        private readonly Func<CancellationToken, Task<FixtureB1WrapperCloseReport>> _close;

        internal FakeProcess(
            FixtureHandoffProcessIdentity identity,
            Stream standardOutput,
            Stream standardError,
            QualificationExitCode exitCode)
            : this(identity, standardOutput, standardError, Task.FromResult(exitCode))
        {
        }

        internal FakeProcess(
            FixtureHandoffProcessIdentity identity,
            Stream standardOutput,
            Stream standardError,
            Task<QualificationExitCode> exit,
            Func<CancellationToken, Task>? terminateAndReap = null,
            Func<CancellationToken, Task<FixtureB1WrapperCloseReport>>? close = null)
        {
            Identity = identity;
            StandardOutput = standardOutput;
            StandardError = standardError;
            _exit = exit;
            _terminateAndReap = terminateAndReap ?? (_ => Task.CompletedTask);
            _close = close ?? (_ => Task.FromResult(FixtureB1WrapperCloseReport.Complete));
        }

        public FixtureHandoffProcessIdentity Identity { get; }
        public Stream StandardOutput { get; }
        public Stream StandardError { get; }
        public int TerminateAndReapCalls { get; private set; }
        public int CloseCalls { get; private set; }
        public CancellationToken LastTerminateAndReapToken { get; private set; }
        public TaskCompletionSource TerminateAndReapEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<QualificationExitCode> WaitForExitAsync(
            CancellationToken cancellationToken) => new(_exit.WaitAsync(cancellationToken));

        public async ValueTask TerminateAndReapAsync(CancellationToken cancellationToken)
        {
            TerminateAndReapCalls++;
            LastTerminateAndReapToken = cancellationToken;
            TerminateAndReapEntered.TrySetResult();
            await _terminateAndReap(cancellationToken);
        }

        public async ValueTask<FixtureB1WrapperCloseReport> CloseAsync(
            CancellationToken cancellationToken)
        {
            CloseCalls++;
            return await _close(cancellationToken);
        }
    }

    private sealed class FragmentedReadStream(byte[] payload, int fragmentSize) : ReadOnlyStream
    {
        private int _offset;

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset == payload.Length)
                return ValueTask.FromResult(0);
            var count = Math.Min(Math.Min(fragmentSize, buffer.Length), payload.Length - _offset);
            payload.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class BarrierReadStream(byte[] payload, TwoReaderBarrier barrier) : ReadOnlyStream
    {
        private bool _read;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read)
                return 0;
            _read = true;
            await barrier.ArriveAsync().WaitAsync(cancellationToken);
            payload.AsMemory().CopyTo(buffer);
            return payload.Length;
        }
    }

    private sealed class ReleasableEofStream : ReadOnlyStream
    {
        private readonly TaskCompletionSource _released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal bool EofObserved { get; private set; }

        internal void Release() => _released.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _released.Task.WaitAsync(cancellationToken);
            EofObserved = true;
            return 0;
        }
    }

    private sealed class ThrowingReadStream : ReadOnlyStream
    {
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new IOException("synthetic read fault"));
    }

    private sealed class CancellableNeverStream : ReadOnlyStream
    {
        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class NoncooperativeNeverStream : ReadOnlyStream
    {
        private readonly TaskCompletionSource<int> _never =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(_never.Task);
    }

    private sealed class TwoReaderBarrier
    {
        private readonly TaskCompletionSource _arrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readerCount;

        internal int ReaderCount => Volatile.Read(ref _readerCount);

        internal Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _readerCount) == 2)
                _arrived.TrySetResult();
            return _arrived.Task;
        }
    }

    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }
}
