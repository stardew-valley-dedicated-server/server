using JunimoServer.Tests.Clients;
using Xunit.Abstractions;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Configuration for connection retry behavior.
/// Timeouts are centralized in TestTimings.
/// </summary>
public class ConnectionOptions
{
    /// <summary>
    /// Maximum number of attempts to connect to the server.
    /// Default is 5 attempts (increased to compensate for reduced FarmhandMenuTimeout).
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>
    /// Timeout for waiting for the farmhand menu to appear.
    /// Can be overridden for slower connections.
    /// </summary>
    public TimeSpan? FarmhandMenuTimeout { get; set; }

    /// <summary>
    /// If true and server password is set, automatically authenticate after joining.
    /// Default is true.
    /// </summary>
    public bool AutoLogin { get; set; } = true;

    /// <summary>
    /// Server password for auto-login. Set by IntegrationTestBase from fixture.
    /// </summary>
    public string? ServerPassword { get; set; }

    /// <summary>
    /// Default options for standard connection scenarios.
    /// </summary>
    public static ConnectionOptions Default => new();

    /// <summary>
    /// Options with more retries for slow connections or CI environments.
    /// </summary>
    public static ConnectionOptions SlowConnection => new()
    {
        MaxAttempts = 5
    };
}

/// <summary>
/// Alias for backward compatibility.
/// </summary>
public class ConnectionRetryOptions : ConnectionOptions
{
    /// <summary>
    /// Default options for standard connection scenarios.
    /// </summary>
    public new static ConnectionRetryOptions Default => new();

    /// <summary>
    /// Options with more retries for slow connections or CI environments.
    /// </summary>
    public new static ConnectionRetryOptions SlowConnection => new()
    {
        MaxAttempts = 5
    };
}

/// <summary>
/// Result of a connection attempt.
/// </summary>
public class ConnectionResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int AttemptsUsed { get; set; }
    public FarmhandsResponse? Farmhands { get; set; }

    public static ConnectionResult Succeeded(int attempts, FarmhandsResponse? farmhands = null) =>
        new() { Success = true, AttemptsUsed = attempts, Farmhands = farmhands };

    public static ConnectionResult Failed(string error, int attempts) =>
        new() { Success = false, Error = error, AttemptsUsed = attempts };
}

/// <summary>
/// Result of joining the game world.
/// </summary>
public class JoinWorldResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public int AttemptsUsed { get; set; }
    public int? SlotIndex { get; set; }

    /// <summary>
    /// Indicates whether the player was automatically authenticated after joining.
    /// Only true if password protection was detected and auto-login succeeded.
    /// </summary>
    public bool IsAuthenticated { get; set; }

    /// <summary>
    /// Indicates whether the player started in the lobby cabin (password protection active).
    /// </summary>
    public bool WasInLobby { get; set; }

    public static JoinWorldResult Succeeded(int attempts, int slotIndex, bool isAuthenticated = false, bool wasInLobby = false) =>
        new() { Success = true, AttemptsUsed = attempts, SlotIndex = slotIndex, IsAuthenticated = isAuthenticated, WasInLobby = wasInLobby };

    public static JoinWorldResult Failed(string error, int attempts) =>
        new() { Success = false, Error = error, AttemptsUsed = attempts };
}

/// <summary>
/// Helper class for connecting to the server with automatic retry logic.
/// Handles the common scenario where connecting gets "stuck" and needs a retry.
/// </summary>
public class ConnectionHelper
{
    private readonly GameTestClient _gameClient;
    private readonly ServerApiClient? _serverApi;
    private readonly ITestOutputHelper? _output;
    private readonly ConnectionOptions _options;

    public ConnectionHelper(GameTestClient gameClient, ConnectionOptions? options = null, ITestOutputHelper? output = null, ServerApiClient? serverApi = null)
    {
        _gameClient = gameClient;
        _serverApi = serverApi;
        _options = options ?? ConnectionOptions.Default;
        _output = output;
    }

    private void Log(string message) => _output?.WriteLine($"[ConnectionHelper] {message}");

    /// <summary>
    /// Ensures the client is fully disconnected and at the title screen.
    /// </summary>
    public async Task<bool> EnsureDisconnectedAsync(TimeSpan? timeout = null)
    {
        await _gameClient.Navigate("title");
        var result = await _gameClient.Wait.ForDisconnected(timeout ?? TestTimings.DisconnectedTimeout);
        return result?.Success == true;
    }

