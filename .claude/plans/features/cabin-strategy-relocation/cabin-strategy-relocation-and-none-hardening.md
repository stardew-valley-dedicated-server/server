# Cabin-strategy: relocation switch + None hardening

**Status:** decisions resolved (D1–D4, C2, A4); Part A in progress
**Origin:** Discord support thread — operator created a save on `CabinStack`, switched to `None` to let
players meet inside cabins, then saw "4 cabins / 4 farmhands, a phantom farmhand that won't delete."
Investigation showed that behavior is entirely the intended cabin-availability pool + the
`CabinStack → None` migration surfacing previously-hidden cabins. No bug — but the thread exposed three
real design gaps this plan addresses.

This plan has three independent parts (A/B/C). They share the settings-plumbing surface but can ship as
separate PRs. Delete this file when the code lands (`plan-discipline.md`).

---

## Background (verified during scoping)

- `EnsureAtLeastXCabins` (`CabinManagerService`) keeps `minEmptyCabins = 1` unclaimed cabin so a new
  player can always join — there is no human host to build cabins via Robin. `DELETE /farmhands`
  (`ApiService.ExecuteFarmhandDeletion`) ends by calling it, so deleting the empty slot rebuilds it. This
  is correct and stays.
- Strategy is stored in `PersistentOptions.Data.CabinStrategy`; `PersistentOptions.RecaptureAndSync`
  captures `PreviousCabinStrategy` before `SyncFromSettings` overwrites it, and
  `CabinManagerService.DetectAndMigrateStrategyChange` → `MigrateCabins` acts on the delta at
  `OnSaveLoaded`.
- Under `None`, cabins are placed at map-authored Paths-layer markers via
  `FarmCabinPositions.GetDesignatedPositions` / `GetNextAvailablePosition` (~7 on Standard). Placement
  uses `buildStructure(skipSafetyChecks: true)` + `Building.ClearTerrainBelow`, which removes "all
  objects, bushes, resource clumps, and terrain features" under the footprint — i.e. it **bulldozes**
  whatever a player put on a designated spot.
- The `!cabin` command (`CabinCommand`) is hard-blocked under `FarmhouseStack` (early return). It records
  intent in `CabinManagerData.PlayerCabinPositions`, read only by `HasSavedPosition` (bulk-mover filters).
- Warp targets (verified in `CabinExtensions`): `SetWarpsToFarmCabinDoor()` points a cabin interior's
  Farm-exit warps at the cabin building's **own** door (`getPointForHumanDoor()`);
  `SetWarpsToFarmFarmhouseDoor()` points them at the **main farmhouse** door
  (`Game1.getFarm().GetMainFarmHouseEntry()`).
- Under `FarmhouseStack`, `OnLocationIntroductionMessage` and `OnLocationDeltaMessage` **unconditionally**
  call `SetWarpsToFarmFarmhouseDoor()` (no `IsInHiddenStack` / `HasSavedPosition` exemption). These are
  the **only** two farmhouse-door repoint sites in `mod/`. `Building.Relocate` sets cabin-door warps but
  the next interceptor clobbers them.
- `IsInHiddenStack()` checks the exact tile `(-20,-20)`. Lobby/editing cabins live on row `y = -21`, so
  they are **not** in the hidden stack — gating an exemption on `!IsInHiddenStack()` alone would wrongly
  exempt lobby cabins. Use `HasSavedPosition` (intent-based) instead.

---

## Part A — `AllowCabinRelocation` setting + relocation under FarmhouseStack

**Goal:** decouple the `!cabin` gate from strategy. A single strategy-independent switch lets operators
allow players to move their cabin out to a real, enterable position under **any** stacking strategy —
making `FarmhouseStack` the clean canonical answer to the Discord operator's goal (nothing on the farm by
default, no ghost/dummy, and moved-out cabins become visible + mutually enterable so players can meet
inside).

### A1 — Plumb the setting (runtime/persisted path, mirrors `CabinStrategy`)

`AllowCabinRelocation` is read live by `!cabin` and must survive `/reload`, so it goes through
`PersistentOptions` (the `CabinStrategy` path), **not** the creation-only `SeparateWallets` path. Add a
`bool` (default per Open Decision D1) to each layer, mirroring `CabinStrategy`/`SeparateWallets`:

- `Services/Settings/ServerSettings.cs` → `ServerRuntimeSettings.AllowCabinRelocation`.
- `Services/Settings/ServerSettingsLoader.cs` → accessor `AllowCabinRelocation`.
- `Services/PersistentOption/PersistentOptionsSaveData.cs` → `AllowCabinRelocation`.
- `Services/PersistentOption/PersistentOptions.cs` → copy in `SyncFromSettings`; add a convenience
  accessor `AllowCabinRelocation => Data.AllowCabinRelocation`.
