using System.Net.Http.Json;
using System.Text.Json.Serialization;
using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;

namespace JunimoServer.Tests.Clients;

/// <summary>
/// Server status data returned by the /status endpoint.
/// Mirrors the ServerStatus class from ApiService.
/// </summary>
public class ServerStatus
{
    [JsonPropertyName("playerCount")]
    public int PlayerCount { get; set; }

    [JsonPropertyName("maxPlayers")]
    public int MaxPlayers { get; set; }

    [JsonPropertyName("steamInviteCode")]
    public string? SteamInviteCode { get; set; }

    [JsonPropertyName("gogInviteCode")]
    public string? GogInviteCode { get; set; }

    /// <summary>
    /// Gets the preferred invite code (Steam if available, otherwise GOG).
    /// </summary>
    [JsonIgnore]
    public string InviteCode => SteamInviteCode ?? GogInviteCode ?? string.Empty;

    [JsonPropertyName("serverVersion")]
    public string ServerVersion { get; set; } = string.Empty;

    [JsonPropertyName("isOnline")]
    public bool IsOnline { get; set; }

    [JsonPropertyName("isReady")]
    public bool IsReady { get; set; }

    [JsonPropertyName("lastUpdated")]
    public string LastUpdated { get; set; } = string.Empty;

    // Game state fields
    [JsonPropertyName("farmName")]
    public string FarmName { get; set; } = string.Empty;

    [JsonPropertyName("day")]
    public int Day { get; set; }

    [JsonPropertyName("season")]
    public string Season { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int Year { get; set; }

    [JsonPropertyName("timeOfDay")]
    public int TimeOfDay { get; set; }

    /// <summary>Farm type key from Game1.GetFarmTypeKey() (a vanilla name, or the farm Id for Data/AdditionalFarms farms).</summary>
    [JsonPropertyName("farmTypeKey")]
    public string FarmTypeKey { get; set; } = string.Empty;

    [JsonPropertyName("isPaused")]
    public bool IsPaused { get; set; }

    /// <summary>
    /// Monotonic snapshot version. Pass back as <c>?since=N</c> on
    /// <c>/wait/status</c> long-poll requests to wait for a newer snapshot.
    /// </summary>
    [JsonPropertyName("version")]
    public long Version { get; set; }
}

/// <summary>
/// Player info returned by the /players endpoint.
/// </summary>
public class PlayerInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isOnline")]
    public bool IsOnline { get; set; }
}

/// <summary>
/// Response from the /players endpoint.
/// </summary>
public class PlayersResponse
{
    [JsonPropertyName("players")]
    public List<PlayerInfo> Players { get; set; } = new();

    /// <summary>
    /// Monotonic snapshot version. Pass back as <c>?since=N</c> on
    /// <c>/wait/players</c> long-poll requests to wait for a newer snapshot.
    /// </summary>
    [JsonPropertyName("version")]
    public long Version { get; set; }
}

/// <summary>
/// Response from the /invite-code endpoint.
/// </summary>
public class InviteCodeResponse
{
    [JsonPropertyName("inviteCode")]
    public string? InviteCode { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from the <c>/diagnostics/state</c> endpoint. Ground-truth live
/// game-engine state for test-failure analysis. Populated best-effort on the
/// server — missing fields are listed in <see cref="FailedFields"/> and the
/// response still returns 200.
/// </summary>
public class DiagnosticsStateResponse
{
    [JsonPropertyName("capturedAt")]
    public string CapturedAt { get; set; } = "";

    [JsonPropertyName("otherFarmerUids")]
    public long[] OtherFarmerUids { get; set; } = Array.Empty<long>();

    [JsonPropertyName("onlineFarmerCount")]
    public int OnlineFarmerCount { get; set; }

    [JsonPropertyName("netReady")]
    public List<ReadyCheckState> NetReady { get; set; } = new();

    [JsonPropertyName("newDaySync")]
    public NewDaySyncState NewDaySync { get; set; } = new();

    [JsonPropertyName("activeClickableMenu")]
    public string? ActiveClickableMenu { get; set; }

    [JsonPropertyName("timeOfDay")]
    public int TimeOfDay { get; set; }

    [JsonPropertyName("dayOfMonth")]
    public int DayOfMonth { get; set; }

    [JsonPropertyName("season")]
    public string Season { get; set; } = "";

    [JsonPropertyName("year")]
    public int Year { get; set; }

    [JsonPropertyName("gameMode")]
    public int GameMode { get; set; }

    [JsonPropertyName("isGameAvailable")]
    public bool? IsGameAvailable { get; set; }

    [JsonPropertyName("lastTickMs")]
    public long? LastTickMs { get; set; }

    [JsonPropertyName("avgGameThreadWaitMs")]
    public double AvgGameThreadWaitMs { get; set; }

    [JsonPropertyName("cabins")]
    public List<DiagnosticsCabinState> Cabins { get; set; } = new();

    [JsonPropertyName("farmhandData")]
    public List<DiagnosticsFarmhandState> FarmhandData { get; set; } = new();

    [JsonPropertyName("disconnectingFarmers")]
    public long[] DisconnectingFarmers { get; set; } = Array.Empty<long>();

    // ── Save-import assertion probes (mirror the mod-side DiagnosticsStateResponse). ──
    [JsonPropertyName("farmHouseObjectCount")]
    public int FarmHouseObjectCount { get; set; }

    [JsonPropertyName("farmHouseFurnitureCount")]
    public int FarmHouseFurnitureCount { get; set; }

    [JsonPropertyName("farmHouseFridgeItemCount")]
    public int FarmHouseFridgeItemCount { get; set; }

    [JsonPropertyName("masterCellarObjectCount")]
    public int MasterCellarObjectCount { get; set; }

    [JsonPropertyName("masterHasFlag")]
    public bool? MasterHasFlag { get; set; }

    [JsonPropertyName("masterCaveChoice")]
    public int MasterCaveChoice { get; set; }

    [JsonPropertyName("masterShadowFriendshipPoints")]
    public int? MasterShadowFriendshipPoints { get; set; }

    [JsonPropertyName("masterDaysPlayed")]
    public int MasterDaysPlayed { get; set; }

    [JsonPropertyName("masterHasSpouse")]
    public bool MasterHasSpouse { get; set; }

    [JsonPropertyName("masterName")]
    public string MasterName { get; set; } = "";

    [JsonPropertyName("saveImportFinalizeCount")]
    public int SaveImportFinalizeCount { get; set; }

    [JsonPropertyName("failedFields")]
    public List<string> FailedFields { get; set; } = new();
}

public class DiagnosticsCabinState
{
    [JsonPropertyName("tileX")]
    public int TileX { get; set; }

    [JsonPropertyName("tileY")]
    public int TileY { get; set; }

    [JsonPropertyName("indoorsName")]
    public string IndoorsName { get; set; } = "";

    [JsonPropertyName("ownerId")]
    public long OwnerId { get; set; }

    [JsonPropertyName("ownerName")]
    public string OwnerName { get; set; } = "";

    [JsonPropertyName("ownerIsCustomized")]
    public bool OwnerIsCustomized { get; set; }

    /// <summary>Whether the owner has a platform ID (Steam/GOG) stamped; true with
    /// OwnerIsCustomized=false is the abandoned-claim state. Resolved via cabin.owner, so it
    /// reflects the live otherFarmers copy while the owner is connected. A bool, not the raw ID
    /// (/diagnostics/state is unauthenticated).</summary>
    [JsonPropertyName("ownerHasUserId")]
    public bool OwnerHasUserId { get; set; }

    [JsonPropertyName("homeLocationOfOwner")]
    public string HomeLocationOfOwner { get; set; } = "";

    [JsonPropertyName("farmhandReferenceDefined")]
    public bool FarmhandReferenceDefined { get; set; }

    [JsonPropertyName("farmhandReferenceUid")]
    public long FarmhandReferenceUid { get; set; }

    [JsonPropertyName("objectCount")]
    public int ObjectCount { get; set; }

    [JsonPropertyName("fridgeItemCount")]
    public int FridgeItemCount { get; set; }

    [JsonPropertyName("petCount")]
    public int PetCount { get; set; }

    [JsonPropertyName("cellarObjectCount")]
    public int CellarObjectCount { get; set; }
}

public class DiagnosticsFarmhandState
{
    [JsonPropertyName("uniqueMultiplayerId")]
    public long UniqueMultiplayerId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("isCustomized")]
    public bool IsCustomized { get; set; }

    [JsonPropertyName("homeLocation")]
    public string HomeLocation { get; set; } = "";

    [JsonPropertyName("lastSleepLocation")]
    public string LastSleepLocation { get; set; } = "";

    /// <summary>Whether a platform ID (Steam/GOG) is stamped on this slot; true with
    /// IsCustomized=false is the abandoned-claim state. A bool, not the raw ID
    /// (/diagnostics/state is unauthenticated).</summary>
    [JsonPropertyName("hasUserId")]
    public bool HasUserId { get; set; }
}

public class ReadyCheckState
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("numberReady")]
    public int NumberReady { get; set; }

    [JsonPropertyName("numberRequired")]
    public int NumberRequired { get; set; }

    [JsonPropertyName("isReady")]
    public bool IsReady { get; set; }

    [JsonPropertyName("isLocked")]
    public bool IsLocked { get; set; }
}

