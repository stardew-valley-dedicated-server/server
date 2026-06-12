namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Centralized timing constants for integration tests.
/// Provides consistent, documented delays and timeouts across all tests.
/// All values use TimeSpan for clarity and type safety.
/// </summary>
public static class TestTimings
{
    #region Fixture Setup Timeouts

    /// <summary>
    /// Wall-clock timeout for a single test body. When it expires, the linked
    /// TestContext cancellation token fires and the test is recorded as timed out.
    /// Cleanup paths run on their own budgets afterward.
    /// </summary>
    public static readonly TimeSpan PerTestTimeout = TimeSpan.FromSeconds(300);

    /// <summary>
    /// Timeout for game client to start and respond to API calls.
    /// </summary>
    public static readonly TimeSpan GameReadyTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Timeout for Docker image builds.
    /// </summary>
    public static readonly TimeSpan DockerBuildServerTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DockerBuildSteamAuthTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Time budget for verifying every configured Steam account is logged in
    /// before any test starts. Accounts with valid tokens flip to logged_in
    /// in 1-3 seconds via the steam-auth container's parallel auto-login;
    /// 30s is ~10x the expected case and well below the 60s+ interactive
    /// Steam Guard fall-through that hangs in piped CI environments.
    /// Polled via SharedSteamAuth.WaitForAccountsLoggedInAsync.
    /// </summary>
    public static readonly TimeSpan SteamAccountReadyTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for stopping Docker containers during cleanup.
    /// </summary>
    public static readonly TimeSpan ContainerStopTimeout = TimeSpan.FromSeconds(10);

    #endregion

    #region Network & Sync Delays

    /// <summary>
    /// Maximum time to wait for network data to sync after connecting to the server.
    /// Used as timeout for polling; actual wait is usually much shorter.
    /// </summary>
    public static readonly TimeSpan NetworkSyncTimeout = TimeSpan.FromMilliseconds(10000);

    /// <summary>
    /// Brief delay for game to sync textbox values during character creation.
    /// </summary>
    public static readonly TimeSpan CharacterCreationSyncDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Brief pause between connection retry attempts.
    /// </summary>
    public static readonly TimeSpan RetryPauseDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Budget for polling the server-side /players endpoint after a client has
    /// disconnected, waiting for the player record to disappear. The next test
    /// reusing the same client container must not reconnect before the server
    /// has finished processing the disconnect, or farmhand rejection loops
    /// ensue. If the budget expires, the caller poisons the server lease
    /// rather than continuing with an unknown server state.
    /// </summary>
    public static readonly TimeSpan FarmerRemovalBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Budget for revalidating a persistent session on reuse. A KeepConnected
    /// session holds client-side state that can diverge from the server if the
    /// farmer was evicted (e.g. server-side peer drop while the test-client
    /// still believes it is connected). On reuse, we poll GET /players for the
    /// farmer UID within this budget; if not seen, the session is torn down
    /// and rebuilt via the existing dead-session fallback in TestBase.
    /// Mirror of FarmerRemovalBudget (the "is gone" check after disconnect);
    /// this is the "is still present" check before reuse.
    /// </summary>
    public static readonly TimeSpan SessionRevalidationBudget = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Total timeout for farmer deletion polling during cleanup.
    /// The delete API is called immediately after disconnect; if the server
    /// hasn't processed the disconnect yet ("currently online" error), we
    /// retry with FastPollInterval until this timeout. Must exceed the
    /// server's RunOnGameThreadAsync timeout (15s) so that a single 503
    /// (which takes ~16s wall-clock) doesn't exhaust the entire budget.
    /// 35s allows one 503 + one successful call with headroom.
    /// </summary>
    public static readonly TimeSpan FarmerDeleteTimeout = TimeSpan.FromSeconds(35);

    /// <summary>
    /// Delay after killing game processes to ensure they fully exit.
    /// </summary>
    public static readonly TimeSpan ProcessExitDelay = TimeSpan.Zero;

    /// <summary>
    /// Short delay between attempts when waiting for game client to respond.
    /// </summary>
    public static readonly TimeSpan GameClientPollDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Delay between connection checks during game client startup.
    /// </summary>
    public static readonly TimeSpan GameClientStartupPollDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Patience window for a non-Steam client lease that finds the pool empty
    /// but is under the container cap. If an outstanding client is in use, wait
    /// up to this long for a return before triggering a fresh container build.
    /// A new container takes ~60s on typical hardware; a returning client is
    /// usually milliseconds. Operator-tunable via <c>SDVD_CLIENT_LEASE_PATIENCE_S</c>
    /// (seconds); <c>0</c> disables the wait and reverts to eager creation.
    /// </summary>
    public static readonly TimeSpan ClientLeasePatience = ParseClientLeasePatience();

