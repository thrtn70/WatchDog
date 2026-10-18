using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;

namespace WatchDog.App.Logging;

/// <summary>
/// Writes bounded, exclusively created safe-log segments. Legacy daily logs remain untouched.
/// Files use %APPDATA%\WatchDog\logs\watchdog-safe-v2-YYYY-MM-DD-NNNN.log (UTC).
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    internal const int DefaultMaxFileBytes = 1024 * 1024;
    internal const int DefaultMaxSlotsPerDay = 16;
    internal const int MaxRecordBytes = 2048;

    private readonly string _logDirectory;
    private readonly LogLevel _minLevel;
    private readonly Action<string>? _testSink;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly int _maxFileBytes;
    private readonly int _maxSlotsPerDay;
    private readonly Func<string, SafeLogCreateNewResult> _createNew;
    private readonly object _lock = new();
    private Stream? _writer;
    private IncrementalHash? _hash;
    private string _currentDate = string.Empty;
    private int _currentSlot;
    private int _nextSlot;
    private int _bytesWritten;
    private int _recordCount;
    private bool _poisoned;
    private bool _disposed;
    private long _droppedLogCount;

    internal long DroppedLogCount => Interlocked.Read(ref _droppedLogCount);

    public FileLoggerProvider(LogLevel minLevel = LogLevel.Information)
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WatchDog", "logs"), static () => DateTimeOffset.UtcNow,
            DefaultMaxFileBytes, DefaultMaxSlotsPerDay, minLevel)
    {
    }

    internal FileLoggerProvider(string logDirectory, LogLevel minLevel = LogLevel.Information)
        : this(logDirectory, static () => DateTimeOffset.UtcNow,
            DefaultMaxFileBytes, DefaultMaxSlotsPerDay, minLevel)
    {
    }

    internal FileLoggerProvider(
        string logDirectory,
        Func<DateTimeOffset> utcNow,
        int maxFileBytes,
        int maxSlotsPerDay,
        LogLevel minLevel = LogLevel.Information,
        Func<string, SafeLogCreateNewResult>? createNew = null)
    {
        if (maxFileBytes < 128 || maxFileBytes > DefaultMaxFileBytes)
            throw new ArgumentOutOfRangeException(nameof(maxFileBytes));
        if (maxSlotsPerDay < 1 || maxSlotsPerDay > DefaultMaxSlotsPerDay)
            throw new ArgumentOutOfRangeException(nameof(maxSlotsPerDay));
        _minLevel = minLevel;
        _logDirectory = logDirectory ?? throw new ArgumentNullException(nameof(logDirectory));
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
        _maxFileBytes = maxFileBytes;
        _maxSlotsPerDay = maxSlotsPerDay;
        _createNew = createNew ?? CreateNewFile;
    }

    internal FileLoggerProvider(Action<string> testSink, LogLevel minLevel = LogLevel.Information)
    {
        _testSink = testSink ?? throw new ArgumentNullException(nameof(testSink));
        _minLevel = minLevel;
        _logDirectory = string.Empty;
        _utcNow = static () => DateTimeOffset.UtcNow;
        _maxFileBytes = DefaultMaxFileBytes;
        _maxSlotsPerDay = DefaultMaxSlotsPerDay;
        _createNew = static _ => throw new InvalidOperationException("Synthetic sink has no file stream.");
    }

    private static SafeLogCreateNewResult CreateNewFile(string path)
    {
        try
        {
            return new SafeLogCreated(new FileStream(path, FileMode.CreateNew,
                FileAccess.Write, FileShare.Read));
        }
        catch (IOException ex) when (unchecked((uint)ex.HResult) is 0x80070050 or 0x800700B7)
        {
            // Only the atomic CreateNew operation may classify an OS name collision.
            return new SafeLogConfirmedNameCollision();
        }
    }

    public ILogger CreateLogger(string categoryName)
        => new FileLogger(this, categoryName, _minLevel);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (!_poisoned) SealCurrent();
            }
            catch (Exception)
            {
                _poisoned = true;
                Interlocked.Increment(ref _droppedLogCount);
            }
            finally
            {
                DisposeBestEffort(_writer);
                _writer = null;
                try { _hash?.Dispose(); } catch { /* Disposal is terminal. */ }
                _hash = null;
            }
        }
    }

    private void RegisterWriteFailure()
    {
        lock (_lock)
            PoisonLocked();
    }

    private void PoisonLocked()
    {
        _poisoned = true;
        Interlocked.Increment(ref _droppedLogCount);
        DisposeBestEffort(_writer);
        _writer = null;
        try { _hash?.Dispose(); } catch { /* Preserve the original write failure. */ }
        _hash = null;
    }

    private static void DisposeBestEffort(Stream? stream, int attempts = 2)
    {
        if (stream is null) return;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try { stream.Dispose(); return; }
            catch { /* A second attempt may release a handle after a pre-close fault. */ }
        }
    }

    private void SealCurrent()
    {
        if (_writer is null) return;
        if (_hash is null || _recordCount == 0)
            throw new InvalidOperationException("Safe-log segment has no complete records.");
        var footer = SafeLogSegmentFooter.Format(_currentDate, _currentSlot,
            _recordCount, _bytesWritten, _hash.GetHashAndReset());
        _writer.Write(footer);
        _writer.Flush();
        var completed = _writer;
        _writer = null;
        try { completed.Dispose(); }
        catch
        {
            // A custom stream can throw before releasing its handle. Retry cleanup once,
            // but never retry the footer or admit a successor segment.
            DisposeBestEffort(completed, attempts: 1);
            throw;
        }
        _hash.Dispose();
        _hash = null;
    }

    private void WriteLog(string message, DateTimeOffset now)
    {
        if (_testSink is not null)
        {
            if (_disposed) { Interlocked.Increment(ref _droppedLogCount); return; }
            _testSink(message);
            return;
        }

        lock (_lock)
        {
            try { WriteLogLocked(message, now); }
            catch (Exception) { PoisonLocked(); }
        }
    }

    private void WriteLogLocked(string message, DateTimeOffset now)
    {
        if (_disposed || _poisoned)
        {
            Interlocked.Increment(ref _droppedLogCount);
            return;
        }

        var encoded = Encoding.UTF8.GetBytes(message + "\n");
        var encodedBytes = encoded.Length;
        if (encodedBytes > MaxRecordBytes ||
            encodedBytes + SafeLogSegmentFooter.FooterBytes > _maxFileBytes)
        {
            Interlocked.Increment(ref _droppedLogCount);
            return;
        }

        var today = now.UtcDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (today != _currentDate)
        {
            SealCurrent();
            _currentDate = today;
            _nextSlot = 0;
            _bytesWritten = 0;
        }

        if (_writer is null || _bytesWritten + encodedBytes + SafeLogSegmentFooter.FooterBytes > _maxFileBytes)
        {
            SealCurrent();
            if (_nextSlot >= _maxSlotsPerDay)
            {
                Interlocked.Increment(ref _droppedLogCount);
                return;
            }
            Directory.CreateDirectory(_logDirectory);
            while (_nextSlot < _maxSlotsPerDay)
            {
                var slot = _nextSlot++;
                var logPath = Path.Combine(_logDirectory,
                    $"watchdog-safe-v2-{today}-{slot:D4}.log");
                var result = _createNew(logPath);
                if (result is SafeLogConfirmedNameCollision)
                    continue;
                if (result is not SafeLogCreated { Stream: not null } created)
                    throw new InvalidOperationException("Invalid safe-log create result.");
                _writer = created.Stream;
                _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                _currentSlot = slot;
                _bytesWritten = 0;
                _recordCount = 0;
                break;
            }
        }

        if (_writer is null)
        {
            Interlocked.Increment(ref _droppedLogCount);
            return;
        }
        _writer.Write(encoded);
        _writer.Flush();
        _hash!.AppendData(encoded);
        _bytesWritten += encodedBytes;
        _recordCount++;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= minLevel && logLevel <= LogLevel.Critical;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            // Neither the formatter nor Exception.ToString is safe for a shareable log.
            // Unknown producers get fixed metadata only. Capture fields are read by type below.
            try
            {
                var now = provider._utcNow().ToUniversalTime();
                var message = SafeLogProjection.Project(logLevel, category, state, exception is not null, now);
                if (!SafeLogRecordValidator.TryParseLine(message, out _))
                    message = SafeLogProjection.Project(logLevel, category, string.Empty,
                        exception is not null, now);
                provider.WriteLog(message, now);
            }
            catch (Exception)
            {
                // A local log failure must not turn a capture outcome into another failure.
                provider.RegisterWriteFailure();
            }
        }
    }
}

