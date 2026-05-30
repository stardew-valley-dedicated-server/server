# Stardew Valley engine reference notes

Engine mechanics that the server/mod relies on. Prescriptive contributor rules live at [.claude/rules/cabin-system.md](https://github.com/stardew-valley-dedicated-server/server/blob/master/.claude/rules/cabin-system.md) and [.claude/rules/host-automation.md](https://github.com/stardew-valley-dedicated-server/server/blob/master/.claude/rules/host-automation.md) (the festival rule lives under host-automation, item 3).

For full source-of-truth, the decompiled game lives at `decompiled/sdv-1.6.15-24356/`.

## Sleep / wakeup / day-end

- `GameLocation.startSleep()` does NOT set `player.isInBed`. `isInBed` is set dynamically by `Farmer.Update()` from tile properties — so `setTileLocation()` to a bed tile MUST run BEFORE `startSleep()`.
- On clients (`!IsMasterGame`), `doSleep()` does NOT call `Game1.NewDay()`. It uses message type 14 (`newDaySync`).

## Ready-check messages and game availability

- Ready-check messages use message type 31. They are blocked by `PasswordProtectionService` for unauthenticated players.
- `isGameAvailable()` returns false during `newDaySync`. It stays false until `newDaySync.destroy()` (which runs AFTER all barriers + save + `PollForEndOfNewDaySync`).
- When `isGameAvailable() == false`, the server sends message type 11 (`Client_WaitForHostAvailability`); the client shows "Waiting for host event" in `FarmhandMenu` and times out after 45s.
- `checkFarmhandRequest` with `isGameAvailable() == false` sends type 11 and registers a `whenGameAvailable` callback that calls `sendAvailableFarmhands` (NOT `Check()`); the client bounces back to `FarmhandMenu`.

## newDaySync barrier behavior on disconnect

- When a client disconnects during the barrier: `playerDisconnected()` enqueues into `disconnectingFarmers`, `removeDisconnectedFarmers()` removes the entry from `otherFarmers`, and with empty `otherFarmers` the `barrierReady()` check returns true immediately.

## Cabin system — reference

Prescriptive cabin invariants are in [.claude/rules/cabin-system.md](https://github.com/stardew-valley-dedicated-server/server/blob/master/.claude/rules/cabin-system.md). Reference-only:

- `BuildStartingCabins` uses map-designated positions (tile 29/30 in the Paths layer), capped by `Game1.startingCabins`.

## Server invite code

- LAN-only servers (`AllowIpConnections=true`, no Steam account configured) never write an invite code. `InviteCodeFile.Read()` returns null in that configuration.