public class NewDaySyncState
{
    [JsonPropertyName("hasStarted")]
    public bool HasStarted { get; set; }

    [JsonPropertyName("hasFinished")]
    public bool HasFinished { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}

/// <summary>
/// Response from the /health endpoint.
/// </summary>
public class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("timestamp")]
    public string Timestamp { get; set; } = string.Empty;

    [JsonPropertyName("lastTickMs")]
    public long? LastTickMs { get; set; }

    [JsonPropertyName("pendingActions")]
    public int PendingActions { get; set; }

    [JsonPropertyName("gameAvailable")]
    public bool? GameAvailable { get; set; }
}

/// <summary>
/// Response from farmhand operations.
/// </summary>
public class FarmhandOperationResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Information about a farmhand slot.
/// </summary>
public class ServerFarmhandInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isCustomized")]
    public bool IsCustomized { get; set; }
}

/// <summary>
/// Response from /farmhands endpoint.
/// </summary>
public class ServerFarmhandsResponse
{
    [JsonPropertyName("farmhands")]
    public List<ServerFarmhandInfo> Farmhands { get; set; } = new();

    [JsonPropertyName("version")]
    public long Version { get; set; }
}

/// <summary>
/// Response from /rendering GET endpoint.
/// </summary>
public class RenderingStatusResponse
{
    /// <summary>Render rate: 0 = disabled, N &gt; 0 = enabled at N fps.</summary>
    [JsonPropertyName("fps")]
    public int Fps { get; set; }
}

/// <summary>
/// Response from /rendering POST endpoint.
/// </summary>
public class RenderingSetResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>Render rate after the operation: 0 = disabled, N &gt; 0 = at N fps.</summary>
    [JsonPropertyName("fps")]
    public int Fps { get; set; }

    /// <summary>Render rate before the operation.</summary>
    [JsonPropertyName("previousFps")]
    public int PreviousFps { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from /time POST endpoint.
/// </summary>
public class TimeSetResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("timeOfDay")]
    public int TimeOfDay { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from /clock-speed POST endpoint.
/// </summary>
public class ClockSpeedResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("multiplier")]
    public double Multiplier { get; set; }

    [JsonPropertyName("effectiveMs")]
    public int EffectiveMs { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// One row from the /test/crops snapshot. Mirrors the server-side TestCrop DTO.
/// </summary>
public class TestCrop
{
    [JsonPropertyName("locationName")]
    public string LocationName { get; set; } = "";

    [JsonPropertyName("tileX")]
    public int TileX { get; set; }

    [JsonPropertyName("tileY")]
    public int TileY { get; set; }

    [JsonPropertyName("isAlive")]
    public bool IsAlive { get; set; }

    [JsonPropertyName("isInPot")]
    public bool IsInPot { get; set; }

    [JsonPropertyName("seedItemId")]
    public string? SeedItemId { get; set; }

    /// <summary>True if CropSaver has a tracking entry for this (locationName, tile).</summary>
    [JsonPropertyName("isManaged")]
    public bool IsManaged { get; set; }

    /// <summary>True if this location is season-immune (greenhouse, Ginger Island, indoors)
    /// — vanilla never withers crops here, so CropSaver must not either.</summary>
    [JsonPropertyName("isSeasonImmune")]
    public bool IsSeasonImmune { get; set; }
}

/// <summary>
/// Response from /test/crops GET endpoint.
/// </summary>
public class TestCropsResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("crops")]
    public List<TestCrop> Crops { get; set; } = new();

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from /test/festival_state GET endpoint (test-only). Mirrors the server-side
/// TestFestivalStateResponse DTO — a direct read of the host's festival state.
/// </summary>
public class TestFestivalStateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("isFestivalDay")]
    public bool IsFestivalDay { get; set; }

    [JsonPropertyName("whereIsTodaysFest")]
    public string? WhereIsTodaysFest { get; set; }

    [JsonPropertyName("isFestivalActive")]
    public bool IsFestivalActive { get; set; }

    [JsonPropertyName("festivalStartReady")]
    public int FestivalStartReady { get; set; }

    [JsonPropertyName("festivalStartRequired")]
    public int FestivalStartRequired { get; set; }

    [JsonPropertyName("festivalEndReady")]
    public int FestivalEndReady { get; set; }

    [JsonPropertyName("festivalEndRequired")]
    public int FestivalEndRequired { get; set; }

    [JsonPropertyName("timeOfDay")]
    public int TimeOfDay { get; set; }
}

/// <summary>
/// Response from /test/set_date POST endpoint.
/// </summary>
public class TestSetDateResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("season")]
    public string? Season { get; set; }

    [JsonPropertyName("day")]
    public int? Day { get; set; }

    [JsonPropertyName("year")]
    public int? Year { get; set; }
}

/// <summary>
/// Response from /test/farmevent POST endpoint (test-only).
/// </summary>
public class TestFarmEventResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

/// <summary>
/// Response from /test/house_upgrade POST endpoint (test-only). Mirrors the server-side
/// TestHouseUpgradeResponse DTO.
/// </summary>
public class TestHouseUpgradeResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("hostHouseUpgradeLevel")]
    public int HostHouseUpgradeLevel { get; set; }
}

/// <summary>
/// Response from /test/stamp_claim POST endpoint (test-only). Mirrors the server-side
/// TestStampClaimResponse DTO. Stamps a synthetic abandoned slot claim onto an uncustomized,
/// homed farmhand so the save-load sweep can be exercised on reload.
/// </summary>
public class TestStampClaimResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("stampedUid")]
    public long StampedUid { get; set; }

    [JsonPropertyName("stampedUserId")]
    public string StampedUserId { get; set; } = "";

    [JsonPropertyName("homeLocation")]
    public string HomeLocation { get; set; } = "";
}

/// <summary>Body for /test/seed_import_source (test-only). Mirrors the server-side DTO.</summary>
public class TestSeedImportSourceRequest
{
    [JsonPropertyName("ownerName")]
    public string? OwnerName { get; set; }

    [JsonPropertyName("houseUpgradeLevel")]
    public int? HouseUpgradeLevel { get; set; }

    [JsonPropertyName("caveChoice")]
    public int? CaveChoice { get; set; }

    [JsonPropertyName("spouse")]
    public string? Spouse { get; set; }

    [JsonPropertyName("mailFlag")]
    public string? MailFlag { get; set; }

    [JsonPropertyName("eventSeen")]
    public string? EventSeen { get; set; }

    [JsonPropertyName("shadowFriendshipPoints")]
    public int? ShadowFriendshipPoints { get; set; }

    [JsonPropertyName("shadowFriendshipKey")]
    public string? ShadowFriendshipKey { get; set; }

    [JsonPropertyName("daysPlayed")]
    public int? DaysPlayed { get; set; }

    [JsonPropertyName("placeChest")]
    public bool PlaceChest { get; set; }

    [JsonPropertyName("chestTileX")]
    public int ChestTileX { get; set; } = 3;

