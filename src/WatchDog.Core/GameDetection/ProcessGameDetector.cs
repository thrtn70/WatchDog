using System.Diagnostics;
using System.Management;
using Microsoft.Extensions.Logging;
using WatchDog.Core.Capture;

namespace WatchDog.Core.GameDetection;

/// <summary>
/// Detects running games using WMI process creation events with a polling fallback.
/// </summary>
public sealed class ProcessGameDetector : IGameDetector
{
    private readonly GameDatabase _gameDatabase;
    private readonly ILogger<ProcessGameDetector> _logger;

    private ManagementEventWatcher? _processWatcher;
    private Timer? _pollTimer;
    private Process? _trackedProcess;
    private readonly object _trackingSync = new();
    private GameInfo? _currentGame;
    private bool _disposed;
    private volatile bool _running;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public GameInfo? CurrentGame
    {
        get
        {
            lock (_trackingSync)
                return _currentGame;
        }
    }

    public event Action<GameInfo>? GameStarted;
    public event Action<GameInfo>? GameStopped;

    public ProcessGameDetector(GameDatabase gameDatabase, ILogger<ProcessGameDetector> logger)
    {
        _gameDatabase = gameDatabase;
        _logger = logger;
    }

    public void Start()
    {
        lock (_trackingSync)
        {
            if (_running) return;
            _running = true;
        }

        StartWmiWatcher();
        StartPolling();

        // Check for already-running games on startup
        ScanRunningProcesses();

        _logger.LogInformation("Game detector started (WMI + polling every {Interval}s)", PollInterval.TotalSeconds);
    }

    public void Stop()
    {
        lock (_trackingSync)
        {
            if (!_running) return;
            _running = false;
        }

        try
        {
            StopWmiWatcher();
        }
        finally
        {
            try { StopPolling(); }
            finally { StopTracking(); }
        }

        _logger.LogInformation("Game detector stopped");
    }

    private void StartWmiWatcher()
    {
        try
        {
            var query = new WqlEventQuery(
                "__InstanceCreationEvent",
                TimeSpan.FromSeconds(1),
                "TargetInstance ISA 'Win32_Process'");

            _processWatcher = new ManagementEventWatcher(query);
            _processWatcher.EventArrived += OnProcessCreated;
            _processWatcher.Start();

            _logger.LogDebug("WMI process watcher started");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start WMI watcher, relying on polling only");
        }
    }

    private void StopWmiWatcher()
    {
        if (_processWatcher is not null)
        {
            _processWatcher.EventArrived -= OnProcessCreated;
            _processWatcher.Stop();
            _processWatcher.Dispose();
            _processWatcher = null;
        }
    }

    private void StartPolling()
    {
        _pollTimer = new Timer(OnPollTick, null, PollInterval, PollInterval);
    }

