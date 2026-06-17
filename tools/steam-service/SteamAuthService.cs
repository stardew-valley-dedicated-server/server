using System.Buffers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using QRCoder;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.CDN;
using SteamKit2.Internal;

namespace SteamService;

/// <summary>
/// Result of <see cref="SteamAuthService.GetAppTicketAsync"/>. Carries the base64-encoded
/// ticket plus instrumentation fields that flow through the HTTP layer to the client mod
/// so cross-log correlation (steam-auth ↔ test-client Galaxy events) can be done by sha8.
/// </summary>
public record AppTicketResult(
    string TicketBase64,
    string Source,
    string Sha8,
    int TicketLengthBytes,
    long AgeMs
);

/// <summary>
/// Steam service used for authentication, ticket generation, and game downloads.
/// Multiple instances can run concurrently for multi-account support,
/// each manages its own SteamClient, callbacks, and session.
/// </summary>
public class SteamAuthService
{
    // Timing constants
    private static readonly TimeSpan ConnectionEstablishmentDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TicketRequestTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan CallbackPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan TicketCacheMaxAge = TimeSpan.FromMinutes(10);

    private readonly string _sessionDir;
    private readonly string _gameDir;
    private readonly string _logPrefix;

    private readonly SteamClient _steamClient;
    private readonly CallbackManager _callbackManager;
    private readonly SteamUser _steamUser;
    private readonly SteamApps _steamApps;
    private readonly SteamContent _steamContent;
    private readonly SteamMatchmaking _matchmaking;

    // For encrypted app ticket request/response
    private TaskCompletionSource<byte[]>? _encryptedTicketTcs;

    // Prevents concurrent encrypted app ticket requests
    private readonly SemaphoreSlim _ticketSemaphore = new(1, 1);

    // Serializes login attempts on this account so concurrent callers cannot race on
    // _steamClient.Connect() or _loginTcs. Acquired at public entry points
    // (EnsureLoggedInAsync, LoginInteractiveAsync); methods called with the lock held
    // carry the *InsideLockAsync suffix.
    private readonly SemaphoreSlim _loginSemaphore = new(1, 1);

    /// <summary>Authentication inputs for EnsureLoggedInAsync. Pass null for saved-session-only.</summary>
    public record LoginConfig(string User, string? Pass, string? Token);

    // Lobby state (stored at creation time for use in subsequent SetLobbyData calls)
    private ulong _currentLobbyId;
    private ulong _currentGameServerSteamId;
    private string _currentProtocolVersion = "";
    private int _currentMaxMembers;

    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<bool>? _loginTcs;
    private string? _refreshToken;
    private string? _username;

    // Reconnect interlock. Set to 1 by either OnLoggedOff or OnDisconnected
    // before scheduling the reconnect task; the second callback in the same
    // incident sees 1 and is a no-op. Reset to 0 when the task exits.
    private int _reconnectInProgress;
    private const int MaxReconnectAttempts = 5;

    /// <summary>Account index (0-based). Used for ?account=N routing.</summary>
    public int AccountIndex { get; }

    /// <summary>Configured username for this account.</summary>
    public string Username { get; }

    public bool IsLoggedIn { get; private set; }
    public string? SteamId => _steamClient.SteamID?.ConvertToUInt64().ToString();
    public ulong CurrentLobbyId => _currentLobbyId;

    public SteamAuthService(
        int accountIndex,
        string username,
        string sessionDir,
        string gameDir,
        IReadOnlyCollection<string>? keepLanguages = null
    )
    {
        AccountIndex = accountIndex;
        Username = username;
        _sessionDir = Path.Combine(sessionDir, username);
        _gameDir = gameDir;
        _logPrefix = $"[SteamAuth:A{accountIndex}]";
        _skipPatterns = BuildSkipPatterns(keepLanguages ?? []);

        Directory.CreateDirectory(_sessionDir);
        MigrateOldSession(sessionDir);

        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>()!;
        _steamApps = _steamClient.GetHandler<SteamApps>()!;
        _steamContent = _steamClient.GetHandler<SteamContent>()!;
        _matchmaking = _steamClient.GetHandler<SteamMatchmaking>()!;

        _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
        _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
        _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
        _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

        // Add handler for encrypted app ticket response
        _steamClient.AddHandler(new EncryptedAppTicketHandler(this));

        // Start callback processing
        Task.Run(ProcessCallbacks);
    }

    // Called by EncryptedAppTicketHandler when response is received
    internal void HandleEncryptedAppTicketResponse(EResult result, uint appId, byte[]? ticket)
    {
        if (result != EResult.OK)
        {
            Logger.Log($"{_logPrefix} Encrypted app ticket failed: {result}");
            _encryptedTicketTcs?.TrySetException(
                new Exception($"Failed to get encrypted app ticket: {result}")
            );
            return;
        }

        if (ticket == null || ticket.Length == 0)
        {
            Logger.Log($"{_logPrefix} Encrypted app ticket is empty");
            _encryptedTicketTcs?.TrySetException(new Exception("Encrypted app ticket is empty"));
            return;
        }

        Logger.Log(
            $"{_logPrefix} Encrypted app ticket received: {ticket.Length} bytes for app {appId}"
        );
        _encryptedTicketTcs?.TrySetResult(ticket);
    }

