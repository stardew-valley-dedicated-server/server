using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace JunimoServer.Services.Auth
{
    /// <summary>
    /// API client for communicating with the Steam authentication service.
    /// Handles all HTTP requests, serialization, and error handling.
    /// </summary>
    public class SteamAuthApiClient
    {
        private readonly string _baseUrl;
        private readonly int _timeoutMs;
        private readonly HttpClient _httpClient;

        public SteamAuthApiClient(string baseUrl, int timeoutMs = 60000)
        {
            _baseUrl = baseUrl.TrimEnd('/');
            _timeoutMs = timeoutMs;
            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromMilliseconds(timeoutMs)
            };
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
        /// Generic GET request
        /// </summary>
        private T Get<T>(string path)
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
        }

        /// <summary>
        /// Generic POST request
        /// </summary>
        private T Post<T>(string path, object body)
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
}
