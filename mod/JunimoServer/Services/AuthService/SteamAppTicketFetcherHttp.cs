using System;
using System.Linq;
using System.Threading;
using StardewModdingAPI;

namespace JunimoServer.Services.Auth
{
    /// <summary>
    /// HTTP-based Steam App Ticket Fetcher that communicates with the steam-auth service.
    ///
    /// This implementation isolates Steam credentials and tokens in a separate service,
    /// exposing only the minimal encrypted app ticket to the game server.
    /// All authentication (credentials, Steam Guard, QR) is handled by the steam-auth
    /// service via its CLI setup flow; the game mod never prompts the user directly.
    /// </summary>
    public class SteamAppTicketFetcherHttp
    {
        private readonly SteamAuthApiClient _api;
        private readonly IMonitor _monitor;
        private string _refreshToken = null;

        /// <summary>
        /// The Steam refresh token obtained during authentication.
        /// Can be used for SteamKit2 login.
        /// </summary>
        public string RefreshToken => _refreshToken;

        /// <summary>
        /// Creates a new SteamAppTicketFetcherHttp instance.
        /// </summary>
        /// <param name="monitor">SMAPI monitor for logging</param>
        /// <param name="steamAuthUrl">URL of the steam-auth service</param>
        /// <param name="timeoutMs">Timeout for HTTP requests</param>
        public SteamAppTicketFetcherHttp(
            IMonitor monitor,
            string steamAuthUrl = "http://localhost:3001",
            int timeoutMs = 60000
        )
        {
            _monitor = monitor;
            _api = new SteamAuthApiClient(steamAuthUrl, timeoutMs);
        }

        /// <summary>
        /// Verifies that the steam-auth service is healthy and logged in.
        /// Also fetches the refresh token for SteamKit2 login.
        /// </summary>
        public void VerifyServiceReady()
        {
            const int budgetSec = 30;
            const int pollSec = 2;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(budgetSec);
            HealthResponse last = null;
            Exception lastEx = null;

            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    last = _api.GetHealth();
                    if (last != null && last.status == "ok" && last.logged_in)
                    {
                        TryFetchRefreshToken();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    lastEx = ex;
                }

                Thread.Sleep(TimeSpan.FromSeconds(pollSec));
            }

            if (last == null)
                throw new Exception(
                    $"Could not reach steam-auth service within {budgetSec}s"
                        + (lastEx != null ? $": {lastEx.Message}" : "")
                );
            if (last.status != "ok")
                throw new Exception(
                    $"Steam-auth service unhealthy after {budgetSec}s: status={last.status}"
                );

            var anyEverLoggedIn = last.accounts != null && last.accounts.Any(a => a.logged_in);
            if (!anyEverLoggedIn)
                throw new Exception(
                    "Steam-auth service has no logged-in accounts. "
                        + "If this is a fresh install, run 'docker compose run -it steam-auth setup'. "
                        + $"Account snapshot: {SummarizeAccounts(last.accounts)}"
                );

            throw new Exception(
                $"Steam-auth account currently disconnected after {budgetSec}s of waiting "
                    + $"(sidecar may be mid-reconnect; check its logs for account_reconnect_* events). "
                    + $"Account snapshot: {SummarizeAccounts(last.accounts)}"
            );
        }

        private static string SummarizeAccounts(HealthAccount[] accounts) =>
            accounts == null
                ? "(none reported)"
                : string.Join(
                    ", ",
                    accounts.Select(a => $"A{a.index}={(a.logged_in ? "ok" : "disconnected")}")
                );

        private void TryFetchRefreshToken()
        {
            try
            {
                var t = _api.GetRefreshToken();
                if (!string.IsNullOrEmpty(t?.refresh_token))
                {
                    _refreshToken = t.refresh_token;
                    _monitor.Log(
                        "Steam refresh token fetched from steam-auth service ✓",
                        LogLevel.Debug
                    );
                }
            }
            catch (Exception ex)
            {
                _monitor.Log(
                    $"Warning: Could not fetch refresh token for SteamKit2: {ex.Message}",
                    LogLevel.Warn
                );
            }
        }

        /// <summary>
        /// Fetches an encrypted app ticket from the steam-auth service.
        /// </summary>
        /// <returns>Encrypted Steam app ticket</returns>
        public SteamEncryptedAppTicket GetTicket()
        {
            try
            {
                _monitor.Log(
                    "Requesting Steam app ticket from steam-auth service...",
                    LogLevel.Debug
                );

                var response = _api.GetAppTicket();

                if (string.IsNullOrEmpty(response?.app_ticket))
                {
                    throw new InvalidOperationException(
                        "Steam auth service returned empty app ticket"
                    );
                }

                _monitor.Log(
                    "Steam encrypted app ticket received from steam-auth service ✓",
                    LogLevel.Info
                );

                // Convert steam-auth response to the expected format
                return new SteamEncryptedAppTicket
                {
                    Ticket = response.app_ticket,
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Expiry = string.IsNullOrEmpty(response.expires_at)
                        ? DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds()
                        : DateTimeOffset.Parse(response.expires_at).ToUnixTimeSeconds(),
                };
            }
            catch (Exception ex)
            {
                _monitor.Log(
                    $"Failed to get app ticket from steam-auth service: {ex.Message}",
                    LogLevel.Error
                );
                throw;
            }
        }
    }
}
