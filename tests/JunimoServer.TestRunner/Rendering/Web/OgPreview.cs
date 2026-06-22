using System.Net;
using System.Text;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Design-only preview harness for the two OG cards. Renders the dynamic test
/// card across every data state (via the production <see cref="OgImageGenerator"/>,
/// so what you see is byte-identical to a real run) plus the single static docs
/// banner, and wraps each in a Discord-style unfurl mockup — left theme-color
/// bar, og:site_name, og:title, og:description, then the image — so the whole
/// social card is reviewable, not just the picture. Title/description come from
/// the real producers (<see cref="ReportGenerator"/> for the test card) so the
/// mockup can't drift from what Discord/Twitter actually scrape.
///
/// Writes the PNGs + an index.html to a gitignored .output/og-preview/. Iterate by
/// editing the draw code and re-running `make og-preview`. Tooling only — it does
/// NOT make the production docs card C#-generated; the docs banner is exported
/// from here and committed as docs/public/og-image.png.
/// </summary>
public static class OgPreview
{
    private const string OutDir = ".output/og-preview";

    private static readonly FontFamily Family = LoadFont();

    private static FontFamily LoadFont()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "fonts", "Inter-SemiBold.ttf");
        return new FontCollection().Add(path);
    }

    /// <summary>One social card: the image plus the meta tags Discord renders around it.</summary>
    private readonly record struct Unfurl(
        string File,
        string SiteName,
        string Title,
        string Description,
        string ThemeColor
    );

    public static Task<int> RunAsync()
    {
        Directory.CreateDirectory(OutDir);

        var testCards = new List<Unfurl>();
        foreach (var (label, summary) in TestStates())
        {
            var file = $"test-{label}.png";
            File.WriteAllBytes(Path.Combine(OutDir, file), OgImageGenerator.Render(summary));
            testCards.Add(
                new Unfurl(
                    file,
                    "JunimoServer E2E Report",
                    ReportGenerator.BuildTitle(summary),
                    ReportGenerator.BuildDescription(summary),
                    TestThemeColor(summary)
                )
            );
        }

        // One site-wide docs banner. Export this as docs/public/og-image.png once
        // approved. Title/description mirror docs/.vitepress/config.ts (home page).
        File.WriteAllBytes(Path.Combine(OutDir, "docs-og-image.png"), RenderDocsBanner());
        var docsCard = new Unfurl(
            "docs-og-image.png",
            "JunimoServer",
            "Documentation",
            "JunimoServer is a Docker-based dedicated server for Stardew Valley, run as a SMAPI mod with cabin management, password protection, and an HTTP control API.",
            "#63dbe4"
        );

        File.WriteAllText(
            Path.Combine(OutDir, "index.html"),
            BuildContactSheet(testCards, docsCard)
        );

        Console.Error.WriteLine(
            $"og-preview: wrote {testCards.Count + 1} cards + index.html to {OutDir}/ — open {OutDir}/index.html"
        );
        return Task.FromResult(0);
    }

    // Mirrors ReportGenerator.BuildMetaTags' theme-color map (ReportGenerator.cs)
    // — the status hex Discord paints as the unfurl's left bar.
    private static string TestThemeColor(RunSummary s) =>
        s.Status == "aborted" ? "#6b7280"
        : s.Failed > 0 ? "#dc2626"
        : "#16a34a";

    private static IEnumerable<(string Label, RunSummary Summary)> TestStates()
    {
        // RunSummary(Status, TotalTests, Passed, Failed, Skipped, Canceled, DurationMs, GitBranch, GitSha)
        yield return (
            "all-pass",
            new RunSummary("finished", 96, 96, 0, 0, 0, 512_000, "master", "6ff8be6abc1234")
        );
        yield return (
            "failures",
            new RunSummary("finished", 96, 91, 5, 0, 0, 547_000, "master", "6ff8be6abc1234")
        );
        yield return (
            "skips-canceled",
            new RunSummary("finished", 96, 84, 2, 6, 4, 498_000, "feat/og-cards", "abcdef0123456")
        );
        yield return (
            "aborted",
            new RunSummary("aborted", 96, 40, 1, 0, 0, 180_000, "master", "6ff8be6abc1234")
        );
        yield return (
            "long-branch",
            new RunSummary(
                "finished",
                96,
                96,
                0,
                0,
                0,
                600_000,
                "feat/a-very-long-branch-name-that-wraps",
                "0123456789abcd"
            )
        );
        yield return (
            "missing-git",
            new RunSummary("finished", 12, 12, 0, 0, 0, 64_000, null, null)
        );
        yield return (
            "large-counts",
            new RunSummary("finished", 1280, 1271, 9, 0, 0, 7_320_000, "master", "6ff8be6abc1234")
        );
    }

    private static byte[] RenderDocsBanner()
    {
        using var image = new Image<Rgba32>(
            OgCard.Width,
            OgCard.Height,
            OgCard.Base.ToPixel<Rgba32>()
        );

        image.Mutate(ctx =>
        {
            OgCard.DrawBackground(ctx, OgCard.Cyan);
            OgCard.DrawAccentLine(ctx, OgCard.Cyan);
            OgCard.DrawLogo(ctx, 72, 64, 96, OgCard.Junimo);

            var wordmark = Family.CreateFont(34, FontStyle.Regular);
            var headline = Family.CreateFont(96, FontStyle.Regular);
            var sub = Family.CreateFont(40, FontStyle.Regular);

            // The card is about the docs site, so "Documentation" is the headline;
            // the product name + category sit in the eyebrow, the pitch in the sub.
            ctx.DrawText(
                "JunimoServer · Dedicated Server",
                wordmark,
                OgCard.Muted,
                new PointF(188, 96)
            );
            ctx.DrawText("Documentation", headline, OgCard.Text, new PointF(72, 250));
            ctx.DrawText(
                "A Docker-based Stardew Valley dedicated server",
                sub,
                OgCard.Cyan,
                new PointF(72, 380)
            );
        });

        using var png = new MemoryStream();
        image.SaveAsPng(png);
        return png.ToArray();
    }

    private static string BuildContactSheet(List<Unfurl> testCards, Unfurl docsCard)
    {
        var sb = new StringBuilder();
        sb.Append(
            """
            <!doctype html><meta charset=utf-8><title>OG card preview</title>
            <style>
              body{background:#313338;color:#dbdee1;font-family:system-ui,Segoe UI,sans-serif;margin:24px;max-width:1000px}
              h1{font-weight:700}h2{font-weight:600;margin:32px 0 4px;color:#f2f3f5}
              .note{color:#949ba4;font-size:13px;margin:0 0 16px}
              .cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(420px,1fr));gap:18px}
              /* Discord-style unfurl: rounded panel, colored left bar, meta then image. */
              .unfurl{background:#2b2d31;border-radius:8px;padding:10px 12px 12px 16px;position:relative;overflow:hidden}
              .unfurl::before{content:"";position:absolute;left:0;top:0;bottom:0;width:4px;background:var(--bar)}
              .site{color:#949ba4;font-size:12px;margin:2px 0 2px}
              .title{color:#00a8fc;font-weight:600;font-size:16px;line-height:1.3;margin:0 0 6px;word-break:break-word}
              .desc{color:#dbdee1;font-size:14px;line-height:1.4;margin:0 0 10px;word-break:break-word}
              .unfurl img{width:100%;max-width:400px;border-radius:8px;display:block}
              .label{color:#949ba4;font-size:12px;margin-top:6px}
            </style>
            <h1>OG card preview</h1>
            <p class=note>Mock of how Discord renders each link unfurl: left bar = theme-color, then og:site_name / og:title / og:description / og:image. Title &amp; description for the test card come from the real ReportGenerator.</p>
            """
        );

        sb.Append("<h2>Test results card</h2>");
        sb.Append("<p class=note>Dynamic per E2E run — every data state shown.</p>");
        sb.Append("<div class=cards>");
        foreach (var card in testCards)
        {
            AppendUnfurl(sb, card);
        }

        sb.Append("</div>");

        sb.Append("<h2>Docs card</h2>");
        sb.Append(
            "<p class=note>Single static banner — committed as docs/public/og-image.png.</p>"
        );
        sb.Append("<div class=cards>");
        AppendUnfurl(sb, docsCard);
        sb.Append("</div>");

        return sb.ToString();
    }

    private static void AppendUnfurl(StringBuilder sb, Unfurl c)
    {
        sb.Append($"<div class=unfurl style=\"--bar:{Enc(c.ThemeColor)}\">");
        sb.Append($"<div class=site>{Enc(c.SiteName)}</div>");
        sb.Append($"<div class=title>{Enc(c.Title)}</div>");
        sb.Append($"<div class=desc>{Enc(c.Description)}</div>");
        sb.Append($"<img src=\"{Enc(c.File)}\" alt=\"{Enc(c.Title)}\">");
        sb.Append($"<div class=label>{Enc(c.File)}</div>");
        sb.Append("</div>");
    }

    private static string Enc(string value) => WebUtility.HtmlEncode(value);
}
