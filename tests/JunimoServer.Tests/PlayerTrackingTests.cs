using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Integration tests for player tracking and server status accuracy.
/// Verifies that the server API correctly reflects connected players and game world state.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly)]
public class PlayerTrackingTests : TestBase
{
    public PlayerTrackingTests() { }

    /// <summary>
    /// Verifies that GET /status returns valid game world data when the server is running.
    /// The server should report a farm name, a valid season/day/year, and a game time.
    /// </summary>
    [Fact]
    [TestServer(Clients = 0, Artifacts = false)]
    public async Task ServerStatus_HasValidGameWorldData()
    {
        var status = await ServerApi.GetStatus(TestContext.Current.CancellationToken);

        Assert.NotNull(status);
        Assert.True(status.IsOnline, "Server should be online");

        // Farm name should be set
        Assert.False(string.IsNullOrEmpty(status.FarmName), "FarmName should not be empty");

        // Validate game state fields
        AssertHelpers.AssertValidGameState(status);

        LogDetail($"Game world: {status.FarmName} Farm, {status.Season} {status.Day}, Year {status.Year}, {status.TimeOfDay}");
    }

    /// <summary>
    /// Verifies that connecting a client updates both GET /players and GET /status,
    /// and that disconnecting removes the client from both endpoints.
    /// </summary>
    [Fact]
    public async Task ConnectedClient_TrackedInPlayersAndStatus()
    {
        // Get the client (lazy)
        await GetClientAsync(TestContext.Current.CancellationToken);

        // Verify our farmer is not already in the player list
        var initialPlayers = await ServerApi.GetPlayers(TestContext.Current.CancellationToken);
        var farmerName = Farmers.GenerateName();
        Assert.True(initialPlayers?.Players?.All(p =>
            !p.Name.Equals(farmerName, StringComparison.OrdinalIgnoreCase)) != false,
            $"Farmer '{farmerName}' should not exist before test starts");

        // Join the server and enter the game world
        var client = await Farmers.ConnectNewAsync(farmerName: farmerName,
            ct: TestContext.Current.CancellationToken);

        // Verify the client appears in /players (poll until name syncs — this test
        // specifically verifies name-sync behavior, so name-based match is intentional).
        var playerFound = await ServerApi.WaitForPlayerByNameAsync(farmerName, ct: TestContext.Current.CancellationToken);

        Assert.True(playerFound, $"Player '{farmerName}' should appear in /players within timeout");
        var connectedPlayers = await ServerApi.GetPlayers(TestContext.Current.CancellationToken);
        Assert.NotNull(connectedPlayers);
        Assert.NotEmpty(connectedPlayers.Players);

        var ourPlayer = connectedPlayers.Players.FirstOrDefault(p =>
            p.Name.Equals(farmerName, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(ourPlayer);
        Assert.True(ourPlayer.IsOnline, "Player should be marked as online");
        Log($"Found player in /players: {ourPlayer.Name} (ID: {ourPlayer.Id})");

        // Verify /status reflects a connected player (at least our client is counted).
        // NOTE: We don't snapshot a baseline count because other tests on the shared
        // server may connect/disconnect concurrently, making any baseline unstable.
        var connectedStatus = await ServerApi.GetStatus(TestContext.Current.CancellationToken);
        Assert.NotNull(connectedStatus);
        Assert.True(connectedStatus.PlayerCount >= 1,
            $"PlayerCount should be >= 1 with our client connected (got {connectedStatus.PlayerCount})");
        Log($"Status playerCount: {connectedStatus.PlayerCount}");

        // Disconnect and wait for the server to release the farmhand slot
        await Farmers.DisconnectAndWaitForSlotAsync(client.JoinResult.UniqueMultiplayerId, farmerName, TestContext.Current.CancellationToken);
        Log("Verified: player removed from /players after disconnect");
    }

}