    [JsonPropertyName("chestTileY")]
    public int ChestTileY { get; set; } = 3;

    [JsonPropertyName("placeFridgeItem")]
    public bool PlaceFridgeItem { get; set; }

    [JsonPropertyName("spawnPet")]
    public bool SpawnPet { get; set; }

    [JsonPropertyName("placeCellarItem")]
    public bool PlaceCellarItem { get; set; }

    [JsonPropertyName("injectFarmhandUserId")]
    public string? InjectFarmhandUserId { get; set; }
}

/// <summary>Response from /test/seed_import_source (test-only).</summary>
public class TestSeedImportSourceResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("ownerUid")]
    public long OwnerUid { get; set; }

    [JsonPropertyName("ownerName")]
    public string OwnerName { get; set; } = "";

    [JsonPropertyName("chestPlaced")]
    public bool ChestPlaced { get; set; }

    [JsonPropertyName("fridgeItemPlaced")]
    public bool FridgeItemPlaced { get; set; }

    [JsonPropertyName("petSpawned")]
    public bool PetSpawned { get; set; }

    [JsonPropertyName("cellarItemPlaced")]
    public bool CellarItemPlaced { get; set; }

    [JsonPropertyName("farmhandUserIdInjected")]
    public bool FarmhandUserIdInjected { get; set; }
}

/// <summary>Body for /test/corrupt_save (test-only).</summary>
public class TestCorruptSaveRequest
{
    [JsonPropertyName("sourceSaveName")]
    public string? SourceSaveName { get; set; }

    [JsonPropertyName("targetSaveName")]
    public string? TargetSaveName { get; set; }
}

/// <summary>Response from /test/corrupt_save and /test/save_tmp_exists (test-only).</summary>
public class TestSaveFileOpResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("exists")]
    public bool Exists { get; set; }

    [JsonPropertyName("targetSaveName")]
    public string TargetSaveName { get; set; } = "";
}

/// <summary>Body for /test/import_save (test-only). Mirrors the server-side DTO.</summary>
public class TestImportSaveRequest
{
    [JsonPropertyName("sourceSaveName")]
    public string? SourceSaveName { get; set; }

    [JsonPropertyName("targetSaveName")]
    public string? TargetSaveName { get; set; }

    [JsonPropertyName("swapHostTo")]
    public string? SwapHostTo { get; set; }

    [JsonPropertyName("skipClone")]
    public bool SkipClone { get; set; }
}

/// <summary>Response from /test/import_save (test-only).</summary>
public class TestImportSaveResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("targetSaveName")]
    public string TargetSaveName { get; set; } = "";

    [JsonPropertyName("swapped")]
    public bool Swapped { get; set; }

    [JsonPropertyName("repointedBind")]
    public bool RepointedBind { get; set; }

    [JsonPropertyName("formerOwnerUid")]
    public long FormerOwnerUid { get; set; }

    [JsonPropertyName("importError")]
    public string? ImportError { get; set; }

    [JsonPropertyName("preImportMainFileHash")]
    public string PreImportMainFileHash { get; set; } = "";

    [JsonPropertyName("postImportMainFileHash")]
    public string PostImportMainFileHash { get; set; } = "";
}

/// <summary>
/// Response from /test/galaxy_relogin POST endpoint (test-only). Mirrors the server-side
/// TestGalaxyReloginResponse DTO. Triggers a Galaxy re-sign-in with no outage so a test can
/// verify re-login while healthy doesn't disrupt the live lobby / invite code.
/// </summary>
public class TestGalaxyReloginResponse
{
    [JsonPropertyName("triggered")]
    public bool Triggered { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from /test/saver_crop POST endpoint (test-only).
/// </summary>
public class TestSaverCropResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>True iff a SaverCrop entry was found at the requested (location, tile).</summary>
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("extraDays")]
    public int? ExtraDays { get; set; }

    [JsonPropertyName("ownerId")]
    public long? OwnerId { get; set; }
}

/// <summary>
/// Response from /roles/admin POST endpoint.
/// </summary>
public class RoleGrantResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("playerId")]
    public long PlayerId { get; set; }

    [JsonPropertyName("playerName")]
    public string? PlayerName { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from /settings GET endpoint.
/// </summary>
public class SettingsResponse
{
    [JsonPropertyName("game")]
    public GameSettingsInfo Game { get; set; } = new();

    [JsonPropertyName("server")]
    public ServerRuntimeSettingsInfo Server { get; set; } = new();
}

/// <summary>
/// Game creation settings from server-settings.json.
/// </summary>
public class GameSettingsInfo
{
    [JsonPropertyName("farmName")]
    public string FarmName { get; set; } = string.Empty;

    [JsonPropertyName("farmType")]
    public FarmTypeSetting FarmType { get; set; }

    [JsonPropertyName("profitMargin")]
    public float ProfitMargin { get; set; }

    [JsonPropertyName("startingCabins")]
    public int StartingCabins { get; set; }

    [JsonPropertyName("spawnMonstersAtNight")]
    public string SpawnMonstersAtNight { get; set; } = string.Empty;
}

/// <summary>
/// Server runtime settings from server-settings.json.
/// </summary>
public class ServerRuntimeSettingsInfo
{
    [JsonPropertyName("maxPlayers")]
    public int MaxPlayers { get; set; }

    [JsonPropertyName("cabinStrategy")]
    public string CabinStrategy { get; set; } = string.Empty;

    [JsonPropertyName("separateWallets")]
    public bool SeparateWallets { get; set; }

    [JsonPropertyName("existingCabinBehavior")]
    public string ExistingCabinBehavior { get; set; } = string.Empty;
}

/// <summary>
/// Response from /auth GET endpoint.
/// </summary>
public class AuthStatusResponse
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; }

    [JsonPropertyName("authenticatedCount")]
    public int AuthenticatedCount { get; set; }

    [JsonPropertyName("pendingCount")]
    public int PendingCount { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; }

    [JsonPropertyName("maxAttempts")]
    public int MaxAttempts { get; set; }
}

