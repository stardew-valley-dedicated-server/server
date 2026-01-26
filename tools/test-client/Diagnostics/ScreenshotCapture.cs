using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace JunimoTestClient.Diagnostics;

/// <summary>
/// Captures screenshots of the game.
/// </summary>
public class ScreenshotCapture
{
    private readonly IMonitor _monitor;

    // Queued screenshot request
    private TaskCompletionSource<ScreenshotResult>? _pendingCapture;
    private string? _pendingFilename;

    public ScreenshotCapture(IMonitor monitor)
    {
        _monitor = monitor;
    }

    /// <summary>
    /// Queue a screenshot capture (will be taken on next render).
    /// </summary>
    public Task<ScreenshotResult> CaptureAsync(string? filename = null)
    {
        if (_pendingCapture != null)
        {
            return Task.FromResult(new ScreenshotResult
            {
                Success = false,
                Error = "Screenshot already pending"
            });
        }

        _pendingCapture = new TaskCompletionSource<ScreenshotResult>();
        _pendingFilename = filename;

        return _pendingCapture.Task;
    }

    /// <summary>
    /// Called from render event to actually capture the screenshot.
    /// </summary>
    public void OnPostRender()
    {
        if (_pendingCapture == null) return;

        var tcs = _pendingCapture;
        _pendingCapture = null;

        try
        {
            var result = CaptureNow(_pendingFilename);
            tcs.SetResult(result);
        }
        catch (Exception ex)
        {
            tcs.SetResult(new ScreenshotResult
            {
                Success = false,
                Error = ex.Message
            });
        }
    }

    /// <summary>
    /// Capture screenshot immediately (must be called from render thread).
    /// </summary>
    private ScreenshotResult CaptureNow(string? filename)
    {
        var device = Game1.graphics.GraphicsDevice;
        var pp = device.PresentationParameters;
        var width = pp.BackBufferWidth;
        var height = pp.BackBufferHeight;

        // Generate filename if not provided
        if (string.IsNullOrEmpty(filename))
        {
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
            filename = $"screenshot_{timestamp}.png";
        }

        if (!filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            filename += ".png";
        }

        // Ensure screenshots directory exists
        var screenshotDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "StardewValley", "Screenshots", "TestClient"
        );
        Directory.CreateDirectory(screenshotDir);

        var filePath = Path.Combine(screenshotDir, filename);

        // Capture the backbuffer
        var backBuffer = new Color[width * height];
        device.GetBackBufferData(backBuffer);

        // Create texture and save
        using var texture = new Texture2D(device, width, height, false, SurfaceFormat.Color);
        texture.SetData(backBuffer);

        using var stream = File.Create(filePath);
        texture.SaveAsPng(stream, width, height);

        _monitor.Log($"Screenshot saved: {filePath}", LogLevel.Debug);

        return new ScreenshotResult
        {
            Success = true,
            FilePath = filePath,
            Filename = filename,
            Width = width,
            Height = height,
            SizeBytes = new FileInfo(filePath).Length
        };
    }

    /// <summary>
    /// Capture screenshot and return as base64 PNG (for API response).
    /// </summary>
    public ScreenshotResult CaptureToBase64()
    {
        try
        {
            var device = Game1.graphics.GraphicsDevice;
            var pp = device.PresentationParameters;
            var width = pp.BackBufferWidth;
            var height = pp.BackBufferHeight;

            var backBuffer = new Color[width * height];
            device.GetBackBufferData(backBuffer);

            using var texture = new Texture2D(device, width, height, false, SurfaceFormat.Color);
            texture.SetData(backBuffer);

            using var stream = new MemoryStream();
            texture.SaveAsPng(stream, width, height);
            var base64 = Convert.ToBase64String(stream.ToArray());

            return new ScreenshotResult
            {
                Success = true,
                Width = width,
                Height = height,
                SizeBytes = stream.Length,
                Base64Png = base64
            };
        }
        catch (Exception ex)
        {
            return new ScreenshotResult
            {
                Success = false,
                Error = ex.Message
            };
        }
    }
}

public class ScreenshotResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? FilePath { get; set; }
    public string? Filename { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public long SizeBytes { get; set; }
    public string? Base64Png { get; set; }
}
