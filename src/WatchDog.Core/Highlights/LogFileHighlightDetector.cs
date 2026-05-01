using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Highlights;

/// <summary>
/// Abstract base for highlight detectors that work by tailing game log files.
/// Subclasses provide the log directory, file pattern, and line parsing logic.
/// </summary>
public abstract class LogFileHighlightDetector : IHighlightDetector
{
    private readonly ILogger _logger;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _tailTask;
    private string? _currentLogFile;
    private long _lastReadPosition;
    private bool _watching;
    private readonly SemaphoreSlim _readLock = new(1, 1);

    protected abstract string LogDirectoryPath { get; }
    protected abstract string LogFilePattern { get; }
    protected abstract void ProcessLogLine(string line);

    public abstract string GameExecutableName { get; }
    public abstract IReadOnlyList<string> SupportedExecutableNames { get; }
    public bool IsRunning => _watching;
    public event Action<HighlightDetectedEventArgs>? HighlightDetected;

    protected LogFileHighlightDetector(ILogger logger)
    {
        _logger = logger;
    }

    protected void RaiseHighlight(HighlightType type, string? description = null)
    {
        HighlightDetected?.Invoke(new HighlightDetectedEventArgs(type, description));
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_watching) return Task.CompletedTask;

        if (!Directory.Exists(LogDirectoryPath))
        {
            _logger.LogWarning("Log directory not found: {Path}. Detector will not start.", LogDirectoryPath);
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Find the most recent log file and seek to end (only process new lines)
        _currentLogFile = FindMostRecentLogFile();
        if (_currentLogFile is not null)
        {
            _lastReadPosition = new FileInfo(_currentLogFile).Length;
            _logger.LogInformation("Tailing log file: {File} (starting at position {Pos})",
                _currentLogFile, _lastReadPosition);
        }

        // Watch for file changes and new files
        _watcher = new FileSystemWatcher(LogDirectoryPath, LogFilePattern)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.FileName,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnLogFileChanged;
        _watcher.Created += OnNewLogFileCreated;

        // Start background poll task as fallback (FSW can miss events)
        _tailTask = PollTailLoopAsync(_cts.Token);
        _watching = true;

        _logger.LogInformation("{Detector} log watcher started on {Dir}",
            GetType().Name, LogDirectoryPath);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (!_watching) return;

        _watching = false;

        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnLogFileChanged;
            _watcher.Created -= OnNewLogFileCreated;
            _watcher.Dispose();
            _watcher = null;
        }

        _cts?.Cancel();

        if (_tailTask is not null)
        {
            try { await _tailTask; }
            catch (OperationCanceledException) { }
        }

        _cts?.Dispose();
        _cts = null;
        _currentLogFile = null;
        _lastReadPosition = 0;

        _logger.LogInformation("{Detector} log watcher stopped", GetType().Name);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _readLock.Dispose();
    }

    private string? FindMostRecentLogFile()
    {
        try
        {
            return new DirectoryInfo(LogDirectoryPath)
                .GetFiles(LogFilePattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error scanning log directory");
            return null;
        }
    }

    private void OnLogFileChanged(object sender, FileSystemEventArgs e)
    {
        // FSW triggers — the poll loop will pick up the new data
    }

    private void OnNewLogFileCreated(object sender, FileSystemEventArgs e)
    {
        // Validate new file is still within the watched directory
        var resolvedBase = Path.GetFullPath(LogDirectoryPath).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var resolvedNew = Path.GetFullPath(e.FullPath);
        if (!resolvedNew.StartsWith(resolvedBase, StringComparison.OrdinalIgnoreCase)) return;

        // Synchronize with ReadNewLinesAsync to prevent torn reads
        if (!_readLock.Wait(0)) return;
        try
        {
            _currentLogFile = resolvedNew;
            _lastReadPosition = 0;
            _logger.LogInformation("Switched to new log file: {File}", e.FullPath);
        }
        finally
        {
            _readLock.Release();
        }
    }

    private async Task PollTailLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, ct);
                await ReadNewLinesAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in log tail loop");
            }
        }
    }

    private async Task ReadNewLinesAsync()
    {
        if (_currentLogFile is null) return;
        if (!File.Exists(_currentLogFile))
        {
            // File may have been rotated; try to find the new one
            var newFile = FindMostRecentLogFile();
            if (newFile is null || newFile == _currentLogFile) return;
            _currentLogFile = newFile;
            _lastReadPosition = 0;
        }

        if (!await _readLock.WaitAsync(0)) return; // Skip if already reading
        try
        {
            using var fs = new FileStream(_currentLogFile, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (fs.Length <= _lastReadPosition) return; // No new data

            fs.Seek(_lastReadPosition, SeekOrigin.Begin);

            // Read in capped chunks to avoid oversized allocations on large log files
            const int MaxReadPerPoll = 1 * 1024 * 1024; // 1 MB cap
            var bytesToRead = (int)Math.Min(fs.Length - _lastReadPosition, MaxReadPerPoll);
            var buffer = new byte[bytesToRead];
            var bytesRead = await fs.ReadAsync(buffer.AsMemory(0, bytesToRead));
            _lastReadPosition += bytesRead;

            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.TrimEnd('\r');
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    try
                    {
                        ProcessLogLine(trimmed);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Error processing log line");
                    }
                }
            }
        }
        catch (IOException ex)
        {
            _logger.LogDebug(ex, "Error reading log file");
        }
        finally
        {
            _readLock.Release();
        }
    }
}
