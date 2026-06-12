using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Renders the 1200×630 link-preview card (og:image) for a finished run: a
/// status-tinted background with pass/fail counts, branch @ sha, duration, and
/// the product wordmark. Text-only (no emoji glyphs — the bundled Inter font has
/// none); pass/fail are colored dots + words.
/// </summary>
public static class OgImageGenerator
{
    private const int Width = 1200;
    private const int Height = 630;

    private static readonly Color Bg = Color.ParseHex("0f1115");
    private static readonly Color Pass = Color.ParseHex("16a34a");
    private static readonly Color Fail = Color.ParseHex("dc2626");
    private static readonly Color Grey = Color.ParseHex("6b7280");
    private static readonly Color Text = Color.ParseHex("e5e7eb");
    private static readonly Color Muted = Color.ParseHex("9ca3af");
    private static readonly Color Junimo = Color.ParseHex("77ff6e");

    private static readonly FontFamily Family = LoadFont();

    private static FontFamily LoadFont()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "Inter-SemiBold.ttf");
        var collection = new FontCollection();
        return collection.Add(path);
    }

    /// <summary>
    /// Renders the card to PNG bytes. Throws on font/draw failure; the caller
    /// (ReportGenerator) treats failure as "skip the image, keep the text tags".
    /// </summary>
    public static byte[] Render(RunSummary summary)
    {
        var accent =
            summary.Status == "aborted" ? Grey
            : summary.Failed > 0 ? Fail
            : Pass;

        using var image = new Image<Rgba32>(Width, Height, Bg.ToPixel<Rgba32>());

        image.Mutate(ctx =>
        {
            // Left accent bar.
            ctx.Fill(accent, new RectangleF(0, 0, 16, Height));

            var wordmark = Family.CreateFont(34, FontStyle.Regular);
            var headline = Family.CreateFont(96, FontStyle.Regular);
            var sub = Family.CreateFont(40, FontStyle.Regular);
            var meta = Family.CreateFont(34, FontStyle.Regular);

            DrawDot(ctx, 72, 86, 14, Junimo);
            ctx.DrawText("SDVD E2E Report", wordmark, Muted, new PointF(98, 70));

            var headlineText =
                summary.Status == "aborted"
                    ? "Run aborted"
                    : $"{summary.Passed} passed · {summary.Failed} failed";
            ctx.DrawText(
                headlineText,
                headline,
                summary.Failed > 0 ? Fail : Text,
                new PointF(72, 220)
            );

            var counts = new List<string> { $"{summary.TotalTests} tests" };
            if (summary.Skipped > 0)
                counts.Add($"{summary.Skipped} skipped");
            if (summary.Canceled > 0)
                counts.Add($"{summary.Canceled} canceled");
            if (summary.DurationMs is { } ms)
                counts.Add(FormatDuration(ms));
            ctx.DrawText(string.Join("  ·  ", counts), sub, Muted, new PointF(72, 360));

            var branch = summary.GitBranch ?? "unknown";
            var sha = summary.GitSha is { Length: >= 7 } s ? s[..7] : summary.GitSha;
            var gitLine = sha != null ? $"{branch} @ {sha}" : branch;
            ctx.DrawText(gitLine, meta, Text, new PointF(72, Height - 90));
        });

        using var png = new MemoryStream();
        image.SaveAsPng(png);
        return png.ToArray();
    }

    private static void DrawDot(
        IImageProcessingContext ctx,
        float cx,
        float cy,
        float r,
        Color color
    )
    {
        var circle = new SixLabors.ImageSharp.Drawing.EllipsePolygon(cx, cy, r);
        ctx.Fill(color, circle);
    }

    private static string FormatDuration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes}m {t.Seconds}s";
        return $"{t.Seconds}s";
    }
}
