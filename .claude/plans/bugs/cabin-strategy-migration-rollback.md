# Fix: CabinStack→None migration partially completes when designated positions < hidden cabins

> Source: `audit-generic.md` M8.

## Bug

`CabinManagerService.MigrateCabins` (Stacked → None branch, `CabinManagerService.cs:245-267`) walks each hidden cabin and calls `FarmCabinPositions.GetNextAvailablePosition(farm)`. If the call returns `null` mid-loop (more hidden cabins than free designated positions on the farm map), already-relocated cabins move to visible tiles while remaining cabins stay at `HiddenCabinLocation` (`-20, -20`).

**Why it sticks across restarts.** `PersistentOptions` writes `Data.CabinStrategy = settings.CabinStrategy` and calls `Save()` in its constructor (`PersistentOptions.cs:58-64`) — *before* `OnSaveLoaded` fires. By the time `DetectAndMigrateStrategyChange` runs, the new strategy is already on disk. `PreviousCabinStrategy` is `[XmlIgnore]` and lives only in-memory (`PersistentOptions.cs:19-20, 28`), so a subsequent reload sees `previousStrategy == currentStrategy == None` and `MigrateCabins` does not retry. Cabins stranded in the hidden stack stay there permanently.

**Player impact.** With strategy now `None`, the warp interceptors (`OnLocationIntroductionMessage` / `OnLocationDeltaMessage`) are unregistered (`CabinManagerService.cs:79-94` skips the registration when `options.IsNone`). Owners of stranded cabins have no client-side rewrite that places their cabin at a visible tile, and there is no operator-facing "move this cabin out of the stack" command. The cabin and its owner's saved progress are effectively orphaned.

**Scope.** Only the `fromUsesHidden && !toUsesHidden` branch (lines 245-267) — i.e. **CabinStack → None** and **FarmhouseStack → None**. The opposite branch (lines 268-281, None → Stacked) moves visible cabins to a single shared `HiddenCabinLocation`; no shortage possible. The Stacked ↔ Stacked case (no relocation, only warp behavior changes) is also unaffected.

## Decision: pre-validate & abort with revert

