# Fix plan: Sprite-null NPC freezes the world clock (`NPC.routeEndAnimationFinished` NRE)

Investigation input: [`npc-sprite-nre-tenminute-update.md`](npc-sprite-nre-tenminute-update.md). This plan supersedes
it: the mechanism is now fully verified against decompiled sources, two of its hypotheses are corrected
(see "Corrections"), and the SkiaSharp errors it declared out-of-scope turn out to be the trigger.

## Summary

An NPC whose spritesheet **exists per `DoesAssetExist` but throws on load** survives save-load with
`Sprite == null`. On this render-suppressed server nothing touches the sprite until the NPC's first
scheduled departure, where vanilla dereferences `Sprite` unconditionally inside the ten-minute clock
update. The queued schedule entry is never dequeued, so **every subsequent ten-minute boundary
re-throws**, and each throw aborts the clock update before `netWorldState.UpdateFromGame1()` — the only
path that pushes `timeOfDay` to clients — so every client's clock freezes permanently ("stuck at 6:40").

Two independent root causes stack:

1. **Image regression (trigger).** #473 (`d0d059b`) removed the GUI apt packages whose transitive deps
   included `libfontconfig1`; SMAPI's SkiaSharp-based image decoding fails without it, so content-pack
   image loads (custom NPC spritesheets from SVE etc.) throw. Data edits (JSON) still succeed, so the
   NPCs keep their data and schedules — only the sprite is missing. The working tree already re-adds
   `libfontconfig1` (uncommitted, `docker/Dockerfile:161-162`).
2. **Vanilla load fragility (the class of bug to harden against).** `NPC.TryLoadSprites` swallows the
   load exception and leaves `Sprite` null; `ChooseAppearance` downgrades that to a log warning;
   `fixProblems` only removes villagers whose *data* is missing. Any broken content pack — not just
   this SkiaSharp incident — reproduces the frozen-clock disaster on a headless server.

Fix = commit the image dependency (layer 1) + a mod-side sprite-integrity heal at the load boundary
(layer 2), with diagnostics that name the broken NPC, plus E2E coverage.

## Verified mechanism (all cites: `decompiled/sdv-1.6.15-24356/` unless noted)

### Shell creation at save-load

- Save deserialization constructs NPCs via the parameterless ctor (XmlSerializer); only the
  `Character(AnimatedSprite, ...)` ctor assigns `Sprite` (`Character.cs:571-582`). `Character.Sprite`
  is `[XmlIgnore]` (`Character.cs:442-453`), so sprites never round-trip — they are always
  reconstructed at load.
- Reconstruction happens once, in `SaveGame.loadDataToLocations`: for every NPC in every location,
  `initializeCharacter(obj, location)` (sets `currentLocation`) then `obj.reloadSprite()`
  (`SaveGame.cs:1567-1579`) → `ChooseAppearance()` (`NPC.cs:1171-1196`).
- `TryLoadSprites` (`NPC.cs:1249-1288`): `if (Sprite == null) Sprite = new AnimatedSprite(content,
  assetName)` sits inside a `try/catch` that returns `false` on any exception — **a throwing content
  load leaves `Sprite` null**. `ChooseAppearance` responds with `Game1.log.Warn("NPC {name} can't load
  sprites from '{asset}': ...")` and carries on (`NPC.cs:1071-1074`); its final size fix-up is guarded
  by `Sprite != null` (`NPC.cs:1080`).
- Crucially, the hole opens **only** when `DoesAssetExist == true` but the load throws (the SkiaSharp
  case, a corrupt PNG, a crashing loader). For a *cleanly missing* asset, `AnimatedSprite`'s ctor
  skips loading (`AnimatedSprite.cs:203-218`) and returns a valid textureless sprite — vanilla's
  designed degradation, rendered as an error box (`NPC.draw`, `NPC.cs:5069-5074`,
  `Utility.DrawErrorTexture`).
- `Game1.fixProblems` (`Game1.cs:7446-7487`) removes a villager only when `GetData() == null`; its own
  `try/catch` around `npc.Sprite.Texture` shows vanilla anticipates Sprite-null NPCs at load. An NPC
  with intact data but a throwing spritesheet **survives as a shell**.

### Why only this server hits it

