using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E coverage for CropSaver across Garden Pots.
///
/// Two complementary tests, both load-bearing:
/// <list type="bullet">
/// <item><description>
/// <see cref="GardenPotCrop_IsRegisteredWithCropSaverWatcher"/> exercises the
/// <c>CropWatcher</c> fix: the watcher now walks
/// <c>Utility.ForEachLocation</c> and inspects <c>IndoorPot.hoeDirt</c>, so a
/// pot crop enters <c>CropSaverData</c> within one scan window.
/// </description></item>
/// <item><description>
/// <see cref="GardenPotCrop_KillSuppressedOnSeasonTransition_WhileOwnerOffline"/>
/// drives a real day-transition across a season boundary and asserts that
/// <c>KillCrop_Prefix</c>'s switch from a hardcoded <c>"Farm"</c> lookup to
/// <c>dirt.Location.Name</c> suppresses vanilla <c>Crop.Kill()</c>'s
/// out-of-season kill. Also exercises <c>SaverCrop.TryGetCoorespondingDirt</c>'s
/// <see cref="StardewValley.Objects.IndoorPot"/> branch.
/// </description></item>
/// </list>
/// </summary>
[TestServer(Isolation = IsolationMode.SharedClass)]
public class CropSaverTests : TestBase
{
    /// <summary>
    /// Cauliflower seed id (unqualified — HoeDirt.plant uses the unqualified
    /// id for Crop.TryGetData lookups). Cauliflower is Spring-only with
    /// DaysInPhase=[1,2,4,4,1] = 12 days total to maturity.
    /// </summary>
    private const string CauliflowerSeedId = "474";

    /// <summary>
    /// Tile coordinates on open Farm soil south of the FarmHouse front door
    /// (which is at (64, 15) per vanilla <c>Farm.GetMainFarmHouseEntry()</c>).
    /// Placing on or directly adjacent to (64, 15) hides the pot behind the
    /// door sprite or lands on porch tiles where <c>HoeDirt.plant</c> rejects
    /// the seed. Tile (64, 21) is in the open Farm grass area south of the
    /// porch — visible on the farmhand screenshot, plantable for any crop.
    ///
    /// <para>
    /// Tests in this class share a server instance (<c>SharedClass</c>) so
    /// each test uses its own tile to avoid "tile occupied" collisions when
    /// xUnit runs them sequentially against the same Farm.
    /// </para>
    /// </summary>
    private const int TileA_X = 64;
    private const int TileA_Y = 21;
    private const int TileB_X = 64;
    private const int TileB_Y = 22;

    [Fact]
    public async Task GardenPotCrop_IsRegisteredWithCropSaverWatcher()
    {
        var ct = TestContext.Current.CancellationToken;
        await PlacePotAndPlantCauliflowerAsync(TileA_X, TileA_Y, ct);
        await AssertWatcherRegistersPotAsync(TileA_X, TileA_Y, ct);
    }

    [Fact]
    public async Task GardenPotCrop_KillSuppressedOnSeasonTransition_WhileOwnerOffline()
    {
        var ct = TestContext.Current.CancellationToken;

        var farmhand = await PlacePotAndPlantCauliflowerAsync(TileB_X, TileB_Y, ct);
        await AssertWatcherRegistersPotAsync(TileB_X, TileB_Y, ct);

        // Pre-arm extraDays past CropSaver.OnDayEnd's branch-1 / branch-2
        // floors so the prolong logic doesn't kill the crop *inside* OnDayEnd
        // before the day actually transitions.
        //
        // Math (CropSaver.cs:39-104, against today's Spring 28 Y1):
        //   nightOfDeath = datePlanted + (extraDays + 28*numSeasons - datePlanted.Day)
        //                = Spring 28 + extraDays
        //   earliestFullyGrownDate (fresh Cauliflower, unwatered)
        //                = Spring 28 + (1 + 10 + 1) = Summer 13
        //   branch-1: !fullyGrown && now.Day==28 && nightOfDeath < earliest →
        //     bypassed when extraDays >= 13 (nightOfDeath = Summer 13 ≥ Summer 13)
        //   branch-2: now >= nightOfDeath → bypassed (Spring 28 < Summer 13)
        //
        // After OnDayEnd runs without killing, the day advances to Summer 1 and
        // Crop.newDay's `isOutdoors && !IsInSeason` check fires Crop.Kill() —
        // the harmony prefix is the only thing that can save it.
        const int ExtraDaysToBypassOnDayEndKill = 13;
        await ServerApi.SetDate("spring", 28, year: 1, ct);
        var armed = await ServerApi.SetSaverCrop(
            "Farm",
            TileB_X,
            TileB_Y,
            extraDays: ExtraDaysToBypassOnDayEndKill,
            ct: ct
        );
        Assert.NotNull(armed);
        Assert.True(armed.Success, $"SetSaverCrop failed: {armed.Error}");
        Assert.True(armed.Found, "SaverCrop entry must exist before pre-arming extraDays");

        // Disconnect the owner — KillCrop_Prefix is the only suppression path
        // when the owner is offline. With an online owner the prolong logic
        // would also keep extraDays steady (line 54: ownerId != 0 && offline).
        await Farmers.DisconnectAndWaitForSlotAsync(
            farmhand.JoinResult.UniqueMultiplayerId,
            farmhand.FarmerName,
            ct
        );

        // Trigger sleep-induced day-transition. SetClockSpeed(20) accelerates
        // the wait from minutes to seconds; same pattern as
        // HostAutomationTests.HostPassesOut_WhenTimeReaches2AM.
        var statusBefore = await ServerApi.GetStatus(ct);
        Assert.NotNull(statusBefore);
        await ServerApi.SetTime(TestTimings.PrePassOutTime, ct);
        await ServerApi.SetClockSpeed(20, ct);
        try
        {
            var dayChanged = await DayChange.WaitAsync(
                statusBefore.Day,
                statusBefore.Season,
                statusBefore.Year,
                ct
            );
            Assert.True(dayChanged, "Day did not advance from Spring 28 → Summer 1");
        }
        finally
        {
            await ServerApi.SetClockSpeed(1, ct);
        }

        // Crop.newDay ran on the host as part of the season-transition. With
        // the prefix fix in place dirt.Location.Name resolves to "Farm" for
        // the pot's dirt and the SaverCrop lookup suppresses Kill(). Without
        // the fix, the pre-fix code's hardcoded "Farm" lookup would *also*
        // match here (because the pot IS on the Farm), but TryGetCoorespondingDirt
        // would have returned null for the pot at OnDayEnd's prolong step,
        // skipping the extraDays increment, AND the watcher wouldn't have
        // registered the pot in the first place — so no SaverCrop exists, the
        // prefix returns true, and Kill() executes. The IsAlive check
        // therefore validates the full pot-aware code path end-to-end.
        var cropsAfter = await ServerApi.GetAllCrops(ct);
        Assert.NotNull(cropsAfter);
        var stillThere = cropsAfter.Crops.SingleOrDefault(c =>
            c.IsInPot && c.LocationName == "Farm" && c.TileX == TileB_X && c.TileY == TileB_Y
        );
        Assert.NotNull(stillThere);
        Assert.True(
            stillThere.IsAlive,
            "Cauliflower in a Garden Pot must survive Spring 28 → Summer 1 with offline owner. "
                + "Pre-fix: CropWatcher never registered the pot, so Crop.newDay's "
                + "out-of-season Kill ran unsuppressed."
        );
    }

