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

    /// <summary>
    /// Auth token that must match the token in gamestate_integration_watchdog.cfg.
    /// CS2 GSI sends this in every payload under "auth": {"token": "..."}.
    /// Payloads without a matching token are rejected to prevent local spoofing.
    /// </summary>
    internal const string GsiAuthToken = "watchdog_gsi_v1";

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
            _listener.Close();
            _listener = null;
            _cts?.Dispose();
            _cts = null;
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

        _listener.Close();
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

    private const int MaxPayloadBytes = 65_536;

    private async Task ProcessRequestAsync(HttpListenerContext context)
    {
        if (context.Request.ContentLength64 > MaxPayloadBytes)
        {
            context.Response.StatusCode = 413;
            context.Response.Close();
            return;
        }

        string body;
        using (var bounded = new BoundedStream(context.Request.InputStream, MaxPayloadBytes))
        using (var reader = new System.IO.StreamReader(bounded, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync();
        }

        context.Response.StatusCode = 200;
        context.Response.Close();

        if (!ValidateAuthToken(body))
        {
            _logger.LogDebug("Rejected GSI payload: auth token mismatch");
            return;
        }

        var newState = Cs2GsiPayloadParser.Parse(body);
        if (newState is null) return;

        DetectHighlights(_previousState, newState);
        _previousState = newState;
    }

    private static bool ValidateAuthToken(string body)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("auth", out var auth)
                && auth.TryGetProperty("token", out var token)
                && token.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                return token.GetString() == GsiAuthToken;
            }
        }
        catch { }
        return false;
    }

    private sealed class BoundedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _totalRead;

        public BoundedStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var allowed = (int)Math.Min(count, _limit - _totalRead);
            if (allowed <= 0) return 0;
            var read = await _inner.ReadAsync(buffer, offset, allowed, ct);
            _totalRead += read;
            return read;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var allowed = (int)Math.Min(count, _limit - _totalRead);
            if (allowed <= 0) return 0;
            var read = _inner.Read(buffer, offset, allowed);
            _totalRead += read;
            return read;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    internal void DetectHighlights(Cs2GameState? previous, Cs2GameState current)
    {
        if (previous is null) return; // First payload — no diff possible

        // Match start detection: warmup/intermission → live means a new match began
        if (current.MapPhase == "live" && previous.MapPhase is "warmup" or "intermission")
        {
            HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                HighlightType.MatchStarted, "Match started"));
        }

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
