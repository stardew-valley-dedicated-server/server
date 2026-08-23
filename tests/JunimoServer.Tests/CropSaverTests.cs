using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E coverage for CropSaver across Garden Pots.
///
/// Complementary tests, all load-bearing:
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
/// <c>dirt.Location.NameOrUniqueName</c> suppresses vanilla <c>Crop.Kill()</c>'s
/// out-of-season kill. Also exercises <c>SaverCrop.TryGetCoorespondingDirt</c>'s
/// <c>StardewValley.Objects.IndoorPot</c> branch.
/// </description></item>
/// <item><description>
/// <see cref="GardenPotOnTilledTile_StaysManagedAcrossWatcherScans"/> exercises
/// the pot-wins-the-tile rule: a pot placed on an empty tilled tile makes two
/// HoeDirts share one (location, tile) key, and without the <c>CropWatcher</c>
/// terrain-loop skip the empty terrain dirt evicts the pot crop's entry one
/// scan after registration.
/// </description></item>
/// <item><description>
/// <see cref="PotCropInImmuneLocation_SurvivesPastDateOfDeath_WhileOwnerOffline"/>
/// drives a pot crop in a season-immune location (the farmhand's cabin interior)
/// past its computed date of death through a real <c>OnDayEnd</c>, asserting the
/// <c>IsCropSeasonImmune()</c> guard spares it — the greenhouse bug class.
/// </description></item>
/// <item><description>
/// <see cref="ManagedCrop_SurvivesLightningStrike_ByDefault"/> exercises the
/// lightning-immunity default: a deterministic <c>/test/lightning_strike</c>
/// (vanilla's crop-strike inside the CropSaver lightning context) must be
/// suppressed by <c>KillCrop_Prefix</c> for a managed crop when
/// <c>CROP_SAVER_LIGHTNING_IMMUNITY</c> is unset. The disabled path (kill let
/// through, tracking entry dropped) needs a server booted with the env var set
/// to false, which the test harness cannot express per-class — not E2E-covered.
/// </description></item>
/// </list>
/// </summary>
// Exclusive serializes the methods (SharedClass alone runs them concurrently): all
// three mutate the global calendar and would clobber each other mid-transition.
[TestServer(Isolation = IsolationMode.SharedClass, Exclusive = true)]
public class CropSaverTests : TestBase
{
    /// <summary>
    /// Cauliflower seed id (unqualified — HoeDirt.plant uses the unqualified
    /// id for Crop.TryGetData lookups). Cauliflower is Spring-only with
    /// DaysInPhase=[1,2,4,4,1] = 12 days total to maturity.
    /// </summary>
    private const string CauliflowerSeedId = "474";

    /// <summary>
    /// Pumpkin seed id (unqualified). Fall-only, so planted Fall 1 its CropSaver date
    /// of death is Fall 28 — the immune-location test drives one SetDate to that
    /// season-end rollover, where a seasonal crop would be killed.
    /// </summary>
    private const string PumpkinSeedId = "490";

    /// <summary>
    /// Tile coordinates on open Farm soil south of the FarmHouse front door
    /// (at (64, 15) per vanilla <c>Farm.GetMainFarmHouseEntry()</c>).
    ///
    /// <para>
    /// <c>PlacePotAndPlantCauliflowerAsync</c> clears each pot's 3×3 tile
    /// neighborhood before placing to make it immune to the overnight weed spawn
    /// (see that method for why 3×3 suffices). The tile stays on the outdoor,
    /// non-season-immune Farm, so the seasonal Kill-suppression path under test
    /// still fires.
    /// </para>
    ///
    /// <para>
    /// Tests in this class share a server instance (<c>SharedClass</c>) so
    /// each test uses its own tile to avoid "tile occupied" collisions when
    /// the methods run against the same Farm.
    /// </para>
    /// </summary>
    private const int TileA_X = 64;
    private const int TileA_Y = 21;
    private const int TileB_X = 64;
    private const int TileB_Y = 22;
    private const int TileC_X = 64;
    private const int TileC_Y = 24;
    private const int TileD_X = 64;
    private const int TileD_Y = 23;

    /// <summary>
    /// Tile inside the farmhand's cabin interior for the immune-location test.
    /// The cabin interior is a separate location from the Farm, so this needs no
    /// coordination with the Farm tiles above. (3, 5) is open floor in the
    /// starter cabin map, clear of the bed/chest/TV furniture near the walls.
    /// </summary>
    private const int CabinTileX = 3;
    private const int CabinTileY = 5;

