using System.Diagnostics;
using System.Management;
using Microsoft.Extensions.Logging;

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
    private bool _disposed;
    private bool _running;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    public GameInfo? CurrentGame { get; private set; }

    public event Action<GameInfo>? GameStarted;
    public event Action<GameInfo>? GameStopped;

    public ProcessGameDetector(GameDatabase gameDatabase, ILogger<ProcessGameDetector> logger)
    {
        _gameDatabase = gameDatabase;
        _logger = logger;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;

        StartWmiWatcher();
        StartPolling();

        // Check for already-running games on startup
        ScanRunningProcesses();

        _logger.LogInformation("Game detector started (WMI + polling every {Interval}s)", PollInterval.TotalSeconds);
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        StopWmiWatcher();
        StopPolling();
        StopTracking();

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
            if (e.NewEvent["TargetInstance"] is not ManagementBaseObject targetInstance)
                return;

            using (targetInstance)
            {
                var processName = targetInstance["Name"]?.ToString();
                var processId = Convert.ToInt32(targetInstance["ProcessId"]);

                if (processName is null)
                    return;

                var game = _gameDatabase.TryMatch(processName, processId);
                if (game is not null)
                {
                    _logger.LogInformation("WMI detected game: {Game} (PID {Pid})", game.DisplayName, game.ProcessId);
                    TrackGame(game);
                }
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

        if (CurrentGame is not null)
            return; // Already tracking

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

    private void TrackGame(GameInfo game)
    {
        if (CurrentGame is not null)
            return;

        CurrentGame = game;

        try
        {
            _trackedProcess = Process.GetProcessById(game.ProcessId);
            _trackedProcess.EnableRaisingEvents = true;
            _trackedProcess.Exited += OnGameProcessExited;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not attach to process {Pid}, will rely on polling for exit detection", game.ProcessId);
            // Start a polling-based exit check
            _pollTimer?.Change(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        }

        GameStarted?.Invoke(game);
    }

    private void OnGameProcessExited(object? sender, EventArgs e)
    {
        var game = CurrentGame;
        if (game is null) return;

        _logger.LogInformation("Game process exited: {Game} (PID {Pid})", game.DisplayName, game.ProcessId);
        StopTracking();
        GameStopped?.Invoke(game);
    }

    private void StopTracking()
    {
        if (_trackedProcess is not null)
        {
            _trackedProcess.Exited -= OnGameProcessExited;
            _trackedProcess.Dispose();
            _trackedProcess = null;
        }

        CurrentGame = null;

        // Restore normal poll interval
        _pollTimer?.Change(PollInterval, PollInterval);
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }
}
