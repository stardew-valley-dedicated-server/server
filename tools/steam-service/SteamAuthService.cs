using System.Buffers;
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
/// Main Steam service for authentication, ticket generation, and game downloads.
/// </summary>
public class SteamAuthService
{
    private readonly string _sessionDir;
    private readonly string _gameDir;

    private readonly SteamClient _steamClient;
    private readonly CallbackManager _callbackManager;
    private readonly SteamUser _steamUser;
    private readonly SteamApps _steamApps;
    private readonly SteamContent _steamContent;

    // For encrypted app ticket request/response
    private TaskCompletionSource<byte[]>? _encryptedTicketTcs;

    private readonly CancellationTokenSource _cts = new();
    private TaskCompletionSource<bool>? _loginTcs;
    private string? _refreshToken;
    private string? _username;

    public bool IsLoggedIn { get; private set; }
    public string? SteamId => _steamClient.SteamID?.ConvertToUInt64().ToString();

    public SteamAuthService(string sessionDir, string gameDir)
    {
        _sessionDir = sessionDir;
        _gameDir = gameDir;

        _steamClient = new SteamClient();
        _callbackManager = new CallbackManager(_steamClient);
        _steamUser = _steamClient.GetHandler<SteamUser>()!;
        _steamApps = _steamClient.GetHandler<SteamApps>()!;
        _steamContent = _steamClient.GetHandler<SteamContent>()!;

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
            Logger.Log($"[Steam] Encrypted app ticket failed: {result}");
            _encryptedTicketTcs?.TrySetException(new Exception($"Failed to get encrypted app ticket: {result}"));
            return;
        }

        if (ticket == null || ticket.Length == 0)
        {
            Logger.Log("[Steam] Encrypted app ticket is empty");
            _encryptedTicketTcs?.TrySetException(new Exception("Encrypted app ticket is empty"));
            return;
        }

