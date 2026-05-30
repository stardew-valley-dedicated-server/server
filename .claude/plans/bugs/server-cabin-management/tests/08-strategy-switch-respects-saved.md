# Test 08 — Strategy switch None → CabinStack: cabins with saved positions stay; others move to hidden stack

## Verifies

The second bulk-mover gated by `HasSavedPosition` in Step 5 — `MigrateCabins`'s None→Stacked branch (`CabinManagerService.cs:268-281`) — respects saved positions just like `SyncExistingCabins`.

This test is structurally similar to Test 05 but exercises a **different code path**. They are not redundant: the two filters are independent additions and either could regress without the other noticing.

## Strategy

Starts in **None** strategy (visible cabins via `BuildNewCabinVisible`), runs `/cabin` for one farmer, then switches to `CabinStack` (via reload + persistent options).

## Test class & method

`tests/JunimoServer.Tests/CabinPositionPersistenceTests.cs` →
`StrategySwitch_NoneToCabinStack_RespectsSavedPosition`

## Preconditions

- Server boots with `CabinStrategy=None`, ≥ 2 starting cabins (to ensure both A and B get their own visible cabin).
- Farmer A has run `/cabin` to write `PlayerCabinPositions[uidA]`. Farmer B has not.
- Save+reload primitive (Test 03 harness extension), with a way to change `CabinStrategy` between sessions (e.g. via the existing `/settings` chat command or a `POST /settings` API; investigate `mod/JunimoServer/Services/Commands/SettingsCommand.cs` for available knobs).

## Steps

1. `await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", startingCabins: 3);`.
2. Two farmers join (A, B) and customize.
3. Drive A onto Farm at a known tile. **Note:** in None strategy, both A's cabin and B's cabin are at visible Paths-layer positions from the start — i.e. A doesn't need to be on the Farm to *prove* their cabin is visible, but `/cabin` still requires it.
4. **Capture B's pre-reload tile from `/cabins`** — do NOT hard-code Paths-layer coordinates (see README "How tests should write expected coordinates"). Variant: assert later only that B's tile **changed to (-20, -20)**, regardless of where it started.
5. `await GameClient.SendChat("/cabin");` — writes `PlayerCabinPositions[uidA]`, master cabin A relocates to the chosen tile.
6. **Save flush** — sleep through a day OR force-save (Test 03 step 7). Without this, `PlayerCabinPositions` is in-memory only and the test passes for the wrong reason.
7. Disconnect both.
8. **Switch CabinStrategy to CabinStack.** Use whichever knob is available — chat `/settings` command, file edit + reload, or a new endpoint. Investigate before committing the test.
9. **Trigger save-reload** so `OnSaveLoaded` runs. `DetectAndMigrateStrategyChange` notices the strategy change and calls `MigrateCabins(None, CabinStack)` (`CabinManagerService.cs:268-281`).
10. Read `/cabins`.

## Assertions

- A's cabin tile == the `/cabin`-chosen tile (`PlayerCabinPositions[uidA]`).
- A's cabin `IsHidden == false`.
- B's cabin tile == `(-20, -20)`.
- B's cabin `IsHidden == true`.
- `after.Strategy == "CabinStack"`.

## Harness limits

- **Same save+reload limitation as Tests 03, 04, 05.**
- **Strategy-switch primitive.** The `MigrateCabins` path is gated on `previousStrategy != currentStrategy`. The test needs *both*: a save with `previousStrategy=None` written into `PersistentOptions`, AND a fresh load where `currentStrategy=CabinStack` is the active server setting. Concretely: between disconnect and reload, the server's `server-settings.json` (or the equivalent live setting) must be flipped. The test framework already supports `CreateNewGameOnServerAsync(cabinStrategy: ...)` — but that destroys the save. Need a non-destructive variant or a separate `POST /settings` endpoint.
- **What to investigate before writing the test code**: (a) does `/settings` chat command let us flip CabinStrategy at runtime? (b) does it persist across save-load? Both unknowns — flag during implementation.

## Why this test exists

Catches:
- Step 5 added `HasSavedPosition` to `SyncExistingCabins` but **forgot to add it to `MigrateCabins`** (or vice versa) — Test 05 would still pass but A's saved position would be wiped by the strategy-switch path. Without this test, a half-finished Step 5 ships.
- The `MigrateCabins` None→Stacked branch using `cabin.SetPosition(HiddenCabinLocation)` (line 277) — a slightly different code path than `cabin.Relocate`. Any future swap to `Relocate` here without re-checking the filter could re-introduce the same bug under a different cabin-strategy lifecycle.