/// <summary>
/// Response from /auth/timeout POST endpoint.
/// </summary>
public class AuthTimeoutResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; }

    [JsonPropertyName("previousTimeoutSeconds")]
    public int PreviousTimeoutSeconds { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Response from /cabins GET endpoint.
/// </summary>
public class CabinsResponse
{
    [JsonPropertyName("strategy")]
    public string Strategy { get; set; } = string.Empty;

    [JsonPropertyName("totalCount")]
    public int TotalCount { get; set; }

    [JsonPropertyName("assignedCount")]
    public int AssignedCount { get; set; }

    [JsonPropertyName("availableCount")]
    public int AvailableCount { get; set; }

    [JsonPropertyName("cabins")]
    public List<CabinInfoResponse> Cabins { get; set; } = new();

    [JsonPropertyName("savedPositionPlayerIds")]
    public List<long> SavedPositionPlayerIds { get; set; } = new();
}

/// <summary>
/// Information about a single cabin.
/// </summary>
public class CabinInfoResponse
{
    [JsonPropertyName("tileX")]
    public int TileX { get; set; }

    [JsonPropertyName("tileY")]
    public int TileY { get; set; }

    [JsonPropertyName("isHidden")]
    public bool IsHidden { get; set; }

    /// <summary>
    /// The cabin type: "Normal", "CabinStack", "FarmhouseStack", or "Lobby".
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "Normal";

    [JsonPropertyName("ownerId")]
    public long OwnerId { get; set; }

    [JsonPropertyName("ownerName")]
    public string OwnerName { get; set; } = string.Empty;

    [JsonPropertyName("isAssigned")]
    public bool IsAssigned { get; set; }
}

/// <summary>
/// HTTP client for the JunimoServer API.
/// Default port is 8080 (configurable via API_PORT env var in the server).
/// </summary>
public class ServerApiClient : IDisposable
{
    /// <summary>
    /// Server-side hard cap on /wait/* timeouts (mirrors ApiService.WaitMaxTimeout).
    /// Sent as the default <c>?timeout=</c> when callers don't provide their own.
    /// The server clamps anything larger to this value, so requesting longer is
    /// pointless — outer re-issue loops are bounded by the caller's deadline.
    /// </summary>
    private static readonly TimeSpan DefaultWaitServerTimeout = TimeSpan.FromSeconds(10);

    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public ServerApiClient(string baseUrl)
    {
        _baseUrl = baseUrl;
        var handler = new TracingHandler("server") { InnerHandler = new HttpClientHandler() };
        _httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl),
            // Server-side operations like /newgame can take up to 120s.
            // Default 100s is too short and causes spurious timeouts.
            Timeout = TimeSpan.FromMinutes(5),
        };
    }

    /// <summary>
    /// Sends an HTTP request with automatic retry on 503 (Service Unavailable).
    /// The server returns 503 when the game thread is blocked during day transitions
    /// or saves. These are transient and resolve within seconds. Only mutating
    /// endpoints (POST /time, DELETE /farmhands, etc.) can return 503; read-only
    /// endpoints use a periodic snapshot and call _httpClient directly.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(
        HttpMethod method,
        string path,
        CancellationToken ct,
        Func<HttpContent>? contentFactory = null
    )
    {
        for (var attempt = 1; ; attempt++)
        {
            var request = new HttpRequestMessage(method, path);
            if (contentFactory != null)
            {
                request.Content = contentFactory();
            }

            var response = await _httpClient.SendAsync(request, ct);

            if (
                response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable
                || attempt >= TestTimings.GameThreadRetryMaxAttempts
            )
            {
                return response;
            }

            // 503: game thread busy, retry after delay
            response.Dispose();
            InfrastructureEventLog.Emit(
                "http_503_retry",
                new
                {
                    path,
                    attempt,
                    maxAttempts = TestTimings.GameThreadRetryMaxAttempts,
                }
            );
            await Task.Delay(TestTimings.GameThreadRetryDelay, ct);
        }
    }

    /// <summary>
    /// Gets the WebSocket URL for the /ws endpoint.
    /// </summary>
    public string GetWebSocketUrl()
    {
        return _baseUrl.Replace("http://", "ws://").Replace("https://", "wss://") + "/ws";
    }

    /// <summary>
    /// Gets the server status including player count, invite code, game state, etc.
    /// Server reads from a periodic snapshot; never blocks on the game thread.
    /// GET /status
    /// </summary>
    public async Task<ServerStatus?> GetStatus(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/status", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServerStatus>(ct);
    }

    /// <summary>
    /// Long-poll variant of <see cref="GetStatus"/>. Server blocks until the
    /// snapshot version exceeds <paramref name="since"/> AND the requested
    /// filters (<paramref name="isReady"/>, <paramref name="isPaused"/>,
    /// <paramref name="day"/>, <paramref name="playerCount"/>) match. Returns
    /// the matching status or <c>null</c> on 408 (no match within the
    /// server-side timeout).
    ///
    /// <para>
    /// The server hard-caps timeouts at <see cref="DefaultWaitServerTimeout"/>;
    /// longer values are clamped. Callers loop on <c>null</c> until their own
    /// outer deadline expires.
    /// </para>
    /// </summary>
    public async Task<ServerStatus?> WaitForStatusAsync(
        long since,
        bool? isReady = null,
        bool? isPaused = null,
        int? day = null,
        int? playerCount = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        var query = new List<string> { $"since={since}" };
        if (isReady is bool b)
        {
            query.Add($"isReady={b.ToString().ToLowerInvariant()}");
        }

        if (isPaused is bool ip)
        {
            query.Add($"isPaused={ip.ToString().ToLowerInvariant()}");
        }

        if (day is int d)
        {
            query.Add($"day={d}");
        }

        if (playerCount is int pc)
        {
            query.Add($"playerCount={pc}");
        }

        var serverTimeoutMs = (long)(
            timeout?.TotalMilliseconds ?? DefaultWaitServerTimeout.TotalMilliseconds
        );
        query.Add($"timeout={serverTimeoutMs}");
        var url = "/wait/status?" + string.Join("&", query);

        var response = await _httpClient.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServerStatus>(ct);
    }

    /// <summary>
    /// Long-poll variant of <see cref="GetPlayers"/>. Server blocks until the
    /// snapshot version exceeds <paramref name="since"/> AND
    /// <paramref name="playerId"/> (when set) is present in the players list.
    /// Returns the matching players response or <c>null</c> on 408.
    /// </summary>
    public async Task<PlayersResponse?> WaitForPlayersAsync(
        long since,
        long? playerId = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        var query = new List<string> { $"since={since}" };
        if (playerId is long pid)
        {
            query.Add($"playerId={pid}");
        }

        var serverTimeoutMs = (long)(
            timeout?.TotalMilliseconds ?? DefaultWaitServerTimeout.TotalMilliseconds
        );
        query.Add($"timeout={serverTimeoutMs}");
        var url = "/wait/players?" + string.Join("&", query);

        var response = await _httpClient.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlayersResponse>(ct);
    }

    /// <summary>
    /// Gets the list of connected players.
    /// Server reads from a periodic snapshot; never blocks on the game thread.
    /// GET /players
    /// </summary>
    public async Task<PlayersResponse?> GetPlayers(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/players", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<PlayersResponse>(ct);
    }

    /// <summary>
    /// Fetches the live game-engine ground-truth state for E2E failure
    /// diagnosis. Bypasses the periodic snapshot — reads directly from
    /// <c>Game1</c> — so a caller on a timed-out poll sees the instantaneous
    /// state rather than a potentially stale cache. Cheap (&lt;1 ms on the
    /// server); safe to call from every failure path.
    /// GET /diagnostics/state
    /// </summary>
    public async Task<DiagnosticsStateResponse?> GetDiagnosticsState(
        CancellationToken ct = default
    ) => await GetDiagnosticsState(null, ct);

    /// <summary>
    /// As <see cref="GetDiagnosticsState(CancellationToken)"/>, but with optional <c>?masterFlag=</c>
    /// (so <c>MasterHasFlag</c> reports whether the master's mailReceived contains that flag) and
    /// <c>?masterFriendKey=</c> (so <c>MasterShadowFriendshipPoints</c> reports that NPC's specific
    /// friendship points). Used by the save-import master-carry tests.
    /// </summary>
    public async Task<DiagnosticsStateResponse?> GetDiagnosticsState(
        string? masterFlag,
        CancellationToken ct = default,
        string? masterFriendKey = null
    )
    {
        var query = new List<string>();
        if (!string.IsNullOrEmpty(masterFlag))
        {
            query.Add($"masterFlag={Uri.EscapeDataString(masterFlag)}");
        }
        if (!string.IsNullOrEmpty(masterFriendKey))
        {
            query.Add($"masterFriendKey={Uri.EscapeDataString(masterFriendKey)}");
        }
        var path =
            query.Count == 0
                ? "/diagnostics/state"
                : "/diagnostics/state?" + string.Join("&", query);
        var response = await _httpClient.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DiagnosticsStateResponse>(ct);
    }

    /// <summary>
    /// Gets the current invite code.
    /// GET /invite-code
    /// </summary>
    public async Task<InviteCodeResponse?> GetInviteCode(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/invite-code", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<InviteCodeResponse>(ct);
    }

    /// <summary>
    /// Health check endpoint.
    /// GET /health
    /// </summary>
    public async Task<HealthResponse?> GetHealth(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/health", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HealthResponse>(ct);
    }

    /// <summary>
    /// Long-poll variant of <see cref="GetHealth"/>. With <paramref name="ready"/>
    /// = <c>true</c> the server blocks until <c>IsFrozen == false</c> (lastTickMs
    /// non-null and ≤ 5s). Returns the matching health response or <c>null</c>
    /// on 408 (no match within the server-side timeout).
    ///
    /// <para>
    /// Stateless predicate (no <c>since</c> cursor): re-issue on null until
    /// the caller's outer deadline expires. Cold-start case: before the first
    /// tick fires the per-tick TCS has nothing to rotate, so the call returns
    /// null after <c>WaitMaxTimeout</c> on the server. Callers MUST
    /// wrap this in an outer re-issue loop, not a single await.
    /// </para>
    /// </summary>
    public async Task<HealthResponse?> WaitForHealthAsync(
        bool ready = true,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        var query = new List<string>();
        if (ready)
        {
            query.Add("ready=true");
        }

        var serverTimeoutMs = (long)(
            timeout?.TotalMilliseconds ?? DefaultWaitServerTimeout.TotalMilliseconds
        );
        query.Add($"timeout={serverTimeoutMs}");
        var url = "/wait/health" + (query.Count > 0 ? "?" + string.Join("&", query) : "");

        var response = await _httpClient.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<HealthResponse>(ct);
    }

    /// <summary>
    /// Gets the OpenAPI specification.
    /// GET /swagger/v1/swagger.json
    /// </summary>
    public async Task<string> GetOpenApiSpec(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/swagger/v1/swagger.json", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Gets the authentication/password protection status.
    /// Server reads from a periodic snapshot; never blocks on the game thread.
    /// GET /auth
    /// </summary>
    public async Task<AuthStatusResponse?> GetAuthStatus(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/auth", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthStatusResponse>(ct);
    }

    /// <summary>
    /// Sets the auth timeout in seconds.
    /// POST /auth/timeout?value=N
    /// </summary>
    public async Task<AuthTimeoutResponse?> SetAuthTimeout(
        int seconds,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Post,
            $"/auth/timeout?value={seconds}",
            ct
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AuthTimeoutResponse>(ct);
    }

    /// <summary>
    /// Gets the current server settings.
    /// GET /settings
    /// </summary>
    public async Task<SettingsResponse?> GetSettings(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/settings", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SettingsResponse>(ct);
    }

    /// <summary>
    /// Gets the current server settings (async suffix variant).
    /// GET /settings
    /// </summary>
    public Task<SettingsResponse?> GetSettingsAsync(CancellationToken ct = default) =>
        GetSettings(ct);

    /// <summary>
    /// Gets the current cabin state and positions.
    /// Server reads from a periodic snapshot; never blocks on the game thread.
    /// GET /cabins
    /// </summary>
    public async Task<CabinsResponse?> GetCabins(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/cabins", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<CabinsResponse>(ct);
    }

    /// <summary>
    /// Gets the current cabin state and positions (async suffix variant).
    /// GET /cabins
    /// </summary>
    public Task<CabinsResponse?> GetCabinsAsync(CancellationToken ct = default) => GetCabins(ct);

    /// <summary>
    /// Gets all farmhand slots.
    /// Server reads from a periodic snapshot; never blocks on the game thread.
    /// GET /farmhands
    /// </summary>
    public async Task<ServerFarmhandsResponse?> GetFarmhands(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/farmhands", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServerFarmhandsResponse>(ct);
    }

    /// <summary>
    /// Long-poll variant of <see cref="GetFarmhands"/>. Server blocks until the
    /// snapshot version exceeds <paramref name="since"/> AND the requested
    /// filters match. Returns the matching farmhands response or <c>null</c>
    /// on 408.
    /// </summary>
    public async Task<ServerFarmhandsResponse?> WaitForFarmhandsAsync(
        long since,
        int? farmhandCount = null,
        string? hasFarmhand = null,
        bool? requireCustomized = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        var query = new List<string> { $"since={since}" };
        if (farmhandCount is int fc)
        {
            query.Add($"farmhandCount={fc}");
        }

        if (!string.IsNullOrEmpty(hasFarmhand))
        {
            query.Add($"hasFarmhand={Uri.EscapeDataString(hasFarmhand)}");
        }

        if (requireCustomized is bool rc)
        {
            query.Add($"requireCustomized={rc.ToString().ToLowerInvariant()}");
        }

        var serverTimeoutMs = (long)(
            timeout?.TotalMilliseconds ?? DefaultWaitServerTimeout.TotalMilliseconds
        );
        query.Add($"timeout={serverTimeoutMs}");
        var url = "/wait/farmhands?" + string.Join("&", query);

        var response = await _httpClient.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestTimeout)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServerFarmhandsResponse>(ct);
    }

    /// <summary>
    /// Deletes a farmhand by name. Use this only when the caller genuinely only has a
    /// name (e.g. error-path coverage); prefer <see cref="DeleteFarmhandById"/> for
    /// fresh-joiner paths, since name sync can lag server state.
    /// DELETE /farmhands?name=X
    /// </summary>
    public async Task<FarmhandOperationResponse?> DeleteFarmhandByName(
        string name,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Delete,
            $"/farmhands?name={Uri.EscapeDataString(name)}",
            ct
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FarmhandOperationResponse>(ct);
    }

    /// <summary>
    /// Deletes a farmhand by UniqueMultiplayerID. Preferred for fresh joiners and
    /// cleanup paths that already hold a <c>Farmer</c> reference.
    /// DELETE /farmhands?playerId=X
    /// </summary>
    public async Task<FarmhandOperationResponse?> DeleteFarmhandById(
        long playerId,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Delete,
            $"/farmhands?playerId={playerId}",
            ct
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FarmhandOperationResponse>(ct);
    }

    /// <summary>
    /// Gets the current rendering status.
    /// GET /rendering
    /// </summary>
    public async Task<RenderingStatusResponse?> GetRendering(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/rendering", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RenderingStatusResponse>(ct);
    }

    /// <summary>
    /// Captures a screenshot from the game's backbuffer.
    /// GET /screenshot
    /// </summary>
    public async Task<ScreenshotResponse?> GetScreenshot(CancellationToken ct = default)
    {
        var response = await SendWithRetryAsync(HttpMethod.Get, "/screenshot", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ScreenshotResponse>(ct);
    }

    /// <summary>
    /// Sets the server render rate. 0 disables rendering; N &gt; 0 caps draws at N fps.
    /// POST /rendering?fps=N
    /// </summary>
    public async Task<RenderingSetResponse?> SetServerFps(int fps, CancellationToken ct = default)
    {
        var response = await SendWithRetryAsync(HttpMethod.Post, $"/rendering?fps={fps}", ct);
        // Safe to add: server returns 200 with Success=false for validation errors (e.g., missing param).
        // Only 503 (game thread blocked) triggers EnsureSuccessStatusCode to throw.
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RenderingSetResponse>(ct);
    }

    /// <summary>
    /// Sets the game time of day.
    /// POST /time?value=XXXX
    /// </summary>
    public async Task<TimeSetResponse?> SetTime(int value, CancellationToken ct = default)
    {
        var response = await SendWithRetryAsync(HttpMethod.Post, $"/time?value={value}", ct);
        // Safe to add: server returns 200 with Success=false for invalid time values.
        // Only 503 (game thread blocked) triggers EnsureSuccessStatusCode to throw.
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TimeSetResponse>(ct);
    }

    /// <summary>
    /// Test-only: enumerate every HoeDirt-with-crop in the world (terrain features and Garden Pots).
    /// Used by E2E tests to verify CropSaver behavior across locations.
    /// GET /test/crops
    /// </summary>
    public async Task<TestCropsResponse?> GetAllCrops(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/test/crops", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestCropsResponse>(ct);
    }

    /// <summary>
    /// Test-only: read the host's current festival state directly (whether a festival
    /// is active, today's festival map, and the festivalStart/festivalEnd ready counts).
    /// Used by FestivalTests to assert festival-active / festival-ended without proxying
    /// through the client location (which reads "Temp" during a festival).
    /// GET /test/festival_state
    /// </summary>
    public async Task<TestFestivalStateResponse?> GetFestivalState(CancellationToken ct = default)
    {
        var response = await _httpClient.GetAsync("/test/festival_state", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestFestivalStateResponse>(ct);
    }

    /// <summary>
    /// Test-only: jump the calendar directly to the given season/day/year.
    /// POST /test/set_date
    /// </summary>
    public async Task<TestSetDateResponse?> SetDate(
        string season,
        int day,
        int year,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Post,
            "/test/set_date",
            ct,
            () =>
                JsonContent.Create(
                    new
                    {
                        season,
                        day,
                        year,
                    }
                )
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestSetDateResponse>(ct);
    }

    /// <summary>
    /// Test-only: queue an overnight FarmEvent (e.g. the Mr. Qi mystery box) for the next night.
    /// POST /test/farmevent?type=qiplane
    /// </summary>
    public async Task<TestFarmEventResponse?> QueueFarmEvent(
        string type,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Post,
            $"/test/farmevent?type={type}",
            ct
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestFarmEventResponse>(ct);
    }

    /// <summary>
    /// Test-only: run a vanilla debug house-upgrade command on the host (via parseDebugInput, so the
    /// HostFarmhouseUpgradeGuard Harmony prefix is exercised) and return the host's resulting
    /// HouseUpgradeLevel. Used to pin that the host farmhouse cannot be upgraded (stays 0).
    /// POST /test/house_upgrade?command=...
    /// </summary>
    public async Task<TestHouseUpgradeResponse?> RunDebugHouseUpgrade(
        string command,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Post,
            $"/test/house_upgrade?command={Uri.EscapeDataString(command)}",
            ct
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestHouseUpgradeResponse>(ct);
    }

    /// <summary>
    /// Test-only: deterministically construct an abandoned slot claim by stamping a synthetic userID
    /// onto an uncustomized, homed farmhand entry (the on-disk shape vanilla's load-time reset leaves
    /// intact). Used to verify the save-load sweep clears it on reload — the live disconnect heal can't
    /// persist such a claim to disk for a sweep test.
    /// POST /test/stamp_claim
    /// </summary>
    public async Task<TestStampClaimResponse?> StampClaim(CancellationToken ct = default)
    {
        var response = await SendWithRetryAsync(HttpMethod.Post, "/test/stamp_claim", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestStampClaimResponse>(ct);
    }

    /// <summary>
    /// Test-only: trigger a Galaxy re-sign-in with no outage, to verify re-login while healthy
    /// doesn't disrupt the live lobby / invite code.
    /// POST /test/galaxy_relogin
    /// </summary>
    public async Task<TestGalaxyReloginResponse?> TriggerGalaxyRelogin(
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(HttpMethod.Post, "/test/galaxy_relogin", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestGalaxyReloginResponse>(ct);
    }

    /// <summary>
    /// Test-only: seed the active game's master + FarmHouse to look like a real importable owner
    /// (non-Server name + inventory, plus optional house/world/relationship state and FarmHouse
    /// contents) before saving, so the save's &lt;player&gt; reads as a played human for a swap import.
    /// POST /test/seed_import_source
    /// </summary>
    public async Task<TestSeedImportSourceResponse?> SeedImportSource(
        TestSeedImportSourceRequest body,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Post,
            "/test/seed_import_source",
            ct,
            () => JsonContent.Create(body)
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestSeedImportSourceResponse>(ct);
    }

    /// <summary>
    /// Test-only: clone a source save folder under a new name and run saves-import on the clone
    /// (ExecuteImport rejects importing the active save). Defaults: source = active save, target =
    /// source + "-import". Pass <c>SwapHostTo</c> for a swap+bind import.
    /// POST /test/import_save
    /// </summary>
    public async Task<TestImportSaveResponse?> ImportSave(
        TestImportSaveRequest body,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Post,
            "/test/import_save",
            ct,
            () => JsonContent.Create(body)
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestImportSaveResponse>(ct);
    }

    /// <summary>
    /// Test-only: clone the active (valid) save to <paramref name="targetSaveName"/>, then corrupt
    /// the clone's main file into non-well-formed XML (resilience test). The active save is untouched.
    /// POST /test/corrupt_save
    /// </summary>
    public async Task<TestSaveFileOpResponse?> CorruptSave(
        string targetSaveName,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Post,
            "/test/corrupt_save",
            ct,
            () => JsonContent.Create(new TestCorruptSaveRequest { TargetSaveName = targetSaveName })
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestSaveFileOpResponse>(ct);
    }

    /// <summary>
    /// Test-only: whether a leftover <c>.tmp</c> remains next to the save's main file (no-junk
    /// assertion after a successful import).
    /// GET /test/save_tmp_exists?saveName=...
    /// </summary>
    public async Task<TestSaveFileOpResponse?> SaveTmpExists(
        string saveName,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Get,
            $"/test/save_tmp_exists?saveName={Uri.EscapeDataString(saveName)}",
            ct
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestSaveFileOpResponse>(ct);
    }

    /// <summary>
    /// Test-only: mutate an existing CropSaver tracking entry. Used by E2E
    /// tests to pre-arm <c>extraDays</c> past <c>OnDayEnd</c>'s
    /// branch-1 floor without simulating many real day-transitions. All
    /// optional parameters are skipped (left at their existing values) when
    /// null. Returns <c>Found=false</c> if no SaverCrop exists at the tile.
    /// POST /test/saver_crop
    /// </summary>
    public async Task<TestSaverCropResponse?> SetSaverCrop(
        string locationName,
        int tileX,
        int tileY,
        int? extraDays = null,
        long? ownerId = null,
        (string Season, int Day, int Year)? datePlanted = null,
        CancellationToken ct = default
    )
    {
        // Cast to object? so the null and the populated-object branches share a
        // compile-time type; System.Text.Json serializes a null property fine,
        // and the endpoint skips DatePlanted when it's null.
        object? datePlantedBody = datePlanted is { } dp
            ? new
            {
                season = dp.Season,
                day = dp.Day,
                year = dp.Year,
            }
            : null;

        var response = await SendWithRetryAsync(
            HttpMethod.Post,
            "/test/saver_crop",
            ct,
            () =>
                JsonContent.Create(
                    new
                    {
                        locationName,
                        tileX,
                        tileY,
                        extraDays,
                        ownerId,
                        datePlanted = datePlantedBody,
                    }
                )
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<TestSaverCropResponse>(ct);
    }

    /// <summary>
    /// Adjusts the game clock speed by the given multiplier.
    /// POST /clock-speed?multiplier=N
    /// </summary>
    public async Task<ClockSpeedResponse?> SetClockSpeed(
        double multiplier,
        CancellationToken ct = default
    )
    {
        var multiplierStr = multiplier.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var response = await SendWithRetryAsync(
            HttpMethod.Post,
            $"/clock-speed?multiplier={multiplierStr}",
            ct
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClockSpeedResponse>(ct);
    }

    /// <summary>
    /// Grants admin role to a player by name. Use this only when the caller genuinely
    /// only has a name (e.g. error-path coverage); prefer <see cref="GrantAdminById"/>
    /// for fresh-joiner paths, since name sync can lag server state by 1-18 s.
    /// POST /roles/admin?name=PlayerName
    /// </summary>
    public async Task<RoleGrantResponse?> GrantAdminByName(
        string playerName,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Post,
            $"/roles/admin?name={Uri.EscapeDataString(playerName)}",
            ct
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RoleGrantResponse>(ct);
    }

    /// <summary>
    /// Grants admin role to a player by UniqueMultiplayerID. Preferred for fresh
    /// joiners: UID is assigned at peer-add, while Name lags behind character XML sync.
    /// POST /roles/admin?playerId=X
    /// </summary>
    public async Task<RoleGrantResponse?> GrantAdminById(
        long playerId,
        CancellationToken ct = default
    )
    {
        var response = await SendWithRetryAsync(
            HttpMethod.Post,
            $"/roles/admin?playerId={playerId}",
            ct
        );
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<RoleGrantResponse>(ct);
    }

    /// <summary>
    /// Posts to the rendering endpoint without the fps parameter (for error testing).
    /// POST /rendering
    /// </summary>
    public async Task<RenderingSetResponse?> PostRenderingRaw(CancellationToken ct = default)
    {
        var response = await _httpClient.PostAsync("/rendering", null, ct);
        return await response.Content.ReadFromJsonAsync<RenderingSetResponse>(ct);
    }

    /// <summary>
    /// Waits for the server to come online and be ready.
    /// </summary>
    /// <param name="timeout">Maximum time to wait</param>
    /// <param name="pollInterval">Time between status checks</param>
    /// <param name="cancellationToken">Cancellation token for early abort (e.g., on server error)</param>
    /// <param name="onProgress">Optional callback for progress reporting (attempt count, detail message)</param>
    /// <param name="requireInviteCode">If true, also waits for a non-empty invite code (Steam/Galaxy). LAN-only servers should pass false.</param>
    /// <returns>The server status once online, or null if timeout/cancelled</returns>
    public Task<ServerStatus?> WaitForServerOnline(
        TimeSpan timeout,
        TimeSpan? pollInterval = null,
        CancellationToken cancellationToken = default,
        Action<string>? onProgress = null,
        bool requireInviteCode = true
    )
    {
        return Helpers.WaitTrace.RunAsync<ServerStatus?>(
            Helpers.WaitName.ServerApi_WaitForServerOnline,
            () =>
                WaitForServerOnlineCoreAsync(
                    timeout,
                    pollInterval,
                    cancellationToken,
                    onProgress,
                    requireInviteCode
                ),
            cancellationToken,
            snapshot: () => new { baseUrl = _baseUrl }
        );
    }

    private async Task<ServerStatus?> WaitForServerOnlineCoreAsync(
        TimeSpan timeout,
        TimeSpan? pollInterval,
        CancellationToken cancellationToken,
        Action<string>? onProgress,
        bool requireInviteCode
    )
    {
        // Long-poll path: each iteration calls /wait/status?isReady=true with a
        // server-side hard cap of 10s. The server blocks until a newer snapshot
        // satisfies the filter, so a healthy server returns immediately on the
        // first poll. The legacy 1s pollInterval is unused on this path —
        // server-side blocking replaces client-side throttling. We still keep
        // an outer loop so the requireInviteCode path can advance `since` and
        // re-poll when the snapshot has refreshed but invite codes are still
        // missing (invite code is read from a file, not from the snapshot).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var deadline = DateTime.UtcNow + timeout;
        long since = 0;
        var attempt = 0;
        string lastReason = "no attempts made";
        ServerStatus? matchedStatus = null;
        Exception? lastException = null;
        const string label = nameof(Helpers.WaitName.ServerApi_WaitForServerOnline);

        // Bracket the snapshot-age slot to this helper's lifetime so the
        // wait_matched emit attributes only to HTTP calls made by this loop.
        using var _diagScope = HttpResponseDiagnostics.BeginScope();

        try
        {
            while (DateTime.UtcNow < deadline)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    onProgress?.Invoke($"Cancelled after {attempt} attempts (last: {lastReason})");
                    return null;
                }

                attempt++;
                try
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    var status = await WaitForStatusAsync(
                        since: since,
                        isReady: true,
                        timeout: remaining,
                        ct: cancellationToken
                    );

                    if (status == null)
                    {
                        // 408 — server-side timeout. Try again under our deadline.
                        lastReason = "server-side timeout (408)";
                    }
                    else
                    {
                        var isReady = status.IsOnline && status.IsReady;
                        if (requireInviteCode)
                        {
                            isReady = isReady && !string.IsNullOrEmpty(status.InviteCode);
                        }

                        if (isReady)
                        {
                            onProgress?.Invoke(
                                $"Server online and ready after {attempt} attempt(s)"
                            );
                            matchedStatus = status;
                            Helpers.PollingHelper.EmitWaitMatched(label);
                            return status;
                        }

                        // Snapshot advanced but conditions still unmet (e.g., invite
                        // code missing). Update `since` so the next /wait/status
                        // doesn't return immediately on the same snapshot.
                        since = status.Version;
                        lastReason = requireInviteCode
                            ? $"IsOnline={status.IsOnline}, IsReady={status.IsReady}, InviteCode='{status.InviteCode ?? "(null)"}'"
                            : $"IsOnline={status.IsOnline}, IsReady={status.IsReady}";
                    }
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    lastReason = $"{ex.GetType().Name}: {ex.Message}";
                }

                // Report progress every 5 attempts.
                if (attempt % 5 == 0)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    onProgress?.Invoke(
                        $"Attempt {attempt}, {remaining.TotalSeconds:0}s remaining: {lastReason}"
                    );
                }
            }

            onProgress?.Invoke($"Timed out after {attempt} attempts (last: {lastReason})");
            return null;
        }
        finally
        {
            Helpers.InfrastructureEventLog.Emit(
                "long_poll_completed",
                new
                {
                    label,
                    succeeded = matchedStatus != null,
                    iterations = attempt,
                    durationMs = sw.ElapsedMilliseconds,
                    timeoutMs = (long)timeout.TotalMilliseconds,
                    snapshotVersionAtMatch = (long?)matchedStatus?.Version,
                    error = lastException?.Message,
                    ctCancelled = cancellationToken.IsCancellationRequested,
                }
            );
        }
    }

    /// <summary>
    /// Creates a new game on the running server with the specified settings.
    /// POST /newgame
    /// The server will return to title, reset all game state, and create a fresh game.
    /// Only explicitly provided fields are sent; omitted fields use server-settings.json defaults.
    /// </summary>
    public async Task<NewGameResponse?> CreateNewGameAsync(
        FarmTypeSetting? farmType = null,
        string? farmName = null,
        int? startingCabins = null,
        string? cabinStrategy = null,
        int? maxPlayers = null,
        bool? allowIpConnections = null,
        CancellationToken ct = default
    )
    {
        var body = new Dictionary<string, object>();
        if (farmType.HasValue)
        {
            body["farmType"] = farmType.Value.ToJsonValue();
        }

        if (farmName != null)
        {
            body["farmName"] = farmName;
        }

        if (startingCabins.HasValue)
        {
            body["startingCabins"] = startingCabins.Value;
        }

        if (cabinStrategy != null)
        {
            body["cabinStrategy"] = cabinStrategy;
        }

        if (maxPlayers.HasValue)
        {
            body["maxPlayers"] = maxPlayers.Value;
        }

        if (allowIpConnections.HasValue)
        {
            body["allowIpConnections"] = allowIpConnections.Value;
        }

        var content = JsonContent.Create(body);
        var response = await _httpClient.PostAsync("/newgame", content, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<NewGameResponse>(ct);
    }

    /// <summary>
    /// Re-reads server-settings.json and reloads the active world in-process (no
    /// container restart). Applies any runtime settings change (e.g. CabinStrategy)
    /// and fires the mod's SaveLoaded path. The response shape matches /newgame.
    /// POST /reload
    /// </summary>
    public async Task<NewGameResponse?> ReloadAsync(CancellationToken ct = default)
    {
        // Reload runs during title/save transitions, where the game thread is most
        // likely busy and the server returns a transient 503. Go through the retry
        // helper so those are retried transparently like every other mutating call.
        var response = await SendWithRetryAsync(HttpMethod.Post, "/reload", ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<NewGameResponse>(ct);
    }

    /// <summary>
    /// Polls GET /players until a player with the given name appears.
    /// Prefer <see cref="WaitForPlayerByIdAsync"/> for fresh joiners: Name can lag
    /// 1-18 s behind peer-add while the character XML round-trips.
    /// </summary>
    public async Task<bool> WaitForPlayerByNameAsync(
        string name,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        return await Helpers.PollingHelper.WaitUntilAsync(
            Helpers.WaitName.Polling_ServerApi_WaitForPlayerByName,
            async () =>
            {
                try
                {
                    using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reqCts.CancelAfter(Helpers.TestTimings.PollingRequestTimeout);
                    var players = await GetPlayers(reqCts.Token);
                    return players?.Players?.Any(p =>
                            p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                        ) == true;
                }
                catch (Exception ex)
                    when (ex
                            is HttpRequestException
                                or TaskCanceledException
                                or OperationCanceledException
                        && !ct.IsCancellationRequested
                    )
                {
                    // Request timeout or connection error during server boot, retry
                    return false;
                }
            },
            timeout ?? Helpers.TestTimings.NetworkSyncTimeout,
            cancellationToken: ct,
            onTimeoutAsync: async () =>
                await Helpers.FailureContext.DumpAsync(
                    this,
                    reason: "WaitForPlayerByNameAsync_timeout",
                    extras: new Dictionary<string, object?> { ["name"] = name }
                )
        );
    }

    /// <summary>
    /// Polls GET /players until a player with the given UniqueMultiplayerID appears.
    /// Preferred for fresh joiners: UID is assigned at peer-add, while Name lags the
    /// character XML round-trip (1-18 s under load).
    /// </summary>
    public async Task<bool> WaitForPlayerByIdAsync(
        long playerId,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        // Long-poll path: each iteration calls /wait/players?playerId=N. The
        // server blocks until a newer snapshot contains the player or the
        // server-side hard cap (10s) expires. The outer LongPollAsync loop is
        // bounded by the caller's timeout and emits long_poll_completed on
        // exit. onTimeoutAsync still runs the failure-context dump on miss.
        return await Helpers.PollingHelper.LongPollAsync(
            Helpers.WaitName.Polling_ServerApi_WaitForPlayerById,
            async (since, remaining) =>
            {
                try
                {
                    using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reqCts.CancelAfter(Helpers.TestTimings.PollingRequestTimeout);
                    var players = await WaitForPlayersAsync(
                        since: since,
                        playerId: playerId,
                        timeout: remaining,
                        ct: reqCts.Token
                    );
                    if (players == null)
                    {
                        // 408 — server-side timeout, no newer snapshot observed.
                        return new Helpers.PollingHelper.LongPollResult(false, since);
                    }
                    return new Helpers.PollingHelper.LongPollResult(true, players.Version);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    // Connection error or per-request timeout during server boot.
                    // Cursor is unchanged; the next iteration retries from `since`.
                    return new Helpers.PollingHelper.LongPollResult(false, since);
                }
            },
            timeout ?? Helpers.TestTimings.NetworkSyncTimeout,
            cancellationToken: ct,
            onTimeoutAsync: async () =>
            {
                // On timeout, pull live ground-truth. The poll is already failed;
                // this is just enrichment. Don't propagate the original ct so
                // cancellation by the outer timeout still lets us dump state.
                return await Helpers.FailureContext.DumpAsync(
                    this,
                    reason: "WaitForPlayerByIdAsync_timeout",
                    extras: new Dictionary<string, object?> { ["playerId"] = playerId }
                );
            }
        );
    }

    /// <summary>
    /// Polls GET /players until none of the named players remain.
    /// </summary>
    public async Task<bool> WaitForPlayersRemovedByNameAsync(
        IEnumerable<string> names,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        var nameSet = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return await Helpers.PollingHelper.WaitUntilAsync(
            Helpers.WaitName.Polling_ServerApi_WaitForPlayersRemovedByName,
            async () =>
            {
                try
                {
                    using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reqCts.CancelAfter(Helpers.TestTimings.PollingRequestTimeout);
                    var players = await GetPlayers(reqCts.Token);
                    if (players?.Players == null)
                    {
                        return false;
                    }

                    return !players.Players.Any(p => nameSet.Contains(p.Name));
                }
                catch (Exception ex)
                    when (ex
                            is HttpRequestException
                                or TaskCanceledException
                                or OperationCanceledException
                        && !ct.IsCancellationRequested
                    )
                {
                    // Request timeout or connection error during server boot, retry
                    return false;
                }
            },
            timeout ?? Helpers.TestTimings.FarmerDeleteTimeout,
            cancellationToken: ct,
            onTimeoutAsync: async () =>
                await Helpers.FailureContext.DumpAsync(
                    this,
                    reason: "WaitForPlayersRemovedByNameAsync_timeout",
                    extras: new Dictionary<string, object?> { ["names"] = nameSet.ToArray() }
                )
        );
    }

    /// <summary>
    /// Polls GET /players until the named player is no longer present.
    /// </summary>
    public Task<bool> WaitForPlayerRemovedByNameAsync(
        string name,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    ) => WaitForPlayersRemovedByNameAsync(new[] { name }, timeout, ct);

    /// <summary>
    /// Polls GET /players until none of the players with the given UniqueMultiplayerIDs remain.
    /// </summary>
    public async Task<bool> WaitForPlayersRemovedByIdAsync(
        IEnumerable<long> playerIds,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        var idSet = playerIds.ToHashSet();
        return await Helpers.PollingHelper.WaitUntilAsync(
            Helpers.WaitName.Polling_ServerApi_WaitForPlayersRemovedById,
            async () =>
            {
                try
                {
                    using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reqCts.CancelAfter(Helpers.TestTimings.PollingRequestTimeout);
                    var players = await GetPlayers(reqCts.Token);
                    if (players?.Players == null)
                    {
                        return false;
                    }

                    return !players.Players.Any(p => idSet.Contains(p.Id));
                }
                catch (Exception ex)
                    when (ex
                            is HttpRequestException
                                or TaskCanceledException
                                or OperationCanceledException
                        && !ct.IsCancellationRequested
                    )
                {
                    return false;
                }
            },
            timeout ?? Helpers.TestTimings.FarmerDeleteTimeout,
            cancellationToken: ct,
            onTimeoutAsync: async () =>
                await Helpers.FailureContext.DumpAsync(
                    this,
                    reason: "WaitForPlayersRemovedByIdAsync_timeout",
                    extras: new Dictionary<string, object?> { ["playerIds"] = idSet.ToArray() }
                )
        );
    }

    /// <summary>
    /// Polls GET /players until the player with the given UniqueMultiplayerID is no longer present.
    /// </summary>
    public Task<bool> WaitForPlayerRemovedByIdAsync(
        long playerId,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    ) => WaitForPlayersRemovedByIdAsync(new[] { playerId }, timeout, ct);

    /// <summary>
    /// Polls GET /farmhands until a farmhand with the given name exists.
    /// Intended for tests that verify name/customization behavior specifically;
    /// name sync is the whole point of such assertions.
    /// </summary>
    public async Task<bool> WaitForFarmhandByNameAsync(
        string name,
        bool requireCustomized = false,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        return await Helpers.PollingHelper.LongPollAsync(
            Helpers.WaitName.Polling_ServerApi_WaitForFarmhandByName,
            async (since, remaining) =>
            {
                try
                {
                    using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reqCts.CancelAfter(Helpers.TestTimings.PollingRequestTimeout);
                    var farmhands = await WaitForFarmhandsAsync(
                        since: since,
                        hasFarmhand: name,
                        requireCustomized: requireCustomized ? true : null,
                        timeout: remaining,
                        ct: reqCts.Token
                    );
                    if (farmhands == null)
                    {
                        return new Helpers.PollingHelper.LongPollResult(false, since);
                    }

                    return new Helpers.PollingHelper.LongPollResult(true, farmhands.Version);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                    return new Helpers.PollingHelper.LongPollResult(false, since);
                }
            },
            timeout ?? Helpers.TestTimings.FarmerDeleteTimeout,
            cancellationToken: ct,
            onTimeoutAsync: async () =>
                await Helpers.FailureContext.DumpAsync(
                    this,
                    reason: "WaitForFarmhandByNameAsync_timeout",
                    extras: new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["requireCustomized"] = requireCustomized,
                    }
                )
        );
    }

    /// <summary>
    /// Retries DELETE /farmhands?name=X until Success == true.
    /// </summary>
    public async Task<FarmhandOperationResponse?> WaitForFarmhandDeletedByNameAsync(
        string name,
        TimeSpan? timeout = null,
        CancellationToken ct = default
    )
    {
        FarmhandOperationResponse? result = null;
        await Helpers.PollingHelper.WaitUntilAsync(
            Helpers.WaitName.Polling_ServerApi_WaitForFarmhandDeletedByName,
            async () =>
            {
                try
                {
                    using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    reqCts.CancelAfter(Helpers.TestTimings.PollingRequestTimeout);
                    result = await DeleteFarmhandByName(name, reqCts.Token);
                    return result?.Success == true;
                }
                catch (Exception ex)
                    when (ex
                            is HttpRequestException
                                or TaskCanceledException
                                or OperationCanceledException
                        && !ct.IsCancellationRequested
                    )
                {
                    // 503 (game thread blocked) or request timeout, retry
                    return false;
                }
            },
            timeout ?? Helpers.TestTimings.FarmerDeleteTimeout,
            cancellationToken: ct,
            onTimeoutAsync: async () =>
                await Helpers.FailureContext.DumpAsync(
                    this,
                    reason: "WaitForFarmhandDeletedByNameAsync_timeout",
                    extras: new Dictionary<string, object?>
                    {
                        ["name"] = name,
                        ["lastResultSuccess"] = result?.Success,
                    }
                )
        );
        return result;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Response from the /newgame endpoint.
/// </summary>
public class NewGameResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
