using System.Linq;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for password protection and lobby system.
/// Verifies authentication flow, lobby cabin behavior, chat commands,
/// message filtering, auth timeout, and day transition resilience.
///
/// Performance: Uses KeepConnected for lobby session persistence.
/// Tests that need a fresh connection use PersistentSession.BreakSessionAsync().
///
/// Disruptive tests (Login_WithWrongPassword_Fails, Login_ExceedsMaxAttempts_KicksPlayer)
/// are in PasswordProtectionDisruptiveTests -- they break their session immediately,
/// gaining nothing from KeepConnected. Extracted to run in parallel.
/// </summary>
[TestServer(Password = "test-password-123", KeepConnected = true)]
public class PasswordProtectionTests : TestBase
{
    // Common keywords for detecting welcome/login messages
    private static readonly string[] WelcomeKeywords = { "PASSWORD", "!login" };
    private static readonly string[] FailureKeywords = { "incorrect", "invalid", "wrong", "failed" };

    public PasswordProtectionTests() { }

    /// <summary>
    /// Verifies that when password protection is enabled, a new player
    /// starts in the lobby cabin and receives a welcome message explaining
    /// how to authenticate with !login.
    /// </summary>
    [Fact]
    public async Task NewPlayer_InLobby_HasCorrectStateAndWelcome()
    {
        await EnsureConnectedAsync("Lobby", SessionJoinMode.Unauthenticated, TestContext.Current.CancellationToken);

        // Wait for welcome message
        await GameClient.Chat.WaitForMessageContainingAsync(WelcomeKeywords, TestTimings.WelcomeMessageTimeout);

        // Verify placement: should be in a lobby cabin
        var state = await GameClient.WaitForLocationAsync("^" + GameTestClient.CabinLocationPrefix, ct: TestContext.Current.CancellationToken);
        Log($"Player location: {state?.Location}");
        Assert.NotNull(state);

        // Verify welcome message was received
        var chatHistory = await GameClient.Chat.GetHistory(20);
        Assert.NotNull(chatHistory);
        LogChatHistory(chatHistory);

        var hasWelcomeMessage = chatHistory.Messages.Any(m =>
            WelcomeKeywords.Any(k => m.Message.Contains(k, StringComparison.OrdinalIgnoreCase)));

        Assert.True(hasWelcomeMessage, "Should receive welcome message mentioning password or !login");

        await Exceptions.AssertNoExceptionsAsync("after joining lobby");
    }

    /// <summary>
    /// Verifies that !help command works for unauthenticated players
    /// and shows available commands.
    /// </summary>
    [Fact]
    public async Task Help_Command_WorksInLobby()
    {
        await EnsureConnectedAsync("Lobby", SessionJoinMode.Unauthenticated, TestContext.Current.CancellationToken);

        // Wait for welcome message first
        await GameClient.Chat.WaitForMessageContainingAsync(WelcomeKeywords, TestTimings.WelcomeMessageTimeout);

        // Wait for "!login" specifically; it arrives after !help's own entry because
        // commands are sent one by one.
        string[] helpResponseKeywords = { "!login" };

        // Send help command and wait for response
        await GameClient.Chat.SendAndWaitForResponseAsync("!help", helpResponseKeywords,
            ct: TestContext.Current.CancellationToken);

        var chatHistory = await GameClient.Chat.GetHistory(20);
        Assert.NotNull(chatHistory);
        LogChatHistory(chatHistory);

        // Verify the help output includes command descriptions (server response format: "!name: description")
        var hasHelpResponse = chatHistory.Messages.Any(m =>
            m.Message.Contains("!login", StringComparison.OrdinalIgnoreCase) &&
            m.Message.Contains(":", StringComparison.OrdinalIgnoreCase));

        Assert.True(hasHelpResponse, "Should receive help response listing !login command");

        await Exceptions.AssertNoExceptionsAsync("after help command");
    }

