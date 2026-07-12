using System;
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

    /// <summary>True if this location is season-immune (greenhouse, Ginger Island, indoors)
    /// — vanilla never withers crops here, so CropSaver must not either.</summary>
    public bool IsSeasonImmune { get; set; }
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

/// <summary>One online farmer's server-side tile, from /test/farmers.</summary>
public class TestFarmer
{
    public long Id { get; set; }
    public int TileX { get; set; }
    public int TileY { get; set; }
}

/// <summary>
/// Response from /test/farmers (test-only). Each online farmer's instantaneous server-side
/// tile, read from Game1 — the position the placement validator reads, which the periodic
/// /players snapshot doesn't carry.
/// </summary>
public class TestFarmersResponse
{
    public bool Success { get; set; }
    public List<TestFarmer> Farmers { get; set; } = new();
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

/// <summary>
/// Response from POST /test/stamp_lobby_home (test-only). Reproduces the lobby-homed-spouse
/// poisoned-save shape on a live server: ensures a shared lobby cabin exists (position-classified,
/// so it works on a passwordless server too), synthesizes a marriage between a cabin-homed farmhand
/// and the given NPC, then points the farmhand's homeLocation/lastSleepLocation and the NPC's
/// DefaultMap/DefaultPosition/currentLocation at the lobby interior. Used to verify the
/// CabinManagerService heal sweeps (DayStarted live heal + SaveLoaded migration) restore both.
/// </summary>
public class TestStampLobbyHomeResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>UniqueMultiplayerID of the farmhand whose home fields were poisoned.</summary>
    public long StampedUid { get; set; }

    /// <summary>The NPC married to the farmhand and stranded in the lobby.</summary>
    public string Npc { get; set; } = "";

    /// <summary>The lobby cabin interior's NameOrUniqueName both victims now point at.</summary>
    public string LobbyLocation { get; set; } = "";

    /// <summary>The farmhand's homeLocation before poisoning — the heal must restore exactly this
    /// (ownership-first reassignment returns them to their own cabin).</summary>
    public string OriginalHome { get; set; } = "";
}

/// <summary>
/// Response from POST /test/galaxy_relogin (test-only). Triggers a Galaxy re-sign-in with no outage,
/// so a test can verify that re-login while Galaxy is healthy and a client is connected does not
/// disrupt the live lobby or change the invite code (the no-op safety question for the
/// Steam-reconnect-triggered Galaxy-reauth fix).
/// </summary>
public class TestGalaxyReloginResponse
{
    /// <summary>True if the re-sign-in was triggered (Galaxy was initialized); false otherwise.</summary>
    public bool Triggered { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Body for POST /test/seed_import_source (test-only). All fields optional. Seeds the active game's
/// master (Game1.player — what the swap import demotes) and FarmHouse to look like a real importable
/// owner before SleepToSaveAsync, so the save's &lt;player&gt; reads as a played human and carries the
/// world/relationship/house state and contents the save-import tests assert.
/// </summary>
public class TestSeedImportSourceRequest
{
    /// <summary>Override the master's name (default "ImportedOwner"). Must NOT be "Server".</summary>
    public string? OwnerName { get; set; }

    /// <summary>Set the master's house upgrade level (drives the cabin level + cellar warn).</summary>
    public int? HouseUpgradeLevel { get; set; }

    /// <summary>Set the master's caveChoice (1 = bat/fruit cave) — bucket-B carry test (7b).</summary>
    public int? CaveChoice { get; set; }

    /// <summary>Set the master's NPC spouse name — spouse-clear-on-master test (8).</summary>
    public string? Spouse { get; set; }

    /// <summary>Add a mailReceived flag (e.g. "ccDoorUnlock") — mail/event carry test (7).</summary>
    public string? MailFlag { get; set; }

    /// <summary>Add an eventsSeen id — mail/event carry test (7).</summary>
    public string? EventSeen { get; set; }

    /// <summary>Set a friendshipData[key].Points entry — shadow-pacifism gate carry test (7c).</summary>
    public int? ShadowFriendshipPoints { get; set; }

    /// <summary>The friendshipData key for ShadowFriendshipPoints (default "Krobus").</summary>
    public string? ShadowFriendshipKey { get; set; }

    /// <summary>Set stats.DaysPlayed — same-day-reconnect gate carry test (7c).</summary>
    public int? DaysPlayed { get; set; }

    /// <summary>Place a chest (with a known item) in the FarmHouse — contents-move test (2).</summary>
    public bool PlaceChest { get; set; }
    public int ChestTileX { get; set; } = 3;
    public int ChestTileY { get; set; } = 3;

    /// <summary>Add an item to the FarmHouse fridge — contents-move test (2).</summary>
    public bool PlaceFridgeItem { get; set; }

    /// <summary>Spawn a pet into the FarmHouse — household-relocation test (3).</summary>
    public bool SpawnPet { get; set; }

    /// <summary>Place an item in the master's "Cellar"-1 — cellar-contents-move test (11).</summary>
    public bool PlaceCellarItem { get; set; }

    /// <summary>Stamp this userID onto a spare uncustomized farmhand slot — userID-collision test (10).</summary>
    public string? InjectFarmhandUserId { get; set; }
}

/// <summary>Response from POST /test/seed_import_source.</summary>
public class TestSeedImportSourceResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>The seeded master's UniqueMultiplayerID (becomes the demoted owner's uid after swap).</summary>
    public long OwnerUid { get; set; }
    public string OwnerName { get; set; } = "";
    public bool ChestPlaced { get; set; }
    public bool FridgeItemPlaced { get; set; }
    public bool PetSpawned { get; set; }
    public bool CellarItemPlaced { get; set; }
    public bool FarmhandUserIdInjected { get; set; }
}

