# Plan: Per-Player Farms v1 (farm-stack)

> **DRAFT — pending owner review.** Grounded in [`../research/farm-stack-findings.md`](../research/farm-stack-findings.md); engine claims there are decompiled-verified, and the control primitives cited below are production-proven in this mod. Open questions at the bottom are decisions, not TODOs.

## Goal

Each player gets their own `Farm`-type location (`Farm_JS_<n>`) with their own main farmhouse on it. Private farmland: own crops, animals, buildings, layout. Money, town, NPCs, festivals, and world progression stay shared. Players live on their own farm; `!visit <playername>` enters another player's farm.

## Locked decisions (owner, 2026-07-13)

- Allocation is **automatic per player**, gated by a setting that is **first-time-startup-only**: stamped into mod save data at world creation; env changes on an existing save are ignored (log a warning, keep the stamped value). No enable/disable migration.
- **No game-logic gaps**: every category of the findings' 106-call-site audit gets an explicit disposition — none skipped silently.
- Every player lives in their **own main farmhouse**, not a visible cabin shack.
- KISS: no permission system, no per-farm settings in v1.

## Ground rules imposed by vanilla clients

Players connect unmodded, so (verified in findings, "vanilla-client constraint" section):

1. Harmony patches run **server-side only**. Server-simulated logic (night events, NPC movement, overnight processing, save/load) is patchable; client-side resolution (menus, totem targets, local warp resolution, mailbox interaction) is not.
2. The only server-initiated player warp is the **passout message** (`FarmerExtensions.WarpHome` pattern) — clean only when the player's source location is `FarmHouse`-derived/`Cellar`/`PassOutSafe`, else the client charges a pass-out fee.
3. **Server-side `location.warps` mutations reach vanilla clients** (proven by the lobby's warp removal/rewrites).
4. Every client warp is observed server-side via message 5.
5. The primary `"Farm"` must stay always-active and populated — client-side `Game1.getFarm()` (world map, shipping UI, menu defaults) throws on clients that don't hold it.

Consequence: `"Farm"`-named flows (bus-stop walk-back, warp totems, map screen) inevitably land vanilla clients on the primary farm. The design embraces that: **the primary farm becomes the communal hub**, and player farms hang off it via gate warps.

## World geometry

- **Primary farm = communal hub.** Host farmhouse (internal-only, unchanged), greenhouse, grandpa shrine, pets, shipping-bin store, and the per-player **gate row**: one warp gate per player farm (server-added warp tiles with sign/fence dressing), each targeting `Farm_JS_<n>`'s entrance. All vanilla `"Farm"`-targeted arrivals land here and players walk into their gate.
- **Player farms** (`Farm_JS_<n>`, `Data/Locations` entries with `CreateOnLoad.Type = "StardewValley.Farm"`, `AlwaysActive: true`, same farm-type map as the world): the player's farmhouse, their farmland, their buildings. Map-edge exits keep their vanilla targets (BusStop/Backwoods/Forest) plus a rewritten warp back to the hub gate row — per-farm `warps` are server-authoritative and sync per location.
- Same farm type for all farms in v1 (`Farm.DayUpdate` keys spawns off global `whichFarm`, `Farm.cs:342` — matching types sidesteps that).

## Housing, farmhand slots, and the join lifecycle

This section is the core contract; invariant numbers refer to `.claude/rules/cabin-system.md`.

### Why the house must be a Cabin under the hood

Farmhand slots exist **only** via `Cabin.CreateFarmhand()` (`Cabin.cs:46-65`, fired by construction/load when a building's indoors is an ownerless `Cabin`, `Building.cs:1220-1222`; invariant 6), and `TryAssignFarmhandHome` hard-checks `is Cabin` — a farmhand homed in a plain `FarmHouse` gets `userID`/`homeLocation` wiped (`NetWorldState.cs:769-783`). So each player farm gets **one building at `Farm.GetStarterFarmhouseLocation()` whose interior is a `Cabin`**, presented as the main farmhouse.

**Appearance** (vanilla clients render buildings from their *own* `Data/Buildings`; server-side data edits don't reach them):
- Primary candidate: a building of vanilla type `"Farmhouse"` (clients have its visuals) with an **instanced `Cabin` set as its `indoors`**. Vanilla's non-instanced-indoors binding guard leaves later `"Farmhouse"` buildings without the singleton `FarmHouse` interior (`Building.cs:1973-1988`), and instanced `indoors` is mutually exclusive with that path (`Building.cs:50-56`) — so the combination is structurally plausible but novel. **Phase 0 must prove it** (door warp, interior sync, upgrades, `CreateFarmhand` firing).
- Fallback if that fails: a plain cabin building (vanilla skin) at the farmhouse spot — full mechanics, weaker looks.
- Suppress the default Farmhouse/Greenhouse shell buildings on player farms (patch `Farm.AddDefaultBuildings` for non-primary farms); keep Shipping Bin and Pet Bowl dispositions per the open questions.

### World creation (GameCreator, farms mode stamped ON)

1. Stamp mode + farm capacity `N` (from server config max players) into mod save data. Implemented as a new `CabinStrategy` value so existing config/stamping plumbing is reused; `DetectAndMigrateStrategyChange` **refuses** migration into/out of farms mode (log, keep stamp).
2. Mod registers `Farm_JS_1..N` `Data/Locations` entries on every boot for the stamped capacity (entries are idempotent asset edits; the count comes from the stamp, so a save always finds its farms).
3. After `loadForNewGame()` (cabins don't survive map realization — invariant 9): on each player farm, build the farmhouse-cabin via the mod's `CreateCabinBuilding` path with interior-created checks and **exact-count asserts**; install gate warps on the hub and the hub-return warps on each farm. `BuildStartingCabins` stays patched out (as for the stack strategies, invariant 1); primary farm gets zero player cabins.
4. Each farmhouse-cabin auto-creates its unclaimed farmhand, homed at that cabin (`homeLocation` = cabin unique name, `Cabin.cs:61`). **Farm ownership needs no new mapping**: farmhand → `homeLocation`/`farmhandReference` → cabin → parent farm.

### Load preflight (mandatory)

On save load, compare saved `Farm_JS_*` locations against the data entries about to be provided; on mismatch **fail loudly before load** — vanilla silently discards a saved location with all contents (`SaveGame.cs:1413-1426`). Verify the preflight passes on existing single-farm saves (zero player farms is a valid state, per `verify-claims`).

### Join flow (new and returning players)

Unchanged steps are listed to prove they carry over — the lobby redirect only rewrites *spawn* fields and deliberately leaves `homeLocation` pointing at the real cabin (`FarmhandSenderService.cs:434-490`), which is the seam that makes cabins-on-player-farms flow through:

1. Connect → `SendAvailableFarmhands_Prefix`: reservation pruning, `EnsureAtLeastXCabins(reserved+1)` — in farms mode this becomes a **check** ("≥1 unclaimed farmhouse-cabin across player farms"), since the pool is fixed at capacity; exhausted pool ⇒ no unclaimed slot offered (server full). Filtering/single-slot limiting/reservations unchanged (invariants 3–4).
2. Client picks the offered farmhand → vanilla approval (`authCheck`, userID stamp) unchanged. Always-active player farms are pushed to the client at join (`GameServer.cs:579-601`) — the client holds every farm, so gate warps and passout warps resolve client-side.
3. Player spawns in the lobby (spawn-data redirect, unchanged), customizes, authenticates.
4. Lobby exit → `WarpHome` passout-warp to their farmhouse-cabin **on their farm** (fee-free: source is the lobby cabin). `WarpHome` must widen from `Game1.getFarm().GetCabin(...)` to an all-farms owned-cabin lookup (`Util/FarmerExtensions.cs:12`).
5. Day-start wake, sleep, pass-out, scepter: all resolve via `homeLocation`/current location — location-agnostic vanilla behavior, no changes.
6. Disconnect: two-Farmer-object rules and the abandoned-claim heal + load sweep apply unchanged (invariants 7–8; all keyed on `farmhandData`, not location).

### Home-integrity guard (new)

Vanilla's `TryAssignFarmhandHome` last-resort fallback assigns **any** ownerless cabin via `Utility.ForEachBuilding` (`NetWorldState.cs:798-807`). With unclaimed farmhouse-cabins on multiple farms, a farmhand with a broken `homeLocation` could silently cross-assign onto another farm. Add a farms-mode prefix mirroring vanilla's full resolution order (homeLocation → currentLocation → lastSleepLocation → fallback, per `mirror-target-component-resolution`) that resolves via the cabin's persisted `farmhandReference.uid` (`FindOwnedCabin` pattern) **before** any unclaimed-pool fallback; true first-joins (no owned cabin anywhere) fall through unchanged.

### Primary-farm scan widening (complete list, from findings)

`FarmerExtensions.WarpHome:12`, `FarmhandSenderService.IsLobbyCabinFarmhand:512`, `CabinManagerService` (`ClearStaleFarmhandReferences:230`, `SyncExistingCabins:485`, `GetAvailableCabinCount:804`, `FindCabinInteriorByName:1724`, `FindOwnedCabin:1755`, `HealLobbyHomedResidents:1540`), `AlwaysOn.LockAllFarmhandStorage:1440` / `ReleaseOnlineFarmhandStorage:1504`, `ApiService.SnapshotCabins:1690`, `ApiService.ExecuteFarmhandDeletion:4595`. Widen to `Utility.ForEachBuilding` / owned-cabin lookups; lobby-cabin classification by hidden-tile coordinates stays primary-farm-scoped (lobby cabins never leave it).

### Deletion (`DELETE /farmhands`)

Delete the farmhand (existing flow, invariant 7 write rules), keep the farmhouse-cabin building, reset the cabin interior, and **wipe the farm's state in place** (clear objects/terrain/animals/non-house buildings on that farm's net collections — no location recreate, so no mid-session location-sync questions), then re-arm the slot (`CreateFarmhand` — mind invariant 6's no-double-create rule). Invariant 2's "ensure a free slot after deletion" is satisfied by the reset itself. Wipe semantics are an open question (below) if the owner prefers preservation.

### Capacity

Stamped farm count = hard capacity. No runtime farm creation in v1 (mid-session location creation + client sync is unproven); raising capacity is a restart-with-bigger-stamp operation only if the owner later asks for it — out of scope v1, documented in admin docs.

## `!visit <playername>`

Server command using the passout-warp primitive. Because the warp is fee-free only from `FarmHouse`-derived/`Cellar` locations, v1 **restricts `!visit` to indoors-at-home** (any FarmHouse/Cabin/Cellar the player stands in); issued elsewhere it replies "use it from inside a house". Walking into someone's hub gate is the outdoor path (open question: gate restrictions). `!visit` warps to the target farm's entrance; return is by walking (farm exits → shared world / hub) or `!visit <own name>`. Server tracks nothing persistent — the warp is one-shot, and message-5 observation covers diagnostics.

## Per-farm game logic (the "no gaps" audit)

Every audit category from the findings, with execution side and v1 disposition:

| Concern | Executes | v1 disposition |
|---|---|---|
| Night events (witch/fairy/meteor/animal) | server | Patch the picker/events to roll per player farm (semantics: open question) |
| Overnight shipping loop | server | **Keep vanilla.** Client-side bin deposits already route to the primary farm's bin store (`ShippingBin.cs:71` caches `getFarm()`), so client and server are consistent; money is pooled regardless. Bins on player farms work — they open the shared store |
| Spouse patio / marriage | server (NPC sim) | Patch patio anchor to the spouse-owner's farm (`NPC.cs:6254-6262`); house-level ≥ 1 marriage rule applies to cabins as usual |
| Robin construction warp | server (NPC sim) | Patch to warp Robin to the farm under construction (`NPC.cs:1297`) |
| Mailbox | client + server | Mail *data* is per-player and synced. Client-side `getMailboxPosition` scans only the primary farm (`Farmer.cs:2624-2634`) — whether the mailbox tile on the player's own farm functions on vanilla clients is a **Phase 0 question**; fallback: hub mailbox is the served mailbox (documented) |
| Warp totems / return scepter | client | Land on the hub (vanilla); walk/gate from there. Scepter targets `homeLocation` → own farm — verify in Phase 0 |
| Hay/silos | server + client interact | Keep primary-farm silos as the shared store v1 (consistent both sides); per-farm silos deferred |
| Carpenter/animal menus | client | Vanilla 1.6 already lets players pick any buildable held location — player farms qualify (`GameLocation.cs:11473-11491`, `Farm.IsBuildableLocation`). Cabin building off-"Farm" is blocked client-side (`CarpenterMenu.cs:1232`) — desirable here. **Gap: any player can build on any player's farm**; server-side construction veto is an open question |
| Grandpa evaluation, lightning, Island shipping | server | Stay global on the hub (communal), documented |
| Save migrations, debug commands | server | Operate on the primary farm only; verified non-crashing with extra farms — no action |
| World map (`MapPage`), shipping UI | client | Requires `"Farm"` always-active (ground rule 5) — satisfied by hub design; no action possible or needed |

## Implementation phases

1. **Phase 0 — runtime prototype (gates everything):** on a dev server with 2 vanilla clients: (a) provision `Farm_JS_1..2` via data entries, verify join payload/sync and save round-trip incl. the preflight; (b) prove the `"Farmhouse"`-building-with-instanced-`Cabin`-indoors combination (door, interior sync, `CreateFarmhand`, upgrade), else adopt the cabin fallback; (c) prove gate-warp **addition** on the hub and farm-edge rewrite; (d) prove lobby-exit passout-warp onto a player farm and `!visit` from indoors; (e) answer the own-farm mailbox-tile question; (f) verify scepter/totem/pass-out flows.
2. **Phase 1 — core:** stamped strategy + provisioning + housing + preflight + scan widening + home-integrity guard + hub gates + `!visit`.
3. **Phase 2 — per-farm logic:** night events, spouse patio, Robin warp, deletion/farm-reset, construction veto (if decided).
4. **Phase 3 — surfaces:** API farm dimension (`/cabins` + `Farm` field, `/farmhands` farm name; `runner-ui-pipeline-plumbing` applies), E2E suites (join lifecycle, two-client visit, deletion/reclaim, save/reload with N farms, import), admin/player docs.

## Compatibility verification (to complete before implementation sign-off)

- LAN vs Steam vs Galaxy transports: join flow (reservations, lobby, passout warp) is transport-agnostic today — re-verify with farms mode; LAN `getUserID()` is `""` (selectability rules unchanged).
- Passwordless config: farms-mode patches must live in unconditionally-constructed services (`harmony-patch-reachability`).
- `SERVER_TPS=5`: no new per-second-loop handlers; provisioning is load-time; message-5 observation is event-driven.
- Disconnect mid-lobby, mid-visit, mid-warp; day transition with a visitor on a foreign farm; wedding/festival days (event warps target town; verify return warps with per-farm homes).
- Save/reload with 0, 1, N player farms; `saves import` of a vanilla save (single-farm save + farms-mode stamp = farms provisioned per capacity; imported owner homed per existing Layer B, then widened lookups apply).
- Host automation: `WarpToFarmDefaultSpawn`/`MonitorFarmhouse`/host sleep unchanged on the hub — verify no host flow references player farms.

## Open questions (owner decisions, not yet settled)

1. **Hub geometry vs "enter own farm by default".** The vanilla-client constraint means walking back from the bus stop lands players on the hub, one gate-walk from home — not directly on their farm as originally requested. Direct arrival-redirect exists only via the passout message, which charges a fee from outdoor sources; a fee-refund + mail-scrub compensation hack is possible but ugly. Accept hub-with-gates for v1 (recommended), or fund the compensation experiment in Phase 0?
2. **Night-event semantics**: one roll per farm per night (vanilla feel per player, N× server-wide frequency) or one event on one randomly-chosen farm per night?
3. **Construction permissions**: vanilla lets any player build on any buildable location, including others' farms. Accept for v1, or add a server-side construction veto (master executes construction, so a server check is possible)?
4. **Deletion wipe semantics**: wipe the farm for the next claimant (recommended, matches cabin-reset behavior) or archive/preserve?
5. **Pets/greenhouse**: both communal on the hub in v1 (vanilla pet roster and greenhouse interior are effectively singletons) — acceptable?
6. **Separate wallets**: leave vanilla-shared, or enable at world creation when farms mode is on?
7. **Gate access**: gates open to everyone (visiting by walking, matching "flesh out later") or `!visit`-only (gates warp only the farm owner)?
