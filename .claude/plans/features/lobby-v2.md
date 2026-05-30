# Plan: Generalize LobbyService to Support Shed (and Cabin) Building Types

## Goal
Refactor LobbyService to support both **Shed** and **Cabin** as lobby building types, using `DecoratableLocation` as the common abstraction. Admins can choose: Shed for a big open room, Cabin for multi-room layouts. Default new lobbies to Shed.

## Scope
- **Lobby system only** — CabinManager (player cabins) stays unchanged
- Support both Cabin and Shed, storing building type per layout
- Generalize `GetIndoors<Cabin>()` → `DecoratableLocation` / `GameLocation` where possible
- Cabin-specific cleanup (farmhand, gift boxes) becomes conditional
- Backwards-compatible with existing saved layouts (missing `BuildingType` → treated as `"Cabin"`)

## Key Reference Data
- **Shed map** (13×14 interior): warp/entry at tile **(6, 14)**, door block needed around that area
- **Cabin map** (upgrade 0): entry at **(3, 11)**, door block at tiles (2,12), (3,12), (4,12)
- **Common base**: `DecoratableLocation` has `furniture`, `appliedWallpaper`, `appliedFloor`, `SetWallpaper()`, `SetFloor()`, `objects`
- **Cabin-only**: `HasOwner`, `DeleteFarmhand()`, `CreateFarmhand()`, `getEntryLocation()`, `upgradeLevel`, starter gift boxes
- **Shed has none of that baggage**

## Files to Modify

| File | Changes |
|------|---------|
| `mod/.../Lobby/LobbyLayout.cs` | Add `BuildingType` property |
| `mod/.../Lobby/LobbyService.cs` | Core refactor (18 `GetIndoors<Cabin>()` sites + signature widenings) |
| `mod/.../Commands/LobbyCommands.cs` | Widen `GetIndoors<Cabin>()` calls |
| `mod/.../AuthService/FarmhandSenderService.cs` | Update `IsLobbyCabin` call site |
| `mod/.../CabinManager/CabinManagerService.cs` | Update `IsLobbyCabin` call site |
| `mod/.../PasswordProtection/PasswordProtectionService.cs` | Update 5 `IsLobbyCabin` call sites |
| `mod/.../Util/CabinPositions.cs` | See Step 2a — lobby-position semantics already live here |

## Implementation Steps

### Step 1: Data Model — `LobbyLayout.cs`

Add `BuildingType` property with backwards-compatible default:

```csharp
public string BuildingType { get; set; } = "Cabin";  // "Cabin" default preserves existing layouts
```

Existing saved layouts without this field deserialize as `"Cabin"` (JSON default for missing string property). New layouts created from scratch will explicitly set `"Shed"` or `"Cabin"`.

`CabinSkin` and `UpgradeLevel` remain but are only meaningful for Cabin layouts.

### Step 2: Rename & Widen `IsLobbyCabin` → `IsLobbyBuilding`

**LobbyService.cs:77-81** (current implementation):

```csharp
public static bool IsLobbyCabin(Building building)
{
    if (building == null || !building.isCabin) return false;
    return CabinPositions.IsLobbyOrEditing(building);
}
```

Becomes:

```csharp
public static bool IsLobbyBuilding(Building building)
{
    if (building == null) return false;
    var type = building.buildingType.Value;
    if (type != "Cabin" && type != "Shed") return false;
    return CabinPositions.IsLobbyOrEditing(building);
}
```

Update the 8 external callers to use new name:
- `mod/JunimoServer/Services/AuthService/FarmhandSenderService.cs:424` (called from `IsLobbyCabinFarmhand` at `:420`)
- `mod/JunimoServer/Services/CabinManager/CabinManagerService.cs:528`
- `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:236`
- `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:764`
- `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:791`
- `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:815`
- `mod/JunimoServer/Services/PasswordProtection/PasswordProtectionService.cs:845`
- Internal use at `LobbyService.cs:539`

`ApiService.cs` contains no `IsLobbyCabin` reference; nothing to update there.

**Note on `FarmhandSenderService.IsLobbyCabinFarmhand()`** (`FarmhandSenderService.cs:420`): This calls `GetCabin()` which only finds Cabin buildings. For Shed lobbies there's no farmhand at all (Sheds don't generate one), so it correctly returns false. No change needed to this helper's internal logic, just the `IsLobbyCabin` → `IsLobbyBuilding` rename at the call site (`:424`).

### Step 2a: `CabinPositions` already classifies lobby positions

