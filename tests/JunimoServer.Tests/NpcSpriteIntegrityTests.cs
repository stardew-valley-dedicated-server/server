using JunimoServer.Tests.Helpers;
using JunimoServer.Tests.Infrastructure;
using Xunit;

namespace JunimoServer.Tests;

/// <summary>
/// E2E gates for the sprite-integrity heal (NpcSpriteIntegrityService): an NPC that survives
/// save-load with <c>Sprite == null</c> (its spritesheet asset exists per DoesAssetExist but
/// throws on load — broken content pack, missing native image decoder) NREs on its next
/// scheduled departure inside <c>Game1.performTenMinuteClockUpdate</c>. The queued schedule
/// entry is never dequeued, so every later ten-minute boundary re-throws, each throw aborting
/// the clock update before <c>netWorldState.UpdateFromGame1()</c> — permanently freezing every
/// client's clock. The service heals such NPCs with an empty sprite (vanilla's own
/// missing-asset degradation, rendered as an error box) at SaveLoaded and DayStarted.
///
/// <para>
/// Sprites are not serialized (<c>Character.Sprite</c> is [XmlIgnore]; they rebuild on every
/// load), so a persisted-poison reload test like the lobby-home sweeps use is impossible —
/// the broken state can only exist live. Instead: /test/break_npc_sprite reproduces the exact
/// post-load field state, /test/heal_npc_sprites runs the same sweep the event handlers run,
/// and the SaveLoaded/DayStarted <em>wiring</em> is proven separately via the service's run
/// counters, which only the real event handlers advance.
/// </para>
/// </summary>
// Exclusive: mutates global calendar state (SetDate/SetClockSpeed) and breaks/heals a
// villager on the shared server (SharedClass does not serialize methods on its own).
// Clients = 1: with zero clients AlwaysOn.HandleAutoPause pauses the clock for the whole
// 600–2500 window, so daytime schedule-departure boundaries — the exact code path under
// test — only ever fire with a player connected.
[TestServer(Clients = 1, Isolation = IsolationMode.SharedClass, Exclusive = true)]
public class NpcSpriteIntegrityTests : TestBase
{
    /// <summary>A vanilla Town villager with a daily schedule — the incident shape.</summary>
    private const string BrokenNpc = "Abigail";

    /// <summary>
    /// Daytime target the clock must reach after the heal: from 6:00 this crosses ~36
    /// ten-minute boundaries, including the healed NPC's first schedule departure (Abigail's
    /// vanilla schedules start mid-morning) — each departure runs
    /// checkSchedule → routeEndAnimationFinished, the exact call that NREs on a null sprite
    /// and freezes the clock.
    /// </summary>
    private const int DaytimeTarget = 1200;