- `Services/GameCreator/NewGameConfig.cs` → property + `FromSettings` + `FromRequest` param/assignment +
  `ToString` (parity with `CabinStrategy`; lets `POST /newgame` override per-game — optional per D4).
- `Services/GameCreator/GameCreatorService.cs` → seed into the `PersistentOptionsSaveData` it builds
  (alongside `CabinStrategy`).
- `Services/Api/ApiService.cs` → `ServerRuntimeSettingsInfo.AllowCabinRelocation` +
  `HandleGetSettings`; `NewGameRequest.AllowCabinRelocation` + the `/newgame` `FromRequest` call.
- `Services/Commands/SettingsCommand.cs` → print line in `ShowConfig`; preview line in `HandleNewGame`.
- Test surface: `tests/.../Clients/ServerApiClient.cs` (`ServerRuntimeSettingsInfo` DTO +
  `/newgame` body key), `tests/.../Containers/ServerContainer.cs` (`BuildSettingsJson`),
  `tests/.../Containers/ServerContainerOptions.cs`.

### A2 — Replace the `CabinCommand` strategy gate with the setting gate

In `CabinCommand.Register`, replace the `options.IsFarmHouseStack` early-return (which rejects both move
and reset) with `!options.AllowCabinRelocation`. Reword the rejection ("Cabin relocation is disabled on
this server."). Now the gate is uniform across all three strategies.

### A3 — Exempt moved-out cabins from the farmhouse-door repoint (the load-bearing change)

In **both** `OnLocationIntroductionMessage` and `OnLocationDeltaMessage`, the `FarmhouseStack` branch must
route a cabin with a saved position through `SetWarpsToFarmCabinDoor()` instead of
`SetWarpsToFarmFarmhouseDoor()` — mirroring the existing `CabinStack` `else` branch in
`OnLocationDeltaMessage`:

- `OnLocationDeltaMessage`: `if (HasSavedPosition(cabin.ParentBuilding)) cabin.SetWarpsToFarmCabinDoor();
  else cabin.SetWarpsToFarmFarmhouseDoor();`
- `OnLocationIntroductionMessage`: same branch on the peer's `fhCabin`.

Use `HasSavedPosition` (owner id in `PlayerCabinPositions`), **not** `!IsInHiddenStack()` — the latter
would also exempt lobby cabins (row `y=-21`). Add a `Cabin`-typed overload of the saved-position check
(current `HasSavedPosition` takes a `Building`; the delta path has a `Cabin`).

**Chicken-and-egg (must land together):** `PlayerCabinPositions` is populated only by `!cabin`, which is
blocked under `FarmhouseStack` today, so `HasSavedPosition` can never be true there until A2 lifts the
gate. A2 and A3 must ship in the same change or neither works.

### A4 — `!cabin reset` under FarmhouseStack (RESOLVED: no ResetCabin change needed)

Once A2 lifts the gate, `ResetCabin` becomes reachable under `FarmhouseStack`. A moved-out cabin is not
`IsNone` and not `IsInHiddenStack`, so it falls through to the generic `else`
(`SetPosition(HiddenCabinLocation)` + intent cleared), sending it back to the stack. The resulting
location-delta then hits the A3 `OnLocationDeltaMessage` branch with `HasSavedPosition` now false →
`SetWarpsToFarmFarmhouseDoor`, so the re-hidden cabin correctly exits at the farmhouse door again. No
FarmhouseStack special-case is needed; a regression test covers it. (`ResetCabin` already clears intent
before moving, which is the safe order.)

**Why FarmhouseStack is cleaner than CabinStack here:** FarmhouseStack has no shared visible `StackLocation`,
so a moved-out player leaves no empty spot to fill — **no door-dead dummy is ever needed** (the dummy is a
CabinStack-only mechanism in the `OnLocationIntroductionMessage` `else` branch). Non-movers keep the tidy
farmhouse-door default; only opt-in movers get a visible cabin.

---

## Part B — `None`: honest cap + place-up-front (kill the silent MaxPlayers lie and the on-demand bulldoze)

**Problem:** under `None` the real player ceiling is the number of designated map spots (~7 Standard), but
`MaxPlayers` is configurable to 100 with nothing reconciling them — `EnsureAtLeastXCabins` never consults
`MaxPlayers`. Joins past the spot count fail with only a `cabin_build_failed` Warn. Separately, on-demand
placement drops real cabins on the live farm and bulldozes player content.

### B1 — Place `min(designatedPositions, MaxPlayers)` up front at new-game creation

In `GameCreatorService.CreateNewGame`, replace the `None` count `Math.Max(1, config.StartingCabins)` with
`min(FarmCabinPositions.GetDesignatedPositions(farm).Count, options.Data.MaxPlayers)`. The farm is empty
at creation, so placing the full set here is safe (no bulldoze). See Open Decision D2 (StartingCabins).

### B2 — Cap `EnsureAtLeastXCabins` at `min(positions, MaxPlayers)` under None

Bound the None build target so total cabins never exceed `min(positions, MaxPlayers)`. This makes the
`MaxPlayers` cap honest and — combined with B1 — means no on-demand growth is needed on a developed farm
(the full set already exists; deletes rebuild only onto the just-freed designated spot, which is clean).
Residual: an operator who *raises* `MaxPlayers` after creation could still trigger on-demand placement onto
possibly-developed spots (Open Decision D3).

### B3 — Document the None ceiling as a known limitation

`docs/features/cabin-strategies.md` + `docs/admins/configuration/server-settings.md`: under `None`,
effective max players = `min(designated map spots, MaxPlayers)`; `None` physically places cabins on the
farm and is recommended only for fresh games / small fixed rosters; use `CabinStack`/`FarmhouseStack` for
larger or existing farms.

---

## Part C — Block switching to `None` on an existing save

**Goal:** `None` only takes effect on fresh games, so migrating a developed `CabinStack`/`FarmhouseStack`
save to `None` can't bulldoze the farm.

### C1 — Reject stacked→None migration on a real existing save

In `MigrateCabins`, the `fromUsesHidden && !toUsesHidden` branch already has a revert pattern (revert
`options.Data.CabinStrategy = from` + `Save()` + emit) for the insufficient-positions abort. Broaden it:
reject **any** stacked→None switch on an existing save (not just insufficient positions), reverting the
persisted strategy and emitting a `cabin_strategy_migration_aborted` reason like `none_on_existing_save`,
with an operator-facing Warn explaining `None` is fresh-games-only. This subsumes the existing
insufficient-positions abort for the None case.

### C2 — RESOLVED: gate the revert on there being cabins to migrate

`DetectAndMigrateStrategyChange` runs at `OnSaveLoaded` for **new games too**. On first boot,
`PreviousCabinStrategy` is the *default* persisted strategy (a fresh `PersistentOptionsSaveData` — default
`CabinStack`), which is **not** `None` — so a naive C1 guard would see `default(CabinStack) → None` on a
legitimately fresh None game and revert it, breaking `None`.

**Resolution:** gate the C1 revert on `hiddenCabins.Count > 0` (the list the branch already computes). A
real stacked save always has its cabins parked in the hidden stack (≥1, guaranteed by
`EnsureAtLeastXCabins`), so a genuine stacked→None switch is caught and reverted. A fresh None game has
**zero** hidden cabins (`GameCreatorService` places None cabins visibly, never in the stack), so it falls
through untouched. No new field, no creation-flag read. **Verify during Part C** that the new-game
placement/`OnSaveLoaded` ordering never leaves a hidden cabin on a fresh None game; if an ordering edge
does, fall back to an `Initialized` flag on `PersistentOptionsSaveData` set after the first successful
load.

---

## Resolved decisions

- **D1 — Default of `AllowCabinRelocation` → `true`.** A single bool can't preserve all three strategies'
  current gate states, so preserve the two existing operators rely on: CabinStack and None both allow
  `!cabin` today, and defaulting `false` would silently disable a working feature on upgrade (a
  regression). `true` keeps CabinStack/None exactly and newly permits FarmhouseStack relocation — the
  operator's actual ask. FarmhouseStack still reads clean by default (no visible cabin until someone runs
  `!cabin`). Operators who want to forbid relocation set `false`.
