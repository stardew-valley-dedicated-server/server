# Test 03 — CabinStack: `/cabin` then server restart → own cabin still at chosen tile

## Verifies

The end-to-end persistence promise: `PlayerCabinPositions` is written to save data by `Data.Write()` (Step 3), survives serialization, is restored by `Data.Read()` on `OnSaveLoaded` (Step 1), and the bulk-mover `SyncExistingCabins` MoveToStack branch respects it (Step 5). Without all three steps, this test fails.

This is **the test that makes the fix worth shipping**. The original bug is "position lost on restart"; this test is the regression gate.

## Strategy

CabinStack with `ExistingCabinBehavior=MoveToStack`. The MoveToStack setting is what makes the bug deterministic: with `KeepExisting`, the position survives by accident (no bulk-mover runs); MoveToStack is the failure mode.

## Test class & method

A new file (this test owns the server) — `tests/JunimoServer.Tests/CabinPositionPersistenceTests.cs` →
`CabinStack_MoveToStack_CabinCommand_PositionSurvivesRestart`

Mirror `CabinStrategyNoneTests`: `[TestServer(Isolation = IsolationMode.SharedAssembly, Priority = 90, Exclusive = true)]` plus `_needsServerReset = true` cleanup so the post-test farm goes back to defaults via `CreateNewGameOnServerAsync`.

## Preconditions

- A server with CabinStrategy=CabinStack and ExistingCabinBehavior=MoveToStack.
- The harness must support either:
  - **A**: Restarting the SAME save (preserving the save volume / mod data), OR
  - **B**: A `POST /admin/reload-save` endpoint that re-runs `OnSaveLoaded` against the on-disk save without recreating the world.

Today, neither A nor B exists — see "Harness limits".

## Steps (gated on harness extension)

1. `await CreateNewGameOnServerAsync(farmType: 0, cabinStrategy: "CabinStack");` — start with a known fresh state on the exclusive server.
2. **(Pre-req for `/cabin` to land on Farm.)** Issue admin-warp to get the farmer on the Farm at a known tile (see Test 02 harness limits).
3. `var client = await Farmers.ConnectNewAsync(ct: TestCt);` — join.
4. Capture `expectedTile = (farmer.Tile.X + 1, farmer.Tile.Y)` from `GameClient.GetState()`.
5. `await GameClient.SendChat("/cabin");` — moves master cabin AND writes `PlayerCabinPositions[uid] = expectedTile` to in-memory mod data (Step 3).
6. Poll `/cabins` until `ourCabin.TileX == expectedTile.X && ourCabin.TileY == expectedTile.Y`.
7. **Trigger a real game save.** `Helper.Data.WriteSaveData(...)` is in-memory until SMAPI's serialise hook fires, which only happens during a game-side save event. Without this step, the test is testing nothing — both the dict and the master cabin position only exist in memory and would be lost on any restart regardless of the fix. Options:
   - **(7a) Sleep through a day.** Real day-end runs the full save path. Wall-cost ~30–60 s with `SERVER_TPS=15`. Most realistic.
   - **(7b) Force-save chat command.** Investigate whether `/save` (or similar) exists in the chat command surface; if not, add one in the same fix. Cheap and deterministic.
   - **(7c) Force `IModHelper.Data.WriteSaveData` to flush via SMAPI internals.** Brittle, version-dependent. Rejected.
8. **Disconnect**, then trigger save-load. Three options:
   - **A**: `POST /newgame` won't work — it nukes save data including `PlayerCabinPositions`. Reject.
   - **B**: New `POST /admin/reload-save` endpoint that re-runs the engine's load-save path against the on-disk save (the same mechanism `saves select <name> --confirm` already preps via `_gameLoader.SetSaveNameToLoad`, but the existing path requires a process restart). The `GameLoaderService` already exists and exposes the relevant entry points; investigate whether it can be invoked mid-run without a Game1 reset.
   - **C**: Container-level restart preserving save volume. `ServerContainer.cs` has no `RestartAsync` today — would need a new primitive that preserves the save bind-mount but recycles the SMAPI process. ~150 LOC plus broker integration. Most realistic for "real" cold-start coverage.
9. After the reload completes, **re-assert via `/cabins`**:
   - `var after = await ServerApi.GetCabins(TestCt);`
   - Locate `var ourCabinAfter = after.Cabins.First(c => c.OwnerId == uid);`
10. Assert `ourCabinAfter.TileX == expectedTile.X && ourCabinAfter.TileY == expectedTile.Y`.

## Assertions

- After reload, master cabin tile equals the pre-reload tile (the `/cabin`-chosen tile).
- `IsHidden == false`.
- `OwnerId` unchanged.

## Harness limits

This test requires harness support that **does not exist today**. Three options to land it, ranked by cost/coverage:

| Option | Investigation needed | Cost | Coverage |
|---|---|---|---|
| **B** — `POST /admin/reload-save` endpoint | Can `GameLoaderService` be invoked while a game is already loaded? `Game1.LoadGame` resets engine state — needs to verify it's safe to re-enter. Possibly 1–3 days if the existing `/newgame` path's `Game1` teardown sequence can be reused without the "create new save" branch. | Hard to estimate before investigation. **~50–80 LOC API surface** plus the engine-side reload work, which is the unknown. | Exercises the exact `OnSaveLoaded` path the bug lives in. Best gate per LOC if it works. |
| **C** — container-level restart with save-volume preservation | Volume mount is already present (server uses persistent save bind mount in test config — verify by reading `ServerContainer.cs`'s mount setup). Just need a `RestartAsync` that does `Stop` + restart the SMAPI process. | ~150 LOC in `ServerContainer` + broker integration. Per-test wall-cost +30–60 s for server boot. | Most realistic; exercises SMAPI cold start AND the engine load path the bug actually fires on. Slower per test but least likely to deceive. |
| **A** — pre-staged save fixture | New fixture pipeline (write a binary save with `PlayerCabinPositions` in mod data, boot server pointing at it). Save format is binary, version-coupled to SDV. | Fragile; binary save format is owner of breakage. | Same gate as C, but harder to maintain. |

**Recommendation**: option **C** has the lowest investigation risk and the most realistic coverage; recommend authoring it as part of this fix. The container-restart primitive has independent value (e.g. testing crash-recovery scenarios). Option B can come later if operators want a no-docker-restart path.

Per `runtime-post-conditions-are-gates.md`, this is a runtime gate, not an "ideally we'd verify." A fix that compiles and looks right in static review but doesn't actually persist re-creates the user-visible bug. **Do not merge the fix without this test green.** If C cannot land in the same window as the fix, a manual sign-off step is required: a maintainer runs the fix locally with a real Stardew Valley client, confirms `/cabin`, sleeps through to next day in-game, restarts the server, and confirms the position survives. The result is recorded in the PR description.

## Why this test exists

This is the regression gate for issue #64. Without it:
- Step 1's `Read()` not actually copying `PlayerCabinPositions` (typo, wrong field name) goes undetected.
- Step 5's `HasSavedPosition` filter mis-evaluating (e.g. `ownerId == 0` due to import-time race) goes undetected.
- A future refactor that introduces a new bulk-mover not gated by `HasSavedPosition` re-creates the bug; only this test fails.
- Step 3's `Data.Write()` order regression (writing AFTER `cabin.Relocate` so a crash leaves move-but-no-intent) is silently accepted.
