namespace WatchDog.Core.Buffer;

/// <summary>
/// Correlates a serialized save request with its completion signal. Since OBS signals
/// have no request ID, a timed-out or cancelled request requires a new buffer before
/// another request can be accepted; a late signal must never complete a newer save.
/// </summary>
internal sealed class ReplaySaveCoordinator : IDisposable
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly object _sync = new();
    private readonly Func<string, bool> _validateOutput;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<string>? _pending;
    private string? _lastCompletedPath;
    private bool _requiresRestart;
    private bool _disposed;

    public ReplaySaveCoordinator(Func<string, bool> validateOutput, TimeSpan? timeout = null)
    {
        _validateOutput = validateOutput;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<string> SaveAsync(Action requestSave, CancellationToken ct)
    {
        await _serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TaskCompletionSource<string> completion;
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_requiresRestart)
                    throw new InvalidOperationException("The previous replay save did not complete. Restart capture before saving again.");
                ct.ThrowIfCancellationRequested();
                completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _pending = completion;
            }

            try
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
                requestSave();
                return await ValidateAsync(completion.Task).WaitAsync(_timeout, cancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(ReplaySaveCoordinator));
            }
            catch
            {
                lock (_sync) _requiresRestart = true;
                throw;
            }
            finally
            {
                lock (_sync) _pending = null;
            }
        }
        finally
        {
            _serial.Release();
        }
    }

    private async Task<string> ValidateAsync(Task<string> completion)
    {
        var path = await completion.ConfigureAwait(false);
        // A disconnected destination may block filesystem calls. Keep those calls off
        // the native signal thread and allow the caller's timeout/disposal to unwind.
        if (!await Task.Run(() => _validateOutput(path)).ConfigureAwait(false))
            throw new IOException("Replay completion did not produce a nonempty file in the recording folder.");
        return path;
    }

    public void Complete(string? path)
    {
        lock (_sync)
        {
            if (_disposed || _requiresRestart || _pending is null ||
                string.Equals(path, _lastCompletedPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (string.IsNullOrWhiteSpace(path))
            {
                _pending.TrySetException(new IOException("Replay completion did not provide an output path."));
                return;
            }

            if (_pending.TrySetResult(path))
                _lastCompletedPath = path;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
            _pending?.TrySetException(new ObjectDisposedException(nameof(ReplaySaveCoordinator)));
        }
        _lifetime.Cancel();
        // Queued callers still release the semaphore while unwinding. It owns no
        // native wait handle, so let collection reclaim it once those callers finish.
    }
}
