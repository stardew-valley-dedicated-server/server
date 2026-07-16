using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// Runtime gate for the TPS-agnostic pacing patches on <b>per-tick physics</b> — projectiles, item
/// debris, and flyer-monster movement (<c>JunimoServer.Shared.TpsAgnosticPacing</c>).
///
/// <para>
/// <b>Why this exists.</b> The mod pins <c>TargetElapsedTime = 1000/TPS</c>, so any vanilla code that
/// advances a fixed amount per <c>Update</c> tick (a projectile's <c>position += velocity</c>, debris
/// gravity, a bat's velocity ramp) runs <c>60/TPS</c>× too slow at a reduced <c>SERVER_TPS</c>. The
/// patches sub-step those per-tick constants so the entity covers the same distance per real second at
/// any TPS. These are <b>server-simulated</b>: <c>IsMasterGame</c> is always true here, so
/// <c>GameLocation.UpdateWhenCurrentLocation</c> ticks the host's projectiles/debris and
/// <c>Monster.behaviorAtGameTick</c> runs the bat's AI.
/// </para>
///
/// <para>
/// <b>Why a client is connected (<c>Clients = 1</c>).</b> The empty-server auto-pause
/// (<c>AlwaysOn.HandleAutoPause</c>) sets <c>netWorldState.IsPaused</c> when no players are online, and
/// <c>Game1.HostPaused</c> then gates out <c>UpdateCharacters</c>/<c>UpdateLocations</c> entirely
/// (<c>Game1.cs:4308</c>) — so on a player-less server NOTHING in the world ticks and every probe reads
/// zero. One connected client unpauses the server (<c>numPlayers >= 1 → IsPaused = false</c>), which is
/// also the realistic scenario: a player present in the world is exactly when these entities simulate.
/// The probe entities are spawned in the HOST's own location (open Farm), which the server ticks because
/// the host is a farmer there.
/// </para>
///
/// <para>
/// <b>What it measures.</b> Each case spawns one probe entity via <c>/test/pacing_probe_spawn</c>, waits
/// a fixed <b>wall-clock</b> window, then reads its travelled distance / rest state via
/// <c>/test/pacing_probe_state</c>. Thresholds are sized so they only pass when the per-tick physics
/// advanced at real-time speed: at <c>SERVER_TPS=5</c> the patch must run ~12 sub-steps per tick, so an
/// unpatched build (or the kill-switch off) falls ~12× short.
/// </para>
/// </summary>
// Clients = 1: one connected player unpauses the server so the world (and the probe entities) tick.
// Exclusive: each case mutates global world entity collections (location.projectiles/debris/characters)
// and the shared host location, so it must not run concurrently with another method on the server.
[TestServer(Clients = 1, Isolation = IsolationMode.SharedClass, Exclusive = true)]
public class PacingProbeTests : TestBase
{
    // Wall-clock window each probe is allowed to run before we read its state. Long enough that a
    // real-time-paced entity travels/settles well past the pass threshold, and that a 12×-slow unpatched
    // entity visibly falls short. Kept short so the test stays quick on the shared exclusive chain.
    private static readonly TimeSpan ProbeWindow = TimeSpan.FromSeconds(3);

    // A projectile fired at 8 px/update covers 8 × 60 = 480 px per real second when the sub-step keeps it
    // wall-clock-correct — ~1440 px over the 3s window. An unpatched build advances 8 × SERVER_TPS = 40
    // px/s (~120 px total) at TPS 5. 500 px cleanly separates the two.
    private const float ProjectileMinTravel = 500f;

    // A GreenSlime (Slipperiness 4) given a 100 px/tick knockback impulse slides ~100/(1/4) ≈ 400 px before
    // friction (¼ decay per tick, once per tick) stops it — a wall-clock-constant distance at any TPS when
    // the velocity path runs once per tick. Under the buggy sub-step the impulse decays ~12× per tick and
    // collapses in ~one tick (barely slides). 250 px cleanly separates fixed from unfixed.
    private const float KnockbackMinDistance = 250f;

    // A Bat spawned 640 px from the host homes in at wall-clock speed once its whole AI tick (velocity ramp
    // + move) is sub-stepped for gliders — MEASURED 766 px net displacement in 3s (it reaches the host and
    // overshoots). Without the glider sub-step it's near-stationary (~15-38 px, velocity never ramps). 400
    // px cleanly separates fixed from unfixed.
    private const float MonsterMinDisplacement = 400f;

    [Fact]
    public async Task Projectile_TravelsWallClockDistance_AtReducedTps()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureConnectedAsync("ProbeProjectile", ct: ct);

        var spawn = await ServerApi.SpawnPacingProbe("projectile", ct);
        Assert.NotNull(spawn);
        Assert.True(spawn.Success, $"Projectile probe spawn failed: {spawn.Error}");
        Assert.True(spawn.Count >= 1, "Projectile was not added to the host location.");

        await Task.Delay(ProbeWindow, ct);

