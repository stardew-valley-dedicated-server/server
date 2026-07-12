using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E coverage for the FarmhouseStack strategy's !cabin gate: under FarmhouseStack the
/// host keeps every cabin in the farmhouse, so !cabin (and !cabin reset) must be rejected
/// before any placement happens — even when the farmer is standing on the Farm, where the
/// off-Farm gate would otherwise fire first. This is the first FarmhouseStack E2E coverage,
/// so a join misbehaving here is a product finding to investigate, not a test bug to mask.
///
/// Exclusive + a fresh game per test so the farm starts clean and assertions scope to the
/// single connected farmer.
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Exclusive = true)]
public class CabinStrategyFarmhouseStackTests : TestBase
{
    public CabinStrategyFarmhouseStackTests() { }

    public override async ValueTask DisposeAsync()
    {
        // Reset to a clean default game so the FarmhouseStack config doesn't leak to a
        // sibling class reusing this server. Disconnect first: /newgame returns 409 while a
        // client is connected, and our tests stay connected through the body.
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
    /// !cabin under FarmhouseStack is rejected with the strategy message, and records no
    /// intent. The farmer is warped onto the Farm first so the rejection proves the
    /// *strategy* gate fired, not the off-Farm one — keeping the test honest if the gate
    /// order ever changes.
    /// </summary>
    [Fact]
    public async Task FarmhouseStack_RejectsCabinMove()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "FarmhouseStack");

        var client = await Farmers.ConnectNewAsync(ct: ct);
        var ownerId = client.JoinResult.UniqueMultiplayerId;

        // Warp onto the Farm so the off-Farm gate can't be the one rejecting us.
        await CabinPlacementHelper.WarpAndClearFootprintAsync(GameClient, ct);
        var baseline = await GetOurCabinAsync(ownerId, ct);

        // Static strategy gate, no race — resend is harmless here (self-identifying reply + one
        // consistent pattern). Match only "keep all cabins" (unique to this gate), not a longer
        // phrase: the delivered message has a double space ("cabins  in") a substring would straddle.
        var rejection = await Chat.ResendUntilResponseAsync(
            "!cabin",
            "keep all cabins",
            replyFamilyPrefix: "Can't move cabin",
            timeout: TestTimings.CabinAssignmentTimeout
        );
        Assert.True(
            rejection.Matched,
            $"Expected a FarmhouseStack rejection reply; {rejection.Describe()}"
        );

        // No move, and no intent written on rejection.
        var after = await GetOurCabinAsync(ownerId, ct);
        Assert.True(after.IsHidden, "Cabin should still be hidden under FarmhouseStack");
        Assert.Equal((baseline.TileX, baseline.TileY), (after.TileX, after.TileY));

        var cabins = await ServerApi.GetCabins(ct);
        Assert.NotNull(cabins);
        Assert.DoesNotContain(ownerId, cabins.SavedPositionPlayerIds);

        await Exceptions.AssertNoExceptionsAsync("after FarmhouseStack !cabin rejection");
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