    private static TimeSpan ParseClientLeasePatience()
    {
        var raw = Environment.GetEnvironmentVariable("SDVD_CLIENT_LEASE_PATIENCE_S");
        if (string.IsNullOrEmpty(raw))
            return TimeSpan.FromSeconds(20);
        if (int.TryParse(raw, out var s) && s >= 0)
            return TimeSpan.FromSeconds(s);
        return TimeSpan.FromSeconds(20);
    }

    #endregion

    #region Rendering & Visual Delays

    /// <summary>
    /// Time to wait after toggling rendering for the change to take effect.
    /// Allows frames to start/stop rendering before capturing screenshots.
    /// </summary>
    public static readonly TimeSpan RenderingToggleDelay = TimeSpan.FromMilliseconds(500);

    #endregion

    #region Game Time Values

    /// <summary>Game time for noon (12:00 PM). Mid-day, firmly inside the pause window.</summary>
    public const int Noon = 1200;

    /// <summary>One tick before 2:00 AM pass-out (2550 → 2600 in ~7s).</summary>
    public const int PrePassOutTime = 2550;

    /// <summary>2:00 AM. Game forces performPassoutWarp() at this time.</summary>
    public const int PassOutTime = 2600;

    /// <summary>Start of the auto-pause window (6:10 AM). AlwaysOn pauses when no players connected.</summary>
    public const int PauseWindowStart = 610;

    /// <summary>End of the auto-pause window (1:00 AM). After this, game unpauses for pass-out sequence.</summary>
    public const int PauseWindowEnd = 2500;

    #endregion

    #region Game Time Delays

    /// <summary>
    /// Time to wait for game time to advance.
    /// With 10x clock speed, a tick fires in ~0.7s instead of ~7s.
    /// 5s is a generous safety margin.
    /// </summary>
    public static readonly TimeSpan TimeAdvanceWait = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Time to wait when verifying time is paused.
    /// With 10x clock speed, 2s covers ~28 game-ticks worth of verification.
    /// </summary>
    public static readonly TimeSpan TimePausedVerification = TimeSpan.FromSeconds(2);

    #endregion

    #region Polling Intervals

    /// <summary>
    /// Interval between status polls when waiting for day change.
    /// The /status endpoint is lightweight; 500ms reduces detection latency.
    /// </summary>
    public static readonly TimeSpan DayChangePollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Interval for tight polling loops in tests (chat responses, state sync, etc.).
    /// Short interval to minimize wasted time while avoiding busy-spinning.
    /// </summary>
    public static readonly TimeSpan FastPollInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Timeout for waiting for cabin assignment to sync after character creation.
    /// Must exceed the server's RunOnGameThreadAsync timeout (15s) so that at least
    /// one API call can complete even if the game thread is briefly contended.
    /// A single 503 takes ~16s wall-clock; 35s allows one 503 + one successful call.
    /// </summary>
    public static readonly TimeSpan CabinAssignmentTimeout = TimeSpan.FromSeconds(35);

    #endregion

    #region Cleanup Delays

