using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace JunimoServer.Shared
{
    /// <summary>
    /// Debug overlay used exclusively by the E2E test harness (both the server mod and the
    /// test client opt in when running under tests; it is never enabled in production).
    ///
    /// Renders two things on top of the game:
    ///   - a top-left panel of flush-left "LABEL value" rows: a reserved white strip the
    ///     recorder's ffmpeg burn-in writes "TIME {epoch}" into (the in-game side paints the
    ///     strip white but draws no text in it), a "TICK {ticks}" row, and — once a save has
    ///     loaded — a "NAME {farmer}" row. The three share one left inset and an even row
    ///     rhythm so the burn-in, tick counter, and farmer name read as one coherent panel
    ///     rather than two overlapping overlays. (Labels are space-separated, not "LABEL:",
    ///     because a colon in ffmpeg's drawtext text= breaks its avfilter parser.)
    ///   - a red border around the local player's full sprite, so tests can locate the
    ///     farmer at a glance in VNC captures.
    ///
    /// Installed as a Harmony postfix on Game1.Draw(GameTime), so it only renders on frames
    /// that are actually drawn (skipped when rendering is suppressed).
    /// </summary>
    public static class TestOverlay
    {
        private const int BorderWidth = 3;

        // Panel anchor. The white panel starts here; (0,0) lands in the top row's white fill
        // — sampled by RenderingTests.PanelOrigin to detect the overlay; keep in sync.
        private const int PanelOrigin = 0;

        // Every row — the ffmpeg TIME strip and the in-game TICK/NAME rows — is exactly this
        // tall, and its text is vertically centered within it. Uniform height is what makes the
        // vertical rhythm even: the top gap and every inter-row gap are identical regardless of
        // which font (DejaVu vs smallFont) draws the row. Set to Game1.smallFont.LineSpacing
        // (28, per decompiled Game1.cs) so the in-game rows fill their row exactly as the font
        // intends (zero centering remainder); the ffmpeg burn-in's y is tuned to share that band.
        private const int RowHeight = 28;

        // Shared left inset for every row's text — the in-game rows AND the ffmpeg burn-in use
        // it so all rows line up flush-left. Set above a few px because the ffmpeg burn-in
        // (box=0) antialiases ~3px left of its nominal x; an inset of 8 keeps the leftmost glyph
        // near x≈5, leaving pixel (0,0) clear with margin.
        private const int TextInsetX = 8;

        // The reserved top row that the recorder's ffmpeg burn-in draws "TIME {epoch}" into
        // (ContainerRecorder.BurnInFilter: DejaVuSansMono fontsize=24, black text, box=0, x=8,
        // y=5). The in-game side paints this row white but draws NO text in it — ffmpeg fills it.
        // ffmpeg's y=5 places DejaVu's glyphs in the same vertical band smallFont occupies in a
        // RowHeight (28px) row, so the three rows share a baseline. Width must back the longest
        // "TIME {epoch}" string (~290px). Keep x=8 / y=5 / fontsize=24 in sync with
        // ContainerRecorder.cs and the test-overlay-pixel-contract rule
        // — this is a cross-process/cross-TFM pixel contract with no shared symbol, so these
        // comments are the only drift detector.
        private const int ReservedPtsWidth = 340; // measured: "TIME {10-digit epoch}.{6dp}" ends ~x=322 at fontsize=24, +margin

        /// <summary>
        /// Registers the test overlay as a Harmony postfix on Game1.Draw(GameTime).
        /// The caller decides when to call this (e.g. only when SDVD_ENV=test).
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(Game1), "Draw", new[] { typeof(GameTime) }),
                postfix: new HarmonyMethod(typeof(TestOverlay), nameof(Draw_Postfix))
            );
        }

        private static void Draw_Postfix()
        {
            var spriteBatch = Game1.spriteBatch;

            spriteBatch.Begin();
            DrawTopLeftLabels(spriteBatch);
            DrawPlayerHighlight(spriteBatch);
            spriteBatch.End();
        }

        /// <summary>
        /// Draws the top-left panel as uniform <see cref="RowHeight"/>-tall rows: a reserved
        /// top row the ffmpeg burn-in writes "TIME {epoch}" into, a "TICK {ticks}" row, and a
        /// "NAME {farmer}" row once a save has loaded. All rows share one width (at least
        /// <see cref="ReservedPtsWidth"/> so the top row fully backs the burn-in, keeping the
        /// right edge straight) and one height, so the vertical rhythm is even across both fonts.
        /// </summary>
        private static void DrawTopLeftLabels(SpriteBatch spriteBatch)
        {
            var font = Game1.smallFont ?? Game1.tinyFont;

            var tickText = $"TICK {Game1.ticks}";
            var tickSize = font.MeasureString(tickText);

            var rawName = Game1.player?.Name;
            bool hasName = !string.IsNullOrEmpty(rawName);
            var nameText = hasName ? $"NAME {rawName}" : string.Empty;
            var nameSize = hasName ? font.MeasureString(nameText) : Vector2.Zero;

            int textWidth = (int)Math.Ceiling(Math.Max(tickSize.X, nameSize.X));
            int panelWidth = Math.Max(textWidth + TextInsetX + TextInsetX, ReservedPtsWidth);

            // Reserved top row first: a white block the ffmpeg burn-in draws "TIME {epoch}" into.
            // Drawing it unconditionally keeps pixel (0,0) white whenever rendering is on,
            // independent of whether a save has loaded. Same RowHeight as the rows below.
            DrawRow(spriteBatch, panelWidth, PanelOrigin);

            int y = PanelOrigin + RowHeight;
            DrawLabelRow(spriteBatch, font, tickText, tickSize.Y, y, panelWidth);
            y += RowHeight;
            if (hasName)
                DrawLabelRow(spriteBatch, font, nameText, nameSize.Y, y, panelWidth);
        }

        /// <summary>
        /// Fills one <see cref="RowHeight"/>-tall, <paramref name="panelWidth"/>-wide white row
        /// at <paramref name="y"/>. No text — used for the reserved top row the ffmpeg burn-in
        /// fills, and as the fill step for <see cref="DrawLabelRow"/>. This row's white fill at
        /// the panel top is what keeps pixel (0,0) white for RenderingTests' detection sample.
        /// </summary>
        private static void DrawRow(SpriteBatch spriteBatch, int panelWidth, int y)
        {
            var row = new Rectangle(PanelOrigin, y, panelWidth, RowHeight);
            spriteBatch.Draw(Game1.staminaRect, row, Color.White);
        }

        /// <summary>
        /// Draws one <see cref="RowHeight"/>-tall white row with bold black text inset by
        /// <see cref="TextInsetX"/> horizontally and vertically centered within the row (so the
        /// in-game rows share the same height and centering as the ffmpeg TIME row, giving an
        /// even vertical rhythm regardless of font). <paramref name="textHeight"/> is the
        /// pre-measured glyph height, used only to center the text.
        /// </summary>
        private static void DrawLabelRow(
            SpriteBatch spriteBatch,
            SpriteFont font,
            string text,
            float textHeight,
            int y,
            int panelWidth
        )
        {
            DrawRow(spriteBatch, panelWidth, y);
            int textY = y + (int)Math.Round((RowHeight - textHeight) / 2f);
            var textPosition = new Vector2(PanelOrigin + TextInsetX, textY);
            Utility.drawBoldText(spriteBatch, text, font, textPosition, Color.Black);
        }

        /// <summary>
        /// Draws a red border around the player's full sprite (16x24 at 4x scale = 64x96 on screen),
        /// not the collision bounding box (48x32 at the feet). Game1.player.getLocalPosition
        /// anchors to the sprite's bottom tile, so we shift up by one tile to align the box with
        /// the actual sprite top.
        /// </summary>
        private static void DrawPlayerHighlight(SpriteBatch spriteBatch)
        {
            var player = Game1.player;
            if (player == null || Game1.currentLocation == null)
                return;

            var sprite = player.Sprite;
            int zoom = Game1.pixelZoom;

            int width = sprite.SpriteWidth * zoom;
            int height = sprite.SpriteHeight * zoom;

            var pos = player.getLocalPosition(Game1.viewport);

            var rect = new Rectangle((int)pos.X, (int)pos.Y - Game1.tileSize, width, height);

            DrawRectangleOutline(spriteBatch, Game1.staminaRect, rect, BorderWidth, Color.Red);
        }

        ///<summary>
        /// Renders a rectangle outline using a 1x1 texture stretched into four edges.
        /// Used as a workaround since the game lacks primitive line/shape drawing.
        ///</summary>
        private static void DrawRectangleOutline(
            SpriteBatch spriteBatch,
            Texture2D texture,
            Rectangle rect,
            int thickness,
            Color color
        )
        {
            // Top
            spriteBatch.Draw(
                texture,
                new Rectangle(rect.Left, rect.Top, rect.Width, thickness),
                color
            );

            // Bottom
            spriteBatch.Draw(
                texture,
                new Rectangle(rect.Left, rect.Bottom - thickness, rect.Width, thickness),
                color
            );

            // Left
            spriteBatch.Draw(
                texture,
                new Rectangle(rect.Left, rect.Top, thickness, rect.Height),
                color
            );

            // Right
            spriteBatch.Draw(
                texture,
                new Rectangle(rect.Right - thickness, rect.Top, thickness, rect.Height),
                color
            );
        }
    }
}