Lobby-position semantics (`-21` / `-20` X-axis split, individual-lobby Y range, editing position) live at `mod/JunimoServer/Util/CabinPositions.cs:20-48` and route through `BuildingExtensions.IsLobbyOrEditing` (`Util/BuildingExtensions.cs:32`). The current `IsLobbyCabin` already delegates here (`LobbyService.cs:80`).

Audit `CabinPositions.Classify` and `IsLobbyOrEditing` to confirm they don't filter on `isCabin` internally — if they do (currently they appear to operate on coordinate-only signals via `building.tileX`/`tileY`), no change is needed; the building-type widening happens at the callers (`IsLobbyBuilding` and `CabinManagerService.cs:272/337/528` which still combine `b.isCabin` with `IsLobbyOrEditing`/`IsInHiddenStack`). Plan to:
- Leave `CabinPositions` coordinate-only.
- Decide per call-site whether to keep the `b.isCabin` AND-clause (Cabin-only sites: master cabin counts) or drop it (lobby sites: now widened to include Shed).

### Step 3: Add Helper Method + Door Block Tiles for Shed

**LobbyService.cs** — add Shed door block constant and helper:

```csharp
private static readonly Vector2[] DoorBlockTilesShed = new[]
{
    new Vector2(5, 14),
    new Vector2(6, 14),
    new Vector2(7, 14)
};

private static DecoratableLocation GetLobbyInterior(Building building)
    => building?.GetIndoors() as DecoratableLocation;
```

### Step 4: Generalize Interior Access — Bulk `GetIndoors<Cabin>()` Changes

There are 18 `GetIndoors<Cabin>()` (and one bare `GetIndoors()`) call sites in LobbyService.cs (verify with `Grep -n GetIndoors mod/JunimoServer/Services/Lobby/LobbyService.cs`). Categorize each by the operation it feeds:

- **Returns/asserts `NameOrUniqueName`** → `GetLobbyInterior(building)?.NameOrUniqueName` (works on `GameLocation`).
- **Furniture / wallpaper / flooring / objects** → `GetLobbyInterior(building)` returning `DecoratableLocation`.
- **Lighting reflection (`SetCabinDaylightMode`)** → `building.GetIndoors()` typed as `GameLocation`.
- **`DeleteFarmhand()`, `HasOwner`, upgrade-level reads** → keep `GetIndoors<Cabin>()` guarded by `if (building.buildingType.Value == "Cabin")` or `is Cabin`.

Re-categorize each site by what it feeds, not by its line number. Current call sites: `LobbyService.cs:764, 907, 994, 1300, 1326, 1337, 1634, 1650, 1668, 1743, 1819, 1826, 1905, 1946, 2018, 2040, 2321, 2503` — re-verify with Grep immediately before editing.

### Step 5: Widen Method Signatures

**`SetCabinDaylightMode(Cabin, bool)` → `SetDaylightMode(GameLocation, bool)`**
- The reflection targets `GameLocation.indoorLightingColor` — already works on any GameLocation

**`SerializeCabin(Cabin)` → `SerializeInterior(DecoratableLocation)`**
- `furniture`, `objects`, `appliedWallpaper`, `appliedFloor` are all on `DecoratableLocation`
- `upgradeLevel`: use `(location as FarmHouse)?.upgradeLevel ?? 0`
- `ParentBuilding` is on `GameLocation` — still works
- Set `layout.BuildingType` from the building

**`DeserializeToCabin(Cabin, LobbyLayout)` → `DeserializeToInterior(DecoratableLocation, LobbyLayout)`**
- All APIs used (`furniture.Clear/Add`, `objects.Clear/Add`, `SetWallpaper`, `SetFloor`) are on `DecoratableLocation`

**`PlaceDoorBlockingFurniture(Cabin)` → `PlaceDoorBlockingFurniture(DecoratableLocation, string buildingType)`**
- Select tile array based on building type

**`GetSafeEntryPoint(Cabin)` → `GetSafeEntryPoint(GameLocation, string buildingType)`**
- Cabin fallback: `(location as FarmHouse)?.getEntryLocation() ?? new Point(3, 11)`
- Shed fallback: `new Point(6, 13)` (one tile above the warp at 6,14)

### Step 6: Split Cleanup Logic

**`CleanupLobbyCabinInterior(Cabin)` → `CleanupLobbyInterior(GameLocation)`**

```
Generic (both types):
  - Remove warps targeting "Farm"

Cabin-only (guarded by `is Cabin`):
  - DeleteFarmhand()
  - Remove starter gift boxes
```

### Step 7: Parameterize Building Creation

**`CreateLobbyCabin` → `CreateLobbyBuilding(GameLocation, Point, LobbyLayout)`**