internal abstract record SafeLogCreateNewResult;

internal sealed record SafeLogCreated(Stream Stream) : SafeLogCreateNewResult;

internal sealed record SafeLogConfirmedNameCollision : SafeLogCreateNewResult;

internal static class SafeLogProjection
{
    private const string CaptureManagerCategory = "WatchDog.App.Services.CaptureSourceManager";

    internal static string Project<TState>(LogLevel level, string category, TState state, bool hasException, DateTimeOffset utcNow)
    {
        var captureManager = StringComparer.Ordinal.Equals(category, CaptureManagerCategory);
        var result = new StringBuilder(200);
        result.Append("schema=watchdog.safe-log.v2 timeUtc=");
        result.Append(utcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
        result.Append(" level=");
        result.Append(level switch
        {
            LogLevel.Trace => "trace",
            LogLevel.Debug => "debug",
            LogLevel.Information => "information",
            LogLevel.Warning => "warning",
            LogLevel.Error => "error",
            LogLevel.Critical => "critical",
            _ => "unknown",
        });
        result.Append(" source=");
        result.Append(SafeSource(category));
        result.Append(hasException ? " exception=true" : " exception=false");

        var eventCode = SafeEventCode(category, state);
        if (eventCode is not null)
        {
            result.Append(" event=");
            result.Append(eventCode);
        }

        if (!captureManager || state is not IReadOnlyList<KeyValuePair<string, object?>> fields)
            return result.ToString();

        // Parse known types only. A hostile or malformed state cannot make the file sink
        // invoke user-controlled formatting or object ToString methods.
        try
        {
            var safe = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in fields)
            {
                var value = SafeValue(field.Key, field.Value);
                if (value is not null && !safe.TryAdd(field.Key, value))
                    return result.ToString();
            }

            if (!safe.ContainsKey("Operation") || !safe.ContainsKey("Disposition"))
                return result.ToString();

            if (eventCode is null)
                result.Append(" event=capture-transition");
            foreach (var key in new[]
            {
                "Operation", "Disposition", "Kind", "OperationId", "BeforeSessionId",
                "AfterSessionId", "BeforeGeneration", "AfterGeneration", "BeforeStatus",
                "AfterStatus", "BeforeHealth", "AfterHealth", "AfterLastFailureKind",
                "AfterContextPresent",
            })
            {
                if (safe.TryGetValue(key, out var value))
                {
                    result.Append(' ');
                    result.Append(char.ToLowerInvariant(key[0]));
                    result.Append(key.AsSpan(1));
                    result.Append('=');
                    result.Append(value);
                }
            }
        }
        catch
        {
            // Metadata-only is the safe fallback for unusual ILogger state objects.
        }

        return result.ToString();
    }