    /// <summary>
    /// Verifies that non-command chat messages from unauthenticated players
    /// are blocked and the player receives a "please authenticate" response.
    /// The server only allows !login and !help commands from lobby players.
    /// </summary>
    [Fact]
    public async Task UnauthenticatedPlayer_RegularChat_IsBlocked()
    {
        await EnsureConnectedAsync("Lobby", SessionJoinMode.Unauthenticated, TestContext.Current.CancellationToken);

        // Wait for welcome message first
        await GameClient.Chat.WaitForMessageContainingAsync(WelcomeKeywords, TestTimings.WelcomeMessageTimeout);

        // Snapshot chat before sending so we only match the server's response
        string[] blockedKeywords = { "authenticate", "!login" };

        // Non-command chat should be blocked; server responds with auth prompt
        await GameClient.Chat.SendAndWaitForResponseAsync("hello everyone", blockedKeywords,
            matchAll: true, ct: TestContext.Current.CancellationToken);

        var chatHistory = await GameClient.Chat.GetHistory(20);
        Assert.NotNull(chatHistory);
        LogChatHistory(chatHistory);

        var hasBlockedResponse = chatHistory.Messages.Any(m =>
            blockedKeywords.All(k => m.Message.Contains(k, StringComparison.OrdinalIgnoreCase)));

        Assert.True(hasBlockedResponse, "Should receive 'please authenticate' response when sending regular chat");

        await Exceptions.AssertNoExceptionsAsync("after blocked chat message");
    }

    /// <summary>
    /// Verifies that a player who sits in the lobby without authenticating
    /// is kicked after the auth timeout expires.
    /// Timeout is queried from the server's /auth endpoint.
    /// </summary>
    [Fact]
    [TestServer(Exclusive = true)]
    public async Task Login_TimesOut_KicksPlayer()
    {
        await PersistentSession.BreakSessionAsync();

        // Query the server's configured auth timeout
        var authStatus = await ServerApi.GetAuthStatus(TestContext.Current.CancellationToken);
        Assert.NotNull(authStatus);
        Assert.True(authStatus.Enabled, "Password protection should be enabled");

        var originalTimeout = authStatus.TimeoutSeconds;
        if (originalTimeout <= 0)
        {
            Log("Auth timeout is disabled (0), skipping test");
            return;
        }

        // Temporarily lower the timeout so we don't wait 2+ minutes
        const int testTimeout = 5;
        var setResult = await ServerApi.SetAuthTimeout(testTimeout, TestContext.Current.CancellationToken);
        Assert.NotNull(setResult);
        Assert.True(setResult.Success, $"Failed to set auth timeout: {setResult.Error}");
        Log($"Auth timeout lowered: {originalTimeout}s → {testTimeout}s");

        try
        {
            // Join the server WITHOUT auto-login (to sit in lobby)
            await Farmers.ConnectNewAsync(
                skipAutoLogin: true, ct: TestContext.Current.CancellationToken);

            // Wait for welcome message to confirm we're in the lobby
            await GameClient.Chat.WaitForMessageContainingAsync(WelcomeKeywords, TestTimings.WelcomeMessageTimeout);
            Log("In lobby, waiting for timeout kick...");

            // Wait for the timeout to expire + a buffer for server processing
            var kickTimeout = TimeSpan.FromSeconds(testTimeout + 10);
            var kicked = await PollingHelper.WaitUntilAsync(
                WaitName.Polling_PasswordProtection_KickedDisconnect,
                async () =>
                {
                    var s = await GameClient.GetState();
                    return s?.IsConnected != true;
                }, kickTimeout, cancellationToken: TestContext.Current.CancellationToken);

            Assert.True(kicked, $"Player should be disconnected after {testTimeout}s auth timeout");

            Log("Player was kicked after auth timeout");
        }
        finally
        {
            // Restore original timeout for other tests sharing this server
            await ServerApi.SetAuthTimeout(originalTimeout, TestContext.Current.CancellationToken);
            Log($"Auth timeout restored to {originalTimeout}s");
        }
    }

