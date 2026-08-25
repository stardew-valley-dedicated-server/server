# Split test-only code out of production service files

| Field             | Value                    |
| ----------------- | ------------------------ |
| **Status**        | Open — execution planned |
| **GitHub issues** | —                        |
| **Last updated**  | 2026-08-24               |

## Goal

Some services contain code that exists only for `/test/*` endpoints, currently mixed into the production file and marked by a name suffix or doc comment at best.

Move that code into a separate file per service, so **“is this production code?” is answered by the filename** — easy to grep and obvious in review diffs.

`ApiService` already follows this pattern with `ApiService.TestEndpoints.cs` (+ `.Models.cs`); use the same convention here.

**Prerequisite:** PR #553 (`feat/fast-client-join`) must be merged first; it adds `FindAvailableCabinWithOwner`.

## Convention

* One `<Service>.TestSupport.cs` per affected service, in the same namespace with a `partial class`.
* Add a file-header comment like `ApiService.TestEndpoints.cs`: test-only, reachable only through `/test/*` (gated by `Env.IsTest` at the dispatcher).
* Keep the code inside the service class as a partial, preserving access to private helpers such as `IsCabinAvailable`. This private access is why the code cannot move to a separate class.
* Mark each affected service's main file `partial` in the same change.

## What moves

| Service                     | Code                                                                                                                 | Notes                                                                                                                                                                                                                  |
| --------------------------- | -------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CabinManagerService`       | `FindAvailableCabinWithOwner`                                                                                        | Wraps private `IsCabinAvailable`, the single definition of slot availability shared with the join path. Called by `/test/stamp_claim`, `/test/precustomize_farmhand`, `/test/seed_import_source`.                      |
| `AuthService`               | `TriggerGalaxyReSignInForTest`                                                                                       | Static; only caller is `/test/galaxy_relogin`.                                                                                                                                                                         |
| `NpcSpriteIntegrityService` | `FindSpritelessNpcs`, plus `SaveLoadedRuns`, `DayStartedRuns`, `LastRunContext`, `LastRunHealedCount`, `TotalHealed` | Move the counter properties with the test-support code. The production sweep continues writing those properties; only `/test/npc_sprite_integrity` reads them. Do not move or otherwise alter those production writes. |

## What stays

Code used by both production and tests remains in the production service files:

* `AuthService`'s gated re-sign-in core (shared with the reconnect path; the test wrapper exists so the two cannot diverge).
* `SaveImportService.ExecuteImport` (console command + `/test/import_save`).
* `FarmhandOwnershipService.RecordOwner` (save-import finalizer + join gate).
* `CropSaverDataLoader.GetSaverCrop`, `CropSaverOverrides.IsManaged` (production overrides).
* `ModEventLog` (test-gated diagnostics channel called from production code).

## Verification

* Re-check the move list at execution time.
* Grep `ApiService.TestEndpoints.cs` and the affected `/test/*` dispatchers to confirm that every moved method is still reachable through its expected test endpoint.
* Confirm the moved *methods* have **no production callers** outside the test endpoint path. The diagnostic counter properties are excluded from this check — the production sweep still writes them (verified separately below); only `/test/npc_sprite_integrity` reads them.
* Confirm no test-only implementation was inadvertently left in the affected production files.
* Confirm the production `NpcSpriteIntegrityService` sweep still writes its diagnostic counters unchanged.
* `dotnet build mod/JunimoServer/JunimoServer.csproj` clean.
* `make test FILTER=NpcSpriteIntegrity` (or the suites covering the three `/test/*` consumers) green.
