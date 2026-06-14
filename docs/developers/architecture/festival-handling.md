# Festival Handling

How the always-on server gets the host into and out of in-game festivals without a human present. The logic lives in [`AlwaysOnServerFestivals`](https://github.com/stardew-valley-dedicated-server/server/blob/master/mod/JunimoServer/Services/AlwaysOnServer/AlwaysOnFestivals.cs) and deliberately mirrors the game's own [`DedicatedServer`](https://github.com/stardew-valley-dedicated-server/server/blob/master/decompiled/sdv-1.6.15-24356/StardewValley/Network/Dedicated/DedicatedServer.cs): monitor ready-checks, warp/answer via public entry points, no reflection-driven reinvention. The manual reproduction steps are in the [festival testing runbook](../testing/festivals-manual.md).

## Lifecycle

A festival day runs through four phases, each driven from a per-tick or per-second handler in [`AlwaysOn`](https://github.com/stardew-valley-dedicated-server/server/blob/master/mod/JunimoServer/Services/AlwaysOnServer/AlwaysOn.cs):

1. **Enter** — `HandleFestivalStart` (per tick). When `Game1.whereIsTodaysFest` is set and the `festivalStart` ready-check shows every player except the host is ready, the host marks itself ready (so clients' "Waiting for players…" dialog clears) and warps to the festival. Once warped, `BeginActiveFestival` records which festival is active.

2. **Main event** — `HandleFestivalEvents` (per second), for festivals that have one. It announces a countdown in chat, and when the countdown elapses the host answers the festival host's "start the event?" dialogue. The `!event` chat command short-circuits the countdown and starts immediately.

3. **Leave** — `HandleFestivalLeave` (per tick). The host triggers `TryStartEndFestivalDialogue` (ending the festival for everyone) in two cases, both matching the game's dedicated host: when the `festivalEnd` ready-check shows every remaining player is ready to leave, or immediately once the last player has left so the host isn't stranded.

4. **Reset** — `UpdateFestivalStatus` (on time change). In-game time is frozen while a festival event is active, so this runs only after the festival has ended and time resumes: once it passes the festival's window, active-festival state is cleared and the server returns to `online` mode. It is deliberately not gated on connected players, so a festival that ended with nobody present still clears its state instead of poisoning the next festival.

### Ready-check formula

"Everyone except the host is ready" is `numberReady >= GetNumberRequired(check) - 1 && !IsReady(check) && numberReady > 0`, matching `DedicatedServer.CheckOthersReady`. The same formula gates both `festivalStart` (warp in) and `festivalEnd` (leave). See [`.claude/rules/host-automation.md`](https://github.com/stardew-valley-dedicated-server/server/blob/master/.claude/rules/host-automation.md) item 2 for why variants break specific transports.

### Timeout backstop

Players drive entry and exit, but an AFK festival where no one votes to leave would otherwise stay open until its time window closes. `RunOfflineTimeout` is the wall-clock backstop: after `*TimeOut` elapses it warns players (`FestivalExitWarningSeconds` before the deadline) and then switches the server to `offline` mode, which disconnects everyone. It does not end the festival directly — but with the last player gone, the Leave phase's no-players path ends it on the next tick. There is no fixed "dwell then auto-leave" timer for leave-only festivals; they end on the `festivalEnd` ready-check, the no-players path, or (via this backstop's disconnect) the time window — nothing in between.

## Per-festival reference

Two shapes:

- **Main event** — the host warps in, runs a countdown, and starts a host-triggered event (egg hunt, grange judging, soup tasting, etc.).
- **Leave-only** — no host-triggered event; the host just waits for the `festivalEnd` ready-check (or the timeout). Spirit's Eve and the Feast of Winter Star are the only two.

Only the **Stardew Valley Fair** auto-ends on its own once the countdown finishes — grange judging *is* the whole festival, so there is nothing left to do. Every other festival, main-event or leave-only, ends on the `festivalEnd` ready-check or the timeout backstop.

| Festival | Date | Shape | Countdown (default) | Timeout (default) |
|----------|------|-------|--------------------|-------------------|
| Egg Festival | Spring 13 | Main event | 5 min | ~33 min |
| Flower Dance | Spring 24 | Main event | 5 min | ~33 min |
| Luau | Summer 11 | Main event (adds iridium starfruit to the soup) | 5 min | ~33 min |
| Dance of the Moonlight Jellies | Summer 28 | Main event | 5 min | ~33 min |
| Stardew Valley Fair | Fall 16 | Main event, **auto-ends after countdown** | 5 min | ~33 min |
| Spirit's Eve | Fall 27 | Leave-only | — | ~33 min |
| Festival of Ice | Winter 8 | Main event | 5 min | ~33 min |
| Feast of the Winter Star | Winter 25 | Leave-only | — | ~33 min |

All durations are operator-tunable via [`AlwaysOnConfig`](https://github.com/stardew-valley-dedicated-server/server/blob/master/mod/JunimoServer/Services/AlwaysOnServer/AlwaysOnConfig.cs): the `*CountdownSeconds` knobs (seconds) and the `*TimeOut` knobs (ticks at 60 TPS, so `120000` ≈ 2000 s ≈ 33 min). Most main-event timeouts start counting once the event begins; leave-only festivals and the Fair start theirs on entry.

## Why no fixed auto-end for leave-only festivals

The game's dedicated host never auto-leaves a festival on a fixed dwell timer — it leaves on the `festivalEnd` ready-check, or immediately once no players remain. The server matches that exactly. A short fixed "dwell then leave" timer would be a second wall-clock timeout layered on top of the existing AFK backstop, so it is deliberately absent.
