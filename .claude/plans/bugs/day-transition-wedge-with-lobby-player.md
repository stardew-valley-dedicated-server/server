# Stuck-barrier recovery cannot run while the new-day task is blocked

**Status:** ready-to-implement
**Priority:** 3 (high)
**GitHub Issue(s):** none
**Area:** server
**Related:** [`tests-password-config-shared-with-exclusive-class.md`](tests-password-config-shared-with-exclusive-class.md)
**Observed:** every wedged transition in the 2026-08-03 and 2026-07-12 full-suite runs; reproduced deterministically on 2026-09-02 by `PasswordProtectionTests.LobbyPlayer_WithAuthenticatedFarmhand_DayTransitionCompletes` against the mod without the barrier check-in broadcast
**Next step:** implement the three changes in `DesyncKicker.cs` below, then run the verification recipe

## Context

The lobby-player day-transition wedge itself is fixed: `LobbyService.BarrierReady_Postfix` checks in at every new-day barrier on behalf of excluded players, for the vanilla farmhands' benefit (their own `barrierReady` waits on every farmer they know about, and no patch reaches them). `docs/developers/architecture/game-engine-notes.md` records the mechanism.

What remains is the recovery path that was supposed to catch any such stall and did not.

## Symptom

While the new-day task is blocked in a barrier, `DesyncKicker` detects the stall after 20s and logs `still stuck in barrier, going to try kicking`, but no `kicking due to not making past barrier` line ever follows. The server stays wedged until the offending player disconnects on its own.

Run `2026-07-12T19-48-23Z_1b5ea11`, `containers/server-3/container.log`: `waiting 20 sec to kick barrier`, then `waited 20 sec to kick barrier`, then nothing. Same shape in the 2026-09-02 baseline run of the repro test: a stall of ~109s, recovered only by the lobby player's disconnect at test teardown.

## Root cause

The new-day task never runs on a background thread. `Game1.Update` hands `_newDayTask` to `hooks.StartTask`, and SMAPI's `SModHooks.StartTask` calls `task.RunSynchronously()` on the game thread (the `Synchronizing 'NewDay' task...` log line is that call). While a barrier is unsatisfied the game thread sits inside `NetSynchronizer.barrier`'s spin loop, which calls `NewDaySynchronizer.processMessages` every 16ms and nothing else. No SMAPI event fires during that time, validated or unvalidated (`ApiService`'s stall watchdog reads a timestamp written from `UnvalidatedUpdateTicked`, which is why it reports the stall).

`DesyncKicker.OnDayEnding` waits 20s on a thread-pool task, then enqueues the kick onto `_pendingGameThreadActions`, which is drained only by its `GameLoop.UpdateTicked` handler. That handler cannot run until the barrier resolves, so the recovery is inert in exactly the situation it exists for.

Two smaller defects in the same handler:

* The kick evaluates who is missing from the hardcoded `sleep` barrier. A stall at an earlier barrier (`start`, `date`) kicks every farmhand, including ones that have checked in at the current barrier.
* A stall at a barrier after `sleep` (there are seventeen more, `handleMiniShippingBins` through `checkcompletion`) kicks nobody, because everyone reached `sleep`.

## Fix

All in `mod/JunimoServer/Services/NetworkTweaks/DesyncKicker.cs`. The constructor gains a `Harmony` parameter, the same way `LobbyService` receives one.

1. **Drain from the barrier spin.** Move the `_pendingGameThreadActions` drain loop out of `OnUpdateTicked` into a private `DrainPendingGameThreadActions()` and call it from both `OnUpdateTicked` and a new Harmony postfix on `NewDaySynchronizer.processMessages`. That method is what the barrier loop calls on every spin iteration, on the game thread, so the existing "mutations run on the game thread" contract in the class comment still holds and the kick runs within one spin (16ms) of being enqueued. It is also called from `hasStarted`, `isBarrierReady` and `isVarReady`, so every wait in `_newDayAfterFade` is covered.
2. **Track the current barrier.** Add a Harmony prefix on `NetSynchronizer.barrier(string name)` that stores `name` in a field, cleared in `OnSaved`. The `OnDayEnding` kick evaluates `barrierPlayers(<current name>)` instead of `"sleep"`; when no barrier has been entered yet (stall inside `newDaySync.start()`), it uses `"start"`.
3. **Kick only the players missing from that barrier**, excluding `_lobbyService.GetExcludedPlayerIds()` as today.

No retry or re-arm loop: after the kick, vanilla's own path completes the barrier. `Server.kick` on both transports calls `playerDisconnected` synchronously (`LidgrenServer.kick`; `SteamGameServerNetServer.kick` via `ShutdownConnection`'s close callback), which marks the farmer in `disconnectingFarmers`; the next `processMessages` runs `Multiplayer.UpdateEarly`, whose `removeDisconnectedFarmers` drops the farmer from `otherFarmers`, and `barrierReady` stops waiting for it. This is the same thread and the same sequence vanilla uses when a client disconnects mid-barrier on its own, so it needs no thread-safety work.

## Compatibility verification

* **Transports:** `LidgrenServer.kick` and `SteamGameServerNetServer.kick` both reach `GameServer.playerDisconnected` synchronously (see above). The forced-kick message to the peer is sent before the connection closes on both.
* **Lobby/unauthenticated players:** still excluded from the kick via `GetExcludedPlayerIds()`; they are never in a barrier set and `LobbyService.BarrierReady_Postfix` vouches for them.
* **Test TPS (`SERVER_TPS=5`):** irrelevant; the spin loop sleeps 16ms wall-clock per iteration regardless of tick rate.
* **Other subscribers on the same seam:** `LobbyService.BarrierReady_Postfix` patches `barrierReady`, not `processMessages` or `barrier`; no ordering dependency.
* **Disconnect mid-operation:** a farmer that disconnects on its own between detection and drain is already in `disconnectingFarmers`; `kick` on a missing peer is a guarded no-op on Lidgren (`peers.ContainsLeft`) and on the Steam server (`_farmerConnectionMap.TryGetValue`).
* **End-of-day kick (`OnSaving`, 60s):** unchanged by this plan. It targets the `ready_for_save` phase, which runs from `SaveGameMenu.update` on normal ticks after the task has completed, so its `UpdateTicked` drain is reachable. Not verified at runtime here.

## Verification

1. Locally comment out the check-in broadcast loop in `LobbyService.BarrierReady_Postfix` (the `Game1.server.sendMessage(peerId, checkIn)` fan-out) so the wedge reproduces.
2. Run `make test FILTER=LobbyPlayer_WithAuthenticatedFarmhand`.
3. In `containers/server-0/container.log` expect, in order: `Synchronizing 'NewDay' task...`, `waited 20 sec to kick barrier`, `kicking due to not making past barrier: <farmhand id>` within a second of it, then the barrier check-in lines and `task complete.` The day must advance. The test itself will fail on its "driver farmhand must be online" assertion, which is the expected outcome in this configuration; the gate is the log sequence plus the day advancing.
4. Restore the broadcast and run the same test plus `LobbyPlayer_SurvivesDayTransition_CanAuthenticateAfter`; both must pass with no `kicking` line.
