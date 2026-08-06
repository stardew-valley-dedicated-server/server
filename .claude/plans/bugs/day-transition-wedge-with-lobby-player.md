# Day transition wedges the server permanently when a lobby player is connected

Status: narrowed to a blocking barrier inside the NewDay task. The barrier's **name** is the one
remaining unknown and needs one targeted log line plus a repro. Not fixed.

Intermittent (~50% of full-suite runs on 2026-08-03: `07-13-01Z` and `11-16-51Z` of four).
Pre-existing — the identical failure occurred at `1b5ea11` (2026-07-12,
`TestResults/runs/2026-07-12T19-48-23Z_1b5ea11` in the main checkout), which predates any of the
wedding-speedup work.

## Symptom

`LobbyHomedSpouseSteadyStateTests.MarriedCouples_UnderPassword_HomesStayRealAcrossNightsAndOffline`
fails with `503 (Service Unavailable)`, `failureCategory: "infrastructure"`. The server it ran on is
dead for the remainder of the run — every game-thread endpoint returns 503, and under `stopOnFail`
the whole run dies with it.

## Sequence (run `2026-08-03T07-13-01Z_38d72b9`, `containers/server-3/container.log`)

- `07:26:57` a client registers unauthenticated in the lobby (`lobby_unauthenticated_registered`,
  player `-183582307287801172`) — owned by `PasswordProtectionTests.Help_Command_WorksInLobby`,
  which parks a client in the lobby on purpose and never authenticates.
- `07:27:00` that client confirms character creation; `07:27:02` the server logs
  `[Auth] Player … finished character customization`. It remains unauthenticated.
