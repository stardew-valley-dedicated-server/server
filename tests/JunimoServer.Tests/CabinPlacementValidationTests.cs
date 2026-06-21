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
            catch (Exception ex)
            {
                LogWarning($"Server reset failed during cleanup: {ex.Message}");
            }
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
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );

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
            "Farm",
            CabinPlacementHelper.ExpectedCabinTile.X + 1,
            CabinPlacementHelper.ExpectedCabinTile.Y,
            clearObstacles: true
        );
        Assert.True(pot?.Success == true, $"PlacePot failed: {pot?.Error}");

        var rejected = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinPlacement_Rejected,
            // "Can't move cabin" (validator reply), not "Must be on Farm" (off-Farm
            // bail); resends absorb server-location lag.
            () => Chat.AssertResponseAsync("!cabin", "Can't move cabin"),
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );

        Assert.True(rejected, "Expected a 'Can't move cabin' rejection reply");

        // Reply alone only proves a message; confirm the move was actually blocked.
        var after = await GetOurCabinAsync(ownerId, ct);
        Assert.True(after.IsHidden, "Cabin should still be hidden after a rejected move");
        Assert.Equal((baseline.TileX, baseline.TileY), (after.TileX, after.TileY));

        await Exceptions.AssertNoExceptionsAsync("after rejected !cabin");
    }

    /// <summary>
    /// !cabin off the Farm is rejected with "Must be on Farm" and records no intent. A
    /// fresh farmhand spawns inside its cabin interior — already off-Farm — so no warp is
    /// needed; this asserts the location gate fires before any placement.
    /// </summary>
    [Fact]
    public async Task OffFarm_RejectsAndDoesNotMove()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;
        var baseline = await GetOurCabinAsync(ownerId, ct);

        var rejected = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinPlacement_Rejected,
            () => Chat.AssertResponseAsync("!cabin", "Must be on Farm"),
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(rejected, "Expected a 'Must be on Farm' rejection reply");

        var after = await GetOurCabinAsync(ownerId, ct);
        Assert.True(after.IsHidden, "Cabin should still be hidden after an off-Farm reject");
        Assert.Equal((baseline.TileX, baseline.TileY), (after.TileX, after.TileY));

        var cabins = await ServerApi.GetCabins(ct);
        Assert.NotNull(cabins);
        Assert.DoesNotContain(ownerId, cabins.SavedPositionPlayerIds);

        await Exceptions.AssertNoExceptionsAsync("after off-Farm !cabin");
    }

    /// <summary>
    /// Another connected, visible farmer standing in the footprint must reject with
    /// "another player is standing there" — and the host (skipped via IsMainPlayer)
    /// must NOT trigger that rejection on its own.
    ///
    /// First consumer of the second-farmer helper (plan 02): a second concurrently-
    /// connected farmer over its own lease. <c>await using</c> disposes it before this
    /// class's <c>DisposeAsync</c> runs <c>/newgame</c> (409s while any client is connected).
    /// </summary>
    [Fact]
    public async Task AnotherPlayerInFootprint_RejectsAndDoesNotMove()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var clientA = await Farmers.ConnectNewAsync(ct: ct);
        var ownerIdA = clientA.JoinResult.UniqueMultiplayerId;
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);
        var baseline = await GetOurCabinAsync(ownerIdA, ct);

        await using var farmerB = await Farmers.ConnectSecondFarmerAsync(ct: ct);

        // Stand B inside A's prospective footprint.
        var warpB = await farmerB.Client.Actions.Warp(
            "Farm",
            CabinPlacementHelper.ExpectedCabinTile.X + 1,
            CabinPlacementHelper.ExpectedCabinTile.Y
        );
        Assert.True(warpB?.Success == true, $"B warp failed: {warpB?.Error}");
        // WaitForLocationAsync returns non-null only when the location matched ^Farm$ within
        // the timeout; assert it so a B-not-settled failure surfaces here, not as an opaque
        // "no rejection" timeout below.
        var bArrived = await farmerB.Client.WaitForLocationAsync("^Farm$", ct: ct);
        Assert.True(
            bArrived is not null,
            "Farmer B did not reach the Farm before the rejection check"
        );

        var rejected = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinPlacement_Rejected,
            () => Chat.AssertResponseAsync("!cabin", "another player is standing there"),
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
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
