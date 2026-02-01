using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using Xunit.Abstractions;

namespace JunimoServer.Tests;

/// <summary>
/// E2E tests for the rendering toggle feature.
/// Verifies both the API responses and the actual visual output
/// by capturing screenshots from the server's X display.
///
/// Tests run with DISABLE_RENDERING=true (set in IntegrationTestFixture),
/// so initial rendering state is OFF.
/// </summary>
[Collection("Integration")]
public class RenderingTests : IntegrationTestBase
{
    public RenderingTests(IntegrationTestFixture fixture, ITestOutputHelper output)
        : base(fixture, output)
    {
    }

    [Fact]
    public async Task ServerApi_GetRendering_ShouldReturnStatus()
    {
        var status = await ServerApi.GetRendering();

        Assert.NotNull(status);
        // Server starts with DISABLE_RENDERING=true, so rendering should be off
        Assert.False(status.Enabled, "Rendering should be disabled initially (DISABLE_RENDERING=true)");
    }

    [Fact]
    public async Task ServerApi_SetRendering_EnableAndDisable_ShouldToggle()
    {
        // Initial state: rendering disabled
        var initialStatus = await ServerApi.GetRendering();
        Assert.NotNull(initialStatus);
        Assert.False(initialStatus.Enabled);

        // Enable rendering
        var enableResponse = await ServerApi.SetRendering(true);
        Assert.NotNull(enableResponse);
        Assert.True(enableResponse.Success, enableResponse.Error ?? "Enable failed");
        Assert.True(enableResponse.Enabled);

        // Verify GET reflects the change
        var enabledStatus = await ServerApi.GetRendering();
        Assert.NotNull(enabledStatus);
        Assert.True(enabledStatus.Enabled);

        // Disable rendering
        var disableResponse = await ServerApi.SetRendering(false);
        Assert.NotNull(disableResponse);
        Assert.True(disableResponse.Success, disableResponse.Error ?? "Disable failed");
        Assert.False(disableResponse.Enabled);

        // Verify GET reflects the change
        var disabledStatus = await ServerApi.GetRendering();
        Assert.NotNull(disabledStatus);
        Assert.False(disabledStatus.Enabled);
    }

    [Fact]
    public async Task ServerApi_SetRendering_WithoutParam_ShouldReturnError()
    {
        var response = await ServerApi.PostRenderingRaw();

        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Contains("enabled", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerApi_SetRendering_Enable_ScreenShouldHaveContent()
    {
        // Skip if no container available (local dev mode)
        if (Fixture.ServerContainer == null)
        {
            Assert.Fail("Screenshot tests require a server container (not available in local dev mode)");
            return;
        }

        // Step 1: Ensure rendering is OFF, capture a "disabled" screenshot
        await ServerApi.SetRendering(false);
        await Task.Delay(TestTimings.RenderingToggleDelay); // Wait for frames to stop

        var disabledScreenshot = await VncScreenshotHelper.CaptureScreenshot(Fixture.ServerContainer);
        await VncScreenshotHelper.SaveScreenshot(disabledScreenshot,
            nameof(RenderingTests), nameof(ServerApi_SetRendering_Enable_ScreenShouldHaveContent),
            "01_rendering-disabled");
        var (disabledAvgBrightness, disabledMaxBrightness) = VncScreenshotHelper.SampleCenterBrightness(disabledScreenshot);
        disabledScreenshot.Dispose();

        Log($"Disabled: avg={disabledAvgBrightness:F1}, max={disabledMaxBrightness}");

        // Center of screen should be dark when rendering is disabled
        Assert.True(disabledAvgBrightness < 10,
            $"Center pixels should be near-black when rendering is disabled, but average brightness was {disabledAvgBrightness:F1}");

        // Step 2: Enable rendering, capture an "enabled" screenshot
        await ServerApi.SetRendering(true);
        await Task.Delay(TestTimings.RenderingToggleDelay); // Wait for game frames to render

        var enabledScreenshot = await VncScreenshotHelper.CaptureScreenshot(Fixture.ServerContainer);
        await VncScreenshotHelper.SaveScreenshot(enabledScreenshot,
            nameof(RenderingTests), nameof(ServerApi_SetRendering_Enable_ScreenShouldHaveContent),
            "02_rendering-enabled");
        var (enabledAvgBrightness, enabledMaxBrightness) = VncScreenshotHelper.SampleCenterBrightness(enabledScreenshot);
        enabledScreenshot.Dispose();

        Log($"Enabled: avg={enabledAvgBrightness:F1}, max={enabledMaxBrightness}");

        // Center of screen should have visible game content when rendering is enabled
        Assert.True(enabledMaxBrightness > 20,
            $"At least some center pixels should have visible content when rendering is enabled, but max brightness was {enabledMaxBrightness}");

        // The enabled screenshot should be meaningfully brighter than the disabled one
        Assert.True(enabledAvgBrightness > disabledAvgBrightness + 5,
            $"Enabled screenshot (avg={enabledAvgBrightness:F1}) should be brighter than disabled (avg={disabledAvgBrightness:F1})");

        // Step 3: Disable rendering again and verify it goes dark
        await ServerApi.SetRendering(false);
        await Task.Delay(TestTimings.RenderingToggleDelay);

        var reDisabledScreenshot = await VncScreenshotHelper.CaptureScreenshot(Fixture.ServerContainer);
        await VncScreenshotHelper.SaveScreenshot(reDisabledScreenshot,
            nameof(RenderingTests), nameof(ServerApi_SetRendering_Enable_ScreenShouldHaveContent),
            "03_rendering-re-disabled");
        var (reDisabledAvgBrightness, _) = VncScreenshotHelper.SampleCenterBrightness(reDisabledScreenshot);
        reDisabledScreenshot.Dispose();

        Log($"Re-disabled: avg={reDisabledAvgBrightness:F1}");

        Assert.True(reDisabledAvgBrightness < 10,
            $"Center pixels should be near-black after re-disabling rendering, but average brightness was {reDisabledAvgBrightness:F1}");
    }

    public override async Task DisposeAsync()
    {
        // Reset rendering to disabled (initial state) to prevent test pollution.
        // A test that enables rendering and then fails would leave it enabled,
        // causing subsequent tests to see the wrong initial state.
        try { await ServerApi.SetRendering(false); } catch { }

        await base.DisposeAsync();
    }
}
