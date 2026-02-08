using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for player tracking and server status accuracy.
/// Verifies that the server API correctly reflects connected players and game world state.
/// </summary>
[Collection("Integration")]
public class PlayerTrackingTests : IntegrationTestBase
{
    public PlayerTrackingTests(IntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output) { }

    /// <summary>
    /// Verifies that GET /status returns valid game world data when the server is running.
    /// The server should report a farm name, a valid season/day/year, and a game time.
    /// </summary>
    [Fact]
    public async Task ServerStatus_HasValidGameWorldData()
    {
        var status = await ServerApi.GetStatus();

        Assert.NotNull(status);
        Assert.True(status.IsOnline, "Server should be online");

        // Farm name should be set
        Assert.False(string.IsNullOrEmpty(status.FarmName), "FarmName should not be empty");

        // Validate game state fields
        AssertHelpers.AssertValidGameState(status);

        LogDetail($"Game world: {status.FarmName} Farm, {status.Season} {status.Day}, Year {status.Year}, {status.TimeOfDay}");

        await AssertNoExceptionsAsync("at end of test");
    }

    /// <summary>
    /// Verifies that connecting a client updates both GET /players and GET /status,
    /// and that disconnecting removes the client from both endpoints.
    /// </summary>
    [Fact]
    public async Task ConnectedClient_TrackedInPlayersAndStatus()
    {
        // Ensure we're fully disconnected first
        await EnsureDisconnectedAsync();

        // Wait for server to report no players (previous test cleanup may still be processing)
        var noPlayers = await PollingHelper.WaitUntilAsync(async () =>
        {
            var players = await ServerApi.GetPlayers();
            return players?.Players?.Count == 0;
        }, TestTimings.FarmerDeleteTimeout);
        Assert.True(noPlayers, "Server should have no players connected initially");

        var initialStatus = await ServerApi.GetStatus();
        Assert.NotNull(initialStatus);
        Assert.Equal(0, initialStatus.PlayerCount);

        Log("Verified: no players connected initially");

        // Join the server and enter the game world
        var farmerName = GenerateFarmerName("Track");
        TrackFarmer(farmerName);
        var joinResult = await JoinWorldWithRetryAsync(farmerName);
        AssertJoinSuccess(joinResult);

        // Verify the client appears in /players
        var connectedPlayers = await ServerApi.GetPlayers();
        Assert.NotNull(connectedPlayers);
        Assert.NotEmpty(connectedPlayers.Players);

        var ourPlayer = connectedPlayers.Players.FirstOrDefault(p => p.Name == farmerName);
        Assert.NotNull(ourPlayer);
        Assert.True(ourPlayer.IsOnline, "Player should be marked as online");
        Log($"Found player in /players: {ourPlayer.Name} (ID: {ourPlayer.Id})");

        // Verify /status reflects the connection
        var connectedStatus = await ServerApi.GetStatus();
        Assert.NotNull(connectedStatus);
        Assert.True(connectedStatus.PlayerCount >= 1, $"PlayerCount should be >= 1, got {connectedStatus.PlayerCount}");
        Log($"Status playerCount: {connectedStatus.PlayerCount}");

        // Disconnect
        await DisconnectAsync();

        // Poll until the player is removed from /players
        var playerRemoved = await PollingHelper.WaitUntilAsync(async () =>
        {
            var players = await ServerApi.GetPlayers();
            return players?.Players?.All(p => !p.Name.Equals(farmerName, StringComparison.OrdinalIgnoreCase)) == true;
        }, TestTimings.FarmerDeleteTimeout);
        Assert.True(playerRemoved, "Player should be removed from /players after disconnect");
        Log("Verified: player removed from /players after disconnect");

        // Poll until /status reflects the disconnection
        ServerStatus? afterDisconnectStatus = null;
        var countZero = await PollingHelper.WaitUntilAsync(async () =>
        {
            afterDisconnectStatus = await ServerApi.GetStatus();
            return afterDisconnectStatus?.PlayerCount == 0;
        }, TestTimings.FarmerDeleteTimeout);
        Assert.True(countZero, "PlayerCount should return to 0 after disconnect");
        Log("Verified: playerCount back to 0 after disconnect");

        await AssertNoExceptionsAsync("at end of test");
    }

}