    /// <summary>
    /// Verifies that a returning player spawns in their own cabin after
    /// authenticating on reconnect (not stuck in lobby or in another player's cabin).
    /// Also verifies cabin ownership via the /cabins API.
    /// </summary>
    [Fact]
    public async Task ReturningPlayer_AfterAuth_SpawnsInOwnCabin()
    {
        // First session: Join and authenticate
        var client = await Farmers.ConnectNewAsync(
            breakSession: true, assertAuthenticated: true,
            ct: TestContext.Current.CancellationToken);

        var stateAfterAuth = await GameClient.GetState();
        Log($"Location after first auth: {stateAfterAuth?.Location}");

        // Reconnect-to-same-farmer needs customized farmhand persisted, not just slot released.
        await Farmers.DisconnectAndWaitForPersistenceAsync(client.FarmerName, TestContext.Current.CancellationToken);
        Log("Disconnected from first session");

        // Second session: Reconnect and authenticate
        var reconnect = await Farmers.ReconnectAsync(client.FarmerName,
            assertAuthenticated: true, ct: TestContext.Current.CancellationToken);
        Assert.True(reconnect.JoinResult.WasInLobby, "Should start in lobby on reconnect");

        // After auth, player should spawn in their cabin (vanilla behavior),
        // not remain stuck in the lobby. Poll because the post-auth warp
        // (passout message type 29) may still be in flight when join returns.
        var stateAfterReconnect = await GameClient.WaitForLocationAsync("^" + GameTestClient.CabinLocationPrefix, ct: TestContext.Current.CancellationToken);
        Log($"Location after reconnect and auth: {stateAfterReconnect?.Location}");

        Assert.NotNull(stateAfterReconnect);

        // Verify this is the player's OWN cabin via the server API.
        var ownedCabin = await WaitForCabinAssignedAsync(
            client.JoinResult.UniqueMultiplayerId, TestContext.Current.CancellationToken);

        Assert.NotNull(ownedCabin);
        Log($"Player's assigned cabin owner: id={ownedCabin.OwnerId}, type={ownedCabin.Type}");
        Assert.NotEqual("Lobby", ownedCabin.Type);

        await Exceptions.AssertNoExceptionsAsync("after returning player spawn");
    }