/// <summary>Body for POST /test/corrupt_save (test-only).</summary>
public class TestCorruptSaveRequest
{
    /// <summary>Folder to clone from (default: the active save).</summary>
    public string? SourceSaveName { get; set; }

    /// <summary>Folder to clone to + corrupt (required).</summary>
    public string? TargetSaveName { get; set; }
}

/// <summary>Response from POST /test/corrupt_save / GET /test/save_tmp_exists.</summary>
public class TestSaveFileOpResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>For save_tmp_exists: whether a leftover .tmp exists next to the main file.</summary>
    public bool Exists { get; set; }

    /// <summary>For corrupt_save: the actual clone folder name created (derived from a re-stamped
    /// uniqueID); the caller imports this with SkipClone.</summary>
    public string TargetSaveName { get; set; } = "";
}

/// <summary>Response from POST /test/force_save (test-only). Persists the current world to disk
/// synchronously (no day transition), so save-import source generation can skip SleepToSaveAsync.</summary>
public class TestForceSaveResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>The save folder the world was written to (Constants.SaveFolderName).</summary>
    public string SaveFolderName { get; set; } = "";
}

/// <summary>
/// Body for POST /test/import_save (test-only). Clones a source save folder under a new name, then
/// runs saves-import on the clone (ExecuteImport rejects importing the active save, so the clone is
/// required). Defaults: source = the active save, target = source + "-import".
/// </summary>
public class TestImportSaveRequest
{
    /// <summary>Folder to clone from (default: the currently-active save).</summary>
    public string? SourceSaveName { get; set; }

    /// <summary>Folder to clone to + import (default: SourceSaveName + "-import").</summary>
    public string? TargetSaveName { get; set; }

    /// <summary>The platform id to bind on swap (all-digit Steam64/Galaxy id). Absent = as-is import.</summary>
    public string? SwapHostTo { get; set; }

    /// <summary>Skip the clone step (import an existing/pre-corrupted target directly — resilience test).</summary>
    public bool SkipClone { get; set; }
}

/// <summary>
/// Response from GET /test/wedding_state (test-only). A direct read of the host's wedding state so an
/// E2E test can assert a ceremony is active / has ended and observe each spouse NPC's location, without
/// proxying through a client's location (which reads the Town "Temp" map during the ceremony).
/// </summary>
public class TestWeddingStateResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>True while a wedding event is loaded and running on the host (CurrentEvent.isWedding).</summary>
    public bool IsWeddingActive { get; set; }

    /// <summary>The host's view of the queried farmhand's spouse (NPC name), or null. Lets a test
    /// confirm the engagement replicated client→host before the day transition.</summary>
    public string? FarmhandSpouse { get; set; }

    /// <summary>The spouse NPC's current location. The ceremony's endBehaviors ("wedding" case) warps
    /// the spouse to the couple's Farm porch, so <c>== "Farm"</c> is a durable, ceremony-only signal
    /// that this couple's ceremony completed (the pre-ceremony finalizer never moves currentLocation).</summary>
    public string? SpouseCurrentLocation { get; set; }

    // Post-ceremony host-stuck signals (see HandleGetTestWeddingStateAsync). All four must be clear
    // after a wedding ends or the host is stuck in the fadeout the multi-wedding fix resolves.

    /// <summary>True while any event is up on the host. Stuck-true after a wedding = event never ended.</summary>
    public bool EventUp { get; set; }

    /// <summary>True while a fade-to-black is armed. Stuck-true after a wedding = the black fadeout never cleared.</summary>
    public bool FadeToBlack { get; set; }

    /// <summary>True while a dialogue is up. Stuck-true after a wedding = the after-wedding dialogue is dangling.</summary>
    public bool DialogueUp { get; set; }

    /// <summary>True while the host is on a temporary event map (e.g. the wedding ceremony Town map).
    /// Stuck-true after a wedding = the host never warped off the ceremony location.</summary>
    public bool HostLocationIsTemporary { get; set; }

    /// <summary>The host's current location name. After the day's last wedding the host must be returned
    /// to its FarmHouse idle spot — not left standing on the open Farm map where the wedding exit warp
    /// drops it (the exit targets getHomeOfFarmer(Game1.player)'s porch). Lets a test assert the host
    /// ended at home rather than just "not on a temporary map".</summary>
    public string? HostCurrentLocation { get; set; }
}

