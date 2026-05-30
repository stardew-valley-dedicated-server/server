---
paths:
  - "mod/JunimoServer/Services/CabinManager/**"
  - "mod/JunimoServer/Services/GameLoader/**"
  - "mod/JunimoServer/Services/GameCreator/**"
---

# Cabin allocation invariants

When editing cabin allocation, slot assignment, or new-game setup, the following invariants must hold:

1. **`Game1.startingCabins` must be set AFTER `loadForNewGame()`.** `loadForNewGame()` calls `resetVariables()` which resets `startingCabins` to 0; setting it earlier is silently discarded.
2. **`DELETE /farmhands` must call `EnsureAtLeastXCabins(minEmptyCabins=1)` after removing a farmhand and its cabin building.** Otherwise the next join sees an exhausted cabin pool.
3. **`sendAvailableFarmhands` (Harmony patched) must call `EnsureAtLeastXCabins` before filtering.** Rapid delete/rejoin cycles otherwise exhaust all cabins.
4. **Concurrent joins need `SlotSelectionGate` (a `SemaphoreSlim`).** `FarmhandMenu` only exposes ONE uncustomized slot at a time (index 0); two clients racing without the gate both claim index 0 and one is kicked.
5. **Lobby cabins live at tile (-21,-21); the regular cabin stack is at (-20,-20).** Don't conflate these coordinate sets when patching cabin placement logic.
6. **`buildStructure` already creates the farmhand on master — don't add an explicit `CreateFarmhand` after it.** `NetworkTweaker.SendBuildingConstructedEvent_Prefix` calls `location.OnBuildingConstructed(building, who)` directly, which runs `performActionOnConstruction → Cabin.CreateFarmhand` for ownerless cabins. Lobby cabins must remain ownerless by design — `CleanupLobbyCabinInterior` deletes the master-created farmhand.

**Why:** Each invariant traces back to a real cabin-allocation bug — the `startingCabins` reset caused empty-farm spawns until ordering was fixed; the filter-before-ensure bug caused new joiners to see "server full" after a few rotations; `SlotSelectionGate` was added after two clients collided on index 0 and one got kicked. Invariant 6 documents the contract established by the `SendBuildingConstructedEvent_Prefix` patch: master runs the construction handler exactly once, peers don't run it at all.

**How to apply:** When editing `CabinManagerService.cs`, `GameCreatorService.cs`, `GameLoaderService.cs`, or any code calling `loadForNewGame()` / `BuildStartingCabins` / `EnsureAtLeastXCabins`, walk through all five invariants above. The map-position reference for `BuildStartingCabins` (tile 29/30 in Paths layer) is in [`docs/developers/architecture/game-engine-notes.md`](../../docs/developers/architecture/game-engine-notes.md).