- **D2 — StartingCabins under None → ignored; place `min(positions, MaxPlayers)` up front.** Under None,
  `StartingCabins` is redundant with the position/player ceiling and its excess silently yields fewer
  (invariant 9). Placing the full `min(positions, MaxPlayers)` set on the empty farm at creation is safe
  (nothing to bulldoze) and makes the cap self-evident. `StartingCabins` stays meaningful for the stacked
  strategies. Documented as ignored-under-None.
- **D3 — MaxPlayers raised after creation under None → freeze.** The None cabin count is fixed at creation
  to `min(positions, MaxPlayers)`; a later `MaxPlayers` increase does NOT grow it. Deletes are replenished
  in place only, up to the frozen count — and because allocation is lowest-Order-first, replenish targets
  the freed low-Order cabin spot, never a never-used higher-Order spot a player may have built on, so no
  bulldoze. To grow a None roster, start a fresh game or use a stacked strategy. This keeps the "no
  automatic placement on a developed farm" guarantee absolute. (Part B: persist the creation-time None
  count on `PersistentOptionsSaveData`/`CabinManagerData` so it survives reloads.)
- **D4 — `POST /newgame` override → include (parity).** Add nullable `AllowCabinRelocation` to
  `NewGameRequest` + a `FromRequest` parameter, matching `CabinStrategy`/`SeparateWallets`. Same semantics
  as those: the `/newgame` value seeds the initial `PersistentOptionsSaveData`, but `SyncFromSettings`
  re-applies the settings-file value on every boot/reload, so the settings file is the durable source of
  truth.

