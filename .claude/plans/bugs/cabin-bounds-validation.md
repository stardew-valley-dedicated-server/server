# `!cabin` bounds & collision validation

## Context

`!cabin` (`mod/JunimoServer/Services/Commands/CabinCommand.cs:50`) places a cabin at `farmer.Tile + (1,0)` with no validation:

- No map-bounds check (cabin can be placed off the buildable rectangle).
- No collision check against other buildings.
- No water / cliff / unbuildable-terrain check.
- No farmer collision check.
- The TODO at `CabinCommand.cs:41-47` already acknowledges the gap.

Impact: cabins land on cliffs, in water, on top of other buildings, off the buildable rectangle, or under the farmer. Warp targets attached to such cabins can softlock the owning player.

The task is a server-side fix: validate the target position before relocating, and tell the player why we refused if it's invalid. We do NOT add a preview / ghost mode, undo, or rollback in this task — the TODO's items (a)/(c) are out of scope.

## Verified ground truth (load-bearing)

- `cabin.Relocate(farmer.Tile.X + 1, farmer.Tile.Y)` resolves to `BuildingExtensions.Relocate(this Building, float, float)` in `mod/JunimoServer/Util/BuildingExtensions.cs:54-56` → `Relocate(Vector2)` → `Relocate(Point)` at lines 59-70. `Relocate` calls `SetPosition` (writes `tileX.Value`/`tileY.Value`), `SetWarpsToFarmCabinDoor`, `ClearTerrainBelow`. **No validation.**
- All callers of `Relocate` in the mod (`grep` confirmed 5 call sites — 3 are extension internals, 2 external):
    - `Services/Commands/CabinCommand.cs:50` — the call this fix hardens.
    - `Services/CabinManager/CabinManagerService.cs:257` — migration to a designated-on-map position (always inside bounds, but already-occupied check is weak).
    - `Services/CabinManager/CabinManagerService.cs:394` — moves cabin INTO the hidden stack at `StackLocation` (≈ `(-20,-20)`). **Per `.claude/rules/cabin-system.md` invariant 5, hidden-stack and lobby coordinates are intentionally off-map.** Validation must therefore live at the call site, not inside `Relocate`.
- Vanilla `GameLocation.buildStructure` at `decompiled/sdv-1.6.15-24356/StardewValley/GameLocation.cs:16635-16717` is the reference for safety checks: footprint via `isBuildable(vector)`, per-tile farmer-bounding-box collision, optional `additionalPlacementTiles`, door-tile (`humanDoor.Y + 1`) `isBuildable || isPath`, and `isThereAnythingtoPreventConstruction`. Buildings already in `buildings` are allowed to overlap their own current footprint (`buildings.Contains(building) && building.occupiesTile(vector)` short-circuits the overlap check at line 16658).
- Vanilla `GameLocation.isBuildable` at line 17096 has a counter-intuitive quirk worth pinning down because the natural reading is "calling it on `farm` checks against the farm." Lines 17098, 17105–17116 do — they use the implicit `this`. But lines 17120, 17122, 17126 explicitly read `Game1.currentLocation.doesTileHavePropertyNoNull/doesTileHaveProperty(...)` for the "Buildable"/"Diggable" map-property checks. This isn't speculation — it's literally what the bytes say. `doesTileHaveProperty` (`GameLocation.cs:13153`) reads from `this.map` (line 13182), so when invoked through `Game1.currentLocation` it reads from whatever location the host happens to be standing in.
- In our context: the AlwaysOn host warps around (`Services/AlwaysOnServer/AlwaysOn.cs:614-639` — FarmHouse on wakeup, festival maps during festivals, etc.). When a player types `!cabin` while the host is in FarmHouse, `farm.isBuildable(tile)` reads the "Buildable" property from FarmHouse's tiles at the supplied (x, y). FarmHouse map tiles don't carry "Buildable=t" or "Diggable", so every footprint tile returns false — even legitimate farm slots. Failure mode is over-rejection (false negatives), not false acceptance. Either way the validator doesn't agree with itself across host states, so we sidestep the quirk by reading map properties from `farm.map` directly.
- Cabin building data: 5×3 footprint, `HumanDoor = (2,1)`, no `AdditionalPlacementTiles`. (`decompiled/content-1.6.15-24356/Data/Buildings.json:1480-1652`.) Door-tile-south = `(tileX+2, tileY+3)` — matches the existing `ClearTerrainBelow` at `BuildingExtensions.cs:22`.
- `OnChatMessage` runs on the game thread (SMAPI ChatBox event), so direct access to `farm.buildings`, `farmers`, `terrainFeatures`, `objects` is safe — no need for `RunOnGameThreadAsync`.
- No existing E2E test covers `!cabin`. Grep of `tests/` for `!cabin`/`CabinCommand` returned only a false positive in `CabinConcurrencyTests.cs`.

## Recommended fix

### Approach

Add validation **at the command site**, before calling `Relocate`. Implement a static helper `CabinPlacementValidator.TryValidate(Farm, Building cabin, Point topLeft, out string failureReason)` that mirrors `buildStructure`'s safety-check section without modifying state and without depending on `Game1.currentLocation`.

