using DotNet.Testcontainers.Containers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Captures screenshots from the server container's X display using scrot via docker exec.
/// Both VNC and scrot observe the same X display (:0), so the captured image matches
/// what a user would see through the VNC web viewer.
/// </summary>
public static class VncScreenshotHelper
{
    private const string ScreenshotPath = "/tmp/screen.png";

    /// <summary>
    /// Captures a screenshot from the container's X display and returns it as an ImageSharp image.
    /// </summary>
    public static async Task<Image<Rgba32>> CaptureScreenshot(IContainer container)
    {
        // Capture X display to PNG using scrot
        var captureResult = await container.ExecAsync(new[]
        {
            "scrot", "--overwrite", "--pointer", "--multidisp", ScreenshotPath
        });

        if (captureResult.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"scrot failed (exit {captureResult.ExitCode}): {captureResult.Stderr}");
        }

        // Base64-encode the PNG for text transport via ExecAsync
        var base64Result = await container.ExecAsync(new[]
        {
            "sh", "-c", $"base64 -w0 {ScreenshotPath}"
        });

        if (base64Result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"base64 encoding failed (exit {base64Result.ExitCode}): {base64Result.Stderr}");
        }

        var pngBytes = Convert.FromBase64String(base64Result.Stdout.Trim());
        return Image.Load<Rgba32>(pngBytes);
    }

    /// <summary>
    /// Saves a screenshot to the test artifacts directory, organized by test class and method.
    /// </summary>
    public static async Task SaveScreenshot(
        Image<Rgba32> image, string testClass, string testMethod, string label)
    {
        var dir = TestArtifacts.GetScreenshotDir(testClass, testMethod);
        var path = Path.Combine(dir, $"{label}.png");
        await image.SaveAsPngAsync(path);
        Console.WriteLine($"[Screenshot] Saved: {Path.GetRelativePath(TestArtifacts.OutputDir, path)}");
    }

    /// <summary>
    /// Samples pixel brightness from the center region of the screen,
    /// avoiding the polybar at the top.
    /// Returns the average brightness (0-255) and maximum brightness across sampled points.
    /// </summary>
    public static (double averageBrightness, int maxBrightness) SampleCenterBrightness(Image<Rgba32> image)
    {
        // Sample from the center 50% of the screen, avoiding polybar at top (~40px)
        var startX = image.Width / 4;
        var endX = image.Width * 3 / 4;
        var startY = Math.Max(image.Height / 4, 50); // Skip polybar area
        var endY = image.Height * 3 / 4;

        var samplePoints = new List<(int x, int y)>();

        // Create a grid of ~10 sample points
        var stepX = (endX - startX) / 3;
        var stepY = (endY - startY) / 3;

        for (var x = startX; x <= endX; x += stepX)
        {
            for (var y = startY; y <= endY; y += stepY)
            {
                samplePoints.Add((x, y));
            }
        }

        var totalBrightness = 0.0;
        var maxBrightness = 0;

        foreach (var (x, y) in samplePoints)
        {
            var pixel = image[x, y];
            var brightness = (pixel.R + pixel.G + pixel.B) / 3;
            totalBrightness += brightness;
            if (brightness > maxBrightness)
                maxBrightness = brightness;
        }

        var averageBrightness = totalBrightness / samplePoints.Count;
        return (averageBrightness, maxBrightness);
    }
}
