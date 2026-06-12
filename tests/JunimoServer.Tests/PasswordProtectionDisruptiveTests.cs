using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Password protection tests that deliberately disconnect the client.
/// These tests call PersistentSession.BreakSessionAsync() in the original KeepConnected class,
/// destroying the persistent session they pay serialization cost for.
/// Extracted to a non-KeepConnected class so they run in parallel with the
/// remaining KeepConnected tests.
///
/// Auth state (FailedAttempts) is per-player on the server
/// (ConcurrentDictionary keyed by player ID), so concurrent wrong-password
/// attempts from different players don't interfere.
/// </summary>
[TestServer(Password = "test-password-123")]
public class PasswordProtectionDisruptiveTests : TestBase
{
    private static readonly string[] WelcomeKeywords = { "PASSWORD", "!login" };
    private static readonly string[] FailureKeywords =
    {
        "incorrect",
        "invalid",
        "wrong",
        "failed",
    };

    public PasswordProtectionDisruptiveTests() { }

    /// <summary>
    /// Verifies that sending !login with an incorrect password
    /// fails and the player remains in the lobby.
    /// </summary>
    [Fact]
    public async Task Login_WithWrongPassword_Fails()
    {
        // Join the server WITHOUT auto-login (to test wrong password manually)
        await Farmers.ConnectNewAsync(
            skipAutoLogin: true,
            ct: TestContext.Current.CancellationToken
        );

        // Get initial location (should be lobby)
        var stateBefore = await GameClient.GetState();
        var lobbyLocation = stateBefore?.Location;
        Log($"Initial location: {lobbyLocation}");

        // Wait for welcome message to appear
        await GameClient.Chat.WaitForMessageContainingAsync(
            WelcomeKeywords,
            TestTimings.WelcomeMessageTimeout
        );

        // Send login with wrong password
        await GameClient.Chat.Send("!login wrongpassword123");

        // Wait for failure message
        var chatHistory = await GameClient.Chat.WaitForMessageContainingAsync(
            FailureKeywords,
            TestTimings.ChatCommandTimeout
        );

        Assert.NotNull(chatHistory);

        var hasFailureMessage = chatHistory.Messages.Any(m =>
            FailureKeywords.Any(k => m.Message.Contains(k, StringComparison.OrdinalIgnoreCase))
        );

        Assert.True(hasFailureMessage, "Should receive authentication failure message");

        // Verify player is still in the same location (lobby)
        var stateAfter = await GameClient.GetState();
        Assert.Equal(lobbyLocation, stateAfter?.Location);
        Log($"Player still in lobby: {stateAfter?.Location}");

        await Exceptions.AssertNoExceptionsAsync("after failed login");
    }

    /// <summary>
    /// Verifies that exceeding max login attempts kicks the player.
    /// Max attempts is queried from the server's /auth endpoint.
    /// </summary>
    [Fact]
    public async Task Login_ExceedsMaxAttempts_KicksPlayer()
    {
        // Query the server's configured max attempts instead of hardcoding
        var authStatus = await ServerApi.GetAuthStatus(TestContext.Current.CancellationToken);
        Assert.NotNull(authStatus);
        Assert.True(authStatus.Enabled, "Password protection should be enabled");
        var maxAttempts = authStatus.MaxAttempts;
        Assert.True(maxAttempts > 0, $"MaxAttempts should be > 0, got {maxAttempts}");
        Log($"Server max login attempts: {maxAttempts}");

        // Join the server WITHOUT auto-login (to test manual wrong passwords)
        var client = await Farmers.ConnectNewAsync(
            skipAutoLogin: true,
            ct: TestContext.Current.CancellationToken
        );

        // Wait for welcome message first
        await GameClient.Chat.WaitForMessageContainingAsync(
            WelcomeKeywords,
            TestTimings.WelcomeMessageTimeout
        );

        // Send wrong password maxAttempts times.
        for (int i = 1; i <= maxAttempts; i++)
        {
            Log($"Sending wrong password attempt {i}/{maxAttempts}");

            if (i < maxAttempts)
            {
                var gotResponse = await GameClient.Chat.SendAndWaitForResponseAsync(
                    $"!login wrongpassword{i}",
                    FailureKeywords,
                    ct: TestContext.Current.CancellationToken
                );
                Log($"Attempt {i} server response received: {gotResponse}");
            }
            else
            {
                // Final attempt. Server kicks without sending a chat response,
                // so we can't wait for one. Instead, verify the server saw our
                // player BEFORE sending, then send and poll for disconnect.
                var playersBefore = await ServerApi.GetPlayers(
                    TestContext.Current.CancellationToken
                );
                var ourPlayer = playersBefore?.Players?.FirstOrDefault(p =>
                    p.Name?.Contains(client.FarmerName, StringComparison.OrdinalIgnoreCase) == true
                );
                Log(
                    $"Server player count before final attempt: {playersBefore?.Players?.Count ?? -1}, "
                        + $"our player found: {ourPlayer != null} (name={ourPlayer?.Name})"
                );

                await GameClient.Chat.Send($"!login wrongpassword{i}");
                Log("Final attempt sent, waiting for disconnect...");
            }
        }

        // Player should be kicked. Poll for disconnected state.
        Log("Waiting for kick/disconnect...");
        var pollCount = 0;
        var kicked = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_PasswordProtectionDisruptive_KickedDisconnect,
            async () =>
            {
                var s = await GameClient.GetState();
                pollCount++;
                if (pollCount <= 3 || pollCount % 10 == 0)
                    Log(
                        $"Poll #{pollCount}: IsConnected={s?.IsConnected}, Location={s?.Location}, IsInGame={s?.IsInGame}"
                    );
                return s?.IsConnected != true;
            },
            TestTimings.DisconnectedTimeout,
            cancellationToken: TestContext.Current.CancellationToken
        );

        if (!kicked)
        {
            var finalState = await GameClient.GetState();
            Log(
                $"KICK FAILED: client state: IsConnected={finalState?.IsConnected}, "
                    + $"Location={finalState?.Location}, IsInGame={finalState?.IsInGame}"
            );

            var serverPlayers = await ServerApi.GetPlayers(TestContext.Current.CancellationToken);
            Log(
                $"KICK FAILED: server players: {serverPlayers?.Players?.Count ?? -1} "
                    + $"[{string.Join(", ", serverPlayers?.Players?.Select(p => p.Name) ?? Array.Empty<string>())}]"
            );

            var chatHistory = await GameClient.Chat.GetHistory(20);
            if (chatHistory?.Messages != null)
            {
                Log($"KICK FAILED: last {chatHistory.Messages.Count} chat messages:");
                foreach (var msg in chatHistory.Messages.TakeLast(10))
                    Log($"  {msg.Message}");
            }
        }

        Assert.True(kicked, "Player should be disconnected after exceeding max attempts");

        Log("Player was kicked after exceeding max login attempts");
    }
}
