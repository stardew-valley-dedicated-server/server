using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

namespace JunimoServer.Services.CropSaver
{
    public class CropWatcher
    {
        private readonly Dictionary<(string, Vector2), bool> _previousHasCrop = new();

        private readonly Action<CropLocation> _onCropAdded;
        private readonly Action<CropLocation> _onCropRemoved;

        private const int UpdateEveryTicks = 5;

        public CropWatcher(
            IModHelper helper,
            Action<CropLocation> onCropAdded,
            Action<CropLocation> onCropRemoved
        )
        {
            helper.Events.GameLoop.UpdateTicked += GameLoopOnUpdateTicked;
            _onCropAdded = onCropAdded;
            _onCropRemoved = onCropRemoved;
        }

        private int _timer = 0;

        private void GameLoopOnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (_timer > 1)
            {
                _timer--;
                return;
            }
            _timer = UpdateEveryTicks;

            Utility.ForEachLocation(location =>
            {
                var locName = location.Name;

                foreach (var feature in location.terrainFeatures.Values)
                {
                    if (feature is not HoeDirt dirt)
                        continue;
                    CheckTile(new CropLocation(location, dirt, locName, dirt.Tile));
                }

                foreach (var pot in location.Objects.Values.OfType<IndoorPot>())
                {
                    var dirt = pot.hoeDirt.Value;
                    if (dirt == null)
                        continue;
                    // Use the pot's tile, not dirt.Tile: an IndoorPot's inner
                    // HoeDirt is not added through GameLocation.terrainFeatures,
                    // so the OnAdded(loc, tilePos) hook that sets dirt.Tile for
                    // terrain features does not run for pots. dirt.Tile lands
                    // on pot.TileLocation only after the IndoorPot.TileLocation
                    // setter runs with hoeDirt.Value already set, which is
                    // typically true post-placement but not guaranteed.
                    CheckTile(new CropLocation(location, dirt, locName, pot.TileLocation));
                }

                return true;
            });
        }

        private void CheckTile(CropLocation cropLoc)
        {
            var key = (cropLoc.LocationName, cropLoc.Tile);
            var hasCrop = cropLoc.HasTrackableCrop;

            // First observation: fire OnCropAdded when a crop is already present.
            // IndoorPot ctor + plant happen between scan windows, so the watcher
            // sees the crop on first observation rather than a false→true edge.
            // OnCropAdded dedupes against CropSaverData so the OnSaveLoaded path
            // (data already on disk) doesn't double-add.
            if (!_previousHasCrop.TryGetValue(key, out var previous))
            {
                if (hasCrop)
                    _onCropAdded(cropLoc);
                _previousHasCrop[key] = hasCrop;
                return;
            }

            if (hasCrop && !previous)
                _onCropAdded(cropLoc);
            else if (!hasCrop && previous)
                _onCropRemoved(cropLoc);

            _previousHasCrop[key] = hasCrop;
        }
    }

    /// <summary>
    /// A located HoeDirt — the watcher resolves the (locationName, tile) pair
    /// once, including the IndoorPot indirection, and passes the resolved pair
    /// alongside the dirt so callbacks don't re-derive the tile from
    /// <c>dirt.Tile</c>. For pots <c>dirt.Tile</c> only matches the pot's tile
    /// after the IndoorPot.TileLocation setter has propagated, which the ctor
    /// flow does not guarantee at the moment the inner HoeDirt is created.
    /// </summary>
    public readonly record struct CropLocation(
        GameLocation Location,
        HoeDirt Dirt,
        string LocationName,
        Vector2 Tile
    )
    {
        public bool HasTrackableCrop => Dirt.crop != null && Dirt.crop.GetData() != null;
    }
}
