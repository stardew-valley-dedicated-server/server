---
paths:
  - "mod/JunimoServer/Services/AlwaysOnServer/**"
  - "mod/JunimoServer/Services/HostAutomation/**"
  - "mod/JunimoServer/Services/Lobby/**"
  - "mod/JunimoServer/Services/CabinManager/**"
---

# Host-automation invariants

For any mod code that automates host behavior (sleep, warp, festival, save, event skip, pause), first read `decompiled/sdv-1.6.15-24356/StardewValley/Network/Dedicated/DedicatedServer.cs`. The game's own dedicated-host path usually already solves it deterministically using public entry points. Below are the concrete invariants that fall out of that approach in this codebase.

1. **`hasDedicatedHost = false` is intentional** (`AlwaysOn.cs:102-104`), not a TODO. Flipping it activates `Game1.dedicatedServer.Tick()` (every-frame from `Game1._update`) and changes cross-service behavior across ~24 services: `SaveGameMenu` player count (`SaveGameMenu.cs:184`), `Event` host routing (`Event.cs:4871, 12083`), `DialogueBox` safetyTimer (`DialogueBox.cs:462`), `FarmerTeam` semantics, `ScreenFade` alpha (`ScreenFade.cs:71-169`), `Game1.updatePause` (`Game1.cs:5394`), and farmer behavior in `Farmer.cs:1915`.

2. **Ready-check formula** for "all others ready, host is the only missing one" must be: `numberReady >= GetNumberRequired(name) - 1 && !IsReady(name) && numberReady > 0`. Used by sleep, festivalStart, festivalEnd, ready_for_save, wakeup. Maintained by `LobbyService.UpdateSleepReadyCheckExclusion` (`LobbyService.cs:843-844`) via `SetLocalRequiredFarmers`, used in `AlwaysOnFestivals.cs:269` and `AlwaysOn.OthersReadyForSleep`, and matches `DedicatedServer.CheckOthersReady` (`DedicatedServer.cs:441-457`). Don't invent variants like `required - ready == 1` (subtraction) or exact equality — they break the lobby exclusion and Steam/LAN transports. The `numberReady > 0` guard is required because excluded unauthenticated players would otherwise trivially satisfy `ready >= required - 1`.

3. **Don't repro festival code with `/debug day`.** `Game1.whereIsTodaysFest` is populated only during a real day transition (sleep-through) from `weatherIcon`; debug-day commands skip that step. Festival paths see empty `whereIsTodaysFest` and silently no-op — any time spent reproducing after `/debug day` is wasted. To repro: set the day to one before the festival via debug, then sleep through. Festival schedule reference: [`docs/developers/testing/festivals-manual.md`](../../docs/developers/testing/festivals-manual.md).

**Why:** AlwaysOn sleep refactor: hours of bandaid analysis collapsed to "the game already does this at `DedicatedServer.cs:401-467`" once we looked. The mod was reinventing ready-check + warp + answerDialogueAction with reflection and tick-modulo retries. The three invariants above each fall out of that decompiled-first reading; ignoring them in favor of subtle variants has cost real debugging cycles (silent festival no-ops, ready-check failures specific to one transport).

**How to apply:** Before proposing any new polling loop, retry heartbeat, or reflection call against a private game method, grep `decompiled/.../DedicatedServer.cs` for how `Tick()` handles the same concern. Port that pattern. When tempted to flip `hasDedicatedHost = true` to delete a workaround, treat it as a multi-service investigation, not a one-line change.
