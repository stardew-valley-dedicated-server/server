using System;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;

namespace JunimoServer.Services.Auth
{
    /// <summary>
    /// HTTP-based Steam App Ticket Fetcher that communicates with the steam-auth service.
    ///
    /// This implementation isolates Steam credentials and tokens in a separate service,
    /// exposing only the minimal encrypted app ticket to the game server.
    ///
    /// Using WebSockets or SSE was also considered, but not used (yet) to for simplicty.
    /// </summary>
    public class SteamAppTicketFetcherHttp
    {
        private readonly SteamAuthApiClient _api;
        private readonly int _timeoutMs;
        private readonly IMonitor _monitor;
        private readonly bool _externalMode;
        private Task<string> _userInputTask = null;
        private bool _hasShownAuthChoice = false;
        private bool _hasShownQrCode = false;
        private string _lastStatus = "";

        /// <summary>
        /// Creates a new SteamAppTicketFetcherHttp instance.
        /// </summary>
        /// <param name="monitor">SMAPI monitor for logging</param>
        /// <param name="steamAuthUrl">URL of the steam-auth service</param>
        /// <param name="timeoutMs">Timeout for HTTP requests</param>
        /// <param name="externalMode">If true, skip login flow - assume steam-auth is already authenticated via CLI setup</param>
        public SteamAppTicketFetcherHttp(IMonitor monitor, string steamAuthUrl = "http://localhost:3001", int timeoutMs = 60000, bool externalMode = false)
        {
            _monitor = monitor;
            _timeoutMs = timeoutMs;
            _externalMode = externalMode;
            _api = new SteamAuthApiClient(steamAuthUrl, timeoutMs);
        }

        /// <summary>
        /// Verifies that the steam-auth service is healthy and logged in.
        /// Used in external mode to check if setup was completed.
        /// </summary>
        public void VerifyServiceReady()
        {
            var health = _api.GetHealth();

            if (health == null)
            {
                throw new Exception("Could not connect to steam-auth service");
            }

            if (health.status != "ok")
            {
                throw new Exception($"Steam-auth service unhealthy: {health.status}");
            }

            if (!health.logged_in)
            {
                throw new Exception("Steam-auth service is not logged in. Run 'docker compose run -it steam-auth setup' first.");
            }
        }