- Rendering is suppressed (`SERVER_FPS=0`, `Game.Draw` patched out — see
  `.claude/rules/display-scaling.md`). A rendering game NREs instantly at `NPC.draw`
  (`Sprite.Texture` deref, `NPC.cs:5069`), making the broken NPC loud. Headless, the shell idles
  silently: per-tick NPC updates only run in locations with players present, and nothing else touches
  `Sprite` until the schedule fires.

### The crash and the permanent clock freeze

- Every 10 game-minutes: `Game1.UpdateGameClock` (`Game1.cs:6050-6090`) →
  `performTenMinuteClockUpdate` (`Game1.cs:5882`): resets `gameTimeInterval` (`:5887`), advances
  `timeOfDay += 10` (`:5890`), then iterates **all** locations calling
  `performTenMinuteUpdate`/`timeUpdate` (`:6005-6014`), and only afterwards syncs world state to
  clients via `netWorldState.Value.UpdateFromGame1()` (`:6017-6020`).
- `GameLocation.performTenMinuteUpdate` loops `characters` with no try/catch, only an `IsInvisible`
  guard (`GameLocation.cs:13638-13652`), calling `checkSchedule(timeOfDay)`.
- `checkSchedule` (`NPC.cs:4093-4159`): queues today's due entry (`:4112-4121`), then if
  `queuedSchedulePaths[0].time` has arrived calls `prepareToDisembarkOnNewSchedulePath()` (`:4135`) —
  the dequeue (`RemoveAt(0)`, `:4139-4141`) happens **after** it returns.
- `prepareToDisembarkOnNewSchedulePath` → `finishEndOfRouteAnimation` (`NPC.cs:4161-4219`): when the
  NPC is *not* mid-end-of-route-animation, the `else` branch calls **`routeEndAnimationFinished(null)`
  unconditionally** (`:4215-4218`). So every scheduled departure of every NPC runs it — no
  end-of-route animation state is required (correction to the input doc).
- `routeEndAnimationFinished` (`NPC.cs:4276-4305`): first statements dereference `Sprite`
  (`Sprite.SpriteWidth = data?.Size.X ?? 16;`, `:4281-4285`). The `who` parameter is never used, so
  `null` is fine; `GetData()` is null-safe; every other member in the method is a non-nullable
  NetField or plain field. **`Sprite == null` is the only NRE candidate.** Line arithmetic matches the
  reported `NPC.cs:4786`: the two stack anchors (real 4713/4719 ↔ decompiled 4217/4223) put the first
  `Sprite.` deref at real ≈4777 + ~9 doc-comment lines = 4786.
- Consequences of the throw, per boundary:
  - The queued entry is never dequeued → the same throw repeats at **every** later boundary that day.
  - The `foreach` over locations aborts → remaining locations get no ten-minute/time updates.
  - `UpdateFromGame1()` is skipped → clients never see `timeOfDay` change again (client time is only
    driven by the world-state broadcast — `.claude/rules/host-automation.md` invariant 6). The
    *server-internal* clock still advances (the increment precedes the loop), which is why the log
    shows exactly one NRE per real ~N seconds, not a tick-storm.
  - SMAPI catches at `SCore.OnPlayerInstanceUpdating` ("An error occurred in the base update loop")
    so the process survives in this zombie state.
- Overnight, `NPC.dayUpdate` clears the queue (`NPC.cs:6091`) and retries `ChooseAppearance`
  (`:6135`) — which fails again while the content is broken, so the crash loop re-arms every morning.

### Corrections to the input investigation

- **Join-race hypothesis: refuted.** The master never deserializes NPC shells mid-session, and no
  vanilla code nulls a live NPC's sprite after construction (project-wide grep for `Sprite = null` /
  `sprite.Value = null`: only unrelated `TemporaryAnimatedSprite` locals). The second player's join is
  incidental timing. The load boundary is the *only* shell-creation seam on the master.
