using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Tests that verify server behavior when password protection is DISABLED.
/// These tests use NoPasswordFixture which starts the server without SERVER_PASSWORD.
///
/// Performance: Uses session persistence for compatible tests to avoid reconnecting.
/// Tests that need a fresh connection (negative assertions about join behavior)
/// use EnsureDisconnectedAsync() instead.
/// </summary>
[Collection("Integration-NoPassword")]
public class NoPasswordTests : NoPasswordTestBase
{
    // Shared session state (persists across test instances in the same collection run)
    private static string? _sharedFarmerName;
    private static bool _sessionEstablished;

    public NoPasswordTests(NoPasswordFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    public override async Task DisposeAsync()
    {
        // base.DisposeAsync() is safe: _didConnect tracking ensures game client cleanup
        // only runs if THIS test instance actually called a connect method.
        // For session-reusing tests, _didConnect is false so base skips the expensive
        // Navigate("title") + disconnect + delay.
        await base.DisposeAsync();
    }

    #region Session Helpers

    /// <summary>
    /// Ensures a session is active, reusing the existing connection if possible.
    /// Only reconnects if the session was lost or never established.
    /// </summary>
    private async Task EnsureSessionAsync()
    {
        if (_sessionEstablished)
        {
            // Verify session is still valid
            try
            {
                var state = await GameClient.GetState();
                if (state?.IsConnected == true && state.IsInGame)
                {
                    LogDetail("Reusing existing session");
                    return;
                }
            }
            catch
            {
                // Session check failed, need to reconnect
            }

            LogDetail("Session lost, reconnecting...");
            _sessionEstablished = false;
        }

        // Establish new session
        _sharedFarmerName ??= GenerateFarmerName("NoPwd");

        await EnsureDisconnectedAsync();

        var joinResult = await JoinWorldWithRetryAsync(_sharedFarmerName);
        AssertJoinSuccess(joinResult);
        Log($"Session established for {_sharedFarmerName}");

        // Don't track the shared farmer in CreatedFarmers — we want to keep
        // it alive across tests. It will be cleaned up naturally when the
        // next test class disconnects.

        _sessionEstablished = true;
    }

    #endregion

    /// <summary>
    /// Verifies that when no password is configured, players join directly
    /// without being placed in a lobby cabin.
    /// </summary>
    [Fact]
    public async Task NewPlayer_JoinsDirectly_WhenNoPasswordConfigured()
    {
        await EnsureSessionAsync();

        // Get current location
        var state = await GameClient.GetState();
        Assert.NotNull(state);
        Log($"Player location: {state.Location}");

        // Player should spawn at their cabin or on the farm, not in a lobby
        // The location should be a normal game location, not a hidden lobby cabin
        Assert.True(state.IsInGame, "Player should be in-game");

        await AssertNoExceptionsAsync("after joining without password");
    }

    /// <summary>
    /// Verifies that without password protection, players don't receive
    /// the authentication welcome message.
    /// This test needs a fresh connection to check messages received on join.
    /// </summary>
    [Fact]
    public async Task Player_NoAuthMessage_WhenPasswordDisabled()
    {
        // This test needs a fresh connection — we're asserting about messages
        // received on join, so we can't reuse an existing session.
        await EnsureDisconnectedAsync();
        _sessionEstablished = false;

        var farmerName = GenerateFarmerName("NoAuth");
        TrackFarmer(farmerName);

        // Join the server
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Wait a bit for any messages that might be sent
        // Welcome messages are sent ~500ms-1s after join, so 1s is sufficient for negative assertion
        await Task.Delay(TestTimings.ChatDeliveryDelay);

        // Check chat history - should NOT have password/login messages
        var chatHistory = await GameClient.GetChatHistory(20);
        Assert.NotNull(chatHistory);

        Log($"Chat messages received: {chatHistory.Messages.Count}");
        foreach (var msg in chatHistory.Messages)
        {
            Log($"  {msg.Message}");
        }

        // Should NOT receive password protection messages
        var hasAuthMessage = chatHistory.Messages.Any(m =>
            m.Message.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
            m.Message.Contains("!login", StringComparison.OrdinalIgnoreCase) ||
            m.Message.Contains("authenticate", StringComparison.OrdinalIgnoreCase));

        Assert.False(hasAuthMessage, "Should NOT receive authentication messages when password is disabled");

        await AssertNoExceptionsAsync("after checking no auth messages");
    }

    /// <summary>
    /// Verifies that players can interact normally without needing to authenticate.
    /// </summary>
    [Fact]
    public async Task Player_CanInteract_WithoutAuthentication()
    {
        await EnsureSessionAsync();

        // Verify we're connected and in-game
        var state = await GameClient.GetState();
        Assert.NotNull(state);
        Assert.True(state.IsConnected, "Should be connected");
        Assert.True(state.IsInGame, "Should be in-game (world ready)");

        // Player should be able to send regular chat messages
        await GameClient.SendChat("Hello world!");

        // Poll until the message appears in chat history
        ChatHistoryResult? chatHistory = null;
        await PollingHelper.WaitUntilAsync(async () =>
        {
            chatHistory = await GameClient.GetChatHistory(10);
            return chatHistory?.Messages?.Any(m =>
                m.Message.Contains("Hello world", StringComparison.OrdinalIgnoreCase)) == true;
        }, TestTimings.ChatCommandTimeout);

        // Should see the message in chat history (not blocked)
        Assert.NotNull(chatHistory);

        var sentMessage = chatHistory.Messages.Any(m =>
            m.Message.Contains("Hello world", StringComparison.OrdinalIgnoreCase));

        Assert.True(sentMessage, "Player should be able to send chat messages freely");

        await AssertNoExceptionsAsync("after interacting without auth");
    }
}
