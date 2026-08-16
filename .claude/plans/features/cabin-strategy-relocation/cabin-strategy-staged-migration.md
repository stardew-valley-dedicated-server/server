# Cabin-strategy: staged, admin-driven strategy migration

**Status:** implemented on this branch (uncommitted, pending review; E2E gates not yet run).
Open decisions applied as: OD1 → place-all (default), OD2 → deferred, OD3 → included
(validate-and-fall-back in `MigrateCabins`). Additional hardening beyond plan: commit runs
`SaveNow` when no farmhands are online, plus a load-time pre-commit heal for a crash between
commit and the next save (see `cabin-system.md` invariant 11).
**Origin:** Review discussion on `cabin-strategy-relocation-and-none-hardening.md` (Parts A–C,
implemented on this branch). Two findings drove this refinement:

1. **FarmhouseStack → CabinStack materializes the ghost cabin onto a possibly-developed spot.**
   The ghost is per-peer and non-destructive on master, but for players it has a full collision
   box and occupies the tile — functionally the same "building appears on my stuff" failure the
   stacked→None rejection exists to prevent (one spot instead of seven, undoable instead of
   permanent). The exposure window is FarmhouseStack-specific: the stack spot renders empty
   there, so players can develop it.
2. **Wholesale validate-or-reject migration can dead-end.** Auto-placement can only use the
   fixed designated Paths-layer spots; on a developed farm those exact tiles may be occupied by
   things the admin won't demolish. Manual placement is not bound to designated spots (any
   valid footprint works, same as `!cabin`), so a semi-manual flow can **always** complete.

**Design ruling (user-approved):** strategy changes that materialize a cabin on an existing
save go through a **staged, admin-driven migration**: auto-place what passes validation
(non-destructively), let the admin place the remainder interactively ("placed, N left"), and
flip the strategy only at an explicit commit. Abort is always clean because nothing is ever
destructive. Delete this file when the code lands (`plan-discipline.md`).

---

## Core principles

- **Commit-at-end.** The old strategy stays live through the whole staging window. The ugly
  intermediate ("strategy=None with cabins still hidden" — broken warps, no interceptor
  handling) never exists. A restart mid-migration is harmless: the record persists, the old
  strategy is still active, the admin resumes or aborts.
- **Never destructive.** All migration placement (auto and manual) is
  `CabinPlacementValidator`-gated with **no** `ClearTerrainBelow` and no
  `skipSafetyChecks`-style force. Consequence: `abort` = move placed cabins back to the stack —
  no rollback machinery.
- **The settings file stays the durable source of truth.** `commit` writes the new strategy
  into `server-settings.json` (new `ServerSettingsLoader` setter, precedent:
  `SetVerboseLogging`). Without this, the next boot/reload's `SyncFromSettings` would flip the
  strategy back and fight the commit.
