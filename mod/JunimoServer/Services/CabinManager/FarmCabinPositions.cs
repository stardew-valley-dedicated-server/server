using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using xTile.Tiles;

namespace JunimoServer.Services.CabinManager;

/// <summary>
/// Reads map-designated cabin positions from the farm map's Paths layer.
/// The vanilla game uses tile indices 29/30 with an "Order" property to
/// determine where starting cabins should be placed.
/// </summary>
public static class FarmCabinPositions
{
    // Cache the static Paths-layer scan, keyed on Farm identity. A reload/newgame realizes a
    // fresh Farm, so reference inequality rescans and prior-session positions can't leak.
    private static Farm _cachedFarm;
    private static List<Vector2> _cachedPositions;

    /// <summary>
    /// Returns all map-designated cabin positions for the given farm, sorted by Order.
    /// Reads both tile index 29 (grouped) and 30 (separate) since we just need valid locations.
    /// </summary>
    /// <remarks>
    /// Returns the cached list directly (no defensive copy) on a hit, so the type is
    /// <see cref="IReadOnlyList{T}"/> to keep callers from mutating the cache.
    /// </remarks>
    public static IReadOnlyList<Vector2> GetDesignatedPositions(Farm farm)
    {
        if (ReferenceEquals(farm, _cachedFarm))
        {
            return _cachedPositions;
        }

        var positions = new List<(int order, Vector2 position)>();
        var layer = farm.map?.GetLayer("Paths");

        if (layer == null)
        {
            // Not realized yet; don't cache, so a later call on this Farm rescans.
            return new List<Vector2>();
        }

        for (int x = 0; x < layer.LayerWidth; x++)
        {
            for (int y = 0; y < layer.LayerHeight; y++)
            {
                Tile tile = layer.Tiles[x, y];
                if (tile == null)
                {
                    continue;
                }

                if (tile.TileIndex != 29 && tile.TileIndex != 30)
                {
                    continue;
                }

                if (
                    tile.Properties.TryGetValue("Order", out var orderValue)
                    && int.TryParse(orderValue?.ToString(), out int order)
                )
                {
                    positions.Add((order, new Vector2(x, y)));
                }
            }
        }

        var result = positions.OrderBy(p => p.order).Select(p => p.position).ToList();
        _cachedFarm = farm;
        _cachedPositions = result;
        return result;
    }

    /// <summary>
    /// Returns the first designated cabin position (for CabinStack visual placement).
    /// Falls back to (50, 14) only if no map positions exist.
    /// </summary>
    public static Vector2 GetDefaultStackPosition(Farm farm)
    {
        var positions = GetDesignatedPositions(farm);
        return positions.Count > 0 ? positions[0] : new Vector2(50, 14);
    }

    /// <summary>
    /// Returns all designated cabin positions that are not currently occupied by a building.
    /// Mirrors the resolution used by GetNextAvailablePosition: if this returns N entries,
    /// then exactly N successive calls to GetNextAvailablePosition (with no other building
    /// changes in between) will succeed; the (N+1)th will return null.
    /// </summary>
    public static List<Vector2> GetAvailablePositions(Farm farm)
    {
        return GetDesignatedPositions(farm).Where(p => !IsPositionOccupied(farm, p)).ToList();
    }

    /// <summary>
    /// Returns the next available designated position where no building currently exists.
    /// Returns null if all designated positions are occupied.
    /// </summary>
    public static Vector2? GetNextAvailablePosition(Farm farm)
    {
        var available = GetAvailablePositions(farm);
        return available.Count > 0 ? available[0] : null;
    }

    private static bool IsPositionOccupied(Farm farm, Vector2 position)
    {
        var point = position.ToPoint();
        foreach (var building in farm.buildings)
        {
            if (building.tileX.Value == point.X && building.tileY.Value == point.Y)
            {
                return true;
            }
        }
        return false;
    }
}
