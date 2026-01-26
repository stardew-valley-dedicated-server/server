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
    /// Timeout for server container to become ready and report a valid invite code.
    /// </summary>
    public static readonly TimeSpan ServerReadyTimeout = TimeSpan.FromSeconds(180);

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
    /// Timeout for starting Docker containers.
    /// </summary>
    public static readonly TimeSpan ContainerStartTimeout = TimeSpan.FromSeconds(60);

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
    public static readonly TimeSpan NetworkSyncTimeout = TimeSpan.FromMilliseconds(5000);

    /// <summary>
    /// Time to wait after disconnecting before checking server state.
    /// Only used as a fallback; prefer polling with FarmerDeleteTimeout.
    /// </summary>
    public static readonly TimeSpan DisconnectProcessingDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Brief delay for game to sync textbox values during character creation.
    /// </summary>
    public static readonly TimeSpan CharacterCreationSyncDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Brief pause between connection retry attempts.
    /// </summary>
    public static readonly TimeSpan RetryPauseDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Total timeout for farmer deletion polling during cleanup.
    /// The delete API is called immediately after disconnect; if the server
    /// hasn't processed the disconnect yet ("currently online" error), we
    /// retry with FastPollInterval until this timeout. The server processes
    /// disconnects within one game tick (~16ms), so this rarely needs more
    /// than one retry.
    /// </summary>
    public static readonly TimeSpan FarmerDeleteTimeout = TimeSpan.FromSeconds(5);

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

    #endregion

    #region Rendering & Visual Delays

    /// <summary>
    /// Time to wait after toggling rendering for the change to take effect.
    /// Allows frames to start/stop rendering before capturing screenshots.
    /// </summary>
    public static readonly TimeSpan RenderingToggleDelay = TimeSpan.FromMilliseconds(1000);

    #endregion

    #region Game Time Delays

    /// <summary>
    /// Time to wait for game time to advance.
    /// Stardew advances time every ~7 seconds (10 game-minutes per tick).
    /// 8 seconds is sufficient to detect at least 1 tick.
    /// </summary>
    public static readonly TimeSpan TimeAdvanceWait = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Time to wait when verifying time is paused.
    /// 8 seconds is longer than one tick (7s), sufficient to detect if game were unpaused.
    /// </summary>
    public static readonly TimeSpan TimePausedVerification = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Small delay to let game loop process a time change.
    /// </summary>
    public static readonly TimeSpan TimeChangeProcessingDelay = TimeSpan.Zero;

    #endregion

    #region Polling Intervals

    /// <summary>
    /// Interval between status polls when waiting for server to become ready.
    /// </summary>
    public static readonly TimeSpan ServerPollInterval = TimeSpan.FromSeconds(2);

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
    /// Interval for polling server container logs.
    /// </summary>
    public static readonly TimeSpan ServerLogPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Delay before retrying after server log streaming error.
    /// </summary>
    public static readonly TimeSpan ServerLogErrorRetryDelay = TimeSpan.FromMilliseconds(2000);

    /// <summary>
    /// Timeout for waiting for cabin assignment to sync after character creation.
    /// </summary>
    public static readonly TimeSpan CabinAssignmentTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Polling interval when waiting for cabin assignment.
    /// </summary>
    public static readonly TimeSpan CabinAssignmentPollInterval = TimeSpan.FromMilliseconds(500);

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

    #endregion

    #region HTTP Client Timeouts

    /// <summary>
    /// Timeout for quick HTTP health checks.
    /// </summary>
    public static readonly TimeSpan HttpHealthCheckTimeout = TimeSpan.FromSeconds(5);

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
    /// </summary>
    public static readonly TimeSpan ChatCommandTimeout = TimeSpan.FromMilliseconds(3000);

    /// <summary>
    /// Time to wait for chat message to be delivered via WebSocket API.
    /// WebSocket delivery is async but typically completes in <1s.
    /// </summary>
    public static readonly TimeSpan ChatDeliveryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Time to wait during layout cleanup operations.
    /// Layout deletion is synchronous; 500ms provides safe buffer.
    /// </summary>
    public static readonly TimeSpan LayoutCleanupDelay = TimeSpan.FromMilliseconds(200);

    #endregion

    #region Game Client Wait Timeouts

    /// <summary>
    /// Timeout for waiting for the farmhand selection menu.
    /// Reduced from 60s to allow faster retries on connection failures.
    /// </summary>
    public static readonly TimeSpan FarmhandMenuTimeout = TimeSpan.FromSeconds(20);

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

    #endregion
}