    [Fact]
    public async Task GardenPotCrop_IsRegisteredWithCropSaverWatcher()
    {
        var ct = TestCt;
        await PlacePotAndPlantCauliflowerAsync(TileA_X, TileA_Y, ct);
        await AssertWatcherRegistersPotAsync("Farm", TileA_X, TileA_Y, ct);
    }

    [Fact]
    public async Task GardenPotOnTilledTile_StaysManagedAcrossWatcherScans()
    {
        var ct = TestCt;

        // Force Spring so Cauliflower is plantable (see PlacePotAndPlantCauliflowerAsync).
        var springDate = await ServerApi.SetDate("spring", 1, year: 1, ct);
        Assert.NotNull(springDate);
        Assert.True(springDate.Success, $"SetDate(spring 1) failed: {springDate.Error}");

        await Farmers.ConnectFastAsync(namePrefix: "PotTilled", ct: ct);

        var warp = await GameClient.Actions.Warp("Farm", TileC_X, TileC_Y);
        Assert.True(warp?.Success, $"Warp failed: {warp?.Error}");
        var arrived = await GameClient.WaitForLocationAsync("^Farm$", TimeSpan.FromSeconds(10), ct);
        Assert.NotNull(arrived);

        // Clear the 3×3 neighborhood (weed-spawn immunity, see the sibling helper),
        // then till the pot tile so it hosts a terrain HoeDirt, and place the pot
        // WITHOUT clearObstacles — the tilled dirt must survive placement. Vanilla
        // allows this: CanItemBePlacedHere rejects only dirt WITH a crop, so a pot
        // on an empty tilled tile is a legal player-reachable state, and the tile
        // then holds two HoeDirts sharing one (location, tile) key.
        var cleared = await GameClient.Actions.ClearArea(
            "Farm",
            TileC_X - 1,
            TileC_Y - 1,
            width: 3,
            height: 3
        );
        Assert.True(cleared?.Success == true, $"ClearArea failed: {cleared?.Error}");

        var tilled = await GameClient.Actions.TillTile("Farm", TileC_X, TileC_Y);
        Assert.True(tilled?.Success == true, $"TillTile failed: {tilled?.Error}");

        var place = await GameClient.Actions.PlacePot(
            "Farm",
            TileC_X,
            TileC_Y,
            clearObstacles: false
        );
        Assert.True(place?.Success, $"PlacePot failed: {place?.Error}");

        var plant = await GameClient.Actions.PlantCrop(CauliflowerSeedId, "Farm", TileC_X, TileC_Y);
        Assert.True(plant?.Success, $"PlantCrop failed: {plant?.Error}");

        await AssertWatcherRegistersPotAsync("Farm", TileC_X, TileC_Y, ct);

        // The pre-fix failure mode is time-shaped: the watcher's terrain visit saw
        // the empty terrain dirt's hasCrop=false AFTER the pot's true and evicted
        // the entry one scan window later — IsManaged flipped false ~1 scan after
        // registration and stayed false. Sample the snapshot across several scan
        // windows (5-tick scan ≈ 1s at SERVER_TPS=5; 3s ≈ 3 scans) and require
        // IsManaged to hold on every sample. Also pin the row count: the crop-less
        // terrain dirt must not double-report the tile.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = await ServerApi.GetAllCrops(ct);
            Assert.NotNull(snapshot);
            var rows = snapshot
                .Crops.Where(c =>
                    c.LocationName == "Farm" && c.TileX == TileC_X && c.TileY == TileC_Y
                )
                .ToList();
            Assert.True(
                rows.Count == 1,
                $"Expected exactly one crop row at Farm ({TileC_X},{TileC_Y}), got {rows.Count} — "
                    + "0 means the pot crop vanished; 2 means the tile double-reported "
                    + "(a crop grew in the terrain dirt under the pot)."
            );
            var potRow = rows[0];
            Assert.True(
                potRow.IsInPot,
                "The only crop row at the shared tile must be the pot's — a terrain row "
                    + "here means a crop landed in the dirt under the pot."
            );
            Assert.True(
                potRow.IsManaged,
                "Pot crop on a tilled tile must STAY managed across watcher scans. "
                    + "Pre-fix: the empty terrain HoeDirt sharing the tile key evicted the "
                    + "SaverCrop entry one scan after registration (watcher churn)."
            );

            await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
        }
    }

    [Fact]
    public async Task ManagedCrop_SurvivesLightningStrike_ByDefault()
    {
        var ct = TestCt;

        await PlacePotAndPlantCauliflowerAsync(TileD_X, TileD_Y, ct);
        await AssertWatcherRegistersPotAsync("Farm", TileD_X, TileD_Y, ct);

        // Deterministic strike: /test/lightning_strike runs vanilla's crop-strike
        // (HoeDirt.performToolAction with lightning damage → Crop.Kill) inside the
        // CropSaver lightning context, bypassing performLightningUpdate's RNG target
        // roll. With CROP_SAVER_LIGHTNING_IMMUNITY at its default (true),
        // KillCrop_Prefix must suppress the kill for a managed crop.
        var strike = await ServerApi.LightningStrike("Farm", TileD_X, TileD_Y, ct);
        Assert.NotNull(strike);
        Assert.True(strike.Success, $"Lightning strike failed: {strike.Error}");
        Assert.True(strike.Found, "Strike must find the pot crop at the target tile");
        Assert.True(
            strike.CropAliveAfter,
            "Managed crop must survive a lightning strike with immunity at its default (true). "
                + "Dead means KillCrop_Prefix failed to suppress the lightning-context kill."
        );
        Assert.True(
            strike.IsManagedAfter,
            "Surviving crop must stay tracked by CropSaver — the entry is only dropped "
                + "when a lightning kill is let through (immunity disabled)."
        );

        // Re-assert on the /test/crops snapshot so the survival check goes through the
        // same read path the other tests gate on.
        var crops = await ServerApi.GetAllCrops(ct);
        Assert.NotNull(crops);
        var row = crops.Crops.SingleOrDefault(c =>
            c.IsInPot && c.LocationName == "Farm" && c.TileX == TileD_X && c.TileY == TileD_Y
        );
        Assert.NotNull(row);
        Assert.True(
            row.IsAlive,
            "Snapshot must agree the managed crop is alive after the lightning strike"
        );
    }

    [Fact]
    public async Task GardenPotCrop_KillSuppressedOnSeasonTransition_WhileOwnerOffline()
    {
        var ct = TestCt;

        var farmhand = await PlacePotAndPlantCauliflowerAsync(TileB_X, TileB_Y, ct);
        await AssertWatcherRegistersPotAsync("Farm", TileB_X, TileB_Y, ct);

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

        // Connect a second, unrelated farmer to drive the day transition. The crop's
        // ownerId is already stamped to the primary (the watcher registered the pot
        // above, before this farmer joins), so this farmer never becomes the owner.
        await using var driver = await Farmers.ConnectSecondFarmerAsync(ct: ct);

        // Disconnect the crop owner — KillCrop_Prefix suppresses the kill only while
        // the crop's owner is offline. With an online owner the prolong logic would
        // also keep extraDays steady (CropSaver.cs:54: ownerId != 0 && offline).
        await Farmers.DisconnectAndWaitForSlotAsync(
            farmhand.JoinResult.UniqueMultiplayerId,
            farmhand.FarmerName,
            ct
        );

        // Advance the day the way the server actually supports it: a connected player
        // sleeps, so the host auto-sleeps (it's the only one not ready) and the group
        // transitions. The server deliberately does NOT advance an empty server's clock
        // — driving this via SetClockSpeed with no one connected froze on the lone-host
        // shouldTimePass gate (~35% "Day did not advance" flake). SetClockSpeed(20) just
        // accelerates the post-sleep pass-out.
        var statusBefore = await ServerApi.GetStatus(ct);
        Assert.NotNull(statusBefore);
        var driverSlept = await driver.Client.Actions.Sleep();
        Assert.True(driverSlept?.Success == true, $"Driver sleep failed: {driverSlept?.Error}");
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
        // the prefix fix in place dirt.Location.NameOrUniqueName resolves to "Farm" for
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
        Assert.True(
            stillThere != null,
            $"Garden Pot crop missing at Farm ({TileB_X},{TileB_Y}) after Spring 28 → Summer 1 — "
                + "the IndoorPot was removed (check the server log for 'Garden Pot was destroyed', "
                + "vanilla's overnight weed spawn spreading onto the pot tile), not merely killed."
        );
        Assert.True(
            stillThere.IsAlive,
            "Cauliflower in a Garden Pot must survive Spring 28 → Summer 1 with offline owner. "
                + "Pre-fix: CropWatcher never registered the pot, so Crop.newDay's "
                + "out-of-season Kill ran unsuppressed."
        );
    }

    [Fact]
    public async Task PotCropInImmuneLocation_SurvivesPastDateOfDeath_WhileOwnerOffline()
    {
        var ct = TestCt;

        // Plant a Pumpkin in a Garden Pot inside the farmhand's own cabin
        // interior — an indoor (season-immune) location, exercising the same fix
        // branch as the greenhouse (the Greenhouse itself is CC-pantry-gated
        // rubble on a fresh test save and isn't warpable/plantable).
        var farmhand = await Farmers.ConnectFastAsync(namePrefix: "CropImmune", ct: ct);

        // The farmhand auto-warps into its cabin on join; the client reports the
        // interior as "FarmHouse" + GUID (NameOrUniqueName), so confirm arrival on that.
        // Placement and the crop snapshot below key on GameLocation.Name, which is
        // "Cabin" — not the unique name above.
        var arrived = await GameClient.WaitForLocationAsync(
            "^FarmHouse",
            TimeSpan.FromSeconds(15),
            ct
        );
        Assert.NotNull(arrived);

        var place = await GameClient.Actions.PlacePot(
            "Cabin",
            CabinTileX,
            CabinTileY,
            clearObstacles: true
        );
        Assert.True(place?.Success, $"PlacePot failed: {place?.Error}");

        var plant = await GameClient.Actions.PlantCrop(
            PumpkinSeedId,
            "Cabin",
            CabinTileX,
            CabinTileY
        );
        Assert.True(plant?.Success, $"PlantCrop failed: {plant?.Error}");

        await AssertWatcherRegistersPotAsync("Cabin", CabinTileX, CabinTileY, ct);

        // The fix must classify the cabin as season-immune. Assert it on the
        // snapshot row so a regression in IsCropSeasonImmune() fails here loudly
        // rather than only via the survival check below.
        var snapshot = await ServerApi.GetAllCrops(ct);
        Assert.NotNull(snapshot);
        var potRow = snapshot.Crops.SingleOrDefault(c =>
            c.IsInPot && c.LocationName == "Cabin" && c.TileX == CabinTileX && c.TileY == CabinTileY
        );
        Assert.NotNull(potRow);
        Assert.True(
            potRow.IsSeasonImmune,
            "Cabin interior must be season-immune (indoor → !IsOutdoors)"
        );

        // Stamp datePlanted = Fall 1 (death Fall 28) with extraDays left at 0, so the
        // upcoming Fall 28 → Winter 1 rollover would kill a seasonal crop. Only the
        // immunity guard spares this one. SetSaverCrop addresses the entry by its key,
        // the unique location name ("Cabin{GUID}") — not the display name "Cabin".
        var planted = await ServerApi.SetSaverCrop(
            potRow.UniqueLocationName,
            CabinTileX,
            CabinTileY,
            datePlanted: ("fall", 1, 1),
            ct: ct
        );
        Assert.NotNull(planted);
        Assert.True(planted.Success, $"SetSaverCrop failed: {planted.Error}");
        Assert.True(planted.Found, "SaverCrop entry must exist before setting datePlanted");

        await ServerApi.SetDate("fall", 28, year: 1, ct);

        // Connect a second, unrelated farmer to drive the day transition. The crop's
        // ownerId is already stamped to the primary (watcher-registered above), so this
        // farmer never becomes the owner.
        await using var driver = await Farmers.ConnectSecondFarmerAsync(ct: ct);

        // Disconnect the crop owner so branch-2's kill exception (online owner) does
        // not apply — the kill would definitely fire here without the fix.
        await Farmers.DisconnectAndWaitForSlotAsync(
            farmhand.JoinResult.UniqueMultiplayerId,
            farmhand.FarmerName,
            ct
        );

        // Advance the day via a connected player's sleep (the server won't advance an
        // empty server's clock — see the sibling test for the lone-host freeze). The
        // driver sleeps, the host auto-sleeps, and the group transitions.
        var statusBefore = await ServerApi.GetStatus(ct);
        Assert.NotNull(statusBefore);
        var driverSlept = await driver.Client.Actions.Sleep();
        Assert.True(driverSlept?.Success == true, $"Driver sleep failed: {driverSlept?.Error}");
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
            Assert.True(dayChanged, "Day did not advance from Fall 28 → Winter 1");
        }
        finally
        {
            await ServerApi.SetClockSpeed(1, ct);
        }

        // OnDayEnd ran during the transition. The immunity guard's `continue`
        // must have skipped the date-of-death kill for this indoor pot.
        var cropsAfter = await ServerApi.GetAllCrops(ct);
        Assert.NotNull(cropsAfter);
        var stillThere = cropsAfter.Crops.SingleOrDefault(c =>
            c.IsInPot && c.LocationName == "Cabin" && c.TileX == CabinTileX && c.TileY == CabinTileY
        );
        Assert.NotNull(stillThere);
        Assert.True(
            stillThere.IsAlive,
            "Pumpkin in a Garden Pot inside a season-immune cabin interior must survive "
                + "Fall 28 → Winter 1 past its computed date of death. Pre-fix: CropSaver.OnDayEnd "
                + "had no immunity awareness and date-of-death-killed it — the greenhouse bug class."
        );
        Assert.True(
            stillThere.IsManaged,
            "CropSaver entry for the cabin pot must survive the Fall 28 → Winter 1 transition. "
                + "IsManaged=false means the OnDayEnd orphan sweep evicted it: entries keyed on "
                + "the shared display Name (\"Cabin\") never resolve via Game1.getLocationFromName, "
                + "so the sweep saw dirt==null and deleted the entry — the immune-guard `continue` "
                + "under test never ran."
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

        var farmhand = await Farmers.ConnectFastAsync(namePrefix: "CropTest", ct: ct);

        var warp = await GameClient.Actions.Warp("Farm", tileX, tileY);
        Assert.True(warp?.Success, $"Warp failed: {warp?.Error}");

        // Game1.warpFarmer is async (queued LocationRequest); poll until the
        // farmhand actually lands on the Farm before placing the pot.
        // PlacePot guards on player.currentLocation matching the target.
        var arrived = await GameClient.WaitForLocationAsync("^Farm$", TimeSpan.FromSeconds(10), ct);
        Assert.NotNull(arrived);

        // Clear the pot's 3×3 neighborhood BEFORE placing, leaving the pot as the only
        // object in it. The one overnight path that destroys the pot is spawnWeedsAndStones
        // with spawnFromOldWeeds=true (Farm.DayUpdate): it spreads debris only within ±1 of
        // an existing object and destroys an unprotected object it lands on (a Garden Pot
        // isn't Fence/Chest/Tapper). An empty neighborhood leaves no source that can reach
        // the pot, and the pot can't target itself (offset re-rolled non-zero,
        // GameLocation.cs:15279). The other Summer-1 passes (spawnFromOldWeeds=false debris,
        // grass spread) only ADD to empty tiles, so they can't remove the pot.
        // removeObjectsAndSpawned uses a top-left origin, so (tile-1) size 3×3 centers on
        // the pot. Supersedes PlacePot's single-tile clearObstacles clear below.
        var cleared = await GameClient.Actions.ClearArea(
            "Farm",
            tileX - 1,
            tileY - 1,
            width: 3,
            height: 3
        );
        Assert.True(cleared?.Success == true, $"ClearArea failed: {cleared?.Error}");

        var place = await GameClient.Actions.PlacePot("Farm", tileX, tileY, clearObstacles: true);
        Assert.True(place?.Success, $"PlacePot failed: {place?.Error}");

        // Crop.TryGetData looks up by unqualified id — passing "(O)474" fails
        // the lookup and HoeDirt.plant returns false silently.
        var plant = await GameClient.Actions.PlantCrop(CauliflowerSeedId, "Farm", tileX, tileY);
        Assert.True(plant?.Success, $"PlantCrop failed: {plant?.Error}");

        return farmhand;
    }

    /// <summary>
    /// Waits up to 5s for the host's CropWatcher (5-tick scan ≈ 1s at
    /// SERVER_TPS=5) to register the pot crop in CropSaverData. Polled via
    /// the host-side /test/crops endpoint, which sets <c>IsManaged=true</c>
    /// when <c>CropSaverData</c> has a matching entry.
    /// </summary>
    private async Task AssertWatcherRegistersPotAsync(
        string locationName,
        int tileX,
        int tileY,
        CancellationToken ct
    )
    {
        var watcherRegisteredPot = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_CropSaver_AwaitWatcher,
            async () =>
            {
                var snapshot = await ServerApi.GetAllCrops(ct);
                return snapshot?.Crops.Any(c =>
                        c.IsInPot
                        && c.IsManaged
                        && c.LocationName == locationName
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