        /// <summary>
        /// Ensures Steam is authenticated with the steam-auth service before getting tickets.
        /// Handles the full login flow including Steam Guard codes and approval.
        /// </summary>
        /// <param name="username">Steam username (or "USE_QR_CODE" for QR login)</param>
        /// <param name="password">Steam password</param>
        public void EnsureAuthenticated(string username, string password)
        {
            try
            {
                _monitor.Log("Starting Steam authentication via steam-auth service...", LogLevel.Debug);

                // Start the login process
                var useQrCode = username == "USE_QR_CODE";
                var startResponse = useQrCode
                    ? _api.StartQrLogin()
                    : _api.StartCredentialsLogin(username, password);

                var startTime = DateTime.Now;
                var pollInterval = 1000; // Poll every 1 second

                // Poll for login status
                while (true)
                {
                    // Check timeout
                    if ((DateTime.Now - startTime).TotalMilliseconds > _timeoutMs)
                    {
                        throw new TimeoutException($"Steam authentication timed out after {_timeoutMs}ms");
                    }

                    // Get current status
                    var status = _api.GetLoginStatus();

                    // Only log when status changes
                    if (status.status != _lastStatus)
                    {
                        _monitor.Log($"[SteamAuth] Login status changed: {status.status}", LogLevel.Debug);
                        _lastStatus = status.status;
                    }

                    switch (status.status)
                    {
                        case "authenticated":
                            _monitor.Log("Steam authentication successful ✓", LogLevel.Info);
                            return;

                        case "needs_authentication":
                            HandleAuthenticationChoice(status);
                            break;

                        case "qr_code_ready":
                            HandleQrCodeReady(status);
                            break;

                        case "error":
                            throw new Exception($"Steam authentication failed: {status.message}");

                        case "idle":
                        case "authenticating":
                            // Check if user entered a code while we were waiting for approval
                            if (_userInputTask != null && _userInputTask.IsCompleted)
                            {
                                var userCode = _userInputTask.Result ?? "";
                                _userInputTask = null;

                                if (!string.IsNullOrWhiteSpace(userCode))
                                {
                                    _monitor.Log("Steam Guard code received from user input ✓", LogLevel.Info);

                                    // Submit the user's code
                                    _api.SubmitCode(userCode.Trim());

                                    _monitor.Log("Code submitted, waiting for authentication...", LogLevel.Debug);
                                }
                            }

                            // Still in progress, keep polling
                            Thread.Sleep(pollInterval);
                            break;

                        default:
                            _monitor.Log($"Unknown login status: {status.status}", LogLevel.Warn);
                            Thread.Sleep(pollInterval);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _monitor.Log($"Failed to authenticate with Steam auth service: {ex.Message}", LogLevel.Error);
                throw;
            }
        }

        /// <summary>
        /// Handle authentication choice - present all available options to user
        /// </summary>
        private void HandleAuthenticationChoice(LoginStatusResponse status)
        {
            // Only show this message once
            if (_hasShownAuthChoice)
            {
                return;
            }
            _hasShownAuthChoice = true;

            if (status.validActions == null || status.validActions.Length == 0)
            {
                throw new Exception("No valid authentication actions available");
            }

            _monitor.Log("***********************************************************************", LogLevel.Info);
            _monitor.Log("*                                                                     *", LogLevel.Info);
            _monitor.Log("*    Steam Guard authentication required                              *", LogLevel.Info);
            _monitor.Log("*    Available authentication methods:                                *", LogLevel.Info);
            _monitor.Log("*                                                                     *", LogLevel.Info);

            var hasEmailCode = false;
            var hasDeviceCode = false;
            var hasDeviceConfirmation = false;
            var hasEmailConfirmation = false;
            var emailDetail = "";

            foreach (var action in status.validActions)
            {
                switch (action.type)
                {
                    case 2: // EmailCode
                        hasEmailCode = true;
                        emailDetail = action.detail ?? "your email";
                        _monitor.Log($"*    [1] Enter code from email                                      *", LogLevel.Info);
                        break;
                    case 3: // DeviceCode
                        hasDeviceCode = true;
                        _monitor.Log("*    [2] Enter code from Steam mobile app                            *", LogLevel.Info);
                        break;
                    case 4: // DeviceConfirmation
                        hasDeviceConfirmation = true;
                        _monitor.Log("*    [3] Approve in Steam mobile app (no code needed)                *", LogLevel.Info);
                        break;
                    case 5: // EmailConfirmation
                        hasEmailConfirmation = true;
                        _monitor.Log("*    [4] Click link in email (no code needed)                        *", LogLevel.Info);
                        break;
                }
            }

            _monitor.Log("*                                                                     *", LogLevel.Info);
            _monitor.Log("***********************************************************************", LogLevel.Info);

            // Prompt user to choose
            _monitor.Log("", LogLevel.Info);
            _monitor.Log("Choose your authentication method:", LogLevel.Info);
            if (hasEmailCode || hasDeviceCode)
            {
                _monitor.Log("  - Enter a code from email or mobile app", LogLevel.Info);
            }
            if (hasDeviceConfirmation || hasEmailConfirmation)
            {
                _monitor.Log("  - Or approve in your Steam mobile app (no code needed)", LogLevel.Info);
            }
            _monitor.Log("", LogLevel.Info);

            // Immediately start approval flow if that option is available
            // This allows mobile approval to work in parallel with code entry
            if (hasDeviceConfirmation || hasEmailConfirmation)
            {
                _api.SubmitCode("");

                _monitor.Log("Waiting for Steam Guard code OR mobile app approval...", LogLevel.Info);
                if (hasEmailCode || hasDeviceCode)
                {
                    _monitor.Log("Enter Steam Guard code (or just approve in mobile app):", LogLevel.Info);
                }
                else
                {
                    _monitor.Log("(Check your Steam mobile app notifications)", LogLevel.Info);
                }

                // Start reading user input in background (non-blocking)
                // This allows polling to continue and detect mobile approval
                if (hasEmailCode || hasDeviceCode)
                {
                    _userInputTask = Task.Run(() => Console.ReadLine());
                }

                _monitor.Log("Waiting for authentication...", LogLevel.Debug);
            }
            else
            {
                // Only code-based auth available, must wait for user input
                _monitor.Log("Enter Steam Guard code:", LogLevel.Info);
                var input = Console.ReadLine() ?? "";

                // Submit the code to steam-auth service
                _api.SubmitCode(input.Trim());

                _monitor.Log("Steam Guard code submitted ✓", LogLevel.Info);
                _monitor.Log("Waiting for authentication to complete...", LogLevel.Debug);
            }
        }

        /// <summary>
        /// Handle QR code ready state - inform user to scan QR code
        /// </summary>
        private void HandleQrCodeReady(LoginStatusResponse status)
        {
            // Only show this message once
            if (_hasShownQrCode)
            {
                return;
            }
            _hasShownQrCode = true;

            _monitor.Log("***********************************************************************", LogLevel.Info);
            _monitor.Log("*                                                                     *", LogLevel.Info);
            _monitor.Log("*    QR Code ready - scan with your Steam mobile app                  *", LogLevel.Info);
            _monitor.Log("*                                                                     *", LogLevel.Info);
            _monitor.Log("***********************************************************************", LogLevel.Info);
            _monitor.Log("", LogLevel.Info);

            // Display the QR code directly in the terminal
            if (!string.IsNullOrEmpty(status.qrCode))
            {
                // Split on newlines and filter out empty lines
                var lines = status.qrCode.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);

                foreach (var line in lines)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        _monitor.Log(line, LogLevel.Info);
                    }
                }
            }
            else
            {
                _monitor.Log("QR code not available. Check steam-auth logs: docker logs sdvd-steam-auth", LogLevel.Warn);
            }

            _monitor.Log("Scan the QR code above with your Steam mobile app...", LogLevel.Info);
            _monitor.Log("Waiting for you to scan and approve...", LogLevel.Info);
        }

        /// <summary>
        /// Fetches an encrypted app ticket from the steam-auth service.
        /// </summary>
        /// <returns>Encrypted Steam app ticket</returns>
        public SteamEncryptedAppTicket GetTicket()
        {
            try
            {
                _monitor.Log("Requesting Steam app ticket from steam-auth service...", LogLevel.Debug);

                var response = _api.GetAppTicket();

                if (string.IsNullOrEmpty(response?.app_ticket))
                {
                    throw new InvalidOperationException("Steam auth service returned empty app ticket");
                }

                _monitor.Log("Steam encrypted app ticket received from steam-auth service ✓", LogLevel.Info);

                // Convert steam-auth response to the expected format
                return new SteamEncryptedAppTicket
                {
                    Ticket = response.app_ticket,
                    Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Expiry = string.IsNullOrEmpty(response.expires_at)
                        ? DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds()
                        : DateTimeOffset.Parse(response.expires_at).ToUnixTimeSeconds()
                };
            }
            catch (Exception ex)
            {
                _monitor.Log($"Failed to get app ticket from steam-auth service: {ex.Message}", LogLevel.Error);
                throw;
            }
        }
    }
}