    /// <summary>
    /// Connects a farmhand, warps to the Farm pot tile, places an IndoorPot,
    /// and plants Cauliflower. Returns the connected farmhand. Note: the
    /// host-side test screenshot will show the FarmHouse interior (the host
    /// bot stays inside) — the pot is only visible on
    /// <c>client_result.png</c>, captured from the farmhand's view.
    /// </summary>
    private async Task<Infrastructure.Fixture.FarmerTestHelper.ClientConnection> PlacePotAndPlantCauliflowerAsync(
        int tileX,
        int tileY,
        CancellationToken ct
    )
    {
        // Force Spring before planting. Cauliflower is Spring-only (HoeDirt.cs:592
        // checks data.Seasons.Contains(location.GetSeason())), and the sibling
        // KillSuppressedOnSeasonTransition test advances the shared-class server
        // to Summer 1.
        var springDate = await ServerApi.SetDate("spring", 1, year: 1, ct);
        Assert.NotNull(springDate);
        Assert.True(springDate.Success, $"SetDate(spring 1) failed: {springDate.Error}");

        var farmhand = await Farmers.ConnectNewAsync(namePrefix: "CropTest", ct: ct);

        var warp = await GameClient.Actions.Warp("Farm", tileX, tileY);
        Assert.True(warp?.Success, $"Warp failed: {warp?.Error}");

        // Game1.warpFarmer is async (queued LocationRequest); poll until the
        // farmhand actually lands on the Farm before placing the pot.
        // PlacePot guards on player.currentLocation matching the target.
        var arrived = await GameClient.WaitForLocationAsync("^Farm$", TimeSpan.FromSeconds(10), ct);
        Assert.NotNull(arrived);

        // clearObstacles=true: the Standard farm spawns rocks/weeds/twigs into
        // location.Objects at game-creation. Clear whatever sits at the target
        // tile so the pot placement succeeds regardless of seasonal spawn density.
        var place = await GameClient.Actions.PlacePot("Farm", tileX, tileY, clearObstacles: true);
        Assert.True(place?.Success, $"PlacePot failed: {place?.Error}");

        // Crop.TryGetData looks up by unqualified id — passing "(O)474" fails
        // the lookup and HoeDirt.plant returns false silently.
        var plant = await GameClient.Actions.PlantCrop(CauliflowerSeedId, "Farm", tileX, tileY);
        Assert.True(plant?.Success, $"PlantCrop failed: {plant?.Error}");

        return farmhand;
    }

    /// <summary>
    /// Waits up to 5s for the host's CropWatcher (5-tick scan ≈ 333ms at
    /// SERVER_TPS=15) to register the pot crop in CropSaverData. Polled via
    /// the host-side /test/crops endpoint, which sets <c>IsManaged=true</c>
    /// when <c>CropSaverData</c> has a matching entry.
    /// </summary>
    private async Task AssertWatcherRegistersPotAsync(int tileX, int tileY, CancellationToken ct)
    {
        var watcherRegisteredPot = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CropSaver_AwaitWatcher,
            async () =>
            {
                var snapshot = await ServerApi.GetAllCrops(ct);
                return snapshot?.Crops.Any(c =>
                        c.IsInPot
                        && c.IsManaged
                        && c.LocationName == "Farm"
                        && c.TileX == tileX
                        && c.TileY == tileY
                    ) == true;
            },
            timeout: TimeSpan.FromSeconds(5),
            cancellationToken: ct
        );

        Assert.True(
            watcherRegisteredPot,
            "CropWatcher should register the IndoorPot crop in CropSaverData within 5s. "
                + "Pre-fix the watcher only scanned terrainFeatures on the Farm location and "
                + "missed every Garden Pot crop."
        );
    }
}
