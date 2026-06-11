using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E coverage for CabinPlacementValidator, which gates the !cabin command: it
/// validates the target footprint (bounds, building/terrain/object overlap, farmer
/// collision, door-front) before relocating and replies "Can't move cabin: ..." on
/// rejection. The validator is a pure helper with no E2E coverage otherwise.
///
/// Placement is deterministic: warpFarmer lands on the exact tile, and !cabin
/// relocates to farmer.Tile + (1,0) with no adjustment, so a warp to (x,y) means a
/// footprint of (x+1..x+5, y). Rejection is asserted on the positive chat reply plus
/// an unchanged /cabins snapshot — a "didn't move" poll has no positive edge.
///
/// Exclusive + a fresh game per test so the farm starts clean and assertions scope to
/// the single connected farmer.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Exclusive = true)]
public class CabinPlacementValidationTests : TestBase
{
    public CabinPlacementValidationTests() { }

    public override async ValueTask DisposeAsync()
    {
        // Reset to a clean default game so a moved cabin doesn't leak to a sibling class
        // reusing this server. Disconnect first: /newgame returns 409 while a client is
        // connected, and our tests stay connected through the body.
        if (Lease != null)
        {
            try
            {
                await DisconnectAsync();
                await CreateNewGameOnServerAsync(farmType: 0);
            }
            catch (Exception ex) { LogWarning($"Server reset failed during cleanup: {ex.Message}"); }
        }
        await base.DisposeAsync();
    }

    /// <summary>
    /// A clear in-bounds target passes validation: the cabin moves out of the hidden
    /// stack to farmer.Tile + (1,0).
    /// </summary>
    [Fact]
    public async Task ValidPlacement_MovesCabinToFarmerTilePlusOne()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);

        CabinInfoResponse? moved = null;
        var ok = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinPlacement_Moved,
            async () =>
            {
                // Resend each poll: the !cabin handler reads the server's view of the
                // farmer location, which can lag the client warp by a tick.
                await GameClient.SendChat("!cabin");
                moved = await GetOurCabinAsync(ownerId, ct);
                return !moved.IsHidden;
            }, TestTimings.CabinAssignmentTimeout, cancellationToken: ct);

        Assert.True(ok, "Cabin did not move out of the hidden stack after !cabin");
        Assert.Equal(CabinPlacementHelper.ExpectedCabinTile, (moved!.TileX, moved.TileY));

        await Exceptions.AssertNoExceptionsAsync("after valid !cabin");
    }

    /// <summary>
    /// An object inside the footprint fails validation: !cabin replies "Can't move
    /// cabin" and the cabin stays put. This exercises the same IsTileBuildable reject
    /// path as out-of-bounds/door-front, without depending on the farm's map geometry.
    /// </summary>
    [Fact]
    public async Task ObstacleInFootprint_RejectsAndDoesNotMove()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);
        var baseline = await GetOurCabinAsync(ownerId, ct);

        // Place a Garden Pot inside the just-cleared footprint so the validator's
        // terrain/object check rejects on a single, deterministic obstacle.
        var pot = await GameClient.Actions.PlacePot(
            "Farm", CabinPlacementHelper.ExpectedCabinTile.X + 1, CabinPlacementHelper.ExpectedCabinTile.Y,
            clearObstacles: true);
        Assert.True(pot?.Success == true, $"PlacePot failed: {pot?.Error}");

        var rejected = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinPlacement_Rejected,
            // "Can't move cabin" (validator reply), not "Must be on Farm" (off-Farm
            // bail); resends absorb server-location lag.
            () => Chat.AssertResponseAsync("!cabin", "Can't move cabin"),
            TestTimings.CabinAssignmentTimeout, cancellationToken: ct);

        Assert.True(rejected, "Expected a 'Can't move cabin' rejection reply");

        // Reply alone only proves a message; confirm the move was actually blocked.
        var after = await GetOurCabinAsync(ownerId, ct);
        Assert.True(after.IsHidden, "Cabin should still be hidden after a rejected move");
        Assert.Equal((baseline.TileX, baseline.TileY), (after.TileX, after.TileY));

        await Exceptions.AssertNoExceptionsAsync("after rejected !cabin");
    }

    /// <summary>
    /// Another connected, visible farmer standing in the footprint must reject with
    /// "another player is standing there" — and the host (skipped via IsMainPlayer)
    /// must NOT trigger that rejection on its own.
    ///
    /// Skipped: needs a second concurrently-connected farmer, which the shared
    /// connection helpers don't support (they bind to the single primary client). The
    /// body documents the contained mechanism — a second ConnectionHelper over a
    /// second lease — so enabling it later is mechanical.
    /// </summary>
    [Fact(Skip = "Needs a 2nd concurrently-connected farmer (first in suite); deferred — see body for approach")]
    public async Task AnotherPlayerInFootprint_RejectsAndDoesNotMove()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var clientA = await Farmers.ConnectNewAsync(ct: ct);
        var ownerIdA = clientA.JoinResult.UniqueMultiplayerId;
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);
        var baseline = await GetOurCabinAsync(ownerIdA, ct);

        // Second farmer over its own lease + ConnectionHelper (LAN; Steam not needed).
        var nameB = Farmers.GenerateName("FarmerB");
        // Scope leaseB so its client disconnects before this class's DisposeAsync runs
        // /newgame — that reset returns 409 while any client is still connected, and
        // ResourceLease.DisposeAsync would otherwise dispose leaseB too late.
        await using var leaseB = await LeaseClientAsync(ct);
        var connB = new ConnectionHelper(leaseB.Client, serverApi: ServerApi);
        var joinB = await connB.JoinWorldViaLanAsync(
            Lease!.ServerLanAddress, Lease.ServerLanPort, nameB, cancellationToken: ct);
        Farmers.TrackFarmer(nameB, joinB.UniqueMultiplayerId);

        // Stand B inside A's prospective footprint.
        var warpB = await leaseB.Client.Actions.Warp(
            "Farm", CabinPlacementHelper.ExpectedCabinTile.X + 1, CabinPlacementHelper.ExpectedCabinTile.Y);
        Assert.True(warpB?.Success == true, $"B warp failed: {warpB?.Error}");
        await leaseB.Client.WaitForLocationAsync("^Farm$", ct: ct);

        var rejected = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinPlacement_Rejected,
            () => Chat.AssertResponseAsync("!cabin", "another player is standing there"),
            TestTimings.CabinAssignmentTimeout, cancellationToken: ct);
        Assert.True(rejected, "Expected an 'another player is standing there' rejection");

        var after = await GetOurCabinAsync(ownerIdA, ct);
        Assert.True(after.IsHidden, "A's cabin should still be hidden after rejection");
        Assert.Equal((baseline.TileX, baseline.TileY), (after.TileX, after.TileY));

        await Exceptions.AssertNoExceptionsAsync("after farmer-collision !cabin");
    }

    #region Helpers

    private async Task<CabinInfoResponse> GetOurCabinAsync(long ownerId, CancellationToken ct)
    {
        var cabins = await ServerApi.GetCabins(ct);
        Assert.NotNull(cabins);
        var ours = cabins.Cabins.FirstOrDefault(c => c.OwnerId == ownerId);
        Assert.NotNull(ours);
        return ours;
    }

    #endregion
}