    private async Task ProcessCallbacks()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
            await Task.Delay(CallbackPollInterval);
        }
    }

    public void Disconnect()
    {
        _cts.Cancel();
        if (IsLoggedIn)
        {
            _steamUser.LogOff();
        }

        _steamClient.Disconnect();
    }

    private void OnConnected(SteamClient.ConnectedCallback cb)
    {
        Logger.Log($"{_logPrefix} Connected to Steam");
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        Logger.Log($"{_logPrefix} Disconnected (UserInitiated: {cb.UserInitiated})");
        IsLoggedIn = false;
        _loginTcs?.TrySetResult(false);
        MaybeStartReconnect("disconnected", cb.UserInitiated, EResult.OK);
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        if (cb.Result != EResult.OK)
        {
            Logger.Log($"{_logPrefix} Login failed: {cb.Result} (Extended: {cb.ExtendedResult})");
            Logger.LogEvent(
                "account_login_failed",
                new
                {
                    prefix = _logPrefix,
                    result = cb.Result.ToString(),
                    extendedResult = cb.ExtendedResult.ToString(),
                }
            );
            _loginTcs?.TrySetResult(false);
            return;
        }

        Logger.Log($"{_logPrefix} Logged in as {_steamClient.SteamID}");
        Logger.LogEvent(
            "account_logged_in",
            new { prefix = _logPrefix, steamId = _steamClient.SteamID?.ToString() }
        );
        IsLoggedIn = true;
        _loginTcs?.TrySetResult(true);
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        Logger.Log($"{_logPrefix} Logged off: {cb.Result}");
        Logger.LogEvent(
            "account_logged_off",
            new { prefix = _logPrefix, result = cb.Result.ToString() }
        );
        IsLoggedIn = false;
        MaybeStartReconnect("logged_off", userInitiated: false, cb.Result);
    }

    private void MaybeStartReconnect(string trigger, bool userInitiated, EResult result)
    {
        if (userInitiated)
        {
            return; // intentional Disconnect()
        }

        if (_cts.IsCancellationRequested)
        {
            return; // shutdown in progress
        }

        if (_refreshToken == null || _username == null)
        {
            return; // nothing to retry with
        }

        if (IsTerminalLogoff(result))
        {
            return; // banned/disabled
        }

        if (Interlocked.CompareExchange(ref _reconnectInProgress, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() => RunReconnectLoopAsync(trigger, result));
    }

    private static bool IsTerminalLogoff(EResult r) =>
        r == EResult.Banned
        || r == EResult.AccountDisabled
        || r == EResult.Suspended
        || r == EResult.AccountLockedDown;

    private async Task RunReconnectLoopAsync(string trigger, EResult initialResult)
    {
        try
        {
            for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
            {
                var delay = TimeSpan.FromSeconds(10 * Math.Pow(2, attempt - 1));
                Logger.LogEvent(
                    "account_reconnect_attempt",
                    new
                    {
                        prefix = _logPrefix,
                        attempt,
                        maxAttempts = MaxReconnectAttempts,
                        trigger,
                        initialResult = initialResult.ToString(),
                        delaySec = delay.TotalSeconds,
                    }
                );
                try
                {
                    await Task.Delay(delay, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                await _loginSemaphore.WaitAsync(_cts.Token);
                try
                {
                    if (IsLoggedIn)
                    {
                        Logger.LogEvent(
                            "account_reconnect_succeeded",
                            new
                            {
                                prefix = _logPrefix,
                                attempt,
                                viaConcurrentCaller = true,
                            }
                        );
                        return;
                    }
                    if (_steamClient.IsConnected)
                    {
                        _steamClient.Disconnect();
                        await Task.Delay(100, _cts.Token);
                    }
                    await ConnectAndLoginAsync(_refreshToken!);
                    if (IsLoggedIn)
                    {
                        Logger.LogEvent(
                            "account_reconnect_succeeded",
                            new
                            {
                                prefix = _logPrefix,
                                attempt,
                                viaConcurrentCaller = false,
                            }
                        );
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogEvent(
                        "account_reconnect_attempt_failed",
                        new
                        {
                            prefix = _logPrefix,
                            attempt,
                            exceptionType = ex.GetType().Name,
                            message = ex.Message,
                        }
                    );
                }
                finally
                {
                    _loginSemaphore.Release();
                }
            }
            Logger.LogEvent(
                "account_reconnect_failed",
                new
                {
                    prefix = _logPrefix,
                    attempts = MaxReconnectAttempts,
                    initialResult = initialResult.ToString(),
                    trigger,
                    note = "LogonSessionReplaced means another SteamKit2 client logged in "
                        + "with the same Steam username while we were running. Check the host "
                        + "for any other sdvd/steam-service container holding this username "
                        + "and stop one of them; concurrent logins of the same account are not supported by Steam.",
                }
            );
        }
        finally
        {
            Interlocked.Exchange(ref _reconnectInProgress, 0);
        }
    }

    // ========================================================================
    // Session Management
    // ========================================================================

    private string SessionFilePath => Path.Combine(_sessionDir, "session.json");
    private string TicketCachePath => Path.Combine(_sessionDir, "app-ticket.json");

    public bool HasSavedSession()
    {
        return File.Exists(SessionFilePath);
    }

    /// <summary>
    /// Get saved session for export (CI token export)
    /// </summary>
    public (string username, string refreshToken)? GetSavedSession() => LoadSession();

    private void SaveSession(string username, string refreshToken)
    {
        Directory.CreateDirectory(_sessionDir);
        var path = SessionFilePath;
        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(new { username, refreshToken });

        // Write to temp file first, then atomically rename to avoid corruption
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, overwrite: true);
        Logger.Log($"{_logPrefix} Session saved for {username}");
    }

    private (string username, string refreshToken)? LoadSession()
    {
        var path = SessionFilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            var username = doc.RootElement.GetProperty("username").GetString()!;
            var token = doc.RootElement.GetProperty("refreshToken").GetString()!;
            return (username, token);
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException)
        {
            Logger.Log($"{_logPrefix} Failed to load session: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Migrates session files from the old flat format (session-{username}.json in the
    /// shared session dir) to the new per-account directory format ({sessionDir}/{username}/session.json).
    /// Added 2026-04 -- can be removed once all deployments have upgraded past this version.
    /// </summary>
    private void MigrateOldSession(string parentSessionDir)
    {
        if (File.Exists(SessionFilePath))
        {
            return;
        }

        var oldPath = Path.Combine(parentSessionDir, $"session-{Username}.json");
        if (!File.Exists(oldPath))
        {
            return;
        }

        try
        {
            File.Move(oldPath, SessionFilePath);
            Logger.Log($"{_logPrefix} Migrated session from old format");
        }
        catch (IOException ex)
        {
            Logger.Log($"{_logPrefix} Session migration failed: {ex.Message}");
        }
    }

    private void SaveTicketCache(string ticketBase64, string? steamId)
    {
        try
        {
            var json = JsonSerializer.Serialize(
                new
                {
                    ticket = ticketBase64,
                    steam_id = steamId,
                    fetched_at = DateTimeOffset.UtcNow.ToString("o"),
                }
            );
            File.WriteAllText(TicketCachePath, json);
        }
        catch
        { /* Non-critical */
        }
    }

    private (string ticket, DateTimeOffset fetchedAt)? LoadTicketCache()
    {
        try
        {
            if (!File.Exists(TicketCachePath))
            {
                return null;
            }

            var json = File.ReadAllText(TicketCachePath);
            var doc = JsonDocument.Parse(json);
            var fetchedAt = DateTimeOffset.Parse(
                doc.RootElement.GetProperty("fetched_at").GetString()!
            );

            if (DateTimeOffset.UtcNow - fetchedAt > TicketCacheMaxAge)
            {
                return null; // Expired
            }

            var ticket = doc.RootElement.GetProperty("ticket").GetString();
            if (ticket == null)
            {
                return null;
            }

            return (ticket, fetchedAt);
        }
        catch
        {
            return null;
        }
    }

    // ========================================================================
    // Authentication
    // ========================================================================

    /// <summary>
    /// Ensures the account is logged in. Safe to call concurrently from any thread:
    /// multiple callers will serialize via _loginSemaphore so exactly one login attempt
    /// runs at a time. Fast path (IsLoggedIn == true) does not acquire the semaphore.
    ///
    /// Auth priority matches the historical LoginAccountAsync helper: token → saved
    /// session → credentials. Throws InvalidOperationException if no auth method is
    /// available.
    /// </summary>
    public async Task EnsureLoggedInAsync(LoginConfig? config, CancellationToken ct = default)
    {
        if (IsLoggedIn)
        {
            return; // lock-free fast path
        }

        await _loginSemaphore.WaitAsync(ct);
        try
        {
            if (IsLoggedIn)
            {
                return; // double-check under lock
            }

            // Quiesce any half-open connection left by a failed prior attempt so the
            // login sequence starts from a known state.
            if (_steamClient.IsConnected && !IsLoggedIn)
            {
                _steamClient.Disconnect();
                await Task.Delay(100, ct); // let SteamKit callbacks settle
            }

            if (config?.Token != null)
            {
                await LoginWithTokenInsideLockAsync(config.User, config.Token);
            }
            else if (HasSavedSession())
            {
                await LoginWithSavedSessionInsideLockAsync();
            }
            else if (config?.Pass != null)
            {
                await LoginWithCredentialsInsideLockAsync(config.User, config.Pass);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Account {AccountIndex}: no auth method (no token, saved session, or password)"
                );
            }
        }
        finally
        {
            _loginSemaphore.Release();
        }
    }

    /// <summary>
    /// Restores a prior session from disk and logs in with its refresh token.
    /// Must be called with _loginSemaphore held.
    /// </summary>
    private async Task LoginWithSavedSessionInsideLockAsync()
    {
        var session = LoadSession();
        if (session == null)
        {
            throw new Exception("No saved session found");
        }

        _username = session.Value.username;
        _refreshToken = session.Value.refreshToken;

        await ConnectAndLoginAsync(_refreshToken);
    }

    /// <summary>
    /// Login with a provided token (for CI/headless environments).
    /// Must be called with _loginSemaphore held.
    /// </summary>
    private async Task LoginWithTokenInsideLockAsync(string username, string refreshToken)
    {
        _username = username;
        _refreshToken = refreshToken;

        Logger.Log($"{_logPrefix} Logging in with provided token for {username}...");
        await ConnectAndLoginAsync(refreshToken);
    }

    public async Task LoginInteractiveAsync()
    {
        await _loginSemaphore.WaitAsync();
        try
        {
            Console.WriteLine();
            Console.WriteLine("*****************************************************************");
            Console.WriteLine("*              Steam Authentication Setup                       *");
            Console.WriteLine("*****************************************************************");
            Console.WriteLine();

            // Check for existing session
            var existingSession = LoadSession();
            if (existingSession != null)
            {
                Console.WriteLine($"Found existing session for: {existingSession.Value.username}");
                Console.Write("Use existing session? [Y/n]: ");
                var useExisting = Console.ReadLine()?.Trim().ToLower();
                if (useExisting != "n" && useExisting != "no")
                {
                    try
                    {
                        await LoginWithSavedSessionInsideLockAsync();
                        Logger.Log($"{_logPrefix} Logged in with saved session");
                        return;
                    }
                    catch
                    {
                        Logger.Log($"{_logPrefix} Saved session invalid, need fresh login");
                    }
                }
            }

            // Choose auth method
            Console.WriteLine();
            Console.WriteLine("Choose authentication method:");
            Console.WriteLine("  [1] Username & Password");
            Console.WriteLine("  [2] QR Code (Steam Mobile App)");
            Console.Write("Choice [1]: ");

            var choice = Console.ReadLine()?.Trim();
            if (choice == "2")
            {
                await LoginWithQrCodeInsideLockAsync();
            }
            else
            {
                await LoginWithCredentialsInsideLockAsync();
            }
        }
        finally
        {
            _loginSemaphore.Release();
        }
    }

    /// <summary>
    /// Login with username/password. If usernameParam/passwordParam are null, prompts
    /// the user on stdin (used by interactive CLI). Must be called with _loginSemaphore held.
    /// </summary>
    private async Task LoginWithCredentialsInsideLockAsync(
        string? usernameParam = null,
        string? passwordParam = null
    )
    {
        string username;
        string password;

        // Use provided params or prompt
        if (!string.IsNullOrEmpty(usernameParam) && !string.IsNullOrEmpty(passwordParam))
        {
            username = usernameParam;
            password = passwordParam;
        }
        else
        {
            Console.Write("Steam Username: ");
            username = Console.ReadLine()?.Trim() ?? "";

            Console.Write("Steam Password: ");
            password = ReadPassword();
            Console.WriteLine();
        }

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            Logger.Log($"{_logPrefix} Username and password required");
            return;
        }

        _username = username;

        // Connect
        Logger.Log($"{_logPrefix} Connecting...");
        _steamClient.Connect();
        await Task.Delay(ConnectionEstablishmentDelay);

        // Authenticate
        try
        {
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = true,
                    Authenticator = new ConsoleAuthenticator(),
                }
            );

            var result = await authSession.PollingWaitForResultAsync();

            _refreshToken = result.RefreshToken;
            SaveSession(username, _refreshToken);

            // Now login with the token
            await LoginWithTokenInternalAsync(_refreshToken);
        }
        catch (Exception ex)
        {
            Logger.Log($"{_logPrefix} Authentication failed: {ex.Message}");
        }
    }

    /// <summary>
    /// QR-code login via Steam Mobile App. Must be called with _loginSemaphore held.
    /// </summary>
    private async Task LoginWithQrCodeInsideLockAsync()
    {
        Logger.Log($"{_logPrefix} Connecting...");
        _steamClient.Connect();
        await Task.Delay(ConnectionEstablishmentDelay);

        try
        {
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaQRAsync(
                new AuthSessionDetails { IsPersistentSession = true }
            );

            PrintQrCode(authSession.ChallengeURL);

            // Steam periodically refreshes the challenge URL. Reprint QR when it changes.
            authSession.ChallengeURLChanged = () =>
            {
                Logger.Log($"{_logPrefix} QR code refreshed by Steam");
                PrintQrCode(authSession.ChallengeURL);
            };

            Console.WriteLine("Waiting for confirmation...");

            var result = await authSession.PollingWaitForResultAsync();

            _username = result.AccountName;
            _refreshToken = result.RefreshToken;
            SaveSession(_username, _refreshToken);

            Logger.Log($"{_logPrefix} Authenticated as {_username}");

            await LoginWithTokenInternalAsync(_refreshToken);
        }
        catch (Exception ex)
        {
            Logger.Log($"{_logPrefix} QR authentication failed: {ex.Message}");
        }
    }

    private static void PrintQrCode(string challengeUrl)
    {
        var qrGenerator = new QRCodeGenerator();
        var qrData = qrGenerator.CreateQrCode(challengeUrl, QRCodeGenerator.ECCLevel.L);
        var qrCode = new AsciiQRCode(qrData);

        Console.WriteLine();
        Console.WriteLine("Scan this QR code with the Steam Mobile App:");
        Console.WriteLine();
        Console.WriteLine(qrCode.GetGraphic(1));
        Console.WriteLine();
        Console.WriteLine($"Or open: {challengeUrl}");
        Console.WriteLine();
    }

    private async Task ConnectAndLoginAsync(string refreshToken)
    {
        _steamClient.Connect();
        await Task.Delay(ConnectionEstablishmentDelay);
        await LoginWithTokenInternalAsync(refreshToken);
    }

    private async Task LoginWithTokenInternalAsync(string refreshToken)
    {
        const int maxRetries = 5;
        const int baseDelaySeconds = 5;
        var loginTimeout = TimeSpan.FromSeconds(30);

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            var tcs = new TaskCompletionSource<bool>();
            _loginTcs = tcs;

            if (attempt == 1)
            {
                Logger.Log(
                    $"{_logPrefix} Logging in as {_username} with token ({refreshToken.Length} chars)..."
                );
                PrintTokenExpiry(refreshToken);
            }
            else
            {
                Logger.Log($"{_logPrefix} Login attempt {attempt}/{maxRetries}...");
            }

            _steamUser.LogOn(
                new SteamUser.LogOnDetails
                {
                    Username = _username,
                    AccessToken = refreshToken,
                    ShouldRememberPassword = true,
                }
            );

            // Wait for login with timeout to avoid hanging forever
            var timeoutTask = Task.Delay(loginTimeout);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                Logger.Log(
                    $"{_logPrefix} Login attempt {attempt} timed out after {loginTimeout.TotalSeconds}s"
                );
            }
            else
            {
                var success = await tcs.Task;
                if (success)
                {
                    return;
                }
            }

            if (attempt < maxRetries)
            {
                // Incrementing delay: 5s, 10s, 15s, 20s...
                var delaySeconds = baseDelaySeconds * attempt;
                Logger.Log($"{_logPrefix} Login failed, retrying in {delaySeconds} seconds...");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

                // Reconnect before retry (connection may have been dropped)
                if (!_steamClient.IsConnected)
                {
                    Logger.Log($"{_logPrefix} Reconnecting...");
                    _steamClient.Connect();
                    await Task.Delay(ConnectionEstablishmentDelay);
                }
            }
        }

        throw new Exception($"Login failed after {maxRetries} attempts");
    }

    private void PrintTokenExpiry(string token)
    {
        try
        {
            // JWT format: header.payload.signature
            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return;
            }

            // Decode payload (base64url)
            var payload = parts[1];
            // Add padding if needed
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2:
                    payload += "==";
                    break;
                case 3:
                    payload += "=";
                    break;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("exp", out var expProp))
            {
                var exp = expProp.GetInt64();
                var expiryDate = DateTimeOffset.FromUnixTimeSeconds(exp);
                var remaining = expiryDate - DateTimeOffset.UtcNow;

                Logger.Log(
                    $"{_logPrefix} Token expires: {expiryDate:yyyy-MM-dd HH:mm:ss} UTC ({remaining.Days} days remaining)"
                );
            }
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException)
        {
            // Token expiry parsing is non-critical, just skip it
        }
    }

    // ========================================================================
    // App Ticket (Encrypted App Ticket for Galaxy cross-platform auth)
    // ========================================================================

    public async Task<AppTicketResult> GetAppTicketAsync(uint appId)
    {
        if (!IsLoggedIn)
        {
            throw new Exception("Not logged in");
        }

        // Serialize concurrent requests to same account
        await _ticketSemaphore.WaitAsync();
        try
        {
            // Check cache first
            var cached = LoadTicketCache();
            if (cached != null)
            {
                var cachedBytes = Convert.FromBase64String(cached.Value.ticket);
                var cachedSha8 = ComputeSha8(cachedBytes);
                var ageMs = (long)
                    (DateTimeOffset.UtcNow - cached.Value.fetchedAt).TotalMilliseconds;
                Logger.Log($"{_logPrefix} Using cached app ticket");
                Logger.LogEvent(
                    "app_ticket_served",
                    new
                    {
                        accountIndex = AccountIndex,
                        source = "cached",
                        ageMs,
                        sha8 = cachedSha8,
                        ticketLengthBytes = cachedBytes.Length,
                    }
                );
                return new AppTicketResult(
                    cached.Value.ticket,
                    "cached",
                    cachedSha8,
                    cachedBytes.Length,
                    ageMs
                );
            }

            Logger.Log($"{_logPrefix} Requesting encrypted app ticket for {appId}...");

            // Create TCS and capture in local variable to avoid race with callback
            var tcs = new TaskCompletionSource<byte[]>();
            _encryptedTicketTcs = tcs;

            // Send ClientRequestEncryptedAppTicket - this is what steam-user's getEncryptedAppTicket does
            var request = new ClientMsgProtobuf<CMsgClientRequestEncryptedAppTicket>(
                EMsg.ClientRequestEncryptedAppTicket
            );
            request.Body.app_id = appId;
            _steamClient.Send(request);

            // Wait for response with timeout. Both failure modes (timeout, EResult error
            // surfaced via TrySetException from HandleEncryptedAppTicketResponse) emit
            // app_ticket_failed on the awaiter — AsyncLocal flow is intact here, unlike
            // inside the SteamKit callback thread itself.
            byte[] ticketBytes;
            var timeoutTask = Task.Delay(TicketRequestTimeout);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            // Clear field before checking result (callback may have already fired)
            _encryptedTicketTcs = null;

            if (completedTask == timeoutTask)
            {
                Logger.LogEvent(
                    "app_ticket_failed",
                    new { accountIndex = AccountIndex, reason = "TIMEOUT" }
                );
                throw new TimeoutException("Timed out waiting for encrypted app ticket");
            }

            try
            {
                ticketBytes = await tcs.Task;
            }
            catch (Exception ex)
            {
                Logger.LogEvent(
                    "app_ticket_failed",
                    new { accountIndex = AccountIndex, reason = ex.Message }
                );
                throw;
            }

            var ticketBase64 = Convert.ToBase64String(ticketBytes);
            var sha8 = ComputeSha8(ticketBytes);

            Logger.Log($"{_logPrefix} Encrypted app ticket obtained ({ticketBytes.Length} bytes)");
            Logger.LogEvent(
                "app_ticket_served",
                new
                {
                    accountIndex = AccountIndex,
                    source = "fresh",
                    ageMs = 0L,
                    sha8,
                    ticketLengthBytes = ticketBytes.Length,
                }
            );

            // Cache for future requests
            SaveTicketCache(ticketBase64, SteamId);

            return new AppTicketResult(ticketBase64, "fresh", sha8, ticketBytes.Length, 0L);
        }
        finally
        {
            _ticketSemaphore.Release();
        }
    }

    /// <summary>
    /// First 8 hex chars of SHA-256(ticketBytes). A fingerprint, not credential material:
    /// 32 bits of a SHA-256 reveal nothing about the ticket. Used as the join key
    /// across steam-auth and test-client logs.
    /// </summary>
    private static string ComputeSha8(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    // ========================================================================
    // Game Download
    // ========================================================================

    /// <summary>
    /// Checks if the app is already downloaded with the specified manifest.
    /// </summary>
    private bool IsAlreadyDownloaded(string downloadDir, uint appId, ulong manifestId)
    {
        var markerPath = Path.Combine(downloadDir, $".download-manifest-{appId}");
        if (!File.Exists(markerPath))
        {
            return false;
        }

        try
        {
            var content = File.ReadAllText(markerPath);
            var doc = JsonDocument.Parse(content);

            if (doc.RootElement.TryGetProperty("manifestId", out var savedManifest))
            {
                var savedManifestId = savedManifest.GetUInt64();
                return savedManifestId == manifestId;
            }
        }
        catch
        {
            // Marker file corrupted or invalid, proceed with download
        }

        return false;
    }

    /// <summary>
    /// Saves a marker file indicating the download is complete with the given manifest.
    /// </summary>
    private void SaveDownloadMarker(
        string downloadDir,
        uint appId,
        uint depotId,
        ulong manifestId,
        string targetOs,
        long totalBytes,
        int totalFiles
    )
    {
        var markerPath = Path.Combine(downloadDir, $".download-manifest-{appId}");
        var marker = new
        {
            appId,
            depotId,
            manifestId,
            targetOs,
            totalBytes,
            totalFiles,
            downloadedAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        File.WriteAllText(
            markerPath,
            JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true })
        );
        Logger.Log($"{_logPrefix} Download marker saved (manifest: {manifestId})");
    }

    /// <summary>
    /// Rewrites Content/ContentHashes.json to list only files actually present on disk,
    /// dropping entries for assets the download filter stripped. Keeps each surviving
    /// entry's original hash (no recompute — we only prune). Keys are content-root-relative
    /// with forward slashes (e.g. "Fonts/SmallFont.pt-BR.xnb"); we join them against
    /// &lt;downloadDir&gt;/Content to check existence.
    /// <para>
    /// The download filter only skips <i>downloading</i> files; it never deletes files
    /// already on disk. So after a re-download (or a STEAM_KEEP_LANGUAGES change), a
    /// previously-downloaded localized file may still be present — this keeps its manifest
    /// entry, which is correct: manifest and filesystem stay in agreement, so the game's
    /// DoesAssetExist never lies and never crashes. The only effect is that stale files
    /// aren't reclaimed on reconfigure (a size concern, not a correctness one).
    /// </para>
    /// </summary>
    private void PruneContentManifest(string downloadDir)
    {
        var contentDir = Path.Combine(downloadDir, "Content");
        var manifestPath = Path.Combine(contentDir, "ContentHashes.json");
        if (!File.Exists(manifestPath))
        {
            Logger.Log(
                $"{_logPrefix} ContentHashes.json not found at {manifestPath}; skipping manifest prune"
            );
            return;
        }

        Dictionary<string, JsonElement>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                File.ReadAllText(manifestPath)
            );
        }
        catch (Exception ex)
        {
            Logger.Log(
                $"{_logPrefix} WARN: failed to parse ContentHashes.json ({ex.Message}); leaving it unchanged"
            );
            return;
        }
        if (entries == null || entries.Count == 0)
        {
            return;
        }

        var kept = new Dictionary<string, JsonElement>(entries.Count);
        foreach (var (key, value) in entries)
        {
            // Manifest keys use forward slashes regardless of OS; normalize for the local FS.
            var localPath = Path.Combine(contentDir, key.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(localPath))
            {
                kept[key] = value;
            }
        }

        var removed = entries.Count - kept.Count;
        if (removed == 0)
        {
            return;
        }

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(kept));
        Logger.Log(
            $"{_logPrefix} Pruned ContentHashes.json: removed {removed} stripped entries, {kept.Count} remain"
        );
    }

    /// <summary>
    /// Runs a Steam CDN/content request with bounded retry + linear backoff, mirroring the
    /// per-chunk retry in the download loop. These are single network calls (CDN server list,
    /// manifest request code, manifest download) where a transient 5xx (e.g. a 503 during a
    /// depot rollout) would otherwise abort the entire download/build. Retries on any
    /// exception — Steam surfaces transient failures as <see cref="SteamKit2.SteamKitWebRequestException"/>,
    /// and a genuinely permanent failure (bad auth, missing depot) still throws after the
    /// last attempt, preserving the original exception for the caller.
    /// </summary>
    private async Task<T> RetryTransientAsync<T>(string label, Func<Task<T>> operation)
    {
        const int maxAttempts = 4;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                Logger.Log(
                    $"{_logPrefix} {label} failed (attempt {attempt}/{maxAttempts}): {ex.Message}"
                );
                await Task.Delay(1000 * attempt); // Linear backoff: 1s, 2s, 3s
            }
        }
    }

    // Runs a CDN-host request across the available servers, failing over on error. A DNS/connect
    // failure (SocketException) is host-fatal: skip to the next server immediately with no delay.
    // A transient HTTP failure (e.g. 503 during a depot rollout) backs off, then retries the next
    // server. Throws an aggregated exception only when every server has been exhausted.
    private async Task<T> RetryAcrossServersAsync<T>(
        string label,
        IReadOnlyList<Server> servers,
        Func<Server, Task<T>> operation
    )
    {
        // Cap so a huge list can't spin forever; floor at 1 so the loop always runs.
        int maxAttempts = Math.Clamp(servers.Count, 1, 8);
        var failures = new List<Exception>();
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            var server = servers[attempt % servers.Count];
            // Log only retries (attempt 2+); logging every first attempt would flood the chunk loop.
            if (attempt > 0)
            {
                Logger.Log(
                    $"{_logPrefix} {label}: retrying on {server.Host} (attempt {attempt + 1}/{maxAttempts})"
                );
            }
            try
            {
                return await operation(server);
            }
            // No `when` guard: the last attempt's exception is collected too, so the post-loop
            // throw carries the real reason instead of propagating it raw.
            catch (Exception ex)
            {
                failures.Add(ex);
                bool hostDead = IsHostFatal(ex);
                Logger.Log(
                    $"{_logPrefix} {label} failed on {server.Host} "
                        + $"(attempt {attempt + 1}/{maxAttempts}{(hostDead ? ", host unreachable, skipping" : "")}): {ex.Message}"
                );
                if (!hostDead && attempt < maxAttempts - 1)
                {
                    await Task.Delay(1000 * (attempt + 1));
                }
            }
        }
        throw new AggregateException(
            $"{label} failed across all {servers.Count} CDN servers",
            failures
        );
    }

    // A DNS-resolution or TCP-connect failure means *this host* is unusable; SteamKit surfaces it
    // as HttpRequestException wrapping a SocketException. Walk the inner chain to detect it.
    private static bool IsHostFatal(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e is SocketException)
            {
                return true;
            }
        }
        return false;
    }

    // Audio is always stripped — the dedicated server runs silent (no Wave Bank).
    private static readonly Regex[] WaveBankPatterns =
    [
        new Regex(@"Content/XACT/Wave Bank.xwb", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(
            @"Content/XACT/Wave Bank\(1.4\).xwb",
            RegexOptions.IgnoreCase | RegexOptions.Compiled
        ),
    ];

    // Every localized content code the game ships (matches LocalizedContentManager
    // .LanguageCodeString in the decompiled game). A file named "*.{code}.xnb" is a
    // per-language variant; by default all are stripped to keep the image small.
    private static readonly string[] AllLanguageCodes =
    [
        "de-DE",
        "es-ES",
        "fr-FR",
        "hu-HU",
        "it-IT",
        "ja-JP",
        "ko-KR",
        "pt-BR",
        "ru-RU",
        "tr-TR",
        "zh-CN",
        "th-TH",
    ];

    // CJK fonts are large, multi-file families keyed by family name (not a "*.{code}.xnb"
    // suffix), so a kept CJK language must also un-strip its whole family directory.
    private static readonly Dictionary<string, string> CjkFontFamilyByCode = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["zh-CN"] = "Chinese",
        ["ja-JP"] = "Japanese",
        ["ko-KR"] = "Korean",
    };

    // Built per-instance in the constructor from the operator's STEAM_KEEP_LANGUAGES.
    private readonly Regex[] _skipPatterns;

    /// <summary>
    /// Parses a STEAM_KEEP_LANGUAGES value ("pt-BR, ru-RU") into validated language
    /// codes. Whitespace-tolerant and case-insensitive on each code; unknown codes are
    /// warned-and-dropped (never throws) so a typo can't abort the download. Returns the
    /// canonical-cased codes from <see cref="AllLanguageCodes"/>.
    /// </summary>
    public static IReadOnlyCollection<string> ParseKeepLanguages(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var canonical = AllLanguageCodes.ToDictionary(c => c, StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (
            var token in raw.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        )
        {
            if (canonical.TryGetValue(token, out var code))
            {
                if (!result.Contains(code))
                {
                    result.Add(code);
                }
            }
            else
            {
                Logger.Log(
                    $"[SteamService] WARN: STEAM_KEEP_LANGUAGES has unknown code '{token}' — ignoring. "
                        + $"Valid codes: {string.Join(", ", AllLanguageCodes)}"
                );
            }
        }
        return result;
    }

    /// <summary>
    /// Builds the download skip-set: always-stripped audio, plus the per-language
    /// "*.{code}.xnb" variants and CJK font families for every code NOT in <paramref name="keepLanguages"/>.
    /// A kept language's font/content files are left in the download (and survive into
    /// the regenerated manifest) so that language renders correctly on clients.
    /// </summary>
    private static Regex[] BuildSkipPatterns(IReadOnlyCollection<string> keepLanguages)
    {
        var keep = new HashSet<string>(keepLanguages, StringComparer.OrdinalIgnoreCase);
        var patterns = new List<Regex>(WaveBankPatterns);

        // Strip CJK font families only for non-kept CJK languages.
        foreach (var (code, family) in CjkFontFamilyByCode)
        {
            if (!keep.Contains(code))
            {
                patterns.Add(
                    new Regex(
                        $@"Content/Fonts/{family}.*",
                        RegexOptions.IgnoreCase | RegexOptions.Compiled
                    )
                );
            }
        }

        // Strip "*.{code}.xnb" localized variants for every non-kept code.
        var strippedCodes = AllLanguageCodes.Where(c => !keep.Contains(c)).ToArray();
        if (strippedCodes.Length > 0)
        {
            var alternation = string.Join("|", strippedCodes.Select(Regex.Escape));
            patterns.Add(
                new Regex(
                    $@"\.({alternation})\.xnb$",
                    RegexOptions.IgnoreCase | RegexOptions.Compiled
                )
            );
        }

        return patterns.ToArray();
    }

    private bool ShouldSkipFile(string fileName)
    {
        foreach (var pattern in _skipPatterns)
        {
            if (pattern.IsMatch(fileName))
            {
                return true;
            }
        }
        return false;
    }

    public async Task DownloadGameAsync(uint appId, string? targetDir = null)
    {
        // Reconnect if disconnected (can happen during long downloads). Passing null
        // config means saved-session-only; throws InvalidOperationException if no
        // saved session exists, matching the previous "Not logged in" error.
        await EnsureLoggedInAsync(null);

        const string targetOs = "linux";
        var downloadDir = targetDir ?? _gameDir;

        Logger.Reset(); // Reset timer for download tracking
        Logger.Log($"{_logPrefix} Downloading app {appId}...");
        Logger.Log($"{_logPrefix} Target directory: {downloadDir}");

        Directory.CreateDirectory(downloadDir);

        try
        {
            // Check license ownership first for better error messages
            Logger.Log($"{_logPrefix} Checking game license...");
            var licenseList = await _steamApps.GetAppOwnershipTicket(appId);
            if (licenseList.Result == EResult.AccessDenied)
            {
                throw new Exception(
                    $"Account does not own App {appId}. Please purchase the game or check that you're using the correct Steam account."
                );
            }
            else if (licenseList.Result != EResult.OK)
            {
                Logger.Log(
                    $"{_logPrefix} License check returned: {licenseList.Result} (continuing anyway)"
                );
            }
            else
            {
                Logger.Log($"{_logPrefix} Game license verified");
            }

            // Get product info
            Logger.Log($"{_logPrefix} Getting product info...");

            var accessTokens = await _steamApps.PICSGetAccessTokens(appId, null);

            ulong accessToken = 0;
            if (accessTokens.AppTokens.TryGetValue(appId, out var token))
            {
                accessToken = token;
            }

            var productInfo = await _steamApps.PICSGetProductInfo(
                new SteamApps.PICSRequest(appId, accessToken),
                null
            );

            if (productInfo.Results == null || !productInfo.Results.Any())
            {
                throw new Exception("Failed to get product info");
            }

            var appInfo = productInfo.Results.First().Apps[appId];
            var depots = appInfo.KeyValues["depots"];

            // Find depot for target OS
            uint depotId = 0;
            foreach (var depot in depots.Children)
            {
                if (!uint.TryParse(depot.Name, out var id))
                {
                    continue;
                }

                var oslist = depot["config"]["oslist"].Value;
                if (oslist != null && oslist.Equals(targetOs, StringComparison.OrdinalIgnoreCase))
                {
                    depotId = id;
                    Logger.Log($"{_logPrefix} Found {targetOs} depot: {depotId}");
                    break;
                }
            }

            if (depotId == 0)
            {
                throw new Exception($"Could not find depot for OS: {targetOs}");
            }

            var depotInfo = depots[depotId.ToString()];

            // Get manifest ID - structure is: manifests/public/gid
            var publicManifest = depotInfo["manifests"]["public"];
            string? manifestIdStr = null;

            // Try gid first (newer structure)
            if (publicManifest["gid"].Value != null)
            {
                manifestIdStr = publicManifest["gid"].Value;
            }
            // Fallback to direct value (older structure)
            else if (publicManifest.Value != null)
            {
                manifestIdStr = publicManifest.Value;
            }

            if (string.IsNullOrEmpty(manifestIdStr))
            {
                Logger.Log($"{_logPrefix} Could not find manifest. Public manifest structure:");
                PrintKeyValue(publicManifest, "  ", 5);
                throw new Exception("Could not find public manifest gid");
            }

            var manifestId = ulong.Parse(manifestIdStr);
            Logger.Log($"{_logPrefix} Manifest ID: {manifestId}");

            // Always validate files to detect corruption/deletion
            var forceRedownload = Environment.GetEnvironmentVariable("FORCE_REDOWNLOAD") == "1";
            if (forceRedownload)
            {
                Logger.Log($"{_logPrefix} FORCE_REDOWNLOAD=1 set, skipping all validation");
            }

            // Get depot key
            var depotKeyResult = await _steamApps.GetDepotDecryptionKey(depotId, appId);
            if (depotKeyResult.Result != EResult.OK)
            {
                throw new Exception($"Failed to get depot key: {depotKeyResult.Result}");
            }

            Logger.Log($"{_logPrefix} Got depot decryption key");

            // Get CDN servers. Steam's content API returns transient 5xx under load
            // (e.g. 503 during depot rollouts), so retry like the chunk loop below.
            var cdnServers = await RetryTransientAsync(
                "Get CDN servers",
                () => _steamContent.GetServersForSteamPipe()
            );
            if (cdnServers == null || !cdnServers.Any())
            {
                throw new Exception("No CDN servers available");
            }

            // DistinctBy(Host) drops duplicate hosts so rotation can't re-hit one dead server.
            var cdnServerList = cdnServers.DistinctBy(s => s.Host).ToList();
            Logger.Log($"{_logPrefix} Found {cdnServerList.Count} CDN servers");

            // Get manifest request code
            Logger.Log($"{_logPrefix} Getting manifest request code...");
            var manifestCode = await RetryTransientAsync(
                "Get manifest request code",
                () => _steamContent.GetManifestRequestCode(depotId, appId, manifestId, "public")
            );
            Logger.Log($"{_logPrefix} Manifest request code: {manifestCode}");

            // Download using CDN client
            var cdnClient = new Client(_steamClient);

            Logger.Log($"{_logPrefix} Downloading manifest from {cdnServerList[0].Host}...");

            // The manifest request code is not server-bound, so the download can fail over to
            // any server in the list.
            var manifest = await RetryAcrossServersAsync(
                "Download manifest",
                cdnServerList,
                server =>
                    cdnClient.DownloadManifestAsync(
                        depotId,
                        manifestId,
                        manifestCode,
                        server,
                        depotKeyResult.DepotKey
                    )
            );

            // Calculate totals and savings from filtering
            var skippedByFilter = manifest.Files!.Where(f => ShouldSkipFile(f.FileName)).ToList();

            var filesToDownload = manifest
                .Files!.Where(f => !ShouldSkipFile(f.FileName))
                .Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))
                .ToList();

            var totalFiles = filesToDownload.Count;
            var totalBytes = filesToDownload.Sum(f => (long)f.TotalSize);
            var skippedBytes = skippedByFilter.Sum(f => (long)f.TotalSize);

            Logger.Log($"{_logPrefix} Manifest contains {manifest.Files!.Count} files");
            Logger.Log(
                $"{_logPrefix} Skipping {skippedByFilter.Count} unnecessary files ({FormatSize(skippedBytes)} saved)"
            );
            Logger.Log($"{_logPrefix} Downloading {totalFiles} files ({FormatSize(totalBytes)})");

            var processedFiles = 0;
            var processedBytes = 0L;
            var skippedExisting = 0;

            foreach (var file in manifest.Files)
            {
                // Skip unnecessary files (Content folder, etc.)
                if (ShouldSkipFile(file.FileName))
                {
                    continue;
                }

                var filePath = Path.Combine(downloadDir, file.FileName);

                // Create directory if needed
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                // Skip directories
                if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                {
                    continue;
                }

                // Check if file already exists and validate its chunks
                List<DepotManifest.ChunkData> chunksToDownload = file.Chunks;

                if (!forceRedownload && File.Exists(filePath))
                {
                    var existingSize = new FileInfo(filePath).Length;
                    if (existingSize == (long)file.TotalSize)
                    {
                        // Size matches - validate chunk checksums
                        await using var existingFs = new FileStream(
                            filePath,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.Read
                        );
                        var invalidChunks = ValidateFileChunks(existingFs, file.Chunks);

                        if (invalidChunks.Count == 0)
                        {
                            // All chunks valid, skip this file
                            processedFiles++;
                            processedBytes += (long)file.TotalSize;
                            skippedExisting++;
                            continue;
                        }

                        // Some chunks invalid - only download those
                        chunksToDownload = invalidChunks;
                        Logger.Log(
                            $"{_logPrefix} {file.FileName}: {invalidChunks.Count}/{file.Chunks.Count} chunks need repair"
                        );
                    }
                }

                // Download file chunks - must write at correct offsets
                // Track bytes for this file separately to handle cleanup on failure
                var fileBytes = 0L;
                var downloadSuccess = false;

                try
                {
                    await using var fs = new FileStream(
                        filePath,
                        FileMode.OpenOrCreate,
                        FileAccess.ReadWrite,
                        FileShare.None
                    );

                    // Pre-allocate file to final size (no-op if file already exists with correct size)
                    fs.SetLength((long)file.TotalSize);

                    foreach (var chunk in chunksToDownload)
                    {
                        var buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);
                        try
                        {
                            // Reusing the rented buffer across servers is safe: a failed attempt
                            // throws before DownloadDepotChunkAsync writes it, so a retry on
                            // another server starts clean.
                            int written = await RetryAcrossServersAsync(
                                $"Chunk {file.FileName}",
                                cdnServerList,
                                server =>
                                    cdnClient.DownloadDepotChunkAsync(
                                        depotId,
                                        chunk,
                                        server,
                                        buffer,
                                        depotKeyResult.DepotKey
                                    )
                            );

                            // Validate chunk was fully downloaded
                            if (written != (int)chunk.UncompressedLength)
                            {
                                throw new Exception(
                                    $"Chunk size mismatch for {file.FileName}: expected {chunk.UncompressedLength}, got {written}"
                                );
                            }

                            // Write chunk at its designated offset (chunks may be out of order)
                            fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                            await fs.WriteAsync(buffer, 0, written);
                            fileBytes += written;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(buffer);
                        }
                    }

                    // Flush to ensure all data is written
                    await fs.FlushAsync();

                    // Verify downloaded chunks by re-reading and checking checksums
                    var failedChunks = ValidateFileChunks(fs, chunksToDownload);
                    if (failedChunks.Count > 0)
                    {
                        throw new Exception(
                            $"Post-download validation failed for {file.FileName}: {failedChunks.Count} chunks corrupted"
                        );
                    }

                    downloadSuccess = true;
                }
                finally
                {
                    if (!downloadSuccess)
                    {
                        // Delete corrupted/partial file so it will be re-downloaded next time
                        try
                        {
                            File.Delete(filePath);
                            Logger.Log($"{_logPrefix} Deleted corrupted file: {file.FileName}");
                        }
                        catch
                        {
                            // Ignore deletion errors
                        }
                    }
                }

                processedBytes += fileBytes;
                processedFiles++;

                // Progress update every 100 files
                if (processedFiles % 100 == 0 || processedFiles == totalFiles)
                {
                    var percent = (double)processedBytes / totalBytes * 100;
                    Logger.Log(
                        $"{_logPrefix} Progress: {processedFiles}/{totalFiles} files - {FormatSize(processedBytes)}/{FormatSize(totalBytes)} ({percent:F1}%)"
                    );
                }
            }

            Logger.Log($"{_logPrefix} Download complete!");
            Logger.Log($"{_logPrefix} App installed to: {downloadDir}");
            Logger.Log($"{_logPrefix} Total size: {FormatSize(processedBytes)}");
            if (skippedExisting > 0)
            {
                Logger.Log(
                    $"{_logPrefix} Skipped {skippedExisting} existing files (already up to date)"
                );
            }

            // Prune the content manifest to match what we actually downloaded. The game's
            // LocalizedContentManager.DoesAssetExist checks ContentHashes.json (a manifest),
            // not the filesystem — so a stripped-but-still-listed asset makes the game think
            // the file exists, then throw ContentLoadException on the missing on-disk read.
            // Removing the stale entries lets the game's built-in localized→English fallback
            // work instead of crashing.
            PruneContentManifest(downloadDir);

            // Save download marker to skip re-download next time
            SaveDownloadMarker(
                downloadDir,
                appId,
                depotId,
                manifestId,
                targetOs,
                totalBytes,
                totalFiles
            );

            Logger.LogTotal();
        }
        catch (Exception ex)
        {
            Logger.Log($"{_logPrefix} Download failed: {ex.Message}");
            throw;
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    /// <summary>
    /// Calculates Adler-32 checksum for a portion of a stream.
    /// This is the same algorithm Steam uses for chunk validation.
    /// </summary>
    private static uint AdlerHash(Stream stream, int length)
    {
        uint a = 0,
            b = 0;
        for (var i = 0; i < length; i++)
        {
            var c = (uint)stream.ReadByte();
            a = (a + c) % 65521;
            b = (b + a) % 65521;
        }
        return a | (b << 16);
    }

    /// <summary>
    /// Validates all chunks in a file against their expected checksums.
    /// Returns list of chunks that need to be (re)downloaded.
    /// </summary>
    private static List<DepotManifest.ChunkData> ValidateFileChunks(
        FileStream fs,
        IEnumerable<DepotManifest.ChunkData> chunks
    )
    {
        var invalidChunks = new List<DepotManifest.ChunkData>();

        foreach (var chunk in chunks.OrderBy(c => c.Offset))
        {
            fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
            var actualChecksum = AdlerHash(fs, (int)chunk.UncompressedLength);

            if (actualChecksum != chunk.Checksum)
            {
                invalidChunks.Add(chunk);
            }
        }

        return invalidChunks;
    }

    private static string FormatSize(long bytes)
    {
        string[] sizes = ["B", "KB", "MB", "GB", "TB"];
        var order = 0;
        var size = (double)bytes;

        while (size >= 1024 && order < sizes.Length - 1)
        {
            order++;
            size /= 1024;
        }

        return $"{size:F2} {sizes[order]}";
    }

    private static string ReadPassword()
    {
        var password = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
                Console.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Write("*");
            }
        }
        return password.ToString();
    }

    private static void PrintKeyValue(KeyValue kv, string indent = "", int maxDepth = 3)
    {
        if (maxDepth <= 0)
        {
            Console.WriteLine($"{indent}...");
            return;
        }

        if (kv.Children.Count == 0)
        {
            Console.WriteLine($"{indent}{kv.Name}: {kv.Value}");
        }
        else
        {
            Console.WriteLine($"{indent}{kv.Name}:");
            foreach (var child in kv.Children.Take(20)) // Limit children to avoid spam
            {
                PrintKeyValue(child, indent + "  ", maxDepth - 1);
            }
            if (kv.Children.Count > 20)
            {
                Console.WriteLine($"{indent}  ... and {kv.Children.Count - 20} more");
            }
        }
    }

    // ========================================================================
    // Lobby Management (uses same SteamClient session as auth)
    // ========================================================================

    /// <summary>
    /// Creates a Steam lobby with the given parameters.
    /// Uses the same authenticated session as other operations.
    /// </summary>
    public async Task<ulong> CreateLobbyAsync(
        uint appId,
        int maxMembers,
        ulong gameServerSteamId,
        string protocolVersion
    )
    {
        if (!IsLoggedIn)
        {
            throw new Exception("Not logged in");
        }

        Logger.Log(
            $"{_logPrefix} Creating lobby for app {appId}, max members: {maxMembers}, gameServer: {gameServerSteamId}, protocol: {protocolVersion}"
        );

        var metadata = BuildLobbyMetadata(gameServerSteamId, protocolVersion);

        // CreateLobby returns AsyncJob<CreateLobbyCallback>? (nullable); guard the job
        // before awaiting, since `await job` would NRE on null. (The SetLobbyData sites
        // instead await first and null-check the resulting callback — both guard null,
        // just at different points.)
        var createJob =
            _matchmaking.CreateLobby(
                appId: appId,
                lobbyType: ELobbyType.Public,
                maxMembers: maxMembers,
                lobbyFlags: 0,
                metadata: metadata
            ) ?? throw new Exception("CreateLobby returned null");

        var createResult = await createJob;

        if (createResult.Result != EResult.OK)
        {
            throw new Exception($"Failed to create lobby: {createResult.Result}");
        }

        // Store lobby state for use in SetLobbyData/SetLobbyPrivacy
        _currentLobbyId = createResult.LobbySteamID.ConvertToUInt64();
        _currentGameServerSteamId = gameServerSteamId;
        _currentProtocolVersion = protocolVersion;
        _currentMaxMembers = maxMembers;

        Logger.Log($"{_logPrefix} Lobby created: {_currentLobbyId}");

        return _currentLobbyId;
    }

    /// <summary>
    /// Sets metadata on the current lobby.
    /// Merges additional metadata with the base lobby metadata (protocolVersion, gameserver keys).
    /// </summary>
    public async Task SetLobbyDataAsync(
        uint appId,
        ulong lobbyId,
        Dictionary<string, string>? additionalMetadata = null
    )
    {
        if (!IsLoggedIn)
        {
            throw new Exception("Not logged in");
        }

        if (_currentLobbyId == 0)
        {
            throw new Exception("No lobby created yet");
        }

        var metadata = BuildLobbyMetadata(_currentGameServerSteamId, _currentProtocolVersion);

        // Merge additional metadata (e.g., farmName, serverMessage)
        if (additionalMetadata != null)
        {
            foreach (var kvp in additionalMetadata)
            {
                metadata[kvp.Key] = kvp.Value;
            }
        }

        Logger.Log($"{_logPrefix} Setting lobby {lobbyId} metadata with {metadata.Count} keys");

        var result = await _matchmaking!.SetLobbyData(
            appId: appId,
            lobbySteamId: lobbyId,
            lobbyType: ELobbyType.Public,
            maxMembers: _currentMaxMembers,
            lobbyFlags: 0,
            metadata: metadata
        );

        if (result == null)
        {
            throw new Exception("SetLobbyData returned null");
        }

        if (result.Result != EResult.OK)
        {
            throw new Exception($"Failed to set lobby data: {result.Result}");
        }

        Logger.Log($"{_logPrefix} Lobby metadata updated");
    }

    /// <summary>
    /// Sets the privacy level on a lobby.
    /// </summary>
    public async Task SetLobbyPrivacyAsync(uint appId, ulong lobbyId, ELobbyType lobbyType)
    {
        if (!IsLoggedIn)
        {
            throw new Exception("Not logged in");
        }

        if (_currentLobbyId == 0)
        {
            throw new Exception("No lobby created yet");
        }

        var metadata = BuildLobbyMetadata(_currentGameServerSteamId, _currentProtocolVersion);

        Logger.Log($"{_logPrefix} Setting lobby {lobbyId} privacy to {lobbyType}");

        var result = await _matchmaking!.SetLobbyData(
            appId: appId,
            lobbySteamId: lobbyId,
            lobbyType: lobbyType,
            maxMembers: _currentMaxMembers,
            lobbyFlags: 0,
            metadata: metadata
        );

        if (result == null)
        {
            throw new Exception("SetLobbyData returned null");
        }

        if (result.Result != EResult.OK)
        {
            throw new Exception($"Failed to set lobby privacy: {result.Result}");
        }

        Logger.Log($"{_logPrefix} Lobby privacy set to {lobbyType}");
    }

    /// <summary>
    /// Builds the standard metadata dictionary for Steam lobbies.
    /// Steam uses special __gameserver* keys to expose game server info to clients.
    /// </summary>
    private static Dictionary<string, string> BuildLobbyMetadata(
        ulong gameServerSteamId,
        string protocolVersion
    )
    {
        return new Dictionary<string, string>
        {
            ["protocolVersion"] = protocolVersion,
            // Steam's special keys for game server discovery
            // Setting IP/Port to 0 tells clients to use SteamID for SDR connection
            ["__gameserverIP"] = "0",
            ["__gameserverPort"] = "0",
            ["__gameserverSteamID"] = gameServerSteamId.ToString(),
        };
    }
}