```csharp
var buildingType = layout?.BuildingType ?? "Shed";
var building = new Building(buildingType, position.ToVector2());
if (buildingType == "Cabin")
    building.skinId.Value = layout?.CabinSkin ?? "Log Cabin";
building.magical.Value = true;
building.daysOfConstructionLeft.Value = 0;
building.load();
```

### Step 8: Update Building Search

**`FindOrCreateLobbyCabin` → `FindOrCreateLobbyBuilding`**

Change the search filter from `b.isCabin` to `(b.isCabin || b.buildingType.Value == "Shed")` when looking for existing lobby buildings at hidden coordinates.

If the existing building's type doesn't match the active layout's type, destroy and recreate. Two **`cabin-system.md`** invariants apply at this branch:

- **Invariant 6 — Cabin path**: when recreating a Cabin lobby, `buildStructure` (via `NetworkTweaker.SendBuildingConstructedEvent_Prefix → location.OnBuildingConstructed → Cabin.CreateFarmhand`) creates a farmhand for ownerless cabins. Lobby cabins must remain ownerless, so the existing `CleanupLobbyCabinInterior` deletes the master-created farmhand. **Keep** that cleanup for the Cabin path.
- **Invariant 6 — Shed path**: `Shed`'s construction handler does not create a farmhand. The cleanup-side `DeleteFarmhand` guard is therefore unnecessary for Shed lobbies — skip it entirely. Cleanup for Shed reduces to: remove warps targeting `"Farm"`, no farmhand work.

The plan's Step 6 (split cleanup logic) already names this divergence; this step is the create-side mirror.

### Step 9: Update Entry Points

**`GetLobbyEntryPoint(long playerId)`:**
1. Check custom spawn point from layout first (already does this)
2. For Cabin: use `getEntryLocation()` as before
3. For Shed: default to `new Point(6, 13)` (one tile above warp)

### Step 10: Update SaveCurrentLayout Type Check

`SaveCurrentLayout` is at `mod/JunimoServer/Services/Lobby/LobbyService.cs:2007` (re-verify). The `session.Cabin?.GetIndoors<Cabin>()` extraction at `:2018` and the corresponding type pattern need to widen to `DecoratableLocation`.

Set `layout.BuildingType` from the editing session's building type.

### Step 11: Update ResetEditingCabin

`ResetEditingCabin` is at `mod/JunimoServer/Services/Lobby/LobbyService.cs:2271` (re-verify). Same widening: change Cabin-typed extraction to `DecoratableLocation`.

### Step 12: Update EnsureDefaultLayout

Set default layout `BuildingType = "Shed"` for new installations. Existing saves with a "default" layout already present won't be affected.

### Step 13: Update CleanupOrphanedIndividualLobbies

Widen filter from `b.isCabin` to include Shed. Guard `DeleteFarmhand` with `is Cabin` check.

### Step 14: Update LobbyCommands.cs

Lines 154 and 203: Change `GetIndoors<Cabin>()` to use `GetLobbyInterior()` or `GetIndoors<DecoratableLocation>()`. Update `GetSafeEntryPoint` calls to pass building type.

### Step 15: Update Field Names (cosmetic, low priority)

Rename internal fields for clarity:
- `_sharedLobbyCabin` → `_sharedLobbyBuilding`
- Session tuple field `.Cabin` → `.Building`

## Implementation Order

1. **LobbyLayout.cs** — add `BuildingType` (1 line, no behavior change)
2. **LobbyService.cs** — add helper method `GetLobbyInterior()`, Shed door block tiles
3. **LobbyService.cs** — widen method signatures (Steps 5-6)
4. **LobbyService.cs** — bulk change `GetIndoors<Cabin>()` calls (Step 4)
5. **LobbyService.cs** — parameterize building creation + search (Steps 7-8)
6. **LobbyService.cs** — entry points and save/reset (Steps 9-11)
7. **LobbyService.cs** — default layout + orphan cleanup (Steps 12-13)
8. **LobbyService.cs** — rename `IsLobbyCabin` → `IsLobbyBuilding` (Step 2)
9. **External callers** — update 3 files (Step 2)
10. **LobbyCommands.cs** — widen type usage (Step 14)
11. **Field renames** (Step 15)

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Shed door block tiles wrong | Verify against Shed.tmx warp coordinates — warp is at (6,14), block tiles around that row |
| Existing layouts break | `BuildingType` defaults to `"Cabin"` for deserialized layouts missing the field |
| Building type mismatch at runtime | If active layout type ≠ existing building type, destroy and recreate |
| Export/import compat | SDVL0 format is self-describing JSON; new `BuildingType` field is additive. Old exports import as Cabin |
