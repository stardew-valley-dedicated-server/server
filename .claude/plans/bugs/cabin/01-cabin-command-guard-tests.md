# 01 — Cabin command guard tests

Three small single-client tests covering `!cabin` early-return and strategy paths that have no E2E coverage. All primitives exist; each test is ~20–40 LOC following established class patterns.

## Context

`CabinCommand.Register` (`mod/JunimoServer/Services/Commands/CabinCommand.cs`) has three gates before the intent write at line 84:

1. `IsFarmHouseStack` → reply "Can't move cabin. The host has chosen to keep all cabins in the farmhouse." (line 25)
2. `currentLocation.Name != "Farm"` → reply "Must be on Farm to move your cabin." (line 36)
3. `CabinPlacementValidator.TryValidate` → reply "Can't move cabin: {reason}." (line 65, covered by `CabinPlacementValidationTests`)

Gates 1 and 2 are untested. The None-strategy happy path is only exercised incidentally (inside `CabinPositionPersistenceTests.StrategySwitch_NoneToCabinStack_PlacedCabinSurvives`). All three tests guard the same regression: a refactor reordering the gates below the intent write or relocate would leave stale state on rejection.

## Test A — FarmhouseStack rejection

New class `tests/JunimoServer.Tests/CabinStrategyFarmhouseStackTests.cs`, shaped like `CabinPlacementValidationTests` (`[TestServer(Isolation = IsolationMode.SharedAssembly, Exclusive = true)]`, DisposeAsync resets via `DisconnectAsync` + `CreateNewGameOnServerAsync(farmType: 0)`; duplicate the small private `GetOurCabinAsync` helper like the other two cabin classes do, or extract it if a third copy feels like the threshold).

Note: this is the first FarmhouseStack E2E coverage at all — if the join itself misbehaves, that's a product finding to investigate, not a test bug to work around.

1. `CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "FarmhouseStack")`, connect a farmer.
2. Warp to the Farm (`CabinPlacementHelper.WarpAndClearFootprintAsync`) — proves the *strategy* rejection fires, not the off-Farm one, and keeps the test honest if the gate order ever changes.
3. Send `!cabin` via the resend-poll pattern; assert the reply via `Chat.AssertResponseAsync("!cabin", "keep all cabins in the farmhouse")` — the phrase is unique to gate 1.
4. Assert via `/cabins`: our cabin's tile unchanged, `IsHidden == true`, and `SavedPositionPlayerIds` does **not** contain our id (no intent write on rejection).
5. `Exceptions.AssertNoExceptionsAsync(...)`.

## Test B — off-Farm rejection

Add to `CabinPlacementValidationTests` (same CabinStack config, same helpers).

1. `CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack")`, connect a farmer. A fresh farmhand spawns in their cabin interior — already off-Farm, no warp needed.
2. Send `!cabin`; assert reply contains "Must be on Farm".
3. Assert via `/cabins`: cabin still `IsHidden`, tile unchanged, `SavedPositionPlayerIds` does not contain our id.

## Test C — None-strategy happy path

Add to `CabinStrategyNoneTests` (set `_needsServerReset = true` per the class pattern).

1. `CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None")`, connect a farmer. Default starting-cabin count — the same setup the green strategy-switch test uses, so the helper tile is known clear.
2. Capture our cabin's baseline tile from `/cabins` (visible map position under None — never hard-code it; it's map-derived).
3. `CabinPlacementHelper.WarpAndClearFootprintAsync`, then `!cabin` via the resend-poll pattern until the tile changes from baseline.
4. Assert: tile == `CabinPlacementHelper.ExpectedCabinTile`, `Type == "Normal"`, `IsHidden == false`, no exceptions.

## Notes

- Reuse the resend-poll shape from `CabinPlacementValidationTests.ValidPlacement_MovesCabinToFarmerTilePlusOne` (the `!cabin` handler reads the server's view of the farmer location, which can lag the warp by a tick).
- Reuse existing `WaitName.Polling_CabinPlacement_*` entries where the wait semantics match; add new enum entries otherwise.
- Per `runtime-post-conditions-are-gates.md`: done means all three tests green on real infrastructure, not compiling.
