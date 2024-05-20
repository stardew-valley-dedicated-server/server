using System;
using System.Collections;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework;
using StardewValley.Pathfinding;
using StardewValley.Locations;
using System.Linq;
using System.Threading;
using StardewValley.Objects;
using StardewValley.Buildings;
using static StardewValley.Menus.LoadGameMenu;
using Microsoft.Xna.Framework.Graphics;

namespace JunimoBot
{
    public static class Util
    {
        public static Point GetBedSpot(Farmer farmer)
        {
            return GetFarmhouseOrCabin(farmer).GetPlayerBedSpot();
        }

        public static FarmHouse GetFarmhouseOrCabin(Farmer player)
        {
            if (player.IsMainPlayer)
            {
                return Game1.getLocationFromName("FarmHouse") as FarmHouse;
            }
            else
            {
                foreach (Building building in Game1.getFarm().buildings)
                {
                    if (building.indoors.Value is Cabin cabin && cabin.owner.UniqueMultiplayerID == player.UniqueMultiplayerID)
                    {
                        return cabin;
                    }
                }
            }

            return null;
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

        public static string ToJson(object data)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(data, Newtonsoft.Json.Formatting.Indented);
        }
    }
}