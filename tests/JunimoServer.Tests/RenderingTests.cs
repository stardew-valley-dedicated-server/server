using JunimoServer.Tests.Clients;
using JunimoServer.Tests.Fixtures;
using JunimoServer.Tests.Helpers;
using Xunit;
using static JunimoServer.Tests.Helpers.TestTimings;

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
public class RenderingTests : IDisposable
{
    private readonly IntegrationTestFixture _fixture;
    private readonly ServerApiClient _serverApi;

    public RenderingTests(IntegrationTestFixture fixture)
    {
        _fixture = fixture;
        _serverApi = new ServerApiClient(_fixture.ServerBaseUrl);
    }

    [Fact]
    public async Task ServerApi_GetRendering_ShouldReturnStatus()
    {
        var status = await _serverApi.GetRendering();

        Assert.NotNull(status);
        // Server starts with DISABLE_RENDERING=true, so rendering should be off
        Assert.False(status.Enabled, "Rendering should be disabled initially (DISABLE_RENDERING=true)");
    }

    [Fact]
    public async Task ServerApi_SetRendering_EnableAndDisable_ShouldToggle()
    {
        // Initial state: rendering disabled
        var initialStatus = await _serverApi.GetRendering();
        Assert.NotNull(initialStatus);
        Assert.False(initialStatus.Enabled);

        // Enable rendering
        var enableResponse = await _serverApi.SetRendering(true);
        Assert.NotNull(enableResponse);
        Assert.True(enableResponse.Success, enableResponse.Error ?? "Enable failed");
        Assert.True(enableResponse.Enabled);

        // Verify GET reflects the change
        var enabledStatus = await _serverApi.GetRendering();
        Assert.NotNull(enabledStatus);
        Assert.True(enabledStatus.Enabled);

        // Disable rendering
        var disableResponse = await _serverApi.SetRendering(false);
        Assert.NotNull(disableResponse);
        Assert.True(disableResponse.Success, disableResponse.Error ?? "Disable failed");
        Assert.False(disableResponse.Enabled);

        // Verify GET reflects the change
        var disabledStatus = await _serverApi.GetRendering();
        Assert.NotNull(disabledStatus);
        Assert.False(disabledStatus.Enabled);
    }

    [Fact]
    public async Task ServerApi_SetRendering_WithoutParam_ShouldReturnError()
    {
        var response = await _serverApi.PostRenderingRaw();

        Assert.NotNull(response);
        Assert.False(response.Success);
        Assert.NotNull(response.Error);
        Assert.Contains("enabled", response.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ServerApi_SetRendering_Enable_ScreenShouldHaveContent()
    {
        // Skip if no container available (local dev mode)
        if (_fixture.ServerContainer == null)
        {
            Assert.Fail("Screenshot tests require a server container (not available in local dev mode)");
            return;
        }

        // Step 1: Ensure rendering is OFF, capture a "disabled" screenshot
        await _serverApi.SetRendering(false);
        await Task.Delay(RenderingDisableDelayMs); // Wait for frames to stop

        var disabledScreenshot = await VncScreenshotHelper.CaptureScreenshot(_fixture.ServerContainer);
        await VncScreenshotHelper.SaveScreenshot(disabledScreenshot,
            nameof(RenderingTests), nameof(ServerApi_SetRendering_Enable_ScreenShouldHaveContent),
            "01_rendering-disabled");
        var (disabledAvgBrightness, disabledMaxBrightness) = VncScreenshotHelper.SampleCenterBrightness(disabledScreenshot);
        disabledScreenshot.Dispose();

        Console.WriteLine($"[Test] Disabled: avg={disabledAvgBrightness:F1}, max={disabledMaxBrightness}");

        // Center of screen should be dark when rendering is disabled
        Assert.True(disabledAvgBrightness < 10,
            $"Center pixels should be near-black when rendering is disabled, but average brightness was {disabledAvgBrightness:F1}");

        // Step 2: Enable rendering, capture an "enabled" screenshot
        await _serverApi.SetRendering(true);
        await Task.Delay(RenderingEnableDelayMs); // Wait for game frames to render

        var enabledScreenshot = await VncScreenshotHelper.CaptureScreenshot(_fixture.ServerContainer);
        await VncScreenshotHelper.SaveScreenshot(enabledScreenshot,
            nameof(RenderingTests), nameof(ServerApi_SetRendering_Enable_ScreenShouldHaveContent),
            "02_rendering-enabled");
        var (enabledAvgBrightness, enabledMaxBrightness) = VncScreenshotHelper.SampleCenterBrightness(enabledScreenshot);
        enabledScreenshot.Dispose();

        Console.WriteLine($"[Test] Enabled: avg={enabledAvgBrightness:F1}, max={enabledMaxBrightness}");

        // Center of screen should have visible game content when rendering is enabled
        Assert.True(enabledMaxBrightness > 20,
            $"At least some center pixels should have visible content when rendering is enabled, but max brightness was {enabledMaxBrightness}");

        // The enabled screenshot should be meaningfully brighter than the disabled one
        Assert.True(enabledAvgBrightness > disabledAvgBrightness + 5,
            $"Enabled screenshot (avg={enabledAvgBrightness:F1}) should be brighter than disabled (avg={disabledAvgBrightness:F1})");

        // Step 3: Disable rendering again and verify it goes dark
        await _serverApi.SetRendering(false);
        await Task.Delay(RenderingDisableDelayMs);

        var reDisabledScreenshot = await VncScreenshotHelper.CaptureScreenshot(_fixture.ServerContainer);
        await VncScreenshotHelper.SaveScreenshot(reDisabledScreenshot,
            nameof(RenderingTests), nameof(ServerApi_SetRendering_Enable_ScreenShouldHaveContent),
            "03_rendering-re-disabled");
        var (reDisabledAvgBrightness, _) = VncScreenshotHelper.SampleCenterBrightness(reDisabledScreenshot);
        reDisabledScreenshot.Dispose();

        Console.WriteLine($"[Test] Re-disabled: avg={reDisabledAvgBrightness:F1}");

        Assert.True(reDisabledAvgBrightness < 10,
            $"Center pixels should be near-black after re-disabling rendering, but average brightness was {reDisabledAvgBrightness:F1}");
    }

    public void Dispose()
    {
        // Reset rendering to disabled (initial state) to prevent test pollution.
        // A test that enables rendering and then fails would leave it enabled,
        // causing subsequent tests to see the wrong initial state.
        try { _serverApi.SetRendering(false).GetAwaiter().GetResult(); } catch { }
        _serverApi.Dispose();
    }
}
