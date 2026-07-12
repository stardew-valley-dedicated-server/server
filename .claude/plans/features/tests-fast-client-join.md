# Fast E2E client-join setup path

## Context

In the E2E suite, the great majority of tests need "a client connected and in-world" only as **setup** — the join itself is not the scenario under test. Today every such test drives the full vanilla join UI flow: connect → FarmhandMenu (select slot) → CharacterCustomization (create farmhand) → world-ready. The user's framing: *"We test the normal join flow in a separate test; for other tests this is a mere setup requirement that doesn't have to be ceremony."*

Measured on a quiescent server (one real run, user-supplied timestamps): a single connect+join took **~11.8s** (~4.8s connect → farmhand list, ~7s select → world-ready). At `SERVER_TPS=5`/`CLIENT_TPS=5` (`.env.test`, CI) every client↔server step serializes through both 5-tick/s loops plus a 200ms client-tick `/wait` poll, so the cost is dominated by **tick-crossing latency**, not HTTP overhead or engine compute. Done ~100+ times, this is a meaningful chunk of suite wall-time.

**Intended outcome:** a fast setup-join that produces a genuinely usable connected client, reusing the *already-existing* fast path for customized slots, with the real vanilla join flow still covered by a dedicated test.

## Mechanism (validated)

- The slow part is the **CharacterCustomization phase for an uncustomized slot**: `SelectSlotAndCreateCharacterAsync` (`tests/JunimoServer.Tests/Helpers/ConnectionHelper.cs:1044-1279`) does select → race(`Wait.ForCharacter` vs `Wait.ForFarmhands`) → customize → 200ms `CharacterCreationSyncDelay` → confirm, with up to 5 bounce-retries.
- An **already-customized slot already takes a fast path** today: `ConnectionHelper.cs:804-811` — if `targetSlot.IsCustomized`, it does only `Farmhands.Select(index)` then straight to the world-ready gate. No character menu, no 200ms delay, no bounce-race.
- The **connect phase (~4.8s) is transport/network-bound and NOT skippable** — the client must do the Lidgren handshake + FarmhandMenu nav. All three post-join usability gates require a real connected peer: world-ready (`ConnectionHelper.cs:814`), client `/status` UID≠0 (`:936`), server `WaitForPlayerByIdAsync(uid)` in `getAllFarmers()` (`:962`). **None require the CharacterCustomization menu** — only a customized + connected farmhand.
- Slot selection: `PickSlot` (`ConnectionHelper.cs:1015-1034`). With `preferExistingFarmer=true` (the default, `:531`) it prefers `s.IsCustomized && s.Name == farmerName`.
- `IsCustomized` on a slot = client-side `Farmer.isCustomized.Value`, read in test-client `CoopController.GetFarmhandSlots()`.

**So the lever is:** pre-customize a farmhand slot **server-side before the client connects**, so the existing already-customized client fast-path consumes it. This saves only the CharacterCustomization portion (the ~7s tail, more under contention) — **not** the ~4.8s connect floor.

### The one critical field rule (LAN-safety)

The new endpoint must set **`isCustomized.Value = true` + a `Name`, and leave `userID` EMPTY.**

- Empty `userID` + `isCustomized` → `IsFarmhandSelectableByUserId` returns `enableFarmhandCreation` (`FarmhandSenderService.cs:499-501`) and the slot is treated as a claimed/returning farmer (always included, exempt from the single-unclaimed-slot limiter, `:294`, `:300-302`). Selectable by any LAN client.
- A **non-empty** `userID` would route the join through vanilla `authCheck` string-equality (`GameServer.cs:495-506`); a LAN client's `getUserID()` returns `""`, so the join would be **rejected** (per `abandoned-claim-is-steam-only.md`). This is the opposite of what `/test/stamp_claim` does (it sets userID, leaves isCustomized=false).

## Phase 0 — Measurement spike (do FIRST; gates the build)

Confirm the saveable portion's real size *and that it sits on the serial critical path* before building anything (`test-timing.md` trap 1: per-test savings can be fully absorbed by parallelism; `e2e-slow-test-cost-model` memory: wins must be on the Exclusive serial path).