    private static string SafeSource(string category) => category switch
    {
        CaptureManagerCategory => "capture-manager",
        "WatchDog.Core.Capture.ObsCaptureEngine" => "obs-capture",
        "WatchDog.App.Services.ObsCaptureLifecycleBackend" => "capture-backend",
        "WatchDog.Core.Buffer.ObsReplayBuffer" => "replay-buffer",
        "WatchDog.Core.Recording.ObsSessionRecorder" => "session-recorder",
        "WatchDog.App.Services.SessionRecordingHostedService" => "session-host",
        "WatchDog.Core.Storage.StorageMaintenanceService" => "storage-maintenance",
        "WatchDog.App.Services.HotkeyListenerHostedService" => "hotkey",
        _ => "other",
    };

    private static string? SafeEventCode<TState>(string category, TState state)
    {
        if (state is not IReadOnlyList<KeyValuePair<string, object?>> fields)
            return null;
        try
        {
            foreach (var field in fields)
            {
                if (field.Key != "{OriginalFormat}" || field.Value is not string format)
                    continue;
                return (category, format) switch
                {
                    ("WatchDog.Core.Capture.ObsCaptureEngine", "Failed to start desktop capture" or
                        "Failed to start physical region capture" or
                        "Failed to start window capture for {Window}") => "capture-start-failed",
                    ("WatchDog.Core.Capture.ObsCaptureEngine", "Replay buffer error: {Error}") => "replay-buffer-error",
                    ("WatchDog.Core.Capture.ObsCaptureEngine", "Capture stopped") => "capture-stopped",
                    ("WatchDog.Core.Buffer.ObsReplayBuffer", "Failed to start replay buffer: {Error}") => "replay-start-failed",
                    ("WatchDog.App.Services.ObsCaptureLifecycleBackend", "Failed to start physical capture for request {RequestId}") => "backend-start-failed",
                    ("WatchDog.App.Services.ObsCaptureLifecycleBackend", "Failed to stop {Stage} for capture session {SessionId}") => "backend-stop-failed",
                    ("WatchDog.App.Services.ObsCaptureLifecycleBackend", "Failed to save replay for session {SessionId}") => "backend-save-failed",
                    ("WatchDog.App.Services.ObsCaptureLifecycleBackend", "Session segment metadata remains pending for retry in {SessionId}") => "segment-metadata-pending",
                    ("WatchDog.App.Services.ObsCaptureLifecycleBackend", "Session recorder error: {Error}") => "session-recorder-error",
                    ("WatchDog.Core.Recording.ObsSessionRecorder", "Session recording started: {Output}") => "session-started",
                    ("WatchDog.App.Services.SessionRecordingHostedService", "Session recorder error: {Error}") => "session-recorder-error",
                    ("WatchDog.Core.Storage.StorageMaintenanceService", "Storage cleanup failed; capture admission will be blocked") => "storage-cleanup-failed",
                    ("WatchDog.App.Services.HotkeyListenerHostedService", "Clip save failed: {Code} — {Message}") => "clip-save-failed",
                    (CaptureManagerCategory, "Failed to handle backend source loss: {Reason}") => "source-loss-handler-failed",
                    (CaptureManagerCategory, "Failed to record capture health degradation: {Code}") => "health-handler-failed",
                    _ => null,
                };
            }
        }
        catch
        {
            // Unknown logging state remains metadata-only.
        }
        return null;
    }

    private static string? SafeValue(string key, object? value) => (key, value) switch
    {
        ("Operation", CaptureOperationKind operation) when Enum.IsDefined(operation) => operation.ToString(),
        ("Disposition", CaptureCommandDisposition disposition) when Enum.IsDefined(disposition) => disposition.ToString(),
        ("Kind", CaptureFailureKind kind) when Enum.IsDefined(kind) => kind.ToString(),
        ("OperationId" or "BeforeSessionId" or "AfterSessionId", Guid id) when id != Guid.Empty => id.ToString("D"),
        ("BeforeGeneration" or "AfterGeneration", long generation) when generation >= 0 => generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ("BeforeStatus" or "AfterStatus", CaptureLifecycleStatus status) when Enum.IsDefined(status) => status.ToString(),
        ("BeforeHealth" or "AfterHealth", CaptureHealth health) when Enum.IsDefined(health) => health.ToString(),
        ("AfterLastFailureKind", CaptureFailureKind kind) when Enum.IsDefined(kind) => kind.ToString(),
        ("AfterContextPresent", bool present) => present ? "true" : "false",
        _ => null,
    };
}
