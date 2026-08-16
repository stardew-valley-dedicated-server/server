using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E coverage for cabin relocation under the FarmhouseStack strategy: with
/// AllowCabinRelocation enabled (the default), players may move their cabin out of the
/// farmhouse stack to a real farm position via !cabin — the moved cabin becomes visible to
/// everyone and exits at its own door instead of the shared farmhouse door — and !cabin
/// reset sends it back into the stack. The AllowCabinRelocation=false rejection across all
/// strategies is covered by <see cref="CabinRelocationTests"/>.
///
/// Exclusive + a fresh game per test so the farm starts clean and assertions scope to the
/// connected farmers.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Exclusive = true)]
public class CabinStrategyFarmhouseStackTests : TestBase
{
    public CabinStrategyFarmhouseStackTests() { }

    public override async ValueTask DisposeAsync()
    {
        // Reset to the pooled config so the FarmhouseStack override (persisted into the
        // in-container settings file by /newgame) doesn't leak to a sibling class reusing
        // this server. Disconnect first: /newgame returns 409 while a client is connected.
        if (Lease != null)
        {
            try
            {
                try
                {
                    await DisconnectAsync();
                }
                catch (Exception ex)
                {
                    LogWarning(
                        $"Primary disconnect during cleanup failed (may not be connected): {ex.Message}"
                    );
                }

                // DisconnectAsync settles the client only
                // (disconnect-settles-client-not-server): gate on server-side removal so
                // the reset /newgame can't 409 against a still-registered player and
                // needlessly retire a healthy server through the catch below. Cheap here —
                // the test bodies already gate their own uids, so this resolves instantly
                // unless a body failed before its gate.
                if (!await ServerApi.WaitForAllPlayersRemovedAsync())
                {
                    LogWarning(
                        "Players still registered server-side after disconnect; the reset may 409."
                    );
                }
                await ResetServerToPooledConfigAsync();
            }
            catch (Exception ex)
            {
                // A failed reset leaves a FarmhouseStack server (settings file included)
                // pooled under the default config hash. Retire it.
                LogWarning($"Server reset failed during cleanup ({ex.Message}); retiring server.");
                Lease.Managed.PoisonServer(
                    $"Cleanup reset to pooled config failed: {ex.Message}",
                    ManagedServer.PoisonReasonCode.TestRetiredServer
                );
            }
        }
        await base.DisposeAsync();
    }