The helper is:

- Pure (no side effects).
- Self-overlap aware: skips tiles the cabin already occupies (so a no-op move or 1-tile shift never trips the building-overlap check).
- Reads map properties from `farm.map` directly, sidestepping the vanilla `Game1.currentLocation` quirk.
- Returns a short, player-friendly reason string on failure for `SendPrivateMessage`.

This is the simplest fix that mirrors vanilla — no preview, no ghost mode, no rollback. Per `.claude/rules/universal/simplest-solution.md`, use the existing pattern (vanilla's `buildStructure` body) rather than invent a new abstraction.

### Files to modify

1. **`mod/JunimoServer/Services/CabinManager/CabinPlacementValidator.cs`** (new file). Static helper class. One public method:

    ```csharp
    public static bool TryValidate(Farm farm, Building cabin, Point topLeft, out string failureReason)
    ```

    Implementation outline (mirrors `GameLocation.buildStructure:16637-16717`):
    1. **Footprint loop** over `cabin.tilesWide.Value` × `cabin.tilesHigh.Value`. For each `Vector2 tile = (topLeft.X + dx, topLeft.Y + dy)`:
        - Skip if `cabin.occupiesTile(tile)` AND `farm.buildings.Contains(cabin)` (self-overlap of the building being moved).
        - Bounds check: `farm.GetBuildableRectangle()` — if non-empty and does not contain the tile → `failureReason = "out of bounds"`, return false.
        - Building overlap: `var other = farm.getBuildingAt(tile); if (other != null && other != cabin && !other.isMoving)` → `failureReason = "another building is in the way"`, return false.
        - Tile occupancy / terrain: `farm.CanItemBePlacedHere(tile, false, CollisionMask.All, ~CollisionMask.Objects, useFarmerTile: true)` returns false AND not an artifact spot → `failureReason = "blocked by terrain or object"`, return false.
        - Map property: `farm.doesTileHavePropertyNoNull((int)tile.X, (int)tile.Y, "Buildable", "Back")` (note: against `farm`, NOT `Game1.currentLocation`). If `"f"` → blocked. Else require `"t"`/`"true"`, OR `"Diggable"` property present and not `"f"`.
        - Farmer collision: for each farmer in `farm.farmers`, if `farmer.GetBoundingBox().Intersects(new Rectangle((int)tile.X * 64, (int)tile.Y * 64, 64, 64))` → `failureReason = "another player is standing there"`, return false.
    2. **Door-front tile** (if `cabin.humanDoor.Value != (-1,-1)`): door-front = `(topLeft.X + cabin.humanDoor.X, topLeft.Y + cabin.humanDoor.Y + 1)`. Skip if `cabin.occupiesTile(doorFront)` AND `farm.buildings.Contains(cabin)` (self-overlap with current footprint). Otherwise apply the same inline checks used in the footprint loop (bounds, building-overlap, terrain via `CanItemBePlacedHere`, "Buildable"/"Diggable" property against `farm.map`) OR allow if `farm.isPath(doorFront)` returns true (paths are valid door-front targets per `buildStructure:16707`). `isPath` is safe to call on `farm` directly — it reads only from `terrainFeatures` and `objects`, no `Game1.currentLocation` references.
    3. **Custom prevention hook**: `cabin.isThereAnythingtoPreventConstruction(farm, new Vector2(topLeft.X, topLeft.Y))`. If non-null, use that string as `failureReason`. (Cabins don't override this in vanilla, but it's cheap insurance and matches `buildStructure`.)
    4. Return true with `failureReason = ""`.

2. **`mod/JunimoServer/Services/Commands/CabinCommand.cs`**. Replace lines 41-50:
    - Compute `var topLeft = new Point((int)farmer.Tile.X + 1, (int)farmer.Tile.Y);`.
    - `if (!CabinPlacementValidator.TryValidate(Game1.getFarm(), cabin, topLeft, out var reason))` → `helper.SendPrivateMessage(msg.SourceFarmer, $"Can't move cabin: {reason}.");` then `return;`.
    - Otherwise call `cabin.Relocate(topLeft);`.
    - Remove TODO sub-bullet (b) "Add checks to prevent placing cabin out-of-bounds, over trees, buildings etc." — the rule for that gap is now closed. Keep (a) and (c) — they're separate work (placeholder cabin in stack + ghost preview). Per `.claude/rules/universal/own-the-whole-file.md`, this is a cleanup pass; trim only what's now landed.

### Files NOT modified

- `BuildingExtensions.Relocate` stays as-is. Adding validation there would break the `(-20,-20)` and lobby-cabin call sites which intentionally place cabins off-map (`cabin-system.md` invariant 5).
- `CabinManagerService.MigrateCabins` (line 257) is out of scope. M8 in the audit covers migration's lack of pre-validation; the new helper is reusable for that fix later but wiring it in is M8's plan, not M4's.
- No changes to `FarmCabinPositions.IsPositionOccupied`. It's intentionally narrow (top-left match) for stack-position lookup and isn't a footprint-collision check.

### Why this doesn't fall into the noted traps

- **`.claude/rules/universal/preflight-check-vs-committed-config.md`** — we're not adding a fail-fast `throw`; this is a player-facing soft reject with a feedback message. The committed map (Standard farm) has cabin slots at designated positions, all of which the validator will accept (we're mirroring vanilla's `isBuildable` which the game itself uses for those slots).
- **`.claude/rules/universal/retry-is-evidence-of-root-cause.md`** — no retries added. Player invokes `!cabin`, server says yes/no once.
- **`.claude/rules/universal/simplest-solution.md`** — we considered just calling vanilla `farm.buildStructure(cabin, tileLocation, who, skipSafetyChecks: false)`. Rejected because (a) `buildStructure` mutates state on success — adds the building to `buildings` if not present, sets `tileX/tileY`, removes terrain features, fires `SendBuildingConstructedEvent` — none of which we want for a relocate of an already-placed cabin (it would double-fire the constructed event); (b) the `Game1.currentLocation` quirk in `isBuildable` is a real correctness bug for our context. Rolling our own pure validator is the cleanest fit.
- **`.claude/rules/universal/verify-claims.md`** — every named identifier (`buildStructure`, `isBuildable`, `getBuildingAt`, `CanItemBePlacedHere`, `GetBuildableRectangle`, `humanDoor`, `tilesWide/tilesHigh`, `farm.buildings`, `farm.farmers`, `farm.map`, `Cabin` size 5×3, `HumanDoor (2,1)`) was read directly from decompiled sources or current mod files in this exploration.
- **`.claude/rules/debugging.md`** — no `LogLevel.Error` calls added. The validator returns a reason string; the command logs nothing. (Pre-existing `LogLevel.Error` at `CabinManagerService.cs:263` is a separate issue, M8.)
- **`.claude/rules/asynclocal-pitfalls.md`** — handler runs on the game thread end-to-end; no async boundary, no AsyncLocal concerns.
- **`.claude/rules/netdictionary-public-surface.md`** / **`netfield-revert-pattern.md`** — not applicable. We don't mutate any `NetDictionary`/`NetField` in the validator (it's read-only) and `Relocate` already uses public API for its writes.

