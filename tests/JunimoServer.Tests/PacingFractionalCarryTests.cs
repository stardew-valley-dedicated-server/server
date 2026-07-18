using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Committed gate for the FRACTIONAL sub-step carry (<c>TpsAgnosticPacing.ExtraStepsThisTick</c>).
/// The rest of the suite runs at the .env.test <c>SERVER_TPS</c> (5 → TickScale 12, an integer), so
/// the carry's fractional path would otherwise be exercised by no committed test — a regression in the
/// floor/remainder math would ship silently. <c>ServerTps = 24</c> gives TickScale 60/24 = 2.5: the
/// carry must alternate 2/3 whole steps per tick, averaging exactly 2.5.
///
/// <para><b>Why the assertion is tick-denominated.</b> A wall-clock distance threshold cannot gate the
/// carry at a mild scale like 2.5× — even an unpatched build covers a "wall-clock-ish" distance at TPS
/// 24, and HTTP/scheduler jitter swamps the 20-vs-16 px/tick difference a carry regression produces.
/// Instead, two probe reads report <c>Game1.ticks</c> atomically with the projectile's
/// <c>travelDistance</c> (both read in one game-thread action), so px/tick = Δdistance/Δticks is exact
/// up to whole-step rounding at the window edges, regardless of when the HTTP reads land.
/// </para>
/// </summary>
// Clients = 1: one connected player unpauses the server so the world (and the probe projectile) ticks.
// Exclusive: mutates the shared host location's projectile collection, like PacingProbeTests.
// ServerTps = 24: provisions this class's own server (TPS is part of the pooling key).
[TestServer(Clients = 1, ServerTps = 24, Isolation = IsolationMode.SharedClass, Exclusive = true)]
public class PacingFractionalCarryTests : TestBase
{
    // 8 px per update step × 2.5 steps/tick (TickScale 60/24). A carry regression that truncates the
    // fraction (constant floor(2.5)−1 = 1 extra step) measures 16; no sub-steps measures 8. The ±1.5
    // band separates those cleanly while absorbing edge rounding (≤ ±1 whole step over ~48 ticks
    // ≈ ±0.35 px/tick).
    private const float ExpectedPxPerTick = 20f;
    private const float PxPerTickTolerance = 1.5f;

    [Fact]
    public async Task Projectile_PxPerTick_MatchesFractionalTickScale()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureConnectedAsync("ProbeCarry", ct: ct);

        var spawn = await ServerApi.SpawnPacingProbe("projectile", ct);
        Assert.NotNull(spawn);
        Assert.True(spawn.Success, $"Projectile probe spawn failed: {spawn.Error}");
        Assert.True(spawn.Count >= 1, "Projectile was not added to the host location.");

        // First sample after a short settle so the measured window is strictly mid-flight.
        await Task.Delay(TimeSpan.FromSeconds(1), ct);
        var first = await ServerApi.GetPacingProbeState("projectile", ct);
        Assert.NotNull(first);
        Assert.True(first.Success, $"First probe state read failed: {first.Error}");
        Assert.True(first.Count == 1, "Probe projectile disappeared before the first read.");

        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var second = await ServerApi.GetPacingProbeState("projectile", ct);
        Assert.NotNull(second);
        Assert.True(second.Success, $"Second probe state read failed: {second.Error}");
        Assert.True(second.Count == 1, "Probe projectile disappeared before the second read.");

        var ticks = second.ServerTicks - first.ServerTicks;
        Assert.True(
            ticks > 0,
            $"Server ticks did not advance between reads ({first.ServerTicks} → {second.ServerTicks})."
        );

        var pxPerTick = (second.ProjectileTravelDistance - first.ProjectileTravelDistance) / ticks;
        Assert.True(
            Math.Abs(pxPerTick - ExpectedPxPerTick) <= PxPerTickTolerance,
            $"Projectile advanced {pxPerTick:F2} px/tick over {ticks} ticks at SERVER_TPS=24 — expected "
                + $"{ExpectedPxPerTick:F0} ± {PxPerTickTolerance:F1} (8 px/step × 2.5 steps/tick). ~16 means "
                + "the fractional carry is truncated (extra steps stuck at floor(TickScale)−1); ~8 means the "
                + "sub-step did not run at all."
        );

        LogSuccess(
            $"Projectile advanced {pxPerTick:F2} px/tick over {ticks} ticks at SERVER_TPS=24 — the "
                + "fractional carry alternates 2/3 whole steps to average 2.5 (60/24) per tick."
        );
    }
}