---

## Compatibility verification (adversarial)

Part-A items (verified during scoping against the worktree + the two Explore agents):

- **LAN vs Steam — verified.** Relocation and warp changes are server-authoritative `GameLocation.warps`
  mutations (a verified vanilla-client control primitive) — transport-independent. The A3 exemption
  predicate `HasSavedPosition` reads only the cabin owner's `UniqueMultiplayerID` + `PlayerCabinPositions`;
  it reads **no** client stamp. No divergence.
- **Lobby / unauthenticated players — verified.** Lobby/editing cabins live on row `y=-21`, are never
  moved by `!cabin`, and so never get a `PlayerCabinPositions` entry → `HasSavedPosition` is always false
  for them → they keep the farmhouse-door / lobby warps. This is exactly why A3 gates on `HasSavedPosition`
  and NOT `!IsInHiddenStack()` (the latter would wrongly exempt lobby cabins, since `IsInHiddenStack`
  checks only tile `(-20,-20)`).
- **`SERVER_TPS=5`, FPS caps — verified.** No timing coupling; warp/placement are one-shot on
  message/creation.
- **Disconnect mid-relocation — verified.** `!cabin` runs synchronously on the source farmer's command;
  a disconnect can't interleave the `PlayerCabinPositions` write + `Relocate`.

Part-B/C items (dispositions recorded; verify when those parts are implemented):

- **Save-import interaction (Part B):** the finalizer builds via `BuildNewCabinVisible` under None
  (`save-import-layer-timing.md`); B2's cap could make that build fail if the None cap is already reached.
  Disposition: the operator-bound cabin must be counted against — or exempt from — the None cap so the
  swap-host import can always place its cabin. Resolve in Part B against `SaveImportService`'s finalizer.
- **Other `EnsureAtLeastXCabins` callers (Part B):** `OnServerJoined`, `OnSaveLoaded`,
  `FarmhandSenderService` (`reservedIds.Count + 1`), `DELETE /farmhands`, `GameCreatorService`. Disposition:
  B2's None cap is authoritative for all; confirm `FarmhandSenderService`'s reserved-count call clamps to
  the frozen None cap rather than stranding a join. Resolve in Part B.
- **Existing None count tests (Part B):** `CabinStrategyNoneTests` asserts exact counts (`cabin-system.md`
  invariant 9). B1/B2 change the counts — update the assertions to the new frozen `min(positions,
  MaxPlayers)` model, and confirm via the run artifact that placement fired
  (`passing-test-isnt-proof-the-scenario-ran.md`). Resolve in Part B.
- **C2 fresh-None-game ordering (Part C):** verify the new-game placement/`OnSaveLoaded` ordering leaves
  zero hidden cabins on a fresh None game (see §C2). Resolve in Part C.

---

## Tests & docs

- New E2E: FarmhouseStack + `AllowCabinRelocation=true` → two farmhands `!cabin` out → assert both cabins
  visible at real positions via `/cabins`, and (if feasible) that a farmhand can enter the other's cabin.
  Extend `CabinStrategyFarmhouseStackTests`.
- New E2E: `AllowCabinRelocation=false` rejects `!cabin` on all three strategies.
- Update `CabinStrategyNoneTests` for the `min(positions, MaxPlayers)` cap (B).
- New E2E: stacked→None on an existing save is rejected (strategy reverts); fresh None game is unaffected
  (C — the C2 false-trip guard).
- `ServerSettingsTests`: `/settings` round-trips `AllowCabinRelocation`.
- Docs: `docs/features/cabin-strategies.md` (relocation switch + None ceiling), the "Moving Cabins"
  section (now works under FarmhouseStack when enabled), and `docs/admins/configuration/server-settings.md`
  (new setting row + example).

## Announced edit surface

Part A: ~11 mod files + 3 test-harness files + docs. Part B: 2 mod files + docs + None tests. Part C:
1–2 mod files (+ possibly `PersistentOptionsSaveData` for the Initialized flag) + tests. Parts are
independently shippable; recommend A first (delivers the operator's actual ask), then B, then C.
