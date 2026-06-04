using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Objects;
using System.Linq;

namespace JunimoServer.Services.AlwaysOn
{
    /// <summary>
    /// The host's main farmhouse is internal-only and stays at level 0 (see #346: mirroring a
    /// farmhand's cabin level onto it put the bed across the exit and soft-locked everyone). No
    /// in-game flow can upgrade it — Robin's "UpgradeHouse" option is master-only and never renders
    /// on a farmhand's client, and the host bot never opens Robin. The one residual path is an admin
    /// running a debug command at the server console / host chat (the mod enables cheats via
    /// <c>Program.enableCheats</c>), which targets <see cref="Game1.player"/> = the host.
    ///
    /// This blocks the three vanilla debug house-upgrade handlers when they would target the host's
    /// main farmhouse, while leaving farmhand cabin upgrades (a <see cref="Cabin"/>) untouched. The
    /// handlers live on the nested <c>DebugCommands.DefaultHandlers</c> type and are bound into the
    /// command table via reflection; Harmony patches the method bodies the cached delegates dispatch
    /// through, so the prefixes run.
    ///
    /// Also exposes <see cref="ResetHostFarmhouseToLevelZero"/>, the transitional load-time self-heal
    /// for saves the old mirroring behavior already corrupted (see that method's note).
    /// </summary>
    public static class HostFarmhouseUpgradeGuard
    {
        private static IMonitor _monitor;

        public static void Initialize(IMonitor monitor) => _monitor = monitor;

        /// <summary>
        /// Reset the host's main farmhouse to level 0 (the load-time self-heal for #346).
        ///
        /// We can't rely on vanilla's relative-shift upgrade machinery here: a save corrupted by the
        /// old bare HouseUpgradeLevel write never had its furniture shifted into the upgraded frame,
        /// and <c>setMapForUpgradeLevel</c>'s bed-relocation block is gated/skipped for several
        /// previous-level combinations (the bed lands off the level-0 DefaultBedPosition). So after the
        /// vanilla relayout loads the level-0 map, deterministically force a single default bed onto
        /// the level-0 DefaultBedPosition: remove every existing bed, then add a fresh one at the
        /// correct tile. Level 0 expects a Single bed (<see cref="FarmHouse.GetPlayerBed"/>), so this
        /// also fixes a leftover Double bed.
        /// </summary>
        public static void ResetHostFarmhouseToLevelZero()
        {
            var farmhouse = Utility.getHomeOfFarmer(Game1.player);

            // Load the level-0 map + objects (vanilla relayout). Bed placement is fixed up below.
            farmhouse.moveObjectsForHouseUpgrade(0);
            farmhouse.setMapForUpgradeLevel(0);
            Game1.player.HouseUpgradeLevel = 0;
            Game1.addNewFarmBuildingMaps();
            farmhouse.ReadWallpaperAndFloorTileData();
            farmhouse.RefreshFloorObjectNeighbors();

            ForceDefaultBedAtLevelZero(farmhouse);
        }

        /// <summary>
        /// Remove every bed in the farmhouse and place one fresh default (Single) bed on the level-0
        /// DefaultBedPosition tile, so the host always has a reachable bed that
        /// <see cref="FarmHouse.GetPlayerBed"/> can find. No-op if the map has no DefaultBedPosition.
        /// </summary>
        private static void ForceDefaultBedAtLevelZero(FarmHouse farmhouse)
        {
            if (!TryGetDefaultBedPosition(farmhouse, out var bedPos))
            {
                _monitor?.Log("Host farmhouse heal: no DefaultBedPosition on the level-0 map; skipped bed fixup.", LogLevel.Warn);
                return;
            }

            foreach (var bed in farmhouse.furniture.OfType<BedFurniture>().ToList())
            {
                bed.performRemoveAction();
                farmhouse.furniture.Remove(farmhouse.furniture.GuidOf(bed));
            }

            farmhouse.furniture.Add(new BedFurniture(BedFurniture.DEFAULT_BED_INDEX, new Vector2(bedPos.X, bedPos.Y)));
        }

        /// <summary>
        /// Scan the farmhouse Back layer for the "DefaultBedPosition" tile property.
        /// </summary>
        private static bool TryGetDefaultBedPosition(FarmHouse farmhouse, out Point position)
        {
            var map = farmhouse.Map;
            if (map?.Layers.Count > 0)
            {
                var layer = map.Layers[0];
                for (int x = 0; x < layer.LayerWidth; x++)
                {
                    for (int y = 0; y < layer.LayerHeight; y++)
                    {
                        if (farmhouse.doesTileHaveProperty(x, y, "DefaultBedPosition", "Back") != null)
                        {
                            position = new Point(x, y);
                            return true;
                        }
                    }
                }
            }

            position = Point.Zero;
            return false;
        }

        /// <summary>
        /// Prefix for <c>DebugCommands.DefaultHandlers.HouseUpgrade</c> and <c>.UpgradeHouse</c>, both
        /// of which act on <c>Utility.getHomeOfFarmer(Game1.player)</c> / <c>Game1.player.HouseUpgradeLevel</c>
        /// — always the host's main farmhouse on the dedicated server. Always blocked.
        /// </summary>
        // ReSharper disable once InconsistentNaming
        public static bool BlockHostHouseUpgrade_Prefix()
        {
            LogBlocked();
            return false; // skip original
        }

        /// <summary>
        /// Prefix for <c>DebugCommands.DefaultHandlers.ThisHouseUpgrade</c>, which upgrades the FarmHouse
        /// the player stands in/next to. Block only when that resolves to the main farmhouse; a
        /// <see cref="Cabin"/> (a farmhand's home) upgrades normally. Mirrors the handler's own target
        /// resolution (decompiled DebugCommands.cs:2159) so a cabin is never accidentally blocked.
        /// </summary>
        // ReSharper disable once InconsistentNaming
        public static bool BlockThisHouseUpgrade_Prefix()
        {
            var target = (Game1.currentLocation?.getBuildingAt(Game1.player.Tile + new Vector2(0f, -1f))?.GetIndoors() as FarmHouse)
                         ?? (Game1.currentLocation as FarmHouse);

            // Cabin is a FarmHouse subclass; only the main farmhouse is a plain FarmHouse. Let cabins through.
            if (target is null || target is Cabin)
            {
                return true; // run original (farmhand cabin upgrade, or not a house at all)
            }

            LogBlocked();
            return false; // skip original (would upgrade the host's main farmhouse)
        }

        private static void LogBlocked() =>
            _monitor?.Log(
                "Blocked a debug house-upgrade command targeting the host's main farmhouse — it is " +
                "intentionally fixed at level 0 (internal-only). Upgrade a farmhand's cabin instead.",
                LogLevel.Warn);
    }
}
