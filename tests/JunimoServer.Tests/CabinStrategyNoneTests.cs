using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Tests that verify game creation with the "None" (vanilla) cabin strategy.
/// Extracted from FarmMapTypeTests to run on a separate server instance,
/// shortening the exclusive sequential chain.
///
/// Uses the same SharedAssembly server pool as FarmMapTypeTests. With 2+
/// pre-started instances, these exclusive tests run on a different instance
/// concurrently with the farm type Theory tests.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Priority = 90, Exclusive = true)]
public class CabinStrategyNoneTests : TestBase
{
    private bool _needsServerReset;

    public CabinStrategyNoneTests() { }

    public override async ValueTask DisposeAsync()
    {
        if (_needsServerReset && Lease != null)
        {
            try
            {
                // Disconnect the primary first: /newgame 409s while any client is connected.
                // The NewGame_* tests connect no client, but NoneStrategy_CabinMovesToFarmerTilePlusOne
                // leaves the primary connected — without this the reset would 409 and leak None
                // state. Tolerant: a no-op throw when never connected / already at title must
                // not block the reset.
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
                // needlessly retire a healthy server through the catch below.
                if (!await ServerApi.WaitForAllPlayersRemovedAsync())
                {
                    LogWarning(
                        "Players still registered server-side after disconnect; the reset may 409."
                    );
                }

                // Explicit-everything reset: every /newgame persists its config into the
                // in-container settings file, so the override values (None strategy,
                // maxPlayers) must be reset explicitly or they'd leak to the next reuser.
                await ResetServerToPooledConfigAsync();
            }
            catch (Exception ex)
            {
                // A failed reset leaves a None-strategy server (whose settings file also
                // says None) pooled under a CabinStack config hash. Retire it so the pool
                // boots a fresh instance instead of handing it to the next reuser.
                LogWarning($"Server reset failed during cleanup ({ex.Message}); retiring server.");
                Lease.Managed.PoisonServer(
                    $"Cleanup reset to pooled config failed: {ex.Message}",
                    ManagedServer.PoisonReasonCode.TestRetiredServer
                );
            }
        }
        _needsServerReset = false;
        await base.DisposeAsync();
    }

    /// <summary>
    /// Under None the cabin count is min(designated map positions, MaxPlayers), placed in full
    /// at creation; StartingCabins is ignored. startingCabins:1 is passed deliberately to
    /// prove it does NOT limit the count. The Standard farm's Paths layer has 7 designated
    /// positions and the test config's MaxPlayers exceeds 7, so exactly 7 cabins exist.
    /// </summary>
    [Fact]
    public async Task NewGame_NoneStrategy_IgnoresStartingCabins_PlacesAllDesignatedPositions()
    {
        LogSection("Testing None (vanilla) strategy places the full designated-position set");

        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", startingCabins: 1);

        Log($"Server ready: {Server.BaseUrl}");

        var cabinsResponse = await ServerApi.GetCabins(TestCt);

        Assert.NotNull(cabinsResponse);
        Assert.Equal("None", cabinsResponse.Strategy);
        // Exact count, not a bound: a tolerant bound would mask a placement regression where
        // vanilla places 0 and the mod backfills 1 (cabin-system invariant 9).
        Assert.Equal(7, cabinsResponse.TotalCount);
        Assert.True(
            cabinsResponse.Cabins.All(c => !c.IsHidden),
            "All None-strategy cabins must be at visible map positions"
        );

        Log($"Cabins created: {cabinsResponse.TotalCount} (strategy: {cabinsResponse.Strategy})");
    }

    /// <summary>
    /// MaxPlayers caps the None cabin count below the designated-position count: the honest
    /// player ceiling is min(positions, MaxPlayers), so maxPlayers:3 yields exactly 3 cabins
    /// even though the Standard farm has 7 designated positions.
    /// </summary>
    [Fact]
    public async Task NewGame_NoneStrategy_MaxPlayersCapsCabinCount()
    {
        LogSection("Testing None (vanilla) strategy MaxPlayers cap");

        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", maxPlayers: 3);

        Log($"Server ready: {Server.BaseUrl}");

        var cabinsResponse = await ServerApi.GetCabins(TestCt);

        Assert.NotNull(cabinsResponse);
        Assert.Equal("None", cabinsResponse.Strategy);
        Assert.Equal(3, cabinsResponse.TotalCount);

        Log($"Cabins created: {cabinsResponse.TotalCount} (strategy: {cabinsResponse.Strategy})");

        Assert.Equal(cabinsResponse.TotalCount, cabinsResponse.AvailableCount);
    }

