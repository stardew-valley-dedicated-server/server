using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E tests for runtime render-rate control.
/// Verifies both the API responses and the actual visual output
/// by capturing screenshots via the server's /screenshot API endpoint
/// (which reads the game's backbuffer directly).
///
/// Visual detection uses the test overlay (white rectangle at top-left,
/// drawn by TestOverlay.Draw_Postfix when TEST_FAIL_FAST=true). This overlay is
/// tied to rendering: it only appears when drawing is active. Unlike
/// game-world brightness, it is immune to time-of-day, day transitions, or
/// paused state.
///
/// Each test sets fps to 0 in InitializeAsync to establish a known "disabled"
/// baseline, then DisposeAsync restores a positive rate so other tests on the
/// shared server are unaffected.
///
/// API-only. Never calls GetClientAsync().
/// </summary>
[TestServer(Isolation = IsolationMode.SharedAssembly, Clients = 0, Exclusive = true)]
public class RenderingTests : TestBase
{
    /// <summary>
    /// The positive render rate tests set to verify the "enabled" state. The exact
    /// value is set explicitly via the API, so it doesn't depend on the server's
    /// boot SERVER_FPS — tests assert GET echoes back exactly this.
    /// </summary>
    private const int EnabledFps = 15;

    /// <summary>
    /// Brightness threshold for the test overlay region.
    /// The overlay draws a white (255) rectangle anchored at the screen's top-left;
    /// when rendering is disabled, that region is black (0). A threshold of 200 is
    /// well above any dark-scene artifacts and well below the 255 white background.
    /// </summary>
    private const int TestOverlayBrightThreshold = 200;

    // Mirrors TestOverlay.PanelOrigin in mod/JunimoServer.Shared/TestOverlay.cs; keep in sync.
    // (0,0) lands in the reserved-strip white fill at the panel top — never on a text stroke.
    private const int PanelOrigin = 0;

    public RenderingTests() { }

    public override async ValueTask InitializeAsync()
    {
        await base.InitializeAsync();

        // Disable rendering (fps 0) to establish a known "off" baseline for each test.
        // This ensures a predictable starting state even if a previous test
        // failed mid-change and left rendering in an unknown state.
        try
        {
            await ServerApi.SetServerFps(0, TestContext.Current.CancellationToken);
            await Task.Delay(
                TestTimings.RenderingToggleDelay,
                TestContext.Current.CancellationToken
            );
        }
        catch (Exception ex)
        {
            LogWarning($"Failed to reset rendering state: {ex.Message}");
        }
    }

