# Issue #46 — 3rd-party mods can block server sleep indefinitely

**Status:** validation
**Priority:** 2 (medium)
**GitHub Issue(s):** [#46](https://github.com/stardew-valley-dedicated-server/server/issues/46)
**Area:** server
**Related:** none
**Observed:** reported on #46 (a client-side mod menu keeps a player un-ready); still present by reading, not reproduced in-tree
**Next step:** post the root-cause comment on #46 (file and symbol), keep open until the sleep-phase timeout is designed

## Root cause

The host only auto-sleeps once every other player has readied for sleep, and that
gate is purely the vanilla ready-check:

- `HandleAutoSleep` (`mod/JunimoServer/Services/AlwaysOnServer/AlwaysOn.cs`)
  returns early unless `OthersReadyForSleep()` is true.
- `OthersReadyForSleep` reads
  `Game1.netReady.GetNumberReady("sleep")` — nothing else.

A client whose mod keeps a menu open (e.g. Lookup Anything's F1 inspect) never
goes to bed, so it never counts as ready → the host never initiates sleep →
**no save starts**, indefinitely.

The existing kicker doesn't cover this case. Both of
`DesyncKicker`'s kick timers (`mod/JunimoServer/Services/NetworkTweaks/DesyncKicker.cs`)
arm only after the day-end sequence has already begun:

- `OnSaving` — 60 s kick of players whose end-of-night status isn't
  "ready"; `Saving` fires after the save barrier starts.
- `OnDayEnding` — 20 s kick of players stuck in the new-day barrier;
  `DayEnding` fires once the day actually ends.

Both events presuppose the host initiated sleep — which is exactly what a
never-ready client prevents. So the kicker handles a client that
readied-then-desynced, **not** one that never readies. No other mechanism forces
a never-slept client to bed or kicks an un-ready (pre-save) player.

## Fix

A sleep-phase timeout that force-passes-out or kicks clients who haven't readied
within N seconds. Design notes for whoever implements it:

- Arming condition needs care: "some players ready, host waiting" is the stuck
  state. A timer keyed off the first `GetNumberReady("sleep") > 0` observation
  (or off 2:00 AM forced pass-out time) are candidate triggers.
- Respect the ready-check invariants in `.claude/rules/host-automation.md` —
  `LobbyService.UpdateSleepReadyCheckExclusion` keeps lobby/unauthenticated
  players out of `GetNumberRequired`, and any new kick path must apply the same
  exclusion set (`LobbyService.GetExcludedPlayerIds`, as both `DesyncKicker`
  paths already do).
- Decide force-pass-out vs kick: vanilla already force-passes-out everyone at
  2:00 AM *if the day advances*, but the day can't advance while the ready-check
  is blocked — that inversion is the heart of the bug.

## Verification

For the eventual fix. E2E: client connects, opens a menu that blocks readiness (or test-client simply
never sends the sleep ready), host requests sleep → assert the client is forced
to bed or kicked within the timeout and the save completes. Per
`.claude/rules/tests-assert-via-http-api.md`, assert via the server HTTP API.

## Task

1. Comment on #46: root cause (vanilla-only sleep gate in `AlwaysOn.HandleAutoSleep` /
   `OthersReadyForSleep`; kicker arms post-save in `DesyncKicker.OnSaving` / `OnDayEnding`) and the fix
   surface above.
2. Keep open until the sleep-phase timeout is designed and implemented (separate
   plan when picked up).