        Logger.Log($"[Steam] Encrypted app ticket received: {ticket.Length} bytes for app {appId}");
        _encryptedTicketTcs?.TrySetResult(ticket);
    }

    private async Task ProcessCallbacks()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(50));
            await Task.Delay(10);
        }
    }

    public void Disconnect()
    {
        _cts.Cancel();
        if (IsLoggedIn) _steamUser.LogOff();
        _steamClient.Disconnect();
    }

    private void OnConnected(SteamClient.ConnectedCallback cb)
    {
        Logger.Log("[Steam] Connected to Steam");
    }

    private void OnDisconnected(SteamClient.DisconnectedCallback cb)
    {
        Logger.Log($"[Steam] Disconnected (UserInitiated: {cb.UserInitiated})");
        IsLoggedIn = false;
        _loginTcs?.TrySetResult(false);
    }

    private void OnLoggedOn(SteamUser.LoggedOnCallback cb)
    {
        if (cb.Result != EResult.OK)
        {
            Logger.Log($"[Steam] Login failed: {cb.Result} (Extended: {cb.ExtendedResult})");
            _loginTcs?.TrySetResult(false);
            return;
        }

        Logger.Log($"[Steam] Logged in as {_steamClient.SteamID}");
        IsLoggedIn = true;
        _loginTcs?.TrySetResult(true);
    }

    private void OnLoggedOff(SteamUser.LoggedOffCallback cb)
    {
        Logger.Log($"[Steam] Logged off: {cb.Result}");
        IsLoggedIn = false;
    }

    // ========================================================================
    // Session Management
    // ========================================================================

    public bool HasSavedSession()
    {
        var sessions = Directory.Exists(_sessionDir)
            ? Directory.GetFiles(_sessionDir, "session-*.json")
            : Array.Empty<string>();
        return sessions.Length > 0;
    }

    /// <summary>
    /// Get saved session for export (CI token export)
    /// </summary>
    public (string username, string refreshToken)? GetSavedSession() => LoadSession();

    private string? GetSavedSessionPath()
    {
        if (!Directory.Exists(_sessionDir)) return null;
        var sessions = Directory.GetFiles(_sessionDir, "session-*.json");
        return sessions.FirstOrDefault();
    }

    private void SaveSession(string username, string refreshToken)
    {
        Directory.CreateDirectory(_sessionDir);
        var path = Path.Combine(_sessionDir, $"session-{username}.json");
        var json = JsonSerializer.Serialize(new { username, refreshToken });
        File.WriteAllText(path, json);
        Logger.Log($"[Steam] Session saved for {username}");
    }

    private (string username, string refreshToken)? LoadSession()
    {
        var path = GetSavedSessionPath();
        if (path == null || !File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            var doc = JsonDocument.Parse(json);
            var username = doc.RootElement.GetProperty("username").GetString()!;
            var token = doc.RootElement.GetProperty("refreshToken").GetString()!;
            return (username, token);
        }
        catch
        {
            return null;
        }
    }

    // ========================================================================
    // Authentication
    // ========================================================================

    public async Task LoginWithSavedSessionAsync()
    {
        var session = LoadSession();
        if (session == null)
            throw new Exception("No saved session found");

        _username = session.Value.username;
        _refreshToken = session.Value.refreshToken;

        await ConnectAndLoginAsync(_refreshToken);
    }

    /// <summary>
    /// Login with a provided token (for CI/headless environments)
    /// </summary>
    public async Task LoginWithTokenAsync(string username, string refreshToken)
    {
        _username = username;
        _refreshToken = refreshToken;

        Logger.Log($"[Steam] Logging in with provided token for {username}...");
        await ConnectAndLoginAsync(refreshToken);
    }

    public async Task LoginInteractiveAsync()
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
                    await LoginWithSavedSessionAsync();
                    Logger.Log("[Steam] Logged in with saved session");
                    return;
                }
                catch
                {
                    Logger.Log("[Steam] Saved session invalid, need fresh login");
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
            await LoginWithQrCodeAsync();
        }
        else
        {
            await LoginWithCredentialsAsync();
        }
    }

    public async Task LoginWithCredentialsAsync(string? usernameParam = null, string? passwordParam = null)
    {
        string username;
        string password;

        // Use provided params, fall back to env vars, then prompt
        if (!string.IsNullOrEmpty(usernameParam) && !string.IsNullOrEmpty(passwordParam))
        {
            username = usernameParam;
            password = passwordParam;
        }
        else
        {
            var envUser = Environment.GetEnvironmentVariable("STEAM_USERNAME");
            var envPass = Environment.GetEnvironmentVariable("STEAM_PASSWORD");

            if (!string.IsNullOrEmpty(envUser) && !string.IsNullOrEmpty(envPass))
            {
                Logger.Log($"[Steam] Using credentials from environment (STEAM_USERNAME: {envUser})");
                username = envUser;
                password = envPass;
            }
            else
            {
                Console.Write("Steam Username: ");
                username = Console.ReadLine()?.Trim() ?? "";

                Console.Write("Steam Password: ");
                password = ReadPassword();
                Console.WriteLine();
            }
        }

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            Logger.Log("[Steam] Username and password required");
            return;
        }

        _username = username;

        // Connect
        Logger.Log("[Steam] Connecting...");
        _steamClient.Connect();
        await Task.Delay(2000);

        // Authenticate
        try
        {
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(
                new AuthSessionDetails
                {
                    Username = username,
                    Password = password,
                    IsPersistentSession = true,
                    Authenticator = new ConsoleAuthenticator()
                });

            var result = await authSession.PollingWaitForResultAsync();

            _refreshToken = result.RefreshToken;
            SaveSession(username, _refreshToken);

            // Now login with the token
            await LoginWithTokenInternalAsync(_refreshToken);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Steam] Authentication failed: {ex.Message}");
        }
    }

    private async Task LoginWithQrCodeAsync()
    {
        Logger.Log("[Steam] Connecting...");
        _steamClient.Connect();
        await Task.Delay(2000);

        try
        {
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaQRAsync(
                new AuthSessionDetails { IsPersistentSession = true });

            // Generate QR code
            var qrGenerator = new QRCodeGenerator();
            var qrData = qrGenerator.CreateQrCode(authSession.ChallengeURL, QRCodeGenerator.ECCLevel.L);
            var qrCode = new AsciiQRCode(qrData);

            Console.WriteLine();
            Console.WriteLine("Scan this QR code with the Steam Mobile App:");
            Console.WriteLine();
            Console.WriteLine(qrCode.GetGraphic(1));
            Console.WriteLine();
            Console.WriteLine($"Or open: {authSession.ChallengeURL}");
            Console.WriteLine();
            Console.WriteLine("Waiting for confirmation...");

            var result = await authSession.PollingWaitForResultAsync();

            _username = result.AccountName;
            _refreshToken = result.RefreshToken;
            SaveSession(_username, _refreshToken);

            Logger.Log($"[Steam] Authenticated as {_username}");

            await LoginWithTokenInternalAsync(_refreshToken);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Steam] QR authentication failed: {ex.Message}");
        }
    }

    private async Task ConnectAndLoginAsync(string refreshToken)
    {
        _steamClient.Connect();
        await Task.Delay(2000);
        await LoginWithTokenInternalAsync(refreshToken);
    }

    private async Task LoginWithTokenInternalAsync(string refreshToken)
    {
        const int maxRetries = 5;
        const int baseDelaySeconds = 5;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            _loginTcs = new TaskCompletionSource<bool>();

            if (attempt == 1)
            {
                Logger.Log($"[Steam] Logging in as {_username} with token ({refreshToken.Length} chars)...");
                PrintTokenExpiry(refreshToken);
            }
            else
            {
                Logger.Log($"[Steam] Login attempt {attempt}/{maxRetries}...");
            }

            _steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = _username,
                AccessToken = refreshToken,
                ShouldRememberPassword = true
            });

            var success = await _loginTcs.Task;
            if (success)
                return;

            if (attempt < maxRetries)
            {
                // Incrementing delay: 5s, 10s, 15s, 20s...
                var delaySeconds = baseDelaySeconds * attempt;
                Logger.Log($"[Steam] Login failed, retrying in {delaySeconds} seconds...");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));

                // Reconnect before retry (connection may have been dropped)
                if (!_steamClient.IsConnected)
                {
                    Logger.Log("[Steam] Reconnecting...");
                    _steamClient.Connect();
                    await Task.Delay(2000);
                }
            }
        }

        throw new Exception($"Login failed after {maxRetries} attempts");
    }

    private static void PrintTokenExpiry(string token)
    {
        try
        {
            // JWT format: header.payload.signature
            var parts = token.Split('.');
            if (parts.Length != 3) return;

            // Decode payload (base64url)
            var payload = parts[1];
            // Add padding if needed
            payload = payload.Replace('-', '+').Replace('_', '/');
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }

            var json = Encoding.UTF8.GetString(Convert.FromBase64String(payload));
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("exp", out var expProp))
            {
                var exp = expProp.GetInt64();
                var expiryDate = DateTimeOffset.FromUnixTimeSeconds(exp);
                var remaining = expiryDate - DateTimeOffset.UtcNow;

                Logger.Log($"[Steam] Token expires: {expiryDate:yyyy-MM-dd HH:mm:ss} UTC ({remaining.Days} days remaining)");
            }
        }
        catch
        {
            // Ignore parsing errors
        }
    }

    // ========================================================================
    // App Ticket (Encrypted App Ticket for Galaxy cross-platform auth)
    // ========================================================================

    public async Task<string> GetAppTicketAsync(uint appId)
    {
        if (!IsLoggedIn)
            throw new Exception("Not logged in");

        Logger.Log($"[Steam] Requesting encrypted app ticket for {appId}...");

        _encryptedTicketTcs = new TaskCompletionSource<byte[]>();

        // Send ClientRequestEncryptedAppTicket - this is what steam-user's getEncryptedAppTicket does
        var request = new ClientMsgProtobuf<CMsgClientRequestEncryptedAppTicket>(EMsg.ClientRequestEncryptedAppTicket);
        request.Body.app_id = appId;
        _steamClient.Send(request);

        // Wait for response with timeout
        var timeoutTask = Task.Delay(30000);
        var completedTask = await Task.WhenAny(_encryptedTicketTcs.Task, timeoutTask);

        if (completedTask == timeoutTask)
        {
            _encryptedTicketTcs = null;
            throw new TimeoutException("Timed out waiting for encrypted app ticket");
        }

        var ticketBytes = await _encryptedTicketTcs.Task;
        _encryptedTicketTcs = null;

        Logger.Log($"[Steam] Encrypted app ticket obtained ({ticketBytes.Length} bytes)");
        return Convert.ToBase64String(ticketBytes);
    }

    // ========================================================================
    // Game Download
    // ========================================================================

    private const string ManifestMarkerFileName = ".download-manifest";

    /// <summary>
    /// Checks if the game is already downloaded with the specified manifest.
    /// </summary>
    private bool IsGameAlreadyDownloaded(ulong manifestId)
    {
        var markerPath = Path.Combine(_gameDir, ManifestMarkerFileName);
        if (!File.Exists(markerPath))
            return false;

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
    private void SaveDownloadMarker(uint appId, uint depotId, ulong manifestId, string targetOs, long totalBytes, int totalFiles)
    {
        var markerPath = Path.Combine(_gameDir, ManifestMarkerFileName);
        var marker = new
        {
            appId,
            depotId,
            manifestId,
            targetOs,
            totalBytes,
            totalFiles,
            downloadedAt = DateTimeOffset.UtcNow.ToString("o")
        };
        File.WriteAllText(markerPath, JsonSerializer.Serialize(marker, new JsonSerializerOptions { WriteIndented = true }));
        Logger.Log($"[Steam] Download marker saved (manifest: {manifestId})");
    }

    // Patterns to skip during download (not needed for dedicated server)
    private static readonly Regex[] SkipPatterns =
    [
        new Regex(@"Content/XACT/Wave Bank.xwb", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"Content/XACT/Wave Bank\(1.4\).xwb", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"Content/Fonts/Chinese.*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"Content/Fonts/Korean.*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"Content/Fonts/Japanese.*", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\.(de-DE|es-ES|fr-FR|hu-HU|it-IT|ja-JP|ko-KR|pt-BR|ru-RU|tr-TR|zh-CN)\.xnb$", RegexOptions.IgnoreCase | RegexOptions.Compiled),
    ];

    private static bool ShouldSkipFile(string fileName)
    {
        foreach (var pattern in SkipPatterns)
        {
            if (pattern.IsMatch(fileName))
                return true;
        }
        return false;
    }

    public async Task DownloadGameAsync(uint appId, string targetOs = "linux")
    {
        if (!IsLoggedIn)
            throw new Exception("Not logged in");

        Logger.Reset(); // Reset timer for download tracking
        Logger.Log($"[Steam] Downloading game (App: {appId}, OS: {targetOs})...");
        Logger.Log($"[Steam] Target directory: {_gameDir}");

        Directory.CreateDirectory(_gameDir);

        try
        {
            // Check license ownership first for better error messages
            Logger.Log("[Steam] Checking game license...");
            var licenseList = await _steamApps.GetAppOwnershipTicket(appId);
            if (licenseList.Result == EResult.AccessDenied)
            {
                throw new Exception($"Account does not own App {appId}. Please purchase the game or check that you're using the correct Steam account.");
            }
            else if (licenseList.Result != EResult.OK)
            {
                Logger.Log($"[Steam] License check returned: {licenseList.Result} (continuing anyway)");
            }
            else
            {
                Logger.Log("[Steam] Game license verified");
            }

            // Get product info
            Logger.Log("[Steam] Getting product info...");

            var accessTokens = await _steamApps.PICSGetAccessTokens(appId, null);

            ulong accessToken = 0;
            if (accessTokens.AppTokens.TryGetValue(appId, out var token))
            {
                accessToken = token;
            }

            var productInfo = await _steamApps.PICSGetProductInfo(
                new SteamApps.PICSRequest(appId, accessToken),
                null);

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
                    continue;

                var oslist = depot["config"]["oslist"].Value;
                if (oslist != null && oslist.Equals(targetOs, StringComparison.OrdinalIgnoreCase))
                {
                    depotId = id;
                    Logger.Log($"[Steam] Found {targetOs} depot: {depotId}");
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
                Logger.Log("[Steam] Could not find manifest. Public manifest structure:");
                PrintKeyValue(publicManifest, "  ", 5);
                throw new Exception("Could not find public manifest gid");
            }

            var manifestId = ulong.Parse(manifestIdStr);
            Logger.Log($"[Steam] Manifest ID: {manifestId}");

            // Check if game is already downloaded with this manifest
            var forceRedownload = Environment.GetEnvironmentVariable("FORCE_REDOWNLOAD") == "1";
            if (!forceRedownload && IsGameAlreadyDownloaded(manifestId))
            {
                Logger.Log("[Steam] Game already downloaded with current manifest. Skipping download.");
                Logger.Log("[Steam] Set FORCE_REDOWNLOAD=1 to force re-download.");
                return;
            }

            if (forceRedownload)
                Logger.Log("[Steam] FORCE_REDOWNLOAD=1 set, forcing full download check");

            // Get depot key
            var depotKeyResult = await _steamApps.GetDepotDecryptionKey(depotId, appId);
            if (depotKeyResult.Result != EResult.OK)
            {
                throw new Exception($"Failed to get depot key: {depotKeyResult.Result}");
            }

            Logger.Log("[Steam] Got depot decryption key");

            // Get CDN servers
            var cdnServers = await _steamContent.GetServersForSteamPipe();
            if (cdnServers == null || !cdnServers.Any())
            {
                throw new Exception("No CDN servers available");
            }

            Logger.Log($"[Steam] Found {cdnServers.Count} CDN servers");

            // Try multiple CDN servers until one works (some may reject certain IP ranges)
            Server? server = null;
            SteamContent.CDNAuthToken? cdnAuthResult = null;
            var serversToTry = cdnServers.Take(5).ToList(); // Try up to 5 servers

            foreach (var candidateServer in serversToTry)
            {
                try
                {
                    Logger.Log($"[Steam] Trying CDN server: {candidateServer.Host}...");
                    var authResult = await _steamContent.GetCDNAuthToken(appId, depotId, candidateServer.Host!);
                    if (authResult.Result == EResult.OK)
                    {
                        server = candidateServer;
                        cdnAuthResult = authResult;
                        Logger.Log("[Steam] CDN auth token obtained");
                        break;
                    }
                    Logger.Log($"[Steam] CDN server {candidateServer.Host} returned: {authResult.Result}");
                }
                catch (Exception ex)
                {
                    Logger.Log($"[Steam] CDN server {candidateServer.Host} failed: {ex.Message}");
                }
            }

            // If CDN auth failed on all servers, try without auth token (works for some public depots)
            string? cdnAuthToken = null;
            if (server == null || cdnAuthResult == null)
            {
                Logger.Log("[Steam] CDN auth failed on all servers, attempting download without auth token...");
                server = serversToTry.First();
            }
            else
            {
                cdnAuthToken = cdnAuthResult.Token;
            }

            // Get manifest request code
            Logger.Log("[Steam] Getting manifest request code...");
            var manifestCode = await _steamContent.GetManifestRequestCode(depotId, appId, manifestId, "public");
            Logger.Log($"[Steam] Manifest request code: {manifestCode}");

            // Download using CDN client
            var cdnClient = new Client(_steamClient);

            Logger.Log("[Steam] Downloading manifest...");

            var manifest = await cdnClient.DownloadManifestAsync(
                depotId,
                manifestId,
                manifestCode,
                server,
                depotKeyResult.DepotKey,
                cdnAuthToken: cdnAuthToken);

            // Calculate totals and savings from filtering
            var skippedByFilter = manifest.Files!
                .Where(f => ShouldSkipFile(f.FileName))
                .ToList();

            var filesToDownload = manifest.Files!
                .Where(f => !ShouldSkipFile(f.FileName))
                .Where(f => !f.Flags.HasFlag(EDepotFileFlag.Directory))
                .ToList();

            var totalFiles = filesToDownload.Count;
            var totalBytes = filesToDownload.Sum(f => (long)f.TotalSize);
            var skippedBytes = skippedByFilter.Sum(f => (long)f.TotalSize);

            Logger.Log($"[Steam] Manifest contains {manifest.Files!.Count} files");
            Logger.Log($"[Steam] Skipping {skippedByFilter.Count} unnecessary files ({FormatSize(skippedBytes)} saved)");
            Logger.Log($"[Steam] Downloading {totalFiles} files ({FormatSize(totalBytes)})");

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

                var filePath = Path.Combine(_gameDir, file.FileName);

                // Create directory if needed
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                // Skip directories
                if (file.Flags.HasFlag(EDepotFileFlag.Directory))
                {
                    continue;
                }

                // Check if file already exists with correct size
                if (!forceRedownload && File.Exists(filePath))
                {
                    var existingSize = new FileInfo(filePath).Length;
                    if (existingSize == (long)file.TotalSize)
                    {
                        processedFiles++;
                        processedBytes += (long)file.TotalSize;
                        skippedExisting++;
                        continue;
                    }
                }

                // Download file chunks - must write at correct offsets
                await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);

                // Pre-allocate file to final size
                fs.SetLength((long)file.TotalSize);

                foreach (var chunk in file.Chunks)
                {
                    // Rent buffer for chunk data
                    var buffer = ArrayPool<byte>.Shared.Rent((int)chunk.UncompressedLength);
                    try
                    {
                        // Retry logic for transient network failures
                        int written = 0;
                        const int maxRetries = 3;
                        for (int retry = 0; retry < maxRetries; retry++)
                        {
                            try
                            {
                                written = await cdnClient.DownloadDepotChunkAsync(
                                    depotId,
                                    chunk,
                                    server,
                                    buffer,
                                    depotKeyResult.DepotKey,
                                    cdnAuthToken: cdnAuthToken);
                                break; // Success
                            }
                            catch (Exception ex) when (retry < maxRetries - 1)
                            {
                                Logger.Log($"[Steam] Chunk download failed (attempt {retry + 1}/{maxRetries}): {ex.Message}");
                                await Task.Delay(1000 * (retry + 1)); // Exponential backoff
                            }
                        }

                        // Validate chunk was fully downloaded
                        if (written != (int)chunk.UncompressedLength)
                        {
                            throw new Exception($"Chunk size mismatch for {file.FileName}: expected {chunk.UncompressedLength}, got {written}");
                        }

                        // Write chunk at its designated offset (chunks may be out of order)
                        fs.Seek((long)chunk.Offset, SeekOrigin.Begin);
                        await fs.WriteAsync(buffer, 0, written);
                        processedBytes += written;
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }

                // Flush to ensure all data is written
                await fs.FlushAsync();

                processedFiles++;

                // Progress update every 100 files
                if (processedFiles % 100 == 0 || processedFiles == totalFiles)
                {
                    var percent = (double)processedBytes / totalBytes * 100;
                    Logger.Log($"[Steam] Progress: {processedFiles}/{totalFiles} files - {FormatSize(processedBytes)}/{FormatSize(totalBytes)} ({percent:F1}%)");
                }
            }

            Logger.Log("[Steam] Download complete!");
            Logger.Log($"[Steam] Game installed to: {_gameDir}");
            Logger.Log($"[Steam] Total size: {FormatSize(processedBytes)}");
            if (skippedExisting > 0)
                Logger.Log($"[Steam] Skipped {skippedExisting} existing files (already up to date)");

            // Save download marker to skip re-download next time
            SaveDownloadMarker(appId, depotId, manifestId, targetOs, totalBytes, totalFiles);

            Logger.LogTotal();
        }
        catch (Exception ex)
        {
            Logger.Log($"[Steam] Download failed: {ex.Message}");
            throw;
        }
    }

    // ========================================================================
    // Helpers
    // ========================================================================

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
                break;
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
}