- `07:27:03` `LobbyHomedSpouseSteadyStateTests` drives its night: `DayEnding`,
  `Synchronizing 'NewDay' task`, plus `[Auth] Blocked startNewDaySync` ×1 and
  `Blocked newDaySync` ×4 to that player (`PasswordProtectionService.cs:456-473` blocks those two
  message types to unauthenticated peers so their customization menu isn't closed).
- `07:27:06` `game_thread_stall_started lastTickMs=3150`. Never recovers.

**Why every endpoint then 503s.** `ApiService` drains its game-thread queue in `OnUpdateTicked`
(`ApiService.cs:1132-1140`), deliberately on the validated tick so mutating callbacks can't corrupt
a save. No tick → no drain → `RunOnGameThreadAsync`'s 5s guard (`:1773`) fires on every request.
`/stats` and `/health` keep answering in 0ms because they read the cached snapshot. Not resource
starvation: CPU 3-8%, `avgTickMs` frozen at 4.66 (no new ticks).

## Verified

**The lobby exclusion is working.** The `sleep` ready check reports `numberRequired: 3` at
`07:26:57.9` — computed *after* the lobby player joined, with four farmers online (host + 2
authenticated + 1 lobby) — and reaches `3/3 isReady:true` at `07:27:03`. A passing run corroborates
from the other side: `2026-06-30T19-28-04Z_864566d` server-2 ran a full transition with a lobby
player connected and 27 blocked `newDaySync` sends at `sleep numberRequired: 1`, and completed fine.
So `LobbyService`'s `IsFarmerRequired` postfix (`LobbyService.cs:273-298`, body `:580-599`) applies
and is not being inlined away.

**`ready_for_save` never ran.** `GameEventTracer` emits `ready_check_transition` on every state
change and demonstrably works for this check on this server (`07:17:27`, and the *previous* night at
`07:25:58-59` ending `3/3 isReady:true`). During the wedge, 204 server-3 events were emitted and
**zero `ready_for_save` transitions**. So that check's `Update` never ran during the stalled
transition.

**The host is blocked inside the NewDay task, before the save handshake.** SMAPI runs the transition
on a background thread; the main game thread is frozen. The only hard blocking wait in that range is
`NetSynchronizer.barrier()` (`decompiled/.../StardewValley/NetSynchronizer.cs:55-73`), whose
`while (!barrierReady(name))` loop cannot exit on the host — `shouldAbort()` reads
`Game1.client.timedOut` and `Game1.client` is null on the master, so a host-side barrier stall is
permanent by construction. That puts the stall in one of the ~15 barriers from `start` through
`saveFarmhands` (`Game1.cs:7786-8241`), and means `BarrierReady_Postfix`
(`LobbyService.cs:500-566`) — which exists precisely to release those for excluded players — did not
release this one.

## Ruled out (do not rebuild on these)

- **"The lobby player is counted as required at `ready_for_save`."** This came from the frozen
  `SaveGameMenu` readout "waiting for other players… (3/…)" plus the reasoning "3 ready and still
  blocked ⇒ required ≥ 4". Wrong: `ready_for_save.Update` never ran, so the displayed
  `numReadyForSave()` = 3 is a **stale cached value from the previous night**. The readout says
  nothing about the live gate. (The denominator is `Game1.getOnlineFarmers().Count`, unfiltered —
  `decompiled/.../Menus/SaveGameMenu.cs:186,208`.)
- **Harmony patch lost to JIT inlining of `IsFarmerRequired`.** Disproved by the `sleep` counts
  above.

## The one open question

**Which barrier.** The barrier system emits no events, so no artifact records it — this is the only
part that needs runtime.

**Next step (small, in existing code):** `BarrierReady_Postfix` already runs on the blocked thread
and already has the barrier `name` and `___barriers` in scope. Log the name and the unsatisfied ids
once a barrier has been unsatisfied for more than N seconds. Do not build a general stall-diagnostic
service — this is a handful of lines in a method that already exists.

Weigh these once the name is known: the postfix returns early when `__result` is already true, when
`_instance` is null, and when `HasPlayersToExclude()` is false (`:511-525`); it also reads
`Game1.otherFarmers.Keys` from the background task thread while the main thread can mutate it.

## Repro

Today this needs the broker to schedule two classes onto one server, which is why it is ~50% and not
reproducible on demand. A test that parks a client unauthenticated in the lobby and then drives a
night on the same server makes it deterministic, and is the gate for any fix.

Per `.claude/rules/universal/passing-test-isnt-proof-the-scenario-ran.md`, assert the transition
completes **and** confirm from the artifact that the lobby player was connected throughout — a green
assertion would also pass if the lobby client happened to disconnect first.

## Fix

Not proposed. Every candidate depends on which barrier stalls and why the postfix did not release
it; writing one now would be a guess. Whatever it turns out to be, do not add a retry or a re-apply
loop (`.claude/rules/universal/retry-is-evidence-of-root-cause.md`).

## Secondary finding — the stuck-barrier recovery cannot run during the stall

`DesyncKicker` detects the stall at +20s and *enqueues* its kick onto `_pendingGameThreadActions`
(`DesyncKicker.cs:168`), drained in `GameLoop.UpdateTicked` (`:48-50`). The game thread is frozen,
so the enqueued action can never execute — matching the log (kick announced at `07:27:23`, no
`Kicking …` line follows).

Real latent defect: the recovery path is inert in exactly the situation it exists for. It is **not**
the fix for this bug. If repaired, the seam is something that runs while the thread is blocked
(`NetSynchronizer.processMessages`, called every spin iteration) or an off-thread
`Game1.server.kick`. Open decision.

## Relationship to PR #494

Not caused by it — the mechanism is entirely server-side and reproduced at `1b5ea11` on master.
But not cleanly independent either: `SessionJoinMode.Unauthenticated` routes through
`Connect.JoinWithoutAuthAsync` (`PersistentSessionCoordinator.cs:258-259`), one of the two methods
#494 rewired (`ConnectionRetryHelper.cs:139-181`), so the lobby join that creates the precondition
is resequenced by that PR. An exposure-rate shift cannot be excluded from four runs. The wedge
itself predates it.

## Evidence notes

Run artifacts under `TestResults/runs/` are local and get pruned. The decisive frame came from the
server's own recording — the host's screen names the menu it is stuck in, and the on-screen TICK
counter across two frames says whether the game thread is alive:

```bash
ffmpeg -ss <seconds-from-start> -i containers/server-N/full_recording.mp4 -frames:v 1 out.png
```

For `2026-08-03T07-13-01Z_38d72b9` server-3 (686s long), t≈650 shows the frozen `SaveGameMenu`.
Reach for this before writing instrumentation.

## Related

- `tests-password-config-shared-with-exclusive-class.md` — the test-side collision that creates the
  precondition in CI. Fixing it reduces exposure but not the bug.
- `newgame-day-transition-never-completes.md` — same symptom family (transition never completes),
  different mechanism (game thread healthy, zero players).
- `tests-flake-cropsaver-day-advance.md` Mode 1 — also "day doesn't happen", but the clock freezes
  *before* any transition starts. Distinct.
