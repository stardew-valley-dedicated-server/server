# Session handoff — implement the staged cabin-strategy migration

**For the next session.** Read this first, then the plan it points at. Delete this file when
the staged-migration work lands (it is session scaffolding, not documentation).

> **STATUS UPDATE:** the staged migration (`cabin-strategy-staged-migration.md` M1–M5) is now
> IMPLEMENTED on this branch, uncommitted, builds + csharpier green. OD1 applied as place-all,
> OD2 deferred, OD3 included. **All E2E gates have been RUN and are green** (47/47 across
> CabinMigrationTests, CabinStrategyNoneTests, CabinStrategyFarmhouseStackTests,
> CabinRelocationTests, CabinPositionPersistenceTests, CabinStrategyTests,
> CabinConcurrencyTests, CabinPlacementValidationTests, ServerSettingsTests — run
> `2026-08-14T03-11-11Z`). The None exact-count gate did its job: the real designated-spot
> count was 14 (both layout marker sets merged), fixed by making `FarmCabinPositions` mirror
> vanilla's `Game1.cabinsSeparate` layout selection (restored from `CabinLayoutNearby` at
> load) — 7 per layout; the separate set on Standard is (23,31),(66,37),(17,10),(17,48),
> (44,12),(39,32),(7,24). The rest of this file describes the state BEFORE this session.

## Where you are

- Worktree: `repos/worktrees/feat-cabin-strategy-relocation`, branch
  `feat/cabin-strategy-relocation` (branched from `master`).
- **Everything on the branch is UNCOMMITTED by explicit user instruction** — the user reviews
  before any commit. Do not commit or push unless asked.
- The `decompiled/` tree exists only in the main checkout (`repos/server`), not here.

## What is already implemented (uncommitted, builds green)

`cabin-strategy-relocation-and-none-hardening.md` Parts A–C are fully implemented:

- **A** — `AllowCabinRelocation` (default `true`) plumbed through settings →
  `PersistentOptions` → `/settings` + `/newgame` override → `SettingsCommand`, replaces the
  FarmhouseStack gate in `CabinCommand`; both message interceptors in `CabinManagerService`
  exempt `HasSavedPosition` cabins from the farmhouse-door repoint (Cabin-typed overload added).
- **B** — None places `min(designated positions, MaxPlayers)` up front at creation
  (`GameCreatorService`), frozen as `PersistentOptionsSaveData.NoneCabinCount`;
  `EnsureAtLeastXCabins` clamps None growth via `GetNoneCabinCap` (save-import finalizer
  deliberately exempt — builds direct).
- **C** — `MigrateCabins` rejects any stacked→None switch on an existing save
  (`none_on_existing_save`, revert + Warn), gated on `hiddenCabins.Count > 0` so fresh None
  games don't false-trip.
- Tests reworked: `CabinStrategyNoneTests` (7-on-Standard exact count, `maxPlayers:3` cap,
  stacked→None rejection), new `CabinRelocationTests` (disabled-switch theory, all three
  strategies), `CabinStrategyFarmhouseStackTests` (two-farmhand move-out + reset),
  `CabinPositionPersistenceTests` (None creates pinned to `maxPlayers:4`), shared
  `ServerSettingsFileHelper`, `/settings` round-trip row. Docs + `cabin-system.md` invariant 9
  + `InfrastructureEventLog` catalog updated.

**Verification state:** `dotnet build` green for `mod/JunimoServer` and
`tests/JunimoServer.Tests`; `dotnet csharpier check` clean. **No E2E has been run** — gating
runs before merge: `make test FILTER=` each of `CabinStrategyNoneTests`,
`CabinStrategyFarmhouseStackTests`, `CabinRelocationTests`, `CabinPositionPersistenceTests`,
`ServerSettingsTests`.

## Your task

Implement **`.claude/plans/features/cabin-strategy-staged-migration.md`** on top of this
branch (user-approved refinement, designed during review of the work above). Spine:
commit-at-end staging, never-destructive placement, settings file updated at commit,
direction matrix (pure-hide switches keep the file+`/reload` flow; FarmhouseStack→CabinStack
and stacked→None require `cabins migrate`). Sections M1–M5; open decisions OD1–OD3 need a
ruling during implementation (plan states default recommendations — confirm with the user or
apply the stated default and flag it).