        var state = await ServerApi.GetPacingProbeState("projectile", ct);
        Assert.NotNull(state);
        Assert.True(state.Success, $"Projectile probe state read failed: {state.Error}");
        Assert.True(
            state.ProjectileTravelDistance >= ProjectileMinTravel,
            $"Projectile travelled {state.ProjectileTravelDistance:F0} px in {ProbeWindow.TotalSeconds:F0}s — "
                + $"expected ≥ {ProjectileMinTravel:F0} px for wall-clock-correct pacing. A value near "
                + "~120 px means the per-tick sub-step did not run (the projectile advanced at 60/TPS× speed)."
        );

        LogSuccess(
            $"Projectile travelled {state.ProjectileTravelDistance:F0} px in {ProbeWindow.TotalSeconds:F0}s "
                + "wall-clock at the reduced test TPS — the sub-step kept it at real-time speed."
        );
    }

    [Fact]
    public async Task Knockback_CarriesWallClockDistance_AtReducedTps()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureConnectedAsync("ProbeKnockback", ct: ct);

        var spawn = await ServerApi.SpawnPacingProbe("knockback", ct);
        Assert.NotNull(spawn);
        Assert.True(spawn.Success, $"Knockback probe spawn failed: {spawn.Error}");
        Assert.True(spawn.Count >= 1, "Knocked-back slime was not added to the host location.");

        await Task.Delay(ProbeWindow, ct);

        var state = await ServerApi.GetPacingProbeState("knockback", ct);
        Assert.NotNull(state);
        Assert.True(state.Success, $"Knockback probe state read failed: {state.Error}");
        Assert.True(
            state.MonsterDisplacement >= KnockbackMinDistance,
            $"Knockback carried the slime {state.MonsterDisplacement:F0} px in {ProbeWindow.TotalSeconds:F0}s — "
                + $"expected ≥ {KnockbackMinDistance:F0} px. A small value means the velocity-friction decay "
                + "ran once per sub-step (~12×/tick) instead of once per tick, collapsing the knockback impulse."
        );

        LogSuccess(
            $"Knockback carried the slime {state.MonsterDisplacement:F0} px wall-clock at the reduced test TPS "
                + "— the velocity path (and its per-tick friction decay) ran once per tick, not per sub-step."
        );
    }

    [Fact]
    public async Task Debris_SettlesWallClock_AtReducedTps()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureConnectedAsync("ProbeDebris", ct: ct);

        var spawn = await ServerApi.SpawnPacingProbe("debris", ct);
        Assert.NotNull(spawn);
        Assert.True(spawn.Success, $"Debris probe spawn failed: {spawn.Error}");
        Assert.True(spawn.Count >= 1, "Debris was not added to the host location.");

        await Task.Delay(ProbeWindow, ct);

        var state = await ServerApi.GetPacingProbeState("debris", ct);
        Assert.NotNull(state);
        Assert.True(state.Success, $"Debris probe state read failed: {state.Error}");
        Assert.True(state.DebrisChunkCount > 0, "Probe debris reported no chunks.");
        Assert.True(
            state.DebrisChunksAtRest == state.DebrisChunkCount,
            $"Only {state.DebrisChunksAtRest}/{state.DebrisChunkCount} debris chunks finished falling in "
                + $"{ProbeWindow.TotalSeconds:F0}s — expected all of them. Chunks still bouncing means the "
                + "gravity/velocity integration ran at 60/TPS× speed (the sub-step did not run)."
        );

        LogSuccess(
            $"All {state.DebrisChunkCount} debris chunks finished falling within {ProbeWindow.TotalSeconds:F0}s "
                + "wall-clock at the reduced test TPS — the sub-step kept the fall at real-time speed."
        );
    }

    [Fact]
    public async Task FlyerMonster_ClosesDistanceWallClock_AtReducedTps()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureConnectedAsync("ProbeMonster", ct: ct);

        var spawn = await ServerApi.SpawnPacingProbe("monster", ct);
        Assert.NotNull(spawn);
        Assert.True(spawn.Success, $"Monster probe spawn failed: {spawn.Error}");
        Assert.True(spawn.Count >= 1, "Bat was not added to the host location.");

        await Task.Delay(ProbeWindow, ct);

        var state = await ServerApi.GetPacingProbeState("monster", ct);
        Assert.NotNull(state);
        Assert.True(state.Success, $"Monster probe state read failed: {state.Error}");
        Assert.True(
            state.MonsterDisplacement >= MonsterMinDisplacement,
            $"Bat moved {state.MonsterDisplacement:F0} px toward the host in {ProbeWindow.TotalSeconds:F0}s "
                + $"(speed {state.MonsterSpeed:F1}) — expected ≥ {MonsterMinDisplacement:F0} px. A small value "
                + "means the glider's velocity ramp + move were not sub-stepped (it integrates a ~0 velocity, "
                + "so it stays near-stationary at reduced TPS)."
        );

        LogSuccess(
            $"Bat closed {state.MonsterDisplacement:F0} px on the host in {ProbeWindow.TotalSeconds:F0}s "
                + $"wall-clock (speed {state.MonsterSpeed:F1}) at the reduced test TPS — the glider AI-tick "
                + "sub-step moved it at real-time speed."
        );
    }
}
