# Plan: Per-Player Farms v1 (farm-stack)

> Grounded in [`../research/farm-stack-findings.md`](../research/farm-stack-findings.md) and a full decompiled + mod-source trace; engine claims are decompiled-verified, control primitives are production-proven, and the housing/upgrade/mail/scepter/event-return paths were traced to source. Net-warp *addition* for walkable gates is the one runtime unknown and is out of v1, so v1 rests only on proven primitives. Phase-0 smoke tests are listed under Implementation phases.

## Goal

Each player gets their own `Farm`-type location (`Farm_JS_<n>`) with their own main farmhouse on it. Private farmland: own crops, animals, buildings, layout. Money, town, NPCs, festivals, and world progression stay shared. Players live on their own farm; `!visit <playername>` enters another player's farm.

## Locked decisions

- Allocation is **automatic per player**, gated by a setting that is **first-time-startup-only**: stamped into mod global data (`PersistentOptions`, on the saves volume) at world creation; env changes on an existing save are ignored (log a warning, keep the stamped value). No enable/disable migration.
- **No game-logic gaps**: every category of the findings' 106-call-site audit gets an explicit disposition — none skipped silently.
- Every player lives in their **own main farmhouse**, not a visible cabin shack.
- KISS: no permission system, no per-farm settings in v1.

**Build decisions:**

- **Housing look:** the Cabin interior is wrapped in a vanilla `"Farmhouse"`-type building so clients render farmhouse visuals (honors "own farmhouse, not a cabin shack"). Proven to work (housing section) but requires migrating the farms-relevant cabin-enumeration from `b.isCabin` to `b.GetIndoors() is Cabin` — a `"Farmhouse"`-type building is `isCabin == false`.
- **Travel:** `!visit`-only in v1 — **no walkable gates.** Every client-side return path (`Return Scepter`, Farm warp totem, festival/wedding end) hardcodes `"Farm"` and lands players on the hub regardless; the 2am pass-out (server-authoritative) returns stragglers to their own bed. Walkable hub gates need net-warp *addition* (unproven) — deferred to a fast-follow.
- **Wallets:** **both** shared and separate supported, chosen at world creation and stamped (`FarmerTeam.useSeparateWallets`), first-startup-only like the other settings.
- **Night events:** one event per night on a randomly chosen player farm (shared-daily-seed retarget of the single vanilla `farmEvent`). Per-farm rolls are deferred — each event's effect is welded to a global-`Game1`-state-hijacking animation, so N rolls would mean per-event effect extraction.
- **Construction:** any player may build on any player's farm in v1 (vanilla behavior); a server-side veto is a fast-follow.
- **Deletion:** wipe the farm in place for the next claimant.
- **Pets & greenhouse:** communal on the hub (vanilla singletons).
- **Per-farm buildings:** keep the auto-added per-farm Shipping Bin (money pooled); suppress Greenhouse; silos shared on the hub.

## Ground rules imposed by vanilla clients

Players connect unmodded, so (verified in findings, "vanilla-client constraint" section):

