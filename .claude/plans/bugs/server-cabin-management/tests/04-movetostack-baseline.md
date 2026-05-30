# Test 04 — `MoveToStack` baseline (no saved positions) — covered by Test 05 Variant A

This test is **consolidated into Test 05** as Theory Variant A (`savedPositionsCount == 0`). The earlier standalone framing produced a near-duplicate setup with Test 05; the only difference between the two was whether `/cabin` ran on a single farmer before the reload.

See [`05-movetostack-respects-saved.md`](05-movetostack-respects-saved.md) for the merged plan.

## If kept as a standalone test

Keep it standalone only if the Theory pattern is rejected during implementation. In that case, the standalone version is:

- **Verifies**: `SyncExistingCabins` MoveToStack branch still moves every visible (non-Lobby, non-Editing) cabin to `(-20, -20)` when `PlayerCabinPositions` is empty. Step 5's added `&& !HasSavedPosition(b)` does not over-filter.
- **Strategy**: CabinStack with `ExistingCabinBehavior=MoveToStack`. Same harness blockers as Test 03 (save+reload, save-flush).
- **Steps**: Seed via `CreateNewGameOnServerAsync(cabinStrategy: "None", startingCabins: 3)`. Two farmers join (or one — count doesn't matter for Variant A; Variant B is the one that needs two). No `/cabin` invocation. Save-flush. Switch strategy + ExistingCabinBehavior. Reload.
- **Assertions**: `after.Cabins.Count(c => c.Type == "CabinStack" && !c.IsHidden) == 0`; every cabin at `(-20, -20)`.
- **Why this test exists**: catches `HasSavedPosition` always returning `true` (filter over-matches; sweep dies). Also catches a future "remove the MoveToStack sweep as obviously dead" refactor.

## Why merge

The two variants share:
- Same server (CabinStack + MoveToStack) and same setup-pipeline (None seed → save-flush → strategy switch → reload).
- Same observation surface (`/cabins`).
- Same harness blockers.

The only difference is one extra `/cabin` invocation in Variant B. A `[Theory]` with `[InlineData(0)] [InlineData(1)]` halves the wall-clock and removes a near-duplicate setup. Per `simplest-solution.md`: "If the simplest fix feels too small, that's usually correct — small fixes are the goal." The simplest plan-file is also the goal.
