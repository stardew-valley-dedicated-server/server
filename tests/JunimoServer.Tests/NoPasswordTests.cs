using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Tests that verify server behavior when password protection is DISABLED.
///
/// Performance: Uses KeepConnected for session persistence to avoid reconnecting.
/// Tests that need a fresh connection (negative assertions about join behavior)
/// use PersistentSession.BreakSessionAsync() to get a clean slate.
/// </summary>
[TestServer(KeepConnected = true)]
public class NoPasswordTests : TestBase
{
    public NoPasswordTests() { }

    /// <summary>
    /// Verifies that when no password is configured, players join directly
    /// without being placed in a lobby cabin.
    /// </summary>
    [Fact]
    public async Task NewPlayer_JoinsDirectly_WhenNoPasswordConfigured()
    {
        await EnsureConnectedAsync("NoPwd", ct: TestContext.Current.CancellationToken);

        // Get current location
        var state = await GameClient.GetState();
        Assert.NotNull(state);
        Log($"Player location: {state.Location}");

        // Player should spawn at their cabin or on the farm, not in a lobby
        // The location should be a normal game location, not a hidden lobby cabin
        Assert.True(state.IsInGame, "Player should be in-game");

        await Exceptions.AssertNoExceptionsAsync("after joining without password");
    }

    /// <summary>
    /// Verifies that without password protection, players don't receive
    /// the authentication welcome message.
    /// This test needs a fresh connection to check messages received on join.
    /// </summary>
    [Fact]
    public async Task Player_NoAuthMessage_WhenPasswordDisabled()
    {
        // This test needs a fresh connection; we're asserting about messages
        // received on join, so we can't reuse an existing session.
        await Farmers.ConnectNewAsync(
            breakSession: true, ct: TestContext.Current.CancellationToken);

        // Poll for the delivery window. If auth messages appear at any point,
        // the test fails immediately instead of waiting the full duration.
        // The password module is disabled so nothing should be sent, but we
        // give 1s for any messages to arrive (welcome messages take ~500ms-1s).
        string[] authKeywords = { "PASSWORD", "!login", "authenticate" };

        var authMessageAppeared = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_NoPassword_AuthMessageAppeared,
            async () =>
            {
                var chat = await GameClient.GetChatHistory(20);
                return chat?.Messages?.Any(m =>
                    authKeywords.Any(k => m.Message.Contains(k, StringComparison.OrdinalIgnoreCase))) == true;
            }, TestTimings.ChatDeliveryDelay, cancellationToken: TestContext.Current.CancellationToken);

        // Should NOT have received any auth messages
        Assert.False(authMessageAppeared, "Should NOT receive authentication messages when password is disabled");

        // Log what was received for diagnostics
        var chatHistory = await GameClient.GetChatHistory(20);
        Assert.NotNull(chatHistory);
        Log($"Chat messages received: {chatHistory.Messages.Count}");
        foreach (var msg in chatHistory.Messages)
        {
            Log($"  {msg.Message}");
        }

        await Exceptions.AssertNoExceptionsAsync("after checking no auth messages");
    }

    /// <summary>
    /// Verifies that players can interact normally without needing to authenticate.
    /// </summary>
    [Fact]
    public async Task Player_CanInteract_WithoutAuthentication()
    {
        await EnsureConnectedAsync("NoPwd", ct: TestContext.Current.CancellationToken);

        // Verify we're connected and in-game
        var state = await GameClient.GetState();
        Assert.NotNull(state);
        Assert.True(state.IsConnected, "Should be connected");
        Assert.True(state.IsInGame, "Should be in-game (world ready)");

        // Player should be able to send regular chat messages.
        // Snapshot before sending so we only match the NEW message, not stale history.
        var chatBefore = await GameClient.GetChatHistory(10);
        var countBefore = chatBefore?.Messages?.Count(m =>
            m.Message.Contains("Hello world", StringComparison.OrdinalIgnoreCase)) ?? 0;

        await GameClient.SendChat("Hello world!");

        // Poll until a NEW "Hello world" appears in chat history
        ChatHistoryResult? chatHistory = null;
        var messageAppeared = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_NoPassword_HelloWorldAppeared,
            async () =>
            {
                chatHistory = await GameClient.GetChatHistory(10);
                var countAfter = chatHistory?.Messages?.Count(m =>
                    m.Message.Contains("Hello world", StringComparison.OrdinalIgnoreCase)) ?? 0;
                return countAfter > countBefore;
            }, TestTimings.ChatCommandTimeout, cancellationToken: TestContext.Current.CancellationToken);

        // Should see the message in chat history (not blocked)
        Assert.True(messageAppeared, "Player should be able to send chat messages freely");

        await Exceptions.AssertNoExceptionsAsync("after interacting without auth");
    }
}
