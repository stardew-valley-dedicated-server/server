# 03 — Multi-client cabin tests

**Blocked on plan 02 (second-farmer helper).**

Two tests that need two concurrent farmers. They close the last coverage gaps around the `HasSavedPosition` sweep exemption (PR #349).

## Test A — same-pass sweep selectivity

The existing persistence tests prove each direction separately (`MoveToStack_PlacedCabinSurvivesReload`: a saved position survives; `MoveToStack_UnclaimedCabinSweptOnReload`: an unsaved cabin is swept). This test proves the filter is *selective within a single migration pass* — one saved and one unsaved cabin handled differently by the same sweep.

Add to `tests/JunimoServer.Tests/CabinPositionPersistenceTests.cs`, reusing its helpers (`MoveCabinViaCommandAsync`, `SleepToSaveAsync`, `SwitchCabinStrategyAsync`, `ReloadServerAsync`, `GetOurCabinAsync`).

1. `CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "None", startingCabins: 2)` — both farmers get visible cabins.
2. Connect farmer A (primary) and farmer B (helper from plan 02); wait for B's farmhand to be customized so the cabin claim exists.
3. A runs `!cabin` (via `MoveCabinViaCommandAsync`); capture `aTile`. B does nothing.
4. **Disconnect B before sleeping** (wrapper disposal; B's claim persists into `farmhandData` on disconnect and is written by the upcoming save). Day-end requires every connected farmer in bed — `SleepToSaveAsync` only sleeps the primary farmer, so an idle connected B blocks the day transition.
5. `SleepToSaveAsync`, then `Farmers.DisconnectAndWaitForPersistenceAsync` for A (`/reload` 409s while connected).
6. `SwitchCabinStrategyAsync("CabinStack")`, `ReloadServerAsync()` — the None→CabinStack migration sweeps in one pass.
7. Assert via `/cabins`: A's cabin at `aTile`, `IsHidden == false`; B's cabin `IsHidden == true`. `SavedPositionPlayerIds` contains A's id only.

Catches: filter inversion (A swept, B kept) and owner-id resolution returning the wrong farmer's key — failure modes the two single-direction tests can only catch one at a time.

## Test B — two players place to distinct tiles

Lower value (the dict-keyed-by-`msg.SourceFarmer` failure mode is already caught by Test A and the existing reload test), but cheap once the helper exists. Skip if it doesn't fall out nearly free.

1. Fresh CabinStack game; connect A and B.
2. A places via the standard helper tile; B places at a second known-clear tile — `CabinPlacementHelper` hard-codes one spot (40,18), so **parametrize it** (e.g. `WarpAndClearFootprintAsync(client, tileX, tileY, ct)` with the current constants as defaults) and pick a second clear western-field tile far enough that the 5×3 footprints can't overlap.
3. Assert via `/cabins`: two distinct non-hidden tiles, each owned by the right uid; `SavedPositionPlayerIds` contains both ids.

## Notes

- Both tests live with the persistence suite (`Exclusive = true`, fresh game per test) — they mutate strategy/cabin state.
- Budget against the 300 s per-test timeout: connect ×2 + sleep-to-save + reload fits (the existing single-client reload tests run well under it), but don't add unneeded polls.
