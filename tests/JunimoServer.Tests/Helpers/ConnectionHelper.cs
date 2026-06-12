using System.Net.Http;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Schema.Events;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Configuration for connection retry behavior.
/// Timeouts are centralized in TestTimings.
/// </summary>
public class ConnectionOptions
{
    /// <summary>
    /// Maximum number of attempts to connect to the server.
    /// Default is 2 attempts to fail fast while still handling transient issues.
    /// </summary>
    public int MaxAttempts { get; set; } = 2;

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
    /// Server password for auto-login. Set by the test base from the lease.
    /// </summary>
    public string? ServerPassword { get; set; }

    /// <summary>
    /// Default options for standard connection scenarios.
    /// </summary>
    public static ConnectionOptions Default => new();
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
        new()
        {
            Success = true,
            AttemptsUsed = attempts,
            Farmhands = farmhands,
        };

    public static ConnectionResult Failed(string error, int attempts) =>
        new()
        {
            Success = false,
            Error = error,
            AttemptsUsed = attempts,
        };
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

    /// <summary>
    /// Indicates the server API was unreachable after auth failure, suggesting the
    /// server is unhealthy rather than auth timing being the issue.
    /// </summary>
    public bool ServerUnhealthy { get; set; }

    /// <summary>
    /// Server-assigned UniqueMultiplayerID captured from the client after world-ready.
    /// Populated on every successful join: the visibility gate fails the join outright
    /// if the client does not surface a UID.
    /// </summary>
    public long UniqueMultiplayerId { get; set; }

    public static JoinWorldResult Succeeded(
        int attempts,
        int slotIndex,
        long uniqueMultiplayerId,
        bool isAuthenticated = false,
        bool wasInLobby = false,
        bool serverUnhealthy = false
    ) =>
        new()
        {
            Success = true,
            AttemptsUsed = attempts,
            SlotIndex = slotIndex,
            UniqueMultiplayerId = uniqueMultiplayerId,
            IsAuthenticated = isAuthenticated,
            WasInLobby = wasInLobby,
            ServerUnhealthy = serverUnhealthy,
        };

    /// <summary>
    /// Diagnostic context attached by <see cref="Failed(string,int,IReadOnlyDictionary{string,object?}?)"/>
    /// on failure paths where <see cref="FailureContext"/> was consulted. Keys
    /// vary by failure site; common ones: <c>reason</c>, <c>serverState</c>,
    /// <c>diagnosticsError</c>. Null when no failure-context was collected.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Diagnostics { get; set; }

    public static JoinWorldResult Failed(string error, int attempts) =>
        new()
        {
            Success = false,
            Error = error,
            AttemptsUsed = attempts,
        };

    public static JoinWorldResult Failed(
        string error,
        int attempts,
        IReadOnlyDictionary<string, object?>? diagnostics
    ) =>
        new()
        {
            Success = false,
            Error = error,
            AttemptsUsed = attempts,
            Diagnostics = diagnostics,
        };
}

/// <summary>
/// Helper class for connecting to the server with automatic retry logic.
/// Handles the common scenario where connecting gets "stuck" and needs a retry.
/// </summary>
public class ConnectionHelper
{
    private readonly GameTestClient _gameClient;
    private readonly ServerApiClient? _serverApi;
    private readonly ConnectionOptions _options;

    /// <summary>
    /// Optional callback invoked at lifecycle checkpoints (after_connect, after_join, after_auth).
    /// Called with a label string suitable for use as a screenshot filename suffix.
    /// </summary>
    public Func<string, Task>? OnCheckpointScreenshot { get; set; }

    public ConnectionHelper(
        GameTestClient gameClient,
        ConnectionOptions? options = null,
        ServerApiClient? serverApi = null
    )
    {
        _gameClient = gameClient;
        _serverApi = serverApi;
        _options = options ?? ConnectionOptions.Default;
    }

    private static void Log(string message)
    {
        var displayName = TestIdentityContext.Current?.DisplayName;
        if (displayName != null)
        {
            SetupEventBus.EmitTestAnnotation(
                displayName,
                AnnotationLevel.Trace,
                AnnotationSource.Broker,
                $"[ConnectionHelper] {message}"
            );
        }
    }

    private static void EmitDiagnostic(string eventName, object? data) =>
        InfrastructureEventLog.Emit(eventName, data);

    private async Task CaptureCheckpointIfEnabled(string label)
    {
        if (OnCheckpointScreenshot == null)
        {
            return;
        }

        try
        {
            await OnCheckpointScreenshot(label);
        }
        catch (Exception ex)
        {
            Log($"[Checkpoint] Failed to capture screenshot '{label}': {ex.Message}");
        }
    }

