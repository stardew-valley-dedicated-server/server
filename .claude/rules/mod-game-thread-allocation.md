---
paths:
  - "mod/**/*.cs"
---

# Minimize per-tick allocations on the game thread from the start — don't defer GC tuning

Mod code that runs on the game thread (`UpdateTicked` and other per-tick / per-scan hot paths) should be written allocation-minimal up front, not "made simple now, optimized later". Reuse buffers across invocations instead of allocating per call: clear-and-refill a collection rather than `new`-ing it each tick, and to prune a keyed map without allocating, double-buffer two dictionaries and swap them (this scan writes the "current" map, then `(prev, cur) = (cur, prev)` and `cur.Clear()` next scan — `Clear()` retains capacity, so steady-state scans allocate nothing).

**Why:** This project treats GC/perf as a from-the-start design constraint, not a later pass — the user's words: "GC and performance is important by design from the get go, we don't want to go optimize later on... the game already is unoptimized, so we need to be more thorough here." On `CropWatcher` (runs in `UpdateTicked`), the first fix pruned a stale-tile dictionary with `.Keys.Where(...).ToList().ForEach(...)`, which allocated a `List` plus LINQ iterators on every prune scan. The accepted fix double-buffered the map and swapped, eliminating per-scan allocation entirely — and was *less* code than the explicit prune.

**How to apply:** When writing or editing code reachable from a per-tick/per-scan game-thread path, avoid per-invocation allocation: no `new List`/`new Dictionary`/`.ToList()`/LINQ-chain garbage inside the loop body; prefer reused fields cleared each pass, swap-buffers, and value-tuple keys (which box nothing). This is the **stated-perf-constraint exception** to [`simplest-solution.md`](universal/simplest-solution.md) — on a declared hot path the allocation-minimal version IS the simplest that meets the constraint, so it is not the speculative over-engineering that rule warns against. The exception is scoped to the hot path: off-hot-path mod code (startup, one-shot handlers, command replies) still defaults to the simplest direct solution.
