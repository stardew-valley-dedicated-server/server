using System.Collections.Generic;

namespace JunimoServer.Services.Api;

// Request/response DTOs for the test-only /test/* endpoints. Paired with the
// handlers in ApiService.TestEndpoints.cs. Kept as top-level types (matching the
// production DTOs) so [ApiResponse(typeof(...))] and the OpenAPI schema naming
// resolve unchanged.

/// <summary>
/// A single managed/unmanaged crop snapshot row returned by /test/crops.
/// </summary>
public class TestCrop
{
    /// <summary>Internal location name where the crop lives (e.g. "Farm", "Greenhouse", "IslandWest").</summary>
    public string LocationName { get; set; } = "";

    /// <summary>Tile X coordinate (terrain HoeDirt tile, or pot's TileLocation).</summary>
    public int TileX { get; set; }

    /// <summary>Tile Y coordinate.</summary>
    public int TileY { get; set; }

    /// <summary>True if the crop is alive (not flagged dead).</summary>
    public bool IsAlive { get; set; }

    /// <summary>True if the crop sits inside a Garden Pot (IndoorPot.hoeDirt).</summary>
    public bool IsInPot { get; set; }

    /// <summary>Seed item id used to plant the crop, e.g. "(O)474" for Cauliflower.</summary>
    public string? SeedItemId { get; set; }

    /// <summary>True if CropSaver has a tracking entry for this (locationName, tile).</summary>
    public bool IsManaged { get; set; }
}

/// <summary>
/// Response from /test/crops (test-only enumeration of every HoeDirt-with-crop in the world).
/// </summary>
public class TestCropsResponse
{
    public bool Success { get; set; }
    public List<TestCrop> Crops { get; set; } = new();
    public string? Error { get; set; }
}

/// <summary>
/// Response from /test/set_date (test-only date jump).
/// </summary>
public class TestSetDateResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Season { get; set; }
    public int? Day { get; set; }
    public int? Year { get; set; }
}

/// <summary>
/// Body for POST /test/set_date.
/// </summary>
public class TestSetDateRequest
{
    public string? Season { get; set; }
    public int Day { get; set; }
    public int Year { get; set; }
}

/// <summary>
/// Response from /test/farmevent (test-only: queue an overnight FarmEvent for the next night).
/// </summary>
public class TestFarmEventResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Type { get; set; }
}

/// <summary>
/// Body for POST /test/saver_crop. Mutates an existing CropSaver entry in
/// place. Optional fields are skipped when null. Used by E2E tests to
/// pre-arm extraDays past CropSaver.OnDayEnd's branch-1 floor without
/// having to simulate many real day-transitions.
/// </summary>
public class TestSaverCropRequest
{
    public string? LocationName { get; set; }
    public int TileX { get; set; }
    public int TileY { get; set; }
    public int? ExtraDays { get; set; }
    public long? OwnerId { get; set; }
    public TestSetDateRequest? DatePlanted { get; set; }
}

/// <summary>
/// Response from /test/saver_crop.
/// </summary>
public class TestSaverCropResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>True if a SaverCrop entry was found at the requested (location, tile).</summary>
    public bool Found { get; set; }
    public int? ExtraDays { get; set; }
    public long? OwnerId { get; set; }
}

/// <summary>
/// Response from POST /test/house_upgrade (test-only). Runs a vanilla debug house-upgrade
/// command through parseDebugInput (exercising the HostFarmhouseUpgradeGuard Harmony prefix) and
/// reports the host's resulting HouseUpgradeLevel, so a test can pin "the host farmhouse can't be
/// upgraded" (it must stay 0).
/// </summary>
public class TestHouseUpgradeResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>The host farmer's HouseUpgradeLevel after the debug command ran (expected 0).</summary>
    public int HostHouseUpgradeLevel { get; set; }
}

/// <summary>
/// Response from GET /test/festival_state (test-only). A direct read of the host's
/// festival state, so E2E tests can assert "festival still active" / "festival ended"
/// without proxying through the client's location (which reads "Temp" during a festival)
/// or the timeOfDay jump. Mirrors the signals the AlwaysOnServerFestivals service acts on.
/// </summary>
public class TestFestivalStateResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>True if today is a festival day (SDateHelper.IsFestivalToday).</summary>
    public bool IsFestivalDay { get; set; }

    /// <summary>
    /// The festival map name today, or null. Populated only by a real day-transition
    /// (sleep-through) — empty after /test/set_date alone (see host-automation.md item 3).
    /// </summary>
    public string? WhereIsTodaysFest { get; set; }

    /// <summary>
    /// True once the festival event is loaded and running (Game1.CurrentEvent.isFestival).
    /// This is the headline signal: it stays true while players are at the festival and
    /// flips to false when the festival ends.
    /// </summary>
    public bool IsFestivalActive { get; set; }

    /// <summary>Game1.netReady ready/required counts for the festivalStart check.</summary>
    public int FestivalStartReady { get; set; }
    public int FestivalStartRequired { get; set; }

    /// <summary>Game1.netReady ready/required counts for the festivalEnd check.</summary>
    public int FestivalEndReady { get; set; }
    public int FestivalEndRequired { get; set; }

    /// <summary>Current in-game time (HHMM). Jumps to the festival's reset cutoff once it ends.</summary>
    public int TimeOfDay { get; set; }
}

/// <summary>
/// Response from POST /test/stamp_claim (test-only). Constructs an abandoned-claim slot
/// deterministically: stamps a synthetic userID onto an uncustomized, homed farmhandData entry,
/// reproducing the on-disk shape (<c>userID != "" &amp;&amp; isCustomized == false</c>, homeLocation
/// resolving to a Cabin) that a player leaves when they claim "New Farmer" and quit before
/// customizing. Used to verify the save-load sweep (ClearAbandonedCabinClaimsOnLoad) clears such a
/// claim on reload — the live disconnect heal cannot persist one to disk for a sweep test.
/// </summary>
public class TestStampClaimResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>UniqueMultiplayerID of the farmhand slot that was stamped.</summary>
    public long StampedUid { get; set; }

    /// <summary>The synthetic userID written onto the slot.</summary>
    public string StampedUserId { get; set; } = "";

    /// <summary>The slot's homeLocation after stamping (must resolve to a Cabin for the homed-path test).</summary>
    public string HomeLocation { get; set; } = "";
}