    [Fact]
    public async Task BrokenSprite_IsHealed_AndScheduleBoundariesDoNotFreezeClock()
    {
        var ct = TestContext.Current.CancellationToken;

        // The boot world becomes reachable a beat before its SaveLoaded handlers finish, so
        // an immediate break could race the boot's own SaveLoaded sweep (which would heal it
        // and leave the explicit-heal leg counting 0). /reload's completion contract resolves
        // only on the tick after every SaveLoaded handler ran — break into a quiescent world.
        // Same settle pattern as LobbyHomedSpouseHealTests. Reload precedes the connect so
        // the world swap can't kick the farmer.
        var ready = await ServerApi.WaitForServerOnline(
            TestTimings.DayChangeTimeout,
            cancellationToken: ct,
            requireInviteCode: false
        );
        Assert.True(ready?.IsReady == true, "Server must be ready before the settle reload.");
        await ReloadServerAsync();

        // A connected player keeps the clock running (HandleAutoPause pauses 600–2500 when
        // the server is alone) and receives the world-state time broadcasts the frozen-clock
        // bug starves.
        var farmer = await Farmers.ConnectNewAsync(namePrefix: "SpriteHeal", ct: ct);
        var uid = farmer.JoinResult.UniqueMultiplayerId;
        Assert.True(
            await ServerApi.WaitForPlayerByIdAsync(uid, ct: ct),
            $"Farmer (uid={uid}) must be present server-side after the join."
        );

        // Fresh non-festival morning (spring 2 → 3 crosses no festival): set_date resets the
        // clock to 6:00, three game-hours before the earliest schedule departure, so no
        // boundary can reach the broken NPC's schedule in the sub-second break→heal window.
        var setDate = await ServerApi.SetDate("spring", 2, year: 1, ct);
        Assert.True(setDate?.Success == true, $"SetDate(spring 2) failed: {setDate?.Error}");

        var baseline = await ServerApi.GetNpcSpriteIntegrity(ct);
        Assert.True(
            baseline?.Success == true,
            $"Baseline integrity read failed: {baseline?.Error}"
        );
        Assert.True(
            baseline!.SpritelessNpcs.Count == 0,
            "A healthy world must have no sprite-less NPCs before the break, got "
                + $"[{string.Join(", ", baseline.SpritelessNpcs)}]."
        );

        // Fault-inject the post-load shape a failed sprite rebuild leaves behind.
        var broken = await ServerApi.BreakNpcSprite(BrokenNpc, ct);
        Assert.True(broken?.Success == true, $"break_npc_sprite failed: {broken?.Error}");
        Assert.True(
            broken!.HadSprite,
            $"{BrokenNpc} must have had a sprite before the break — a false value means the "
                + "world was already broken and this test is not injecting anything."
        );

        var poisoned = await ServerApi.GetNpcSpriteIntegrity(ct);
        Assert.True(
            poisoned?.SpritelessNpcs.Contains(BrokenNpc) == true,
            $"{BrokenNpc} must read sprite-less after the break, got "
                + $"[{string.Join(", ", poisoned?.SpritelessNpcs ?? new())}]."
        );

        // Heal via the same sweep the SaveLoaded/DayStarted handlers run.
        var healed = await ServerApi.HealNpcSprites(ct);
        Assert.True(healed?.Success == true, $"heal_npc_sprites failed: {healed?.Error}");
        Assert.True(
            healed!.HealedCount == 1,
            $"The sweep must heal exactly the one broken NPC, healed {healed.HealedCount}."
        );

        var afterHeal = await ServerApi.GetNpcSpriteIntegrity(ct);
        Assert.True(
            afterHeal?.Success == true && afterHeal.SpritelessNpcs.Count == 0,
            "No sprite-less NPCs may remain after the heal, got "
                + $"[{string.Join(", ", afterHeal?.SpritelessNpcs ?? new())}]."
        );

        try
        {
            // The load-bearing gate: cross the morning schedule-departure boundaries with the
            // healed NPC. Pre-fix (unhealed), the first departure NREs, the clock never
            // advances again (and the ERROR log-scan poisons the server); healed, the clock
            // sails past. ~36 boundaries at clock speed 20 ≈ 15s.
            await ServerApi.SetClockSpeed(20, ct);
            var crossed = await PollingHelper.WaitUntilAsync(
                WaitName.Polling_NpcSprite_DaytimeBoundariesCrossed,
                async () => (await ServerApi.GetStatus(ct))?.TimeOfDay >= DaytimeTarget,
                timeout: TestTimings.DayChangeTimeout,
                cancellationToken: ct
            );
            if (!crossed)
            {
                var status = await ServerApi.GetStatus(ct);
                Assert.Fail(
                    $"Clock did not reach {DaytimeTarget} (stuck at {status?.TimeOfDay}) with "
                        + $"the healed {BrokenNpc} in the world — a frozen clock across "
                        + "schedule departures is the incident this fix exists for."
                );
            }

            // Finish the day the proven way (client sleeps, clock runs to pass-out): the
            // overnight dayUpdate re-runs ChooseAppearance (restoring the real sprite where
            // content is healthy) and the DayStarted sweep runs.
            var before = await ServerApi.GetStatus(ct);
            Assert.NotNull(before);
            var sleep = await GameClient.Actions.Sleep();
            Assert.True(sleep?.Success == true, $"Farmer sleep failed: {sleep?.Error}");
            await ServerApi.SetTime(TestTimings.PrePassOutTime, ct);
            var (dayChanged, disconnected) = await DayChange.WaitAsync(
                before.Day,
                before.Season,
                before.Year,
                checkConnection: true,
                ct
            );
            Assert.False(disconnected, "The farmer disconnected during the sleep-through.");
            Assert.True(
                dayChanged,
                $"Day did not advance past {before.Season} {before.Day} Y{before.Year}."
            );
        }
        finally
        {
            await ServerApi.SetClockSpeed(1, ct);
        }

        var nextMorning = await ServerApi.GetNpcSpriteIntegrity(ct);
        Assert.True(
            nextMorning?.Success == true && nextMorning.SpritelessNpcs.Count == 0,
            "No sprite-less NPCs may exist the morning after the heal, got "
                + $"[{string.Join(", ", nextMorning?.SpritelessNpcs ?? new())}]."
        );

        LogSuccess(
            $"Sprite-less {BrokenNpc} was healed by the sweep (count=1), the clock crossed "
                + $"the morning departure boundaries to {DaytimeTarget}+ without freezing, "
                + "and the day completed normally."
        );
    }

    [Fact]
    public async Task Sweeps_AreWiredToSaveLoadedAndDayStarted_HealthyWorldHealsNothing()
    {
        var ct = TestContext.Current.CancellationToken;

        var ready = await ServerApi.WaitForServerOnline(
            TestTimings.DayChangeTimeout,
            cancellationToken: ct,
            requireInviteCode: false
        );
        Assert.True(ready?.IsReady == true, "Server must be ready before the wiring reload.");

        // /reload completes only after every SaveLoaded handler ran, so the first read below
        // observes this reload's own sweep — proving the subscription fired, not just that
        // the sweep logic works when invoked directly (the bypass path a green
        // /test/heal_npc_sprites call can't distinguish).
        await ReloadServerAsync();

        var integrity = await ServerApi.GetNpcSpriteIntegrity(ct);
        Assert.True(
            integrity?.Success == true,
            $"Integrity read after reload failed: {integrity?.Error}"
        );
        Assert.True(
            integrity!.SaveLoadedRuns >= 1,
            "The SaveLoaded sweep must have run at least once after a /reload — zero means "
                + "the subscription wiring is broken and shells from a failed load would "
                + "never be healed."
        );
        Assert.True(
            integrity.DayStartedRuns >= 1,
            "The DayStarted tripwire sweep must have run at least once — zero means its "
                + "subscription wiring is broken."
        );
        Assert.True(
            integrity.LastRunHealedCount == 0,
            $"A healthy world must heal nothing on load, healed {integrity.LastRunHealedCount} "
                + "— a nonzero count means the load path is producing sprite-less NPCs."
        );
        Assert.True(
            integrity.SpritelessNpcs.Count == 0,
            "A freshly reloaded healthy world must have no sprite-less NPCs, got "
                + $"[{string.Join(", ", integrity.SpritelessNpcs)}]."
        );

        LogSuccess(
            "Sprite-integrity sweeps are wired: SaveLoadedRuns="
                + $"{integrity.SaveLoadedRuns}, DayStartedRuns={integrity.DayStartedRuns}, "
                + "and the healthy reload healed nothing."
        );
    }
}