1. Add two diagnostic stamps in `ConnectionHelper.cs` using the existing `EmitDiagnostic(name, new {... elapsedMs})` pattern (`:192`), keyed off a `Stopwatch` started at connect entry:
   - `connect_phase_completed` in `ConnectViaLanOnceAsync` (~`:503`) and the invite-code twin `ConnectToServerOnceAsync` (~`:382`): `{ attempt, transport, slotCount, elapsedMs }`.
   - `world_ready_completed` in `CompleteJoinAsync` (~after `:818`): `{ slotIndex, wasCustomizedFastPath = targetSlot.IsCustomized, elapsedMs }`.
   The existing `slot_picked`/`select_returned`/`character_menu_detected`/`character_confirmed`/`join_poll_state` events already carry `elapsedMs` for the middle phases.
2. Register both new event names in the `InfrastructureEventLog` catalog (`event-catalog-no-inline-enums.md`).
3. Run a connect-heavy class — `FarmerCreationTests` — via the `/run-tests` skill. Read `{runDir}/diagnostics/infrastructure.jsonl`, group by `requestId`, bucket per-join into CONNECT / SELECT+MENU / CUSTOMIZE / WORLD-READY-TAIL.
4. **Reconcile against the prior measurement.** The `e2e-slow-test-cost-model` memory (run 2026-06-28) already names *"customization-sync `/wait/farmhands` p90 20s = 822s"* as one of the two big suite sinks. Confirm whether that sink IS the CharacterCustomization tail this plan eliminates (same code path) or something adjacent. If it's the same, the win is far larger than the conservative "~7s tail" framing and the build is already half-justified; if adjacent, note what the 822s actually was. Don't re-measure from scratch — validate against the existing number.
5. **Decision gate:** proceed to build only if CUSTOMIZE is a material fraction of join wall-time on the serial critical path. If the spike shows the connect floor dominates and CUSTOMIZE is small, stop and report — the win isn't there.

The two stamps are useful diagnostics regardless, so they stay in even if the build doesn't proceed.

## Phase 1 — Server-side endpoint (after the gate)

**`mod/JunimoServer/Services/Api/ApiService.TestEndpoints.cs`** — add `case "/test/precustomize_farmhand"` to the POST dispatch switch (~after `:128`) and `HandlePostTestPrecustomizeFarmhandAsync`, modeled on `HandlePostTestStampClaimAsync`'s slot-finding loop (`:684-718`) but with the inverted field writes:
- Fail closed like `set_date` (`gameMode != 3 || !IsMasterGame` → error), matching `:386-389`.
- In a `RunOnGameThreadAsync` body: for each requested name, find an empty/uncustomized cabin-owner `Farmer`, set `owner.isCustomized.Value = true`, `owner.Name = name` (+ `displayName`), pin `owner.homeLocation.Value` to its cabin (as stamp_claim does at `:710`). **Do NOT set `userID`.** Mutate via `Farmer` NetField public API; no `NetWorldState.UpdateFromGame1()` needed (we write Farmer fields, not replicated world scalars — `test-state-setter-runs-engine-reconcile.md` applies only to world-state reconciles).
- Never log at `LogLevel.Error` (`debugging.md` — server-side poison).
- Return each created farmhand's `{ Uid, Name, HomeLocation }`.
- No `/test/force_save` afterward: server `/farmhands` reads `Game1.getAllFarmhands()` live (`ApiService.cs:1415-1424`) and the connecting client gets the snapshot via `WriteFull` during `sendAvailableFarmhands` (`FarmhandSenderService.cs`), not from disk.

**`ApiService.TestEndpoints.Models.cs`** — DTOs with the OpenAPI `[ApiEndpoint]`/`[ApiResponse]` attributes every sibling uses:
- `TestPrecustomizeFarmhandRequest { List<string> Names }`
- `TestPrecustomizedFarmhand { long Uid; string Name; string HomeLocation }`
- `TestPrecustomizeFarmhandResponse { bool Success; string? Error; List<TestPrecustomizedFarmhand> Farmhands }`

## Phase 2 — Harness integration