    [Fact]
    public async Task ServerApi_SetServerFps_EnableAndDisable_ShouldUpdateState()
    {
        // Initial state: rendering disabled (set by InitializeAsync)
        var initialStatus = await ServerApi.GetRendering(TestContext.Current.CancellationToken);
        Assert.NotNull(initialStatus);
        Assert.Equal(0, initialStatus.Fps);

        // Enable at a positive rate
        var enableResponse = await ServerApi.SetServerFps(
            EnabledFps,
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(enableResponse);
        Assert.True(enableResponse.Success, enableResponse.Error ?? "Enable failed");
        Assert.Equal(EnabledFps, enableResponse.Fps);
        Assert.Equal(0, enableResponse.PreviousFps);

        // Verify GET reflects the change
        var enabledStatus = await ServerApi.GetRendering(TestContext.Current.CancellationToken);
        Assert.NotNull(enabledStatus);
        Assert.Equal(EnabledFps, enabledStatus.Fps);

        // Disable again (fps 0)
        var disableResponse = await ServerApi.SetServerFps(
            0,
            TestContext.Current.CancellationToken
        );
        Assert.NotNull(disableResponse);
        Assert.True(disableResponse.Success, disableResponse.Error ?? "Disable failed");
        Assert.Equal(0, disableResponse.Fps);
        Assert.Equal(EnabledFps, disableResponse.PreviousFps);

        // Verify GET reflects the change
        var disabledStatus = await ServerApi.GetRendering(TestContext.Current.CancellationToken);
        Assert.NotNull(disabledStatus);
        Assert.Equal(0, disabledStatus.Fps);
    }

    [Fact]
    public async Task ServerApi_SetServerFps_Enable_ScreenShouldHaveContent()
    {
        // Detection strategy: the test overlay (white rectangle at top-left)
        // is drawn as a Game1.Draw postfix, so it only appears when rendering is
        // enabled and draw frames are not suppressed. This is immune to game-world
        // brightness (nighttime, day-transition fade, etc.).

        // Step 1: Ensure rendering is OFF, poll until the test overlay disappears.
        await ServerApi.SetServerFps(0, TestContext.Current.CancellationToken);

        int disabledBrightness = 0;
        var isDark = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Rendering_OverlayDark,
            async () =>
            {
                var (brightness, pngBytes) = await CaptureTestOverlayBrightnessAsync();
                disabledBrightness = brightness;
                Log($"Disabled poll: test overlay brightness={brightness}");

                if (brightness < TestOverlayBrightThreshold && pngBytes != null)
                {
                    await SaveScreenshotAsync(
                        pngBytes,
                        nameof(ServerApi_SetServerFps_Enable_ScreenShouldHaveContent),
                        "01_rendering-disabled"
                    );
                    return true;
                }
                return false;
            },
            TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(
            isDark,
            $"Test overlay should be absent when rendering is disabled, but brightness was {disabledBrightness}"
        );

        // Step 2: Enable rendering, poll until the test overlay appears.
        await ServerApi.SetServerFps(EnabledFps, TestContext.Current.CancellationToken);

        int enabledBrightness = 0;
        var overlayVisible = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Rendering_OverlayVisible,
            async () =>
            {
                var (brightness, pngBytes) = await CaptureTestOverlayBrightnessAsync();
                enabledBrightness = brightness;
                Log($"Enabled poll: test overlay brightness={brightness}");

                if (brightness >= TestOverlayBrightThreshold && pngBytes != null)
                {
                    await SaveScreenshotAsync(
                        pngBytes,
                        nameof(ServerApi_SetServerFps_Enable_ScreenShouldHaveContent),
                        "02_rendering-enabled"
                    );
                    return true;
                }
                return false;
            },
            TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(
            overlayVisible,
            $"Test overlay should be visible when rendering is enabled, but brightness was {enabledBrightness}"
        );

        // Step 3: Disable rendering again and poll until the test overlay disappears.
        await ServerApi.SetServerFps(0, TestContext.Current.CancellationToken);

        int reDisabledBrightness = 0;
        var wentDark = await PollingHelper.WaitUntilAsync(
            WaitName.Polling_Rendering_OverlayWentDarkAgain,
            async () =>
            {
                var (brightness, pngBytes) = await CaptureTestOverlayBrightnessAsync();
                reDisabledBrightness = brightness;
                Log($"Re-disabled poll: test overlay brightness={brightness}");

                if (brightness < TestOverlayBrightThreshold && pngBytes != null)
                {
                    await SaveScreenshotAsync(
                        pngBytes,
                        nameof(ServerApi_SetServerFps_Enable_ScreenShouldHaveContent),
                        "03_rendering-re-disabled"
                    );
                    return true;
                }
                return false;
            },
            TimeSpan.FromSeconds(5),
            pollInterval: TimeSpan.FromMilliseconds(500),
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.True(
            wentDark,
            $"Test overlay should disappear after re-disabling rendering, but brightness was {reDisabledBrightness}"
        );
    }

    public override async ValueTask DisposeAsync()
    {
        // Restore a positive render rate so other tests on the shared server are
        // unaffected by whatever state this test left.
        try
        {
            await ServerApi.SetServerFps(EnabledFps);
        }
        catch { }

        await base.DisposeAsync();
    }

    #region Helpers

    /// <summary>
    /// Captures a screenshot and measures brightness at the overlay's anchor pixel.
    /// The panel's reserved-strip white fill starts at (PanelOrigin, PanelOrigin), so that
    /// pixel sits inside the white strip — never on a text stroke. When rendering is enabled
    /// the pixel is white (255); when disabled the backbuffer is black (0).
    /// </summary>
    private async Task<(int brightness, byte[]? pngBytes)> CaptureTestOverlayBrightnessAsync()
    {
        var result = await ServerApi.GetScreenshot(TestContext.Current.CancellationToken);
        if (result?.Success != true || result.Base64Png == null)
            return (0, null);

        var bytes = Convert.FromBase64String(result.Base64Png);
        using var image = Image.Load<Rgba32>(bytes);
        var brightness = SampleTestOverlayBrightness(image);
        return (brightness, bytes);
    }

    /// <summary>
    /// Saves a screenshot to the test artifacts directory and emits an event
    /// so the screenshot appears in the test UI.
    /// </summary>
    private async Task SaveScreenshotAsync(byte[] pngBytes, string testMethod, string label)
    {
        var dir = TestArtifacts.GetTestScreenshotDir(nameof(RenderingTests), testMethod);
        var path = Path.Combine(dir, $"{label}.png");
        await File.WriteAllBytesAsync(path, pngBytes);

        var displayName =
            TestContext.Current?.Test?.TestDisplayName ?? $"{nameof(RenderingTests)}.{testMethod}";
        SetupEventBus.EmitScreenshot(
            nameof(RenderingTests),
            nameof(RenderingTests),
            displayName,
            path,
            "server"
        );
    }

    /// <summary>
    /// Samples brightness (0-255) at the overlay's anchor pixel — guaranteed to land
    /// in the reserved-strip white fill at the panel top, never on a text stroke.
    /// </summary>
    private static int SampleTestOverlayBrightness(Image<Rgba32> image)
    {
        if (PanelOrigin >= image.Width || PanelOrigin >= image.Height)
            return 0;
        var pixel = image[PanelOrigin, PanelOrigin];
        return (pixel.R + pixel.G + pixel.B) / 3;
    }

    #endregion
}