    /// <summary>
    /// Ensures the client is fully disconnected and at the title screen.
    /// </summary>
    public async Task<bool> EnsureDisconnectedAsync(TimeSpan? timeout = null)
    {
        await _gameClient.Navigate("title");
        var result = await _gameClient.Wait.ForDisconnected(
            timeout ?? TestTimings.DisconnectedTimeout
        );
        return result?.Success == true;
    }

    /// <summary>
    /// Connects to the server using an invite code with automatic retry on failure.
    /// Returns when the farmhand selection screen is displayed.
    /// </summary>
    /// <param name="inviteCode">The server invite code.</param>
    /// <param name="cancellationToken">Optional cancellation token for early abort.</param>
    /// <returns>Connection result with farmhand slots if successful.</returns>
    public async Task<ConnectionResult> ConnectToServerAsync(
        string inviteCode,
        CancellationToken cancellationToken = default
    )
    {
        var errors = new List<string>();

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                Log("[Connect] Returning to title for retry...");
                await EnsureDisconnectedAsync();
                await Task.Delay(TestTimings.RetryPauseDelay, cancellationToken);

                // Re-fetch invite code in case server regenerated it after connection loss
                if (_serverApi != null)
                {
                    var freshCode = await _serverApi.GetInviteCode();
                    if (!string.IsNullOrEmpty(freshCode?.InviteCode))
                    {
                        inviteCode = freshCode.InviteCode;
                    }
                }
            }

            var result = await ConnectToServerOnceAsync(
                inviteCode,
                attempt,
                _options.MaxAttempts,
                cancellationToken
            );
            if (result.Success)
            {
                return result;
            }

