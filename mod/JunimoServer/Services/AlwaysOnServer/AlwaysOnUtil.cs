using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace JunimoServer.Services.AlwaysOn
{
    public static class AlwaysOnUtil
    {
        public static void WarpToHouse()
        {
            // Default farmhouse door position - inside
            int x = 64;
            int y = 15;

            Game1.warpFarmer("FarmHouse", x, y, false);
        }

        public static void WarpToHidingSpot()
        {
            // Default farmhouse door position - outside
            int x = 64;
            int y = 15;

            // Ensure we use the real door position
            Utility.getDefaultWarpLocation("Farm", ref x, ref y);

            Game1.warpFarmer("Farm", x, y, false);
        }

        /// <summary>
        /// Draws a textbox.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="y"></param>
        /// <param name="font"></param>
        /// <param name="message"></param>
        /// <param name="align"></param>
        /// <param name="colorIntensity"></param>
        public static void DrawTextBox(int x, int y, SpriteFont font, string message, int align = 0, float colorIntensity = 1f)
        {
            SpriteBatch spriteBatch = Game1.spriteBatch;
            int width = (int)font.MeasureString(message).X + 32;
            int num = (int)font.MeasureString(message).Y + 21;

            int xPosTextureBox;
            int xPosText;

            switch (align)
            {
                case 0:
                    xPosTextureBox = x;
                    xPosText = x + 16;
                    break;
                case 1:
                    xPosTextureBox = x - width / 2;
                    xPosText = x + 16 - width / 2;
                    break;
                case 2:
                    xPosTextureBox = x - width;
                    xPosText = x + 16 - width;
                    break;
                default:
                    return;
            }


            IClickableMenu.drawTextureBox(spriteBatch, Game1.menuTexture, new Rectangle(0, 256, 60, 60), xPosTextureBox, y, width, num + 4, Color.White * colorIntensity);
            Utility.drawTextWithShadow(spriteBatch, message, font, new Vector2(xPosText, y + 16), Game1.textColor);
        }
    }
}
