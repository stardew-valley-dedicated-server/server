namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Centralized timing constants for integration tests.
/// Provides consistent, documented delays and timeouts across all tests.
/// </summary>
public static class TestTimings
{
    #region Network & Sync Delays

    /// <summary>
    /// Time to wait for network data to sync after connecting to the server.
    /// Used after joining world to allow player data to propagate.
    /// </summary>
    public const int NetworkSyncDelayMs = 2000;

    /// <summary>
    /// Time to wait after disconnecting before checking server state.
    /// Allows server to process the disconnection event.
    /// </summary>
    public const int DisconnectProcessingDelayMs = 3000;

    /// <summary>
    /// Brief delay for game to sync textbox values during character creation.
    /// </summary>
    public const int CharacterCreationSyncDelayMs = 200;

    /// <summary>
    /// Brief pause between connection retry attempts.
    /// </summary>
    public const int RetryPauseDelayMs = 1000;

    #endregion

    #region Rendering & Visual Delays

    /// <summary>
    /// Time to wait after disabling rendering for frames to stop.
    /// </summary>
    public const int RenderingDisableDelayMs = 1500;

    /// <summary>
    /// Time to wait after enabling rendering for game frames to render.
    /// </summary>
    public const int RenderingEnableDelayMs = 3000;

    #endregion

    #region Game Time Delays

    /// <summary>
    /// Time to wait for game time to advance.
    /// Stardew advances time every ~7 seconds (10 game-minutes per tick).
    /// 16 seconds ensures at least 2 time advances.
    /// </summary>
    public const int TimeAdvanceWaitMs = 16000;

    /// <summary>
    /// Time to wait when verifying time is paused.
    /// Long enough for multiple time advances if game were unpaused (~15 seconds = ~2 advances).
    /// </summary>
    public const int TimePausedVerificationMs = 15000;

    /// <summary>
    /// Small delay to let game loop process a time change.
    /// </summary>
    public const int TimeChangeProcessingDelayMs = 1000;

    #endregion

    #region Polling Intervals

    /// <summary>
    /// Interval between status polls when waiting for day change.
    /// </summary>
    public const int DayChangePollIntervalMs = 2000;

    #endregion

    #region Timeouts

    /// <summary>
    /// Timeout for waiting for the farmhand selection menu.
    /// </summary>
    public const int FarmhandMenuTimeoutMs = 60000;

    /// <summary>
    /// Timeout for waiting for the world to be ready after joining.
    /// </summary>
    public const int WorldReadyTimeoutMs = 60000;

    /// <summary>
    /// Timeout for waiting for character customization menu.
    /// </summary>
    public const int CharacterMenuTimeoutMs = 30000;

    /// <summary>
    /// Timeout for waiting for title screen after exit.
    /// </summary>
    public const int TitleScreenTimeoutMs = 30000;

    /// <summary>
    /// Timeout for waiting for disconnected state.
    /// </summary>
    public const int DisconnectedTimeoutMs = 10000;

    /// <summary>
    /// Timeout for waiting for CoopMenu.
    /// </summary>
    public const int MenuWaitTimeoutMs = 10000;

    /// <summary>
    /// Timeout for waiting for text input menu.
    /// </summary>
    public const int TextInputTimeoutMs = 10000;

    /// <summary>
    /// Timeout for waiting for day change (sleep/pass-out transitions).
    /// </summary>
    public static readonly TimeSpan DayChangeTimeout = TimeSpan.FromSeconds(120);

    #endregion
}
