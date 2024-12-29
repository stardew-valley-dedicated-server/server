using JunimoServer.Util;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.GameData.WorldMaps;
using StardewValley.WorldMaps;
using System.Collections.Generic;
using System.IO;


namespace JunimoServer.Services.Map
{
    public class MapMessage
    {
        public readonly string type = "MapMessage";
        public MapSyncData data;
    }

    public class MapSyncData
    {
        public List<MapSyncPlayerData> players;
    }

    public class MapSyncPlayerData
    {
        public string uniqueMultiplayerID;
        public string name;
        public Vector2 position;
        public Color hairColor;
        public Vector2 hairOffset;
    }

    public class TextureData
    {
        public Texture2D texture;
        public Rectangle rect;
    }

    class MapService
    {
        private readonly string[] _seasons = { "summer", "fall", "winter" };

        private MapSyncData _syncData = new MapSyncData();

        private readonly IModHelper _helper;
        private readonly IMonitor _monitor;
        private readonly WebSocketClient _ws;

        public MapService(IModHelper helper, IMonitor monitor, WebSocketClient ws)
        {
            _helper = helper;
            _monitor = monitor;
            _ws = ws;

            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTick;
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs args)
        {
            SyncMapTextures();
        }


        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            // Send every 7 ticks
            if (e.Ticks % 7 == 0)
            {
                _syncData.players = new List<MapSyncPlayerData>();
                SyncFarmerPositions();

                _ws.Send(Newtonsoft.Json.JsonConvert.SerializeObject(new MapMessage() { data = _syncData }));
            }

        }

        private void OnOneSecondUpdateTick(object sender, OneSecondUpdateTickedEventArgs args)
        {
            // Reset data
            //_syncData.players = new List<MapSyncPlayerData>();

            SyncFarmerPortraits();
            //SyncFarmerPositions();

            // Sync data
            //SaveJson($"positions.json", _syncData);
        }

        #region Farmer Position
        private void SyncFarmerPositions()
        {
            // _monitor.Log($"Syncing player positions", LogLevel.Info);

            Dictionary<Vector2, int> usedPositions = new Dictionary<Vector2, int>();

            foreach (Farmer player in Game1.getOnlineFarmers())
            {
                Point tile = MapUtil.GetNormalizedPlayerTile(player);
                MapAreaPositionWithContext? positionDataContext = WorldMapManager.GetPositionData(player.currentLocation, tile);

                // Skip if farmer is not visible on a map
                if (!positionDataContext.HasValue)
                {
                    continue;
                }

                MapAreaPosition positionData = positionDataContext.Value.Data;

                foreach (MapRegion region in GetRegions())
                {
                    // Skip for all except the region the player is in
                    if (positionData.Region.Id != region.Id)
                    {
                        continue;
                    }

                    Rectangle mapBounds = region.GetMapPixelBounds();
                    Vector2? mapPixelPos = positionData.GetMapPixelPosition(player.currentLocation, tile);

                    if (mapPixelPos == null || !mapPixelPos.HasValue)
                    {
                        continue;
                    }

                    Vector2? pos = mapPixelPos;
                    // TODO: Find out why we have to use -32f here. Probably accounting for avatar/marker image size? MapBounds are ignored on ALL our calculations.
                    //pos = new Vector2(pos.X + (float)mapBounds.X - 32f, pos.Y + (float)mapBounds.Y - 32f);

                    // Prevent markers from overlapping each other
                    usedPositions.TryGetValue(pos.Value, out var count);
                    usedPositions[pos.Value] = count + 1;
                    if (count > 0)
                    {
                        pos += new Vector2(48 * (count % 2), 48 * (count / 2));
                    }

                    // Add processed data for player
                    _syncData.players.Add(new MapSyncPlayerData()
                    {
                        uniqueMultiplayerID = player.UniqueMultiplayerID.ToString(),
                        name = player.Name,
                        position = pos.Value,
                        hairColor = GetHairColor(player),
                        hairOffset = GetHairOffset(player)
                    });

                    // Player can probably only be visible on one region, so bailing here
                    if (positionData.Region.Id == region.Id)
                    {
                        break;
                    }
                }
            }

            // _monitor.Log($"Syncing player positions done", LogLevel.Info);
        }
        #endregion

        #region Farmer Portraits
        private void SyncFarmerPortraits()
        {
            foreach (Farmer farmer in Game1.getOnlineFarmers())
            {
                SaveFarmerPortrait(farmer);
            }
        }

        // StardewValley.FarmerRenderer.MapPage
        // StardewValley.FarmerRenderer.drawMiniPortrat
        private void SaveFarmerPortrait(Farmer farmer)
        {
            // Make sure that correct colors are applied to textures
            _helper.Reflection.GetMethod(farmer.FarmerRenderer, "executeRecolorActions").Invoke(farmer);

            TextureData baseTexture = GetFarmerBaseTexture(farmer);
            SaveTexture($"portrait_{farmer.UniqueMultiplayerID}_base.png", baseTexture.texture, baseTexture.rect);

            TextureData hairTexture = GetFarmerHairTexture(farmer);
            SaveTexture($"portrait_{farmer.UniqueMultiplayerID}_hair.png", hairTexture.texture, hairTexture.rect);
        }

