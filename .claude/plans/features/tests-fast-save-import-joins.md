# Fast setup-joins in SaveImportTests

## Context

`SaveImportTests` still uses the slow vanilla join everywhere, in two shapes:

- `GenerateSourceSaveAsync` — the shared helper serving every test in the class — connects a client so `/test/force_save` captures a customized farmhand into the source save (`saveFarmhands` clones the connected client's live root into `farmhandData`).
- Two tests additionally connect their own client afterwards: `Import_Reload_RefusesWhenClientConnected` (needs a connected peer so `saves reload` refuses) and `Import_ForceReload_KicksThenFinalizes` (needs a kick target). Both assert on the reload/kick outcome, not on the join.

The class is `[TestServer(Exclusive = true)]`, so its tests serialize — unlike most of the suite, per-join savings here land directly on serial wall-time. ~12 slow joins × ~2.5–3s each.

**Prerequisite:** the fast setup-join infrastructure must be in-tree — `/test/precustomize_farmhand`, `ServerApiClient.PrecustomizeFarmhands`, `FarmerTestHelper.ConnectFastAsync`, and the `wasCustomizedFastPath` diagnostic.

## Why a blanket swap is wrong

- **Spare-cabin arithmetic.** `Import_SwapHost_UpgradedHouse_BuildPath_MovesContents` pins `startingCabins: 1` precisely so the import finalizer finds no spare assignable cabin and exercises the cabin **build** path. Precustomization consumes the spare before the join, and the join-time `EnsureAtLeastXCabins` backfills a fresh hidden cabin — a spare would then exist at import time, the finalizer would silently take the **assign** path, and the test would pass without exercising what it exists for.
- **The customized-wait doubles as a join-settled gate.** The helper's `WaitForFarmhandByNameAsync(requireCustomized: true)` returns on the first poll for a precustomized slot, so it stops proving the client is in-world. Fast-join callers need the settled signal re-based on server-side presence (`WaitForPlayerByIdAsync`).
- `Import_SwapHost_AllowsSameBindIdOnMultipleFarmhands` seeds spare farmhand slots for its collision setup — audit its slot arithmetic before opting it in.

## Design

Parameterize `GenerateSourceSaveAsync` with an opt-in fast join (default slow), and opt in per caller only after auditing that caller's slot arithmetic. Keep the build-path test (and any other spare-count-sensitive caller) on the slow path permanently, with a comment naming the reason so a later cleanup doesn't "simplify" it away. Swap the two direct sites (`Import_Reload_RefusesWhenClientConnected`, `Import_ForceReload_KicksThenFinalizes`) after confirming the precustomize-triggered backfill doesn't perturb any spare-count assertion in those tests (class config is `StartingCabins = 2`).

## Verification

Full `SaveImportTests` run: green, plus per-site `wasCustomizedFastPath=true` in `infrastructure.jsonl`, plus explicit evidence that the build-path test still exercised the build path (the finalizer's cabin-build log line in the server container log — not just green), per `passing-test-isnt-proof-the-scenario-ran.md`.
