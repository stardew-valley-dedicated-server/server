using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.WorldMaps;
using StardewValley.GameData.WorldMaps;

namespace JunimoServer.Services.Map
{
    static class MapUtil
    {
        public static bool TextureExists(string assetName)
        {
            return Game1.content.DoesAssetExist<Texture2D>(assetName);
        }

        public static MapAreaTexture GetMapAreaTexture(string assetName, WorldMapTextureData worldMapTextureData)
        {
            Texture2D texture = Game1.content.Load<Texture2D>(assetName);

            Rectangle sourceRect = worldMapTextureData.SourceRect;
            if (sourceRect.IsEmpty)
            {
                sourceRect = new Rectangle(0, 0, texture.Width, texture.Height);
            }

            Rectangle mapPixelArea = worldMapTextureData.MapPixelArea;
            if (mapPixelArea.IsEmpty)
            {
                mapPixelArea = sourceRect;
            }

            return new MapAreaTexture(mapPixelArea: new Rectangle(mapPixelArea.X * 4, mapPixelArea.Y * 4, mapPixelArea.Width * 4, mapPixelArea.Height * 4), texture: texture, sourceRect: sourceRect);
        }

        public static Texture2D CropFromTexture(Texture2D texture, Rectangle rect)
        {
            Texture2D destinationTexture = new Texture2D(Game1.graphics.GraphicsDevice, rect.Width, rect.Height);
            Color[] data = new Color[rect.Width * rect.Height];
            texture.GetData(0, rect, data, 0, rect.Width * rect.Height);
            destinationTexture.SetData(data);
            return destinationTexture;
        }

        public static Texture2D CropFromTexture(MapAreaTexture mapAreaTexture)
        {
            return CropFromTexture(mapAreaTexture.Texture, mapAreaTexture.SourceRect);
        }


        public static Point GetNormalizedPlayerTile(Farmer player)
        {
            Point tile = player.TilePoint;
            if (tile.X < 0 || tile.Y < 0)
            {
                tile = new Point(Math.Max(0, tile.X), Math.Max(0, tile.Y));
            }
            return tile;
        }
    }

    static class MapRegionUtil
    {
        public static bool TextureExists(string assetName)
        {
            return Game1.content.DoesAssetExist<Texture2D>(assetName);
        }

        public static MapAreaTexture GetMapAreaTexture(string assetName, WorldMapTextureData worldMapTextureData)
        {
            Texture2D texture = Game1.content.Load<Texture2D>(assetName);

            Rectangle sourceRect = worldMapTextureData.SourceRect;
            if (sourceRect.IsEmpty)
            {
                sourceRect = new Rectangle(0, 0, texture.Width, texture.Height);
            }

            Rectangle mapPixelArea = worldMapTextureData.MapPixelArea;
            if (mapPixelArea.IsEmpty)
            {
                mapPixelArea = sourceRect;
            }

            return new MapAreaTexture(mapPixelArea: new Rectangle(mapPixelArea.X * 4, mapPixelArea.Y * 4, mapPixelArea.Width * 4, mapPixelArea.Height * 4), texture: texture, sourceRect: sourceRect);
        }

        public static Texture2D CropFromTexture(Texture2D texture, Rectangle rect)
        {
            Texture2D destinationTexture = new Texture2D(Game1.graphics.GraphicsDevice, rect.Width, rect.Height);
            Color[] data = new Color[rect.Width * rect.Height];
            texture.GetData(0, rect, data, 0, rect.Width * rect.Height);
            destinationTexture.SetData(data);
            return destinationTexture;
        }

        public static Texture2D CropFromTexture(MapAreaTexture mapAreaTexture)
        {
            return CropFromTexture(mapAreaTexture.Texture, mapAreaTexture.SourceRect);
        }


        public static Point GetNormalizedPlayerTile(Farmer player)
        {
            Point tile = player.TilePoint;
            if (tile.X < 0 || tile.Y < 0)
            {
                tile = new Point(Math.Max(0, tile.X), Math.Max(0, tile.Y));
            }
            return tile;
        }
    }
}
