namespace JunimoServer.Tests.Helpers;

/// <summary>
/// Tiered per-container stats collection, gated by the
/// <c>SDVD_TEST_STATS</c> environment variable.
///
/// <para>
/// The Web UI's live FPS/TPS/memory graphs read <c>instance_stats</c> events
/// emitted by <see cref="ContainerStatsCollector"/>. In CI / headless runs the
/// graphs are pure overhead — Docker stats streaming + per-container HTTP
/// <c>/stats</c> polls add ~4200 calls per run.
/// </para>
///
/// <para>
/// Empty <c>instance_stats</c> arrays render gracefully in the UI
/// (<c>tests/test-ui/src/utils/stats.ts</c> filters nulls).
/// </para>
/// </summary>
public enum TestStatsLevel
{
    /// <summary>No Docker stats stream and no HTTP /stats poll. UI graphs render empty.</summary>
    None = 0,

    /// <summary>Docker stats stream only — CPU / memory still graphed; no HTTP /stats fan-out.</summary>
    Docker = 1,

    /// <summary>Today's behavior — Docker stats stream + per-container /stats HTTP poll for FPS/TPS/etc.</summary>
    DockerAndGame = 2,
}

/// <summary>
/// Static accessor for the process-wide stats level. Reads
/// <c>SDVD_TEST_STATS</c> once at first access.
/// </summary>
public static class TestStats
{
    private static readonly Lazy<TestStatsLevel> _level = new(Resolve);

    public static TestStatsLevel Level => _level.Value;

    private static TestStatsLevel Resolve()
    {
        var raw = Environment.GetEnvironmentVariable("SDVD_TEST_STATS")?.Trim().ToLowerInvariant();
        return raw switch
        {
            null or "" or "docker+game" => TestStatsLevel.DockerAndGame,
            "docker" => TestStatsLevel.Docker,
            "none" => TestStatsLevel.None,
            _ => TestStatsLevel.DockerAndGame, // unknown values keep today's behavior
        };
    }
}