- **SkiaSharp errors are the trigger, not a separate bug.** The "9 minutes after the NRE" ordering
  argument only covered the pasted excerpt; the load-time failures ("NPC X can't load sprites from
  ...") happen at boot. Timeline fits: #473 merged 2026-07-12, operator pulls the new image, restarts
  → save reloads with broken SkiaSharp → shells → first scheduled departure freezes the clock.
- **#474 `ReHomeNpc` exonerated with mechanism**: `ClearSchedule` leaves `queuedSchedulePaths` stale,
  but `checkSchedule` early-returns on `Schedule == null` (`NPC.cs:4107-4110`) and `dayUpdate` clears
  the queue (`:6091`), so the stale entries are unreachable.

### Answers to the input doc's open questions

1. *Which NPC?* Not derivable from the excerpt; the reporter's log names it — see "Field
   confirmation". The fix's diagnostics name it permanently.
2. *Mid-resync at join?* Irrelevant — no server-side shell path exists via joins.
3. *Deterministic?* Yes: with broken content it reproduces on **every load**, not a race.
4. *Vanilla-only?* Vanilla bug (worth an upstream report), but the headless config uniquely hides the
   visual signal and uniquely suffers the clock freeze — the hardening belongs in this mod.
5. *Stuck clock mechanism?* Answered above: repeat-throw before dequeue + skipped `UpdateFromGame1`.

## Field confirmation (cheap; do before/alongside the fix)

- Reporter's full SMAPI log, grep: `can't load sprites from` (names every shell NPC at load),
  `Removed broken NPC` (data-less removals), and the NRE recurring once per boundary after 16:00:12.
- Confirm the server image is post-#473 without the `libfontconfig1` line, and that the server was
  restarted (image update) before in-game day 4.

## Fix design

### Layer 1 — image dependency (already in working tree, commit it)

`docker/Dockerfile` re-adds `libfontconfig1` with a consumer comment. Ship as its own
`fix(docker):` commit/PR — it unbreaks all content-pack image loading (EditImage too) independently
of the mod hardening.

Verification (per `.claude/rules/image-runtime-deps-must-be-explicit.md` — same #473 incident class
as libicu67): build, boot a server container far enough that the game files exist (they live in the
volume, downloaded at runtime), then
`docker exec <c> ldd /data/game/smapi-internal/libSkiaSharp.so` → no `not found` lines. A green build
proves nothing here.

### Layer 2 — mod: `NpcSpriteIntegrityService` (the durable hardening)

**Principle: restore the engine invariant at the boundary that violates it.** The engine assumes
`NPC.Sprite != null` in ~40 unguarded call sites (schedule departure, route animations, emotes, draw,
day updates); guarding the one reported crash site would be whack-a-mole. Removal is wrong too —
vanilla only removes *data-less* NPCs, and these NPCs have data, schedules, possibly marriages. The
correct heal makes the "asset exists but throws" case converge onto vanilla's already-supported
"asset missing" case: a **valid, textureless `AnimatedSprite`**, which vanilla renders as an error
box (`NPC.cs:5069-5074`) and null-guards everywhere that matters (`AnimatedSprite.draw`
`:563-577`, `UpdateSourceRect` via `Texture?.Width ?? 96` `:73-75`, `StopAnimation` `:244-256`).

New service `mod/JunimoServer/Services/NpcIntegrity/NpcSpriteIntegrityService.cs` (auto-discovered via
`IModService`, ctor-injected `IModHelper`/`IMonitor`, subscribes in `Entry()`):

- `GameLoop.SaveLoaded` → `HealSpritelessNpcs("save_loaded")` — the seam where shells are created.
- `GameLoop.DayStarted` → `HealSpritelessNpcs("day_started")` — tripwire sweep against unknown seams,
  mirroring the `HealLobbyHomedResidents` precedent (`CabinManagerService.cs:214,225`). No-ops (zero
  allocations beyond the walk) on healthy saves; runs twice per day, not per-tick.
- `HealSpritelessNpcs(string context)`:
  - `Utility.ForEachLocation(..., includeInteriors: true)` over `location.characters` — married
    spouses live in cabin/farmhouse interiors. All `NPC` subtypes are eligible (a Sprite-null
    Pet/Child/Horse breaks other engine paths the same way), not just villagers.
  - For each `npc.Sprite == null`:
    - `npc.Sprite = new AnimatedSprite();` (property assignment — same shape `TryLoadSprites` uses;
      the `NetRef` replicates the healed sprite to clients, whose draw takes the error-box path
      instead of NREing at `NPC.cs:5069`).
    - Size from data, mirroring `routeEndAnimationFinished`'s own fallbacks: `SpriteWidth =
      GetData()?.Size.X ?? 16`, `SpriteHeight = GetData()?.Size.Y ?? 32`, then `UpdateSourceRect()`.
    - **Do not set `textureName`.** A broken name would throw in every client's lazy `Texture` getter
      (`AnimatedSprite.cs:64-71,220-232` — `loadTexture` has no exists-gate and no catch); a null
      name is exactly what vanilla produces for missing assets. Self-heal is free: the engine's daily
      `dayUpdate → ChooseAppearance → TryLoadSprites → Sprite.LoadTexture(...)` (`NPC.cs:1278`)
      restores the real texture the day the content is fixed.
    - `Monitor.Log(..., LogLevel.Warn)` naming NPC, location, and `getTextureName()` (**Warn, not
      Error** — `.claude/rules/debugging.md`: Error is test poison), and
      `Diagnostics.ModEventLog.Emit("npc_sprite_healed", new { npcName, location, textureName,
      context })` — mirrors `npc_rehomed` (`CabinManagerService.cs:1697-1709`).
  - Track last-run metadata (context, healed count) in fields for the test-status endpoint.

**Deliberately not doing** (with reasons, so nobody re-adds them):
- No Harmony guard on `routeEndAnimationFinished`/`checkSchedule`/`performTenMinuteUpdate`: with no
  mid-day Sprite writer in the engine (grep-verified) the load/day boundaries are complete coverage;
  a guard would defend against nothing real and add a per-boundary patch to maintain.
- No `ChooseAppearance` postfix: it's virtual with seven overrides (Pet, Horse, Child, Junimo,
  Monster, TrashBear, JunimoHarvester), Harmony only hits the base implementation, and load-time
  calls run off the main thread; the sweep avoids all of that and matches the in-tree heal pattern.
- No portrait heal: `Portrait` is `[XmlIgnore]`, loaded per-machine, unused by any server-side code
  path (the server opens no dialogue UI); clients load their own.
- No NPC removal (breaks marriages/quests for an NPC that is otherwise fully functional).

### Test surface (`ApiService.TestEndpoints.cs`, existing `/test/*` switch + `Env.IsTest` gate)

- `GET /test/npc_sprite_integrity` → `{ lastRunContext, lastRunHealedCount, spritelessNpcs: [names] }`
  (live scan + service metadata). The metadata assertion proves the SaveLoaded *wiring* fired, not
  just that the heal logic works when invoked — closes the bypass-path gap
  (`.claude/rules/universal/passing-test-isnt-proof-the-scenario-ran.md`).
- `POST /test/break_npc_sprite` `{ "npcName": "..." }` → nulls that NPC's `Sprite` on the game thread
  (`RunOnGameThreadAsync`), returning prior state. This is the same inject-synthetic-state-for-a-sweep
  pattern as `/test/stamp_claim` / `/test/stamp_lobby_home`.
- `POST /test/heal_npc_sprites` → invokes the same `HealSpritelessNpcs("test")` the event handlers
  call; returns healed count. (Service exposed to ApiService via DI, like other services.)

## Implementation steps

1. **Commit layer 1** (`fix(docker): restore libfontconfig1 for SkiaSharp image decoding`) with the
   boot + `ldd` verification recorded in the PR. Independent PR; ship first.
2. **`NpcSpriteIntegrityService`** as specified above (new folder `Services/NpcIntegrity/`).
3. **Test endpoints** (3 routes + models per existing file conventions; follow the dispatcher's
   registration list so unknown-route behavior stays consistent).
4. **E2E tests** — new `tests/JunimoServer.Tests/NpcSpriteIntegrityTests.cs`:
   - `BrokenSprite_IsHealed_AndClockAdvances`: `/test/set_date` (fresh 6:00 day — no schedule entry
     due in the first boundaries, so the break→heal window is safe), `POST /test/break_npc_sprite`
     for a scheduled Town villager (e.g. Abigail), immediately `POST /test/heal_npc_sprites`; assert
     healed count 1 and `spritelessNpcs` empty; then assert `timeOfDay` (existing `/status`-family
     endpoint) advances across ≥2 ten-minute boundaries — the incident's failure mode is "frozen",
     so time-advance is the load-bearing assertion. Time flows only with a connected player: lease a
     pooled client (standard `KeepConnected` pattern) or use the suite's established time-advance
     primitive.
   - `Sweep_RunsOnReload_HealthySaveHealsNothing`: `/reload`, then assert
     `lastRunContext == "save_loaded"` and `lastRunHealedCount == 0` — proves subscription wiring AND
     that healthy saves are untouched (regression guard for the whole suite).
   - Assert via HTTP API only (`.claude/rules/tests-assert-via-http-api.md`); the
     `npc_sprite_healed` event is diagnostics.
   - *Optional follow-up (not this PR):* full load-seam fidelity via an `Env.IsTest`-gated
     `AssetRequested` fault (custom `TextureName` + throwing loader + `/reload`). Prerequisite to
     verify first: SMAPI must propagate a crashing sole-loader as `ContentLoadException` rather than
     degrade to asset-missing — if it degrades, the shell never forms and the test is unbuildable.
     The decompile-verified `TryLoadSprites` behavior plus the two tests above already cover the
     shipped code paths.
5. **Docs**: one line in the events schema doc for `npc_sprite_healed` if the catalog lists mod
   events (follow `event-catalog-no-inline-enums.md` — reference the emitting class).

## Pre-implementation verification checklist (claims to re-confirm in-code before writing)

- `Game1.warpCharacter` / `NPC.arriveAt*` touch nothing on `Sprite` (input doc verified this; cheap
  re-read) — makes SaveLoaded handler ordering vs `CabinManagerService`'s NPC-warping heals
  irrelevant (services subscribe in alphabetical `StartServices` order; CabinManager runs first).
- `Utility.ForEachLocation(Func<GameLocation,bool>, includeInteriors)` exact signature.
- The exact status endpoint field for `timeOfDay` used by existing tests.
- OpenAPI generator treatment of `/test/*` routes (they're outside the `[ApiEndpoint]` attribute
  surface — confirm new routes don't need spec entries, matching existing test endpoints).

## Compatibility verification (per `plan-discipline.md`)

- **Transports (LAN/Steam):** heal touches only NPC state; no transport interaction. Tests run on
  default LAN harness.
- **`SERVER_TPS=5` / FPS 0:** sweep is event-driven (twice per day), no per-tick work; satisfies
  `mod-game-thread-allocation.md` (allocations only on the rare heal path).
- **Lobby/cabin interiors:** covered by `includeInteriors: true`; lobby-homed NPC heals (#474) run
  before or after harmlessly (no `Sprite` interaction either direction).
- **Clients:** healed sprite replicates via the `sprite` NetRef → clients render vanilla's error box
  instead of NREing on a null `Sprite`; a client that has the working content pack still gets its
  texture back the day the server's content heals (textureName re-syncs from
  `LoadTexture(assetName, IsMasterGame: true)`).
- **`multiplayerMode = 2`:** sweep runs on the master by construction
  (`masterplayer-is-player-on-server.md` — no host/master split to consider).
- **Save flow / day transition:** SaveLoaded and DayStarted both run outside the overnight
  save window; the heal never mutates during serialization.

## Post-conditions (runtime gates, not static checks)

1. `make test FILTER=NpcSpriteIntegrityTests` green, **and** the run artifact shows the heal Warn
   line + `npc_sprite_healed` event exactly once in the break test and zero heals in the reload test
   (read the server container log, per `passing-test-isnt-proof-the-scenario-ran.md`).
2. Full suite unaffected (sweep no-ops on every healthy load — gate 1's reload test asserts it, the
   suite confirms at scale).
3. Layer 1: rebuilt image boots and `ldd` on `libSkiaSharp.so` resolves cleanly inside the container.
4. Field: reporter (or a local repro with a deliberately broken content pack) confirms the server
   now logs the heal, keeps the clock advancing, and shows error-box NPCs instead of freezing.

## Out of scope / follow-ups

- **Upstream report to ConcernedApe/SMAPI**: `TryLoadSprites` leaving `Sprite` null +
  `routeEndAnimationFinished`'s unguarded deref is a vanilla bug pair worth reporting.
- **Optional operator preflight**: a startup warning when `libSkiaSharp.so`'s dynamic deps don't
  resolve (protects modded servers against the whole "image lib removed" class). Only if wanted —
  layer 1's explicit dependency + comment is the primary defense.
- The broader CP `EditImage` breakage from missing SkiaSharp is fully covered by layer 1; nothing
  mod-side to do.
