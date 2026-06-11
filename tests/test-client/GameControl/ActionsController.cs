using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;
using System;
using System.Reflection;

namespace JunimoTestClient.GameControl;

/// <summary>
/// Controller for in-game actions like sleeping, warping, placing pots, and planting crops.
/// </summary>
public class ActionsController
{
    private readonly IMonitor _monitor;

    private static readonly MethodInfo? StartSleepMethod =
        typeof(GameLocation).GetMethod("startSleep", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? typeof(FarmHouse).GetMethod("startSleep", BindingFlags.NonPublic | BindingFlags.Instance);

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
                return new SleepResult { Success = false, Error = "Could not find player's home location" };
            }

            if (StartSleepMethod == null)
            {
                return new SleepResult { Success = false, Error = "Could not find startSleep method via reflection" };
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

            _monitor.Log($"Triggered sleep at {home.NameOrUniqueName} (bed @ {bedSpot})", LogLevel.Info);

            return new SleepResult
            {
                Success = true,
                Message = $"Going to sleep at {home.NameOrUniqueName}",
                Location = home.NameOrUniqueName
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
            return new WarpResult { Success = false, Error = "Not in a game world" };

        Game1.warpFarmer(locationName, tileX, tileY, Game1.player.FacingDirection);
        return new WarpResult
        {
            Success = true,
            Message = $"Warp queued to {locationName} ({tileX},{tileY})",
            LocationName = locationName
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
            return new PlacePotResult { Success = false, Error = "Not in a game world" };

        if (Game1.player.currentLocation?.Name != locationName)
            return new PlacePotResult
            {
                Success = false,
                Error = $"Player is on '{Game1.player.currentLocation?.Name}', not '{locationName}'"
            };

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
            return new PlacePotResult
            {
                Success = false,
                Error = $"Tile ({tileX},{tileY}) is occupied"
            };

        location.Objects.Add(tile, new IndoorPot(tile));
        return new PlacePotResult
        {
            Success = true,
            LocationName = locationName,
            TileX = tileX,
            TileY = tileY
        };
    }

    /// <summary>
    /// Clear objects, terrain features, bushes, and resource clumps from a tile area
    /// on the player's current location, via the same <c>removeObjectsAndSpawned</c>
    /// the game uses to clear under a placed building. Lets placement tests prepare a
    /// debris-free footprint so cabin/building validation isn't tripped by spawn-time
    /// rocks/weeds/grass.
    /// </summary>
    public ClearAreaResult ClearArea(string locationName, int tileX, int tileY, int width, int height)
    {
        if (!Context.IsWorldReady)
            return new ClearAreaResult { Success = false, Error = "Not in a game world" };

        if (Game1.player.currentLocation?.Name != locationName)
            return new ClearAreaResult
            {
                Success = false,
                Error = $"Player is on '{Game1.player.currentLocation?.Name}', not '{locationName}'"
            };

        Game1.player.currentLocation.removeObjectsAndSpawned(tileX, tileY, width, height);
        return new ClearAreaResult
        {
            Success = true,
            LocationName = locationName,
            TileX = tileX,
            TileY = tileY,
            Width = width,
            Height = height
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
            return new PlantCropResult { Success = false, Error = "Not in a game world" };

        if (Game1.player.currentLocation?.Name != locationName)
            return new PlantCropResult
            {
                Success = false,
                Error = $"Player is on '{Game1.player.currentLocation?.Name}', not '{locationName}'"
            };

        var location = Game1.player.currentLocation;
        var tile = new Vector2(tileX, tileY);

        HoeDirt? dirt = null;
        if (location.terrainFeatures.TryGetValue(tile, out var tf) && tf is HoeDirt td) dirt = td;
        else if (location.Objects.TryGetValue(tile, out var obj) && obj is IndoorPot pot) dirt = pot.hoeDirt.Value;

        if (dirt == null)
            return new PlantCropResult { Success = false, Error = $"No HoeDirt or IndoorPot at ({tileX},{tileY})" };

        if (!dirt.plant(itemId, Game1.player, isFertilizer: false))
            return new PlantCropResult { Success = false, Error = $"HoeDirt.plant rejected '{itemId}'" };

        return new PlantCropResult
        {
            Success = true,
            LocationName = locationName,
            TileX = tileX,
            TileY = tileY,
            SeedItemId = itemId
        };
    }
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