    /// <summary>
    /// Verifies that a player sitting in the lobby survives a day transition
    /// without being disconnected or stuck. The server blocks newDaySync and
    /// startNewDaySync messages to unauthenticated players, so they should
    /// remain in the lobby unaffected, and be able to authenticate afterward.
    /// </summary>
    [Fact]
    [TestServer(Exclusive = true)]
    public async Task LobbyPlayer_SurvivesDayTransition_CanAuthenticateAfter()
    {
        // Join the server WITHOUT auto-login (sit in lobby)
        await Farmers.ConnectNewAsync(
            breakSession: true, skipAutoLogin: true,
            ct: TestContext.Current.CancellationToken);

        // Wait for welcome message to confirm we're in the lobby
        await GameClient.Chat.WaitForMessageContainingAsync(WelcomeKeywords, TestTimings.WelcomeMessageTimeout);

        // Record current day
        var statusBefore = await ServerApi.GetStatus(TestContext.Current.CancellationToken);
        Assert.NotNull(statusBefore);
        var dayBefore = statusBefore.Day;
        var seasonBefore = statusBefore.Season;
        var yearBefore = statusBefore.Year;
        Log($"Before day transition: {seasonBefore} {dayBefore} Y{yearBefore}, Time {statusBefore.TimeOfDay}");

        // Trigger a day transition by setting time to pass-out which forces the day to advance.
        // The server's host bot auto-sleeps when no authenticated players are connected,
        // and the lobby player is excluded from sleep checks.
        var setTimeResult = await ServerApi.SetTime(TestTimings.PassOutTime, TestContext.Current.CancellationToken);
        Assert.NotNull(setTimeResult);
        Assert.True(setTimeResult.Success, $"SetTime failed: {setTimeResult.Error}");
        Log("Set time to 2600, waiting for day transition...");

        // Wait for day to change and server to finish the transition
        var dayChanged = await DayChange.WaitAsync(
            dayBefore, seasonBefore, yearBefore, TestContext.Current.CancellationToken);
        Assert.True(dayChanged, "Day should have advanced");

        var statusAfter = await ServerApi.GetStatus(TestContext.Current.CancellationToken);
        Log($"After day transition: {statusAfter?.Season} {statusAfter?.Day}, Time {statusAfter?.TimeOfDay}");

        // Verify the lobby player is still connected
        var stateAfterTransition = await GameClient.GetState();
        Assert.NotNull(stateAfterTransition);
        Assert.True(stateAfterTransition.IsConnected,
            "Lobby player should remain connected through day transition");
        Log($"Player still connected at: {stateAfterTransition.Location}");

        // Authenticate in-place from the lobby (no need to disconnect+rejoin)
        var preAuthLocation = stateAfterTransition.Location;
        await GameClient.SendChat($"!login {Lease!.Password}");

        // Wait for post-auth warp (location change confirms auth succeeded).
        // Use a longer timeout than AuthLoginAttemptTimeout (10s). The warp may take
        // longer after a day transition while the server finishes post-save processing.
        var authenticated = await GameClient.WaitForAuthWarpAsync(
            preAuthLocation!, TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        Assert.True(authenticated, "Lobby player should be able to authenticate after day transition");
        var stateAfterAuth = await GameClient.GetState();
        Log($"Location after post-transition auth: {stateAfterAuth?.Location}");

        await Exceptions.AssertNoExceptionsAsync("after day transition + auth");
    }

    /// <summary>
    /// Regression test for stale-cabin lookup failures after farmhand deletion.
    ///
    /// Reproduces the deletion-fan-out scenario: a player joins, gets a real cabin
    /// assigned, disconnects, and is deleted via the API. A subsequent fresh-player
    /// connect must successfully authenticate and warp to a real (non-lobby) cabin
    /// without hitting "Cabin lookup failed" — proving that the deleted cabin's
    /// name no longer lingers as a stale homeLocation in any surviving
    /// farmhandData entry.
    /// </summary>
    [Fact]
    [TestServer(Exclusive = true)]
    public async Task DeletedCabin_DoesNotPoisonSubsequentJoins()
    {
        // First session: authenticate. assertAuthenticated guarantees the auth
        // warp completed (post-auth location is a non-lobby cabin).
        var first = await Farmers.ConnectNewAsync(
            breakSession: true, assertAuthenticated: true,
            ct: TestContext.Current.CancellationToken);

        // Disconnect and delete via API — this is the path that calls DestroyCabin,
        // which fans out homeLocation cleanup to any surviving farmhandData entries.
        await Farmers.DisconnectAndWaitForSlotAsync(
            first.JoinResult.UniqueMultiplayerId, first.FarmerName, TestContext.Current.CancellationToken);
        var deleteResult = await ServerApi.WaitForFarmhandDeletedByNameAsync(
            first.FarmerName, ct: TestContext.Current.CancellationToken);
        Assert.True(deleteResult?.Success,
            $"Delete should succeed: {deleteResult?.Error ?? "timeout"}");
        Farmers.CreatedFarmers.RemoveAll(f => f.Uid == first.JoinResult.UniqueMultiplayerId);

        // Second session: fresh player must successfully authenticate and warp.
        // Before the fix, a surviving farmhandData entry could carry the deleted
        // cabin's name as homeLocation, causing FindPlayerCabin to return null
        // at !login time and kick the player. assertAuthenticated throws if the
        // post-auth warp doesn't complete, so its success here proves the fix.
        await Farmers.ConnectNewAsync(
            breakSession: true, assertAuthenticated: true,
            ct: TestContext.Current.CancellationToken);

        await Exceptions.AssertNoExceptionsAsync("after deleted-cabin re-join cycle");
    }

    /// <summary>
    /// Helper to log chat history at trace level (filtered renderer-side unless verbose).
    /// </summary>
    private void LogChatHistory(ChatHistoryResult chatHistory)
    {
        LogTrace($"Chat messages ({chatHistory.Messages.Count}):");
        foreach (var msg in chatHistory.Messages)
            LogTrace($"  {msg.Message}");
    }
}
