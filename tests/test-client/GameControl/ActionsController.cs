using System.Reflection;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace JunimoTestClient.GameControl;

/// <summary>
/// Controller for in-game actions like sleeping, warping, placing pots, and planting crops.
/// </summary>
public class ActionsController
{
    private readonly IMonitor _monitor;

    private static readonly MethodInfo? StartSleepMethod =
        typeof(GameLocation).GetMethod("startSleep", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? typeof(FarmHouse).GetMethod(
            "startSleep",
            BindingFlags.NonPublic | BindingFlags.Instance
        );

    public ActionsController(IMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Make the current player go to sleep by triggering the sleep sequence
    /// at their home location (Cabin for farmhands, Farmhouse for host).
    /// </summary>
    public SleepResult GoToSleep()
    {
        try
        {
            if (!Context.IsWorldReady)
            {
                return new SleepResult { Success = false, Error = "Not in a game world" };
            }

            // Get the player's home location (FarmHouse for host, Cabin for farmhands)
            var home = Utility.getHomeOfFarmer(Game1.player);
            if (home == null)
            {
                return new SleepResult
                {
                    Success = false,
                    Error = "Could not find player's home location",
                };
            }

            if (StartSleepMethod == null)
            {
                return new SleepResult
                {
                    Success = false,
                    Error = "Could not find startSleep method via reflection",
                };
            }

            // Set sleep location data (mirrors what the server host bot does)
            var bedSpot = home.GetPlayerBedSpot();
            Game1.player.lastSleepLocation.Value = home.NameOrUniqueName;
            Game1.player.lastSleepPoint.Value = bedSpot;

            // Mark the player as sleeping in a temporary bed. Farmer.Update() continuously
            // recalculates isInBed every tick based on tile properties:
            //   isInBed = doesTileHaveProperty(TilePoint, "Bed", "Back") || sleptInTemporaryBed
            // Setting isInBed directly gets overwritten on the next tick if the player isn't
            // physically on a bed tile. Using sleptInTemporaryBed makes isInBed persist
            // regardless of position. This is critical for the day transition: when the server
            // sends newDaySync, Game1.NewDay() only starts the screen fade if isInBed is true.
            // Without the fade, the day transition coroutine never starts, barriers are never
            // reached, and the DesyncKicker kicks the player after 20 seconds.
            Game1.player.sleptInTemporaryBed.Value = true;

            // Trigger sleep via reflection (startSleep is private)
            StartSleepMethod.Invoke(home, null);

            _monitor.Log(
                $"Triggered sleep at {home.NameOrUniqueName} (bed @ {bedSpot})",
                LogLevel.Info
            );

            return new SleepResult
            {
                Success = true,
                Message = $"Going to sleep at {home.NameOrUniqueName}",
                Location = home.NameOrUniqueName,
            };
        }
        catch (Exception ex)
        {
            _monitor.Log($"Failed to go to sleep: {ex.Message}", LogLevel.Error);
            return new SleepResult { Success = false, Error = ex.Message };
        }
    }

    /// <summary>
    /// Queue a warp to the given location/tile. Stardew's warp is asynchronous — the actual
    /// switch lands several ticks later via LocationRequest. Callers must poll /status (or
    /// /wait/location) to confirm the player has arrived before issuing follow-up actions
    /// that depend on currentLocation.
    /// </summary>
    public WarpResult Warp(string locationName, int tileX, int tileY)
    {
        if (!Context.IsWorldReady)
        {
            return new WarpResult { Success = false, Error = "Not in a game world" };
        }

        Game1.warpFarmer(locationName, tileX, tileY, Game1.player.FacingDirection);
        return new WarpResult
        {
            Success = true,
            Message = $"Warp queued to {locationName} ({tileX},{tileY})",
            LocationName = locationName,
        };
    }

    /// <summary>
    /// Vote to leave the current festival, exactly as a real player walking out does.
    /// <c>Event.TryStartEndFestivalDialogue</c> sets this client's <c>festivalEnd</c> ready
    /// flag and opens an auto-confirming <c>ReadyCheckDialog</c>; once the host is also ready
    /// (its <c>HandleFestivalLeave</c> sees <c>CheckOthersReady("festivalEnd")</c>), the
    /// festival ends for everyone. No-op (returns Success=false) if not at a festival.
    /// </summary>
    public LeaveFestivalResult LeaveFestival()
    {
        if (!Context.IsWorldReady)
        {
            return new LeaveFestivalResult { Success = false, Error = "Not in a game world" };
        }

        var currentEvent = Game1.CurrentEvent;
        if (currentEvent is not { isFestival: true })
        {
            return new LeaveFestivalResult
            {
                Success = false,
                Error = "Not currently at a festival",
            };
        }

        var started = currentEvent.TryStartEndFestivalDialogue(Game1.player);
        return new LeaveFestivalResult
        {
            Success = started,
            Error = started
                ? null
                : "TryStartEndFestivalDialogue declined (not local player or no longer a festival)",
        };
    }

    /// <summary>
    /// Engage THIS client's player to an NPC so the next day-transition queues their wedding. The
    /// engagement (spouse + an Engaged friendship with a WeddingDate) must be authored on the client,
    /// not the host: a farmhand's <see cref="Farmer"/> root is client-authoritative, so the client
    /// resends its full root each night (<c>Multiplayer.sendFarmhand</c> → <c>MarkReassigned</c>) and
    /// would overwrite any host-side spouse write before the wedding fires. Setting it here makes the
    /// engagement durable across the nightly sync, exactly as a real player accepting a proposal does.
    /// <paramref name="daysUntilWedding"/> sets the WeddingDate (default 1 = the next morning).
    /// </summary>
    public EngageToNpcResult EngageToNpc(string npc, int daysUntilWedding = 1)
    {
        if (!Context.IsWorldReady)
        {
            return new EngageToNpcResult { Success = false, Error = "Not in a game world" };
        }

        if (string.IsNullOrEmpty(npc))
        {
            return new EngageToNpcResult { Success = false, Error = "Missing npc name" };
        }

        var me = Game1.player;
        var weddingDate = WorldDate.ForDaysPlayed(
            Game1.Date.TotalDays + (daysUntilWedding < 0 ? 0 : daysUntilWedding)
        );

        // A real NPC engagement is gated on houseUpgradeLevel >= 1 (NPC.cs RejectMermaidPendant_
        // NeedHouseUpgrade) because there is no level-0 marriage map: FarmHouse.updateMap derives
        // "Maps/FarmHouse_marriage" at level 0, which exists in no SDV install (only
        // FarmHouse1_marriage/FarmHouse2_marriage do). The cabin's upgradeLevel is owner.HouseUpgrade
        // Level (FarmHouse.upgradeLevel getter), so bumping this farmhand to level 1 makes the host's
        // updateFarmLayout resolve FarmHouse1_marriage when the marriage applies — no missing-map crash.
        if (me.HouseUpgradeLevel < 1)
        {
            me.HouseUpgradeLevel = 1;
        }

        me.spouse = npc;
        if (!me.friendshipData.TryGetValue(npc, out var friendship) || friendship == null)
        {
            friendship = new Friendship(2500);
            me.friendshipData[npc] = friendship;
        }
        friendship.Status = FriendshipStatus.Engaged;
        friendship.Proposer = me.UniqueMultiplayerID;
        friendship.WeddingDate = weddingDate;

        _monitor.Log(
            $"[Wedding] Engaged client to {npc}, wedding in {daysUntilWedding} day(s).",
            LogLevel.Info
        );

        return new EngageToNpcResult
        {
            Success = true,
            Spouse = me.spouse,
            IsEngaged = me.isEngaged(),
        };
    }

    /// <summary>
    /// Place a Garden Pot at the given tile on the player's current location. The
    /// IndoorPot ctor reads <c>Game1.currentLocation</c> when initializing its inner
    /// HoeDirt, so the player must already be at <paramref name="locationName"/>.
    /// Mirrors the in-game placement path at <c>Object.cs:6841</c>: construct directly,
    /// add to <c>location.objects</c>.
    /// </summary>
    public PlacePotResult PlacePot(string locationName, int tileX, int tileY, bool clearObstacles)
    {
        if (!Context.IsWorldReady)
        {
            return new PlacePotResult { Success = false, Error = "Not in a game world" };
        }

        if (Game1.player.currentLocation?.Name != locationName)
        {
            return new PlacePotResult
            {
                Success = false,
                Error =
                    $"Player is on '{Game1.player.currentLocation?.Name}', not '{locationName}'",
            };
        }

        var location = Game1.player.currentLocation;
        var tile = new Vector2(tileX, tileY);

        if (clearObstacles)
        {
            // Spawn-time rocks/weeds/twigs land in location.Objects; grass and
            // pre-tilled HoeDirts land in terrainFeatures. Remove both so the
            // pot can be placed cleanly. Use the NetDictionary public API per
            // netdictionary-public-surface.md — direct FieldDict mutation
            // would skip replication.
            location.Objects.Remove(tile);
            location.terrainFeatures.Remove(tile);
        }

        if (location.Objects.ContainsKey(tile))
        {
            return new PlacePotResult
            {
                Success = false,
                Error = $"Tile ({tileX},{tileY}) is occupied",
            };
        }

        location.Objects.Add(tile, new IndoorPot(tile));
        return new PlacePotResult
        {
            Success = true,
            LocationName = locationName,
            TileX = tileX,
            TileY = tileY,
        };
    }

    /// <summary>
    /// Clear objects, terrain features, bushes, and resource clumps from a tile area
    /// on the player's current location, via the same <c>removeObjectsAndSpawned</c>
    /// the game uses to clear under a placed building. Lets placement tests prepare a
    /// debris-free footprint so cabin/building validation isn't tripped by spawn-time
    /// rocks/weeds/grass.
    /// </summary>
    public ClearAreaResult ClearArea(
        string locationName,
        int tileX,
        int tileY,
        int width,
        int height
    )
    {
        if (!Context.IsWorldReady)
        {
            return new ClearAreaResult { Success = false, Error = "Not in a game world" };
        }

        if (Game1.player.currentLocation?.Name != locationName)
        {
            return new ClearAreaResult
            {
                Success = false,
                Error =
                    $"Player is on '{Game1.player.currentLocation?.Name}', not '{locationName}'",
            };
        }

        Game1.player.currentLocation.removeObjectsAndSpawned(tileX, tileY, width, height);
        return new ClearAreaResult
        {
            Success = true,
            LocationName = locationName,
            TileX = tileX,
            TileY = tileY,
            Width = width,
            Height = height,
        };
    }

    /// <summary>
    /// Till a tile into a terrain HoeDirt via the vanilla hoe path
    /// (<c>GameLocation.makeHoeDirt</c>), which checks the Diggable tile
    /// property and collisions the way a real hoe swing does.
    /// </summary>
    public TillTileResult TillTile(string locationName, int tileX, int tileY)
    {
        if (!Context.IsWorldReady)
        {
            return new TillTileResult { Success = false, Error = "Not in a game world" };
        }

        if (Game1.player.currentLocation?.Name != locationName)
        {
            return new TillTileResult
            {
                Success = false,
                Error =
                    $"Player is on '{Game1.player.currentLocation?.Name}', not '{locationName}'",
            };
        }

        var location = Game1.player.currentLocation;
        var tile = new Vector2(tileX, tileY);

        if (!location.makeHoeDirt(tile))
        {
            return new TillTileResult
            {
                Success = false,
                Error =
                    $"makeHoeDirt refused ({tileX},{tileY}) — tile not Diggable, blocked, "
                    + "or already tilled",
            };
        }

        return new TillTileResult
        {
            Success = true,
            LocationName = locationName,
            TileX = tileX,
            TileY = tileY,
        };
    }

    /// <summary>
    /// Plant a seed via <c>HoeDirt.plant</c>. The dirt may be a terrain HoeDirt or
    /// the inner HoeDirt of a Garden Pot at the same tile. <c>plant</c> requires
    /// <c>player.currentLocation == dirt.Location</c>.
    /// </summary>
    public PlantCropResult PlantCrop(string itemId, string locationName, int tileX, int tileY)
    {
        if (!Context.IsWorldReady)
        {
            return new PlantCropResult { Success = false, Error = "Not in a game world" };
        }

        if (Game1.player.currentLocation?.Name != locationName)
        {
            return new PlantCropResult
            {
                Success = false,
                Error =
                    $"Player is on '{Game1.player.currentLocation?.Name}', not '{locationName}'",
            };
        }

        var location = Game1.player.currentLocation;
        var tile = new Vector2(tileX, tileY);

        // Pot first: a Garden Pot on a tilled tile coexists with an empty
        // terrain HoeDirt at the same key, and real interaction always reaches
        // the pot (it occupies the tile) — mirrors CropSaver's
        // pot-wins-the-tile rule so tests plant where a player would.
        HoeDirt? dirt = null;
        if (location.Objects.TryGetValue(tile, out var obj) && obj is IndoorPot pot)
        {
            dirt = pot.hoeDirt.Value;
        }
        else if (location.terrainFeatures.TryGetValue(tile, out var tf) && tf is HoeDirt td)
        {
            dirt = td;
        }

        if (dirt == null)
        {
            return new PlantCropResult
            {
                Success = false,
                Error = $"No HoeDirt or IndoorPot at ({tileX},{tileY})",
            };
        }

        if (!dirt.plant(itemId, Game1.player, isFertilizer: false))
        {
            return new PlantCropResult
            {
                Success = false,
                Error = $"HoeDirt.plant rejected '{itemId}'",
            };
        }

        return new PlantCropResult
        {
            Success = true,
            LocationName = locationName,
            TileX = tileX,
            TileY = tileY,
            SeedItemId = itemId,
        };
    }

    /// <summary>
    /// Lists this client's view of the farm's cabin buildings (name + tile). The server
    /// rewrites each peer's locationIntroduction copy (CabinManagerService), so the client's
    /// farm can differ from master state — e.g. a dummy cabin relocated to the shared stack
    /// for a player whose own cabin was moved. /cabins (master state) cannot observe that, so
    /// this is the positive-observation gate for the per-peer cabin mutations.
    /// </summary>
    public FarmBuildingsResult GetFarmBuildings()
    {
        if (!Context.IsWorldReady)
        {
            return new FarmBuildingsResult { Success = false, Error = "Not in a game world" };
        }

        var farm = Game1.getFarm();
        if (farm == null)
        {
            return new FarmBuildingsResult { Success = false, Error = "Farm not loaded" };
        }

        var cabins = new List<FarmBuildingInfo>();
        foreach (var building in farm.buildings)
        {
            if (!building.isCabin)
            {
                continue;
            }

            cabins.Add(
                new FarmBuildingInfo
                {
                    Name = building.GetIndoors()?.NameOrUniqueName ?? "",
                    TileX = building.tileX.Value,
                    TileY = building.tileY.Value,
                    // HasInterior == false means the door is dead: Building.doAction only warps
                    // the player inside when GetIndoors() != null, so a null interior is an
                    // unenterable (door-dead) cabin — what the dummy-cabin prop must be.
                    HasInterior = building.GetIndoors() != null,
                }
            );
        }

        return new FarmBuildingsResult { Success = true, Cabins = cabins };
    }

    /// <summary>
    /// Lists the current location's warps as this client resolves them. Warp targets ride
    /// replication deltas whose values stay invisible until the netcode interpolation
    /// window elapses (15 client ticks), so tests poll this before walking onto a warp —
    /// the values here are exactly what a walk would use.
    /// </summary>
    public LocationWarpsResult GetLocationWarps()
    {
        if (!Context.IsWorldReady)
        {
            return new LocationWarpsResult { Success = false, Error = "Not in a game world" };
        }

        var location = Game1.currentLocation;
        if (location == null)
        {
            return new LocationWarpsResult { Success = false, Error = "No current location" };
        }

        return new LocationWarpsResult
        {
            Success = true,
            LocationName = location.NameOrUniqueName,
            Warps = location
                .warps.Select(w => new LocationWarpInfo
                {
                    X = w.X,
                    Y = w.Y,
                    TargetName = w.TargetName,
                    TargetX = w.TargetX,
                    TargetY = w.TargetY,
                })
                .ToList(),
        };
    }

    /// <summary>
    /// Walks the player onto a warp/door tile and triggers the resulting transition the way
    /// real play does. With no tile given, targets the current location's first warp (a
    /// cabin interior's only warps are its exit mat). The player is stood on the adjacent
    /// tile opposite <paramref name="direction"/> and then stepped forward with the exact
    /// per-step pair vanilla movement runs (Farmer.MovePositionImpl: nextPosition →
    /// GameLocation.isCollidingWithWarp → Farmer.warpFarmer); when no warp triggers along
    /// the walk, the action-press door path is tried at the target tile
    /// (GameLocation.checkAction — building humanDoors warp via Building.doAction).
    /// Source of truth: decompiled Farmer.MovePositionImpl / GameLocation.checkAction —
    /// keep this in sync if their resolution changes. The transition itself is
    /// asynchronous; callers confirm arrival via /status (WaitForLocationAsync).
    /// </summary>
    public WalkOntoTileResult WalkOntoTile(int? tileX, int? tileY, int direction = 2)
    {
        if (!Context.IsWorldReady)
        {
            return new WalkOntoTileResult { Success = false, Error = "Not in a game world" };
        }

        if (Game1.eventUp)
        {
            return new WalkOntoTileResult
            {
                Success = false,
                Error = "An event is up; warps are suppressed (Farmer.warpFarmer no-ops)",
            };
        }

        var location = Game1.currentLocation;
        if (location == null)
        {
            return new WalkOntoTileResult { Success = false, Error = "No current location" };
        }

        if (tileX == null || tileY == null)
        {
            var firstWarp = location.warps.FirstOrDefault();
            if (firstWarp == null)
            {
                return new WalkOntoTileResult
                {
                    Success = false,
                    Error = $"No warps in {location.NameOrUniqueName} to auto-target",
                };
            }

            tileX = firstWarp.X;
            tileY = firstWarp.Y;
        }

        var player = Game1.player;
        var (standX, standY) = direction switch
        {
            0 => (tileX.Value, tileY.Value + 1), // stepping up
            1 => (tileX.Value - 1, tileY.Value), // stepping right
            3 => (tileX.Value + 1, tileY.Value), // stepping left
            _ => (tileX.Value, tileY.Value - 1), // stepping down (default)
        };
        player.Position = new Vector2(standX * 64f, standY * 64f);
        player.faceDirection(direction);

        // Walk up to two tiles, mirroring MovePositionImpl's per-step warp check + advance.
        var speed = Math.Max(1f, player.getMovementSpeed());
        var (dx, dy) = direction switch
        {
            0 => (0f, -1f),
            1 => (1f, 0f),
            3 => (-1f, 0f),
            _ => (0f, 1f),
        };
        var maxSteps = (int)Math.Ceiling(128f / speed);
        for (var i = 0; i < maxSteps; i++)
        {
            var next = player.nextPosition(direction);
            var warp = location.isCollidingWithWarp(next, player);
            if (warp != null)
            {
                player.warpFarmer(warp, direction);
                return new WalkOntoTileResult
                {
                    Success = true,
                    Via = "warp",
                    TargetLocation = warp.TargetName,
                    TargetX = warp.TargetX,
                    TargetY = warp.TargetY,
                };
            }

            player.Position += new Vector2(dx * speed, dy * speed);
        }

        // No warp along the walk — try the action-press door path at the target tile.
        if (
            location.checkAction(
                new xTile.Dimensions.Location(tileX.Value, tileY.Value),
                Game1.viewport,
                player
            )
        )
        {
            return new WalkOntoTileResult
            {
                Success = true,
                Via = "door",
                TargetLocation = Game1.locationRequest?.Name,
            };
        }

        return new WalkOntoTileResult
        {
            Success = false,
            Error =
                $"No warp or door triggered at ({tileX},{tileY}) in {location.NameOrUniqueName}",
        };
    }
}

public class FarmBuildingInfo
{
    public string Name { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }

    /// <summary>True if this building has an interior the player can enter (door is live).</summary>
    public bool HasInterior { get; set; }
}

public class FarmBuildingsResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public List<FarmBuildingInfo> Cabins { get; set; } = new();
}

public class LocationWarpInfo
{
    /// <summary>Trigger tile in the current location.</summary>
    public int X { get; set; }

