using System.Diagnostics;

namespace WatchDog.CaptureIsolation.Windows.Qualification;

internal interface IFixtureB1WrapperProcess
{
    FixtureHandoffProcessIdentity Identity { get; }
    Stream StandardOutput { get; }
    Stream StandardError { get; }

    ValueTask<QualificationExitCode> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Terminates and reaps the owned process and closes its pipe endpoints. The method
    /// must not return while a process or pipe operation owned by the implementation remains.
    /// </summary>
    ValueTask TerminateAndReapAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Closes every owned process, pipe, and retained binary-stream obligation.
    /// </summary>
    ValueTask<FixtureB1WrapperCloseReport> CloseAsync(CancellationToken cancellationToken);
}

internal sealed record FixtureB1WrapperCloseReport(
    bool ProcessHandleClosed,
    bool StandardOutputClosed,
    bool StandardErrorClosed,
    bool RetainedBinaryStreamsClosed)
{
    internal bool IsComplete => ProcessHandleClosed && StandardOutputClosed &&
        StandardErrorClosed && RetainedBinaryStreamsClosed;

    internal static FixtureB1WrapperCloseReport Complete { get; } = new(true, true, true, true);
}

internal enum FixtureB1WrapperDisposition
{
    Parsed,
    InvalidTerminalRecord,
    OutputLimitExceeded,
    TimedOut,
    Cancelled,
    StreamFault,
    ProcessFault,
    CloseIncomplete,
}

/// <summary>
/// Reports bounded wrapper observations. ParsedStatus is syntax attributed to the observed
/// process only; it is not native cleanup, artifact, stream-close, or release evidence.
/// </summary>
internal sealed record FixtureB1WrapperResult(
    FixtureB1WrapperDisposition Disposition,
    QualificationExitCode ExitCode,
    FixtureB1StatusRecord? ParsedStatus,
    int StandardErrorBytesObserved,
    bool TerminateAndReapAttempted,
    bool TerminateAndReapCompleted,
    bool OutputDrainCompleted,
    bool ObservationOperationsStopped,
    FixtureB1WrapperCloseReport? CloseReport,
    bool AdapterRetainsOwnership);

/// <summary>
/// Pure B1 process/stream observation. This contract does not launch a process or enable
/// a native qualification route.
/// </summary>
internal static class FixtureB1WrapperContract
{
    internal const int MaximumStandardOutputBytes = FixtureB1Command.MaximumStatusBytes;
    internal const int MaximumStandardErrorBytes = 64 * 1024;