Companion docs:

- `.claude/plans/features/cabin-strategy-relocation-and-none-hardening.md` — the implemented
  plan (background + invariants; delete when its code merges).
- `.claude/plans/features/cabin-strategy-relocation-deferrals.md` — open decisions & unrun
  gates from the first pass. Items it marks as absorbed by the migration plan (the
  interceptor gap → M3; the FH→CS ghost question → direction matrix) get closed by your work;
  the rest (cabins-add cap bypass, legacy-None live fallback, config-hash line,
  mutual-enterability assertion) still await user rulings — don't silently resolve them.

## Facts verified this session (don't re-derive)

- **Standard farm designated cabin spots** (from prior run logs, order not fully known):
  (17,10), (23,31), (35,14), (42,14), (50,14), (66,37) + one unobserved 7th. "7 on Standard"
  comes from repo docs, not a fresh run — the None exact-count E2E is its gate. The two
  `CabinPlacementHelper` footprints (rows 18–21 and 30–33, x 41–46) don't overlap any
  observed spot; None+`!cabin` tests pin `maxPlayers:4` so the unknown 7th never places.
- **`StackLocation` resolution:** `CabinManagerData.DefaultCabinLocation` (persisted per-save
  override — **nothing writes it today**) → first designated position → fallback (50,14).
- **The ghost cabin is per-peer**, applied to the outgoing message copy in
  `OnLocationIntroductionMessage` — but for players it has full collision and occupies the
  spot; treat it as a real placement UX-wise (that finding motivated the migration plan).
- **None→CabinStack via file switch is ghost-safe**: the sweep vacates the first designated
  spot in the same migration, so the ghost lands on a just-cleared tile (unless a stale
  `DefaultCabinLocation` override exists — plan OD3).
- **Interceptors + `BuildStartingCabins` patch are registered in the `CabinManagerService`
  constructor only when the boot strategy ≠ None** — pre-existing gap, fixed by plan M3
  (register always, gate per-message on `options.IsNone`).
- **`SyncFromSettings` reapplies the settings file on every boot/`/reload`** — that's why
  `migrate commit` must write the new strategy into `server-settings.json`
  (`ServerSettingsLoader` needs a strategy setter; `SetVerboseLogging` is the precedent).
  `NoneCabinCount` is deliberately NOT synced (creation/commit-time state).
- **`/reload`+`/newgame` completion resolves after `SaveLoaded` and republishes the snapshot**
  — first post-reload `/cabins` read is safe without polling (`tests-assert-via-http-api.md`).
- Tooling: CSharpier enforced (lefthook staged-files hook; run
  `dotnet csharpier format mod/JunimoServer/Services tests/JunimoServer.Tests` before
  finishing), IDE analyzers are build errors (unused usings fail the build), conventional
  commits, no Co-Authored-By trailers.

## Gotchas for the migration work specifically

- E2E asserts via the HTTP snapshot only — the plan's `CabinsResponse.Migration` DTO (M4) is
  a prerequisite for the staging tests, not an optional nicety.
- Staged (placed-but-uncommitted) cabins must survive interim reloads: exempt them in BOTH
  bulk movers (`SyncExistingCabins` MoveToStack sweep and `MigrateCabins`), and make
  `DetectAndMigrateStrategyChange` refuse file-driven changes while a record is active.
- "Remaining" must be computed live (hidden non-lobby count) — a join during staging adds a
  cabin that legitimately needs placement.
- Migration placement uses `CabinPlacementValidator` with NO `ClearTerrainBelow` — that
  non-destructiveness is what makes `abort` trivially safe; don't reuse `Building.Relocate`
  blindly (it clears terrain).
- Admin gating for `!migrate place` goes through `RoleService` like existing admin chat
  commands; console commands live in `CabinsConsoleCommand`.
- `LogLevel.Error` in mod code is test poison (server-side scan) — Warn/Info only.
- Update `cabin-system.md` invariant 9's migration sentence + the event catalog again when
  M3/M4 land (both were already touched once on this branch — keep them consistent).
