using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Renders the 1200×630 link-preview card (og:image) for a finished run: a dark
/// gradient with the Junimo logo, pass/fail counts, branch, and duration,
/// following the shared <see cref="OgCard"/> design spec so it reads as a sibling
/// of the static docs banner. Text-only (no emoji glyphs — the bundled Inter font
/// has none). Status color lives only in the top accent line; the headline is
/// always white so the count, not the color, carries the result.
/// </summary>
public static class OgImageGenerator
{
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
        // Status drives only the top accent line (and the Discord left bar via
        // theme-color); the headline text stays white.
        var accent =
            summary.Status == "aborted" ? OgCard.Grey
            : summary.Failed > 0 ? OgCard.Fail
            : OgCard.Pass;

        using var image = new Image<Rgba32>(
            OgCard.Width,
            OgCard.Height,
            OgCard.Base.ToPixel<Rgba32>()
        );

        image.Mutate(ctx =>
        {
            OgCard.DrawBackground(ctx, accent);
            OgCard.DrawAccentLine(ctx, accent);
            OgCard.DrawLogo(ctx, 72, 64, 96, OgCard.Junimo);

            var wordmark = Family.CreateFont(34, FontStyle.Regular);
            var headline = Family.CreateFont(96, FontStyle.Regular);
            var sub = Family.CreateFont(40, FontStyle.Regular);
            var meta = Family.CreateFont(34, FontStyle.Regular);

            ctx.DrawText("JunimoServer E2E Report", wordmark, OgCard.Muted, new PointF(188, 96));

            var headlineText =
                summary.Status == "aborted"
                    ? "Run aborted"
                    : $"{summary.Passed} passed · {summary.Failed} failed";
            ctx.DrawText(headlineText, headline, OgCard.Text, new PointF(72, 250));

            var counts = new List<string> { $"{summary.TotalTests} total" };
            if (summary.Skipped > 0)
            {
                counts.Add($"{summary.Skipped} skipped");
            }

            if (summary.Canceled > 0)
            {
                counts.Add($"{summary.Canceled} canceled");
            }

            if (summary.DurationMs is { } ms)
            {
                counts.Add(FormatDuration(ms));
            }

            ctx.DrawText(string.Join("  ·  ", counts), sub, OgCard.Muted, new PointF(72, 380));

            // Branch only (no sha — not actionable on a social card), truncated to
            // match the description's branch suffix via the shared ReportGenerator.
            var branch = summary.GitBranch is { } b ? ReportGenerator.Truncate(b) : "unknown";
            ctx.DrawText(branch, meta, OgCard.Text, new PointF(72, OgCard.Height - 90));
        });

        using var png = new MemoryStream();
        image.SaveAsPng(png);
        return png.ToArray();
    }

    private static string FormatDuration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(ms);
        if (t.TotalHours >= 1)
        {
            return $"{(int)t.TotalHours}h {t.Minutes}m";
        }

        if (t.TotalMinutes >= 1)
        {
            return $"{t.Minutes}m {t.Seconds}s";
        }

        return $"{t.Seconds}s";
    }
}
