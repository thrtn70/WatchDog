using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Highlights.Cs2;

public sealed class Cs2HighlightDetector : IHighlightDetector
{
    private readonly ILogger<Cs2HighlightDetector> _logger;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private Cs2GameState? _previousState;

    public string GameExecutableName => "cs2.exe";
    public bool IsRunning => _listener?.IsListening ?? false;

    public event Action<HighlightDetectedEventArgs>? HighlightDetected;

    private const int Port = 35847;

    public Cs2HighlightDetector(ILogger<Cs2HighlightDetector> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        if (_listener is not null) return Task.CompletedTask;

        _previousState = null;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://localhost:{Port}/");

        try
        {
            _listener.Start();
            _listenTask = ListenLoopAsync(_cts.Token);
            _logger.LogInformation("CS2 GSI listener started on port {Port}", Port);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start CS2 GSI listener on port {Port}", Port);
            _listener = null;
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_listener is null) return;

        _cts?.Cancel();
        _listener.Stop();

        if (_listenTask is not null)
        {
            try { await _listenTask; }
            catch (OperationCanceledException) { }
        }

        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _previousState = null;

        _logger.LogInformation("CS2 GSI listener stopped");
    }

    private async Task ListenLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener?.IsListening == true)
        {
            try
            {
                var context = await _listener.GetContextAsync().WaitAsync(ct);
                await ProcessRequestAsync(context);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error processing GSI request");
            }
        }
    }

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        // Reject oversized payloads (CS2 GSI is typically <2KB)
        if (context.Request.ContentLength64 > 65_536)
        {
            context.Response.StatusCode = 413;
            context.Response.Close();
            return;
        }

        string body;
        using (var reader = new System.IO.StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync();
        }

        // Respond immediately (CS2 expects a quick 200)
        context.Response.StatusCode = 200;
        context.Response.Close();

        var newState = Cs2GsiPayloadParser.Parse(body);
        if (newState is null) return;

        DetectHighlights(_previousState, newState);
        _previousState = newState;
    }

    internal void DetectHighlights(Cs2GameState? previous, Cs2GameState current)
    {
        if (previous is null) return; // First payload — no diff possible

        // Kill detection
        if (current.Kills > previous.Kills)
        {
            var killCount = current.Kills - previous.Kills;
            HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                HighlightType.Kill, $"{killCount} kill(s)"));
        }

        // Ace detection (5 kills in one round)
        if (current.RoundKills >= 5 && previous.RoundKills < 5)
        {
            HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                HighlightType.Ace, "ACE! 5 kills in one round"));
        }

        // Death detection
        if (current.Health == 0 && previous.Health > 0)
        {
            HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                HighlightType.Death));
        }

        // Round end detection
        if (current.RoundPhase == "over" && previous.RoundPhase == "live")
        {
            if (current.TeamScore > previous.TeamScore)
            {
                HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                    HighlightType.RoundWin, $"Round won ({current.TeamScore}-{current.EnemyScore})"));
            }
            else if (current.EnemyScore > previous.EnemyScore)
            {
                HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                    HighlightType.RoundLoss, $"Round lost ({current.TeamScore}-{current.EnemyScore})"));
            }
        }

        // Match end detection
        if (current.MapPhase == "gameover" && previous.MapPhase != "gameover")
        {
            if (current.TeamScore > current.EnemyScore)
            {
                HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                    HighlightType.MatchWin, $"Match won {current.TeamScore}-{current.EnemyScore}"));
            }
            else
            {
                HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                    HighlightType.MatchLoss, $"Match lost {current.TeamScore}-{current.EnemyScore}"));
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }
}
