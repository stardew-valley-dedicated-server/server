# Farm Stack — Research Findings

Research backing for the per-player-farms build plan ([`../features/farm-stack.md`](../features/farm-stack.md)). Investigated 2026-07-13 against `decompiled/sdv-1.6.15-24356/` and the mod sources. All engine claims cite decompiled file:line; load-bearing claims were verified by direct read, not agent summary alone.

## Verdict

**Feasible.** SDV 1.6's location model handles extra `Farm`-type locations generically across save, sync, cabin-assignment, cellar, and NPC-pathfinding machinery, and the join/lobby/claim lifecycle carries over with scan-widening only. The shaping constraint is that **players run unmodded vanilla clients** (see the vanilla-client section below): client-side resolution can't be patched, which fixes the world geometry as primary-farm-as-hub and makes the passout message (fee-gated) the only server-initiated player warp. The larger half of the work is retrofitting JunimoServer itself, which is uniformly single-farm.

## `Game1.getFarm()` resolution path and blast radius

`getFarm()` = `RequireLocation<Farm>("Farm")` (`Game1.cs:4712-4715`); `RequireLocation` throws `KeyNotFoundException` when the name is missing (`Game1.cs:10210-10222`), so the canonical `"Farm"` location must always exist — secondary farms are strictly additive. 106 call sites across 34 files, by category:

