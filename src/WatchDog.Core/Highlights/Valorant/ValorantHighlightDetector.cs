using System.Net.Http;
using System.Net.Security;
using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Highlights.Valorant;

public sealed class ValorantHighlightDetector : IHighlightDetector
{
    private readonly ILogger<ValorantHighlightDetector> _logger;
    private readonly HttpClient _sharedHttpClient;
    private ValorantLocalApiClient? _client;
    private ValorantGameState? _previousState;

    public string GameExecutableName => "valorant-win64-shipping.exe";
    public IReadOnlyList<string> SupportedExecutableNames =>
        ["valorant-win64-shipping.exe", "valorant.exe"];
    public bool IsRunning => _client?.IsConnected ?? false;

    public event Action<HighlightDetectedEventArgs>? HighlightDetected;

    public ValorantHighlightDetector(ILogger<ValorantHighlightDetector> logger)
    {
        _logger = logger;

        // Shared HttpClient with SSL bypass scoped to localhost only — reused across game sessions.
        // Riot's local API uses a self-signed cert at 127.0.0.1, so we tolerate
        // RemoteCertificateChainErrors and RemoteCertificateNameMismatch on loopback only.
        // Use bitwise mask so combined-flag values (e.g. ChainErrors | NameMismatch) are also accepted.
        const SslPolicyErrors LoopbackAllowedErrors =
            SslPolicyErrors.RemoteCertificateChainErrors |
            SslPolicyErrors.RemoteCertificateNameMismatch;

        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (message, cert, _, errors) =>
            {
                if (message.RequestUri?.Host is not ("127.0.0.1" or "localhost"))
                    return false;
                if (cert is null)
                    return false;
                return (errors & ~LoopbackAllowedErrors) == SslPolicyErrors.None;
            }
        };
        _sharedHttpClient = new HttpClient(handler);
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_client is not null) return;

        _previousState = null;
        _client = new ValorantLocalApiClient(_logger, _sharedHttpClient);
        _client.MessageReceived += OnMessageReceived;

        var connected = await _client.ConnectAsync(ct);
        if (!connected)
        {
            _logger.LogWarning("Valorant local API connection failed — highlight detection unavailable");
            await DisposeClientAsync();
        }
        else
        {
            _logger.LogInformation("Valorant highlight detector started");
        }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await DisposeClientAsync();
        _previousState = null;
        _logger.LogInformation("Valorant highlight detector stopped");
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeClientAsync();
        _sharedHttpClient.Dispose();
    }

    private void OnMessageReceived(string json)
    {
        var newState = ValorantEventParser.ParseSessionPayload(json)
                       ?? ValorantEventParser.Parse(json);

        if (newState is null) return;

        DetectHighlights(_previousState, newState);
        _previousState = newState;
    }

    internal void DetectHighlights(ValorantGameState? previous, ValorantGameState current)
    {
        if (previous is null) return;

        // Match start detection: transition to in_progress means a new match began
        if (current.MatchPhase == "in_progress" && previous.MatchPhase != "in_progress")
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

            // Ace takes priority over multikill (avoid double-fire)
            if (current.RoundKills >= 5 && previous.RoundKills < 5)
            {
                HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                    HighlightType.Ace, "ACE! 5 kills in one round"));
            }
            else if (current.RoundKills >= 3 && previous.RoundKills < current.RoundKills)
            {
                HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                    HighlightType.Multikill, $"{current.RoundKills}K round"));
            }
        }

        // Death detection
        if (current.Health == 0 && previous.Health > 0)
        {
            HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                HighlightType.Death));
        }

        // Spike plant detection (Valorant equivalent of bomb plant)
        if (current.SpikeState && !previous.SpikeState)
        {
            HighlightDetected?.Invoke(new HighlightDetectedEventArgs(
                HighlightType.BombPlant, "Spike planted"));
        }

        // Round end detection
        if (current.RoundPhase == "end" && previous.RoundPhase is "combat" or "shopping")
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
        if (current.MatchPhase == "completed" && previous.MatchPhase != "completed")
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

    private async Task DisposeClientAsync()
    {
        if (_client is not null)
        {
            _client.MessageReceived -= OnMessageReceived;
            await _client.DisposeAsync();
            _client = null;
        }
    }
}