    /// <summary>
    /// Delay between kill command retries during cleanup.
    /// </summary>
    public static readonly TimeSpan KillRetryDelay = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Time to wait for background tasks to complete during cleanup.
    /// </summary>
    public static readonly TimeSpan TaskCleanupTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Hard timeout for the entire test cleanup phase (disconnect, farmer delete,
    /// exception check, client lease return). Prevents slow HTTP calls from stalling
    /// the global client capacity gate and blocking all subsequent tests.
    /// Must accommodate at least one 503 retry (~16s) per cleanup phase.
    /// </summary>
    public static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(45);

    #endregion

    #region Game Thread Retry

    /// <summary>
    /// Max retry attempts for 503 (game thread blocked during day transition/save).
    /// Applied at the raw API method level so all callers benefit transparently.
    /// </summary>
    public const int GameThreadRetryMaxAttempts = 5;

    /// <summary>
    /// Delay between 503 retries. 3s balances fast recovery against not hammering
    /// a server whose game thread is busy saving.
    /// </summary>
    public static readonly TimeSpan GameThreadRetryDelay = TimeSpan.FromSeconds(3);

    #endregion

    #region Screenshot Capture

    /// <summary>
    /// Per-attempt timeout for screenshot capture. Caps one GetScreenshot() call
    /// including any 503 retries at the HTTP layer. 8s accommodates one game-thread
    /// dispatch plus margin without waiting for a full 503 retry cycle.
    /// </summary>
    public static readonly TimeSpan ScreenshotAttemptTimeout = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Delay between screenshot retry attempts. Shorter than GameThreadRetryDelay
    /// because screenshot capture is non-critical and we want fast recovery.
    /// </summary>
    public static readonly TimeSpan ScreenshotRetryDelay = TimeSpan.FromSeconds(1);

    #endregion

    #region HTTP Client Timeouts

    /// <summary>
    /// Timeout for quick HTTP health checks.
    /// </summary>
    public static readonly TimeSpan HttpHealthCheckTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Per-request timeout for HTTP calls inside polling loops.
    /// Read-only endpoints (status, players, farmhands, cabins, auth) respond
    /// instantly from a snapshot, so this primarily guards against TCP-level
    /// timeouts during server boot. For mutating endpoints (DELETE /farmhands),
    /// this caps individual requests so the polling loop can retry promptly
    /// instead of burning its budget on a single slow call.
    /// </summary>
    public static readonly TimeSpan PollingRequestTimeout = TimeSpan.FromSeconds(5);

    #endregion

    #region Chat & Command Delays

    /// <summary>
    /// Maximum time to wait for server welcome message after joining (sent ~2s after join).
    /// Used as timeout for polling; actual wait is usually much shorter.
    /// </summary>
    public static readonly TimeSpan WelcomeMessageTimeout = TimeSpan.FromMilliseconds(5000);

    /// <summary>
    /// Maximum time to wait for chat command response.
    /// Used as timeout for polling; actual wait is usually much shorter.
    /// Needs headroom for concurrent day transitions that block the game thread
    /// for several seconds (auto-sleep triggered by other tests' clients disconnecting).
    /// </summary>
    public static readonly TimeSpan ChatCommandTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum time to wait for a single !login attempt to be acknowledged.
    /// Longer than ChatCommandTimeout because the server has a 2s welcome message
    /// delay that can overlap with auth processing, and under load the game thread
    /// may be busy (saves, serialization, or contended by concurrent servers).
    /// Each attempt polls for this long before retrying the !login command.
    /// </summary>
    public static readonly TimeSpan AuthLoginAttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Maximum number of !login attempts during auto-authentication.
    /// The first attempt can fail if the server is busy processing the welcome
    /// message or the game thread is blocked during a save.
    /// </summary>
    public static readonly int AuthLoginMaxAttempts = 3;

    /// <summary>
    /// Time to wait for chat message to be delivered via WebSocket API.
    /// WebSocket delivery is async but typically completes in <1s.
    /// </summary>
    public static readonly TimeSpan ChatDeliveryDelay = TimeSpan.FromSeconds(1);

    #endregion

    #region Game Client Wait Timeouts

    /// <summary>
    /// Timeout for waiting for the farmhand selection menu.
    /// Must be long enough to cover transient "host busy" waits where the server
    /// defers the farmhand list via a whenGameAvailable callback. On Docker hosts
    /// running multiple game servers, game thread stalls of 23-32s have been observed
    /// during startup due to resource contention. 45s survives these stalls with margin.
    /// </summary>
    public static readonly TimeSpan FarmhandMenuTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Timeout for waiting for the world to be ready after joining.
    /// </summary>
    public static readonly TimeSpan WorldReadyTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Timeout for waiting for character customization menu.
    /// </summary>
    public static readonly TimeSpan CharacterMenuTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for waiting for title screen after exit.
    /// </summary>
    public static readonly TimeSpan TitleScreenTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for waiting for disconnected state.
    /// </summary>
    public static readonly TimeSpan DisconnectedTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for waiting for CoopMenu.
    /// </summary>
    public static readonly TimeSpan MenuWaitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for waiting for text input menu.
    /// </summary>
    public static readonly TimeSpan TextInputTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Timeout for waiting for day change (sleep/pass-out transitions).
    /// </summary>
    public static readonly TimeSpan DayChangeTimeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// Timeout for waiting for server to be ready between tests.
    /// Covers both player disconnect processing AND day transition completion.
    /// After all clients disconnect, barriers auto-complete (empty otherFarmers)
    /// but the save and post-barrier processing can take several more seconds.
    /// </summary>
    public static readonly TimeSpan ServerReadyBetweenTests = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Timeout for waiting for IsReady after a day change is detected.
    /// Covers newDaySync barriers + save (5-10+ seconds on large farms).
    /// </summary>
    public static readonly TimeSpan DayTransitionSettleTimeout = TimeSpan.FromSeconds(30);

    #endregion
}
