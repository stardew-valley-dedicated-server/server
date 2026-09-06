# Empty server never pauses on festival days and burns the day at the 2:00 AM pass-out

**Status:** ready-to-implement
**Priority:** 3 (high)
**GitHub Issue(s):** none (Discord report)
**Area:** server (`AlwaysOn.HandleAutoPause`)
**Related:** [`tests-timepaused-inherits-festival-day.md`](./tests-timepaused-inherits-festival-day.md) (same mechanism seen from the test side; this fix removes that flake's root cause)
**Observed:** production report — every player left at 6:00 AM on Spring 13 (Egg Festival); the server ran through the day, logged the vanilla `Server didn't make it to bed...` pass-out line, and woke on day 14 with the festival lost. Reproducible by reading: the branch is unconditional.
**Next step:** implement the predicate change and the regression test in one PR

## Symptom

On a festival date, an empty server keeps its clock running. Nothing writes `IsPaused=true`, so the day advances unattended until the host passes out at 2:00 AM and the day ends. Operators see the vanilla pass-out chat line (`Strings\UI:Chat_PassedOut1`, "{0} didn't make it to bed...") with the host's name, and the festival is skipped.

Public docs describe the auto-pause without this exception (`docs/features/server-mechanics.md`, `docs/community/faq.md`), so the behavior reads as a bug to users. The docs side is handled in the docs plan on the `docs/save-rollback-and-autopause` branch; this plan is the code fix.

## Root cause

`AlwaysOn.HandleAutoPause` pauses an empty server only when today is not a festival date:

```csharp
else if (numPlayers == 0 && !isFestivalDay)
{
    Game1.netWorldState.Value.IsPaused = Game1.timeOfDay is >= 600 and <= 2500;
}
```

`isFestivalDay` is `SDateHelper.IsFestivalToday()`, a pure date check. It is true from 6:00 AM on a festival date regardless of whether a festival event is running or anyone is online.

### Provenance

The exclusion is inherited verbatim from the upstream "Always On Server for Multiplayer" mod (`funny-snek/Always-On-Server-for-Multiplayer`, `NoClientsPause` in `ModEntry.cs`), where it has existed since that project's first commit in October 2018 as eight per-date comparisons. Our initial commit ported it as the single `!isFestivalDay` term and the line has only been touched for formatting, the 6:10 to 6:00 change, and the day-transition guard added in front of it.

Upstream's festival automation was driven by the in-game clock (announce at 6:00 to 6:30, open the `festivalStart` ready-check between 9:00 and 14:00, reset at 14:10), so the author kept the clock running on festival dates. But every one of those handlers was already gated on at least one player being online, so the clock was never needed on an *empty* festival day. The exclusion was coarse from the start, not a deliberate choice to let empty festival days burn.

Our festival code has since been rewritten to be ready-check driven (`AlwaysOnFestivals.HandleFestivalStart`, `HandleFestivalLeave`) with wall-clock timeouts (`RunFestivalTimeout`). None of it depends on the clock advancing while nobody is online: `HandleFestivalStart` returns immediately when `OnlineFarmers.CountOthers() == 0`, and the no-players branch of `HandleFestivalLeave` ends an in-progress festival and sends the host home.

### What the pause must not block

`Game1.HostPaused` (backed by `netWorldState.IsPaused`) gates the whole per-tick block that runs `UpdateGameClock`, `UpdateCharacters`, `UpdateLocations`, and `UpdateOther` (`Game1.cs`, the `!HostPaused && !showingEndOfNightStuff` branch in `Update`). `UpdateLocations` is what advances an active `Event`, including the `festivalEnd` warp home. So a pause applied while the host is still inside a festival event would strand the host at the festival with no way to end it. The empty-server pause on a festival date must therefore wait until no festival event is active.

## Fix

### 1. Narrow the exclusion from "festival date" to "festival event in progress"

In `AlwaysOn.HandleAutoPause`, replace the date-based `isFestivalDay` term with an event-based one:

```csharp
var festivalActive = Game1.CurrentEvent?.isFestival == true;

if (numPlayers >= 1)
{
    Game1.netWorldState.Value.IsPaused = clientPaused;
}
else if (numPlayers == 0 && !festivalActive)
{
    // The 600 floor pauses at day start (6:00), not after the first 10-minute tick.
    Game1.netWorldState.Value.IsPaused = Game1.timeOfDay is >= 600 and <= 2500;
}
```

Resulting behavior on a festival date with nobody online:

- Before anyone joins: paused at 6:00 like any other day.
- Players join, attend the festival, and all leave mid-festival: `HandleFestivalLeave`'s no-players branch ends the festival (`TryStartEndFestivalDialogue`), the host warps home, `CurrentEvent` clears, and the pause engages on the next tick at whatever time the festival left the clock.
- Players leave after the festival ended: pause engages immediately.

The 1:00 AM ceiling is unchanged: an empty server whose clock is already past 1:00 AM (players were online when it crossed) still runs to the 2:00 AM pass-out so the day closes.

Update the summary doc comment on `HandleAutoPause` and remove the now-unused `isFestivalDay` local. Keep the `SDateHelper.IsFestivalToday()` early-return that skips the *other* automation handlers in `OnUpdateTicked` on festival days; it is unrelated to the pause and out of scope.

### 2. Regression test

Add to `HostAutomationTests` (shares the `TimePaused_WhenNoPlayersConnected` setup):

`TimePaused_WhenNoPlayersConnected_OnFestivalDay` — `ServerApi.SetDate` to a festival date (Spring 13), confirm no players, then `GET /wait/status?isPaused=true`. Asserts via the HTTP snapshot per `tests-assert-via-http-api.md`. Before the fix this long-poll can never succeed, which is exactly the flake `tests-timepaused-inherits-festival-day.md` documents.

Per `passing-test-isnt-proof-the-scenario-ran.md`, confirm in the run's server log that `whereIsTodaysFest` was set (the `[Festival]` trace from `HandleFestivalStart` prints each second while it is) so the test really ran on a festival date.

### 3. Related test plan

Once this lands, `tests-timepaused-inherits-festival-day.md` loses its root cause. Its proposed per-test date pin is still a reasonable isolation guard; either apply it in the same PR and delete that plan, or leave the plan with a note that the mechanism is gone and only the hygiene part remains. Decide at implementation time.

## Verification

- Unit: none; the predicate is two lines.
- E2E: the new test passes and the existing `TimePaused_WhenNoPlayersConnected` still passes.
- E2E: the festival suite (`FestivalTests`) still passes, in particular any test where the last player leaves during an active festival. That is the path the "must not block" analysis above protects; a hang there means the pause engaged before `CurrentEvent` cleared.
- Runtime: on a running server, `/test/set_date` to Spring 13 with no players connected, then `GET /status` shows `isPaused=true` and the clock holds at 6:00.

## Compatibility verification

- **LAN vs Steam:** the predicate reads only `Game1.otherFarmers.Count` and `Game1.CurrentEvent`; transport-independent.
- **Lobby players:** lobby players are entries in `Game1.otherFarmers` (`LobbyService` iterates it to queue the frozen-time delta), so a lobby-only server takes the players-online branch exactly as today; unchanged.
- **`SERVER_TPS=5`:** `HandleAutoPause` runs per tick from `OnUpdateTicked`, not from the 12-second `OneSecondUpdateTicked` cadence; the one-tick delay between `CurrentEvent` clearing and the pause engaging is the same at any TPS.
- **Day transition in flight:** the existing `GameManagerService.IsDayTransitionComplete()` guard in front of the predicate is untouched and still forces `IsPaused=false` during transitions.
- **Festival wall-clock timeout:** `RunFestivalTimeout` runs while the event is active, when the pause is not applied; unaffected.
- **Other `IsPaused` writers:** `LobbyService` saves and restores the flag around serializing the frozen lobby delta (net zero), and `PasswordProtectionService` clears it once on a join so the joining client is not born paused. Neither writes the empty-server case, and the next `HandleAutoPause` tick re-asserts the predicate either way.
