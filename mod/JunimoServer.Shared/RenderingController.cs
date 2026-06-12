using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using xTile.Display;

namespace JunimoServer.Shared;

/// <summary>
/// Single source of truth for runtime render-rate control, shared by the
/// server mod and the test client. 0 fps = rendering disabled
/// (NullDisplayDevice installed, draws suppressed, "Rendering Disabled"
/// notice painted once to the VNC framebuffer). N &gt; 0 = draws capped at N
/// fps via FpsThrottle. Instantiated once per side; both sides watch their
/// own VNC display during debugging, so both get the notice.
/// </summary>
public class RenderingController
{
    private readonly IMonitor _monitor;
    private readonly string _side; // "Server" / "Client" — log prefix only
    private int _currentFps; // 0 = disabled, N > 0 = throttled at N
    private bool _shouldDrawFrame = true;
    private bool _renderDisabledNoticeNeeded;
    private IDisplayDevice? _originalDisplayDevice;
    private IDisplayDevice? _nullDisplayDevice;

    public RenderingController(IMonitor monitor, string side)
    {
        _monitor = monitor;
        _side = side;
    }

    /// <summary>Current render rate: 0 = disabled, N &gt; 0 = throttled at N fps.</summary>
    public int CurrentFps => _currentFps;

    /// <summary>
    /// Captures the real display device so SetFps(N&gt;0) can restore it.
    /// Call from OnGameLaunched, after Game1.mapDisplayDevice is set.
    /// </summary>
    public void SaveOriginalDisplayDevice(IDisplayDevice device)
    {
        _originalDisplayDevice = device;
        _nullDisplayDevice = new NullDisplayDevice();
    }

    /// <summary>
    /// Sets the render rate. 0 installs NullDisplayDevice, queues the notice,
    /// suppresses draws. N &gt; 0 restores the real device and throttles at N.
    /// </summary>
    public void SetFps(int fps)
    {
        if (fps < 0)
        {
            fps = 0;
        }

        var previous = _currentFps;
        _currentFps = fps;

        if (fps == 0)
        {
            _nullDisplayDevice ??= new NullDisplayDevice();
            Game1.mapDisplayDevice = _nullDisplayDevice;
            _renderDisabledNoticeNeeded = true;
            _shouldDrawFrame = false;
            _monitor.Log($"{_side} rendering disabled (fps {previous} → 0)", LogLevel.Info);
        }
        else
        {
            if (_originalDisplayDevice != null)
            {
                Game1.mapDisplayDevice = _originalDisplayDevice;
            }

            _renderDisabledNoticeNeeded = false;
            _shouldDrawFrame = true;
            _monitor.Log($"{_side} rendering enabled at {fps} fps (was {previous})", LogLevel.Info);
        }
    }

    // Save-window helpers — only the server uses these (OnDayEnding/OnDayStarted),
    // but they live here so the one ShouldBeginDraw body below is identical for both.
    public void EnableDrawing() => _shouldDrawFrame = true;

    public void DisableDrawing() => _shouldDrawFrame = false;

    /// <summary>
    /// Prefix body for Game.BeginDraw. Returns false (and the caller
    /// SuppressDraw()s) to skip the frame, true to let it draw. Preserves the
    /// two passthroughs: the disabled-notice frame and the day-end save window.
    /// </summary>
    public bool ShouldBeginDraw()
    {
        if (!_shouldDrawFrame)
        {
            if (_renderDisabledNoticeNeeded)
            {
                return true; // PASSTHROUGH 1 — notice frame
            }

            if (Game1.showingEndOfNightStuff)
            {
                return true; // PASSTHROUGH 2 — save window
            }

            return false;
        }
        if (_currentFps == 0)
        {
            return true; // save forced a draw; skip throttle
        }

        return FpsThrottle.ShouldDraw(_currentFps); // _currentFps >= 1 here
    }

    /// <summary>
    /// Prefix body for Game.Draw(GameTime). Paints the "Rendering Disabled"
    /// notice once when queued, then lets normal draws through. Returns false
    /// to skip the game's Draw (the notice frame), true otherwise.
    /// </summary>
    public bool ShouldGameDraw(Game game)
    {
        if (!_renderDisabledNoticeNeeded)
        {
            return true;
        }

        _renderDisabledNoticeNeeded = false;
        try
        {
            var gd = game.GraphicsDevice;
            gd.Clear(Color.Black);
            var sb = Game1.spriteBatch;
            var font = Game1.smallFont;
            if (sb != null && font != null)
            {
                sb.Begin();
                var text = "Rendering Disabled";
                var size = font.MeasureString(text);
                sb.DrawString(
                    font,
                    text,
                    new Vector2(
                        (gd.Viewport.Width - size.X) / 2f,
                        (gd.Viewport.Height - size.Y) / 2f
                    ),
                    Color.Gray
                );
                sb.End();
            }
        }
        catch
        { /* notice paint is best-effort */
        }
        return false;
    }
}