**`tests/JunimoServer.Tests/Clients/ServerApiClient.cs`** — add `PrecustomizeFarmhandsAsync(IReadOnlyList<string> names, CancellationToken ct)` posting to `/test/precustomize_farmhand`, mirroring the existing server `/test/*` client wrappers.

**`tests/JunimoServer.Tests/Infrastructure/Fixture/FarmerTestHelper.cs`** — add `ConnectFastAsync(namePrefix, ct)` alongside `ConnectNewAsync` (`:46-95`):
1. `GenerateName(namePrefix)` (existing `Interlocked` counter, `:33`/`:66`) → a **process-unique** name (eliminates the SharedClass same-name collision risk).
2. `PrecustomizeFarmhandsAsync([name])`.
3. `JoinWithRetryAsync(name, preferExistingFarmer: true)` → existing fast path matches by name.
4. `TrackFarmer(name, uid)` for cleanup (unchanged).

`ConnectNewAsync` is **kept** for the dedicated normal-join test and any test that asserts on the join UI, character creation, auth/lobby, abandoned-claim, or `IsCustomized` transitions.

## Phase 3 — Migrate setup-only call sites

Switch setup-only `ConnectNewAsync` call sites to `ConnectFastAsync`. Leave on the slow path: the dedicated normal-join test, plus any test whose *scenario* is the join, customization, auth/lobby, abandoned-claim, or a customization-state transition. Each migration is a one-line swap.

**Migration scope is deferred to after Phase 0** — once the spike quantifies the per-join saving and confirms it lands on the serial critical path, decide between "prove on one class, then bulk-migrate the rest in this effort" vs "land one class as a small PR, iterate in follow-ups." Regardless of scope, verify a representative class stays green (and the fast path actually fired — see Verification) before migrating more.

## Risks / edge cases

- **Bounce-race avoided entirely** — it lives only in the `!IsCustomized` branch (`:1101-1268`); the fast path has none of it.
- **Concurrent same-slot collision** — eliminated by always `GenerateName`-ing (never a fixed name); the endpoint finds a distinct empty slot per name.
- **Abandoned-claim heals don't touch it** — `TryClearAbandonedClaim` acts only on `userID != "" && !isCustomized`; our slot (`isCustomized=true, userID=""`) matches neither condition.
- **Cabin pool** — consuming a slot could empty the unclaimed pool, but `sendAvailableFarmhands` self-heals on the next connect (`FarmhandSenderService.cs:259`). Verify with N≥2 in the spike that a following join still finds a slot; only wire an explicit `EnsureAtLeastXCabins` if the spike shows starvation.
- **Cleanup** — a pre-customized slot is an ordinary customized farmhand to the server; the uid-based `DELETE /farmhands` + per-class `/newgame` reset handle it identically to a real farmer.

## Runtime checks the spike must confirm (assumptions that can still be wrong)

1. A host write of `isCustomized=true` on an **unconnected** slot sticks and replicates to the connecting client (same write shape as stamp_claim's userID write, known to work; client-authoritative caveats apply only to a *connected* farmhand resending its root). Check: `slot_picked.isCustomized == true` on the first fast join.
2. `enableFarmhandCreation` is true on the test config (must be, since normal uncustomized joins work — same flag).
3. Pre-customizing N=2 doesn't starve the next join's slot pool.

## Verification

- **Spike (Phase 0):** read `infrastructure.jsonl`, confirm the phase budget and critical-path placement before building.
- **Endpoint (Phase 1-2):** run a migrated class (e.g. one `FarmerCreationTests` method via `ConnectFastAsync`) through `/run-tests`; confirm green AND read the run's `container.log` / infra events to confirm the fast path actually fired (`wasCustomizedFastPath=true`, no `character_menu_detected`) — a green test alone doesn't prove the scenario ran (`passing-test-isnt-proof-the-scenario-ran.md`).
- **Net effect:** compare per-join `world_ready_completed.elapsedMs` and the class's serial wall-time before/after, confirming the saving lands on the critical path (`test-timing.md`).
- **Regression guard:** the dedicated normal-join test (still on `ConnectNewAsync`) stays green, proving the real flow is unbroken.
