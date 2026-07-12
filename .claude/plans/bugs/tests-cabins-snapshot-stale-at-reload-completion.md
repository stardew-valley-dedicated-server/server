# `/cabins` serves a pre-migration snapshot at `/reload` completion

## Incident

`CabinPositionPersistenceTests.MoveToStack_UnclaimedCabinSweptOnReload` fails deterministically
on local Windows (`Assert.Equal() Failure: Expected: 0, Actual: 2` — two unclaimed cabins still
visible after a None→CabinStack switch + `/reload`). Discovered 2026-07-12 while running the
regression suites for the lobby-homed-spouses fix (PR #474); **fails on a pure master build too**
(verified by stashing the branch diff and running against a master image, run
`2026-07-12T02-10-44Z`), so it is pre-existing, not a #474 regression.

The sweep itself works — post-SaveLoaded game state is correct. Only the API read is stale.

This is the **second seam** of the flake documented in
`.claude/rules/tests-assert-via-http-api.md` ("a post-reload snapshot is safe on the first read
... don't add a settle-poll — fixed at the contract layer"). The completion contract closed the
*migration-hasn't-run-yet* seam; it did not close the *published-snapshot-is-stale* seam.

## Root cause (verified)

1. Read-only endpoints (`/status`, `/players`, `/farmhands`, `/cabins`, `/auth`) serve a
   `volatile GameStateSnapshot` (`ApiService.cs:824-829`), republished at 1 Hz wall-clock on the
   game thread (`OnUnvalidatedUpdateTicked`, `:1218-1243`; skipped during `Game1.newDay`) and
   force-refreshed only after `RunOnGameThreadAsync` mutations (`OnUpdateTicked`, `:1145-1151`).
2. `/reload` / `/newgame` completion resolves on the first `UpdateTicked` after `SaveLoaded`
   (`GameManagerService.cs:171-192`, resolve pair at `:186-191`), by which point the SaveLoaded
   chain (cabin migration/sync/sweep, `EnsureAtLeastXCabins`) has run — the **game state** is
   final, but nothing republishes the **API snapshot** at that moment. GameManagerService's own
   comment ("and the snapshot is final", `:170`) is false for the published snapshot.
3. During the load, a periodic 1 Hz snapshot can capture the world after it is loaded but before
   `CabinManagerService.OnSaveLoaded`'s migration runs — a pre-migration cabin layout. The next
   periodic republish is up to 1s away; the test's first read lands milliseconds after completion
   and reads the stale snapshot. At `SERVER_TPS=5` (200ms ticks) the read virtually always beats
   the next 1 Hz refresh — hence deterministic locally.
4. `/diagnostics/state` and `/test/*` are live game-thread reads, not snapshot-served — which is
   why `LobbyHomedSpouseHealTests`' first-read-after-reload holds while `/cabins`' doesn't.

## Verified facts the design rests on

| Fact | Source |
|---|---|
| Snapshot fields: `volatile _snapshot`, monotonic `_snapshotVersion`, `_snapshotChanged` TCS rotated on publish | `ApiService.cs:824-843` |
| Periodic republish: 1 Hz wall-clock gate in `OnUnvalidatedUpdateTicked`, skipped while `Game1.newDay` | `ApiService.cs:1218-1243` |
| Post-mutation force-refresh precedent: `OnUpdateTicked` calls `TakeGameStateSnapshot()` after draining `_pendingGameActions`, without resetting the 1 Hz timer | `ApiService.cs:1145-1151` |
| Concurrent publishers are serialized by call site — every `TakeGameStateSnapshot` call is on the game thread | `PublishSnapshot` doc, `ApiService.cs:1246-1252` |
| Completion resolves next-tick after `SaveLoaded`, deliberately independent of SaveLoaded subscriber order; `/newgame` additionally gated on `ComputeDayTransitionComplete()` | `GameManagerService.cs:168-192` |
| GameManagerService already calls ApiService **statically**: `Api.ApiService.ComputeDayTransitionComplete()` (`public static`, `ApiService.cs:1627`) | `GameManagerService.cs:181` |
| Static `_instance` pattern for cross-service static hooks exists in-tree | `PasswordProtectionService.cs` (`_instance`) |
| The failing assertion counts visible unclaimed cabins from `ServerApi.GetCabins` immediately after `ReloadServerAsync()` | `CabinPositionPersistenceTests.cs:94-123` |
| `TakeGameStateSnapshot` early-returns an empty snapshot when `gameMode != 3`; at the completion tick the world is loaded so the full branch runs | `ApiService.cs:1289-1311` |

## Design

Make snapshot freshness part of the load-completion contract: republish the snapshot at the
moment the contract already guarantees the SaveLoaded chain has run.

In `GameManagerService.OnUpdateTicked`, immediately before the `TrySetResult` pair (`:186-191`,
after the `_saveLoadedSinceRequest = false;` reset at `:186`), call a new static hook:

- `ApiService`: add `private static ApiService? _instance;` (assigned in the constructor — DI
  singleton, single construction) and
  `public static void RefreshSnapshotAtLoadCompletion() => _instance?.TakeGameStateSnapshot();`
  placed next to `ComputeDayTransitionComplete` (`:1627`), whose static-access-from-
  GameManagerService pattern it mirrors. The null guard covers `API_ENABLED=false` (Entry
  early-returns but the ctor still runs; a refresh without subscriptions is harmless either way).
- `GameManagerService`: invoke it right before resolving, and fix the now-true comment at
  `:168-170` to say the snapshot is *made* final here (republished), not that it happens to be.

Why this shape:

- **Runs on the game thread** — same serialization guarantee as both existing
  `TakeGameStateSnapshot` call sites; no new concurrency.
- **No 1 Hz timer reset** — consistent with the post-mutation refresh precedent (`:1150`).
- **Safe w.r.t. the `newDay` skip** — for `/reload`, `Game1.newDay` is false at the completion
  tick; for `/newgame`, the `ComputeDayTransitionComplete()` gate (`:181`) has already required
  the deferred day-transition save to be durable before the resolve block is reachable.
- **Covers both `/reload` and `/newgame`** (shared resolve block) and fixes **all**
  snapshot-served endpoints at once, not just `/cabins`.
- **Order-independent** — inherits the resolve block's next-tick-after-SaveLoaded guarantee, so
  no dependence on SaveLoaded subscriber order (the exact trap the contract was built to avoid).

### Rejected alternatives

- **Test-side settle-poll / retry on `/cabins`**: a bandaid (`retry-is-evidence-of-root-cause`),
  explicitly banned by `tests-assert-via-http-api.md`, and every other post-reload first-read
  consumer would stay broken.
- **ApiService subscribes to `SaveLoaded` and refreshes there**: handler order = construction
  order, so the refresh could run *before* CabinManagerService's migration — reintroducing the
  subscriber-order dependence the next-tick design exists to avoid.
- **Snapshot every tick / raise cadence**: per-tick allocation and scan cost on the game thread
  (`mod-game-thread-allocation`) and still a race window, just smaller.
- **Suppress periodic snapshots during load** (extend the `newDay` skip): the pre-reload world's
  last snapshot would serve throughout the load (worse staleness), and a snapshot captured just
  before the migration would still race — it narrows the window without closing the seam.

## Edge cases

| Case | Handling |
|---|---|
| `/newgame` (day-transition-gated) | Refresh runs after the `ComputeDayTransitionComplete` gate passes → snapshot reflects the durable new-day world |
| `API_ENABLED=false` | Static hook no-ops via `_instance?.` null-safety (ctor sets it, but refresh without a listener is harmless) |
| Reload failure path | The resolve block is reached only via `_saveLoadedSinceRequest`; failure paths fault the completion elsewhere and never hit the refresh |
| `/wait/*` long-poll waiters | The extra publish bumps `Version` and wakes them once; they re-evaluate their predicate — by design (`:832-843`) |
| Mid-load periodic snapshots | Unchanged — they may still capture transitional state, but nothing awaiting the completion contract can observe it anymore |

## Implementation steps

1. `ApiService`: static `_instance` + `RefreshSnapshotAtLoadCompletion()` next to
   `ComputeDayTransitionComplete`.
2. `GameManagerService.OnUpdateTicked`: call it immediately before the `TrySetResult` pair;
   correct the `:168-170` comment.
3. `.claude/rules/tests-assert-via-http-api.md`: the reload-caveat paragraph (`:14`) was already
   corrected (2026-07-12) to describe the second seam as open rather than asserting the false
   "safe on first read" claim — once this fix lands, amend it again to state the seam is closed
   (completion now *includes a snapshot republish*) and drop the pointer to this plan file.
4. No new test: `MoveToStack_UnclaimedCabinSweptOnReload` **is** the regression gate (it fails
   deterministically today). Delete the memory
   `movetostack-sweep-reload-snapshot-race.md` once merged (content graduates into the rule).

## Post-conditions (runtime gates, not static checks)

- `make test FILTER=MoveToStack_UnclaimedCabinSweptOnReload` green **3× consecutively** on the
  machine where it currently fails deterministically (0/3 today).
- `make test FILTER=CabinPositionPersistenceTests` — full class green (sibling tests guard the
  placed-cabin exemption paths).
- `make test FILTER="AbandonedClaimTests|SaveImportTests"` — the other reload/newgame-contract
  consumers stay green.
- Server log of a fixed run shows no new ERROR lines and unchanged `snapshot_skipped_newday`
  behavior.

## To validate at implementation start (each is one read/grep)

- [ ] No existing static instance field on `ApiService` (grep `static ApiService`); DI registers
      it as a singleton (single construction, `ModEntry.cs:223`).
- [ ] Baseline repro: one `make test FILTER=MoveToStack_UnclaimedCabinSweptOnReload` run on
      current master still fails before the fix.
- [ ] The reload failure path (`GameManagerService.cs:241` onward) cannot reach the resolve
      block (refresh fires on success only).
- [ ] `Game1.newDay == false` at the `/reload` completion tick (read the resolve-block
      preconditions; the `/newgame` case is gated at `:181`).
- [ ] `TakeGameStateSnapshot`'s `IsOnline` branch takes the full-snapshot path at the completion
      tick (`gameMode == 3` post-SaveLoaded).
- [ ] Re-read `.claude/rules/tests-assert-via-http-api.md:14`'s current wording before editing it
      in step 3 — confirm it still describes the seam as open (it may have drifted since this
      plan was written).
