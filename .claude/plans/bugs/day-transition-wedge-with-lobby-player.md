# Day transition wedges the server when a lobby player is connected

**Status:** Root cause narrowed to a host-side blocking barrier inside the NewDay task. The exact barrier **name** remains unknown and requires one targeted log line plus a deterministic repro. Not fixed.

**Intermittent:** ~50% of full-suite runs on 2026-08-03 (`07-13-01Z` and `11-16-51Z` of four).

**Pre-existing:** The identical failure occurred at `1b5ea11` (2026-07-12, `TestResults/runs/2026-07-12T19-48-23Z_1b5ea11` in the main checkout), before the wedding-speedup work.

## Symptom

`LobbyHomedSpouseSteadyStateTests.MarriedCouples_UnderPassword_HomesStayRealAcrossNightsAndOffline` fails with `503 (Service Unavailable)`, `failureCategory: "infrastructure"`.

The server it ran on is dead for the remainder of the run: every game-thread endpoint returns 503. Under `stopOnFail`, the whole run therefore dies with it.

## Sequence

Run `2026-08-03T07-13-01Z_38d72b9`, `containers/server-3/container.log`:

* **07:26:57:** A client registers unauthenticated in the lobby (`lobby_unauthenticated_registered`, player `-183582307287801172`). This is the client owned by `PasswordProtectionTests.Help_Command_WorksInLobby`, which intentionally parks a client in the lobby without authenticating.
* **07:27:00:** That client confirms character creation.
* **07:27:02:** The server logs `[Auth] Player … finished character customization`. The client remains unauthenticated.
* **07:27:03:** `LobbyHomedSpouseSteadyStateTests` drives its night: `DayEnding`, `Synchronizing 'NewDay' task`, then `[Auth] Blocked startNewDaySync` ×1 and `Blocked newDaySync` ×4 for that player. `PasswordProtectionService.cs:456-473` deliberately blocks those two message types to unauthenticated peers so their customization menu is not closed.
* **07:27:06:** `game_thread_stall_started lastTickMs=3150`. The game thread never recovers.

### Why every endpoint then returns 503

`ApiService` drains its game-thread queue in `OnUpdateTicked` (`ApiService.cs:1132-1140`), deliberately on the validated tick so mutating callbacks cannot corrupt a save.

Once the game thread stops ticking, the queue is never drained and `RunOnGameThreadAsync`'s 5s guard (`ApiService.cs:1773`) fires on every game-thread request.

`/stats` and `/health` continue answering in 0ms because they read the cached snapshot.

This is **not resource starvation**: CPU remains at 3-8%, while `avgTickMs` is frozen at 4.66 because no new ticks are occurring.

## What is proven

### The lobby exclusion is working

The `sleep` ready check reports `numberRequired: 3` at `07:26:57.9` — computed **after** the lobby player joined, with four farmers online (host + 2 authenticated + 1 lobby) — and reaches `3/3 isReady:true` at `07:27:03`.

A passing run corroborates this from the other direction: `2026-06-30T19-28-04Z_864566d` server-2 completed a full transition with a lobby player connected and 27 blocked `newDaySync` sends at `sleep numberRequired: 1`.

Therefore `LobbyService`'s `IsFarmerRequired` postfix (`LobbyService.cs:273-298`, body `:580-599`) is applying correctly; this is not a case where the Harmony patch was lost or bypassed.

### `ready_for_save` is not the stalled gate

`GameEventTracer` emits `ready_check_transition` on every state change and demonstrably works for this check on this server:

* `07:17:27`
* the previous night at `07:25:58-59`, ending `3/3 isReady:true`

During the wedge, 204 server-3 events were emitted and **zero `ready_for_save` transitions** occurred.

Therefore `ready_for_save.Update` never ran during the stalled transition.

The frozen `SaveGameMenu` readout — `"waiting for other players… (3/…)"` — is consequently not evidence that the live `ready_for_save` gate requires four players. Its `numReadyForSave()` value of 3 is a **stale cached value from the previous night**.

The denominator shown by the menu is also `Game1.getOnlineFarmers().Count`, which is unfiltered (`decompiled/.../Menus/SaveGameMenu.cs:186,208`).

### The host is blocked inside the NewDay task, before the save handshake

SMAPI runs the transition on a background thread while the main game thread is frozen.

The identified hard blocking wait in the relevant NewDay range is `NetSynchronizer.barrier()` (`decompiled/.../StardewValley/NetSynchronizer.cs:55-73`). Its:

```text
while (!barrierReady(name))
```

loop cannot exit on the host if the barrier never becomes ready: `shouldAbort()` reads `Game1.client.timedOut`, but `Game1.client` is null on the master.

A host-side barrier stall is therefore permanent by construction.

The stall must therefore be in one of the roughly 15 barriers spanning `start` through `saveFarmhands` (`Game1.cs:7786-8241`).

`BarrierReady_Postfix` (`LobbyService.cs:500-566`) exists specifically to release barriers for excluded players, yet it did not release the barrier that stalled this transition.