    public int Y { get; set; }

    public string TargetName { get; set; } = "";

    /// <summary>Landing tile in the target location, as currently visible to this client.</summary>
    public int TargetX { get; set; }

    public int TargetY { get; set; }
}

public class LocationWarpsResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? LocationName { get; set; }
    public List<LocationWarpInfo> Warps { get; set; } = new();
}

public class WalkOntoTileParams
{
    /// <summary>Target tile. Omit both to auto-target the current location's first warp.</summary>
    public int? TileX { get; set; }

    public int? TileY { get; set; }

    /// <summary>Step direction (0 up, 1 right, 2 down, 3 left). Default: 2 (down).</summary>
    public int Direction { get; set; } = 2;
}

public class WalkOntoTileResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>How the transition triggered: "warp" (touch warp) or "door" (action press).</summary>
    public string? Via { get; set; }

    /// <summary>The transition's target location name (for "door", the queued request's name).</summary>
    public string? TargetLocation { get; set; }

    /// <summary>Warp landing tile ("warp" only).</summary>
    public int? TargetX { get; set; }

    public int? TargetY { get; set; }
}

public class SleepResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Location { get; set; }
    public string? Error { get; set; }
}

public class WarpResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public string? LocationName { get; set; }
}

public class LeaveFestivalResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
}

