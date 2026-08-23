using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E coverage for the AllowCabinRelocation switch: with it disabled, !cabin (move and
/// reset alike — the gate fires before subcommand parsing) is rejected uniformly under all
/// three cabin strategies, and no placement intent is recorded. The enabled default is
/// covered per strategy elsewhere (CabinStack/None move tests,
/// <see cref="CabinStrategyFarmhouseStackTests"/> for FarmhouseStack).
///
/// Exclusive + a fresh game per case so the farm starts clean and assertions scope to the
/// single connected farmer.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Exclusive = true)]
public class CabinRelocationTests : TestBase
{
    public CabinRelocationTests() { }

    public override async ValueTask DisposeAsync()
    {
        // Reset to the pooled config so the relocation-disabled overrides (persisted into
        // the in-container settings file by /newgame) don't leak to a sibling class reusing
        // this server. Disconnect first: /newgame returns 409 while a client is connected.
        if (Lease != null)
        {
            try
            {
                // Tolerant: a no-op throw when never connected / already at title must not
                // block the reset.
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
                await ResetServerToPooledConfigAsync();
            }
            catch (Exception ex)
            {
                // A failed reset leaves a relocation-disabled server (settings file
                // included) pooled under the default config hash. Retire it.
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
    /// !cabin with AllowCabinRelocation=false is rejected with the switch's message on every
    /// strategy, and neither moves the cabin nor records intent. The farmer is warped onto
    /// the Farm first so the rejection proves the relocation gate fired, not the off-Farm
    /// one. maxPlayers:4 keeps the None case's up-front placement to the 4 lowest-Order
    /// designated spots, clear of the warp/clear footprint (harmless for the stacks).
    /// </summary>
    [Theory]
    [InlineData("CabinStack")]
    [InlineData("FarmhouseStack")]
    [InlineData("None")]
    public async Task RelocationDisabled_RejectsCabinCommand(string strategy)
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(
            farmType: 0,
            cabinStrategy: strategy,
            maxPlayers: 4,
            allowCabinRelocation: false
        );

        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);
        var baseline = await GetOurCabinAsync(ownerId, ct);

        // Static gate, no race — resend is harmless (self-identifying reply, one pattern).
        var rejection = await Chat.ResendUntilResponseAsync(
            "!cabin",
            "relocation is disabled",
            replyFamilyPrefix: "Cabin relocation",
            timeout: TestTimings.CabinAssignmentTimeout
        );
        Assert.True(
            rejection.Matched,
            $"Expected a relocation-disabled rejection under {strategy}; {rejection.Describe()}"
        );

        // The gate fires before subcommand parsing, so 'reset' is rejected identically. Pins
        // the ordering the class doc promises: were the gate moved below the parse, reset would
        // silently run PlayerCabinPositions.TryRemove + Data.Write() on a disabled server.
        var resetRejection = await Chat.ResendUntilResponseAsync(
            "!cabin reset",
            "relocation is disabled",
            replyFamilyPrefix: "Cabin relocation",
            timeout: TestTimings.CabinAssignmentTimeout
        );
        Assert.True(
            resetRejection.Matched,
            $"Expected '!cabin reset' to hit the same gate under {strategy}; "
                + resetRejection.Describe()
        );

        // No move, and no intent written or cleared on either rejection.
        var after = await GetOurCabinAsync(ownerId, ct);
        Assert.Equal((baseline.TileX, baseline.TileY), (after.TileX, after.TileY));
        Assert.Equal(baseline.IsHidden, after.IsHidden);

        var cabins = await ServerApi.GetCabins(ct);
        Assert.NotNull(cabins);
        Assert.DoesNotContain(ownerId, cabins.SavedPositionPlayerIds);

        await Exceptions.AssertNoExceptionsAsync($"after !cabin rejection under {strategy}");
    }

    /// <summary>
    /// The /newgame allowCabinRelocation override is persisted into server-settings.json,
    /// so it survives a /reload (SyncFromSettings re-reads the file on every reload — an
    /// unpersisted override would silently revert to the file's original true, re-enabling
    /// relocation without any operator action).
    /// </summary>
    [Fact]
    public async Task RelocationDisabled_SurvivesReload()
    {
        var ct = TestCt;
        await CreateNewGameOnServerAsync(
            farmType: 0,
            cabinStrategy: "CabinStack",
            maxPlayers: 4,
            allowCabinRelocation: false
        );

        await ReloadServerAsync();

        var settings = await ServerApi.GetSettings(ct);
        Assert.NotNull(settings);
        Assert.False(
            settings.Server.AllowCabinRelocation,
            "allowCabinRelocation: false must survive a /reload (persisted into "
                + "server-settings.json at game creation); true means the override reverted"
        );

        // Behavior-level proof: !cabin is still rejected after the reload.
        await Farmers.ConnectNewAsync(ct: ct);
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);
        var rejection = await Chat.ResendUntilResponseAsync(
            "!cabin",
            "relocation is disabled",
            replyFamilyPrefix: "Cabin relocation",
            timeout: TestTimings.CabinAssignmentTimeout
        );
        Assert.True(
            rejection.Matched,
            $"!cabin must still be rejected after /reload; {rejection.Describe()}"
        );

        await Exceptions.AssertNoExceptionsAsync("after relocation-disabled reload coverage");
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