1. Harmony patches run **server-side only**. Server-simulated logic (night events, NPC movement, overnight processing, save/load) is patchable; client-side resolution (menus, totem targets, local warp resolution, mailbox interaction) is not.
2. The only server-initiated player warp is the **passout message** (`FarmerExtensions.WarpHome` pattern) — clean only when the player's source location is `FarmHouse`-derived/`Cellar`/`PassOutSafe`, else the client charges a pass-out fee.
3. **Server-side `location.warps` mutations reach vanilla clients** (proven by the lobby's warp removal/rewrites).
4. Every client warp is observed server-side via message 5.
5. The primary `"Farm"` must stay always-active and populated — client-side `Game1.getFarm()` (world map, shipping UI, menu defaults) throws on clients that don't hold it.

Consequence: `"Farm"`-named flows (bus-stop walk-back, warp totems, Return Scepter, festival/wedding end, map screen) inevitably land vanilla clients on the primary farm. The design embraces that: **the primary farm becomes the communal hub**, and players reach their own farm via `!visit` (or the server-authoritative pass-out).

## World geometry

- **Primary farm = communal hub.** Host (the headless "Server" bot) farmhouse (internal-only, unchanged), greenhouse, grandpa shrine, pets, and shipping-bin store. All vanilla `"Farm"`-targeted arrivals (bus-stop walk-back, warp totem, Return Scepter, festival/wedding end) land here — **the vanilla client hardcodes them.** Players leave the hub for their own farm via `!visit <own name>` (from inside any house, fee-free) or are delivered home by the 2am pass-out. (Walkable hub gates are a deferred fast-follow, pending the net-warp-addition experiment.)
- **Player farms** (`Farm_JS_<n>`, `Data/Locations` entries with `CreateOnLoad.Type = "StardewValley.Farm"`, `AlwaysActive: true`, same farm-type map as the world): the player's farmhouse, their farmland, their buildings. Map-edge exits keep their vanilla targets (BusStop/Backwoods/Forest), which loop back to the shared world and thence the hub — no per-farm warp rewrites needed in the `!visit`-only design.
- Same farm type for all farms in v1 (`Farm.DayUpdate` keys spawns off global `whichFarm`, `Farm.cs` — matching types sidesteps that).

## Housing, farmhand slots, and the join lifecycle

This section is the core contract; invariant numbers refer to `.claude/rules/cabin-system.md`.

### Why the house must be a Cabin under the hood

Farmhand slots exist **only** via `Cabin.CreateFarmhand()` (`Cabin.cs`, fired by construction/load when a building's indoors is an ownerless `Cabin`, `Building.cs`; invariant 6), and `TryAssignFarmhandHome` hard-checks `is Cabin` — a farmhand homed in a plain `FarmHouse` gets `userID`/`homeLocation` wiped (`NetWorldState.cs`). So each player farm gets **one building at `Farm.GetStarterFarmhouseLocation()` whose interior is a `Cabin`**, presented as the main farmhouse.

**Appearance** — decided: a building of vanilla type `"Farmhouse"` (clients render its visuals from their own `Data/Buildings`) with an **instanced `Cabin` set as its `indoors`**. This is **resolved to work statically**, not merely plausible: `"Farmhouse"` building data has `IndoorMap: null`, so `Building.load()`/`LoadFromBuildingData`/`createIndoors` never fabricate or migrate an interior over ours, and `GetIndoors()`/`GetIndoorsType()` prefer the instanced `indoors.Value` — the Cabin survives load in both binding branches. The door warp uses `GetIndoors()` (so it reaches the Cabin, `isStructure == true`), `updateInteriorWarps` retargets the Cabin's exit to `GetParentLocation()` (`Farm_JS_<n>`), and upgrades/cellars resolve generically via `homeLocation`/`OwnerId` (farm-independent). Two residual runtime checks remain for Phase 0: (i) the *replication* of an on-`Farm_JS_<n>` upgrade map/object swap to peers, and (ii) that the degenerate dual-set state (`indoors` + `nonInstancedIndoorsName` both set, if nothing else claims `"FarmHouse"`) never bites — `GetIndoors()` returns the Cabin regardless, but confirm no `nonInstancedIndoorsName` consumer misreads it.

**Consequence — `isCabin` migration:** `Building.isCabin` is `buildingType == "Cabin"`, so a `"Farmhouse"`-type building is `isCabin == false` and is **invisible to the mod's `b.isCabin`-keyed cabin enumeration**. The farms-relevant sites (the scan-widening list below, plus the join filter) must switch to a shared `IsPlayerCabin(Building)` = `b.GetIndoors() is Cabin` predicate. Vanilla itself enumerates farmhand cabins via `GetIndoors() is Cabin`, so this aligns with the engine.

- Suppress the default shell buildings on player farms by patching `Farm.AddDefaultBuildings` for non-primary farms. This is **correctness, not just looks**: `AddDefaultBuildings` also runs on the load path (`SaveGame` finalizer), and `AddDefaultBuilding` is idempotent by *building type only*, so absent the patch each player farm re-acquires an indoor-less shell on every load. The mod's `"Farmhouse"`-type farmhouse-cabin self-satisfies the `"Farmhouse"` default (so it is not re-added), so only **Greenhouse** must be suppressed; keep the auto-added **Shipping Bin** (money pooled) and suppress **Pet Bowl** (pets communal on the hub).

### World creation (GameCreator, farms mode stamped ON)

1. Stamp mode + farm capacity `N` (from server config max players) into the `PersistentOptionsSaveData` store — SMAPI global data on the saves volume (key `JunimoHost.PersistentOptions`, the same store that already holds `MaxPlayers`/`CabinStrategy`/`NoneCabinCount`; add a farms-capacity field alongside `NoneCabinCount`). Implemented as a new `CabinStrategy` value so existing config/stamping plumbing is reused; extend `DetectAndApplyStrategySwitch` / `RequiresStagedMigration` to **refuse** migration into/out of farms mode (log, keep stamp). Stamp the **wallet mode** (shared vs separate) alongside and apply it at world creation via `Game1.player.team.useSeparateWallets`.
2. Mod registers `Farm_JS_1..N` `Data/Locations` entries on every boot for the stamped capacity (idempotent asset edits). The count MUST come from the persisted stamp (not live config) for an existing save, or the entry set drifts from what the save baked in and the preflight below trips. Ordering is load-bearing: the `Data/Locations` edit must be in place before `loadForNewGame` → `AddLocations()` runs on every load, and on the first `/newgame` the asset must be invalidated after the stamp is written (in `CreateNewGameCore`, before `loadForNewGame`) so the just-stamped capacity is realized into the new save.
3. After `loadForNewGame()` (cabins don't survive map realization — invariant 9): on each player farm, build the farmhouse-cabin (a `"Farmhouse"`-type building with an instanced `Cabin` indoors — see housing section) via the mod's cabin-building path with interior-created checks and **exact-count asserts** (no gate warps in the `!visit`-only design). `BuildStartingCabins` stays patched out (as for the stack strategies, invariant 1); primary farm gets zero player cabins.
4. Each farmhouse-cabin auto-creates its unclaimed farmhand, homed at that cabin (`homeLocation` = cabin unique name, `Cabin.cs`). **Farm ownership needs no new mapping**: farmhand → `homeLocation`/`farmhandReference` → cabin → parent farm.

### Load preflight (mandatory)

On save load, compare saved `Farm_JS_*` locations against the data entries about to be provided; on mismatch **fail loudly before load** — vanilla silently discards a saved location with all contents (`SaveGame.cs`). Verify the preflight passes on existing single-farm saves (zero player farms is a valid state, per `verify-claims`).

### Join flow (new and returning players)

Unchanged steps are listed to prove they carry over — the lobby redirect only rewrites *spawn* fields and deliberately leaves `homeLocation` pointing at the real cabin (`FarmhandSenderService.cs`), which is the seam that makes cabins-on-player-farms flow through:

1. Connect → `SendAvailableFarmhands_Prefix`: reservation pruning, `EnsureAtLeastXCabins(reserved+1)` — in farms mode this becomes a **check** ("≥1 unclaimed farmhouse-cabin across player farms"), since the pool is fixed at capacity; exhausted pool ⇒ no unclaimed slot offered (server full). Filtering/single-slot limiting/reservations unchanged (invariants 3–4).
2. Client picks the offered farmhand → vanilla approval (`authCheck`, userID stamp) unchanged. Always-active player farms are pushed to the client at join (`GameServer.cs`) — the client holds every farm, so `!visit`/passout warps resolve client-side.
3. Player spawns in the lobby (spawn-data redirect, unchanged), customizes, authenticates.
4. Lobby exit → `WarpHome` passout-warp to their farmhouse-cabin **on their farm** (fee-free: source is the lobby cabin). `WarpHome` must widen from `Game1.getFarm().GetCabin(...)` to an all-farms owned-cabin lookup (`Util/FarmerExtensions.cs`).
5. Day-start wake, sleep, pass-out, scepter: all resolve via `homeLocation`/current location — location-agnostic vanilla behavior, no changes.
6. Disconnect: two-Farmer-object rules and the abandoned-claim heal + load sweep apply unchanged (invariants 7–8; all keyed on `farmhandData`, not location).

### Home-integrity guard (new)

Vanilla's `TryAssignFarmhandHome` last-resort fallback assigns **any** ownerless cabin via `Utility.ForEachBuilding` (`NetWorldState.cs`). With unclaimed farmhouse-cabins on multiple farms, a farmhand with a broken `homeLocation` could silently cross-assign onto another farm. Add a farms-mode prefix mirroring vanilla's full resolution order (homeLocation → currentLocation → lastSleepLocation → fallback, per `mirror-target-component-resolution`) that resolves via the cabin's persisted `farmhandReference.uid` (`FindOwnedCabin` pattern) **before** any unclaimed-pool fallback; true first-joins (no owned cabin anywhere) fall through unchanged.

### Primary-farm scan widening (complete list, from findings)

`FarmerExtensions.WarpHome`, `FarmhandSenderService.IsLobbyCabinFarmhand`, `CabinManagerService` (`ClearStaleFarmhandReferences`, `SyncExistingCabins`, `FindCabinInteriorByName`, `FindOwnedCabin`), `AlwaysOn.LockOfflineFarmhandStorage` / `ReleaseOnlineFarmhandStorage`, `ApiService.SnapshotCabins`, `ApiService.ExecuteFarmhandDeletion`. Widen each to `Utility.ForEachBuilding` / all-farms owned-cabin lookups. Two sites already carry the seam and only need widening at the call site, not internally: `GetAvailableCabinCount(GameLocation, ...)` is already farm-parametrized (its caller `EnsureAtLeastXCabins` is what passes `getFarm()`), and `HealLobbyHomedResidents` already sweeps villagers globally via `Utility.ForEachVillager` — only its cabin-building/name lookups are primary-farm-scoped. Lobby-cabin classification by hidden-tile coordinates stays primary-farm-scoped (lobby cabins never leave it). Each of these sites also switches its `b.isCabin` test to the `IsPlayerCabin(b)` = `b.GetIndoors() is Cabin` predicate (housing section) so it recognises the `"Farmhouse"`-skin farmhouse-cabins.

### Deletion (`DELETE /farmhands`)

Delete the farmhand (existing flow, invariant 7 write rules), keep the farmhouse-cabin building, reset the cabin interior, and **wipe the farm's state in place** (clear objects/terrain/animals/non-house buildings on that farm's net collections — no location recreate, so no mid-session location-sync questions), then re-arm the slot (`CreateFarmhand` — mind invariant 6's no-double-create rule). Invariant 2's "ensure a free slot after deletion" is satisfied by the reset itself.

### Capacity

Stamped farm count = hard capacity. No runtime farm creation in v1 (mid-session location creation + client sync is unproven); raising capacity is a restart-with-bigger-stamp operation only if the owner later asks for it — out of scope v1, documented in admin docs.

## `!visit <playername>`

Server command using the passout-warp primitive, and the primary inter-farm and go-home mechanism in the `!visit`-only design. Because the warp is fee-free only from `FarmHouse`-derived/`Cellar`/`PassOutSafe` **source** locations, v1 **restricts `!visit` to when the player stands inside a house** (any FarmHouse/Cabin/Cellar); issued elsewhere it replies "use it from inside a house". `!visit <name>` warps to the target farm's entrance; `!visit <own name>` is the go-home path. Return from a visit is by walking out to the shared world (farm map-edge exits → hub) or `!visit <own name>` from inside the visited house. Server tracks nothing persistent — the warp is one-shot, and message-5 observation covers diagnostics.

## Per-farm game logic (the "no gaps" audit)

Every audit category from the findings, with execution side and v1 disposition:

| Concern | Executes | v1 disposition |
|---|---|---|
| Night events (witch/fairy/meteor/animal) | server | **One event per night on a randomly chosen player farm** — retarget the single vanilla `farmEvent`'s farm via the shared daily seed (`Utility.CreateRandom(uniqueIDForThisGame, DaysPlayed)`) so all clients agree. Per-farm rolls deferred (each event's effect is welded to a global-`Game1`-state-hijacking animation) |
| Overnight shipping loop | server | **Keep vanilla.** Money is pooled; the auto-added per-farm Shipping Bin works (opens the shared store). `ShippingBin.cs`'s `getFarm()` lid-cache is cosmetic |
| Spouse patio / marriage | server (NPC sim) | Patch `GetSpousePatioPosition` + the `"Farm"` warp in `setUpForOutdoorPatioActivity` to resolve the spouse's married-farmer → `homeLocation` → parent `Farm_JS_<n>` (same patio coord, shared map). No-level-0-marriage rule applies to cabins as usual |
| Robin construction warp | server (NPC sim) | Replace the host-only `Game1.player.daysUntilHouseUpgrade` check with an all-farmers scan; warp Robin to the upgrading farmer's farm. **Cosmetic only** — the upgrade map-swap completes independently in `Farmer.dayupdate` |
| Mailbox | client + server | **Mail is per-`Farmer`, never cross-delivered.** Each player reads their own mail at **their own cabin's mailbox on `Farm_JS_<n>`** (the ownership gate passes for the owner). The hub main mailbox is host-owned and **blocks** farmhands (`Farm_OtherPlayerMailbox`, `GameLocation.cs`) — it is NOT the served mailbox. Client-side `getMailboxPosition` (`Farmer.cs`) points the "you have mail" icon at the hub — a cosmetic mismatch, documented |
| Warp totems / return scepter | client | **All land on the hub** — `Return Scepter` (`Wand.cs`) and Farm totem (`Object.cs`) hardcode `warpFarmer("Farm", …)`; not server-redirectable. Go home via `!visit`/2am pass-out |
| Hay/silos | server + client interact | Shared on the hub v1; per-farm silos deferred |
| Carpenter/animal menus | client | Vanilla 1.6 lets players build on any buildable held location — player farms qualify (`Farm.IsBuildableLocation`). Cabin building off-`"Farm"` is blocked client-side (`CarpenterMenu.cs`) — desirable here. **Any player can build on any player's farm in v1**; server-side veto is a fast-follow |
| Grandpa evaluation, lightning, Island shipping | server | Stay global on the hub (communal), documented |
| Save migrations, debug commands | server | Operate on the primary farm only; non-crashing with extra farms — no action |
| World map (`MapPage`), shipping UI | client | Requires `"Farm"` always-active (ground rule 5) — satisfied by hub design; no action possible or needed |

## Implementation phases

1. **Phase 0 — runtime prototype (gates everything):** on a dev server with 2 vanilla clients: (a) provision `Farm_JS_1..2` via data entries, verify join payload/sync and save round-trip incl. the preflight; (b) confirm the `"Farmhouse"`-building-with-instanced-`Cabin`-indoors interior syncs and `CreateFarmhand` fires, then drive a Robin cabin **upgrade** and verify the map/object swap replicates to peers (the one residual housing unknown), and confirm the degenerate dual-set `indoors` state doesn't bite; (c) prove lobby-exit passout-warp onto a player farm and `!visit` from inside a house; (d) confirm mail reads at the player's own-farm cabin mailbox; (e) verify scepter/totem/festival-end all land on the hub and the 2am pass-out returns to the own bed.
2. **Phase 1 — core:** stamped strategy (+ wallet mode) + provisioning + housing (incl. `isCabin`→`IsPlayerCabin` migration) + preflight + scan widening + home-integrity guard + `!visit`.
3. **Phase 2 — per-farm logic:** night-event random-farm retarget, spouse patio, Robin warp, deletion/farm-reset.
4. **Phase 3 — surfaces:** API farm dimension (`/cabins` + `Farm` field, `/farmhands` farm name; `runner-ui-pipeline-plumbing` applies), E2E suites (join lifecycle, two-client visit, deletion/reclaim, save/reload with N farms, import), admin/player docs.

## Compatibility verification (to complete before implementation sign-off)

- LAN vs Steam vs Galaxy transports: join flow (reservations, lobby, passout warp) is transport-agnostic today — re-verify with farms mode; LAN `getUserID()` is `""` (selectability rules unchanged).
- Passwordless config: farms-mode patches must live in unconditionally-constructed services (`harmony-patch-reachability`).
- `SERVER_TPS=5`: no new per-second-loop handlers; provisioning is load-time; message-5 observation is event-driven.
- Disconnect mid-lobby, mid-visit, mid-warp; day transition with a visitor on a foreign farm; wedding/festival days — confirmed: event-end warps hardcode `"Farm"`, so all players land on the hub (not a crash), and any straggler still out at 2am self-heals home via the server-authoritative pass-out.
- Save/reload with 0, 1, N player farms; `saves import` of a vanilla save (single-farm save + farms-mode stamp = farms provisioned per capacity; imported owner homed per existing Layer B, then widened lookups apply).
- Host automation: `WarpToFarmDefaultSpawn`/`MonitorFarmhouse`/host sleep unchanged on the hub — verify no host flow references player farms.

## Decisions

1. **Hub geometry.** Hub-with-`!visit`, the only option on unmodded clients: every client-side return path (`Return Scepter`, Farm totem, festival/wedding end) hardcodes `"Farm"` and lands players on the hub on unmodded clients. New players still spawn on their own farm at join (lobby-exit pass-out → own cabin, server-authoritative).
2. **Night events.** One event per night on a randomly chosen player farm (shared-seed retarget). Per-farm rolls deferred (effects welded to global-state animations).
3. **Construction.** Any player may build on any player's farm in v1; server-side veto is a fast-follow.
4. **Deletion.** Wipe the farm in place for the next claimant.
5. **Pets/greenhouse.** Communal on the hub.
6. **Wallets.** Both shared and separate supported, stamped at world creation (`FarmerTeam.useSeparateWallets`), first-startup-only.
7. **Walkable gates.** Not in v1 — `!visit`-only. Gates need net-warp *addition* to vanilla clients (unproven); a fast-follow may fund that Phase-0 experiment.

## Deferred to fast-follow (post-v1)

- Walkable hub gates (net-warp-addition experiment).
- Per-farm night-event rolls (per-event effect extraction from animations); the fairy-crop-growth effect is the highest-value candidate.
- Server-side construction veto.
- Per-farm silos/hay; runtime capacity increase (mid-session location creation + sync is unproven).