        private TextureData GetFarmerBaseTexture(Farmer farmer)
        {
            return new TextureData()
            {
                texture = _helper.Reflection.GetField<Texture2D>(farmer.FarmerRenderer, "baseTexture").GetValue(),
                rect = new Rectangle(0, 0, 16, farmer.IsMale ? 15 : 16)
            };
        }

        private TextureData GetFarmerHairTexture(Farmer farmer)
        {
            int hairStyleIndex = farmer.getHair(ignore_hat: true);
            HairStyleMetadata hairMetadata = Farmer.GetHairStyleMetadata(hairStyleIndex);

            Texture2D texture = hairMetadata != null
                ? hairMetadata.texture
                : FarmerRenderer.hairStylesTexture;

            Rectangle rect = hairMetadata != null
                ? new Rectangle(hairMetadata.tileX * 16, hairMetadata.tileY * 16, 16, 15)
                : new Rectangle(hairStyleIndex * 16 % FarmerRenderer.hairStylesTexture.Width, hairStyleIndex * 16 / FarmerRenderer.hairStylesTexture.Width * 96, 16, 15);

            return new TextureData()
            {
                texture = texture,
                rect = rect
            };
        }

        private Color GetHairColor(Farmer player)
        {
            return (bool)player.prismaticHair.Value ? Utility.GetPrismaticColor() : player.hairstyleColor.Value;
        }

        private Vector2 GetHairOffset(Farmer farmer)
        {
            int hairStyleIndex = farmer.getHair(ignore_hat: true);
            return new Vector2(0f, FarmerRenderer.featureYOffsetPerFrame[0] * 4 + ((farmer.IsMale && hairStyleIndex >= 16) ? (-4) : ((!farmer.IsMale && hairStyleIndex < 16) ? 4 : 0)));
        }
        #endregion

        #region Map Textures
        private void SyncMapTextures()
        {
            _monitor.Log($"Syncing maps", LogLevel.Info);

            int regionIndex = 0;
            foreach (MapRegion region in GetRegions())
            {
                SyncRegionTextures(region, regionIndex);
                regionIndex++;
            }

            _monitor.Log($"Syncing maps done", LogLevel.Info);
        }

        private IEnumerable<MapRegion> GetRegions()
        {
            // Retrieve specific map region
            //var mapAreaPosition = (WorldMapManager.GetPositionData(Game1.player.currentLocation, Game1.player.TilePoint) ?? WorldMapManager.GetPositionData(Game1.getFarm(), Point.Zero)).Region;

            IEnumerable<MapRegion> mapRegions = WorldMapManager.GetMapRegions();
            // _monitor.Log($"Processing map regions ({mapRegions.Count()})", LogLevel.Info);
            return mapRegions;
        }

        private void SyncRegionTextures(MapRegion region, int regionIndex)
        {
            foreach (WorldMapTextureData worldMapTextureData in region.Data.BaseTexture)
            {
                // Save default variant
                _monitor.Log($"Processing region base texture - Name: {worldMapTextureData.Texture}, Season: none", LogLevel.Info);
                SaveTextureData($"region_{regionIndex}.png", MapUtil.GetMapAreaTexture(worldMapTextureData.Texture, worldMapTextureData));

                // Save seasonal variants
                foreach (string season in _seasons)
                {
                    string textureNameSeasonal = worldMapTextureData.Texture + "_" + season.ToLower();
                    _monitor.Log($"Processing region base texture - Name: {worldMapTextureData.Texture}, Season: {season}", LogLevel.Info);

                    // Check if the seasonal texture exists, not all maps have them (e.g. island)
                    if (MapUtil.TextureExists(textureNameSeasonal))
                    {
                        SaveTextureData($"region_{regionIndex}_{season.ToLower()}.png", MapUtil.GetMapAreaTexture(textureNameSeasonal, worldMapTextureData));
                    }
                }
            }
        }
        #endregion

        #region Saving Data
        private void SaveJson(string filename, object data)
        {
            // _monitor.Log($"Saving JSON file ({filename})", LogLevel.Info);
            _helper.Data.WriteServerJsonFile($"{Path.Combine("Data", filename)}", data);
        }

        private void SaveTexture(string filename, Texture2D texture, Rectangle rect)
        {
            // _monitor.Log($"Saving map region base texture ({filename})", LogLevel.Info);
            _helper.Data.WriteServerTextureFile($"{Path.Combine("Images", filename)}", MapUtil.CropFromTexture(texture, rect));
        }

        private void SaveTextureData(string filename, TextureData textureData)
        {
            SaveTexture(filename, textureData.texture, textureData.rect);
        }

        private void SaveTextureData(string filename, MapAreaTexture mapAreaTexture)
        {
            SaveTexture(filename, mapAreaTexture.Texture, mapAreaTexture.SourceRect);
        }
        #endregion
    }
}