**The exact barrier name and the reason its postfix did not release it are the remaining unknowns.**

## Ruled out

Do not rebuild these theories:

* **“The lobby player is counted as required at `ready_for_save`.”** `ready_for_save.Update` never ran. The `SaveGameMenu` `3/…` value is stale from the previous night and says nothing about the live gate.
* **Harmony patch lost to JIT inlining of `IsFarmerRequired`.** Disproved by the `sleep` ready counts above and by the passing run with a lobby player connected.

## Open question: which barrier?

The barrier system currently emits no events, so no artifact records the barrier name.

The next step should therefore be **small and targeted**, not a general stall-diagnostic system:

`BarrierReady_Postfix` already runs on the blocked thread and already has the barrier `name` and `___barriers` in scope. Add a diagnostic that logs the barrier name and unsatisfied IDs once a barrier invocation has remained unsatisfied for more than N seconds, with enough throttling to avoid logging on every spin iteration.

Once the barrier name is known, inspect why `BarrierReady_Postfix` failed to release it. In particular, the postfix returns early when:

* `__result` is already true;
* `_instance` is null;
* `HasPlayersToExclude()` is false (`LobbyService.cs:511-525`).

It also reads `Game1.otherFarmers.Keys` from the background task thread while the main thread can mutate that collection, so the threading behavior around that read should be considered once the failing barrier is identified.

## Repro

The current full-suite failure depends on the broker scheduling two classes onto one server, which makes it roughly 50% reproducible rather than deterministic.

A targeted test should make the precondition explicit:

1. Park an unauthenticated client in the lobby and keep it connected.
2. Drive a night transition on the same server.
3. Assert that the transition completes.
4. Confirm from the artifact that the lobby player remained connected throughout the transition.

The artifact assertion is important. Per `.claude/rules/universal/passing-test-isnt-proof-the-scenario-ran.md`, a green transition assertion alone is insufficient: the test could pass because the lobby client happened to disconnect before the transition reached the problematic barrier.

This deterministic repro should be the gate for any production fix.

## Fix

**No fix proposed yet.**

Every candidate currently depends on which barrier stalls and why `BarrierReady_Postfix` did not release it. Anything more specific would be guesswork.

Do not add a retry or re-apply loop as the fix; per `.claude/rules/universal/retry-is-evidence-of-root-cause.md`, that would mask the blocking condition rather than explain it.

## Secondary finding: stuck-barrier recovery cannot run during the stall

`DesyncKicker` detects the stall at +20s and **enqueues** its kick onto `_pendingGameThreadActions` (`DesyncKicker.cs:168`).

Those actions are drained in `GameLoop.UpdateTicked` (`DesyncKicker.cs:48-50`).

The game thread is frozen, so the queued kick can never execute. This matches the log:

* `07:27:23`: kick announced
* no subsequent `Kicking …` line

This is a real latent defect: the recovery path is inert in exactly the situation it exists to recover.

It is **not the fix for this bug**.

If the recovery path is repaired, the execution seam must run while the game thread is blocked — for example, something called from `NetSynchronizer.processMessages`, which runs on each barrier spin iteration — or an off-thread `Game1.server.kick`. Which approach is appropriate is an open decision.

## Relationship to PR #494

The wedge itself is **not caused by PR #494**.

The mechanism is entirely server-side and reproduces at `1b5ea11` on main, before the PR's changes.

However, the PR is not cleanly independent of the exposure rate:

`SessionJoinMode.Unauthenticated` routes through `Connect.JoinWithoutAuthAsync` (`PersistentSessionCoordinator.cs:258-259`), one of the two methods rewired by #494 (`ConnectionRetryHelper.cs:139-181`).

Therefore #494 resequences the lobby join that creates the precondition, so an exposure-rate change cannot be excluded from the four-run sample.

The distinction is:

* **Wedge mechanism:** pre-existing and reproduced before #494.
* **Whether #494 changes how often the precondition occurs:** still possible.

## Evidence notes

Run artifacts under `TestResults/runs/` are local and may be pruned.

The decisive visual evidence came from the server's own recording: the host's screen identifies the menu in which it is stuck, and the on-screen TICK counter across two frames establishes whether the game thread is still advancing.

Use:

```bash
ffmpeg -ss <seconds-from-start> -i containers/server-N/full_recording.mp4 -frames:v 1 out.png
```

For `2026-08-03T07-13-01Z_38d72b9`, server-3 recording is 686s long; approximately `t=650` shows the frozen `SaveGameMenu`.

Check the recording before adding broader instrumentation.

## Related

* `tests-password-config-shared-with-exclusive-class.md` — the test-side collision that creates the precondition in CI. Fixing it reduces exposure but does not fix the underlying server bug.
* `newgame-day-transition-never-completes.md` — same symptom family (transition never completes), but a different mechanism: the game thread remains healthy and there are zero players.
* `tests-flake-cropsaver-day-advance.md` Mode 1 — also “day doesn't happen,” but the clock freezes before any transition starts. Distinct mechanism.
