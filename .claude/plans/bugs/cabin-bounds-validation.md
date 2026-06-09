# `!cabin` bounds & collision validation

## Problem

`!cabin` (`mod/JunimoServer/Services/Commands/CabinCommand.cs:60`) relocates a cabin to `farmer.Tile + (1,0)` with no validation — no bounds, building-overlap, terrain, or farmer-collision check. Cabins can land on cliffs, in water, on other buildings, off the buildable rectangle, or under the farmer; warp targets on such cabins can softlock the owner. The TODO at `CabinCommand.cs:42-47` acknowledges the gap.

Scope: validate the target before `Relocate`, reply to the player on rejection. Out of scope: preview/ghost mode, undo, rollback (TODO items a/c).

## Ground truth

- `cabin.Relocate(...)` → `BuildingExtensions.Relocate(Point)` (`mod/JunimoServer/Util/BuildingExtensions.cs:64-70`): calls `SetPosition`, `SetWarpsToFarmCabinDoor`, `ClearTerrainBelow`. No validation.
- `Relocate` external callers (3):
    - `Services/Commands/CabinCommand.cs:60` — the call this fix hardens.
    - `Services/CabinManager/CabinManagerService.cs:288` — migration to a designated on-map position.
    - `Services/CabinManager/CabinManagerService.cs:438` — moves cabin into the hidden stack at `StackLocation` (≈ `(-20,-20)`). Per `.claude/rules/cabin-system.md` invariant 5, hidden-stack/lobby coordinates are intentionally off-map — so validation lives at the command site, not inside `Relocate`.
- `GameLocation.buildStructure` (`decompiled/sdv-1.6.15-24356/StardewValley/GameLocation.cs:16635-16717`) is the reference: footprint via `isBuildable`, per-tile farmer-bounding-box collision, door-tile (`humanDoor.Y + 1`) `isBuildable || isPath`, and `isThereAnythingtoPreventConstruction`. Buildings already in `buildings` may overlap their own footprint — short-circuit at `16658` (`buildings.Contains(building) && building.occupiesTile(vector)`).
- `GameLocation.isBuildable` (`GameLocation.cs:17096`): the map-property reads at `17120/17122/17126` go through `Game1.currentLocation.doesTileHaveProperty*`, NOT the implicit `this`. `doesTileHaveProperty` (`GameLocation.cs:13153`) reads from its instance `map` (`13182`).
- The AlwaysOn host warps off-farm (e.g. into FarmHouse at `Services/AlwaysOnServer/AlwaysOn.cs:751-776`). So when a player types `!cabin` while the host stands in FarmHouse, `farm.isBuildable(tile)` would read FarmHouse's tile properties via `Game1.currentLocation` — FarmHouse tiles lack `Buildable`/`Diggable`, so every footprint tile returns false and legitimate slots are rejected. The validator reads map properties from `farm.map` directly to sidestep this.
- Cabin building data (`decompiled/content-1.6.15-24356/Data/Buildings.json:1480`): 5×3 footprint, `HumanDoor = (2,1)`, no `AdditionalPlacementTiles`. Door-tile-south = `(tileX+2, tileY+3)`, matching `ClearTerrainBelow` (`BuildingExtensions.cs:22`).
- `isPath` (`GameLocation.cs:17083`) reads only `terrainFeatures`/`objects` — safe to call on `farm` directly.
- The `!cabin` handler runs on the game thread (SMAPI ChatBox event); direct access to `farm.buildings`/`farmers`/`terrainFeatures`/`objects` is safe.
- No E2E test covers `!cabin` (grep of `tests/` returned only an unrelated `CabinConcurrencyTests.cs` hit).

## Fix

A pure static helper validates the target before `Relocate`, mirroring `buildStructure`'s safety checks without mutating state and without reading `Game1.currentLocation`.

### 1. New file: `mod/JunimoServer/Services/CabinManager/CabinPlacementValidator.cs`

```csharp
public static bool TryValidate(Farm farm, Building cabin, Point topLeft, out string failureReason)
```

Mirrors `buildStructure:16637-16717`:

1. **Footprint loop** over `cabin.tilesWide.Value` × `cabin.tilesHigh.Value`. For each `Vector2 tile = (topLeft.X + dx, topLeft.Y + dy)`:
    - Skip if `farm.buildings.Contains(cabin) && cabin.occupiesTile(tile)` (self-overlap of the building being moved).
    - Bounds: `farm.GetBuildableRectangle()` non-empty and not containing the tile → `"out of bounds"`.
    - Building overlap: `var other = farm.getBuildingAt(tile); if (other != null && other != cabin && !other.isMoving)` → `"another building is in the way"`.
    - Terrain/occupancy: `!farm.CanItemBePlacedHere(tile, false, CollisionMask.All, ~CollisionMask.Objects, useFarmerTile: true)` and not an artifact spot (`farm.getObjectAtTile((int)tile.X, (int)tile.Y)?.QualifiedItemId == "(O)590"`, mirroring `isBuildable:17116` — there is no `isArtifactSpot` helper) → `"blocked by terrain or object"`.
    - Map property against `farm`: `farm.doesTileHavePropertyNoNull((int)tile.X, (int)tile.Y, "Buildable", "Back")`. `"f"` → blocked; else require `"t"`/`"true"`, OR a `"Diggable"` property present and not `"f"`.
    - Farmer collision: for each `farmer in farm.farmers`, `farmer.GetBoundingBox().Intersects(new Rectangle((int)tile.X * 64, (int)tile.Y * 64, 64, 64))` → `"another player is standing there"`. (Absolute tile coords — vanilla at `buildStructure:16668` uses loop-index offsets, a vanilla bug we don't reproduce.)
2. **Door-front tile** (if `cabin.humanDoor.Value != (-1,-1)`): `(topLeft.X + cabin.humanDoor.X, topLeft.Y + cabin.humanDoor.Y + 1)`. Skip if `farm.buildings.Contains(cabin) && cabin.occupiesTile(doorFront)`. Else apply the same bounds / building-overlap / terrain / map-property checks, OR allow if `farm.isPath(doorFront)` (paths are valid door-front targets per `buildStructure:16707`).
3. **Custom prevention hook**: `cabin.isThereAnythingtoPreventConstruction(farm, new Vector2(topLeft.X, topLeft.Y))` — if non-null, use as `failureReason`.
4. Return true with `failureReason = ""`.

### 2. `mod/JunimoServer/Services/Commands/CabinCommand.cs`

- Remove the TODO sub-bullet (b) at line 43 (`Add checks to prevent placing cabin out-of-bounds...`); keep (a) and (c) — separate work.
- Before the `PlayerCabinPositions` intent-write (currently lines 52-58), insert:
    ```csharp
    var topLeft = new Point((int)farmer.Tile.X + 1, (int)farmer.Tile.Y);
    if (!CabinPlacementValidator.TryValidate(Game1.getFarm(), cabin, topLeft, out var reason))
    {
        helper.SendPrivateMessage(msg.SourceFarmer, $"Can't move cabin: {reason}.");
        return;
    }
    ```
    The guard must precede the intent-write so a rejected move records no false intent. Reuse `topLeft` for the existing `newPosition`/`Relocate` call rather than recomputing.

### Not modified

- `BuildingExtensions.Relocate` — validation there would break the off-map call sites (`cabin-system.md` invariant 5).
- `CabinManagerService.MigrateCabins` (`:288`) — its pre-validation gap is M8's plan; the helper is reusable there later.
- `FarmCabinPositions.IsPositionOccupied` — intentionally a top-left-match stack lookup, not a footprint check.

### Why not vanilla `buildStructure` directly

`buildStructure` mutates on success (adds to `buildings`, sets `tileX/tileY`, removes terrain, fires `SendBuildingConstructedEvent` — which would double-fire for an already-placed cabin), and its `isBuildable` carries the `Game1.currentLocation` quirk. A pure read-only validator avoids both.

## Verification (post-conditions are gates)

1. **Build** — `dotnet build mod/JunimoServer/JunimoServer.csproj`.
2. **Bug repro** — at the west Farm edge so `farmer.Tile.X + 1` puts the right edge off-bounds, `!cabin` → reply `Can't move cabin: out of bounds.`; cabin position unchanged.
3. **Happy path** — clear in-bounds spot, `!cabin` → cabin moves; interior warp leads to the new door tile.
4. **Building overlap** — next to a building, `!cabin` → `another building is in the way`.
5. **Self-overlap allowed** — `!cabin` twice without moving; second call's footprint overlaps the first by 4/5 columns and must pass (no spurious overlap message).
6. **Farmer collision** — second player on the target tile, `!cabin` → `another player is standing there`.
7. **Door-tile rejection** — footprint passes but `(tileX+2, tileY+3)` is water/cliff → blocked.
8. **Migration regression** — flip `CabinStrategy` `CabinStack → None`; migration still relocates hidden cabins (validator not on this path; sanity check).

If any of 2-7 fail, the fix is not complete.

## Read before implementing

- `mod/JunimoServer/Services/Commands/CabinCommand.cs` — call site.
- `mod/JunimoServer/Util/BuildingExtensions.cs` — `Relocate`, `ClearTerrainBelow` (do not touch).
- `decompiled/sdv-1.6.15-24356/StardewValley/GameLocation.cs:16635-17132` — `buildStructure`, `isBuildable`.
- `decompiled/sdv-1.6.15-24356/StardewValley/Buildings/Building.cs` — `occupiesTile`.
- `decompiled/content-1.6.15-24356/Data/Buildings.json:1480` — Cabin data.
- `.claude/rules/cabin-system.md` invariant 5.