    /// <summary>
    /// !cabin under None moves a visible cabin to farmer.Tile + (1,0): None has no hidden
    /// stack, so the move is between two real map positions. Proves the None happy path of
    /// the command (otherwise only exercised incidentally by the strategy-switch test).
    /// </summary>
    [Fact]
    public async Task NoneStrategy_CabinMovesToFarmerTilePlusOne()
    {
        LogSection("Testing !cabin under None (vanilla) strategy");

        _needsServerReset = true;
        // maxPlayers:4 caps the up-front placement at the 4 lowest-Order designated spots,
        // keeping the !cabin target footprint (CabinPlacementHelper) clear of cabins.
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", maxPlayers: 4);

        var ct = TestCt;
        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        // Baseline is map-derived under None — never hard-coded.
        var baseline = await GetOurCabinAsync(ownerId, ct);
        Assert.False(baseline.IsHidden, "None-strategy cabin should start visible");

        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);

        // Resend each poll: the !cabin handler reads the server's view of the farmer
        // location, which can lag the client warp by a tick.
        CabinInfoResponse? moved = null;
        var ok = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CabinPlacement_Moved,
            async () =>
            {
                await GameClient.SendChat("!cabin");
                moved = await GetOurCabinAsync(ownerId, ct);
                return (moved.TileX, moved.TileY) != (baseline.TileX, baseline.TileY);
            },
            TestTimings.CabinAssignmentTimeout,
            cancellationToken: ct
        );

        Assert.True(ok, "Cabin did not move to the farmer tile after !cabin under None");
        Assert.Equal(CabinPlacementHelper.ExpectedCabinTile, (moved!.TileX, moved.TileY));
        Assert.Equal("Normal", moved.Type);
        Assert.False(moved.IsHidden, "Moved None cabin must stay visible");

        await Exceptions.AssertNoExceptionsAsync("after !cabin under None");

        Log($"None-strategy cabin moved to ({moved.TileX},{moved.TileY})");
    }

    /// <summary>
    /// Switching a stacked strategy to None via the settings file is rejected on an
    /// existing save: file-driven placement would bulldoze whatever players built on the
    /// designated spots, so the rejection Warn points operators at the staged
    /// 'cabins migrate' flow (covered by CabinMigrationTests) or a fresh game. The
    /// persisted strategy reverts and the stacked cabins stay hidden. The false-trip guard
    /// (a legitimately fresh None game must NOT be reverted — it has zero hidden cabins)
    /// is exercised by every other test in this class, which all create fresh None games.
    /// </summary>
    [Fact]
    public async Task StrategySwitch_StackedToNone_RejectedOnExistingSave()
    {
        LogSection("Testing stacked → None strategy switch rejection");

        var ct = TestCt;
        _needsServerReset = true;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");

        var before = await ServerApi.GetCabins(ct);
        Assert.NotNull(before);
        Assert.Equal("CabinStack", before.Strategy);

        try
        {
            await ServerSettingsFileHelper.SwitchCabinStrategyAsync(Server, "None", ct);
            await ReloadServerAsync();

            // The reload's OnSaveLoaded migration rejects the switch and reverts the
            // persisted strategy; the first post-reload read observes the final state
            // (the completion contract republishes the snapshot).
            var after = await ServerApi.GetCabins(ct);
            Assert.NotNull(after);
            Assert.Equal("CabinStack", after.Strategy);
            var playerCabins = after.Cabins.Where(c => c.Type == "CabinStack").ToList();
            Assert.NotEmpty(playerCabins);
            Assert.True(
                playerCabins.All(c => c.IsHidden),
                "Stacked cabins must remain hidden after the rejected switch to None"
            );
            Log($"Switch rejected; strategy stayed {after.Strategy}");
        }
        finally
        {
            // Restore the settings file so later reloads by a server reuser don't
            // re-trigger the rejection warning.
            await ServerSettingsFileHelper.SwitchCabinStrategyAsync(Server, "CabinStack", ct);
        }
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