| Category | Runs on | Sites |
|---|---|---|
| Overnight/day-update (shipping loop, entry-warp props, lightning, fairy-rose flag, farm cave) | host | `Game1.cs:8009-8220,9818-9873`; `Utility.cs:3433,3501`; `Crop.cs:913`; `HoeDirt.cs:566,982`; `FarmCave.cs:258`; `SaveGame.cs:395,1000-1001` |
| Night events — all target the primary farm only | host | `WitchEvent.cs:41`; `FairyEvent.cs:37`; `SoundInTheNightEvent.cs:68,237,266,272`; `WorldChangeEvent.cs:144,984`; `Utility.cs:4661-4662` |
| NPC/pet/spouse (spouse patio anchor, Robin construction warp, pet roster) | host + read queries | `NPC.cs:1297,6254,6260,6592`; `Pet.cs:440,521,569`; `Utility.cs:418-421,7202`; `Farmer.cs:3736` |
| Farmer home/mailbox (cabin-mailbox scan over primary farm's buildings) | mixed | `Farmer.cs:2624-2634,5945-6011` |
| Grandpa evaluation, hay/silos, warp totems, Island shipping | mixed | `Multiplayer.cs:687,788`; `Event.cs:3213-3218,3549,4785,13078`; `GameLocation.cs:11482,11513,12544,16488,16516`; `Object.cs:3174`; `IslandWest.cs:200,243,313`; `BusStop.cs:178`; `FarmHouse.cs:131,510-520,1015,1024` |
| Menus/UI (Carpenter/PurchaseAnimals default `?? getFarm()`, shipping-bin undo, map page) | client | `CarpenterMenu.cs:265,568,1285`; `PurchaseAnimalsMenu.cs:85`; `AnimalQueryMenu.cs:248,383,451`; `ItemGrabMenu.cs` (9 sites); `MapPage.cs:64` |
| Save migrations (operate on singleton only; extra farms ignored, not crashed) | host, load | `SaveMigrator_1_4.cs:52`; `_1_5.cs:87,128`; `_1_6.cs:73,120,394,405,981` |
| Debug commands | dev | `DebugCommands.cs` (7 sites) |

Key counterweight: vanilla 1.6 already routes per-player "home" through `Farmer.homeLocation` (`Farmer.cs:904-905`, set to the cabin's unique name by `Cabin.AssignFarmhand`, `Cabin.cs:61,103`) and `Utility.getHomeOfFarmer` (`Utility.cs:5117-5120`). `getFarm()` remains mostly for genuinely-global farm state.

## Save/load: location serialization and name matching

- Locations serialize polymorphically via `XmlSerializer` (`[XmlInclude(typeof(Farm))]`, `GameLocation.cs:75`); a `Farm` named `Farm_JS_2` round-trips as `xsi:type="Farm"`.
- Load matches saved→fresh **by name**, case-insensitive (`SaveGame.cs:1394`; `Game1._locationLookup` is `OrdinalIgnoreCase`, `Game1.cs:2168`). Names must be globally unique.
- The fresh set is built first: `loadForNewGame` hardcodes the primary `new Farm(..., "Farm")` (`Game1.cs:3367`), then `AddLocations()` instantiates every `Data/Locations` entry with `CreateOnLoad` (`Game1.cs:7378-7416`). `CreateGameLocation` takes the type from data: `Activator.CreateInstance(Type.GetType(createData.Type), MapPath, id)`, then applies `isAlwaysActive = createData.AlwaysActive` (`Game1.cs:7367-7375`, verified). **A data entry with `Type = "StardewValley.Farm"` creates a real Farm — no C# location bootstrapping needed.**
- **Hazard:** a saved location with no fresh counterpart is silently dropped — villager NPCs are salvaged, everything else (crops, chests, buildings, cabins) is discarded with only a Warn log (`SaveGame.cs:1413-1426`, verified). A load-time preflight guard is mandatory.
- Cabins serialize nested in the parent location's `buildings` (`Building.cs:51-52`); transferred wholesale on load (`SaveGame.cs:1498,1519-1526`). A cabin on `Farm_JS_2` round-trips inside that farm's element.
- Per-instance caveats: `Farm.DayUpdate` keys forage/ore spawns off the global `Game1.whichFarm`, not the instance's own map (`Farm.cs:342`). `AddDefaultBuildings` runs on every Farm instance (`Farm.cs:170-177`); the farmhouse/greenhouse are *non-instanced* interiors with a first-come-wins guard, so secondary farms get indoor-less shell buildings (`Building.cs:1973-1988`, verified) unless suppressed.

## Hardcoded `"Farm"`/`"FarmHouse"` strings

39 `"Farm"` literals across 22 files. The ones that matter:

- **`Game1.warpFarmer`'s `case "Farm":`** (`Game1.cs:9809-9880`) — entry-tile fixups via `getFarm()` map properties; the central warp resolver has no per-farmer indirection. Map-edge warps in BusStop/Backwoods/Forest `.tmx` files target the literal `"Farm"`. This is the primary patch surface.
- **`CarpenterMenu.IsValidBuildingForLocation`** (`CarpenterMenu.cs:1232`, verified): cabins refused wherever `TargetLocation.Name != "Farm"`. Programmatic `buildStructure(skipSafetyChecks: true)` bypasses it.
- **`WarpPathfindingCache.IgnoreLocationNames`** (`WarpPathfindingCache.cs:15`) excludes `"Farm"` by name — but `Farm.ShouldExcludeFromNpcPathfinding()` returns `true` unconditionally by type (`Farm.cs:946-949`, verified), so `Farm`-typed secondary locations are excluded without patching.
- Remaining literals are new-game creation, edge-clamping, debug warps, and event-script tokens — low risk.

## Engine primitives to hook into

Vanilla 1.6.15 ships official dedicated-host groundwork at `StardewValley/Network/Dedicated/DedicatedServer.cs` (`Game1.dedicatedServer`). Its automation (`Tick`, event locks, sleep/festival driving) is gated on `Game1.IsDedicatedHost`, which is false on this server (`hasDedicatedHost = false` per `host-automation.md`) — so it stays inert here, but two of its mechanisms matter as reference:

- `GameServer.processIncomingMessage` case 5 calls `Game1.dedicatedServer.HandleFarmerWarp(...)` on **every** farmhand warp notification (`GameServer.cs:724`) — message 5 is the server's observation point for every client map transition (name, tile, facing), regardless of dedicated-host mode.
- Its forced-event path (`TryForceClientHostEvent`, `DedicatedServer.cs:134-153`) shows the limits of message 4 as a warp vehicle: the client resolves the event **by id against its own content** (`location.findEventById`, `Multiplayer.cs:1616-1619` — custom events don't exist on vanilla clients) and the event returns the player to their pre-event location afterward (`setExitLocation`, `Multiplayer.cs:1642`). Forced events are NOT a general server→client warp.

The 1.6 primitives that carry the feature:

- Sync: `isAlwaysActiveLocation` is per-location flag only, no name check (`Multiplayer.cs:1306-1313`); join pushes all always-active locations (`GameServer.cs:579-601`); on-demand location requests resolve by name (message 5 → `sendLocation`, `GameServer.cs:677-692`).
- Home/assignment: `Farmer.homeLocation` + `Utility.getHomeOfFarmer` + `NetWorldState.TryAssignFarmhandHome`, whose fallback scans **all** locations via `Utility.ForEachBuilding` (`NetWorldState.cs:781-809`, verified). Day-start wake, warp-home, and pass-out are location-agnostic (`Game1.cs:4195-4224,9750-9758,10340-10343`).
- Slots: farmhand slots exist only via `Cabin.CreateFarmhand()` (`Cabin.cs:46-65`), invoked when a building's indoors `is Cabin { HasOwner: false }` (`Building.cs:1220-1222`, verified). `TryAssignFarmhandHome` hard-checks `is Cabin` (`NetWorldState.cs:783`); a farmhand homed in a plain `FarmHouse` fails it, and the failure path wipes `userID`/`homeLocation` (`NetWorldState.cs:769-774`). **Any per-player-farm housing must remain `Cabin`-typed under the hood** (Cabin subclasses FarmHouse; 1.6 cabin appearance is skinnable via `Data/Buildings`) — or patch the slot/assignment machinery.
- Cellars: per-player against top-level `Cellar2..N`, farm-independent (`Game1.cs:4515-4546`; `FarmHouse.cs:970-1004`).
- Building placement: 1.6 supports building on any always-active buildable location (`GameLocation.cs:2455-2475`); `Farm.IsBuildableLocation` overrides to `true` (`Farm.cs:164-167`).

## Why cabin-stacking doesn't cover this

Settled by product decision (2026-07-13): the requirement is **private farmland** — own crops/animals/buildings/layout and griefing isolation — which cabin-stacking on a shared farm cannot provide. What per-user farms still won't change: pooled money (unless vanilla separate wallets), master-gated world progression (CC/island/greenhouse via `MasterPlayer.mailReceived`), shared town/NPCs/festivals/clock.

## LandGrants (1.5 multi-farm mod) — reference only

Do not port; mine for ideas only. It is an unmaintained 1.5 beta (`0.5.2-beta`, SMAPI ≥3.18, no 1.6 update, no issue history). Its `NetFieldBase<string, NetString>.Equals` patch globally spoofs `"Farm_LG_1" == "Farm"`, with `UseLocationNameAsIs`/`CompareRealStrings` escape-hatch flags betraying cascade problems — the opposite of the targeted-patch shape 1.6 permits. Half its machinery is obsolete in 1.6: per-cabin farmhand saving (now save-level, `SaveGame.cs:56,367,864`), manual location registration (now `Data/Locations` `CreateOnLoad`), always-active patching (now a data flag). Its warp interception concept (per-player routing keys consulted at warp time) remains the right idea — though it only worked because LandGrants runs on every client; see the vanilla-client section for why that doesn't transfer here. Source: [LandGrantsMod.cs](https://github.com/Platonymous/Stardew-Valley-Mods/blob/master/LandGrants/LandGrantsMod.cs) ("Instant Multi Farm" on [Nexus](https://www.nexusmods.com/stardewvalley/mods/15855)).

## The vanilla-client constraint — what the server can and cannot control

Players connect with **unmodded vanilla clients**; Harmony patches exist only in the server process. Every "patch X" disposition must be checked against where X executes. In the blast-radius table above, the Menus/UI category and any client-side resolution (`warpFarmer`'s local path, totem targets, `getMailboxPosition` during interaction, `ItemGrabMenu` shipping) is **not patchable**. Server-side simulation (night events, NPC movement, overnight shipping, save logic) is.

Verified server→client control primitives (and their limits):

1. **Passout warp** — `Game1.server.sendMessage(peer, Multiplayer.passout, host, { locationName, x, y, hasBed })` makes a vanilla client fade-and-warp to an arbitrary named location+tile (`Multiplayer.cs:635-644` → `Farmer.performPassoutWarp`, `Farmer.cs:5841+`). **Production-proven**: the mod's lobby exit uses exactly this (`FarmerExtensions.WarpHome`, `mod/JunimoServer/Util/FarmerExtensions.cs:10-44`). **Caveat**: it is fee-free only when the player's *source* location is `FarmHouse`-derived, `Cellar`, or has `PassOutSafe` — otherwise the client charges the pass-out fee and sends pass-out mail (`Farmer.cs:5869-5877`). The lobby use is safe because the player stands in a lobby cabin (a `FarmHouse` subclass). It cannot be used as a routine warp from arbitrary outdoor locations without side effects.
2. **Location `warps` mutations** — `GameLocation.warps` is a net-synced, server-authoritative collection; server-side rewrites reach vanilla clients and change where walking onto a tile takes them. **Production-proven**: lobby cabins get their exit warps removed (`LobbyService.cs:1687`) and hidden-stack cabins get exit warps rewritten to the farmhouse door (`CabinExtensions.SetWarpsToFarmFarmhouseDoor`) — both load-bearing, E2E-covered behaviors on vanilla clients. (Warp *addition* — new tiles, not just rewrites — is the same mechanism but unproven; prototype item.)
3. **Warp observation** — every client warp notifies the server via message 5 (`Game1.notifyServerOfWarp`; server handler `GameServer.cs:698-724`), so the server always knows where every farmhand goes, immediately.
4. **Forced events** — dead end for routing (see Q4).
5. **Join-time spawn control** — the server serializes each farmhand's spawn fields to the joining client and can point them anywhere (the lobby redirect mechanism, `FarmhandSenderService.ApplyLobbyRedirectToFarmhand`).

Consequence for routing: warps that vanilla clients resolve **locally** against a held location — most importantly every `"Farm"`-named target (bus-stop walk-back, warp totems, `MapPage`) — complete client-side before the server could intervene, and the fee caveat rules out routine post-arrival passout redirects. The primary `"Farm"` also cannot be made non-always-active: client-side `Game1.getFarm()` calls (`MapPage.cs:64`, `ItemGrabMenu`, menu defaults) throw `KeyNotFoundException` on a client that doesn't hold a location named `"Farm"`. Both constraints together push the design toward **primary-farm-as-communal-hub** with per-player gate warps onto the player farms.

## Current join lifecycle (mod, verified)

The existing machinery the feature must integrate with — all in production, invariants in `.claude/rules/cabin-system.md`:

1. **Slot supply**: farmhand slots exist only via `Cabin.CreateFarmhand()`; the mod pre-provisions hidden-stack cabins and tops up via `EnsureAtLeastXCabins` (called from the `sendAvailableFarmhands` prefix with `reserved+1`, `FarmhandSenderService.cs:259`; invariants 2–3).
2. **Offer**: `SendAvailableFarmhands_Prefix` filters farmhands (offline, cabin+inventory unlocked, userID-selectable, not lobby-cabin, not reserved), limits to exactly one uncustomized slot per client, and reserves it per connection (30s expiry) (`FarmhandSenderService.cs:127-377`; invariant 4's `SlotSelectionGate`).
3. **Lobby redirect**: only the farmhand's *spawn* fields (position, currentLocation, disconnect/lastSleep) are temporarily rewritten to the lobby during serialization; **`homeLocation` is deliberately untouched** — it keeps pointing at the real cabin (`FarmhandSenderService.cs:434-490`). This is exactly the seam per-player farms reuse: a farmhand whose `homeLocation` is a cabin on `Farm_JS_N` flows through the entire join path unchanged.
4. **Lobby exit**: passout-warp to the player's cabin (`FarmerExtensions.WarpHome` — currently scans only `Game1.getFarm()`, one of the sites to widen).
5. **Disconnect/reload healing**: two-Farmer-object split on disconnect (invariant 7), abandoned-claim disconnect heal + load sweep (invariant 8), `EnsureFarmhandRealHome`/`HealLobbyHomedResidents`/`GetHomeOfFarmer_Prefix` recovery (`CabinManagerService.cs`).

Primary-farm-only scans that must widen for cabins living on player farms: `FarmerExtensions.WarpHome` (`Util/FarmerExtensions.cs:12`), `FarmhandSenderService.IsLobbyCabinFarmhand` (`:512`), `CabinManagerService` building scans (`ClearStaleFarmhandReferences:230`, `SyncExistingCabins:485`, `GetAvailableCabinCount:804`, `FindCabinInteriorByName:1724`, `FindOwnedCabin:1755`, `HealLobbyHomedResidents:1540`), `AlwaysOn.LockAllFarmhandStorage` (`:1440`) / `ReleaseOnlineFarmhandStorage` (`:1504`), `ApiService.SnapshotCabins` (`:1690`) and `ExecuteFarmhandDeletion` (`:4595`).

One vanilla guard to add under multi-farm: `NetWorldState.TryAssignFarmhandHome`'s last-resort fallback assigns the farmhand to **any** ownerless cabin via `Utility.ForEachBuilding` (`NetWorldState.cs:798-807`) — with unclaimed farmhouse-cabins on multiple farms, a farmhand with a broken `homeLocation` could silently cross-assign to another player's farm. Recovery must resolve via the cabin's persisted `farmhandReference.uid` (the mod's `FindOwnedCabin` pattern) before any unclaimed-pool fallback.

## Scale considerations

Every always-active location is pushed to every client at join and delta-synced continuously (`GameServer.cs:579-601`); the server simulates N farms overnight. v1 uses `AlwaysActive: true` for simplicity; if join payload or tick cost becomes a problem at high player counts, revisit with on-demand farms (day updates still run server-side for all of `Game1.locations`; `Farm.IsBuildableLocation` doesn't require always-active).

## Product decisions recorded (2026-07-13)

1. Core need: private farmland (money/town/progression stay shared).
2. Allocation: automatic for every player, gated by an opt-in setting that is **first-time-startup-only** (stamped at world creation; no enable/disable migration on existing saves).
3. Coverage: no game-logic gaps — all per-player behaviors (mailbox, shipping, marriage/spouse, night events) supported in the initial implementation.
4. Housing: every player lives in their **own main farmhouse** on their own farm; no visible player cabins.
5. Entry: players enter their own farm by default; `!visit <playername>` enters that player's farm instead.
6. Next step: draft build plan for owner review before any prototype or implementation.