## Verification plan

Per `.claude/rules/universal/runtime-post-conditions-are-gates.md`, post-conditions are gates — each must be observed at runtime before declaring the fix done.

1. **Static** — `dotnet build mod/JunimoServer/JunimoServer.csproj` succeeds.
2. **Manual repro of the original bug** — run the mod via `make build` + `make up`, connect with a client, walk to the **west edge of the Farm** so `farmer.Tile.X + 1` would put the cabin's right edge off the buildable rectangle, type `!cabin`. Expect a private chat reply matching `Can't move cabin: out of bounds.` (or whatever exact string we choose). Cabin position must be unchanged afterwards (verify with `!info` debug or by re-reading `farm.buildings`).
3. **Manual happy path** — walk to a clear, in-bounds spot, type `!cabin`. Cabin moves; warp from cabin interior still leads back to the new door tile.
4. **Building-overlap rejection** — walk next to an existing building so the cabin would overlap its tiles, type `!cabin`. Expect "another building is in the way."
5. **Self-overlap is allowed** — call `!cabin` twice with no movement in between. Second call's footprint overlaps the first call's footprint by 4 of 5 columns; the self-overlap branch must let it through (no spurious "another building" message). This guards against the most likely regression in the validator.
6. **Farmer-collision rejection** — have a second player stand on the target tile, run `!cabin`. Expect "another player is standing there."
7. **Door-tile rejection** — find a configuration where the 5×3 footprint passes but the tile at `(tileX+2, tileY+3)` is water / cliff. Expect blocked. (Designed-Standard farm: place farmer such that footprint is on the dirt patch but the door drops onto the upper cliff edge.)
8. **No regression in migration** — flip `CabinStrategy` from `CabinStack` → `None` via `!admin` (or the persisted option mechanism) and confirm migration still relocates hidden cabins to designated positions. The validator is not on this path so this is a sanity check that no shared-state coupling crept in.

If any of items 2-7 fail, the fix is not complete — investigate, do not declare done.

## Critical files to read before implementation

- `mod/JunimoServer/Services/Commands/CabinCommand.cs` — call site.
- `mod/JunimoServer/Util/BuildingExtensions.cs` — `Relocate` and `ClearTerrainBelow` (do not touch).
- `decompiled/sdv-1.6.15-24356/StardewValley/GameLocation.cs:16635-17132` — `buildStructure` and `isBuildable` (the spec we mirror).
- `decompiled/sdv-1.6.15-24356/StardewValley/Buildings/Building.cs:2079-2110` — `occupiesTile`.
- `decompiled/content-1.6.15-24356/Data/Buildings.json:1480-1652` — Cabin building data (5×3, HumanDoor, no AdditionalPlacementTiles).
- `.claude/rules/cabin-system.md` invariant 5 — explains why validation cannot live inside `Relocate`.
- `.claude/rules/universal/simplest-solution.md` — why we don't reach for `buildStructure` directly.