            errors.Add($"Attempt {attempt}: {result.Error}");
        }

        return ConnectionResult.Failed(
            $"Connection failed after {_options.MaxAttempts} attempts: {string.Join("; ", errors)}",
            _options.MaxAttempts
        );
    }

    /// <summary>
    /// Single attempt to connect via invite code. No retry logic; callers handle retries.
    /// </summary>
    private async Task<ConnectionResult> ConnectToServerOnceAsync(
        string inviteCode,
        int attempt,
        int maxAttempts,
        CancellationToken cancellationToken
    )
    {
        Log($"[Connect] Attempt {attempt}/{maxAttempts} - connecting via invite code...");

        try
        {
            var navigateResult = await _gameClient.Navigate("coopmenu");
            if (navigateResult?.Success != true)
            {
                return ConnectionResult.Failed(
                    $"Navigate to coop menu: {navigateResult?.Error ?? "failed"}",
                    attempt
                );
            }

            var menuWait = await _gameClient.Wait.ForMenu("CoopMenu", TestTimings.MenuWaitTimeout);
            if (menuWait?.Success != true)
            {
                return ConnectionResult.Failed(
                    $"Wait for CoopMenu: {menuWait?.Error ?? "timeout"}",
                    attempt
                );
            }

            var tabResult = await _gameClient.Coop.Tab(0);
            if (tabResult?.Success != true)
            {
                return ConnectionResult.Failed(
                    $"Switch to JOIN tab: {tabResult?.Error ?? "failed"}",
                    attempt
                );
            }

            var openResult = await _gameClient.Coop.OpenInviteCodeMenu();
            if (openResult?.Success != true)
            {
                var error = $"Open invite code menu: {openResult?.Error ?? "failed"}";
                if (openResult?.Error != null && IsNonRetryableError(openResult.Error))
                {
                    return ConnectionResult.Failed(
                        $"{error}. This usually means Steam is not running or not logged in.",
                        attempt
                    );
                }

                return ConnectionResult.Failed(error, attempt);
            }

            var textInputWait = await _gameClient.Wait.ForTextInput(TestTimings.TextInputTimeout);
            if (textInputWait?.Success != true)
            {
                return ConnectionResult.Failed(
                    $"Wait for text input: {textInputWait?.Error ?? "timeout"}",
                    attempt
                );
            }

            var submitResult = await _gameClient.Coop.SubmitInviteCode(inviteCode);
            if (submitResult?.Success != true)
            {
                return ConnectionResult.Failed(
                    $"Submit invite code: {submitResult?.Error ?? "failed"}",
                    attempt
                );
            }

            var farmhandTimeout = _options.FarmhandMenuTimeout ?? TestTimings.FarmhandMenuTimeout;
            var farmhandWait = await _gameClient.Wait.ForFarmhands(farmhandTimeout);
            if (farmhandWait?.Success != true)
            {
                return ConnectionResult.Failed(
                    $"Wait for farmhands: {farmhandWait?.Error ?? "timeout"}",
                    attempt
                );
            }

            FarmhandsResponse? farmhands = null;
            var slotsReady = await PollingHelper.WaitUntilAsync(
                WaitName.Polling_ConnectionHelper_LoadFarmhandSlotsCoop,
                async () =>
                {
                    farmhands = await _gameClient.Farmhands.GetSlots();
                    return farmhands?.Success == true && farmhands.Farmhands.Count > 0;
                },
                TestTimings.NetworkSyncTimeout,
                cancellationToken: cancellationToken
            );

            if (!slotsReady || farmhands == null)
            {
                return ConnectionResult.Failed("Load farmhand slots: timeout", attempt);
            }

            Log($"Connected ({farmhands.Farmhands.Count} slots, attempt {attempt}/{maxAttempts})");
            await CaptureCheckpointIfEnabled("after_connect");
            return ConnectionResult.Succeeded(attempt, farmhands);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ConnectionResult.Failed($"[{ex.GetType().Name}] {ex.Message}", attempt);
        }
    }

    /// <summary>
    /// Determines if an error should not be retried (e.g., Steam not available).
    /// </summary>
    private static bool IsNonRetryableError(string error)
    {
        return error.Contains("Invite codes not supported", StringComparison.OrdinalIgnoreCase)
            || error.Contains("Invite code slot not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Connects to the server using LAN/IP address with automatic retry on failure.
    /// Returns when the farmhand selection screen is displayed.
    /// </summary>
    public async Task<ConnectionResult> ConnectViaLanAsync(
        string address,
        int port = 24642,
        CancellationToken cancellationToken = default
    )
    {
        var errors = new List<string>();

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempt > 1)
            {
                Log("[Connect] Returning to title for retry...");
                await EnsureDisconnectedAsync();
                await Task.Delay(TestTimings.RetryPauseDelay, cancellationToken);
            }

            var result = await ConnectViaLanOnceAsync(
                address,
                port,
                attempt,
                _options.MaxAttempts,
                cancellationToken
            );
            if (result.Success)
            {
                return result;
            }

            errors.Add($"Attempt {attempt}: {result.Error}");
        }

        return ConnectionResult.Failed(
            $"LAN connection failed after {_options.MaxAttempts} attempts: {string.Join("; ", errors)}",
            _options.MaxAttempts
        );
    }

    /// <summary>
    /// Single attempt to connect via LAN. No retry logic; callers handle retries.
    /// </summary>
    private async Task<ConnectionResult> ConnectViaLanOnceAsync(
        string address,
        int port,
        int attempt,
        int maxAttempts,
        CancellationToken cancellationToken
    )
    {
        var fullAddress = port == 24642 ? address : $"{address}:{port}";
        Log($"[Connect] Attempt {attempt}/{maxAttempts} - connecting via LAN to {fullAddress}...");

        try
        {
            var joinResult = await _gameClient.Coop.ConnectLanDirect(fullAddress);
            if (joinResult?.Success != true)
            {
                return ConnectionResult.Failed(
                    $"Direct LAN connect ({fullAddress}): {joinResult?.Error ?? "failed"}",
                    attempt
                );
            }

            var farmhandTimeout = _options.FarmhandMenuTimeout ?? TestTimings.FarmhandMenuTimeout;
            var farmhandWait = await _gameClient.Wait.ForFarmhands(farmhandTimeout);
            if (farmhandWait?.Success != true)
            {
                return ConnectionResult.Failed(
                    $"Wait for farmhands: {farmhandWait?.Error ?? "timeout"}",
                    attempt
                );
            }

            FarmhandsResponse? farmhands = null;
            var slotsReady = await PollingHelper.WaitUntilAsync(
                WaitName.Polling_ConnectionHelper_LoadFarmhandSlotsLan,
                async () =>
                {
                    farmhands = await _gameClient.Farmhands.GetSlots();
                    return farmhands?.Success == true && farmhands.Farmhands.Count > 0;
                },
                TestTimings.NetworkSyncTimeout,
                cancellationToken: cancellationToken
            );

            if (!slotsReady || farmhands == null)
            {
                return ConnectionResult.Failed("Load farmhand slots: timeout", attempt);
            }

            Log(
                $"Connected via LAN ({farmhands.Farmhands.Count} slots, attempt {attempt}/{maxAttempts})"
            );
            await CaptureCheckpointIfEnabled("after_connect");
            return ConnectionResult.Succeeded(attempt, farmhands);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ConnectionResult.Failed($"[{ex.GetType().Name}] {ex.Message}", attempt);
        }
    }

    /// <summary>
    /// Connects to the server via invite code and joins the game world by selecting a farmhand slot.
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
    public Task<JoinWorldResult> JoinWorldAsync(
        string inviteCode,
        string farmerName,
        string favoriteThing = "Testing",
        bool preferExistingFarmer = true,
        bool skipAutoLogin = false,
        CancellationToken cancellationToken = default
    )
    {
        // Capture invite code in a mutable variable so the onRetry callback can
        // refresh it (server may regenerate codes after a connection drop).
        var currentInviteCode = inviteCode;
        return JoinWorldCoreAsync(
            (attempt, maxAttempts, ct) =>
                ConnectToServerOnceAsync(currentInviteCode, attempt, maxAttempts, ct),
            farmerName,
            favoriteThing,
            preferExistingFarmer,
            skipAutoLogin,
            onRetry: async () =>
            {
                if (_serverApi != null)
                {
                    var freshCode = await _serverApi.GetInviteCode();
                    if (!string.IsNullOrEmpty(freshCode?.InviteCode))
                    {
                        currentInviteCode = freshCode.InviteCode;
                    }
                }
            },
            cancellationToken
        );
    }

    /// <summary>
    /// Connects to the server via LAN/IP and joins the game world by selecting a farmhand slot.
    /// For uncustomized slots, also handles character creation.
    /// If password protection is detected, automatically authenticates unless skipAutoLogin is true.
    /// </summary>
    public Task<JoinWorldResult> JoinWorldViaLanAsync(
        string address,
        int port,
        string farmerName,
        string favoriteThing = "Testing",
        bool preferExistingFarmer = true,
        bool skipAutoLogin = false,
        CancellationToken cancellationToken = default
    )
    {
        return JoinWorldCoreAsync(
            (attempt, maxAttempts, ct) =>
                ConnectViaLanOnceAsync(address, port, attempt, maxAttempts, ct),
            farmerName,
            favoriteThing,
            preferExistingFarmer,
            skipAutoLogin,
            onRetry: null,
            cancellationToken
        );
    }

    /// <summary>
    /// Shared implementation for JoinWorldAsync and JoinWorldViaLanAsync.
    /// Owns the single retry loop. connectOnceAsync performs one attempt with no internal retries.
    /// </summary>
    /// <param name="connectOnceAsync">Single-attempt connect function (attempt, maxAttempts, ct).</param>
    /// <param name="onRetry">Optional callback invoked before each retry (e.g., invite code re-fetch).</param>
    private async Task<JoinWorldResult> JoinWorldCoreAsync(
        Func<int, int, CancellationToken, Task<ConnectionResult>> connectOnceAsync,
        string farmerName,
        string favoriteThing,
        bool preferExistingFarmer,
        bool skipAutoLogin,
        Func<Task>? onRetry,
        CancellationToken cancellationToken
    )
    {
        string? lastError = null;

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            EmitDiagnostic(
                "join_attempt_started",
                new { attempt, maxAttempts = _options.MaxAttempts }
            );

            try
            {
                // Connect to server (single attempt; this method owns the retry loop)
                var connectResult = await connectOnceAsync(
                    attempt,
                    _options.MaxAttempts,
                    cancellationToken
                );
                if (!connectResult.Success || connectResult.Farmhands == null)
                {
                    lastError = connectResult.Error;
                    // Non-retryable errors propagate immediately
                    if (connectResult.Error != null && IsNonRetryableError(connectResult.Error))
                    {
                        EmitDiagnostic(
                            "join_attempt_failed",
                            new
                            {
                                attempt,
                                error = connectResult.Error,
                                retryable = false,
                                stage = "connect",
                            }
                        );
                        return JoinWorldResult.Failed(connectResult.Error, attempt);
                    }
                    EmitDiagnostic(
                        "join_attempt_failed",
                        new
                        {
                            attempt,
                            error = connectResult.Error,
                            retryable = attempt < _options.MaxAttempts,
                            stage = "connect",
                        }
                    );
                    if (attempt == _options.MaxAttempts)
                    {
                        break;
                    }
                    // Fall through to retry logic below
                }
                else
                {
                    // Complete the join (slot selection, character creation, auto-login)
                    var (joinResult, completeError) = await CompleteJoinAsync(
                        connectResult.Farmhands,
                        farmerName,
                        favoriteThing,
                        preferExistingFarmer,
                        skipAutoLogin,
                        attempt,
                        cancellationToken
                    );

                    if (joinResult != null)
                    {
                        return joinResult;
                    }

                    // CompleteJoinAsync returned null = retryable failure
                    lastError = completeError;
                    EmitDiagnostic(
                        "join_attempt_failed",
                        new
                        {
                            attempt,
                            error = completeError,
                            retryable = attempt < _options.MaxAttempts,
                            stage = "complete_join",
                        }
                    );
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"[{ex.GetType().Name}] {ex.Message}";
                EmitDiagnostic(
                    "join_attempt_failed",
                    new
                    {
                        attempt,
                        error = lastError,
                        retryable = attempt < _options.MaxAttempts,
                        stage = "exception",
                    }
                );
                if (attempt == _options.MaxAttempts)
                {
                    return JoinWorldResult.Failed(
                        $"Join failed after {_options.MaxAttempts} attempts: {lastError}",
                        attempt
                    );
                }
            }

            // Return to title before retry
            EmitDiagnostic("join_retry_cleanup_started", new { attempt });
            var cleanupStart = DateTime.UtcNow;
            try
            {
                await EnsureDisconnectedAsync();
                await Task.Delay(TestTimings.RetryPauseDelay, cancellationToken);
                if (onRetry != null)
                {
                    await onRetry();
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log($"[Retry] Disconnect before retry failed: {ex.Message}");
            }
            finally
            {
                EmitDiagnostic(
                    "join_retry_cleanup_completed",
                    new
                    {
                        attempt,
                        durationMs = (long)(DateTime.UtcNow - cleanupStart).TotalMilliseconds,
                    }
                );
            }
        }

        return JoinWorldResult.Failed(
            $"Join failed after {_options.MaxAttempts} attempts: {lastError ?? "unknown error"}",
            _options.MaxAttempts
        );
    }

    /// <summary>
    /// Handles the post-connection join flow: slot selection, character creation, and auto-login.
    /// Returns (result, null) on success, or (null, error) if the attempt should be retried.
    /// </summary>
    private async Task<(JoinWorldResult? Result, string? Error)> CompleteJoinAsync(
        FarmhandsResponse farmhands,
        string farmerName,
        string favoriteThing,
        bool preferExistingFarmer,
        bool skipAutoLogin,
        int attempt,
        CancellationToken cancellationToken
    )
    {
        // Find appropriate slot
        var targetSlot = PickSlot(farmhands, farmerName, preferExistingFarmer);
        if (targetSlot == null)
        {
            return (null, "No available farmhand slots");
        }

        int selectedSlotIndex = targetSlot.Index;
        EmitDiagnostic(
            "slot_picked",
            new { slotIndex = targetSlot.Index, isCustomized = targetSlot.IsCustomized }
        );

        // Select the farmhand slot and handle character creation if uncustomized.
        // The server may bounce the client back to the farmhand selection screen if
        // isGameAvailable() is false (day transition, save sync). This should be rare
        // now that GameClientContainer.DisconnectAsync waits for ForDisconnected before
        // returning containers to the pool -- the main historical cause was stale peer
        // reconnections that tied up the game thread with "farmhand availability failed"
        // rejection loops. If this retry loop still exhausts, investigate whether a
        // client container was reused without a clean disconnect.
        // We detect this and re-select the slot.
        if (!targetSlot.IsCustomized)
        {
            var (characterCreated, charError) = await SelectSlotAndCreateCharacterAsync(
                targetSlot,
                farmerName,
                favoriteThing,
                preferExistingFarmer,
                cancellationToken
            );
            if (!characterCreated)
            {
                return (null, charError);
            }
        }
        else
        {
            var selectResult = await _gameClient.Farmhands.Select(targetSlot.Index);
            if (selectResult?.Success != true)
            {
                return (null, $"Select farmhand slot: {selectResult?.Error ?? "failed"}");
            }
        }

        // Wait for world to be ready
        var worldWait = await _gameClient.Wait.ForWorldReady(TestTimings.WorldReadyTimeout);
        if (worldWait?.Success != true)
        {
            return (null, $"Wait for world ready: {worldWait?.Error ?? "timeout"}");
        }

        await CaptureCheckpointIfEnabled("after_join");

        // Snapshot client state once after world-ready. Both the auth lobby-detection
        // below and the UID visibility gate read from the same result, so we avoid
        // a duplicate /status round-trip. UID does not change across the post-auth
        // warp, so the pre-auth snapshot remains valid for the gate.
        GameStateResult? state = null;
        try
        {
            state = await _gameClient.GetState();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log($"[Join] GetState failed after world ready: {ex.Message}");
        }

        // Check if auto-login is needed
        bool wasAuthenticated = false;
        bool wasInLobby = false;
        bool serverUnhealthy = false;

        var shouldAutoLogin =
            !skipAutoLogin && _options.AutoLogin && !string.IsNullOrEmpty(_options.ServerPassword);
        if (shouldAutoLogin)
        {
            // Detect lobby placement by checking player location.
            // The server places unauthenticated players in a lobby cabin during
            // sendServerIntroduction (before world load), so the location is
            // available immediately after world ready, with no timing dependency on
            // the welcome chat message which has a 2-second server-side delay.
            // Location uses NameOrUniqueName: cabin interiors are "FarmHouse{guid}"
            // (IndoorMap="FarmHouse" in Buildings.json). Both lobby and player cabins match.
            var needsLogin =
                state?.Location?.StartsWith(
                    GameTestClient.CabinLocationPrefix,
                    StringComparison.OrdinalIgnoreCase
                ) == true;
            Log($"[Auth] Location after join: {state?.Location} (needsLogin={needsLogin})");

            if (needsLogin)
            {
                wasInLobby = true;
                var preAuthLocation = state?.Location;

                for (
                    int loginAttempt = 1;
                    loginAttempt <= TestTimings.AuthLoginMaxAttempts;
                    loginAttempt++
                )
                {
                    if (loginAttempt > 1)
                    {
                        Log(
                            $"[Auth] Retrying !login (attempt {loginAttempt}/{TestTimings.AuthLoginMaxAttempts})..."
                        );
                    }

                    try
                    {
                        await _gameClient.SendChat($"!login {_options.ServerPassword}");
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        Log($"[Auth] Failed to send !login: {ex.Message}");
                        continue;
                    }

                    Log(
                        "[Auth] Waiting for post-auth warp (location change to different cabin)..."
                    );
                    wasAuthenticated = await _gameClient.WaitForAuthWarpAsync(
                        preAuthLocation!,
                        ct: cancellationToken
                    );

                    if (wasAuthenticated)
                    {
                        Log("[Auth] Authenticated (confirmed via location change)");
                        await CaptureCheckpointIfEnabled("after_auth");
                        break;
                    }

                    Log(
                        $"[Auth] Auth not confirmed within {TestTimings.AuthLoginAttemptTimeout.TotalSeconds}s"
                    );
                }

                // All auth attempts exhausted. Probe the server to distinguish
                // "auth timing issue" from "server is broken".
                if (!wasAuthenticated && _serverApi != null)
                {
                    try
                    {
                        var players = await _serverApi.GetPlayers();
                        if (players == null)
                        {
                            serverUnhealthy = true;
                            Log(
                                "[Auth] Server player check returned null, server may be unhealthy"
                            );
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        serverUnhealthy = true;
                        Log(
                            $"[Auth] Server player check failed, game thread unresponsive: {ex.Message}"
                        );
                    }
                }
            }
        }

        // Wait for the server API to confirm this farmhand is visible before returning.
        // Match by UniqueMultiplayerID, not Name: UID is assigned at peer-add while
        // Name lags 1-18s behind the character XML round-trip.
        if (!long.TryParse(state?.UniqueId, out var uid) || uid == 0)
        {
            var diagnostics = await FailureContext.DumpAsync(
                _serverApi,
                reason: "join_uid_missing_from_client_state",
                extras: new Dictionary<string, object?>
                {
                    ["farmerName"] = farmerName,
                    ["statePresent"] = state != null,
                    ["uniqueIdRaw"] = state?.UniqueId,
                    ["attempt"] = attempt,
                }
            );
            return (
                JoinWorldResult.Failed(
                    $"Join for '{farmerName}' failed: GetState returned no UniqueMultiplayerID after world ready "
                        + $"(state={(state == null ? "null" : "present")}, uniqueId='{state?.UniqueId}')",
                    attempt,
                    diagnostics
                ),
                null
            );
        }

        if (_serverApi != null)
        {
            var synced = await _serverApi.WaitForPlayerByIdAsync(uid, ct: cancellationToken);
            if (!synced)
            {
                // Enrich with live server state so the user-facing error — and
                // the summary.json classification — can distinguish "snapshot
                // stale", "uid never added", "uid added then removed", etc.
                // The WaitFor helper's onTimeoutAsync already emitted a
                // failure_context event; we re-dump here so the result object
                // carries diagnostics for test assertions.
                var diagnostics = await FailureContext.DumpAsync(
                    _serverApi,
                    reason: "player_visibility_timeout",
                    extras: new Dictionary<string, object?>
                    {
                        ["farmerName"] = farmerName,
                        ["uid"] = uid,
                        ["attempt"] = attempt,
                    }
                );
                return (
                    JoinWorldResult.Failed(
                        $"Server did not confirm player '{farmerName}' (uid={uid}) visibility after join",
                        attempt,
                        diagnostics
                    ),
                    null
                );
            }
        }

        var authStatus = wasAuthenticated ? ", authenticated" : (wasInLobby ? ", in lobby" : "");
        Log(
            $"Joined world as '{farmerName}' (uid={uid}){authStatus} (attempt {attempt}/{_options.MaxAttempts})"
        );
        return (
            JoinWorldResult.Succeeded(
                attempt,
                selectedSlotIndex,
                uid,
                wasAuthenticated,
                wasInLobby,
                serverUnhealthy
            ),
            null
        );
    }

    /// <summary>
    /// Picks the best available farmhand slot from a farmhand list.
    /// Prefers an existing customized slot matching farmerName (if preferExistingFarmer),
    /// otherwise picks a random uncustomized slot to avoid collisions with concurrent clients.
    /// Returns null if no suitable slot is available.
    /// </summary>
    private static FarmhandSlot? PickSlot(
        FarmhandsResponse farmhands,
        string farmerName,
        bool preferExistingFarmer
    )
    {
        if (preferExistingFarmer)
        {
            var existing = farmhands.Farmhands.FirstOrDefault(s =>
                s.IsCustomized && s.Name.Equals(farmerName, StringComparison.OrdinalIgnoreCase)
            );
            if (existing != null)
            {
                return existing;
            }
        }

        var available = farmhands.Farmhands.Where(s => !s.IsCustomized).ToList();
        return available.Count == 0 ? null : available[Random.Shared.Next(available.Count)];
    }

    /// <summary>
    /// Selects an uncustomized farmhand slot and creates the character, handling server
    /// bounce-backs. When the server's game thread is busy (isGameAvailable() == false),
    /// it sends "Waiting for host event" then re-sends the farmhand list instead of
    /// processing the join. The client goes back to FarmhandMenu with slots visible.
    /// This method detects that state and re-selects the slot, re-picking from the
    /// fresh farmhand list since slot indices may have changed.
    /// </summary>
    private async Task<(bool Success, string? Error)> SelectSlotAndCreateCharacterAsync(
        FarmhandSlot initialSlot,
        string farmerName,
        string favoriteThing,
        bool preferExistingFarmer,
        CancellationToken cancellationToken
    )
    {
        const int maxBounceRetries = 5;
        var methodStart = DateTime.UtcNow;
        var deadline = methodStart + TestTimings.CharacterMenuTimeout;
        var currentSlot = initialSlot;

        for (int bounce = 0; bounce <= maxBounceRetries; bounce++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (bounce > 0)
            {
                Log(
                    $"[Join] Server bounced back to farmhand selection (attempt {bounce}/{maxBounceRetries}), re-selecting slot..."
                );
            }

            var bounceElapsedMs = (long)(DateTime.UtcNow - methodStart).TotalMilliseconds;
            Log(
                $"[Join] Bounce attempt {bounce}/{maxBounceRetries}, slot={currentSlot.Index}, elapsed={bounceElapsedMs}ms"
            );
            EmitDiagnostic(
                "join_bounce",
                new
                {
                    bounce,
                    slot = currentSlot.Index,
                    elapsedMs = bounceElapsedMs,
                }
            );

            // Select the slot
            var selectResult = await _gameClient.Farmhands.Select(currentSlot.Index);
            Log(
                $"[Join] Select slot {currentSlot.Index} returned success={selectResult?.Success}, error={selectResult?.Error}"
            );
            EmitDiagnostic(
                "select_returned",
                new
                {
                    slot = currentSlot.Index,
                    success = selectResult?.Success,
                    error = selectResult?.Error,
                }
            );
            if (selectResult?.Success != true)
            {
                return (false, $"Select farmhand slot: {selectResult?.Error ?? "failed"}");
            }

            // Poll for either CharacterCustomization (success) or FarmhandMenu reappearing (bounce-back)
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return (
                    false,
                    $"Wait for character menu: deadline exceeded after {maxBounceRetries} bounce-back(s)"
                );
            }

            // Race the test-client's two server-side wait endpoints — character
            // customization (success) vs. farmhand-menu reappearing (bounce-back).
            // The test-client mod's WaitForCondition does the predicate evaluation
            // game-thread-side, so the loser keeps spinning a worker until cancelled.
            bool gotCharacterMenu = false;
            bool gotBounceBack = false;
            FarmhandsResponse? bouncedSlots = null;
            Exception? lastException = null;

            using (var raceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var characterTask = _gameClient.Wait.ForCharacter(
                    timeout: remaining,
                    ct: raceCts.Token
                );
                var farmhandsTask = _gameClient.Wait.ForFarmhands(
                    timeout: remaining,
                    ct: raceCts.Token
                );

                Task<WaitResult?> winner;
                try
                {
                    winner = await Task.WhenAny(characterTask, farmhandsTask);
                }
                finally
                {
                    // Free the coordinator-side HttpClient connection. NOTE: the
                    // test-client mod's WaitForCondition (ModEntry.cs:918) is a
                    // game-thread busy-loop that doesn't honor per-request
                    // cancellation — System.Net.HttpListener doesn't surface one.
                    // So the loser's busy-loop continues until its own timeout
                    // (CharacterMenuTimeout=30s); the cancellation here only
                    // releases our coordinator-side HTTP worker, not the
                    // test-client one. Acceptable per the plan's §B.3 sizing.
                    raceCts.Cancel();
                }

                try
                {
                    var result = await winner;
                    if (result?.Success == true)
                    {
                        if (result.Condition == "character-customization")
                        {
                            gotCharacterMenu = true;
                            EmitDiagnostic(
                                "character_menu_detected",
                                new
                                {
                                    elapsedMs = (long)
                                        (DateTime.UtcNow - methodStart).TotalMilliseconds,
                                    bounce,
                                }
                            );
                        }
                        else if (result.Condition == "farmhand-menu")
                        {
                            // Bounce-back — re-fetch fresh slots from the test-client.
                            try
                            {
                                var slots = await _gameClient.Farmhands.GetSlots();
                                if (slots?.Success == true && slots.Farmhands.Count > 0)
                                {
                                    bouncedSlots = slots;
                                    gotBounceBack = true;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                lastException = ex;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The loser surfaces OperationCanceledException after raceCts.Cancel();
                    // ignore it — we've already extracted the winner's result above.
                }
                catch (Exception ex)
                {
                    lastException = ex;
                }

                // Drain the loser quietly so its HTTP request is fully reaped before
                // we exit the using-block (cancelled request still completes).
                try
                {
                    await Task.WhenAll(characterTask, farmhandsTask);
                }
                catch
                { /* one or both faulted with OperationCanceledException — expected */
                }
            }

            EmitDiagnostic(
                "join_poll_state",
                new
                {
                    elapsedMs = (long)(DateTime.UtcNow - methodStart).TotalMilliseconds,
                    timeoutSec = (int)TestTimings.CharacterMenuTimeout.TotalSeconds,
                    gotCharacterMenu,
                    gotBounceBack,
                    error = lastException?.Message,
                }
            );

            if (gotCharacterMenu)
            {
                // Character creation menu appeared; proceed with customization
                var customizeResult = await _gameClient.Character.Customize(
                    farmerName,
                    favoriteThing
                );
                if (customizeResult?.Success != true)
                {
                    return (false, $"Customize character: {customizeResult?.Error ?? "failed"}");
                }

                await Task.Delay(TestTimings.CharacterCreationSyncDelay, cancellationToken);

                var confirmResult = await _gameClient.Character.Confirm();
                if (confirmResult?.Success != true)
                {
                    return (false, $"Confirm character: {confirmResult?.Error ?? "failed"}");
                }

                EmitDiagnostic("character_confirmed", new { slotIndex = currentSlot.Index });
                return (true, null);
            }

            if (!gotBounceBack)
            {
                return (
                    false,
                    $"Wait for character menu: Timeout after {TestTimings.CharacterMenuTimeout.TotalSeconds}s "
                        + $"(lastError={lastException?.Message ?? "none"})"
                );
            }

            // Got bounce-back -- re-pick slot from the fresh list the server sent
            if (bouncedSlots != null)
            {
                var repicked = PickSlot(bouncedSlots, farmerName, preferExistingFarmer);
                if (repicked != null)
                {
                    if (repicked.Index != currentSlot.Index)
                    {
                        Log(
                            $"[Join] Re-picked slot {repicked.Index} (was {currentSlot.Index}) after bounce-back"
                        );
                    }

                    currentSlot = repicked;
                }
            }
        }

        // This should no longer occur now that GameClientContainer.DisconnectAsync waits
        // for ForDisconnected. If it does, a client container was likely reused before
        // the server fully processed the previous peer's disconnect, causing a stale
        // peer reconnection loop that blocks isGameAvailable(). Check server logs for
        // "game didn't see them disconnect" around the failure time.
        return (
            false,
            $"Server bounced back to farmhand selection {maxBounceRetries} times (game thread persistently unavailable)"
        );
    }
}