    internal static async Task<FixtureB1WrapperResult> ObserveAsync(
        Guid expectedRunNonce,
        IFixtureB1WrapperProcess process,
        TimeSpan overallDeadline,
        TimeSpan cleanupDeadline,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (expectedRunNonce == Guid.Empty)
            throw new ArgumentException("A non-empty run nonce is required.", nameof(expectedRunNonce));
        if (!process.Identity.IsValid)
            throw new ArgumentException("The process identity must be valid.", nameof(process));
        if (process.StandardOutput is null || !process.StandardOutput.CanRead ||
            process.StandardError is null || !process.StandardError.CanRead)
            throw new ArgumentException("Readable standard streams are required.", nameof(process));
        ValidateDeadline(overallDeadline, nameof(overallDeadline));
        ValidateDeadline(cleanupDeadline, nameof(cleanupDeadline));

        var startedAt = Stopwatch.GetTimestamp();
        using var observationCancellation = new CancellationTokenSource();
        var observation = new Observation(process, observationCancellation.Token);
        var operation = observation.ObserveAsync();
        using var deadlineCancellation = new CancellationTokenSource();
        var deadline = Task.Delay(overallDeadline, deadlineCancellation.Token);
        var callerCancelled = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), callerCancelled);

        var completed = await Task.WhenAny(operation, deadline, callerCancelled.Task)
            .ConfigureAwait(false);
        if (completed == operation)
        {
            try
            {
                var observed = await operation.ConfigureAwait(false);
                if (!FixtureB1Command.TryParseTerminalStatus(
                    observed.StandardOutput,
                    expectedRunNonce,
                    process.Identity,
                    observed.ExitCode,
                    out var parsedStatus))
                {
                    await deadlineCancellation.CancelAsync().ConfigureAwait(false);
                    return await UncertainAfterCleanupAsync(
                        FixtureB1WrapperDisposition.InvalidTerminalRecord,
                        process,
                        observation,
                        observationCancellation,
                        cleanupDeadline).ConfigureAwait(false);
                }

                var remaining = overallDeadline - Stopwatch.GetElapsedTime(startedAt);
                if (remaining <= TimeSpan.Zero || deadline.IsCompleted)
                {
                    await deadlineCancellation.CancelAsync().ConfigureAwait(false);
                    return await UncertainAfterCleanupAsync(
                        FixtureB1WrapperDisposition.TimedOut,
                        process,
                        observation,
                        observationCancellation,
                        cleanupDeadline).ConfigureAwait(false);
                }

                var close = await CloseObservedAsync(
                    process, remaining, cancellationToken).ConfigureAwait(false);
                var exceededDeadline = deadline.IsCompleted ||
                    Stopwatch.GetElapsedTime(startedAt) >= overallDeadline;
                await deadlineCancellation.CancelAsync().ConfigureAwait(false);
                if (exceededDeadline || cancellationToken.IsCancellationRequested)
                {
                    return Uncertain(
                        cancellationToken.IsCancellationRequested
                            ? FixtureB1WrapperDisposition.Cancelled
                            : FixtureB1WrapperDisposition.TimedOut,
                        observed.StandardErrorBytes,
                        outputDrainCompleted: true,
                        observationOperationsStopped: true,
                        closeReport: close.Report,
                        adapterRetainsOwnership: close.Report?.IsComplete != true);
                }
                if (close.Report is null || !close.Report.IsComplete)
                {
                    return Uncertain(
                        FixtureB1WrapperDisposition.CloseIncomplete,
                        observed.StandardErrorBytes,
                        outputDrainCompleted: true,
                        observationOperationsStopped: true,
                        closeReport: close.Report,
                        adapterRetainsOwnership: true);
                }

                return new(
                    FixtureB1WrapperDisposition.Parsed,
                    observed.ExitCode,
                    parsedStatus,
                    observed.StandardErrorBytes,
                    TerminateAndReapAttempted: false,
                    TerminateAndReapCompleted: false,
                    OutputDrainCompleted: true,
                    ObservationOperationsStopped: true,
                    CloseReport: close.Report,
                    AdapterRetainsOwnership: false);
            }
            catch (ObservationException exception)
            {
                await deadlineCancellation.CancelAsync().ConfigureAwait(false);
                var disposition = exception.LimitExceeded
                    ? FixtureB1WrapperDisposition.OutputLimitExceeded
                    : exception.Origin == ObservationSource.Process
                        ? FixtureB1WrapperDisposition.ProcessFault
                        : FixtureB1WrapperDisposition.StreamFault;
                return await UncertainAfterCleanupAsync(
                    disposition,
                    process,
                    observation,
                    observationCancellation,
                    cleanupDeadline).ConfigureAwait(false);
            }
        }

        await deadlineCancellation.CancelAsync().ConfigureAwait(false);
        var watchdogDisposition = completed == callerCancelled.Task
            ? FixtureB1WrapperDisposition.Cancelled
            : FixtureB1WrapperDisposition.TimedOut;
        return await UncertainAfterCleanupAsync(
            watchdogDisposition,
            process,
            observation,
            observationCancellation,
            cleanupDeadline).ConfigureAwait(false);
    }

    private static void ValidateDeadline(TimeSpan value, string parameterName)
    {
        if (value <= TimeSpan.Zero || value == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(parameterName, "A finite positive deadline is required.");
    }

    private static async Task<FixtureB1WrapperResult> UncertainAfterCleanupAsync(
        FixtureB1WrapperDisposition disposition,
        IFixtureB1WrapperProcess process,
        Observation observation,
        CancellationTokenSource observationCancellation,
        TimeSpan cleanupDeadline)
    {
        using var cleanupCancellation = new CancellationTokenSource(cleanupDeadline);
        using var abortRegistration = cleanupCancellation.Token.Register(
            static state => ((CancellationTokenSource)state!).Cancel(), observationCancellation);
        var terminateAndReapCompleted = false;
        FixtureB1WrapperCloseReport? closeReport = null;
        try
        {
            await process.TerminateAndReapAsync(cleanupCancellation.Token).AsTask()
                .WaitAsync(cleanupCancellation.Token).ConfigureAwait(false);
            terminateAndReapCompleted = true;
            await observation.DrainAsync().WaitAsync(cleanupCancellation.Token).ConfigureAwait(false);
            closeReport = await process.CloseAsync(cleanupCancellation.Token).AsTask()
                .WaitAsync(cleanupCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            await observationCancellation.CancelAsync().ConfigureAwait(false);
            if (!observation.OperationsStopped)
            {
                try
                {
                    await observation.DrainAsync().WaitAsync(cleanupCancellation.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // The adapter retains ownership of any unconfirmed operation.
                }
            }
        }

        var operationsStopped = observation.OperationsStopped;
        var closeCompleted = closeReport?.IsComplete == true;
        return Uncertain(
            disposition,
            observation.StandardErrorBytesObserved,
            terminateAndReapAttempted: true,
            terminateAndReapCompleted: terminateAndReapCompleted,
            outputDrainCompleted: observation.OutputDrainCompleted,
            observationOperationsStopped: operationsStopped,
            closeReport: closeReport,
            adapterRetainsOwnership:
                !terminateAndReapCompleted || !operationsStopped || !closeCompleted);
    }

    private static async Task<CloseAttempt> CloseObservedAsync(
        IFixtureB1WrapperProcess process,
        TimeSpan remainingDeadline,
        CancellationToken callerCancellation)
    {
        using var closeCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(callerCancellation);
        closeCancellation.CancelAfter(remainingDeadline);
        try
        {
            var report = await process.CloseAsync(closeCancellation.Token).AsTask()
                .WaitAsync(closeCancellation.Token).ConfigureAwait(false);
            return new(report);
        }
        catch (Exception)
        {
            return new(null);
        }
    }

    private static FixtureB1WrapperResult Uncertain(
        FixtureB1WrapperDisposition disposition,
        int standardErrorBytes,
        bool terminateAndReapAttempted = false,
        bool terminateAndReapCompleted = false,
        bool outputDrainCompleted = false,
        bool observationOperationsStopped = false,
        FixtureB1WrapperCloseReport? closeReport = null,
        bool adapterRetainsOwnership = true) =>
        new(
            Disposition: disposition,
            ExitCode: QualificationExitCode.CleanupIncomplete,
            ParsedStatus: null,
            StandardErrorBytesObserved: standardErrorBytes,
            TerminateAndReapAttempted: terminateAndReapAttempted,
            TerminateAndReapCompleted: terminateAndReapCompleted,
            OutputDrainCompleted: outputDrainCompleted,
            ObservationOperationsStopped: observationOperationsStopped,
            CloseReport: closeReport,
            AdapterRetainsOwnership: adapterRetainsOwnership);

    private enum ObservationSource
    {
        Process,
        StandardOutput,
        StandardError,
    }

    private sealed class ObservationException(
        ObservationSource source,
        bool limitExceeded,
        Exception innerException)
        : Exception("B1 observation failed.", innerException)
    {
        internal ObservationSource Origin { get; } = source;
        internal bool LimitExceeded { get; } = limitExceeded;
    }

    private sealed class OutputLimitException : IOException
    {
    }

    private sealed class Observation
    {
        private readonly Task<QualificationExitCode> _processExit;
        private readonly Task<byte[]> _standardOutput;
        private readonly Task<int> _standardError;
        private int _standardErrorBytesObserved;
        private int _standardOutputEof;
        private int _standardErrorEof;

        internal Observation(IFixtureB1WrapperProcess process, CancellationToken cancellationToken)
        {
            _processExit = WaitForExitAsync(process, cancellationToken);
            _standardOutput = ReadStandardOutputAsync(process.StandardOutput, cancellationToken);
            _standardError = ReadStandardErrorAsync(process.StandardError, cancellationToken);
        }

        internal int StandardErrorBytesObserved =>
            Volatile.Read(ref _standardErrorBytesObserved);

        internal bool OutputDrainCompleted =>
            Volatile.Read(ref _standardOutputEof) != 0 &&
            Volatile.Read(ref _standardErrorEof) != 0;

        internal bool OperationsStopped =>
            _processExit.IsCompleted && _standardOutput.IsCompleted && _standardError.IsCompleted;

        internal async Task<ObservedTerminal> ObserveAsync()
        {
            var pending = new List<Task> { _processExit, _standardOutput, _standardError };
            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending).ConfigureAwait(false);
                try
                {
                    await completed.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    var source = completed == _processExit
                        ? ObservationSource.Process
                        : completed == _standardOutput
                            ? ObservationSource.StandardOutput
                            : ObservationSource.StandardError;
                    throw new ObservationException(
                        source, exception is OutputLimitException, exception);
                }
                pending.Remove(completed);
            }

            return new(
                _processExit.Result,
                _standardOutput.Result,
                _standardError.Result);
        }

        internal Task DrainAsync() => Task.WhenAll(
            SuppressFailureAsync(_processExit),
            SuppressFailureAsync(_standardOutput),
            SuppressFailureAsync(_standardError));

        private static async Task<QualificationExitCode> WaitForExitAsync(
            IFixtureB1WrapperProcess process,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            return await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task<byte[]> ReadStandardOutputAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            return await ReadBoundedAsync(
                stream,
                MaximumStandardOutputBytes,
                cancellationToken,
                null,
                () => Volatile.Write(ref _standardOutputEof, 1)).ConfigureAwait(false);
        }

        private async Task<int> ReadStandardErrorAsync(
            Stream stream,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            var bytes = await ReadBoundedAsync(
                stream,
                MaximumStandardErrorBytes,
                cancellationToken,
                count => Volatile.Write(ref _standardErrorBytesObserved, count),
                () => Volatile.Write(ref _standardErrorEof, 1))
                .ConfigureAwait(false);
            return bytes.Length;
        }

        private static async Task<byte[]> ReadBoundedAsync(
            Stream stream,
            int maximumBytes,
            CancellationToken cancellationToken,
            Action<int>? reportProgress,
            Action reportEof)
        {
            var buffer = new byte[maximumBytes + 1];
            var total = 0;
            while (true)
            {
                var count = await stream.ReadAsync(
                    buffer.AsMemory(total, Math.Min(4096, buffer.Length - total)),
                    cancellationToken).ConfigureAwait(false);
                if (count == 0)
                {
                    reportEof();
                    return buffer.AsSpan(0, total).ToArray();
                }
                if (count < 0 || count > buffer.Length - total)
                    throw new IOException("The stream returned an invalid byte count.");

                total += count;
                reportProgress?.Invoke(Math.Min(total, maximumBytes));
                if (total > maximumBytes)
                    throw new OutputLimitException();
            }
        }

        private static async Task SuppressFailureAsync(Task task)
        {
            try
            {
                await task.ConfigureAwait(false);
            }
            catch
            {
                // A primary observation failure is already represented by code-5 uncertainty.
            }
        }
    }

    private sealed record ObservedTerminal(
        QualificationExitCode ExitCode,
        byte[] StandardOutput,
        int StandardErrorBytes);

    private sealed record CloseAttempt(FixtureB1WrapperCloseReport? Report);
}
