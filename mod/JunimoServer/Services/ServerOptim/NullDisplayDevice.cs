using Microsoft.Xna.Framework.Graphics;
using xTile.Dimensions;
using xTile.Display;
using xTile.Tiles;

namespace JunimoServer.Services.ServerOptim
{
    public class NullDisplayDevice : IDisplayDevice
    {

        public void LoadTileSheet(TileSheet tileSheet)
        {
        }
        public void DisposeTileSheet(TileSheet tileSheet)
        {
        }
        public void BeginScene(SpriteBatch b)
        {
        }
        public void SetClippingRegion(Rectangle clippingRegion)
        {
        }
        public void DrawTile(Tile tile, Location location, float layerDepth)
        {
        }
        public void EndScene()
        {
        }
    }
}