When designated positions < hidden cabins:
1. Log a Warn-level message naming the deficit (per `debugging.md`, `LogLevel.Error` is test poison and this isn't a test-failure-worthy condition — operator config issue, not server crash).
2. Emit a structured diagnostic event with deficit numbers.
3. **Do not relocate any cabin.**
4. Revert `options.Data.CabinStrategy` to `previousStrategy` and persist via `options.Save()` so the on-disk state matches what actually happened. The operator must add positions (or accept fewer cabins) before the migration can be retried.

Rollback-after-partial-migration was considered (option C in design discussion). Rejected: pre-validation is structurally simpler and reaches the same end state without ever touching cabins. If a `cabin.Relocate` call throws *after* pre-validation passes, that's a deeper bug in `BuildingExtensions.Relocate` or `ClearTerrainBelow` — surface it loudly rather than wrap each Relocate in try/catch (per `retry-is-evidence-of-root-cause.md`).

---

## Step 1 — Expose available-position count

**File**: `mod/JunimoServer/Services/CabinManager/FarmCabinPositions.cs`

Add a public helper that mirrors `GetNextAvailablePosition`'s resolution (per `mirror-target-component-resolution.md` — the pre-validation MUST count using the same logic the loop consumes, or it under-/over-counts and the abort decision is wrong). Refactor `GetNextAvailablePosition` to consume the new helper so they cannot drift:

```csharp
/// <summary>
/// Returns all designated cabin positions that are not currently occupied by a building.
/// Mirrors the resolution used by GetNextAvailablePosition: if this returns N entries,
/// then exactly N successive calls to GetNextAvailablePosition (with no other building
/// changes in between) will succeed; the (N+1)th will return null.
/// </summary>
public static List<Vector2> GetAvailablePositions(Farm farm)
{
    return GetDesignatedPositions(farm)
        .Where(p => !IsPositionOccupied(farm, p))
        .ToList();
}

public static Vector2? GetNextAvailablePosition(Farm farm)
{
    var available = GetAvailablePositions(farm);
    return available.Count > 0 ? available[0] : null;
}
```

`IsPositionOccupied` stays private. Existing callers of `GetNextAvailablePosition` (the migration loop, `BuildNewCabinVisible`) are untouched.

## Step 2 — Pre-validate and revert in `MigrateCabins`

**File**: `mod/JunimoServer/Services/CabinManager/CabinManagerService.cs`

In the `fromUsesHidden && !toUsesHidden` branch (lines 245-267), insert pre-validation before the `foreach`:

```csharp
if (fromUsesHidden && !toUsesHidden)
{
    // Stacked → None: move hidden cabins to visible farm positions
    var hiddenCabins = farm.buildings
        .Where(b => b.isCabin && b.IsInHiddenStack())
        .ToList();

    var availablePositions = FarmCabinPositions.GetAvailablePositions(farm);

    if (hiddenCabins.Count > availablePositions.Count)
    {
        Monitor.Log(
            $"CabinStrategy migration {from} → {to} aborted: {hiddenCabins.Count} hidden cabin(s) " +
            $"but only {availablePositions.Count} designated position(s) available on this farm map. " +
            $"Reverting strategy to {from}. Add cabin positions to the Paths layer or remove " +
            $"surplus cabins before retrying.",
            LogLevel.Warn);

        Diagnostics.ModEventLog.Emit("cabin_strategy_migration_aborted", new
        {
            fromStrategy = from.ToString(),
            toStrategy = to.ToString(),
            hiddenCabinCount = hiddenCabins.Count,
            availablePositionCount = availablePositions.Count,
            deficit = hiddenCabins.Count - availablePositions.Count,
            reason = "insufficient_designated_positions"
        });

        // Revert the persisted strategy so the on-disk state matches what
        // actually happened. PersistentOptions.SyncFromSettings already wrote
        // the new strategy in the constructor; without this revert, the next
        // reload would see previous == current == new and never retry.
        options.Data.CabinStrategy = from;
        options.Save();
        return;
    }

    foreach (var cabin in hiddenCabins)
    {
        var nextPos = FarmCabinPositions.GetNextAvailablePosition(farm);
        if (nextPos.HasValue)
        {
            cabin.Relocate(nextPos.Value);
            Monitor.Log($"  Migrated cabin to ({nextPos.Value.X}, {nextPos.Value.Y})", LogLevel.Info);
            migrated++;
        }
        else
        {
            // Defensive: pre-validation above guarantees we never reach here.
            // Surface loudly if the invariant breaks (concurrent build between
            // pre-validation and the loop, e.g. lobby cabin construction —
            // shouldn't happen on the game thread but warns if a future change
            // makes it possible).
            Monitor.Log(
                $"  Pre-validation passed but GetNextAvailablePosition returned null mid-loop. " +
                $"This indicates a building was added concurrently. Skipping remaining cabins.",
                LogLevel.Warn);
            migrateFailed++;
        }
    }
}
```

Notes:
- `Monitor.Log(..., LogLevel.Error)` at the original line 263 is replaced with `LogLevel.Warn`. Per `debugging.md`, `LogLevel.Error` from mod code triggers test cancellation in `ServerContainer`; an operator-config-induced abort is not a test poison condition.
- The defensive `else` branch in the post-validation loop is kept for the "concurrent build between pre-validation and the loop" scenario per `holistic-or-explicit-todo.md` (an explicit Warn rather than silent partial migration). Using `LogLevel.Warn` not `Error` for the same reason.
- The `cabin_strategy_migration` event at lines 284-290 already covers the success path; the new `cabin_strategy_migration_aborted` event is distinct so log readers can filter on the abort reason without parsing message text.

---

## Files changed

| File | Change |
|------|--------|
| `mod/JunimoServer/Services/CabinManager/FarmCabinPositions.cs` | Add `GetAvailablePositions(Farm)` public helper. Refactor `GetNextAvailablePosition` to delegate. |
| `mod/JunimoServer/Services/CabinManager/CabinManagerService.cs` | Pre-validate hidden-cabin count vs available positions in `MigrateCabins` Stacked→None branch. Revert strategy on abort. Downgrade Error→Warn on per-cabin failure. |

## Files NOT changed

- `mod/JunimoServer/Services/PersistentOption/PersistentOptions.cs` — the constructor's `SyncFromSettings → Save()` ordering is intentional (settings file is source of truth). The fix lives in `MigrateCabins`, which has full information to decide.
- `mod/JunimoServer/Services/CabinManager/CabinManagerService.cs:268-281` (None → Stacked branch) — single shared destination, no shortage possible. The active `server-cabin-management` plan modifies this same branch (Step 5, `HasSavedPosition` filter); changes here are independent and won't conflict.

## Compatibility

- **Default settings** (Standard farm, CabinStack, 1 starting cabin): pre-validation is satisfied trivially. Even with many tests' default of 8 starting cabins, Standard farm has 16 designated positions per `decompiled` and `Map/Farm.tbin` Paths layer (tile indices 29/30). Verified per `preflight-check-vs-committed-config.md`: the new check never aborts on default committed configs.
- **Farm types with fewer designated positions**: Beach farm, Riverland, custom maps with limited Paths-layer entries can hit the limit when paired with a larger starting cabin count. The abort surfaces the issue loudly with a fix-it-yourself message instead of silently stranding cabins.
- **Save format**: no schema change. `cabin_strategy_migration_aborted` is a new diagnostic event name; UI consumers (test-ui timeline) ignore unknown event names.
- **Cross-plan**: `server-cabin-management` plan touches the None → Stacked branch (lines 268-281) only. No file-level conflict; both can land in either order.

---

## Tests

### What can be tested today

The bug is gated on three primitives that combine in a way no current test exercises:

1. **`MigrateCabins` runs only inside `OnSaveLoaded`**, after `Data.Read()` and the in-memory `previousStrategy` snapshot taken in `PersistentOptions` constructor.
2. **The migration trigger is `previousStrategy != currentStrategy`**, requiring (a) a save written with strategy A, then (b) a fresh load where strategy B is the active server setting.
3. **The shortage condition requires** more hidden cabins than designated positions, which means either many `StartingCabins` or a farm map with few Paths-layer entries — and a previously-completed CabinStack/FarmhouseStack initialization so `hiddenCabins.Count > 0`.

`CreateNewGameOnServerAsync` (`TestBase.cs:535`) destroys the save when called with a different strategy, so it cannot exercise the migration path. The harness needs **either**:
- (A) A non-destructive strategy-switch primitive: e.g. `POST /settings { "cabinStrategy": "None" }` that writes `server-settings.json` and triggers a save+reload; **or**
- (B) A container-level restart that preserves the save volume and accepts a settings override across the restart boundary.

This is **the same primitive** that `server-cabin-management` Test 08 (`tests/08-strategy-switch-respects-saved.md:46-49`) is gated on. The two tests should land together once the primitive exists.

### Test plan once primitive lands

`tests/JunimoServer.Tests/CabinPositionPersistenceTests.cs` →
`StrategyMigration_CabinStackToNone_AbortsAndReverts_WhenInsufficientPositions`

- **Verifies**: Step 2 — `MigrateCabins` aborts cleanly and reverts strategy when `hiddenCabins.Count > availablePositions.Count`.
- **Strategy**: starts in **CabinStack** with `StartingCabins` set above the farm map's designated position count. Standard farm has ~16 designated positions; pick a farm type with fewer (Beach has 0 per current implementation; verify before writing the test) OR set `StartingCabins` high enough to exceed Standard's positions if no scarce-position map exists.
- **Preconditions**:
  - Server boots with `CabinStrategy=CabinStack`, `StartingCabins=N` where N > available positions on the chosen farm map.
  - All N starting cabins are at `HiddenCabinLocation` (`-20, -20`) — verified via `/cabins`.
  - Save+reload primitive (gated on `server-cabin-management` Test 03 harness extension).
  - Strategy-switch primitive (gated on `server-cabin-management` Test 08).
- **Steps**:
  1. `await CreateNewGameOnServerAsync(farmType: <scarce-positions>, cabinStrategy: "CabinStack", startingCabins: N);`
  2. Verify all N cabins are at `(-20, -20)` via `/cabins`.
  3. Disconnect any clients.
  4. Switch `CabinStrategy` to `None` via the new primitive.
  5. Trigger save-reload so `OnSaveLoaded` runs `DetectAndMigrateStrategyChange → MigrateCabins`.
  6. Read `/cabins`.
- **Assertions**:
  - `after.Strategy == "CabinStack"` (revert happened — the most important assertion).
  - All N cabins still at `(-20, -20)` (no partial migration).
  - Diagnostic event `cabin_strategy_migration_aborted` present in `infrastructure.jsonl` with `hiddenCabinCount == N`, `availablePositionCount == M`, `deficit == N - M`.
  - No `LogLevel.Error` lines from `CabinManagerService` in the SMAPI log (would trip `ServerContainer` cancellation per `debugging.md`).
- **Why this test exists**: Without it, a future refactor that drops the pre-validation would silently re-introduce the orphaned-cabin bug, and the `cabin_strategy_migration` event's `migrateFailed` count would be the only signal — a count nobody alerts on.

### Coverage gap

Per `runtime-post-conditions-are-gates.md`: I cannot exercise this end-to-end today. The runtime post-condition (the abort fires, the strategy reverts, no cabins move) is observable only via the missing primitive. The fix can land on master with static review (build clean, manual code review, the existing happy-path migration tests still pass), but a real regression test must follow once the harness extension lands.

The PR description must explicitly state: "Regression test gated on save+reload + non-destructive strategy-switch harness primitive (also blocking `server-cabin-management` Test 08). Test plan: this file, '## Test plan once primitive lands' section. Manually verify the abort path before merge by setting `StartingCabins=20` with a farm type that has < 20 positions, switching to `None`, restarting; observe revert in `/settings`."

---

## Verification plan

Per `runtime-post-conditions-are-gates.md`, the runtime gates here are:

| # | Post-condition | How to verify |
|---|----------------|---------------|
| 1 | `dotnet build mod/JunimoServer/JunimoServer.csproj` clean. | `make build` (static). |
| 2 | Existing happy-path migration still works (CabinStack → None when positions ≥ cabins). | `make test FILTER=CabinStrategyTests` — these tests already cover the success path; no new failure expected. |
| 3 | Abort path fires when configured to under-size positions. | Manual: set `StartingCabins` higher than available designated positions, run `make dev` to start a CabinStack server, sleep through a day to persist the save, edit `server-settings.json` to `CabinStrategy: None`, restart container with the same save volume. Observe `[Warn]` log from `CabinManagerService` naming the deficit, `infrastructure.jsonl` containing `cabin_strategy_migration_aborted`, and `/settings` reporting `CabinStack` (not `None`). |
| 4 | All previously-hidden cabins still at `(-20, -20)` after abort. | Same manual test, `curl /cabins` after restart, assert each `Cabins[i].TileX == -20 && Cabins[i].TileY == -20`. |

Gates 3 and 4 are **runtime-only** and require either the manual procedure above or the test harness extension. Per `runtime-post-conditions-are-gates.md`: do not declare this plan complete based on gates 1+2 alone. Either run the manual procedure and link the recording / log excerpt in the PR, or block merge on the harness extension.