    /// <summary>
    /// Connects to the server using an invite code with automatic retry on failure.
    /// Returns when the farmhand selection screen is displayed.
    /// </summary>
    /// <param name="inviteCode">The server invite code.</param>
    /// <param name="cancellationToken">Optional cancellation token for early abort.</param>
    /// <returns>Connection result with farmhand slots if successful.</returns>
    public async Task<ConnectionResult> ConnectToServerAsync(string inviteCode, CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? stepError = null;

            try
            {
                // Ensure we're disconnected before attempting
                if (attempt > 1)
                {
                    await EnsureDisconnectedAsync();
                    await Task.Delay(TestTimings.RetryPauseDelay, cancellationToken); // Brief pause between attempts

                    // Re-fetch invite code in case server regenerated it after connection loss
                    if (_serverApi != null)
                    {
                        var freshCode = await _serverApi.GetInviteCode();
                        if (!string.IsNullOrEmpty(freshCode?.InviteCode))
                            inviteCode = freshCode.InviteCode;
                    }
                }

                // Navigate to coop menu
                var navigateResult = await _gameClient.Navigate("coopmenu");
                if (navigateResult?.Success != true)
                {
                    stepError = $"Navigate to coop menu: {navigateResult?.Error ?? "failed"}";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Wait for CoopMenu to be ready
                var menuWait = await _gameClient.Wait.ForMenu("CoopMenu", TestTimings.MenuWaitTimeout);
                if (menuWait?.Success != true)
                {
                    stepError = $"Wait for CoopMenu: {menuWait?.Error ?? "timeout"}";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Switch to JOIN tab (tab 0)
                var tabResult = await _gameClient.Coop.Tab(0);
                if (tabResult?.Success != true)
                {
                    stepError = $"Switch to JOIN tab: {tabResult?.Error ?? "failed"}";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Open invite code input dialog
                var openResult = await _gameClient.Coop.OpenInviteCodeMenu();
                if (openResult?.Success != true)
                {
                    stepError = $"Open invite code menu: {openResult?.Error ?? "failed"}";

                    // Check for non-retryable errors that indicate Steam is not available
                    if (openResult?.Error != null && IsNonRetryableError(openResult.Error))
                    {
                        return ConnectionResult.Failed(
                            $"{stepError}. This usually means Steam is not running or not logged in.",
                            attempt);
                    }

                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Wait for text input menu
                var textInputWait = await _gameClient.Wait.ForTextInput(TestTimings.TextInputTimeout);
                if (textInputWait?.Success != true)
                {
                    stepError = $"Wait for text input: {textInputWait?.Error ?? "timeout"}";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Submit the invite code
                var submitResult = await _gameClient.Coop.SubmitInviteCode(inviteCode);
                if (submitResult?.Success != true)
                {
                    stepError = $"Submit invite code: {submitResult?.Error ?? "failed"}";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Wait for farmhand selection screen (this is where "stuck connecting" typically occurs)
                var farmhandWait = await _gameClient.Wait.ForFarmhands(TestTimings.FarmhandMenuTimeout);
                if (farmhandWait?.Success != true)
                {
                    stepError = $"Wait for farmhands: {farmhandWait?.Error ?? "timeout"}";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Poll for farmhand slot data to be loaded
                FarmhandsResponse? farmhands = null;
                var slotsReady = await PollingHelper.WaitUntilAsync(async () =>
                {
                    farmhands = await _gameClient.Farmhands.GetSlots();
                    return farmhands?.Success == true && farmhands.Slots.Count > 0;
                }, TestTimings.NetworkSyncTimeout, cancellationToken: cancellationToken);

                if (!slotsReady || farmhands == null)
                {
                    stepError = "Load farmhand slots: timeout";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                Log($"Connected ({farmhands.Slots.Count} slots, attempt {attempt}/{_options.MaxAttempts})");
                return ConnectionResult.Succeeded(attempt, farmhands);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"Attempt {attempt}: {ex.Message}");
            }
        }

        var errorSummary = errors.Count > 0
            ? string.Join("; ", errors)
            : "Connection timed out";
        return ConnectionResult.Failed($"Connection failed after {_options.MaxAttempts} attempts: {errorSummary}", _options.MaxAttempts);
    }

    /// <summary>
    /// Determines if an error should not be retried (e.g., Steam not available).
    /// </summary>
    private static bool IsNonRetryableError(string error)
    {
        // These errors indicate Steam/networking isn't available and won't be fixed by retrying
        return error.Contains("Invite codes not supported", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Invite code slot not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Connects to the server using LAN/IP address with automatic retry on failure.
    /// Returns when the farmhand selection screen is displayed.
    /// </summary>
    /// <param name="address">Server address (hostname or IP).</param>
    /// <param name="port">Server game port (default: 24642).</param>
    /// <param name="cancellationToken">Optional cancellation token for early abort.</param>
    /// <returns>Connection result with farmhand slots if successful.</returns>
    public async Task<ConnectionResult> ConnectViaLanAsync(
        string address,
        int port = 24642,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var fullAddress = port == 24642 ? address : $"{address}:{port}";

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? stepError = null;

            try
            {
                // Ensure we're disconnected before attempting
                if (attempt > 1)
                {
                    await EnsureDisconnectedAsync();
                    await Task.Delay(TestTimings.RetryPauseDelay, cancellationToken);
                }

                // Navigate to coop menu
                var navigateResult = await _gameClient.Navigate("coopmenu");
                if (navigateResult?.Success != true)
                {
                    stepError = $"Navigate to coop menu: {navigateResult?.Error ?? "failed"}";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Wait for CoopMenu to be ready
                var menuWait = await _gameClient.Wait.ForMenu("CoopMenu", TestTimings.MenuWaitTimeout);
                if (menuWait?.Success != true)
                {
                    stepError = $"Wait for CoopMenu: {menuWait?.Error ?? "timeout"}";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Switch to JOIN tab (tab 0)
                var tabResult = await _gameClient.Coop.Tab(0);
                if (tabResult?.Success != true)
                {
                    stepError = $"Switch to JOIN tab: {tabResult?.Error ?? "failed"}";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Join via LAN address
                var joinResult = await _gameClient.Coop.JoinLan(fullAddress);
                if (joinResult?.Success != true)
                {
                    stepError = $"Join via LAN ({fullAddress}): {joinResult?.Error ?? "failed"}";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Wait for farmhand selection screen
                var farmhandTimeout = _options.FarmhandMenuTimeout ?? TestTimings.FarmhandMenuTimeout;
                var farmhandWait = await _gameClient.Wait.ForFarmhands(farmhandTimeout);
                if (farmhandWait?.Success != true)
                {
                    stepError = $"Wait for farmhands: {farmhandWait?.Error ?? "timeout"}";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                // Poll for farmhand slot data to be loaded
                FarmhandsResponse? farmhands = null;
                var slotsReady = await PollingHelper.WaitUntilAsync(async () =>
                {
                    farmhands = await _gameClient.Farmhands.GetSlots();
                    return farmhands?.Success == true && farmhands.Slots.Count > 0;
                }, TestTimings.NetworkSyncTimeout, cancellationToken: cancellationToken);

                if (!slotsReady || farmhands == null)
                {
                    stepError = "Load farmhand slots: timeout";
                    errors.Add($"Attempt {attempt}: {stepError}");
                    continue;
                }

                Log($"Connected via LAN ({farmhands.Slots.Count} slots, attempt {attempt}/{_options.MaxAttempts})");
                return ConnectionResult.Succeeded(attempt, farmhands);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"Attempt {attempt}: {ex.Message}");
            }
        }

        var errorSummary = errors.Count > 0
            ? string.Join("; ", errors)
            : "Connection timed out";
        return ConnectionResult.Failed($"LAN connection failed after {_options.MaxAttempts} attempts: {errorSummary}", _options.MaxAttempts);
    }

    /// <summary>
    /// Connects to the server and joins the game world by selecting a farmhand slot.
    /// For uncustomized slots, also handles character creation.
    /// If password protection is detected, automatically authenticates unless skipAutoLogin is true.
    /// </summary>
    /// <param name="inviteCode">The server invite code.</param>
    /// <param name="farmerName">Name for new farmer (if selecting uncustomized slot).</param>
    /// <param name="favoriteThing">Favorite thing for new farmer (if selecting uncustomized slot).</param>
    /// <param name="preferExistingFarmer">If true, prefer selecting an existing customized farmer with matching name.</param>
    /// <param name="skipAutoLogin">If true, skip automatic login even if password protection is detected.</param>
    /// <param name="cancellationToken">Optional cancellation token for early abort.</param>
    /// <returns>Join result with slot index and authentication status if successful.</returns>
    public async Task<JoinWorldResult> JoinWorldAsync(
        string inviteCode,
        string farmerName,
        string favoriteThing = "Testing",
        bool preferExistingFarmer = true,
        bool skipAutoLogin = false,
        CancellationToken cancellationToken = default)
    {
        string? lastError = null;

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Connect to server
                var connectResult = await ConnectToServerAsync(inviteCode, cancellationToken);
                if (!connectResult.Success || connectResult.Farmhands == null)
                {
                    lastError = connectResult.Error;
                    continue;
                }

                var farmhands = connectResult.Farmhands;

                // Find appropriate slot
                FarmhandSlot? targetSlot = null;

                if (preferExistingFarmer)
                {
                    // First try to find existing farmer with matching name
                    targetSlot = farmhands.Slots.FirstOrDefault(s =>
                        s.IsCustomized && s.Name.Equals(farmerName, StringComparison.OrdinalIgnoreCase));
                }

                // If no existing farmer found, use uncustomized slot
                if (targetSlot == null)
                {
                    targetSlot = farmhands.Slots.FirstOrDefault(s => !s.IsCustomized);
                    if (targetSlot == null)
                        return JoinWorldResult.Failed("No available farmhand slots", attempt);
                }

                // Select the farmhand slot
                var selectResult = await _gameClient.Farmhands.Select(targetSlot.Index);
                if (selectResult?.Success != true)
                    continue;

                // If slot was uncustomized, handle character creation
                if (!targetSlot.IsCustomized)
                {
                    // Wait for character customization menu
                    var charWait = await _gameClient.Wait.ForCharacter(TestTimings.CharacterMenuTimeout);
                    if (charWait?.Success != true)
                        continue;

                    // Set character data
                    var customizeResult = await _gameClient.Character.Customize(farmerName, favoriteThing);
                    if (customizeResult?.Success != true)
                        continue;

                    // Brief delay for game to sync textbox values
                    await Task.Delay(TestTimings.CharacterCreationSyncDelay, cancellationToken);

                    // Confirm character creation
                    var confirmResult = await _gameClient.Character.Confirm();
                    if (confirmResult?.Success != true)
                        continue;
                }

                // Wait for world to be ready
                var worldWait = await _gameClient.Wait.ForWorldReady(TestTimings.WorldReadyTimeout);
                if (worldWait?.Success != true)
                    continue;

                // Check if auto-login is needed
                bool wasAuthenticated = false;
                bool wasInLobby = false;

                var shouldAutoLogin = !skipAutoLogin && _options.AutoLogin && !string.IsNullOrEmpty(_options.ServerPassword);
                if (shouldAutoLogin)
                {
                    // Poll for password protection welcome message
                    Log("Waiting for welcome message (polling)...");
                    var needsLogin = await PollingHelper.WaitUntilAsync(async () =>
                    {
                        var chat = await _gameClient.GetChatHistory(10);
                        return chat?.Messages?.Any(m =>
                            m.Message.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
                            m.Message.Contains("!login", StringComparison.OrdinalIgnoreCase)) == true;
                    }, TestTimings.WelcomeMessageTimeout, cancellationToken: cancellationToken);

                    if (needsLogin)
                    {
                        wasInLobby = true;

                        // Send login command
                        await _gameClient.SendChat($"!login {_options.ServerPassword}");

                        // Poll for authentication success message
                        Log("Waiting for auth response (polling)...");
                        wasAuthenticated = await PollingHelper.WaitUntilAsync(async () =>
                        {
                            var chat = await _gameClient.GetChatHistory(10);
                            return chat?.Messages?.Any(m =>
                                m.Message.Contains("authenticated", StringComparison.OrdinalIgnoreCase) ||
                                m.Message.Contains("welcome", StringComparison.OrdinalIgnoreCase) ||
                                m.Message.Contains("success", StringComparison.OrdinalIgnoreCase)) == true;
                        }, TestTimings.ChatCommandTimeout, cancellationToken: cancellationToken);
                    }
                }

                var authStatus = wasAuthenticated ? ", authenticated" : (wasInLobby ? ", in lobby" : "");
                Log($"Joined world as '{farmerName}'{authStatus} (attempt {attempt}/{_options.MaxAttempts})");
                return JoinWorldResult.Succeeded(attempt, targetSlot.Index, wasAuthenticated, wasInLobby);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                if (attempt == _options.MaxAttempts)
                    return JoinWorldResult.Failed($"Join failed after {_options.MaxAttempts} attempts: {lastError}", attempt);
            }

            // Return to title before retry
            await EnsureDisconnectedAsync();
            await Task.Delay(TestTimings.RetryPauseDelay, cancellationToken);
        }

        return JoinWorldResult.Failed($"Join failed after {_options.MaxAttempts} attempts: {lastError ?? "unknown error"}", _options.MaxAttempts);
    }
}