    /// <summary>
    /// Two farmhands each move their cabin out of the farmhouse stack via !cabin: both
    /// cabins become visible at distinct real positions (mutually visible — under
    /// FarmhouseStack there is no per-peer relocation, so a visible cabin is visible to
    /// everyone), and both intents are recorded. Then one farmhand runs !cabin reset and
    /// its cabin returns to the hidden stack with the intent cleared — the moved-out
    /// exemption must not survive a reset.
    /// </summary>
    [Fact]
    public async Task FarmhouseStack_RelocationEnabled_TwoPlayersMoveOut_AndResetReturns()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "FarmhouseStack");

        var clientA = await Farmers.ConnectNewAsync(ct: ct);
        var ownerIdA = clientA.JoinResult.UniqueMultiplayerId;

        await using var farmerB = await Farmers.ConnectSecondFarmerAsync(ct: ct);

        // A moves out at the standard placement tile.
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);
        var aExpected = CabinPlacementHelper.ExpectedCabinTile;
        CabinInfoResponse? aMoved = null;
        var aOk = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinStrategy_OurCabinAssigned,
            async () =>
            {
                await GameClient.SendChat("!cabin");
                aMoved = await GetOurCabinAsync(ownerIdA, ct);
                return !aMoved.IsHidden && (aMoved.TileX, aMoved.TileY) == aExpected;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(aOk, "A's cabin did not move out of the farmhouse stack after !cabin");

        // B moves out at the second known-clear tile (footprints can't overlap).
        await CabinPlacementHelper.WarpAndClearFootprintAsync(
            farmerB.Client,
            CabinPlacementHelper.SecondFarmerTileX,
            CabinPlacementHelper.SecondFarmerTileY,
            ct
        );
        var bExpected = CabinPlacementHelper.ExpectedCabinTileFor(
            CabinPlacementHelper.SecondFarmerTileX,
            CabinPlacementHelper.SecondFarmerTileY
        );
        CabinInfoResponse? bMoved = null;
        var bOk = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinStrategy_OurCabinAssigned,
            async () =>
            {
                await farmerB.Client.SendChat("!cabin");
                bMoved = await GetOurCabinAsync(farmerB.Uid, ct);
                return !bMoved.IsHidden && (bMoved.TileX, bMoved.TileY) == bExpected;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(bOk, "B's cabin did not move out of the farmhouse stack after !cabin");

        // Both cabins visible at distinct real positions, both intents recorded.
        var cabins = await ServerApi.GetCabins(ct);
        Assert.NotNull(cabins);
        Assert.NotEqual((aMoved!.TileX, aMoved.TileY), (bMoved!.TileX, bMoved.TileY));
        Assert.Contains(ownerIdA, cabins.SavedPositionPlayerIds);
        Assert.Contains(farmerB.Uid, cabins.SavedPositionPlayerIds);
        Log($"A at ({aMoved.TileX},{aMoved.TileY}), B at ({bMoved.TileX},{bMoved.TileY})");

        // A resets: cabin returns to the hidden stack, intent cleared (so the warp
        // interceptors route A through the farmhouse door again). Resend is idempotent —
        // once hidden it replies "nothing to reset".
        CabinsResponse? snapshot = null;
        CabinInfoResponse? aAfterReset = null;
        var reset = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinReset_CabinHidden,
            async () =>
            {
                await GameClient.SendChat("!cabin reset");
                snapshot = await ServerApi.GetCabins(ct);
                aAfterReset = snapshot?.Cabins.FirstOrDefault(c => c.OwnerId == ownerIdA);
                return aAfterReset?.IsHidden == true
                    && snapshot?.SavedPositionPlayerIds.Contains(ownerIdA) == false;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(reset, "A's cabin did not return to the farmhouse stack after !cabin reset");

        // B's moved-out cabin is untouched by A's reset.
        var bAfter = snapshot!.Cabins.FirstOrDefault(c => c.OwnerId == farmerB.Uid);
        Assert.NotNull(bAfter);
        Assert.False(bAfter.IsHidden, "B's moved-out cabin must survive A's reset");
        Assert.Contains(farmerB.Uid, snapshot.SavedPositionPlayerIds);

        // Disconnect both before this class's DisposeAsync runs /newgame, and gate on
        // server-side removal: DisconnectAsync settles the client only
        // (disconnect-settles-client-not-server), so without the gate the reset /newgame
        // can 409 against a still-registered player.
        await farmerB.DisconnectAsync();
        await DisconnectAsync();
        var removed = await ServerApi.WaitForPlayersRemovedByIdAsync(
            new[] { ownerIdA, farmerB.Uid },
            ct: ct
        );
        Assert.True(removed, "both players should be removed server-side before the class reset");
        await Exceptions.AssertNoExceptionsAsync("after FarmhouseStack relocation and reset");
    }

    /// <summary>
    /// E2E gate for the FarmhouseStack exit-warp repoint, exercised through real door
    /// walks: a stacked player's cabin exit lands them at the main farmhouse's front door;
    /// after they move their cabin out via !cabin, the same exit lands them at their own
    /// cabin's door. Asserted on the warp the client's own location copy resolves (the
    /// repoint is a per-peer message rewrite — /cabins can't observe it) plus the actual
    /// location transitions.
    /// </summary>
    [Fact]
    public async Task FarmhouseStack_ExitWarp_RepointsAfterMoveOut()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "FarmhouseStack");

        var clientA = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = clientA.JoinResult.UniqueMultiplayerId;

        // A new farmhand spawns inside their cabin interior ("FarmHouse{guid}").
        var spawned = await GameClient.WaitForLocationAsync("^FarmHouse.+", ct: ct);
        Assert.True(
            spawned != null,
            "the new farmhand should spawn inside their cabin interior (FarmHouse{guid})"
        );

        // The authoritative main-farmhouse door tile the stacked exit must repoint to
        // (Game1.getFarm().GetMainFarmHouseEntry()), sourced from the server so the gate asserts
        // the exact tile — layout-agnostic — rather than merely "any on-map target".
        var cabinsBefore = await ServerApi.GetCabins(ct);
        Assert.NotNull(cabinsBefore?.MainFarmHouseEntry);
        var expectedFarmhouseDoor = (
            X: cabinsBefore!.MainFarmHouseEntry!.X,
            Y: cabinsBefore.MainFarmHouseEntry.Y
        );

        // Step 1 — walk out through the (stacked) cabin's exit: the warp must target the
        // main farmhouse's front door on the Farm. The repoint reaches the client as a
        // replication delta (the client re-derives interior warps locally from the hidden
        // building position while deserializing the introduction, clobbering the
        // introduction's own targets), and a delta's value stays invisible for the netcode
        // interpolation window (15 client ticks — 3s at CLIENT_TPS=5). Wait for the client's
        // own copy to converge on the EXACT farmhouse-door tile before walking; the
        // hidden-stack-derived target is negative.
        var farmhouseDoorOnClient = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinExitWarp_FarmhouseDoorOnClient,
            async () =>
            {
                var warps = await GameClient.Actions.GetLocationWarps(ct);
                var exit = warps?.Warps.FirstOrDefault(w => w.TargetName == "Farm");
                return exit != null
                    && exit.TargetX == expectedFarmhouseDoor.X
                    && exit.TargetY == expectedFarmhouseDoor.Y;
            },
            TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            farmhouseDoorOnClient,
            "the stacked cabin's exit warp should converge to the main farmhouse door "
                + $"({expectedFarmhouseDoor.X},{expectedFarmhouseDoor.Y}) on the client — a negative "
                + "or mismatched target means the client is still on its locally re-derived "
                + "hidden-stack warp, or the repoint targeted the wrong tile"
        );

        var stepOut = await GameClient.Actions.WalkOntoTile();
        Assert.True(stepOut?.Success == true, $"first step-out failed: {stepOut?.Error}");
        Assert.Equal("warp", stepOut!.Via);
        Assert.Equal("Farm", stepOut.TargetLocation);
        Assert.NotNull(await GameClient.WaitForLocationAsync("^Farm$", ct: ct));
        var farmhouseDoor = (X: stepOut.TargetX!.Value, Y: stepOut.TargetY!.Value);
        Assert.Equal(expectedFarmhouseDoor, farmhouseDoor);
        Log($"Stacked exit lands at farmhouse door ({farmhouseDoor.X},{farmhouseDoor.Y})");

        // Step 2 — move the cabin out via !cabin at the standard cleared footprint.
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);
        var expected = CabinPlacementHelper.ExpectedCabinTile;
        CabinInfoResponse? moved = null;
        var movedOk = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinStrategy_OurCabinAssigned,
            async () =>
            {
                await GameClient.SendChat("!cabin");
                moved = await GetOurCabinAsync(ownerId, ct);
                return !moved.IsHidden && (moved.TileX, moved.TileY) == expected;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(movedOk, "the cabin did not move out of the farmhouse stack after !cabin");

        // Step 3 — re-enter the player's cabin through real play: pressing the main
        // farmhouse's door puts them in the host-reserved FarmHouse, whose monitor ports
        // them home ("Can't enter main building, porting to your own cabin").
        var enter = await GameClient.Actions.WalkOntoTile(
            farmhouseDoor.X,
            farmhouseDoor.Y - 1,
            direction: 0
        );
        Assert.True(enter?.Success == true, $"farmhouse-door entry failed: {enter?.Error}");
        var backInside = await GameClient.WaitForLocationAsync("^FarmHouse.+", ct: ct);
        Assert.True(
            backInside != null,
            "the farmhouse monitor should port the player into their own cabin interior"
        );

        // Step 4 — walk out again: the exit warp must now target the player's OWN cabin
        // door (within the moved building's footprint), not the farmhouse door. Same
        // convergence wait as step 1: the move-out's warp rewrite rides a replication
        // delta, invisible until the interpolation window elapses.
        var ownDoorOnClient = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinExitWarp_OwnDoorOnClient,
            async () =>
            {
                var warps = await GameClient.Actions.GetLocationWarps(ct);
                var exit = warps?.Warps.FirstOrDefault(w => w.TargetName == "Farm");
                return exit != null
                    && exit.TargetX >= moved!.TileX
                    && exit.TargetX <= moved.TileX + 4
                    && exit.TargetY >= moved.TileY
                    && exit.TargetY <= moved.TileY + 4;
            },
            TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            ownDoorOnClient,
            "the moved-out cabin's exit warp should converge to the cabin's own door on "
                + $"the client (cabin at ({moved!.TileX},{moved.TileY}))"
        );

        var stepOut2 = await GameClient.Actions.WalkOntoTile();
        Assert.True(stepOut2?.Success == true, $"second step-out failed: {stepOut2?.Error}");
        Assert.Equal("warp", stepOut2!.Via);
        Assert.Equal("Farm", stepOut2.TargetLocation);
        Assert.NotEqual(farmhouseDoor, (X: stepOut2.TargetX!.Value, Y: stepOut2.TargetY!.Value));
        Assert.True(
            stepOut2.TargetX >= moved!.TileX
                && stepOut2.TargetX <= moved.TileX + 4
                && stepOut2.TargetY >= moved.TileY
                && stepOut2.TargetY <= moved.TileY + 4,
            $"the moved-out cabin's exit must land at its own door (cabin at "
                + $"({moved.TileX},{moved.TileY})); landed at "
                + $"({stepOut2.TargetX},{stepOut2.TargetY})"
        );
        Assert.NotNull(await GameClient.WaitForLocationAsync("^Farm$", ct: ct));

        await DisconnectAsync();
        var removed = await ServerApi.WaitForPlayerRemovedByIdAsync(ownerId, ct: ct);
        Assert.True(removed, "the player should be removed server-side before the class reset");
        await Exceptions.AssertNoExceptionsAsync("after FarmhouseStack exit-warp repoint walks");
    }

    /// <summary>
    /// A corrupt one-way farmhand↔cabin link — the cabin's farmhandReference nulled while the
    /// farmhand's homeLocation still names it, the exact shape vanilla's join gate approves without
    /// repairing (NetWorldState.TryAssignFarmhandHome's homeLocation-resolves-to-Cabin early
    /// return) — is healed on rejoin: the join-time repair re-binds the cabin to its rightful
    /// owner, so the player rejoins with their cabin intact instead of hidden. Pre-fix the
    /// FarmhouseStack branch only logged a warning and the player joined cabin-hidden.
    /// </summary>
    [Fact]
    public async Task FarmhouseStack_OneWayCabinLink_RepairedOnRejoin()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "FarmhouseStack");

        var clientA = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = clientA.JoinResult.UniqueMultiplayerId;
        await ServerApi.WaitForFarmhandByNameAsync(
            clientA.FarmerName,
            requireCustomized: true,
            ct: ct
        );
        Assert.Equal(ownerId, (await GetOurCabinAsync(ownerId, ct)).OwnerId);

        // Disconnect (stable persisted snapshot), then null the cabin→farmhand back-link on the
        // now-offline slot so the one-way corruption survives to the next join.
        await Farmers.DisconnectAndWaitForPersistenceAsync(clientA.FarmerName, ct);
        var broke = await ServerApi.BreakCabinLink(ownerId, ct: ct);
        Assert.True(broke?.Success, $"break_cabin_link should succeed: {broke?.Error}");

        // The break landed: no cabin resolves to A (its interior owner is now null).
        var broken = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinLink_Broken,
            async () =>
            {
                var snapshot = await ServerApi.GetCabins(ct);
                return snapshot != null && snapshot.Cabins.All(c => c.OwnerId != ownerId);
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(broken, "the cabin should resolve to no owner after its back-link is nulled");

        // Rejoin: OnLocationIntroductionMessage's repair re-binds the cabin to A.
        await Farmers.ReconnectAsync(clientA.FarmerName, ct: ct);
        await ServerApi.WaitForFarmhandByNameAsync(
            clientA.FarmerName,
            requireCustomized: true,
            ct: ct
        );

        var healed = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinLink_Repaired,
            async () =>
            {
                var snapshot = await ServerApi.GetCabins(ct);
                return snapshot?.Cabins.Any(c => c.OwnerId == ownerId) == true;
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );
        Assert.True(
            healed,
            $"the one-way cabin link should be repaired on rejoin — a cabin owned by {ownerId} must "
                + "resolve again (pre-fix the back-link stayed null and the player joined cabin-hidden)"
        );

        await DisconnectAsync();
        var removed = await ServerApi.WaitForPlayerRemovedByIdAsync(ownerId, ct: ct);
        Assert.True(removed, "the player should be removed server-side before the class reset");
        await Exceptions.AssertNoExceptionsAsync("after FarmhouseStack one-way cabin-link repair");
    }

    /// <summary>
    /// The join-time repair must never STEAL a cabin owned by another farmhand. With a farmhand's
    /// home (corruptly) pointed at another player's cabin and its own back-link nulled, the repair
    /// declines — the other player's cabin ownership is left untouched — and the joiner simply
    /// joins with its cabin hidden.
    /// </summary>
    [Fact]
    public async Task FarmhouseStack_HomeOwnedByAnotherFarmhand_RepairDeclines()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "FarmhouseStack");

        var clientA = await Farmers.ConnectNewAsync(ct: ct);
        var ownerIdA = clientA.JoinResult.UniqueMultiplayerId;
        await ServerApi.WaitForFarmhandByNameAsync(
            clientA.FarmerName,
            requireCustomized: true,
            ct: ct
        );

        await using var farmerB = await Farmers.ConnectSecondFarmerAsync(ct: ct);
        await ServerApi.WaitForFarmhandByNameAsync(
            farmerB.FarmerName,
            requireCustomized: true,
            ct: ct
        );
        var ownerIdB = farmerB.Uid;

        // A offline (B stays connected), then corruptly point A's home at B's cabin and null A's
        // own back-link — the unrecoverable "home owned by another farmhand" shape.
        await Farmers.DisconnectAndWaitForPersistenceAsync(clientA.FarmerName, ct);
        var broke = await ServerApi.BreakCabinLink(
            ownerIdA,
            redirectHomeToOwnerId: ownerIdB,
            ct: ct
        );
        Assert.True(broke?.Success, $"break_cabin_link should succeed: {broke?.Error}");
        Assert.True(broke!.Redirected, "the break should have repointed A's home at B's cabin");

        // A rejoins: the repair sees A's home cabin owned by B and declines.
        await Farmers.ReconnectAsync(clientA.FarmerName, ct: ct);
        await ServerApi.WaitForFarmhandByNameAsync(
            clientA.FarmerName,
            requireCustomized: true,
            ct: ct
        );

        // B's cabin ownership is untouched — the repair did not steal it.
        var cabins = await ServerApi.GetCabins(ct);
        Assert.NotNull(cabins);
        Assert.Contains(cabins!.Cabins, c => c.OwnerId == ownerIdB);

        await farmerB.DisconnectAsync();
        await DisconnectAsync();
        var removed = await ServerApi.WaitForPlayersRemovedByIdAsync(
            new[] { ownerIdA, ownerIdB },
            ct: ct
        );
        Assert.True(removed, "both players should be removed server-side before the class reset");
        await Exceptions.AssertNoExceptionsAsync("after FarmhouseStack repair-declines");
    }

    /// <summary>
    /// Item-3 coverage: a FarmhouseStack player disconnects and reconnects, then walks out their
    /// cabin door — the exit warp must still repoint to the farmhouse door even though the rejoin
    /// re-sends the SAME warp target (an equal-value NetInt write emits no delta; the SetWarpsToFarm
    /// force-bounce is what makes the client converge anyway).
    /// </summary>
    [Fact]
    public async Task FarmhouseStack_ExitWarp_RepointsAfterRejoin()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "FarmhouseStack");

        var clientA = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = clientA.JoinResult.UniqueMultiplayerId;
        await ServerApi.WaitForFarmhandByNameAsync(
            clientA.FarmerName,
            requireCustomized: true,
            ct: ct
        );

        var cabins = await ServerApi.GetCabins(ct);
        Assert.NotNull(cabins?.MainFarmHouseEntry);
        var entry = (X: cabins!.MainFarmHouseEntry!.X, Y: cabins.MainFarmHouseEntry.Y);

        await Farmers.DisconnectAndWaitForPersistenceAsync(clientA.FarmerName, ct);
        await Farmers.ReconnectAsync(clientA.FarmerName, ct: ct);
        await ServerApi.WaitForFarmhandByNameAsync(
            clientA.FarmerName,
            requireCustomized: true,
            ct: ct
        );

        // Ensure the client is inside its own cabin: if the rejoin landed it on the Farm, press the
        // main farmhouse door and let the monitor port it home.
        var here = await GameClient.Actions.GetLocationWarps(ct);
        if (
            here?.LocationName == null
            || !here.LocationName.StartsWith("FarmHouse", StringComparison.Ordinal)
        )
        {
            var enter = await GameClient.Actions.WalkOntoTile(entry.X, entry.Y - 1, direction: 0);
            Assert.True(enter?.Success == true, $"farmhouse-door entry failed: {enter?.Error}");
        }
        Assert.NotNull(await GameClient.WaitForLocationAsync("^FarmHouse.+", ct: ct));

        // The rejoin's exit-warp repoint rides the SetWarpsToFarm force-delta; wait for the client's
        // own copy to converge on the exact farmhouse-door tile.
        var converged = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinExitWarp_FarmhouseDoorOnClient,
            async () =>
            {
                var warps = await GameClient.Actions.GetLocationWarps(ct);
                var exit = warps?.Warps.FirstOrDefault(w => w.TargetName == "Farm");
                return exit != null && exit.TargetX == entry.X && exit.TargetY == entry.Y;
            },
            TestTimings.NetworkSyncTimeout,
            cancellationToken: ct
        );
        Assert.True(
            converged,
            $"after rejoin the cabin exit warp should converge to the farmhouse door ({entry.X},"
                + $"{entry.Y}) — the force-delta must fire despite the equal-value rewrite"
        );

        var stepOut = await GameClient.Actions.WalkOntoTile();
        Assert.True(stepOut?.Success == true, $"step-out after rejoin failed: {stepOut?.Error}");
        Assert.Equal("warp", stepOut!.Via);
        Assert.Equal("Farm", stepOut.TargetLocation);
        Assert.Equal(entry, (X: stepOut.TargetX!.Value, Y: stepOut.TargetY!.Value));
        Assert.NotNull(await GameClient.WaitForLocationAsync("^Farm$", ct: ct));

        await DisconnectAsync();
        var removed = await ServerApi.WaitForPlayerRemovedByIdAsync(ownerId, ct: ct);
        Assert.True(removed, "the player should be removed server-side before the class reset");
        await Exceptions.AssertNoExceptionsAsync("after FarmhouseStack exit-warp rejoin");
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