/// <summary>Body for POST /test/console (test-only): a console command name + args to invoke.</summary>
public class TestConsoleCommandRequest
{
    public string Name { get; set; } = ""; // e.g. "saves"

    public string[] Args { get; set; } = Array.Empty<string>(); // e.g. ["reload", "--force"]
}

/// <summary>Response from POST /test/console.</summary>
public class TestConsoleCommandResponse
{
    /// <summary>True if the named command was found and its callback invoked without throwing.</summary>
    public bool Success { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Response from GET /test/npc_sprite_integrity (test-only). Reports the
/// NpcSpriteIntegrityService's sweep metadata plus a live scan for sprite-less NPCs, so E2E
/// tests can prove both the heal outcome and the SaveLoaded/DayStarted wiring (the run
/// counters only advance when the real event handlers fire — a direct /test/heal_npc_sprites
/// call can't satisfy them).
/// </summary>
public class TestNpcSpriteIntegrityResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>Names of NPCs currently missing a sprite (live scan; empty when healthy).</summary>
    public List<string> SpritelessNpcs { get; set; } = new();

    /// <summary>Context tag of the most recent sweep ("save_loaded", "day_started", "test"), or null.</summary>
    public string? LastRunContext { get; set; }

    /// <summary>NPCs healed by the most recent sweep.</summary>
    public int LastRunHealedCount { get; set; }

    /// <summary>Sweeps triggered by the SaveLoaded handler since mod start.</summary>
    public int SaveLoadedRuns { get; set; }

    /// <summary>Sweeps triggered by the DayStarted handler since mod start.</summary>
    public int DayStartedRuns { get; set; }

    /// <summary>NPCs healed across all sweeps since mod start.</summary>
    public int TotalHealed { get; set; }
}

/// <summary>
/// Response from POST /test/break_npc_sprite (test-only fault injector — see the handler doc).
/// </summary>
public class TestBreakNpcSpriteResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>The NPC whose sprite was nulled.</summary>
    public string NpcName { get; set; } = "";

    /// <summary>True if the NPC had a sprite before the break (sanity signal for the test).</summary>
    public bool HadSprite { get; set; }
}

/// <summary>Response from POST /test/heal_npc_sprites (test-only).</summary>
public class TestHealNpcSpritesResponse
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>NPCs healed by this sweep run.</summary>
    public int HealedCount { get; set; }
}

/// <summary>Response from POST /test/import_save.</summary>
public class TestImportSaveResponse
{
    /// <summary>True if the endpoint's own steps (clone + dispatch) succeeded.</summary>
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>The target save folder the import ran against.</summary>
    public string TargetSaveName { get; set; } = "";

    /// <summary>Whether the import was a host swap (vs as-is).</summary>
    public bool Swapped { get; set; }

    /// <summary>Whether the import only re-pointed an existing pending bind.</summary>
    public bool RepointedBind { get; set; }

    /// <summary>The demoted owner's UniqueMultiplayerID (swap only).</summary>
    public long FormerOwnerUid { get; set; }

    /// <summary>The import's own error/warn message (when ImportError is set, the import did not succeed).</summary>
    public string? ImportError { get; set; }

    /// <summary>SHA-256 (base64) of the target main file before import — for the byte-unchanged assertion.</summary>
    public string PreImportMainFileHash { get; set; } = "";

    /// <summary>SHA-256 (base64) of the target main file after import.</summary>
    public string PostImportMainFileHash { get; set; } = "";
}
