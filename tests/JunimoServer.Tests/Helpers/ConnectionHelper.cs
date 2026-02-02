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

    public static JoinWorldResult Succeeded(int attempts, int slotIndex) =>
        new() { Success = true, AttemptsUsed = attempts, SlotIndex = slotIndex };

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
    private readonly ITestOutputHelper? _output;
    private readonly ConnectionOptions _options;

    public ConnectionHelper(GameTestClient gameClient, ConnectionOptions? options = null, ITestOutputHelper? output = null)
    {
        _gameClient = gameClient;
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

            Log($"Connection attempt {attempt}/{_options.MaxAttempts}");

            try
            {
                // Ensure we're disconnected before attempting
                if (attempt > 1)
                {
                    Log("Returning to title for retry...");
                    await EnsureDisconnectedAsync();
                    await Task.Delay(TestTimings.RetryPauseDelay, cancellationToken); // Brief pause between attempts
                }

                // Navigate to coop menu
                var navigateResult = await _gameClient.Navigate("coopmenu");
                if (navigateResult?.Success != true)
                {
                    Log($"Navigate to coopmenu failed: {navigateResult?.Error}");
                    continue;
                }

                // Wait for CoopMenu to be ready
                var menuWait = await _gameClient.Wait.ForMenu("CoopMenu", TestTimings.MenuWaitTimeout);
                if (menuWait?.Success != true)
                {
                    Log($"Wait for CoopMenu failed: {menuWait?.Error}");
                    continue;
                }

                // Switch to JOIN tab (tab 0)
                var tabResult = await _gameClient.Coop.Tab(0);
                if (tabResult?.Success != true)
                {
                    Log($"Tab switch failed: {tabResult?.Error}");
                    continue;
                }

                // Open invite code input dialog
                var openResult = await _gameClient.Coop.OpenInviteCodeMenu();
                if (openResult?.Success != true)
                {
                    Log($"Open invite code menu failed: {openResult?.Error}");
                    continue;
                }

                // Wait for text input menu
                var textInputWait = await _gameClient.Wait.ForTextInput(TestTimings.TextInputTimeout);
                if (textInputWait?.Success != true)
                {
                    Log($"Wait for text input failed: {textInputWait?.Error}");
                    continue;
                }

                // Submit the invite code
                var submitResult = await _gameClient.Coop.SubmitInviteCode(inviteCode);
                if (submitResult?.Success != true)
                {
                    Log($"Submit invite code failed: {submitResult?.Error}");
                    continue;
                }

                Log("Invite code submitted, waiting for farmhand selection...");

                // Wait for farmhand selection screen (this is where "stuck connecting" typically occurs)
                var farmhandWait = await _gameClient.Wait.ForFarmhands(TestTimings.FarmhandMenuTimeout);
                if (farmhandWait?.Success != true)
                {
                    Log($"Wait for farmhands timed out after {TestTimings.FarmhandMenuTimeout.TotalMilliseconds}ms (attempt {attempt})");
                    continue;
                }

                Log($"Farmhand menu appeared after {farmhandWait.WaitedMs}ms");

                // Give the game time to load farmhand data
                await Task.Delay(TestTimings.NetworkSyncDelay, cancellationToken);

                // Get farmhand slots
                var farmhands = await _gameClient.Farmhands.GetSlots();
                if (farmhands?.Success != true)
                {
                    Log($"Get farmhands failed: {farmhands?.Error}");
                    continue;
                }

                Log($"Successfully connected on attempt {attempt}, found {farmhands.Slots.Count} farmhand slots");
                return ConnectionResult.Succeeded(attempt, farmhands);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = $"Attempt {attempt}: {ex.Message}";
                Log($"Exception during connection attempt {attempt}: {ex.Message}");
                errors.Add(error);
            }
        }

        var errorSummary = errors.Count > 0
            ? string.Join("; ", errors)
            : "Unknown failure";
        return ConnectionResult.Failed($"All {_options.MaxAttempts} connection attempts failed. Errors: {errorSummary}", _options.MaxAttempts);
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

            Log($"LAN connection attempt {attempt}/{_options.MaxAttempts} to {fullAddress}");

            try
            {
                // Ensure we're disconnected before attempting
                if (attempt > 1)
                {
                    Log("Returning to title for retry...");
                    await EnsureDisconnectedAsync();
                    await Task.Delay(TestTimings.RetryPauseDelay, cancellationToken);
                }

                // Navigate to coop menu
                var navigateResult = await _gameClient.Navigate("coopmenu");
                if (navigateResult?.Success != true)
                {
                    Log($"Navigate to coopmenu failed: {navigateResult?.Error}");
                    continue;
                }

                // Wait for CoopMenu to be ready
                var menuWait = await _gameClient.Wait.ForMenu("CoopMenu", TestTimings.MenuWaitTimeout);
                if (menuWait?.Success != true)
                {
                    Log($"Wait for CoopMenu failed: {menuWait?.Error}");
                    continue;
                }

                // Switch to JOIN tab (tab 0)
                var tabResult = await _gameClient.Coop.Tab(0);
                if (tabResult?.Success != true)
                {
                    Log($"Tab switch failed: {tabResult?.Error}");
                    continue;
                }

                // Join via LAN address
                var joinResult = await _gameClient.Coop.JoinLan(fullAddress);
                if (joinResult?.Success != true)
                {
                    Log($"Join LAN failed: {joinResult?.Error}");
                    continue;
                }

                Log($"LAN join initiated to {fullAddress}, waiting for farmhand selection...");

                // Wait for farmhand selection screen
                var farmhandTimeout = _options.FarmhandMenuTimeout ?? TestTimings.FarmhandMenuTimeout;
                var farmhandWait = await _gameClient.Wait.ForFarmhands(farmhandTimeout);
                if (farmhandWait?.Success != true)
                {
                    Log($"Wait for farmhands timed out after {farmhandTimeout.TotalMilliseconds}ms (attempt {attempt})");
                    continue;
                }

                Log($"Farmhand menu appeared after {farmhandWait.WaitedMs}ms");

                // Give the game time to load farmhand data
                await Task.Delay(TestTimings.NetworkSyncDelay, cancellationToken);

                // Get farmhand slots
                var farmhands = await _gameClient.Farmhands.GetSlots();
                if (farmhands?.Success != true)
                {
                    Log($"Get farmhands failed: {farmhands?.Error}");
                    continue;
                }

                Log($"Successfully connected via LAN on attempt {attempt}, found {farmhands.Slots.Count} farmhand slots");
                return ConnectionResult.Succeeded(attempt, farmhands);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = $"Attempt {attempt}: {ex.Message}";
                Log($"Exception during LAN connection attempt {attempt}: {ex.Message}");
                errors.Add(error);
            }
        }

        var errorSummary = errors.Count > 0
            ? string.Join("; ", errors)
            : "Unknown failure";
        return ConnectionResult.Failed($"All {_options.MaxAttempts} LAN connection attempts failed. Errors: {errorSummary}", _options.MaxAttempts);
    }

    /// <summary>
    /// Connects to the server and joins the game world by selecting a farmhand slot.
    /// For uncustomized slots, also handles character creation.
    /// </summary>
    /// <param name="inviteCode">The server invite code.</param>
    /// <param name="farmerName">Name for new farmer (if selecting uncustomized slot).</param>
    /// <param name="favoriteThing">Favorite thing for new farmer (if selecting uncustomized slot).</param>
    /// <param name="preferExistingFarmer">If true, prefer selecting an existing customized farmer with matching name.</param>
    /// <param name="cancellationToken">Optional cancellation token for early abort.</param>
    /// <returns>Join result with slot index if successful.</returns>
    public async Task<JoinWorldResult> JoinWorldAsync(
        string inviteCode,
        string farmerName,
        string favoriteThing = "Testing",
        bool preferExistingFarmer = true,
        CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Log($"Join world attempt {attempt}/{_options.MaxAttempts}");

            try
            {
                // Connect to server
                var connectResult = await ConnectToServerAsync(inviteCode, cancellationToken);
                if (!connectResult.Success || connectResult.Farmhands == null)
                {
                    Log($"Connection failed: {connectResult.Error}");
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

                    if (targetSlot != null)
                    {
                        Log($"Found existing farmer '{farmerName}' at slot {targetSlot.Index}");
                    }
                }

                // If no existing farmer found, use uncustomized slot
                if (targetSlot == null)
                {
                    targetSlot = farmhands.Slots.FirstOrDefault(s => !s.IsCustomized);
                    if (targetSlot == null)
                    {
                        return JoinWorldResult.Failed("No available farmhand slots (all customized)", attempt);
                    }
                    Log($"Using uncustomized slot at index {targetSlot.Index}");
                }

                // Select the farmhand slot
                var selectResult = await _gameClient.Farmhands.Select(targetSlot.Index);
                if (selectResult?.Success != true)
                {
                    Log($"Select farmhand failed: {selectResult?.Error}");
                    continue;
                }

                // If slot was uncustomized, handle character creation
                if (!targetSlot.IsCustomized)
                {
                    // Wait for character customization menu
                    var charWait = await _gameClient.Wait.ForCharacter(TestTimings.CharacterMenuTimeout);
                    if (charWait?.Success != true)
                    {
                        Log($"Wait for character menu failed: {charWait?.Error}");
                        continue;
                    }

                    // Set character data
                    var customizeResult = await _gameClient.Character.Customize(farmerName, favoriteThing);
                    if (customizeResult?.Success != true)
                    {
                        Log($"Customize failed: {customizeResult?.Error}");
                        continue;
                    }

                    // Brief delay for game to sync textbox values
                    await Task.Delay(TestTimings.CharacterCreationSyncDelay, cancellationToken);

                    // Confirm character creation
                    var confirmResult = await _gameClient.Character.Confirm();
                    if (confirmResult?.Success != true)
                    {
                        Log($"Confirm character failed: {confirmResult?.Error}");
                        continue;
                    }

                    Log($"Character '{farmerName}' created");
                }

                // Wait for world to be ready
                var worldWait = await _gameClient.Wait.ForWorldReady(TestTimings.WorldReadyTimeout);
                if (worldWait?.Success != true)
                {
                    Log($"Wait for world ready failed: {worldWait?.Error}");
                    continue;
                }

                Log($"Successfully joined world on attempt {attempt}, world ready after {worldWait.WaitedMs}ms");

                // Wait for network sync
                await Task.Delay(TestTimings.NetworkSyncDelay, cancellationToken);

                return JoinWorldResult.Succeeded(attempt, targetSlot.Index);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"Exception during join attempt {attempt}: {ex.Message}");
                if (attempt == _options.MaxAttempts)
                {
                    return JoinWorldResult.Failed($"All {_options.MaxAttempts} join attempts failed. Last error: {ex.Message}", attempt);
                }
            }

            // Return to title before retry
            await EnsureDisconnectedAsync();
            await Task.Delay(TestTimings.RetryPauseDelay, cancellationToken);
        }

        return JoinWorldResult.Failed($"All {_options.MaxAttempts} join attempts failed", _options.MaxAttempts);
    }
}
