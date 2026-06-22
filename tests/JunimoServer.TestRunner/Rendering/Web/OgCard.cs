using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace JunimoServer.TestRunner.Rendering.Web;

/// <summary>
/// Shared design language for both Open Graph (social link-preview) cards — the
/// dynamic test-results card (<see cref="OgImageGenerator"/>, rendered per E2E
/// run) and the static docs banner (committed PNG, mocked here by the preview
/// harness). They are siblings by following this one spec, not by sharing a
/// production generator: the test card stays dynamic C# because its numbers
/// change per run; the docs card ships as a hand-finished static image.
///
/// SPEC (the source of truth for "same look &amp; feel"):
///   - Canvas: 1200×630.
///   - Background: a radial dark gradient over <see cref="Base"/> (#0f1115), the
///     bright stop biased toward the card's accent color so the logo + headline
///     sit on the lighter region.
///   - Accent: a horizontal line along the TOP edge (not a left bar). Discord
///     already draws a vertical status bar on the left of the unfurl from the
///     page's theme-color meta; an in-image left bar would stack two parallel
///     lines on the same edge, so the in-image accent goes on top. The test
///     card's line is status-tinted (grey/red/green); the docs banner's is cyan.
///   - Logo: the Junimo wordmark mark (<c>logo-240.png</c>) top-left, composited
///     via <see cref="DrawLogo"/>.
///   - Type: Inter (bundled). Headline ~96, sub ~40, meta/wordmark ~34.
///   - Color tokens below; docs cyan (#63dbe4) is the docs theme-color.
/// </summary>
public static class OgCard
{
    public const int Width = 1200;
    public const int Height = 630;

    // Background + text tokens (shared by both cards).
    public static readonly Color Base = Color.ParseHex("0f1115");
    public static readonly Color Text = Color.ParseHex("e5e7eb");
    public static readonly Color Muted = Color.ParseHex("9ca3af");

    // Status tokens (test card).
    public static readonly Color Pass = Color.ParseHex("16a34a");
    public static readonly Color Fail = Color.ParseHex("dc2626");
    public static readonly Color Grey = Color.ParseHex("6b7280");

    // Brand tokens (docs banner + logo dot fallback).
    public static readonly Color Cyan = Color.ParseHex("63dbe4");
    public static readonly Color Junimo = Color.ParseHex("77ff6e");

    /// <summary>Top accent line thickness, in px.</summary>
    public const int AccentHeight = 8;

    /// <summary>
    /// Fills the canvas with the radial dark gradient. The bright stop sits near
    /// the top-left (behind the logo + headline) and fades to <see cref="Base"/>.
    /// <paramref name="tint"/> is the status/brand color the bright stop is biased
    /// toward (subtly — this is a near-black background).
    /// </summary>
    public static void DrawBackground(IImageProcessingContext ctx, Color tint)
    {
        // Bright stop = Base nudged ~10% toward the tint.
        var lift = Blend(Base, tint, 0.10f);

        SixLabors.ImageSharp.Drawing.IPath fullPath =
            new SixLabors.ImageSharp.Drawing.RectangularPolygon(0, 0, Width, Height);

        var brush = new RadialGradientBrush(
            new PointF(Width * 0.28f, Height * 0.34f),
            Height * 0.95f,
            GradientRepetitionMode.None,
            new ColorStop(0f, lift),
            new ColorStop(1f, Base)
        );
        ctx.Fill(brush, fullPath);
    }

    /// <summary>Draws the top-edge accent line in <paramref name="color"/>.</summary>
    public static void DrawAccentLine(IImageProcessingContext ctx, Color color)
    {
        ctx.Fill(color, new RectangleF(0, 0, Width, AccentHeight));
    }

    /// <summary>
    /// Rasterizes the brand mark from <c>logo.svg</c> at <paramref name="size"/> px
    /// square and composites it at (<paramref name="x"/>, <paramref name="y"/>).
    /// Falls back to a colored dot if the SVG is missing or unparseable, so the card
    /// still renders. The card consumes the SVG directly (via <see cref="LogoPicture"/>),
    /// so the vector is the single committed logo source — there is no PNG asset.
    /// </summary>
    public static void DrawLogo(
        IImageProcessingContext ctx,
        float x,
        float y,
        int size,
        Color dotFallback
    )
    {
        var logo = RasterizeLogo(size);
        if (logo == null)
        {
            var r = size / 2f;
            ctx.Fill(dotFallback, new SixLabors.ImageSharp.Drawing.EllipsePolygon(x + r, y + r, r));
            return;
        }

        ctx.DrawImage(logo, new Point((int)x, (int)y), 1f);
    }

    // The SVG's vector picture, parsed once. Null if the asset is missing or
    // unparseable — the card then renders with a dot fallback rather than throwing.
    private static readonly Lazy<SkiaSharp.SKPicture?> LogoPicture = new(LoadLogoPicture);

    // Rasterized logos cached per target size — DrawLogo is called with one size per
    // card, so this rasterizes the SVG at most once per distinct size across a run.
    private static readonly Dictionary<int, Image<Rgba32>?> RasterCache = new();

    private static SkiaSharp.SKPicture? LoadLogoPicture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "img", "logo.svg");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return new Svg.Skia.SKSvg().Load(path);
        }
        catch
        {
            return null;
        }
    }

    private static Image<Rgba32>? RasterizeLogo(int size)
    {
        if (RasterCache.TryGetValue(size, out var cached))
        {
            return cached;
        }

        var image = RasterizeLogoCore(size);
        RasterCache[size] = image;
        return image;
    }

    private static Image<Rgba32>? RasterizeLogoCore(int size)
    {
        if (LogoPicture.Value is not { } picture)
        {
            return null;
        }

        // Scale the SVG's intrinsic viewBox to fill a size×size transparent surface.
        var bounds = picture.CullRect;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return null;
        }

        var scale = size / Math.Max(bounds.Width, bounds.Height);

        var info = new SkiaSharp.SKImageInfo(
            size,
            size,
            SkiaSharp.SKColorType.Rgba8888,
            SkiaSharp.SKAlphaType.Unpremul
        );
        using var surface = SkiaSharp.SKSurface.Create(info);
        surface.Canvas.Clear(SkiaSharp.SKColors.Transparent);
        surface.Canvas.Scale(scale);
        surface.Canvas.DrawPicture(picture);
        surface.Canvas.Flush();

        using var snapshot = surface.Snapshot();
        using var pixels = snapshot.PeekPixels();

        // SKColorType.Rgba8888 (unpremultiplied) matches ImageSharp's Rgba32 byte
        // order, so the pixel span maps straight across with no channel swizzle.
        return Image.LoadPixelData<Rgba32>(pixels.GetPixelSpan(), size, size);
    }

    private static Color Blend(Color a, Color b, float t)
    {
        var pa = a.ToPixel<Rgba32>();
        var pb = b.ToPixel<Rgba32>();
        return Color.FromRgb(
            (byte)(pa.R + (pb.R - pa.R) * t),
            (byte)(pa.G + (pb.G - pa.G) * t),
            (byte)(pa.B + (pb.B - pa.B) * t)
        );
    }
}