    private void StopPolling()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private void OnProcessCreated(object sender, EventArrivedEventArgs e)
    {
        if (CurrentGame is not null)
            return; // Already tracking a game

        try
        {
            var targetInstance = (ManagementBaseObject)e.NewEvent["TargetInstance"];
            var processName = targetInstance["Name"]?.ToString();
            var processId = Convert.ToInt32(targetInstance["ProcessId"]);

            if (processName is null)
                return;

            var game = _gameDatabase.TryMatch(processName, processId);
            if (game is not null)
            {
                var processStartedAt = GetProcessStartedAt(processId);
                if (processStartedAt is null)
                {
                    _logger.LogDebug(
                        "Could not capture process identity for {Game} PID {Pid}; deferring detection to polling",
                        game.DisplayName,
                        game.ProcessId);
                    return;
                }

                game = game with { ProcessStartedAt = processStartedAt };
                _logger.LogInformation("WMI detected game: {Game} (PID {Pid})", game.DisplayName, game.ProcessId);
                TrackGame(game);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error processing WMI event");
        }
    }

    private void OnPollTick(object? state)
    {
        if (!_running) return;

        var currentGame = CurrentGame;
        if (currentGame is not null)
        {
            if (!IsCurrentProcess(currentGame) && ReferenceEquals(CurrentGame, currentGame))
            {
                _logger.LogInformation(
                    "Polling detected game exit: {Game} (PID {Pid})",
                    currentGame.DisplayName,
                    currentGame.ProcessId);
                if (StopTracking(currentGame) is not null)
                    GameStopped?.Invoke(currentGame);
            }

            return;
        }

        ScanRunningProcesses();
    }

    private void ScanRunningProcesses()
    {
        Process[]? processes = null;
        try
        {
            processes = Process.GetProcesses();
            foreach (var process in processes)
            {
                try
                {
                    var exeName = process.ProcessName + ".exe";
                    var game = _gameDatabase.TryMatch(exeName, process.Id, GetWindowTitle(process));

                    if (game is not null)
                    {
                        var processStartedAt = GetProcessStartedAt(process);
                        if (processStartedAt is null)
                            continue;

                        game = game with { ProcessStartedAt = processStartedAt };
                        _logger.LogInformation("Poll detected game: {Game} (PID {Pid})", game.DisplayName, game.ProcessId);
                        TrackGame(game);
                        return;
                    }
                }
                catch
                {
                    // Access denied for system processes — skip
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error scanning processes");
        }
        finally
        {
            if (processes is not null)
                foreach (var p in processes)
                    p.Dispose();
        }
    }

    internal void TrackGame(GameInfo game)
    {
        lock (_trackingSync)
        {
            if (!_running || _currentGame is not null)
                return;
        }

        Process? process = null;
        try
        {
            process = Process.GetProcessById(game.ProcessId);
            var currentTarget = new GameCaptureTarget(
                game.ExecutableName,
                game.ProcessId,
                game.ExecutableName,
                game.ProcessStartedAt);
            if (!GameProcessIdentity.Matches(
                    process.Id,
                    GetProcessStartedAt(process),
                    process.ProcessName + ".exe",
                    currentTarget))
            {
                _logger.LogDebug(
                    "Ignoring changed process identity for {Game} PID {Pid}",
                    game.DisplayName,
                    game.ProcessId);
                process.Dispose();
                return;
            }

            process.EnableRaisingEvents = true;
            process.Exited += OnGameProcessExited;
        }
        catch (Exception ex)
        {
            process?.Dispose();
            process = null;
            _logger.LogWarning(ex, "Could not attach to process {Pid}, will rely on polling for exit detection", game.ProcessId);
            // Start a polling-based exit check
            try { _pollTimer?.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)); }
            catch (ObjectDisposedException) { }
        }

        lock (_trackingSync)
        {
            if (!_running || _currentGame is not null)
            {
                if (process is not null)
                {
                    process.Exited -= OnGameProcessExited;
                    process.Dispose();
                }
                return;
            }

            _trackedProcess = process;
            process = null;
            _currentGame = game;
        }

        GameStarted?.Invoke(game);
    }

    private void OnGameProcessExited(object? sender, EventArgs e)
    {
        if (sender is not Process exitedProcess)
            return;

        int exitedProcessId;
        try
        {
            exitedProcessId = exitedProcess.Id;
        }
        catch (InvalidOperationException)
        {
            return;
        }

        GameInfo? game;
        lock (_trackingSync)
            game = _currentGame;
        if (game is null || exitedProcessId != game.ProcessId)
            return;

        if (StopTracking(game, exitedProcess) is null)
        {
            _logger.LogDebug(
                "Ignoring stale process-exit callback for PID {ExitedPid}; active game is PID {ActivePid}",
                exitedProcessId, game.ProcessId);
            return;
        }

        _logger.LogInformation("Game process exited: {Game} (PID {Pid})", game.DisplayName, game.ProcessId);
        GameStopped?.Invoke(game);
    }

    private GameInfo? StopTracking(GameInfo? expectedGame = null, Process? expectedProcess = null)
    {
        Process? trackedProcess;
        GameInfo? stoppedGame;
        Timer? pollTimer;
        bool restorePolling;
        lock (_trackingSync)
        {
            if ((expectedGame is not null && !ReferenceEquals(_currentGame, expectedGame)) ||
                (expectedProcess is not null && !ReferenceEquals(_trackedProcess, expectedProcess)))
            {
                return null;
            }

            trackedProcess = _trackedProcess;
            _trackedProcess = null;
            stoppedGame = _currentGame;
            _currentGame = null;
            pollTimer = _pollTimer;
            restorePolling = _running;
        }

        if (trackedProcess is not null)
        {
            trackedProcess.Exited -= OnGameProcessExited;
            trackedProcess.Dispose();
        }

        // Restore normal poll interval
        if (restorePolling)
        {
            try { pollTimer?.Change(PollInterval, PollInterval); }
            catch (ObjectDisposedException) { }
        }

        return stoppedGame;
    }

    private static string? GetWindowTitle(Process process)
    {
        try
        {
            return string.IsNullOrWhiteSpace(process.MainWindowTitle) ? null : process.MainWindowTitle;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? GetProcessStartedAt(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return GetProcessStartedAt(process);
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? GetProcessStartedAt(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsCurrentProcess(GameInfo game)
    {
        try
        {
            using var process = Process.GetProcessById(game.ProcessId);
            var target = new GameCaptureTarget(
                game.ExecutableName,
                game.ProcessId,
                game.ExecutableName,
                game.ProcessStartedAt);
            return !process.HasExited && GameProcessIdentity.Matches(
                process.Id,
                GetProcessStartedAt(process),
                process.ProcessName + ".exe",
                target);
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }
}
