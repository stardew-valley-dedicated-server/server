---
paths:
  - "mod/JunimoServer/Services/Api/**"
---

# A test-only state-setter endpoint must run the engine's reconciliation, not just poke `Game1` fields

When a `/test/*` (or any debug) endpoint mutates game state, setting the bare `Game1.*` field is rarely enough — the vanilla code path that normally changes that state also runs *reconciliation* (replication to peers, derived-field recompute, day-boundary resets). Skip it and you get a host that looks right but desyncs clients or crashes later. Before adding a state-setter, find where the engine itself changes the same state and mirror its full reconciliation.

**Why:** Two separate failed E2E runs, same root shape:
- **`POST /time` didn't replicate.** It set `Game1.timeOfDay = time` only. `timeOfDay` is a replicated `NetWorldState` field; without `Game1.netWorldState.Value.UpdateFromGame1()` the host's write never reached the client, which stayed at its stale time until the host's *next natural 10-minute tick* broadcast a delta. A connected client read 06:30 while the server was at 22:00.
- **`POST /test/set_date` left stale festival state.** It set season/day/year but skipped the engine's new-day reset (`newDayAfterFade`: `timeOfDay = 600`, `whereIsTodaysFest = null`, `updateWeatherIcon()`). A stale `weatherIcon == 1` (festival, from the prior day) bled onto a non-festival day, so `performTenMinuteClockUpdate` tried to load a non-existent `Data/Festivals/<season><day>` and crashed the update loop ("Server error during: joining world"). Note `weatherIcon` is **local, not replicated** — each instance derives it from `isFestivalDay()`, so `updateWeatherIcon()` only fixes the host; clients recompute theirs once the date replicates.

**How to apply:** Before landing a `/test/*` state-setter, open the vanilla method that performs the same change (sleep/new-day/warp/etc.) in `decompiled/sdv-1.6.15-24356/` and list what it does *besides* the bare write — `UpdateFromGame1()` for any replicated `NetWorldState` field (time, date, weather, daysPlayed…), `updateWeatherIcon()` + `whereIsTodaysFest = null` + `timeOfDay = 600` for a date jump, etc. Mirror those. Decide per-field whether it's replicated (push via `UpdateFromGame1`) or local-derived (the host fix is enough; clients self-correct from the replicated inputs). Adjacent to `host-automation.md` (decompiled-first for host *automation*) and `netfield-revert-pattern.md`/`netdictionary-public-surface.md` (replication semantics of specific writes); this rule is the narrower case of a *test setter* that has to reproduce a whole reconciliation step.
