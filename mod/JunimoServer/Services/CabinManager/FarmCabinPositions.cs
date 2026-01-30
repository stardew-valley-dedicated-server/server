using Microsoft.Xna.Framework;
using StardewValley;
using System.Collections.Generic;
using System.Linq;
using xTile.Tiles;

namespace JunimoServer.Services.CabinManager
{
    /// <summary>
    /// Reads map-designated cabin positions from the farm map's Paths layer.
    /// The vanilla game uses tile indices 29/30 with an "Order" property to
    /// determine where starting cabins should be placed.
    /// </summary>
    public static class FarmCabinPositions
    {
        /// <summary>
        /// Returns all map-designated cabin positions for the given farm, sorted by Order.
        /// Reads both tile index 29 (grouped) and 30 (separate) since we just need valid locations.
        /// </summary>
        public static List<Vector2> GetDesignatedPositions(Farm farm)
        {
            var positions = new List<(int order, Vector2 position)>();
            var layer = farm.map?.GetLayer("Paths");

            if (layer == null)
            {
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

                    if (tile.Properties.TryGetValue("Order", out var orderValue) &&
                        int.TryParse(orderValue?.ToString(), out int order))
                    {
                        positions.Add((order, new Vector2(x, y)));
                    }
                }
            }

            return positions
                .OrderBy(p => p.order)
                .Select(p => p.position)
                .ToList();
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
        /// Returns the next available designated position where no building currently exists.
        /// Returns null if all designated positions are occupied.
        /// </summary>
        public static Vector2? GetNextAvailablePosition(Farm farm)
        {
            var positions = GetDesignatedPositions(farm);

            foreach (var pos in positions)
            {
                if (!IsPositionOccupied(farm, pos))
                {
                    return pos;
                }
            }

            return null;
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
}
