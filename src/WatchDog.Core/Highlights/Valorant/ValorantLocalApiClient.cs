using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WatchDog.Core.Highlights.Valorant;

/// <summary>
/// Connects to the Riot Client local API via lockfile discovery.
/// Uses WebSocket for real-time events with HTTPS polling fallback.
/// </summary>
internal sealed class ValorantLocalApiClient : IAsyncDisposable
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private int _port;
    private string _password = string.Empty;

    public event Action<string>? MessageReceived;
    public bool IsConnected => _webSocket?.State == WebSocketState.Open;

    public ValorantLocalApiClient(ILogger logger, HttpClient? httpClient = null)
    {
        _logger = logger;

        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsHttpClient = false;
        }
        else
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (message, cert, chain, errors) =>
                {
                    if (message.RequestUri?.Host is "127.0.0.1" or "localhost")
                        return true;
                    return errors == SslPolicyErrors.None;
                }
            };
            _httpClient = new HttpClient(handler);
            _ownsHttpClient = true;
        }
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        var lockfileData = await ReadLockfileWithRetryAsync(ct);
        if (lockfileData is null)
        {
            _logger.LogWarning("Could not read Valorant lockfile — Riot Client may not be running");
            return false;
        }

        (_port, _password) = lockfileData.Value;

        var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{_password}"));
        _httpClient.DefaultRequestHeaders.Clear();
        _httpClient.DefaultRequestHeaders.Add("Authorization", $"Basic {authHeader}");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        // Try WebSocket first, fall back to HTTP polling
        if (await TryConnectWebSocketAsync(_cts.Token))
        {
            _listenTask = WebSocketListenLoopAsync(_cts.Token);
            _logger.LogInformation("Connected to Valorant via WebSocket on port {Port}", _port);
        }
        else
        {
            _listenTask = HttpPollFallbackAsync(_cts.Token);
            _logger.LogInformation("Connected to Valorant via HTTP polling on port {Port}", _port);
        }

        return true;
    }

    public async Task DisconnectAsync()
    {
        _cts?.Cancel();

        if (_webSocket is not null)
        {
            try
            {
                if (_webSocket.State == WebSocketState.Open)
                    await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch { /* Best effort */ }
            _webSocket.Dispose();
            _webSocket = null;
        }

        if (_listenTask is not null)
        {
            try { await _listenTask; }
            catch (OperationCanceledException) { }
        }

        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        if (_ownsHttpClient) _httpClient.Dispose();
    }

    /// <summary>
    /// Polls the HTTPS endpoint for current game state.
    /// </summary>
    public async Task<string?> GetGameStateAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                $"https://127.0.0.1:{_port}/chat/v4/presences", ct);

            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync(ct);

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error fetching Valorant game state");
            return null;
        }
    }

    private async Task<(int port, string password)?> ReadLockfileWithRetryAsync(CancellationToken ct)
    {
        var lockfilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Riot Games", "Riot Client", "Config", "lockfile");

        for (int attempt = 0; attempt < 5; attempt++)
        {
            if (ct.IsCancellationRequested) return null;

            try
            {
                if (!File.Exists(lockfilePath))
                {
                    _logger.LogDebug("Lockfile not found, attempt {Attempt}/5", attempt + 1);
                    await Task.Delay(2000, ct);
                    continue;
                }

                // Riot holds a lock on the file, so open with sharing flags
                using var fs = new FileStream(lockfilePath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                var content = await reader.ReadToEndAsync(ct);

                // Format: name:pid:port:password:protocol
                var parts = content.Split(':');
                if (parts.Length >= 5 && int.TryParse(parts[2], out var port))
                {
                    return (port, parts[3]);
                }

                _logger.LogDebug("Lockfile format unexpected: got {PartCount} colon-separated parts", parts.Length);
            }
            catch (IOException ex)
            {
                _logger.LogDebug(ex, "Error reading lockfile, attempt {Attempt}/5", attempt + 1);
            }

            await Task.Delay(2000, ct);
        }

        return null;
    }

    private async Task<bool> TryConnectWebSocketAsync(CancellationToken ct)
    {
        try
        {
            _webSocket = new ClientWebSocket();
            _webSocket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"riot:{_password}"));
            _webSocket.Options.SetRequestHeader("Authorization", $"Basic {credentials}");

            var uri = new Uri($"wss://127.0.0.1:{_port}");
            await _webSocket.ConnectAsync(uri, ct);

            return _webSocket.State == WebSocketState.Open;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WebSocket connection failed, will use HTTP polling");
            _webSocket?.Dispose();
            _webSocket = null;
            return false;
        }
    }

    private async Task WebSocketListenLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];

        while (!ct.IsCancellationRequested && _webSocket?.State == WebSocketState.Open)
        {
            try
            {
                // Accumulate frames until EndOfMessage (payloads can exceed buffer size)
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger.LogInformation("Valorant WebSocket closed, switching to HTTP polling");
                        _listenTask = HttpPollFallbackAsync(ct);
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(ms.ToArray());
                    MessageReceived?.Invoke(message);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (WebSocketException ex)
            {
                _logger.LogDebug(ex, "WebSocket error, switching to HTTP polling");
                _listenTask = HttpPollFallbackAsync(ct);
                return;
            }
        }
    }

    private async Task HttpPollFallbackAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var json = await GetGameStateAsync(ct);
                if (json is not null)
                    MessageReceived?.Invoke(json);

                await Task.Delay(1000, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error in HTTP poll loop");
                await Task.Delay(3000, ct);
            }
        }
    }
}
