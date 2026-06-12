using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace JunimoServer.Services.AlwaysOn;

public static class AlwaysOnUtil
{
    /// <summary>
    /// True while a <see cref="ReadyCheckDialog"/> (sleep, festival start/end,
    /// ready-for-save, wakeup) is the active menu. Any host-side automation
    /// that would replace <see cref="Game1.activeClickableMenu"/> — directly,
    /// or via engine calls like <see cref="Game1.drawObjectDialogue(string)"/>,
    /// <see cref="GameLocation.mailbox"/>, <see cref="Utility.TryOpenShopMenu(string, string, bool)"/>,
    /// <c>Event.namePet</c> — MUST bail out when this returns true.
    ///
    /// Why: <c>ReadyCheckDialog.update()</c> is the only code that polls
    /// <c>netReady</c> and calls <c>doSleep()</c>/<c>NewDay()</c>. Its
    /// <c>emergencyShutDown()</c> is the base no-op and does not release the
    /// local-ready flag, so if the dialog is replaced the server-side
    /// handshake finishes but nothing ever invokes <c>NewDay</c> and the day
    /// stalls. We protect all ready-check variants uniformly — the invariant
    /// is "don't clobber a handshake menu", not "don't clobber sleep
    /// specifically".
    /// </summary>
    public static bool IsReadyCheckActive() => Game1.activeClickableMenu is ReadyCheckDialog;

    /// <summary>
    /// Warps the host to the Farm map's default entry tile. Used to park the
    /// automated host off to the side so clients don't see it occupying doors.
    /// </summary>
    public static void WarpToFarmDefaultSpawn()
    {
        int x = 0,
            y = 0;
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
    public static void DrawTextBox(
        int x,
        int y,
        SpriteFont font,
        string message,
        int align = 0,
        float colorIntensity = 1f
    )
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

        IClickableMenu.drawTextureBox(
            spriteBatch,
            Game1.menuTexture,
            new Rectangle(0, 256, 60, 60),
            xPosTextureBox,
            y,
            width,
            num + 4,
            Color.White * colorIntensity
        );
        Utility.drawTextWithShadow(
            spriteBatch,
            message,
            font,
            new Vector2(xPosText, y + 16),
            Game1.textColor
        );
    }
}