public class EngageToNpcResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? Spouse { get; set; }
    public bool IsEngaged { get; set; }
}

public class EngageToNpcParams
{
    public string Npc { get; set; } = "";
    public int DaysUntilWedding { get; set; } = 1;
}

public class WarpParams
{
    public string LocationName { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }
}

public class PlacePotResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public string? LocationName { get; set; }
    public int? TileX { get; set; }
    public int? TileY { get; set; }
}

public class PlacePotParams
{
    public string LocationName { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }

    /// <summary>If true, remove any existing Object or terrainFeature at the
    /// tile before placing the pot (handles spawn-time rocks/weeds/twigs).</summary>
    public bool ClearObstacles { get; set; }
}

public class ClearAreaResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? LocationName { get; set; }
    public int TileX { get; set; }
    public int TileY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public class ClearAreaParams
{
    public string LocationName { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public class TillTileResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? LocationName { get; set; }
    public int? TileX { get; set; }
    public int? TileY { get; set; }
}

public class TillTileParams
{
    public string LocationName { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }
}

public class PlantCropResult
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public string? Error { get; set; }
    public string? LocationName { get; set; }
    public int? TileX { get; set; }
    public int? TileY { get; set; }
    public string? SeedItemId { get; set; }
}

public class PlantCropParams
{
    public string ItemId { get; set; } = "";
    public string LocationName { get; set; } = "";
    public int TileX { get; set; }
    public int TileY { get; set; }
}
