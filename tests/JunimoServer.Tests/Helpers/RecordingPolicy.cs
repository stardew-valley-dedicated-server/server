namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Video recording mode for tests.
/// Controlled by SDVD_TEST_RECORDING environment variable.
/// </summary>
public enum TestRecordingMode
{
    None,
    Failure,
    All,
}

/// <summary>
/// Static configuration for video recording. Parsed once from environment variables.
/// </summary>
/// <remarks>
/// One fps knob per container type. <c>SERVER_FPS</c> / <c>CLIENT_FPS</c> drive both
/// the in-container draw cap (<c>FpsThrottle.ShouldDraw</c>) AND the recorder's
/// ffmpeg sample rate. A value of 0 (or unset) disables rendering and recording
/// for that container type. There is no separate recorder-fps knob — sampling
/// X11 faster than the framebuffer updates is wasted work.
/// </remarks>
public static class RecordingPolicy
{
    public static TestRecordingMode Mode { get; } = ParseMode();
    public static int ServerFps { get; } = ParseFps("SERVER_FPS");
    public static int ClientFps { get; } = ParseFps("CLIENT_FPS");
    public static bool RecordServer { get; } = ParseBool("SDVD_TEST_RECORDING_SERVER", true);
    public static bool RecordClient { get; } = ParseBool("SDVD_TEST_RECORDING_CLIENT", true);
    public static int SegmentTime { get; } = ParseSegmentTime();

    // The single render-and-capture resolution. DISPLAY_WIDTH/HEIGHT set the X
    // display size the game renders into (injected into both test containers), and
    // the recorder's x11grab captures at the same dimensions — record == render,
    // so lowering these shrinks both rendering and encoding CPU. Rounded down to
    // even because the libx264 yuv420p path requires even width/height.
    public static int Width { get; } = ParseEvenDimension("DISPLAY_WIDTH", 1280);
    public static int Height { get; } = ParseEvenDimension("DISPLAY_HEIGHT", 720);
    public static bool IsEnabled => Mode != TestRecordingMode.None;

    // Per-type recording gate: a container type only records when recording is
    // globally enabled, the type is opted in, AND its draws are not disabled.
    // Recording a container that doesn't draw produces black/stale frames.
    public static bool RecordServerEnabled => IsEnabled && RecordServer && ServerFps > 0;
    public static bool RecordClientEnabled => IsEnabled && RecordClient && ClientFps > 0;

    private static TestRecordingMode ParseMode()
    {
        var value = Environment.GetEnvironmentVariable("SDVD_TEST_RECORDING");
        return value?.ToLowerInvariant() switch
        {
            "none" or "off" or "false" or "0" => TestRecordingMode.None,
            "failure" => TestRecordingMode.Failure,
            "all" or "true" or "1" => TestRecordingMode.All,
            _ => TestRecordingMode.None,
        };
    }

    private static int ParseFps(string key)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return int.TryParse(value, out var fps) && fps >= 0 ? fps : 0;
    }

    private static int ParseSegmentTime()
    {
        var value = Environment.GetEnvironmentVariable("SDVD_TEST_RECORDING_SEGMENT_TIME");
        return int.TryParse(value, out var t) && t >= 1 ? t : 1;
    }

    /// <summary>
    /// Parses a positive display dimension from <paramref name="key"/>, falling back to
    /// <paramref name="defaultValue"/> when unset or non-positive, then rounds down to the
    /// nearest even number (libx264's yuv420p path rejects odd width/height).
    /// </summary>
    private static int ParseEvenDimension(string key, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        var parsed = int.TryParse(value, out var result) && result > 0 ? result : defaultValue;
        return parsed & ~1;
    }

    private static bool ParseBool(string key, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(key);
        return value?.ToLowerInvariant() switch
        {
            "true" or "1" or "yes" => true,
            "false" or "0" or "no" => false,
            _ => defaultValue,
        };
    }
}
