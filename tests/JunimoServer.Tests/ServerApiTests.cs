using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for server API endpoints.
/// Tests basic functionality, error handling, and edge cases.
/// </summary>
[Collection("Integration")]
public class ServerApiTests : IntegrationTestBase
{
    public ServerApiTests(IntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    #region GET /players

    /// <summary>
    /// Verifies /players returns an empty list when no players are connected.
    /// </summary>
    [Fact]
    public async Task GetPlayers_WhenNoPlayersConnected_ReturnsEmptyList()
    {
        await EnsureDisconnectedAsync();

        var response = await ServerApi.GetPlayers();

        Assert.NotNull(response);
        Assert.NotNull(response.Players);
        Assert.Empty(response.Players);
        Log("Verified: no players connected");
    }

    /// <summary>
    /// Verifies /players shows connected player after joining.
    /// </summary>
    [Fact]
    public async Task GetPlayers_WhenPlayerConnected_ShowsPlayer()
    {
        await EnsureDisconnectedAsync();

        var farmerName = GenerateFarmerName("Plyr");
        TrackFarmer(farmerName);

        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Wait for server to register the connection
        await Task.Delay(TestTimings.NetworkSyncDelay);

        var response = await ServerApi.GetPlayers();

        Assert.NotNull(response);
        Assert.NotNull(response.Players);
        Assert.NotEmpty(response.Players);

        var player = response.Players.FirstOrDefault(p =>
            p.Name.Equals(farmerName, StringComparison.OrdinalIgnoreCase));
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
    public async Task SetTime_WithValidValue_Succeeds()
    {
        var response = await ServerApi.SetTime(1200); // noon

        Assert.NotNull(response);
        Assert.True(response.Success, response.Error ?? "SetTime failed");
        Assert.Equal(1200, response.TimeOfDay);
        Log($"Time set to {response.TimeOfDay}");
    }

    /// <summary>
    /// Verifies setting time below valid range returns error.
    /// </summary>
    [Fact]
    public async Task SetTime_BelowValidRange_ReturnsError()
    {
        var response = await ServerApi.SetTime(500); // below 600

        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Contains("range", response.Error, StringComparison.OrdinalIgnoreCase);
        Log($"Correctly rejected: {response.Error}");
    }

    /// <summary>
    /// Verifies setting time above valid range returns error.
    /// </summary>
    [Fact]
    public async Task SetTime_AboveValidRange_ReturnsError()
    {
        var response = await ServerApi.SetTime(2700); // above 2600

        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Contains("range", response.Error, StringComparison.OrdinalIgnoreCase);
        Log($"Correctly rejected: {response.Error}");
    }

    #endregion

    #region DELETE /farmhands - error cases

    /// <summary>
    /// Verifies deleting non-existent farmhand returns appropriate error.
    /// </summary>
    [Fact]
    public async Task DeleteFarmhand_WhenNotExists_ReturnsNotFoundError()
    {
        var response = await ServerApi.DeleteFarmhand("NonExistentFarmer12345");

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
    public async Task GetOpenApiSpec_ReturnsValidJson()
    {
        var spec = await ServerApi.GetOpenApiSpec();

        Assert.NotNull(spec);
        Assert.NotEmpty(spec);

        // Verify it's valid JSON by attempting to parse
        var jsonDoc = System.Text.Json.JsonDocument.Parse(spec);
        Assert.NotNull(jsonDoc);

        // Verify basic OpenAPI structure
        var root = jsonDoc.RootElement;
        Assert.True(root.TryGetProperty("openapi", out _) || root.TryGetProperty("swagger", out _),
            "Should have openapi or swagger version property");
        Assert.True(root.TryGetProperty("paths", out _), "Should have paths property");
        Assert.True(root.TryGetProperty("info", out _), "Should have info property");

        Log("OpenAPI spec is valid JSON with expected structure");
    }

    #endregion

    #region GET /status - game state fields

    /// <summary>
    /// Verifies /status returns valid game state fields.
    /// </summary>
    [Fact]
    public async Task GetStatus_ReturnsValidGameState()
    {
        var status = await ServerApi.GetStatus();

        Assert.NotNull(status);
        Assert.True(status.IsOnline);

        // Verify game state fields have reasonable values
        Assert.True(status.Day >= 1 && status.Day <= 28, $"Day should be 1-28, got {status.Day}");
        Assert.True(status.Year >= 1, $"Year should be >= 1, got {status.Year}");
        Assert.Contains(status.Season, new[] { "spring", "summer", "fall", "winter" });
        Assert.True(status.TimeOfDay >= 600 && status.TimeOfDay <= 2600,
            $"TimeOfDay should be 600-2600, got {status.TimeOfDay}");

        Log($"Game state: Year {status.Year}, {status.Season} {status.Day}, {status.TimeOfDay}");
    }

    /// <summary>
    /// Verifies /status player count updates when player joins/leaves.
    /// </summary>
    [Fact]
    public async Task GetStatus_PlayerCount_UpdatesOnJoinLeave()
    {
        await EnsureDisconnectedAsync();

        // Check initial count
        var beforeStatus = await ServerApi.GetStatus();
        Assert.NotNull(beforeStatus);
        var initialCount = beforeStatus.PlayerCount;
        Log($"Initial player count: {initialCount}");

        // Join
        var farmerName = GenerateFarmerName("Cnt");
        TrackFarmer(farmerName);
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);
        await Task.Delay(TestTimings.NetworkSyncDelay);

        // Check count increased
        var duringStatus = await ServerApi.GetStatus();
        Assert.NotNull(duringStatus);
        Assert.True(duringStatus.PlayerCount > initialCount,
            $"Player count should increase after join: was {initialCount}, now {duringStatus.PlayerCount}");
        Log($"After join: {duringStatus.PlayerCount}");

        // Disconnect
        await GameClient.Exit();
        await GameClient.Wait.ForDisconnected(TestTimings.DisconnectedTimeout);
        await Task.Delay(TestTimings.DisconnectProcessingDelay);

        // Check count decreased
        var afterStatus = await ServerApi.GetStatus();
        Assert.NotNull(afterStatus);
        Assert.Equal(initialCount, afterStatus.PlayerCount);
        Log($"After disconnect: {afterStatus.PlayerCount}");
    }

    #endregion

    #region Concurrent requests

    /// <summary>
    /// Verifies the API can handle multiple concurrent requests.
    /// </summary>
    [Fact]
    public async Task Api_HandlesConcurrentRequests()
    {
        var tasks = new List<Task<ServerStatus?>>
        {
            ServerApi.GetStatus(),
            ServerApi.GetStatus(),
            ServerApi.GetStatus(),
            ServerApi.GetStatus(),
            ServerApi.GetStatus()
        };

        var results = await Task.WhenAll(tasks);

        Assert.All(results, status =>
        {
            Assert.NotNull(status);
            Assert.True(status.IsOnline);
        });

        Log($"Successfully handled {tasks.Count} concurrent requests");
    }

    #endregion
}
