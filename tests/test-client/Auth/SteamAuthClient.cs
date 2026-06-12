using System.Text.Json;
using StardewModdingAPI;

namespace JunimoTestClient.Auth;

/// <summary>
/// Client for communicating with the steam-auth service.
/// Supports multi-account via ?account=N query parameter.
/// </summary>
public class SteamAuthClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly IMonitor _monitor;
    private readonly string _baseUrl;
    private readonly int _accountIndex;
    private bool _disposed;

    public SteamAuthClient(string baseUrl, int accountIndex, IMonitor monitor)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _accountIndex = accountIndex;
        _monitor = monitor;
        // InfiniteTimeSpan disables HttpClient's built-in timer so per-request
        // CancellationTokenSources below give us precise control. Without this a
        // single slow response can consume the entire retry budget (we saw 32s
        // hangs in production leaving zero retries).
        _httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    private string AccountQuery => $"?account={_accountIndex}";

    /// <summary>
    /// Polls /steam/ready until it reports ready, or the overall deadline expires.
    /// Per-request timeout (5s) is short enough that a single stuck server-side call
    /// doesn't starve retries; overall deadline (default 30s) bounds total wait.
    /// With 5s per-request + 1s delay, the worst case is ~5 attempts within 30s.
    /// </summary>
    public async Task<ReadyResponse?> WaitForReadyAsync(int timeoutSeconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var perRequestTimeout = TimeSpan.FromSeconds(5);
        var retryDelay = TimeSpan.FromSeconds(1);
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            attempt++;
            try
            {
                using var cts = new CancellationTokenSource(perRequestTimeout);
                var response = await _httpClient.GetAsync(
                    $"{_baseUrl}/steam/ready{AccountQuery}",
                    cts.Token
                );
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var ready = JsonSerializer.Deserialize<ReadyResponse>(json);
                    if (ready?.ready == true)
                    {
                        return ready;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                _monitor.Log(
                    $"Steam auth ready check attempt {attempt}: per-request timeout ({perRequestTimeout.TotalSeconds}s)",
                    LogLevel.Trace
                );
            }
            catch (Exception ex)
            {
                _monitor.Log(
                    $"Steam auth ready check attempt {attempt}: {ex.Message}",
                    LogLevel.Trace
                );
            }

            await Task.Delay(retryDelay);
        }

        return null;
    }

    /// <summary>
    /// Get an encrypted app ticket for Galaxy authentication.
    /// Uses short per-request timeouts with retries: the server-side first ticket fetch
    /// can take up to 30s (SteamAuthService.TicketRequestTimeout), but subsequent calls
    /// hit a 10-minute cache. Retrying with a 10s per-request timeout is the sweet spot.
    /// </summary>
    public async Task<AppTicketResponse?> GetAppTicketAsync()
    {
        const int maxAttempts = 3;
        var perRequestTimeout = TimeSpan.FromSeconds(10);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var cts = new CancellationTokenSource(perRequestTimeout);
                var response = await _httpClient.GetAsync(
                    $"{_baseUrl}/steam/app-ticket{AccountQuery}",
                    cts.Token
                );
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    _monitor.Log(
                        $"Failed to get app ticket (attempt {attempt}/{maxAttempts}): {response.StatusCode} - {error}",
                        LogLevel.Error
                    );
                    if (attempt == maxAttempts)
                    {
                        return null;
                    }

                    continue;
                }

                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<AppTicketResponse>(json);
            }
            catch (OperationCanceledException)
            {
                _monitor.Log(
                    $"Failed to get app ticket (attempt {attempt}/{maxAttempts}): per-request timeout ({perRequestTimeout.TotalSeconds}s)",
                    LogLevel.Error
                );
            }
            catch (Exception ex)
            {
                _monitor.Log(
                    $"Failed to get app ticket (attempt {attempt}/{maxAttempts}): {ex.Message}",
                    LogLevel.Error
                );
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _disposed = true;
    }

    public class ReadyResponse
    {
        public bool ready { get; set; }
        public int account { get; set; }
        public string? username { get; set; }
        public string? steam_id { get; set; }
        public bool has_ticket { get; set; }
    }

    public class AppTicketResponse
    {
        public string? app_ticket { get; set; }
        public string? steam_id { get; set; }
        public string? source { get; set; }
        public string? sha8 { get; set; }
        public int ticket_length_bytes { get; set; }
    }
}
