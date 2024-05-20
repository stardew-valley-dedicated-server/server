using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.TerrainFeatures;

namespace JunimoServer.Services.CropSaver
{
    public class CropWatcher
    {
        private readonly Dictionary<Vector2, bool> _previousHasCrop = new Dictionary<Vector2, bool>();

        private readonly Action<TerrainFeature> _onCropAdded;
        private readonly Action<TerrainFeature> _onCropRemoved;
        
        private const int UpdateEveryTicks = 5;

        public CropWatcher(IModHelper helper, Action<TerrainFeature> onCropAdded, Action<TerrainFeature> onCropRemoved)
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
            var farmLocation = Game1.getLocationFromName("Farm");
            if (farmLocation == null) return;

            var farmTerrainFeatures = farmLocation.terrainFeatures.Values;

            foreach (var feature in farmTerrainFeatures)
            {
                var tileLoc = feature.Tile;
                var hasCrop = feature.ContainsCrop();
                if (!_previousHasCrop.ContainsKey(tileLoc))
                {
                    _previousHasCrop.Add(tileLoc, hasCrop);
                    continue;
                }

                if (hasCrop && !_previousHasCrop[tileLoc])
                {
                    _onCropAdded(feature);
                }

                if (!hasCrop && _previousHasCrop[tileLoc])
                {
                    _onCropRemoved(feature);
                }

                _previousHasCrop[tileLoc] = hasCrop;
            }
        }
    }
}