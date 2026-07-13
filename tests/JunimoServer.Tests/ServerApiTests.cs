using System.Net.WebSockets;
using System.Text;
using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for server API endpoints.
/// Tests basic functionality, error handling, and edge cases.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Artifacts = false)]
public class ServerApiTests : TestBase
{
    public ServerApiTests() { }

    #region GET /players

    /// <summary>
    /// Verifies /players returns an empty list when no players are connected.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0, Exclusive = true)]
    public async Task GetPlayers_WhenNoPlayersConnected_ReturnsEmptyList()
    {
        await Connect.EnsureDisconnectedAsync();

        // Wait for server to report no players (previous test cleanup may still be processing)
        var noPlayers = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_ServerApi_NoPlayersConnected,
            async () =>
            {
                var players = await ServerApi.GetPlayers(TestContext.Current.CancellationToken);
                return players?.Players?.Count == 0;
            },
            // Waits for a prior test's cleanup to drain /players to 0 (cross-test lag).
            TestTimings.ServerReadyBetweenTests,
            cancellationToken: TestContext.Current.CancellationToken
        );
        Assert.True(noPlayers, "Server should have no players connected");
        Log("Verified: no players connected");
    }

    /// <summary>
    /// Verifies /players shows connected player after joining.
    /// </summary>
    [Fact]
    [TestServer(Artifacts = true)]
    public async Task GetPlayers_WhenPlayerConnected_ShowsPlayer()
    {
        var client = await Farmers.ConnectNewAsync(ct: TestContext.Current.CancellationToken);

        // Poll until player appears in the /players API
        PlayersResponse? response = null;
        await ServerApi.WaitForPlayerByNameAsync(
            client.FarmerName,
            ct: TestContext.Current.CancellationToken
        );
        response = await ServerApi.GetPlayers(TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.NotNull(response.Players);
        Assert.NotEmpty(response.Players);

        var player = response.Players.FirstOrDefault(p =>
            p.Name.Equals(client.FarmerName, StringComparison.OrdinalIgnoreCase)
        );
        Assert.NotNull(player);
        Assert.True(player.IsOnline);
        Assert.True(player.Id != 0);

        Log($"Found connected player: {player.Name} (ID: {player.Id})");
    }

    #endregion

    #region POST /time

    /// <summary>
    /// Verifies setting valid game time succeeds.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task SetTime_WithValidValue_Succeeds()
    {
        var response = await ServerApi.SetTime(
            TestTimings.Noon,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(response);
        Assert.True(response.Success, response.Error ?? "SetTime failed");
        Assert.Equal(TestTimings.Noon, response.TimeOfDay);
        Log($"Time set to {response.TimeOfDay}");
    }

    /// <summary>
    /// Verifies setting time outside valid range (600-2600) returns error.
    /// </summary>
    [Theory]
    [InlineData(500, "below valid range")]
    [InlineData(2700, "above valid range")]
    [TestServer(Clients = 0)]
    public async Task SetTime_OutOfRange_ReturnsError(int timeValue, string description)
    {
        var response = await ServerApi.SetTime(timeValue, TestContext.Current.CancellationToken);

        Assert.NotNull(response);
        Assert.False(response.Success, $"Expected failure for {description} ({timeValue})");
        Assert.NotNull(response.Error);
        Assert.Contains("range", response.Error, StringComparison.OrdinalIgnoreCase);
        Log($"Correctly rejected {description} ({timeValue}): {response.Error}");
    }

    #endregion

    #region DELETE /farmhands - error cases

    /// <summary>
    /// Verifies deleting a non-existent farmhand by name returns a not-found error.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task DeleteFarmhandByName_WhenNotExists_ReturnsNotFoundError()
    {
        var response = await ServerApi.DeleteFarmhandByName(
            "NonExistentFarmer12345",
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Contains("not found", response.Error, StringComparison.OrdinalIgnoreCase);
        Log($"Correctly rejected: {response.Error}");
    }

    /// <summary>
    /// Verifies deleting a non-existent farmhand by playerId returns a not-found error.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task DeleteFarmhandById_WhenNotExists_ReturnsNotFoundError()
    {
        var response = await ServerApi.DeleteFarmhandById(
            999999999L,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Contains("not found", response.Error, StringComparison.OrdinalIgnoreCase);
        Log($"Correctly rejected: {response.Error}");
    }

    /// <summary>
    /// Verifies granting admin to a non-existent player by name returns a not-found error.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task GrantAdminByName_WhenNotExists_ReturnsNotFoundError()
    {
        var response = await ServerApi.GrantAdminByName(
            "NonExistentAdmin12345",
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Contains("not found", response.Error, StringComparison.OrdinalIgnoreCase);
        Log($"Correctly rejected: {response.Error}");
    }

    /// <summary>
    /// Verifies granting admin to a non-existent player by playerId returns a not-found error.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task GrantAdminById_WhenNotExists_ReturnsNotFoundError()
    {
        var response = await ServerApi.GrantAdminById(
            999999999L,
            TestContext.Current.CancellationToken
        );

        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Contains("not found", response.Error, StringComparison.OrdinalIgnoreCase);
        Log($"Correctly rejected: {response.Error}");
    }

    #endregion

    #region GET /swagger - OpenAPI spec

    /// <summary>
    /// Verifies the OpenAPI specification is valid JSON and has expected structure.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task GetOpenApiSpec_ReturnsValidJson()
    {
        var spec = await ServerApi.GetOpenApiSpec(TestContext.Current.CancellationToken);

        Assert.NotNull(spec);
        Assert.NotEmpty(spec);

        // Verify it's valid JSON by attempting to parse
        var jsonDoc = System.Text.Json.JsonDocument.Parse(spec);
        Assert.NotNull(jsonDoc);

        // Verify basic OpenAPI structure
        var root = jsonDoc.RootElement;
        Assert.True(
            root.TryGetProperty("openapi", out _) || root.TryGetProperty("swagger", out _),
            "Should have openapi or swagger version property"
        );
        Assert.True(root.TryGetProperty("paths", out _), "Should have paths property");
        Assert.True(root.TryGetProperty("info", out _), "Should have info property");

        Log("OpenAPI spec is valid JSON with expected structure");
    }

    #endregion

    #region Concurrent requests

    /// <summary>
    /// Verifies the API can handle multiple concurrent requests.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task Api_HandlesConcurrentRequests()
    {
        var ct = TestContext.Current.CancellationToken;
        var tasks = new List<Task<ServerStatus?>>
        {
            ServerApi.GetStatus(ct),
            ServerApi.GetStatus(ct),
            ServerApi.GetStatus(ct),
            ServerApi.GetStatus(ct),
            ServerApi.GetStatus(ct),
        };

        var results = await Task.WhenAll(tasks);

        Assert.All(
            results,
            status =>
            {
                Assert.NotNull(status);
                Assert.True(status.IsOnline);
            }
        );

        Log($"Successfully handled {tasks.Count} concurrent requests");
    }

    #endregion

    #region WebSocket

    /// <summary>
    /// Verifies WebSocket connection can be established.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task WebSocket_CanConnect()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = new ClientWebSocket();
        var wsUrl = ServerApi.GetWebSocketUrl();
        Log($"Connecting to WebSocket: {wsUrl}");

        await ws.ConnectAsync(new Uri(wsUrl), ct);

        Assert.Equal(WebSocketState.Open, ws.State);
        Log("WebSocket connection established");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test complete", ct);
    }

    /// <summary>
    /// Verifies WebSocket ping/pong heartbeat works.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0)]
    public async Task WebSocket_PingPong()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ws = new ClientWebSocket();
        var wsUrl = ServerApi.GetWebSocketUrl();

        await ws.ConnectAsync(new Uri(wsUrl), ct);
        Assert.Equal(WebSocketState.Open, ws.State);

        // Send ping
        var pingMessage = "{\"type\":\"ping\"}";
        var pingBytes = Encoding.UTF8.GetBytes(pingMessage);
        await ws.SendAsync(new ArraySegment<byte>(pingBytes), WebSocketMessageType.Text, true, ct);
        Log("Sent ping");

        // Receive pong
        var buffer = new byte[1024];
        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
        var response = Encoding.UTF8.GetString(buffer, 0, result.Count);

        Assert.Contains("pong", response);
        Log($"Received pong: {response}");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Test complete", ct);
    }

    /// <summary>
    /// Verifies chat_send message via WebSocket is delivered to the game.
    /// Connects a player, sends a message via WebSocket, and verifies it appears in chat history.
    /// </summary>
    [Fact]
    [TestServer(Artifacts = true)]
    public async Task WebSocket_ChatSend_MessageAppearsInGame()
    {
        // Connect a player to receive the chat message
        await Farmers.ConnectNewAsync(ct: TestContext.Current.CancellationToken);

        // Send message via WebSocket API
        var ct = TestContext.Current.CancellationToken;
        using var ws = new ClientWebSocket();
        var wsUrl = ServerApi.GetWebSocketUrl();
        await ws.ConnectAsync(new Uri(wsUrl), ct);
        Assert.Equal(WebSocketState.Open, ws.State);

        var testMessage = $"WS-Test-{DateTime.UtcNow.Ticks % 10000}";
        var chatJson =
            $"{{\"type\":\"chat_send\",\"payload\":{{\"author\":\"API\",\"message\":\"{testMessage}\"}}}}";
        var chatBytes = Encoding.UTF8.GetBytes(chatJson);
        await ws.SendAsync(new ArraySegment<byte>(chatBytes), WebSocketMessageType.Text, true, ct);
        Log($"Sent chat_send message: {testMessage}");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", ct);

        // Poll until the message appears in-game chat history
        ChatHistoryResult? chatHistory = null;
        var messageDelivered = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_ServerApi_ChatMessageDelivered,
            async () =>
            {
                chatHistory = await GameClient.GetChatHistory(20);
                return chatHistory?.Messages?.Any(m =>
                        m.Message.Contains(testMessage, StringComparison.OrdinalIgnoreCase)
                    ) == true;
            },
            TestTimings.ChatCommandTimeout,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.NotNull(chatHistory);
        Log($"Chat history ({chatHistory.Messages.Count} messages):");
        foreach (var msg in chatHistory.Messages)
        {
            Log($"  {msg.Message}");
        }

        Assert.True(
            messageDelivered,
            $"WebSocket chat message '{testMessage}' should appear in game chat"
        );

        Log("WebSocket chat message successfully delivered to game");
    }

    #endregion
}
