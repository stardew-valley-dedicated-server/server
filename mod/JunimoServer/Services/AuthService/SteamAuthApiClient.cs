using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace JunimoServer.Services.Auth
{
    /// <summary>
    /// API client for communicating with the Steam authentication service.
    /// Handles all HTTP requests, serialization, error handling, and retry logic.
    /// </summary>
    public class SteamAuthApiClient : IDisposable
    {
        private readonly string _baseUrl;
        private readonly HttpClient _httpClient;
        private bool _disposed;

        // Retry configuration
        private const int MaxRetries = 3;
        private static readonly int[] RetryDelaysMs = { 1000, 2000, 4000 }; // Exponential backoff

        public SteamAuthApiClient(string baseUrl, int timeoutMs = 60000)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs)
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _httpClient.Dispose();
            _disposed = true;
        }

        /// <summary>
        /// Get service health status
        /// </summary>
        public HealthResponse GetHealth()
        {
            return Get<HealthResponse>("/health");
        }

        /// <summary>
        /// Start login with username and password credentials
        /// </summary>
        public LoginStatusResponse StartCredentialsLogin(string username, string password)
        {
            if (string.IsNullOrEmpty(username))
                throw new ArgumentException("Username cannot be null or empty", nameof(username));
            if (string.IsNullOrEmpty(password))
                throw new ArgumentException("Password cannot be null or empty", nameof(password));

            var request = new { username, password };
            return Post<LoginStatusResponse>("/steam/login/start", request);
        }

        /// <summary>
        /// Start login with QR code
        /// </summary>
        public LoginStatusResponse StartQrLogin()
        {
            return Post<LoginStatusResponse>("/steam/login/start-qr", null);
        }

        /// <summary>
        /// Get current login status
        /// </summary>
        public LoginStatusResponse GetLoginStatus()
        {
            return Get<LoginStatusResponse>("/steam/login/status");
        }

        /// <summary>
        /// Submit Steam Guard code (or empty string for approval-based auth)
        /// </summary>
        public LoginStatusResponse SubmitCode(string code)
        {
            var request = new { code };
            return Post<LoginStatusResponse>("/steam/login/submit-code", request);
        }

        /// <summary>
        /// Get encrypted app ticket for a game
        /// </summary>
        public AppTicketResponse GetAppTicket()
        {
            return Get<AppTicketResponse>($"/steam/app-ticket");
        }

        /// <summary>
        /// Get refresh token for SteamKit2 login (used for lobby creation)
        /// </summary>
        public RefreshTokenResponse GetRefreshToken()
        {
            return Get<RefreshTokenResponse>("/steam/refresh-token");
        }

        // ====================================================================
        // Lobby Management
        // ====================================================================

        /// <summary>
        /// Create a Steam lobby via the steam-auth service
        /// </summary>
        public CreateLobbyResponse CreateLobby(ulong gameServerSteamId, string protocolVersion, int maxMembers)
        {
            var request = new
            {
                game_server_steam_id = gameServerSteamId,
                protocol_version = protocolVersion,
                max_members = maxMembers
            };
            return Post<CreateLobbyResponse>("/steam/lobby/create", request);
        }

        /// <summary>
        /// Set metadata on a Steam lobby (uses stored gameServerSteamId/protocolVersion from CreateLobby)
        /// </summary>
        public SetLobbyDataResponse SetLobbyData(ulong lobbyId, System.Collections.Generic.Dictionary<string, string> metadata = null)
        {
            var request = new
            {
                lobby_id = lobbyId,
                metadata
            };
            return Post<SetLobbyDataResponse>("/steam/lobby/set-data", request);
        }

        /// <summary>
        /// Set privacy on a Steam lobby (uses stored gameServerSteamId/protocolVersion from CreateLobby)
        /// </summary>
        public SetLobbyPrivacyResponse SetLobbyPrivacy(ulong lobbyId, string privacy = "public")
        {
            var request = new
            {
                lobby_id = lobbyId,
                privacy
            };
            return Post<SetLobbyPrivacyResponse>("/steam/lobby/set-privacy", request);
        }

        /// <summary>
        /// Get current lobby status
        /// </summary>
        public LobbyStatusResponse GetLobbyStatus()
        {
            return Get<LobbyStatusResponse>("/steam/lobby/status");
        }

        // NOTE: These methods use .Result which blocks the thread. This is intentional because
        // the callers (AuthService Harmony patches) run in game loop context where async
        // would require significant restructuring. The HTTP calls are to localhost (steam-auth
        // container) so latency is minimal (<10ms).

        /// <summary>
        /// Generic GET request (blocking) with retry logic.
        /// </summary>
        private T Get<T>(string path)
        {
            return ExecuteWithRetry(() =>
            {
                var url = $"{_baseUrl}{path}";
                var response = _httpClient.GetAsync(url).Result;

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = response.Content.ReadAsStringAsync().Result;
                    throw new HttpRequestException($"Steam auth service returned error {response.StatusCode}: {errorContent}");
                }

                var json = response.Content.ReadAsStringAsync().Result;
                return JsonSerializer.Deserialize<T>(json);
            });
        }

        /// <summary>
        /// Generic POST request (blocking) with retry logic.
        /// </summary>
        private T Post<T>(string path, object body)
        {
            return ExecuteWithRetry(() =>
            {
                var url = $"{_baseUrl}{path}";

                var jsonBody = body != null
                    ? JsonSerializer.Serialize(body)
                    : "";

                var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                var response = _httpClient.PostAsync(url, content).Result;

                if (!response.IsSuccessStatusCode)
                {
                    var errorContent = response.Content.ReadAsStringAsync().Result;
                    throw new HttpRequestException($"Steam auth service returned error {response.StatusCode}: {errorContent}");
                }

                var json = response.Content.ReadAsStringAsync().Result;
                return JsonSerializer.Deserialize<T>(json);
            });
        }

        /// <summary>
        /// Executes a function with exponential backoff retry logic.
        /// Retries on transient failures (network errors, timeouts).
        /// </summary>
        private T ExecuteWithRetry<T>(Func<T> action)
        {
            Exception lastException = null;

            for (int attempt = 0; attempt <= MaxRetries; attempt++)
            {
                try
                {
                    return action();
                }
                catch (HttpRequestException ex) when (IsTransientError(ex))
                {
                    lastException = ex;
                    if (attempt < MaxRetries)
                    {
                        Thread.Sleep(RetryDelaysMs[attempt]);
                    }
                }
                catch (AggregateException ex) when (ex.InnerException is HttpRequestException httpEx && IsTransientError(httpEx))
                {
                    lastException = ex.InnerException;
                    if (attempt < MaxRetries)
                    {
                        Thread.Sleep(RetryDelaysMs[attempt]);
                    }
                }
                catch (Exception ex) when (IsTransientException(ex))
                {
                    lastException = ex;
                    if (attempt < MaxRetries)
                    {
                        Thread.Sleep(RetryDelaysMs[attempt]);
                    }
                }
            }

            throw new HttpRequestException($"Steam auth service request failed after {MaxRetries + 1} attempts", lastException);
        }

        /// <summary>
        /// Determines if an HttpRequestException is transient (worth retrying).
        /// </summary>
        private static bool IsTransientError(HttpRequestException ex)
        {
            // Retry on connection failures, timeouts, and 5xx errors
            var message = ex.Message.ToLowerInvariant();
            return message.Contains("connection") ||
                   message.Contains("timeout") ||
                   message.Contains("503") ||
                   message.Contains("502") ||
                   message.Contains("504");
        }

        /// <summary>
        /// Determines if an exception is transient (worth retrying).
        /// </summary>
        private static bool IsTransientException(Exception ex)
        {
            // Retry on task cancellation (timeout) and socket errors
            return ex is TaskCanceledException ||
                   ex is System.Net.Sockets.SocketException ||
                   (ex is AggregateException agg && agg.InnerException is TaskCanceledException);
        }
    }

    // Response types
    public class HealthResponse
    {
        public string status { get; set; }
        public bool logged_in { get; set; }
        public string timestamp { get; set; }
    }

    public class LoginStatusResponse
    {
        public string status { get; set; }
        public string message { get; set; }
        public string refreshToken { get; set; }
        public ValidAction[] validActions { get; set; }
        public string challengeUrl { get; set; }
        public string qrCode { get; set; }
    }

    public class ValidAction
    {
        public int type { get; set; }
        public string detail { get; set; }
    }

    public class AppTicketResponse
    {
        public string app_ticket { get; set; }
        public string expires_at { get; set; }
        public string steam_id { get; set; }
    }

    public class RefreshTokenResponse
    {
        public string username { get; set; }
        public string refresh_token { get; set; }
    }

    public class CreateLobbyResponse
    {
        public string lobby_id { get; set; }
        public uint app_id { get; set; }
    }

    public class SetLobbyDataResponse
    {
        public bool success { get; set; }
    }

    public class SetLobbyPrivacyResponse
    {
        public bool success { get; set; }
    }

    public class LobbyStatusResponse
    {
        public string lobby_id { get; set; }
        public bool is_logged_in { get; set; }
    }
}
