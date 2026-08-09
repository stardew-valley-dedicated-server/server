# Fix: pacing-probe spawn geometry depends on leftover host position (debris deleted out-of-bounds)

Status: **planned, not implemented.** Diagnosed from CI run
[31162945185](https://github.com/stardew-valley-dedicated-server/server/actions/runs/31162945185)
(scheduled E2E, 2026-08-07) — sole real failure `PacingProbeTests.Debris_SettlesWallClock_AtReducedTps`
("Probe debris reported no chunks."), everything else stop-on-fail cascade.

## Problem (verified against run artifacts + code)

`POST /test/pacing_probe_spawn` places every probe entity relative to **wherever the host happens to
be standing** (`ApiService.TestEndpoints.cs` — debris at host + (640, −128) px, bat +640, slime +320).
The endpoint's docstring assumes the host stands on the open Farm. It doesn't: the host idles hidden
**inside its level-0 FarmHouse**, and after any sleep-driven day transition on the same shared server
(e.g. WeddingTests — `AlwaysOn.HostSleepInBed`, `AlwaysOn.cs:1155`, snaps the host to the bed spot),
the host is left lying in a bed **directly against the farmhouse's east wall**. Nothing moves it
afterward.

From the bed, +640 px east is outside the interior map entirely. Vanilla deletes out-of-bounds
debris chunks on the first update tick (`Debris.cs:711`: `position.X >= map.DisplayWidth + 64` →
`chunks.RemoveAt`), and this debris has exactly **one** chunk (`new Debris("(O)388", …)` →
`InitializeChunks(1, …)`), so the debris empties and `updateChunks` returns true → removed from
`location.debris` immediately.

Evidence chain from the failing run's artifact:

- Server log: `[PacingProbe] Debris: debrisAtRest=0/0 count=0` — debris object already gone from the
  location 3s after a successful spawn.
- Failure screenshot + recording frames: host asleep in bed, chat shows the prior wedding test
  ("Server has gone to bed"), black void starting one tile east of the bed.
- Not timing/TPS-related: the pacing sub-step under test never got a chance to matter.

So the "flake" is **deterministic given test order** on the shared server: sleep-driving test runs
first on `lan-farm0-CabinStack` → host in bed → debris probe fails 100%. Otherwise the host is near
the farmhouse entry (west side), the drop lands in-bounds by ~220 px → passes.

**Latent siblings:** the knockback probe (slime knocked +x from host+320) and monster probe share the
same geometry assumption. Indoors with the host at the east wall, the slime's eastward slide hits the
wall (or spawns in the void where collision blocks all movement) and would miss its 250 px threshold.
Fix the class of problem, not just the debris instance.

## Fix (recommended): give the probes a deterministic arena

In `HandlePostTestPacingProbeSpawnAsync` (`ApiService.TestEndpoints.cs`):

1. **Before spawning, warp the host to a fixed, known-clear tile on the Farm** (e.g. a clear spot
   near the farmhouse porch). The Farm guarantees every probe offset is in-bounds with runway to
   move. Host presence in the location is what makes the world tick there (the tests' `Clients = 1`
   unpause requirement is unchanged), and the bat's homing target stays correct.
2. **Restore the host to its farmhouse idle spot** when the probe is cleaned up
   (`RemoveTrackedProbes` / after the state read), so the shared server is left exactly as other
   tests expect. `PacingProbeTests` is `Exclusive`, so nothing observes the excursion mid-test.
3. Correct the endpoint + test docstrings: the "spawned in the HOST's own location (open Farm)"
   claim was never true.
4. Strengthen the assertion message: "no chunks" should say what zero means — debris was deleted,
   most likely spawned out of map bounds; check host position (this investigation in one sentence).

Rejected smaller fix: clamping the debris drop point into the current room's bounds fixes only the
debris probe and leaves the knockback/monster wall/runway traps in place. Take it only if the host
warp turns out to have an unacceptable side effect (none known — `HandleAutoSleep` already warps a
not-at-home host home before sleeping, wedding automation already returns the host home between
events).

## Verification (runtime gates, in order)

1. **Repro red first, deterministically:** on a shared server, drive one sleep/day-transition
   (connect a client, `SleepToSaveAsync`, disconnect — host ends in bed), then run
   `make test FILTER=PacingProbeTests`. `Debris_SettlesWallClock_AtReducedTps` must fail every time
   with "no chunks". If it doesn't fail, stop — the diagnosis is wrong.
2. Apply the fix; repeat the same host-in-bed scenario. All four probe tests green, repeatedly
   (≥3 runs).
3. Read the passing run's server log and confirm the scenario actually ran (`[PacingProbe] Debris:`
   line shows `count=1`, all chunks at rest) — don't trust the green checkmark alone
   (`passing-test-isnt-proof-the-scenario-ran.md`).
4. Full local suite once, watching wedding/sleep tests for any host-position interaction.

## Related, separate items (not this plan)

- CI `SDVD_STOP_ON_FAIL=true` canceled ~70 tests on this one flake; recommendation from the
  investigation: let scheduled runs finish for the full flake surface per run.
- `summary.json` counted one cascade victim as `failed` (innermost `SocketException` slipped the
  canceled-classifier seam from PR #425) — separate accounting fix.