- **Direction matrix.** Pure-hide switches stay available via the existing settings-file +
  `/reload` flow; materializing switches require the migration command:

  | Direction | Path |
  |---|---|
  | anything → FarmhouseStack | file switch (pure hide; today's behavior, incl. `HasSavedPosition` exemption) |
  | None → CabinStack | file switch (sweep vacates the default stack spot, so the ghost lands on a just-cleared tile) |
  | FarmhouseStack → CabinStack | **migrate command** (ghost spot must be validated or admin-chosen) |
  | stacked → None | **migrate command** (real placements; subsumes the current blanket rejection) |

---

## M1 — Migration record + sweep protection

- `CabinManagerData` gains a persisted `MigrationState` (nullable): `FromStrategy`,
  `ToStrategy`, `PlacedCabinIndoorNames` (cabins identified by interior `NameOrUniqueName` —
  unique and save-stable; owner uid won't do, spare cabins are ownerless). Save-scoped by
  design (`ReadSaveData`), cleared by `/newgame`.
- The bulk movers (`SyncExistingCabins` MoveToStack sweep, `MigrateCabins`) exempt
  migration-placed cabins via a new predicate alongside `HasSavedPosition` — a staged cabin
  must survive an interim reload.
- `DetectAndMigrateStrategyChange` refuses file-driven strategy changes while a migration is
  active: revert the synced value to `MigrationState.FromStrategy`, Warn "migration in
  progress — use `cabins migrate`". Otherwise an operator file edit mid-staging would flip the
  strategy underneath the record.
- "Remaining" is always computed **live** (hidden non-lobby cabin count), never stored: a join
  during staging creates a new hidden cabin, which correctly becomes one more required
  placement (`EnsureAtLeastXCabins` keeps running under the still-live old strategy).

## M2 — Command surface

Console (extend `CabinsConsoleCommand`, which already owns cabin ops):

- `cabins migrate start <strategy>` — validates preconditions (no active migration, target ≠
  current, target materializes — else point at the file-switch flow), writes the record, runs
  the auto-place pass, prints the plan: "placed 3/7 automatically at designated spots; 4
  remaining — stand where you want each and run `!migrate place` (footprint 5×3), or
  `cabins migrate place <x> <y>`".
- `cabins migrate status` — placed/remaining counts + tiles.
- `cabins migrate place <x> <y>` — coordinate variant for headless admins.
- `cabins migrate commit` — refuses while remaining > 0; otherwise: flip persisted strategy +
  settings file, run the one-shot warp reconciliation (below), clear the record, emit event.
- `cabins migrate abort` — move placed cabins back to the hidden stack, clear the record.

In-game (admin-gated via `RoleService`, registered in `ChatCommandsService`):

- `!migrate place` — places the next remaining cabin at admin position + (1,0), exactly the
  `!cabin` ergonomics (validator-gated, no clearing). Reply: "placed, 3 left." Subcommand
  parsing mirrors `CabinCommand`.

### Per-direction flow

- **stacked → None:** auto-place at designated spots (validated, skip occupied), manual for
  the rest anywhere valid. Commit sets `NoneCabinCount` = final visible non-lobby count (the
  frozen cap from the current branch), then sets each placed cabin's warps to its own door.
  Feedback must state the capacity consequence: an admin who wants headroom for new players
  places spare cabins too — under None, capacity IS the cabin count.
- **FarmhouseStack → CabinStack:** zero building moves; the single materialization is the
  ghost. `start` validates the current `StackLocation` resolution — if clear, report
  "ready, run commit"; if occupied, remaining = 1 ghost placement: `!migrate place` sets
  `CabinManagerData.DefaultCabinLocation` (validated) instead of moving a building. Commit
  flips; the existing interceptors handle warps per-message.
- **→ FarmhouseStack / None → CabinStack:** not routed through the command (file switch, see
  matrix). `start` explains this instead of staging.

## M3 — Interceptor lifecycle: per-message gating (fixes the pre-existing boot-strategy gap)

Strategy can now change in-process at commit, so constructor-time gating on the *boot*
strategy is wrong (it already was: a `/newgame CabinStack` on a None-booted server has no
interceptors — noted pre-existing in the deferrals file; this absorbs it):

- Register the `locationIntroduction`/`locationDelta` interceptors and the `UpdateTicked`
  farmhouse monitor **unconditionally** in the `CabinManagerService` constructor; gate at the
  top of each handler on `options.IsNone` (leave the message untouched / skip the tick).
- Register the `BuildStartingCabins` disable-patch unconditionally: consistent with
  `cabin-system.md` invariant 9 — vanilla None placement doesn't survive the headless path
  anyway and the mod places None cabins itself.

## M4 — Observability (tests assert via HTTP, `tests-assert-via-http-api.md`)

- Extend `CabinsResponse` with a nullable `Migration` object: `FromStrategy`, `ToStrategy`,
  `PlacedCount`, `RemainingCount`. E2E gates staging state on this, never on mod events.
- Mod events (diagnostics only): `cabin_migration_started` (from, to, autoPlaced, remaining),
  `cabin_migration_placed` (tileX, tileY, manual), `cabin_migration_committed`,
  `cabin_migration_aborted`. Update the `InfrastructureEventLog` catalog.

## M5 — Warp reconciliation at commit

- **→ None commit:** for each visible non-lobby cabin, `SetWarpsToFarmCabinDoor()` once
  (during staging the still-live FarmhouseStack interceptors point staged cabins at the
  farmhouse door — correct for the old strategy; the flip is what players perceive as the
  migration happening).
- **→ CabinStack commit:** no explicit pass needed — the per-message interceptors (CabinStack
  branch) reconcile each cabin as deltas/introductions flow; ghost handling is the existing
  `StackLocation` path.

---

## Open decisions

- **OD1 — Unclaimed spare cabins under stacked→None.** v1 recommendation: they must be placed
  like every other cabin (remaining counts them). Alternative: a `--destroy-spares` option on
  `start` that removes unclaimed spares (via `DestroyCabin`) so fewer placements are needed —
  at the cost of post-commit capacity. Decide at implementation; default to place-all.
- **OD2 — Standalone stack-position setter.** `!migrate place` sets `DefaultCabinLocation`
  only during a FarmhouseStack→CabinStack staging. A standalone admin command to (re)set it on
  a live CabinStack server (validated) is small and independently useful — include or defer.
- **OD3 — None → CabinStack ghost edge.** A stale `DefaultCabinLocation` override can point at
  a developed tile, weakening the "lands on the vacated spot" guarantee for the file-switch
  path. Cheap hardening: validate the override at migration/load and fall back to the map
  default with a Warn. Include or defer.

## Compatibility verification (to perform during implementation, per `plan-discipline.md`)

- **LAN vs Steam:** all placement/warp effects are server-authoritative building moves +
  `GameLocation.warps` mutations (verified vanilla-client primitives) — transport-independent.
  Admin identity for `!migrate` goes through `RoleService` like existing admin commands.
- **Lobby / unauthenticated players:** lobby/editing cabins (`IsLobbyOrEditing`) are excluded
  from remaining, placement, and commit reconciliation. Verify the lobby exit-warp removal is
  untouched by the commit warp pass.
- **`SERVER_TPS=5` / FPS:** all commands are one-shot on invocation; no per-tick machinery
  beyond the (cheap) `IsNone` gate added to existing handlers.
- **Disconnect / restart mid-staging:** record is persisted; old strategy live; verify a
  reload during staging neither sweeps staged cabins (M1 exemption) nor fires
  `DetectAndMigrateStrategyChange` against the file (M1 refusal).
- **`!cabin` during staging:** a player moving their own cabin out mid-staging gains
  `HasSavedPosition`; it is then already visible and drops out of remaining — verify the
  commit passes treat it identically to a migration-placed cabin.
- **`EnsureAtLeastXCabins` during staging:** still runs under the old strategy (hidden
  builds); verify the live remaining count reflects new spares and blocks commit until placed.

## Tests & docs

- New E2E class (Exclusive): stacked→None staged flow with a deliberately obstructed
  designated spot (place a chest first) → auto-place partial → one manual `place` → commit →
  assert via `/cabins` (strategy, counts, `Migration == null`, frozen cap) and that the
  obstructing chest survived; abort path restores hidden stack + old strategy; reload
  mid-staging keeps old strategy and staged cabins; commit survives `/reload` (settings file
  updated). FarmhouseStack→CabinStack: obstructed stack spot → ghost placement → commit.
  Confirm scenarios actually ran via run artifacts (`passing-test-isnt-proof-the-scenario-ran.md`).
- Existing branch tests to touch: `CabinStrategyNoneTests.StrategySwitch_StackedToNone_RejectedOnExistingSave`
  asserts the *file-switch* rejection — keep, and extend its expected Warn text to point at
  `cabins migrate`.
- Docs: `docs/features/cabin-strategies.md` (migration section replacing the "fresh games
  only" absolute with "fresh games, or the staged `cabins migrate` flow"),
  `docs/admins/operations/commands.md` (`cabins migrate` + `!migrate place`),
  `docs/admins/configuration/server-settings.md` (strategy-switch matrix).
- `.claude/rules/cabin-system.md`: update invariant 9's migration sentence and the
  interceptor-registration description after M3.

## Announced edit surface (snapshot, re-derive at implementation)

Mod: `CabinManagerService` (record, exemptions, gating, commit passes), `CabinManagerData`,
`CabinsConsoleCommand`, new `!migrate` chat command registration, `ServerSettingsLoader`
(strategy setter), `ApiService` (DTO + snapshot), `ModEntry` (wiring if a new class).
Tests: new E2E class, `ServerApiClient` DTO, `InfrastructureEventLog` catalog, one existing
None test's expected text. Docs: 3 pages + 1 rule.
