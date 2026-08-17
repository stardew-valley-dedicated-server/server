# Fast second-farmer setup-join

## Context

The fast setup-join (`FarmerTestHelper.ConnectFastAsync` + `POST /test/precustomize_farmhand`) covers only the primary client's joins. The second-farmer helpers — `ConnectSecondFarmerAsync` and `ConnectBothConcurrentlyAsync`, both funneling into `JoinSecondFarmerAsync` (`tests/JunimoServer.Tests/Infrastructure/Fixture/FarmerTestHelper.cs`) — still drive the full vanilla join UI (select → CharacterCustomization → confirm), which costs ~2.5–3s per join at quiescence and more under contention (measured via the `connect_phase_completed` / `world_ready_completed` diagnostics).

**Prerequisite:** the fast setup-join infrastructure must be in-tree — `/test/precustomize_farmhand` (`ApiService.TestEndpoints.cs`), `ServerApiClient.PrecustomizeFarmhands`, `CabinManagerService.FindAvailableCabinWithOwner`, and the `wasCustomizedFastPath` diagnostic.

## Design

Opt-in per call site, never a blanket helper change: add `bool fastJoin = false` to `ConnectSecondFarmerAsync` and `ConnectBothConcurrentlyAsync`, threaded into `JoinSecondFarmerAsync`. When set, call `ServerApi.PrecustomizeFarmhands` with the generated name before `JoinWorldViaLanAsync`; `ConnectionHelper.PickSlot`'s `preferExistingFarmer` default then matches the precustomized slot by name and the existing already-customized fast path fires with no further changes.

Second farmers join via LAN by design (see the `ConnectSecondFarmerAsync` doc comment), and a precustomized slot (`isCustomized` set, `userID` empty) is selectable by any LAN client — no transport hazard.

## Consumer classification (re-verify against the tree at implementation time)

- `CropSaverTests` — both `WhileOwnerOffline` day-transition driver joins: setup-only → opt in.
- `CabinPositionPersistenceTests` `SameSweep_KeepsPlacedCabin_SweepsUntouchedCabin` and `TwoPlayers_PlaceCabinsToDistinctTiles` — farmer B's "customized claim exists before the sweep" precondition is a state gate, which precustomization pre-satisfies → likely opt in; confirm the customize wait is not also serving as a join-settled gate.
- `CabinPlacementValidationTests` `AnotherPlayerInFootprint_RejectsAndDoesNotMove` — the class's `SettleJoinAsync` keys "join settled" on customized-in-`/farmhands`, which precustomization pre-satisfies into a no-op → leave slow (same reason the class's primary joins stay slow).
- `FarmhandVisibilityTests` `MixedTransports_HideCrossTransportFarmhands` — the LAN farmer's slot list IS the assertion, and a precustomized unstamped slot changes what a fresh LAN list shows → leave slow.
- `WeddingTests` / `LobbyHomedSpouseTests` (via `ConnectBothConcurrentlyAsync`) — the back-to-back join overlap is part of the two-same-day-weddings setup and the class is timing-sensitive → classify with care; if opted in, verify across repeated runs.

## Verification

Green alone is not proof — a slow-path fallback also passes (`passing-test-isnt-proof-the-scenario-ran.md`). For each opted-in site, read the run's `infrastructure.jsonl` and confirm the second farmer's join emitted `wasCustomizedFastPath=true` and no `character_menu_detected`. If `WeddingTests` is opted in, run it repeatedly and confirm both ceremonies fire per its ceremony-only gate.

Before claiming a wall-clock win, check the joins sit on the serial critical path (`test-timing.md`) — most second-farmer joins overlap other tests and may already be absorbed by parallelism.
